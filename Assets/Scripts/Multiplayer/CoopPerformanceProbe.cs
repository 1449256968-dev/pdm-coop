using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Mirror;
using UnityEngine;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

// Opt-in bounded timing capture; inactive in normal games.
//
// The probe used to start counting from the first frame where gameInProgress became
// true, so a four-player run was really measuring "whoever joined first". It now waits
// for the whole roster (see -coopPerfPlayers) and then measures inside a wall-clock
// aligned window that every process on this machine computes identically, so the four
// per-process reports describe the same interval.
public sealed class CoopPerformanceProbe : MonoBehaviour
{
    private static string[] Arguments => Environment.GetCommandLineArgs();
    private static bool HasFlag(string name) => Array.IndexOf(Arguments, name) >= 0;
    private static int IntegerArgument(string name, int fallback)
    {
        string[] args = Arguments;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == name && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0)
                return value;
        return fallback;
    }

    public static readonly bool Enabled = HasFlag("-coopPerf");
    // Measurement-window only diagnostic: stops the host from publishing world snapshots so a
    // run can quantify how much of each frame the state replication actually costs.
    public static readonly bool SkipStateReplication = HasFlag("-coopPerfSkipState");
    public static bool Recording => recording;
    private static readonly bool RawCapture = HasFlag("-coopProfilerRaw");
    private static readonly bool NativeCounters = RawCapture || HasFlag("-coopProfilerCounters");
    // 0 keeps the legacy behaviour: measure from gameInProgress, no roster barrier.
    private static readonly int ExpectedPlayers = IntegerArgument("-coopPerfPlayers", 0);
    private static readonly float WarmupSeconds = IntegerArgument("-coopPerfWarmup", 10);
    private static readonly float DurationSeconds = IntegerArgument("-coopPerfDuration", 30);
    private static readonly float AlignmentSeconds = IntegerArgument("-coopPerfAlign", 10);
    private static readonly float ReadyTimeoutSeconds = IntegerArgument("-coopPerfReadyTimeout", 180);
    private sealed class Samples { public readonly List<double> times = new List<double>(); public long bytes; }
    private static readonly Dictionary<string, Samples> stages = new Dictionary<string, Samples>();
    private static readonly Dictionary<string, ProfilerMarker> markers = new Dictionary<string, ProfilerMarker>();
    private static readonly Stack<ProfilerMarker> activeMarkers = new Stack<ProfilerMarker>();
    private readonly List<double> frames = new List<double>();
    private int collections;
    private bool done;
    private static bool recording;
    private readonly Dictionary<string, ProfilerRecorder> recorders = new Dictionary<string, ProfilerRecorder>();
    private readonly Dictionary<string, List<double>> profilerSamples = new Dictionary<string, List<double>>();
    private bool profilerStarted;
    private bool rawStarted, rawStopped;
    private bool wasProfilerEnabled;
    private bool ready;
    private double readyEpoch = -1, windowStart, windowEnd;
    private double firstFrameEpoch = -1;
    private string readyDetail = "not evaluated";

    // Every process on this machine shares the wall clock, so an epoch derived from it
    // is comparable across the four players. Time.realtimeSinceStartup alone is not.
    private static readonly double EpochBase =
        (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - Time.realtimeSinceStartup;
    private static double EpochNow => EpochBase + Time.realtimeSinceStartup;

    public static long Begin(string name)
    {
        if (!recording) return 0;
        if (!markers.TryGetValue(name, out ProfilerMarker marker)) markers[name] = marker = new ProfilerMarker(name);
        marker.Begin(); activeMarkers.Push(marker);
        return Stopwatch.GetTimestamp();
    }
    public static void End(string name, long began, long bytes = 0)
    {
        if (!recording || began == 0) return;
        if (activeMarkers.Count != 0) activeMarkers.Pop().End();
        if (!stages.TryGetValue(name, out Samples samples)) stages[name] = samples = new Samples();
        samples.times.Add((Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency);
        samples.bytes += bytes;
    }
    // Worker results are submitted on the main thread; the sample dictionaries are
    // never shared with an encoder/decoder thread.
    public static void AddStage(string name, double milliseconds, long bytes = 0)
    {
        if (!recording) return;
        if (!stages.TryGetValue(name, out Samples samples)) stages[name] = samples = new Samples();
        samples.times.Add(milliseconds); samples.bytes += bytes;
    }

    // ------------------------------------------------------------------
    // Movement pipeline accounting. These are cheap counters plus bounded
    // lists of doubles, all fed from the main thread by CoopMotion and the
    // input flow so a post-run report can separate "waiting for the host"
    // from "interpolation ran dry" from "the main thread stalled".
    // ------------------------------------------------------------------
    public static long MotionChunks, MotionPoses, MotionSequenceGaps, MotionTeleports, MotionHeldFrames, MotionRenderedTracks;
    public static long InputSends, InputSamples, InputStaleReads;
    private static readonly List<double> transportAge = new List<double>();
    private static readonly List<double> interpDelay = new List<double>();
    private static readonly List<double> inputIntervals = new List<double>();
    private static readonly List<double> inputAges = new List<double>();
    private static readonly List<double> ownerSteps = new List<double>();
    private static readonly List<double> publishIntervals = new List<double>();
    public static void MotionPublishInterval(double seconds) { if (recording) publishIntervals.Add(seconds); }
    public static void MotionChunkReceived() { if (recording) MotionChunks++; }
    public static void MotionPoseApplied() { if (recording) MotionPoses++; }
    public static void MotionGap() { if (recording) MotionSequenceGaps++; }
    public static void MotionTeleport() { if (recording) MotionTeleports++; }
    public static void MotionHeld() { if (recording) MotionHeldFrames++; }
    public static void MotionRendered() { if (recording) MotionRenderedTracks++; }
    // How old the newest received sample was when the frame was rendered.
    public static void MotionTransportAge(double seconds) { if (recording) transportAge.Add(seconds); }
    // How far behind the render clock sat inside the sample pair (interpolation depth).
    public static void MotionInterpolationDelay(double seconds) { if (recording) interpDelay.Add(seconds); }
    // Host: spacing between two accepted movement samples from one client.
    public static void InputInterval(double seconds) { if (recording) inputIntervals.Add(seconds); }
    // Host: age of the sample that the simulation actually consumed.
    public static void InputAge(double seconds) { if (recording) inputAges.Add(seconds); }
    public static void InputStale() { if (recording) InputStaleReads++; }
    public static void InputSend() { if (recording) InputSends++; }
    public static void InputSample() { if (recording) InputSamples++; }
    // Distance the locally owned character travelled in one rendered frame.
    public static void OwnerStep(double distance) { if (recording) ownerSteps.Add(distance); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Enabled) new GameObject("Coop Performance Capture").AddComponent<CoopPerformanceProbe>();
    }
    private void Awake()
    {
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 0;
        UnityEngine.Debug.Log("[CoopPerf] captureMode=" + (RawCapture ? "raw-profiler" : NativeCounters ? "native-counters" : "timing") + "; compiledDevelopment=" + CompiledDevelopment + "; incrementalGC=" + UnityEngine.Scripting.GarbageCollector.isIncremental);
    }
    // The roster is only complete once every player is authenticated (host view) or
    // present in the replicated name roster and the local replica world is applied.
    private bool CheckReady(out string detail)
    {
        detail = "no manager";
        var manager = NetworkManager.singleton;
        if (manager == null) return false;
        var world = manager.GetComponent<CoopWorld>();
        var session = manager.GetComponent<CoopSession>();
        var game = GameSystem.instance;
        if (game == null) { detail = "no game"; return false; }
        if (!game.gameInProgress) { detail = "game not in progress"; return false; }
        if (world == null || !world.SharedWorldRunning) { detail = "shared world not running"; return false; }
        if (session == null) { detail = "no session"; return false; }
        if (ExpectedPlayers <= 0) { detail = "legacy"; return true; }
        int present = NetworkServer.active ? session.PlayerCount : session.Names.Count;
        if (present < ExpectedPlayers) { detail = "roster " + present + "/" + ExpectedPlayers; return false; }
        if (!NetworkServer.active)
        {
            var replication = manager.GetComponent<CoopReplication>();
            if (replication == null) { detail = "no replication"; return false; }
            if (replication.LocalPlayerId == 0) { detail = "no local player id"; return false; }
            if (replication.AppliedRevision == 0) { detail = "no world snapshot applied"; return false; }
        }
        detail = "roster " + present + "/" + ExpectedPlayers;
        return true;
    }
    private int RosterCount()
    {
        var manager = NetworkManager.singleton;
        var session = manager == null ? null : manager.GetComponent<CoopSession>();
        if (session == null) return 0;
        return NetworkServer.active ? session.PlayerCount : session.Names.Count;
    }
    private static int ReplicaCount()
    {
        var manager = NetworkManager.singleton;
        var replication = manager == null ? null : manager.GetComponent<CoopReplication>();
        return replication == null ? 0 : replication.ReplicaCount;
    }
    private static int EntityCount()
    {
        var manager = NetworkManager.singleton;
        var replication = manager == null ? null : manager.GetComponent<CoopReplication>();
        return replication == null ? 0 : replication.EntityCount;
    }
    private void Update()
    {
        if (done) return;
        var game = GameSystem.instance;
        if (game == null || !game.gameInProgress) return;
        double now = EpochNow;
        if (firstFrameEpoch < 0) firstFrameEpoch = now;
        if (!ready)
        {
            if (now - firstFrameEpoch > ReadyTimeoutSeconds)
            {
                done = true;
                UnityEngine.Debug.LogError("[CoopPerf] ABORT: not ready after " + ReadyTimeoutSeconds + "s; " + readyDetail +
                    "; roster=" + RosterCount() + "/" + ExpectedPlayers);
                enabled = false;
                return;
            }
            string detail;
            if (!CheckReady(out detail)) { readyDetail = detail; return; }
            ready = true; readyDetail = detail; readyEpoch = now;
            // Align to a shared boundary that is still a few seconds away, so processes
            // that become ready at slightly different moments land on the same window.
            windowStart = Math.Ceiling((now + 4.0) / AlignmentSeconds) * AlignmentSeconds + WarmupSeconds;
            windowEnd = windowStart + DurationSeconds;
            UnityEngine.Debug.Log("[CoopPerf] ready; detail=" + detail + "; epoch=" + now.ToString("F3", CultureInfo.InvariantCulture) +
                "; windowStart=" + windowStart.ToString("F3", CultureInfo.InvariantCulture) +
                "; windowEnd=" + windowEnd.ToString("F3", CultureInfo.InvariantCulture) +
                "; warmup=" + WarmupSeconds + "; duration=" + DurationSeconds);
        }
        if (now < windowStart) return;
        if (recording && now > windowEnd) { Finish(); return; }
        if (!recording)
        {
            recording = true;
            collections = GC.CollectionCount(0);
            stages.Clear();
            UnityEngine.Debug.Log("[CoopPerf] measurement window open; role=" + Role() + "; roster=" + RosterCount() + "/" + ExpectedPlayers);
        }
        if (!profilerStarted && NativeCounters)
        {
            profilerStarted = true;
            UnityEngine.Debug.Log("[CoopProfiler] development=" + UnityEngine.Debug.isDebugBuild + "; compiledDevelopment=" + CompiledDevelopment + "; rawRequested=" + RawCapture);
            wasProfilerEnabled = UnityEngine.Profiling.Profiler.enabled;
            UnityEngine.Profiling.Profiler.SetCategoryEnabled(ProfilerCategory.Scripts, true);
            if (RawCapture) UnityEngine.Profiling.Profiler.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, true);
            if (RawCapture && CompiledDevelopment)
            {
                string[] args = Arguments; int log = Array.IndexOf(args, "-logFile");
                if (log >= 0 && log + 1 < args.Length)
                {
                    UnityEngine.Profiling.Profiler.enabled = false;
                    UnityEngine.Profiling.Profiler.logFile = args[log + 1] + ".raw";
                    UnityEngine.Profiling.Profiler.enableBinaryLog = true;
                    UnityEngine.Profiling.Profiler.enabled = true;
                    rawStarted = true;
                }
            }
            // Register markers after enabling profiling. The first scan before enabling
            // it does not include the opt-in Coop samples in all player configurations.
            foreach (string name in new[] { "Coop.Capture", "Coop.CaptureEntities", "Coop.CaptureGlobals", "Coop.CaptureRooms", "Coop.EncodeCompress", "Coop.Decode", "Coop.Apply", "Coop.ApplyFields", "Coop.Presentation" })
            {
                var marker = new ProfilerMarker(name);
                using (marker.Auto()) { }
            }
            var handles = new List<ProfilerRecorderHandle>(); ProfilerRecorderHandle.GetAvailable(handles);
            foreach (var handle in handles)
            {
                var description = ProfilerRecorderHandle.GetDescription(handle);
                string name = description.Name;
                if (!(name.StartsWith("Coop.") || name == "Main Thread" || name == "Render Thread" || name == "GC.Alloc" || name == "GC.Collect" || name == "GC Allocated In Frame" || name == "Draw Calls Count")) continue;
                var recorder = ProfilerRecorder.StartNew(description.Category, name, 1);
                if (!recorder.Valid) { recorder.Dispose(); continue; }
                recorders[name] = recorder; profilerSamples[name] = new List<double>();
                UnityEngine.Debug.Log("[CoopProfiler] metric=" + name + "; unit=" + description.UnitType);
            }
        }
        foreach (var pair in recorders) profilerSamples[pair.Key].Add(pair.Value.LastValue);
        if (now - windowStart >= 2 && rawStarted && !rawStopped)
        {
            UnityEngine.Profiling.Profiler.enableBinaryLog = false;
            rawStopped = true;
        }
        frames.Add(Time.unscaledDeltaTime * 1000.0);
    }
    private static string Role()
    {
        if (NetworkServer.active) return "host";
        var manager = NetworkManager.singleton;
        var replication = manager == null ? null : manager.GetComponent<CoopReplication>();
        return "client" + (replication == null ? "?" : replication.LocalPlayerId.ToString(CultureInfo.InvariantCulture));
    }
    private void Finish()
    {
        done = true;
        recording = false;
        double totalSeconds = frames.Sum() / 1000.0;
        UnityEngine.Debug.Log("[CoopPerf] context role=" + Role() +
            "; expectedPlayers=" + ExpectedPlayers +
            "; roster=" + RosterCount() +
            "; username=" + CoopUsername.Local +
            "; entities=" + EntityCount() +
            "; replicas=" + ReplicaCount() +
            "; screen=" + Screen.width + "x" + Screen.height +
            "; windowed=" + (!Screen.fullScreen) +
            "; targetFps=" + Application.targetFrameRate +
            "; vSync=" + QualitySettings.vSyncCount +
            "; development=" + UnityEngine.Debug.isDebugBuild +
            "; window=" + windowStart.ToString("F3", CultureInfo.InvariantCulture) + ".." + windowEnd.ToString("F3", CultureInfo.InvariantCulture) +
            "; readyEpoch=" + readyEpoch.ToString("F3", CultureInfo.InvariantCulture) +
            // Movement-feel context: the render clock sits this far behind the host, on top of
            // the pose sample spacing, and that delay is what client prediction would remove.
            "; rttMs=" + (NetworkTime.rtt * 1000.0).ToString("F2", CultureInfo.InvariantCulture) +
            "; bufferTimeMs=" + (NetworkClient.bufferTime * 1000.0).ToString("F2", CultureInfo.InvariantCulture) +
            "; bufferMultiplier=" + NetworkClient.bufferTimeMultiplier.ToString("F2", CultureInfo.InvariantCulture) +
            "; detail=" + readyDetail);
        UnityEngine.Debug.Log("[CoopPerf] frames " + Summary(frames) +
            "; n=" + frames.Count +
            "; totalSec=" + totalSeconds.ToString("F3", CultureInfo.InvariantCulture) +
            "; fpsTotal=" + (totalSeconds > 0 ? (frames.Count / totalSeconds).ToString("F2", CultureInfo.InvariantCulture) : "n/a") +
            "; fpsMean=" + (frames.Count > 0 ? (1000 / frames.Average()).ToString("F2", CultureInfo.InvariantCulture) : "n/a") +
            "; over16_67ms=" + frames.Count(x => x > 1000.0 / 60) +
            "; over33ms=" + frames.Count(x => x > 1000.0 / 30) +
            "; over50ms=" + frames.Count(x => x > 50) +
            "; gc0=" + (GC.CollectionCount(0) - collections));
        foreach (var pair in stages) UnityEngine.Debug.Log("[CoopPerf] " + pair.Key + " " + Summary(pair.Value.times) + "; bytes=" + pair.Value.bytes);
        UnityEngine.Debug.Log("[CoopPerf] motion chunks=" + MotionChunks + "; poses=" + MotionPoses + "; gaps=" + MotionSequenceGaps +
            "; teleports=" + MotionTeleports + "; heldFrames=" + MotionHeldFrames + "; renderedTracks=" + MotionRenderedTracks);
        UnityEngine.Debug.Log("[CoopPerf] transportAge " + Summary(transportAge));
        UnityEngine.Debug.Log("[CoopPerf] interpDelay " + Summary(interpDelay));
        UnityEngine.Debug.Log("[CoopPerf] ownerStep " + Summary(ownerSteps));
        UnityEngine.Debug.Log("[CoopPerf] publishInterval " + Summary(publishIntervals));
        UnityEngine.Debug.Log("[CoopPerf] input sends=" + InputSends + "; samples=" + InputSamples + "; staleReads=" + InputStaleReads);
        UnityEngine.Debug.Log("[CoopPerf] inputInterval " + Summary(inputIntervals));
        UnityEngine.Debug.Log("[CoopPerf] inputAge " + Summary(inputAges));
        foreach (var pair in profilerSamples) UnityEngine.Debug.Log("[CoopProfiler] " + pair.Key + " " + Summary(pair.Value).Replace("Ms", "Raw"));
        DisposeRecorders();
        stages.Clear();
        enabled = false;
    }
    private void OnDestroy() { DisposeRecorders(); }
    private void DisposeRecorders()
    {
        foreach (var recorder in recorders.Values) recorder.Dispose(); recorders.Clear();
        if (rawStarted)
        {
            UnityEngine.Profiling.Profiler.enableBinaryLog = false;
            UnityEngine.Profiling.Profiler.enabled = wasProfilerEnabled;
            rawStarted = false;
        }
    }
    private static bool CompiledDevelopment
    {
        get
        {
#if DEVELOPMENT_BUILD
            return true;
#else
            return false;
#endif
        }
    }
    private static string Summary(List<double> data)
    {
        if (data.Count == 0) return "n=0; unavailable";
        data.Sort();
        return "n=" + data.Count + "; meanMs=" + data.Average().ToString("F2") + "; p95Ms=" + data[(int)((data.Count - 1) * .95)].ToString("F2") + "; p99Ms=" + data[(int)((data.Count - 1) * .99)].ToString("F2") + "; maxMs=" + data.Last().ToString("F2");
    }
}
