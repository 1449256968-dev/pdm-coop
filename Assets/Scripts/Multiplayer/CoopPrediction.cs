using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

// Owner-only authoritative acknowledgement. Clients send inputs, never positions.
public struct CoopPredictionState : NetworkMessage
{
    public uint world, acknowledged;
    public int owner;
    public double time;
    public Vector3 position;
    public float speed, runMultiplier;
    public bool manual, canRun;
}

[DefaultExecutionOrder(9000)]
public sealed class CoopPrediction : MonoBehaviour
{
    public static CoopPrediction Instance { get; private set; }
    public static readonly bool Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "-coopNoPrediction") < 0;
    private static readonly bool Trace = Array.IndexOf(Environment.GetCommandLineArgs(), "-coopInputTrace") >= 0;
    private static readonly bool MotionDiagnostics = Array.IndexOf(Environment.GetCommandLineArgs(), "-coopMotionHandsValidate") >= 0;
    private const float MaxLead = .5f, Skin = .015f;
    private const int Capacity = 128;
    private struct Step { public uint sequence; public PlayerMovementInput input; public float delta; }
    private struct ServerInput { public uint sequence; public float at, speed, runMultiplier; public bool canRun, manual; }
    private readonly Dictionary<int, ServerInput> serverInputs = new Dictionary<int, ServerInput>();
    private readonly List<Step> history = new List<Step>(Capacity);
    private readonly RaycastHit[] hits = new RaycastHit[32];
    private PlayerMovementInput currentInput;
    private uint currentSequence, generation;
    private CoopPredictionState received;
    private double appliedTime = double.NegativeInfinity;
    private float receivedAt, nextTrace, pendingSeconds;
    private bool initialized, owns, hasState;
    private Vector3 simulated, correction;
    private PathNode node;
    private long inputCaptured;
    private bool inputOnset;
    private float responseMs, lastError;
    private int hardCorrections, wallClips, stalledFrames;
    private CoopMotion motion;

    public static bool Owns(Component actor) => Enabled && Instance != null && Instance.owns &&
        !NetworkServer.active && GameSystem.instance != null && actor == GameSystem.instance.player;

    private void Awake() { Instance = this; motion = GetComponent<CoopMotion>(); }
    public void StartClient() { ResetClient(); NetworkClient.RegisterHandler<CoopPredictionState>(Receive); }
    public void StopClient() { NetworkClient.UnregisterHandler<CoopPredictionState>(); ResetClient(); }
    public void ResetClient()
    {
        history.Clear(); currentInput = default; currentSequence = generation = 0;
        hasState = initialized = owns = inputOnset = false; pendingSeconds = 0; correction = Vector3.zero;
        appliedTime = double.NegativeInfinity; node = null;
        hardCorrections = wallClips = stalledFrames = 0; responseMs = lastError = 0;
    }
    public void ResetServer() { serverInputs.Clear(); }
    public static void Release(int playerId) { if (Instance != null) Instance.serverInputs.Remove(playerId); }
    private void OnDestroy() { if (Instance == this) Instance = null; }

    // Called only from the original host physics tick, in the actor's context.
    public static void Consumed(PlayerScript actor, uint sequence)
    {
        if (Instance == null || !NetworkServer.active || actor == null) return;
        Instance.serverInputs[actor.CoopPlayerId] = new ServerInput {
            sequence = sequence, at = Time.unscaledTime, speed = actor.CurrentMovementSpeed(),
            runMultiplier = actor.npcType.runSpeedMultiplier, canRun = !actor.staminaLocked,
            // Replay currently models walking against static world geometry only.
            // A moving UFO adds transport velocity and updates its colliders at the
            // snapshot rate; let the authoritative pose stream own its passengers.
            manual = !(actor.currentNode?.associatedRoom is UFOMover) && !actor.dashActive && actor.knockbackVelocity.sqrMagnitude < .0001f &&
                !CoopDialogue.HasPending(actor.CoopPlayerId) && !CoopUiBridge.HasSession(actor.CoopPlayerId)
        };
    }

    public static void InputSent(uint sequence, PlayerMovementInput input)
    {
        if (!Enabled || Instance == null || NetworkServer.active) return;
        var predictor = Instance;
        if (input.strafe * input.strafe + input.forward * input.forward > .001f &&
            predictor.currentInput.strafe * predictor.currentInput.strafe + predictor.currentInput.forward * predictor.currentInput.forward < .001f)
        {
            predictor.inputCaptured = System.Diagnostics.Stopwatch.GetTimestamp();
            predictor.inputOnset = true;
        }
        predictor.currentInput = input;
        predictor.currentSequence = sequence;
    }

    // Shared with original PlayerScript: preserves its diagonal/analogue speed rule.
    public static Vector3 InputVelocity(PlayerMovementInput input, float speed)
    {
        Vector3 direction = new Vector3(input.strafe, 0, input.forward);
        float magnitude = Mathf.Max(Mathf.Abs(input.strafe), Mathf.Abs(input.forward));
        return Quaternion.Euler(0, input.yaw, 0) * direction.normalized * (speed * magnitude);
    }

    private void Receive(CoopPredictionState state)
    {
        var world = GetComponent<CoopWorld>();
        if (!Enabled || NetworkServer.active || !NetworkClient.isConnected || CoopReplication.Instance == null ||
            state.owner != CoopReplication.Instance.LocalPlayerId || state.world != world.Generation ||
            double.IsNaN(state.time) || double.IsInfinity(state.time) ||
            !Finite(state.position) || !Finite(state.speed) || !Finite(state.runMultiplier) ||
            state.speed < 0 || state.runMultiplier < 0) return;
        if (generation != state.world) { ResetClient(); generation = state.world; }
        if (hasState && state.time <= received.time) return;
        received = state; receivedAt = Time.unscaledTime; hasState = true;
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

    private void LateUpdate()
    {
        var game = GameSystem.instance;
        var world = GetComponent<CoopWorld>();
        if (!world.SharedWorldRunning || game == null || game.player == null) { owns = false; return; }
        if (NetworkServer.active)
        {
            if (generation != world.Generation) { ResetServer(); generation = world.Generation; }
            var session = GetComponent<CoopSession>();
            var registry = GetComponent<CoopPlayerRegistry>();
            foreach (var connection in NetworkServer.connections.Values)
            {
                // Unreliable state may overtake the reliable Welcome. Ready proves that
                // this client has processed Welcome and accepted authentication already.
                if (connection == null || !connection.isAuthenticated || !connection.isReady || connection == NetworkServer.localConnection) continue;
                int id = session.GetPlayerId(connection);
                if (!registry.TryGetPlayer(id, out PlayerScript actor) || actor == null) continue;
                serverInputs.TryGetValue(id, out ServerInput input);
                connection.Send(new CoopPredictionState {
                    world = generation, owner = id, time = NetworkTime.localTime,
                    acknowledged = input.sequence, position = actor.rigidbodyDirectReference.position,
                    speed = input.speed, runMultiplier = input.runMultiplier, canRun = input.canRun,
                    manual = game.gameInProgress && !game.pauseAllInteraction && !game.pauseActionOnly &&
                        actor.gameObject.activeInHierarchy && input.manual && Time.unscaledTime - input.at < .06f
                }, Channels.Unreliable);
            }
            return;
        }
        if (generation != world.Generation) { ResetClient(); generation = world.Generation; }
        bool permitted = Enabled && !CoopConnectionPanel.BlocksLocalInput && hasState && received.manual && Time.unscaledTime - receivedAt < MaxLead &&
            CoopReplication.Instance != null && received.owner == CoopReplication.Instance.LocalPlayerId &&
            game.gameInProgress && game.player.gameObject.activeInHierarchy && !game.pauseAllInteraction && !game.pauseActionOnly &&
            game.mainGameUI.activeSelf && game.inputWatcher.inputWatchers.Count == 0 &&
            !(game.player.currentNode?.associatedRoom is UFOMover) &&
            game.player.currentAI != null && game.player.currentAI.ObeyPlayerInput();
        if (!permitted)
        {
            owns = initialized = false; history.Clear(); pendingSeconds = 0; correction = Vector3.zero;
            appliedTime = double.NegativeInfinity; inputOnset = false;
            return;
        }
        var player = game.player;
        Vector3 before = player.transform.position;
        if (received.time > appliedTime)
        {
            Vector3 prior = initialized ? simulated : received.position;
            int acknowledged = 0;
            while (acknowledged < history.Count && unchecked((int)(history[acknowledged].sequence - received.acknowledged)) <= 0) acknowledged++;
            if (acknowledged > 0) history.RemoveRange(0, acknowledged);
            pendingSeconds = 0;
            simulated = received.position;
            node = player.currentNode;
            for (int i = 0; i < history.Count; i++)
            {
                simulated = Move(player, simulated, history[i].input, history[i].delta);
                pendingSeconds += history[i].delta;
            }
            Vector3 error = prior - simulated;
            lastError = error.magnitude;
            if (!initialized || lastError > 2f || Mathf.Abs(error.y) > .75f)
            {
                correction = Vector3.zero;
                if (initialized) { hardCorrections++; history.Clear(); pendingSeconds = 0; simulated = received.position; }
            }
            else correction = Vector3.ClampMagnitude(correction + error, .5f);
            initialized = true;
            appliedTime = received.time;
        }
        float delta = Mathf.Clamp(Time.deltaTime, 0, .05f);
        bool stalled = false;
        if (history.Count < Capacity && pendingSeconds + delta <= MaxLead && currentSequence != 0)
        {
            history.Add(new Step { sequence = currentSequence, input = currentInput, delta = delta });
            pendingSeconds += delta;
            simulated = Move(player, simulated, currentInput, delta);
        }
        else { stalledFrames++; stalled = true; }
        correction *= Mathf.Exp(-delta / .08f);
        player.transform.position = Clip(player, simulated, correction);
        player.latestRigidBodyPosition = player.transform.position;
        owns = true;
        float moved = Vector3.Distance(before, player.transform.position);
        CoopPerformanceProbe.OwnerStep(moved);
        if (moved < 4) player.movementTrack += moved;
        if (MotionDiagnostics && motion != null)
        {
            motion.MeasuredFrames++;
            if (stalled && CoopMotionHandsValidation.Moving) motion.HeldMovingFrames++;
            motion.CameraYawError += Mathf.Abs(Mathf.DeltaAngle(player.horizontalPlaneRotation, player.rotationTransformReference.eulerAngles.y));
        }
        if (inputOnset && Vector3.Distance(before, player.transform.position) > .001f)
        {
            responseMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - inputCaptured) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            inputOnset = false;
        }
        if (Trace && Time.unscaledTime >= nextTrace)
        {
            nextTrace = Time.unscaledTime + 1;
            Debug.Log("[CoopPrediction] active=True; ack=" + received.acknowledged + "; sent=" + currentSequence +
                "; pending=" + history.Count + "; leadMs=" + (pendingSeconds * 1000).ToString("F1") +
                "; sampleToMoveMs=" + responseMs.ToString("F2") + "; correction=" + lastError.ToString("F3") +
                "; hardCorrections=" + hardCorrections + "; wallClips=" + wallClips + "; stalledFrames=" + stalledFrames);
        }
    }

    private Vector3 Move(PlayerScript player, Vector3 position, PlayerMovementInput input, float delta)
    {
        float speed = received.speed * (input.run && received.canRun ? received.runMultiplier : 1);
        Vector3 displacement = InputVelocity(input, speed) * delta;
        if (node != null && node.associatedRoom != null)
        {
            foreach (var room in node.associatedRoom.connectedRooms.Keys)
                if (room.Contains(position + displacement) && !player.npcType.CanAccessRoom(room, player)) return position;
        }
        Vector3 result = Clip(player, position, displacement);
        if (node != null && !node.Contains(result)) node = PathNode.FindContainingNode(result, node);
        return result;
    }

    // Kinematic sweep/slide against the same scene colliders; never runs triggers,
    // timers, AI, combat, item use, or Physics.Simulate during input replay.
    private Vector3 Clip(PlayerScript player, Vector3 position, Vector3 displacement)
        => Sweep(player.transform, position, displacement);

    private Vector3 Sweep(Transform owner, Vector3 position, Vector3 displacement)
    {
        for (int iteration = 0; iteration < 3 && displacement.sqrMagnitude > .000001f; iteration++)
        {
            float distance = displacement.magnitude;
            Vector3 direction = displacement / distance;
            int count = Physics.SphereCastNonAlloc(position + Vector3.up * .25f, .2f,
                direction, hits, distance + Skin, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (count == hits.Length) { wallClips++; break; } // saturated query: fail closed
            float nearest = distance + Skin;
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.collider == null || hit.collider.transform.IsChildOf(owner) ||
                    Physics.GetIgnoreLayerCollision(owner.gameObject.layer, hit.collider.gameObject.layer) ||
                    (hit.normal.y > .7f && displacement.y >= -.001f)) continue;
                if (hit.distance < nearest) { nearest = hit.distance; normal = hit.normal; }
            }
            if (normal == Vector3.zero) { position += displacement; break; }
            float advance = Mathf.Max(0, nearest - Skin);
            position += direction * advance;
            displacement = Vector3.ProjectOnPlane(displacement - direction * advance, normal);
            wallClips++;
        }
        return position;
    }

    public static void Validate()
    {
        foreach (float yaw in new[] { 0f, 45f, 90f, 270f })
        foreach (float x in new[] { -1f, -.3f, 0f, .3f, 1f })
        foreach (float z in new[] { -1f, -.3f, 0f, .3f, 1f })
        {
            var input = new PlayerMovementInput { strafe = x, forward = z, yaw = yaw };
            Vector3 original = x == 0 && z == 0 ? Vector3.zero :
                Quaternion.Euler(0, yaw - 57.29578f * Mathf.Atan2(z, x) + 90, 0) *
                Vector3.forward * 5 * Mathf.Max(Mathf.Abs(x), Mathf.Abs(z));
            if ((original - InputVelocity(input, 5)).sqrMagnitude > .000001f)
                throw new InvalidOperationException("Prediction changed original analogue/diagonal movement speed");
        }
        if (Instance == null) throw new InvalidOperationException("Prediction component missing from network manager");
        var own = new GameObject("Prediction sweep owner");
        var wall = new GameObject("Prediction sweep wall");
        try
        {
            Vector3 start = new Vector3(1000, 1000, 1000);
            own.transform.position = start;
            var self = own.AddComponent<SphereCollider>(); self.center = Vector3.up * .25f; self.radius = .2f;
            wall.transform.position = start + new Vector3(0, .25f, 2);
            wall.AddComponent<BoxCollider>().size = new Vector3(10, 4, .2f);
            Physics.SyncTransforms();
            Vector3 blocked = Instance.Sweep(own.transform, start, Vector3.forward * 3);
            if (blocked.z > start.z + 1.71f || blocked.z < start.z + 1.6f)
                throw new InvalidOperationException("Predicted movement crossed a wall or hit its own collider");
            Vector3 slide = Instance.Sweep(own.transform, start, new Vector3(1, 0, 3));
            if (slide.x < start.x + .9f || slide.z > start.z + 1.71f)
                throw new InvalidOperationException("Predicted movement failed to slide along a wall");
        }
        finally { Destroy(own); Destroy(wall); }
        Debug.Log("[CoopPredictionTest] PASS: original movement speeds, self-collider rejection, wall blocking and sliding");
    }
}
