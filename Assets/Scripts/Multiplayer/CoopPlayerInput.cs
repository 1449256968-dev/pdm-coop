using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>One remote movement/aim sample sent from a client to the host.</summary>
public struct CoopPlayerInputMessage : NetworkMessage
{
    public uint sequence;
    public float strafe;
    public float forward;
    public float yaw;
    public bool run;
}

/// <summary>
/// Client sends sanitized movement input at a fixed cadence; the host validates, stores
/// the latest sample per player and falls back to neutral input when a sample goes stale.
/// Nothing is consumed by the world simulation until the shared world phase is started
/// (see Active). The host player reads local input directly and never sends to itself.
/// </summary>
public sealed class CoopPlayerInputFlow : MonoBehaviour
{
    // 60Hz instead of 30Hz: the host consumes the newest sample each fixed step anyway, so
    // the faster stream removes half of the input quantisation (the average wait for the
    // next uplink) from the local player's response time. The message is tiny and unreliable.
    // -coopCompatInput30 restores the previous 30Hz rate for bisection.
    private static readonly float SendInterval = CoopCompat.Input30 ? 1f / 30f : 1f / 60f;
    // Opt-in end-to-end trace for movement reports: shows what this client actually captured
    // and what the host actually consumed. Inert without -coopInputTrace.
    internal static readonly bool InputTrace = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-coopInputTrace") >= 0;
    private float nextTrace;
    private long sentSamples;
    private int hostSamples;
    private const float MaxInputAge = 0.25f;
    private static readonly bool BurstValidation = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-coopInputBurstValidate") >= 0;

    private readonly Dictionary<int, PlayerMovementInput> latestInput = new Dictionary<int, PlayerMovementInput>();
    private readonly Dictionary<int, uint> latestSequence = new Dictionary<int, uint>();
    private readonly Dictionary<int, uint> consumedSequence = new Dictionary<int, uint>();
    private readonly Dictionary<int, float> lastReceived = new Dictionary<int, float>();
    private readonly HashSet<int> announced = new HashSet<int>();

    private uint localSequence;
    private float nextSend;

    /// <summary>Set by the shared-world start path; gates client sending until then.</summary>
    public bool Active { get; set; }
    public System.Func<PlayerMovementInput> ValidationInputSource { get; set; }

    private CoopSession Session => GetComponent<CoopSession>();

    public void StartServer()
    {
        consumedSequence.Clear();
        NetworkServer.RegisterHandler<CoopPlayerInputMessage>(OnServerInput);
        latestInput.Clear();
        latestSequence.Clear();
        lastReceived.Clear();
        announced.Clear();
    }

    public void StopServer()
    {
        consumedSequence.Clear();
        NetworkServer.UnregisterHandler<CoopPlayerInputMessage>();
        latestInput.Clear();
        latestSequence.Clear();
        lastReceived.Clear();
        announced.Clear();
    }

    public void StartClient()
    {
        localSequence = 0;
        nextSend = 0f;
    }

    public void StopClient()
    {
        localSequence = 0;
    }

    public void Release(int playerId)
    {
        CoopPrediction.Release(playerId);
        consumedSequence.Remove(playerId);
        latestInput.Remove(playerId);
        latestSequence.Remove(playerId);
        lastReceived.Remove(playerId);
        announced.Remove(playerId);
    }
    internal bool TryLatestSample(int playerId, out PlayerMovementInput input, out uint sequence)
    {
        sequence = 0;
        return latestInput.TryGetValue(playerId, out input) && latestSequence.TryGetValue(playerId, out sequence);
    }

    /// <summary>Latest validated sample, or neutral (zero) input when none exists or it expired.</summary>
    public PlayerMovementInput Read(int playerId)
    {
        if (CoopDialogue.HasPending(playerId) || CoopUiBridge.HasSession(playerId)) return default(PlayerMovementInput);
        PlayerMovementInput input;
        float received;
        if (!latestInput.TryGetValue(playerId, out input) || !lastReceived.TryGetValue(playerId, out received) ||
            Time.unscaledTime - received > MaxInputAge)
        {
            CoopPerformanceProbe.InputStale();
            return default(PlayerMovementInput);
        }
        // Age at the moment the host simulation consumed the sample. This is the part
        // of the round trip the host cannot shorten by simulating sooner.
        CoopPerformanceProbe.InputAge(Time.unscaledTime - received);
        if (latestSequence.TryGetValue(playerId, out uint sequence)) consumedSequence[playerId] = sequence;
        return input;
    }
    public uint ConsumedSequence(int playerId) => consumedSequence.TryGetValue(playerId, out uint sequence) ? sequence : 0;

