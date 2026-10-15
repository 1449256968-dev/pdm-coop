using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

public struct CoopMotionPose
{
    public long id;
    public Vector3 position;
    public Quaternion rotation;
}
public struct CoopMotionFrame : NetworkMessage
{
    public uint world, sequence;
    public double time;
    public CoopMotionPose[] poses;
}

// Gameplay fields stay authoritative in full snapshots. Character poses use a
// small independent unreliable stream and Mirror's existing render timeline.
[DefaultExecutionOrder(10000)]
public sealed class CoopMotion : MonoBehaviour
{
    // The pose stream used to run at 30Hz, so the render clock (Mirror's buffer is two
    // 60Hz send intervals) sat a full sample interval behind the newest pose and any
    // host frame hitch drained the buffer into a frozen frame. One message still fits
    // the transport batch whenever the whole world needs a single chunk, so the stream
    // runs at frame rate in that case and stays at 30Hz when it would need several.
    private const double SlowSendInterval = 1.0 / 30;
    private const double FastSendInterval = 1.0 / 60;
    private const float TeleportDistance = 4;
    // One unreliable CoopMotionFrame must fit inside the transport's batch
    // threshold (KCP: 1184 bytes). A pose costs 36 bytes plus the frame header,
    // so a whole-world frame silently exceeded the limit and was dropped.
    private const int MaxPosesPerFrame = 24;
    private static readonly bool Diagnostics = Array.IndexOf(Environment.GetCommandLineArgs(), "-coopViewValidate") >= 0;
    private sealed class Track
    {
        public Component component;
        public double newest = double.NegativeInfinity;
        public Vector3 newestPosition;
        public readonly SortedList<double, TransformSnapshot> samples = new SortedList<double, TransformSnapshot>(32);
    }
    private readonly Dictionary<long, Track> tracks = new Dictionary<long, Track>();
    private readonly List<CoopMotionPose> outgoing = new List<CoopMotionPose>();
    private readonly List<long> removed = new List<long>();
    private uint world, sequence, receivedSequence;
    private double nextSend;
    public long ReceivedFrames { get; private set; }
    public long OwnerMovingInterpolations { get; private set; }
    private float nextDiagnostic;
    // Host-only adaptive pose rate: a host that is clearly missing its frame budget must not
    // keep spending main-thread time on a 60Hz pose stream. Clients interpolate whatever
    // arrives, so dropping to 30Hz only coarsens the sampling while the host recovers.
    private float frameTimeEma = 1f / 60f;
    private bool HasSendHeadroom()
    {
        frameTimeEma += (Time.unscaledDeltaTime - frameTimeEma) * 0.05f;
        return frameTimeEma <= 0.020f;
    }
    public long MeasuredFrames, HeldMovingFrames, SnapshotCorrections;
    public double SnapshotCorrectionDistance, CameraYawError;
    private static readonly bool MotionDiagnostics = Array.IndexOf(Environment.GetCommandLineArgs(), "-coopMotionHandsValidate") >= 0;
    public void StartClient()
    {
        Reset();
        // Unreliable poses may arrive ahead of the reliable authentication reply,
        // just like Mirror's TimeSnapshotMessage. Drop them until verification.
        NetworkClient.RegisterHandler<CoopMotionFrame>(Receive, false);
    }
    public void StopClient() { NetworkClient.UnregisterHandler<CoopMotionFrame>(); Reset(); }
    public void Reset() { tracks.Clear(); world = sequence = receivedSequence = 0; nextSend = 0; }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
    private static bool Finite(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w);
    private void Receive(CoopMotionFrame frame)
    {
        var shared = GetComponent<CoopWorld>();
        if (NetworkServer.active || NetworkClient.connection == null || !NetworkClient.connection.isAuthenticated ||
            !shared.SharedWorldRunning || frame.world != shared.Generation ||
            frame.poses == null || frame.poses.Length > 4096 || double.IsNaN(frame.time) || double.IsInfinity(frame.time)) return;
        if (world != frame.world) { Reset(); world = frame.world; }
        if (receivedSequence != 0 && unchecked((int)(frame.sequence - receivedSequence)) <= 0) return;
        if (receivedSequence != 0 && frame.sequence != receivedSequence + 1) CoopPerformanceProbe.MotionGap();
        receivedSequence = frame.sequence;
        ReceivedFrames++;
        CoopPerformanceProbe.MotionChunkReceived();
        // Positive values mean the pose was produced before it arrived: pure transport
        // and host-frame delay that no client-side code can remove. Both times are on the
        // server-synced timeline, unlike NetworkTime.localTime which is per-process.
        CoopPerformanceProbe.MotionTransportAge(NetworkTime.unscaledTime - frame.time);
        foreach (CoopMotionPose pose in frame.poses)
            if (pose.id > 0 && Finite(pose.position) && Finite(pose.rotation)) { CoopPerformanceProbe.MotionPoseApplied(); Add(pose.id, frame.time, pose.position, pose.rotation); }
    }
    private Track Add(long id, double time, Vector3 position, Quaternion rotation)
    {
        if (!tracks.TryGetValue(id, out Track track)) tracks[id] = track = new Track();
        Insert(track, time, position, rotation);
        return track;
    }
    private static void Insert(Track track, double time, Vector3 position, Quaternion rotation)
    {
        if (time <= track.newest) return;
        bool teleport = track.samples.Count == 0 || (position - track.newestPosition).sqrMagnitude > TeleportDistance * TeleportDistance;
        if (teleport && track.samples.Count != 0) CoopPerformanceProbe.MotionTeleport();
        if (teleport) track.samples.Clear();
        if (track.samples.Count >= 32) track.samples.RemoveAt(0);
        SnapshotInterpolation.InsertIfNotExists(track.samples, 32,
            new TransformSnapshot(time, NetworkTime.localTime, position, rotation, Vector3.one));
        track.newest = time; track.newestPosition = position;
        if (teleport && track.component != null) track.component.transform.SetPositionAndRotation(position, rotation);
    }
    public static void ValidateInterpolation()
    {
        var track = new Track();
        Insert(track, 1, Vector3.zero, Quaternion.identity);
        Insert(track, 1.1, Vector3.right, Quaternion.identity);
        Insert(track, 1.05, Vector3.one * 100, Quaternion.identity);
        if (track.newest != 1.1 || track.samples.Count != 2) throw new InvalidOperationException("Older motion replaced the latest pose");
        SnapshotInterpolation.StepInterpolation(track.samples, 1.05, out TransformSnapshot from, out TransformSnapshot to, out double t);
        if ((Vector3.LerpUnclamped(from.position, to.position, (float)t) - Vector3.right * .5f).sqrMagnitude > .000001f)
            throw new InvalidOperationException("Motion interpolation is not continuous between packets");
        Insert(track, 1.2, Vector3.right * 10, Quaternion.identity);
        if (track.samples.Count != 1) throw new InvalidOperationException("Teleport was interpolated through intervening geometry");
        SnapshotInterpolation.StepInterpolation(track.samples, 1.4, out from, out to, out t);
        if (from.position != Vector3.right * 10 || to.position != from.position)
            throw new InvalidOperationException("Motion extrapolated after the packet stream stopped");
        var probe = new GameObject("Snapshot pose isolation test");
        try
        {
            var bound = new Track();
            BindSnapshot(bound, probe.transform, 1, Vector3.zero, Quaternion.identity);
            Insert(bound, 1.1, Vector3.right, Quaternion.identity);
            probe.transform.position = Vector3.right * .5f;
            BindSnapshot(bound, probe.transform, 1.05, Vector3.right * .1f, Quaternion.identity);
            if (probe.transform.position != Vector3.right * .5f)
                throw new InvalidOperationException("Delayed world snapshot rewound an interpolated pose");
            BindSnapshot(bound, probe.transform, 1.2, Vector3.right * 2, Quaternion.identity);
            if (probe.transform.position != Vector3.right * .5f)
                throw new InvalidOperationException("World snapshot bypassed render interpolation");
            Insert(bound, 1.3, Vector3.right * 10, Quaternion.identity);
            if (probe.transform.position != Vector3.right * 10)
                throw new InvalidOperationException("Bound actor teleport failed to snap immediately");
        }
        finally { UnityEngine.Object.Destroy(probe); }
        Debug.Log("[CoopMotionTest] PASS: delayed world snapshots cannot rewind rendered poses; bound teleports remain immediate");
        Debug.Log("[CoopMotionTest] PASS: midpoint interpolation, stale packet rejection, immediate teleport, bounded hold after packet loss");
    }
    public bool Snapshot(long id, Component component, uint generation, double time, Vector3 position, Quaternion rotation)
    {
        if (!(component is CharacterStatus)) return false;
        if (world != generation) { Reset(); world = generation; }
        if (!tracks.TryGetValue(id, out Track track)) tracks[id] = track = new Track();
        BindSnapshot(track, component, time, position, rotation);
        return true;
    }
    private static void BindSnapshot(Track track, Component component, double time, Vector3 position, Quaternion rotation)
    {
        Insert(track, time, position, rotation);
        bool first = track.component != component;
        track.component = component;
        // Reliable world state often arrives after a newer unreliable pose. It
        // seeds new replicas, but must never rewind an already rendered actor.
        if (first && track.samples.Count > 0)
        {
            TransformSnapshot latest = track.samples.Values[track.samples.Count - 1];
            component.transform.SetPositionAndRotation(latest.position, latest.rotation);
        }
    }
    public void Retain(HashSet<long> alive)
    {
        removed.Clear();
        foreach (long id in tracks.Keys) if (!alive.Contains(id)) removed.Add(id);
        foreach (long id in removed) tracks.Remove(id);
    }
    private void LateUpdate()
    {
        var shared = GetComponent<CoopWorld>(); var replication = GetComponent<CoopReplication>();
        var game = GameSystem.instance;
        if (!shared.SharedWorldRunning || game == null || game.map == null) return;
        if (NetworkServer.active)
        {
            if (world != shared.Generation) { Reset(); world = shared.Generation; }
            double now = NetworkTime.localTime;
            if (now < nextSend) return;
            bool hasPeer = false;
            foreach (var connection in NetworkServer.connections.Values)
                if (connection != null && connection.isAuthenticated && connection != NetworkServer.localConnection) { hasPeer = true; break; }
            if (!hasPeer) return;
            long began = CoopPerformanceProbe.Begin("Coop.MotionPublish");
            outgoing.Clear();
            foreach (CharacterStatus actor in game.activeCharacters)
            {
                if (actor == null || !actor.gameObject.activeInHierarchy || !replication.TryHostId(actor, out long id)) continue;
                outgoing.Add(new CoopMotionPose { id = id, position = actor.transform.position, rotation = actor.transform.rotation });
            }
            double interval = CoopCompat.Motion30 || outgoing.Count > MaxPosesPerFrame || !HasSendHeadroom() ? SlowSendInterval : FastSendInterval;
            nextSend = nextSend <= 0 || now - nextSend > interval * 2 ? now + interval : nextSend + interval;
            var frame = new CoopMotionFrame { world = world, time = now };
            // Send the whole world as strictly increasing sequence chunks. A single
            // world frame is larger than the transport batch limit once enough
            // characters exist, and Mirror drops an oversized message outright.
            for (int start = 0; start < outgoing.Count; start += MaxPosesPerFrame)
            {
                int count = Math.Min(MaxPosesPerFrame, outgoing.Count - start);
                frame.sequence = ++sequence;
                frame.poses = new CoopMotionPose[count];
                outgoing.CopyTo(start, frame.poses, 0, count);
                foreach (var connection in NetworkServer.connections.Values)
                    if (connection != null && connection.isAuthenticated && connection != NetworkServer.localConnection)
                        connection.Send(frame, Channels.Unreliable);
            }
            CoopPerformanceProbe.End("motionPublish", began, 20 + outgoing.Count * 36);
            CoopPerformanceProbe.MotionPublishInterval(interval);
        }
        else
        {
            long began = CoopPerformanceProbe.Begin("Coop.MotionRender");
            // NetworkTime.unscaledTime already includes Mirror's adaptive jitter
            // buffer. Adding another fixed delay here would double interpolation lag.
            double time = NetworkTime.unscaledTime;
            foreach (Track track in tracks.Values)
            {
                Component component = track.component;
                if (component == null || !component.gameObject.activeInHierarchy || track.samples.Count == 0) continue;
                SnapshotInterpolation.StepInterpolation(track.samples, time, out TransformSnapshot from, out TransformSnapshot to, out double t);
                if (component == game.player && t > 0 && t < 1 && (to.position - from.position).sqrMagnitude > .000001f) OwnerMovingInterpolations++;
                Quaternion viewRotation = component == game.player ? game.player.rotationTransformReference.rotation : Quaternion.identity;
                Vector3 before = component.transform.position;
                component.transform.SetPositionAndRotation(Vector3.LerpUnclamped(from.position, to.position, (float)t),
                    Quaternion.SlerpUnclamped(from.rotation, to.rotation, (float)t));
                CoopPerformanceProbe.MotionRendered();
                if (component == game.player)
                {
                    // How far the render clock sits behind the sample it is displaying: a
                    // large value is buffering lag, a zero-width pair is a stalled stream.
                    CoopPerformanceProbe.MotionInterpolationDelay(time - from.remoteTime);
                    if (from.remoteTime == to.remoteTime) CoopPerformanceProbe.MotionHeld();
                    // The first-person camera is a child of the interpolated root.
                    // Remote body yaw must not rotate the owner's locally aimed view.
                    game.player.rotationTransformReference.rotation = viewRotation;
                    float distance = Vector3.Distance(before, component.transform.position);
                    CoopPerformanceProbe.OwnerStep(distance);
                    if (distance < TeleportDistance) game.player.movementTrack += distance;
                    if (MotionDiagnostics)
                    {
                        MeasuredFrames++;
                        if (from.remoteTime == to.remoteTime && CoopMotionHandsValidation.Moving) HeldMovingFrames++;
                        CameraYawError += Mathf.Abs(Mathf.DeltaAngle(game.player.horizontalPlaneRotation, game.player.rotationTransformReference.eulerAngles.y));
                    }
                }
            }
            CoopPerformanceProbe.End("motionRender", began);
            if (Diagnostics && Time.unscaledTime >= nextDiagnostic)
            {
                nextDiagnostic = Time.unscaledTime + 5;
                Debug.Log("[CoopMotion] receivedFrames=" + ReceivedFrames + "; ownerMovingInterpolations=" + OwnerMovingInterpolations + "; tracks=" + tracks.Count);
            }
        }
    }
}