    private void LateUpdate()
    {
        if (NetworkServer.active) return; // the host player reads local input directly
        if (CoopReplication.Instance == null || CoopReplication.Instance.LocalPlayerId == 0) return;
        if (!Active || !NetworkClient.isConnected || NetworkClient.connection == null ||
            !NetworkClient.connection.isAuthenticated || GameSystem.settings == null ||
            GameSystem.settings.keySettingsLists == null) return;
        if (Time.unscaledTime < nextSend) return;
        nextSend = nextSend <= 0 || Time.unscaledTime - nextSend > SendInterval * 2 ? Time.unscaledTime + SendInterval : nextSend + SendInterval;

        float yaw = 0f;
        var game = GameSystem.instance;
        if (game != null && game.player != null) yaw = game.player.horizontalPlaneRotation;

        PlayerMovementInput input = (ValidationInputSource == null ? PlayerMovementInput.CaptureLocal(yaw) : ValidationInputSource()).Sanitized();
        if (CoopConnectionPanel.BlocksLocalInput) input = new PlayerMovementInput { yaw = yaw };
        CoopPerformanceProbe.InputSend();
        // Opt-in real transport regression: two messages in one batch must leave
        // the newest movement sample active on the server, even at one frame time.
        if (BurstValidation)
            NetworkClient.Send(new CoopPlayerInputMessage { sequence = ++localSequence, yaw = input.yaw }, Channels.Unreliable);
        NetworkClient.Send(new CoopPlayerInputMessage
        {
            sequence = ++localSequence,
            strafe = input.strafe,
            forward = input.forward,
            yaw = input.yaw,
            run = input.run
        }, Channels.Unreliable);
        CoopPrediction.InputSent(localSequence, input);
        sentSamples++;
        if (InputTrace && Time.unscaledTime >= nextTrace)
        {
            nextTrace = Time.unscaledTime + 1f;
            var trace = GameSystem.instance;
            Debug.Log("[CoopInput] sent=" + sentSamples + " total=" + localSequence + "; strafe=" + input.strafe.ToString("F2") +
                "; forward=" + input.forward.ToString("F2") + "; run=" + input.run + "; yaw=" + input.yaw.ToString("F1") +
                "; playerId=" + CoopReplication.Instance.LocalPlayerId + "; mainGame=" + trace.mainGameUI.activeSelf +
                "; pause=" + trace.pauseAllInteraction + "; watchers=" + trace.inputWatcher.inputWatchers.Count +
                "; blocked=" + CoopConnectionPanel.BlocksLocalInput + "; inWorld=" + trace.gameInProgress +
                "; bindings=" + (GameSystem.settings.keySettingsLists == null ? -1 : GameSystem.settings.keySettingsLists.Count) +
                MovementDiagnostics());
        }
    }
    // Raw input vs bound input: tells a real report whether the keyboard reaches Unity at all
    // and whether the movement bindings still resolve to a held key.
    private static string MovementDiagnostics()
    {
        string axes;
        try
        {
            axes = Input.GetAxis("ThumbstickAxis" + GameSystem.settings.moveXAxis).ToString("F2") + "/" +
                   Input.GetAxis("ThumbstickAxis" + GameSystem.settings.moveYAxis).ToString("F2");
        }
        catch (System.Exception exception) { axes = "THREW:" + exception.GetType().Name; }
        var text = new System.Text.StringBuilder();
        text.Append("; axes=").Append(axes);
        text.Append("; rawW=").Append(Input.GetKey(KeyCode.W));
        text.Append("; rawUp=").Append(Input.GetKey(KeyCode.UpArrow));
        foreach (HoPInput action in new[] { HoPInput.MoveForwards, HoPInput.MoveBackwards, HoPInput.StrafeLeft, HoPInput.StrafeRight })
        {
            int count = 0, held = 0;
            if (GameSystem.settings.keySettingsLists.TryGetValue(action, out var bindings))
            {
                count = bindings.Count;
                foreach (var binding in bindings) if (binding.CheckForKeyHeld()) held++;
            }
            text.Append("; ").Append(action).Append('=').Append(held).Append('/').Append(count);
        }
        return text.ToString();
    }

    private void OnServerInput(NetworkConnectionToClient connection, CoopPlayerInputMessage message)
    {
        if (!NetworkServer.active || connection == null) return;
        if (!GetComponent<CoopWorld>().SharedWorldRunning || GameSystem.instance == null || !GameSystem.instance.gameInProgress) return;
        int playerId = Session.GetPlayerId(connection);
        if (playerId == 0) return;

        PlayerMovementInput input = new PlayerMovementInput
        {
            strafe = message.strafe,
            forward = message.forward,
            yaw = message.yaw,
            run = message.run
        }.Sanitized();

        uint lastSeq;
        if (latestSequence.TryGetValue(playerId, out lastSeq) && message.sequence <= lastSeq) return;

        // Several valid samples can arrive in one Unity frame after a stall.
        // Coalesce to the newest sequence instead of preserving the oldest one.
        float now = Time.unscaledTime;
        float interval = lastReceived.TryGetValue(playerId, out float previous) ? now - previous : 0f;
        CoopPerformanceProbe.InputInterval(interval);
        CoopPerformanceProbe.InputSample();
        latestSequence[playerId] = message.sequence;
        lastReceived[playerId] = now;
        latestInput[playerId] = input;
        hostSamples++;
        if (InputTrace && Time.unscaledTime >= nextTrace)
        {
            nextTrace = Time.unscaledTime + 1f;
            Debug.Log("[CoopInputHost] player=" + playerId + "; samples=" + hostSamples + "; seq=" + message.sequence +
                "; strafe=" + input.strafe.ToString("F2") + "; forward=" + input.forward.ToString("F2") + "; run=" + input.run +
                "; lastIntervalMs=" + (interval * 1000f).ToString("F1"));
        }

        if (!announced.Contains(playerId))
        {
            announced.Add(playerId);
            Debug.Log("[Coop] Input stream active for player " + playerId);
        }
    }
}
