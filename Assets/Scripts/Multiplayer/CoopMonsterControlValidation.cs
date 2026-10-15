using System;
using System.Linq;
using Mirror;
using UnityEngine;

public struct CoopMonsterControlCommand : NetworkMessage { public int phase; public string species; }
public struct CoopMonsterControlAck : NetworkMessage { public bool camera, toggle; }

// Opt-in real peers: follows the original three-player assimilation sequence.
[DefaultExecutionOrder(12000)]
public sealed class CoopMonsterControlValidation : MonoBehaviour
{
    private bool started, finished, enteredVanity, lostDistance, toggle, keyPending;
    private int phase = -1, acknowledgements;
    private bool clientsPassed = true;
    private bool inputPrepared, aimPrepared, autoArmed, transformLogged;
    private float nextAutoTry, transformDeadline, stableSince, nextDiagnostic, settledSince;
    private static int TransformTicks(AIState state)
    {
        if (state is DullahanTransformState dullahan) return dullahan.transformTicks;
        if (state is PumpkinheadTransformState pumpkin) return pumpkin.transformTicks;
        return -1;
    }
    private string expectedSpecies;
    private readonly System.Collections.Generic.Dictionary<PlayerScript, Vector3> origins = new System.Collections.Generic.Dictionary<PlayerScript, Vector3>();
    private float began;
    private PlayerScript remote;
    private Vector3 origin;
    // Opt-in full-roster sweep: every start-as species is driven through the same
    // real-player control regression, and results are reported instead of stopping.
    private static readonly string[] FullArguments = Environment.GetCommandLineArgs();
    private static readonly bool RosterMode = FullArguments.Contains("-coopMonsterRoster");
    private static int Argument(string name)
    {
        for (int i = 0; i < FullArguments.Length; i++)
        {
            if (FullArguments[i] == name && i + 1 < FullArguments.Length && int.TryParse(FullArguments[i + 1], out int value)) return value;
            string prefix = name + "=";
            if (FullArguments[i].StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(FullArguments[i].Substring(prefix.Length), out value)) return value;
        }
        return 0;
    }
    private static readonly int RosterSkip = Argument("-coopMonsterRosterSkip");
    private static readonly int RosterTake = Argument("-coopMonsterRosterCount");
    private static string RosterOnly
    {
        get
        {
            for (int i = 0; i < FullArguments.Length; i++)
            {
                if (FullArguments[i] == "-coopMonsterRosterOnly" && i + 1 < FullArguments.Length) return FullArguments[i + 1];
                string prefix = "-coopMonsterRosterOnly=";
                if (FullArguments[i].StartsWith(prefix, StringComparison.Ordinal)) return FullArguments[i].Substring(prefix.Length);
            }
            return null;
        }
    }
    private static readonly float RosterHold = 7f;
    private static readonly float RosterWindow = 20f;
    private readonly System.Collections.Generic.List<string> rosterSpecies = new System.Collections.Generic.List<string>();
    private readonly System.Collections.Generic.List<string> rosterResult = new System.Collections.Generic.List<string>();
    private readonly System.Collections.Generic.List<string> rosterDetail = new System.Collections.Generic.List<string>();
    private int rosterIndex, rosterFailures;
    private string rosterCurrent;
    private static bool Enabled => FullArguments.Contains("-coopMonsterControlValidate") || RosterMode;
    private static bool autopilotOff => !GameSystem.settings.autopilotActive || !GameSystem.settings.autopilotPassive;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Enabled) new GameObject("Coop Monster Control Validation").AddComponent<CoopMonsterControlValidation>();
    }
    private void Start()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopHost"))
        {
            NetworkServer.RegisterHandler<CoopMonsterControlAck>((c, a) => {
                acknowledgements++; clientsPassed &= a.camera && a.toggle;
                Debug.Log("[CoopMonsterControl] client=" + c.connectionId + "; camera=" + a.camera + "; toggle=" + a.toggle);
            });
        }
        else NetworkClient.RegisterHandler<CoopMonsterControlCommand>(command => {
            // The prior assimilation driver otherwise keeps replacing movement with
            // neutral input every Update, invalidating the post-transform test.
            var assimilation = FindObjectOfType<CoopAssimilationProbe>();
            if (assimilation != null) assimilation.enabled = false;
            phase = command.phase; expectedSpecies = command.species; inputPrepared = aimPrepared = false; stableSince = settledSince = 0f;
        });
    }
    private void LateUpdate()
    {
        var game = GameSystem.instance;
        if (finished || game == null || !game.gameInProgress || CoopReplication.Instance == null) return;
        if (NetworkServer.active)
        {
            // Roster mode needs no assimilation prerequisite: it drives species
            // directly. Waiting for that driver overlapped its own Pixie transforms
            // with the measurement and corrupted the result.
            var registry = NetworkManager.singleton.GetComponent<CoopPlayerRegistry>();
            // Count includes the host. Starting at two can miss the second client's
            // spawn and command, leaving it Human and guaranteeing an ack timeout.
            bool ready = (RosterMode || CoopAssimilationProbe.Completed) && registry.Count >= 3;
            if (!started && ready)
            {
                started = true; phase = 0;
                if (RosterMode)
                {
                    var assimilation = FindObjectOfType<CoopAssimilationProbe>();
                    if (assimilation != null) assimilation.enabled = false;
                    StartRoster();
                }
                else BeginPhase();
            }
            // Roster phases advance only after both clients reported (or the window
            if (RosterMode && started && !finished && Time.unscaledTime >= nextDiagnostic)
            {
                nextDiagnostic = Time.unscaledTime + 1f;
                foreach (var actor in registry.Players.Where(p => p.IsRemotePlayer)) TraceActor("host", actor);
            }
            // Roster phases advance only after both clients reported (or the window
            // expires), so a client never measures a species the host already left.
            // Transform-derived species need a longer host window than the plain
            // control check, because the client withholds its ack until the state ends.
            bool rosterReady = RosterMode && acknowledgements >= 2 && Time.unscaledTime - began > RosterHold;
            if (started && remote != null && !(RosterMode && rosterIndex >= rosterSpecies.Count) &&
                (RosterMode ? (rosterReady || Time.unscaledTime - began > RosterWindow) : Time.unscaledTime - began > 4f))
            {
                bool obey;
                using (new CoopActorContext(remote)) obey = remote.currentAI.ObeyPlayerInput();
                float moved = Vector3.ProjectOnPlane(remote.transform.position - origin, Vector3.up).magnitude;
                bool allMoved = true, movementValid = true;
                int stationaryActors = 0;
                foreach (var pair in origins)
                {
                    bool actorObeys;
                    using (new CoopActorContext(pair.Key)) actorObeys = pair.Key.currentAI.ObeyPlayerInput();
                    bool actorMoved = Vector3.ProjectOnPlane(pair.Key.transform.position - pair.Value, Vector3.up).magnitude > .1f;
                    bool stationary = Mathf.Approximately(pair.Key.npcType.movementSpeed, 0f);
                    if (stationary) stationaryActors++;
                    allMoved &= actorObeys && actorMoved;
                    // Entoloma is intentionally stationary (original movementSpeed=0).
                    // It must still obey manual input and pass the same client checks.
                    movementValid &= actorObeys && (stationary || actorMoved);
                }
                NetworkManager.singleton.GetComponent<CoopPlayerInputFlow>().TryLatestSample(remote.CoopPlayerId, out var sample, out uint sequence);
                Debug.Log("[CoopMonsterControl] phase=" + phase + "; species=" + remote.npcType.name + "; remoteObey=" + obey + "; moved=" + moved + "; bothMoved=" + allMoved + "; forward=" + sample.forward + "; yaw=" + sample.yaw + "; sequence=" + sequence + "; hostAutopilot=" + GameSystem.settings.autopilotActive + "; acks=" + acknowledgements +
                    "; remotePrefs=" + CoopPlayerControl.For(remote.CoopPlayerId) + "; state=" + (remote.currentAI == null || remote.currentAI.currentState == null ? "none" : remote.currentAI.currentState.GetType().Name));
                Debug.Log("[CoopMonsterControl] movementValid=" + movementValid + "; stationaryActors=" + stationaryActors);
                bool passed = obey && movementValid && sample.forward == 1 && acknowledgements == 2 && clientsPassed && GameSystem.settings.autopilotActive;
                Debug.Log(passed ? "[CoopMonsterControl] HOST PASS" : "[CoopMonsterControl] HOST FAIL");
                if (RosterMode)
                {
                    RecordRoster(passed, "remoteObey=" + obey + ";bothMoved=" + allMoved + ";movementValid=" + movementValid + ";stationaryActors=" + stationaryActors + ";forward=" + sample.forward + ";acks=" + acknowledgements + ";clientsPassed=" + clientsPassed);
                    AdvanceRoster();
                    return;
                }
                if (!passed || phase == 3)
                {
                    finished = true;
                    if (passed) Debug.Log("[CoopMonsterControl] ALL PASS: Pixie/Fairy/Centaur/Dog; both clients move; local camera and controls; host preference isolation");
                }
                else { phase++; BeginPhase(); }
            }
            return;
        }
        if (CoopReplication.Instance.LocalPlayerId == 0) return;
        var player = game.player;
        if (RosterMode && phase >= 0 && Time.unscaledTime >= nextDiagnostic)
        {
            nextDiagnostic = Time.unscaledTime + 1f;
            TraceActor("client", player);
        }
        // Arm only on a sustained species match. During the assimilation driver's
        // transform hand-off the local player momentarily reports the target species
        // while still inside a transform state; arming on that false positive made the
        // measurement window expire before the real transformation had finished.
        if (phase >= 0 && !inputPrepared && player.npcType.name == expectedSpecies)
        {
            if (stableSince <= 0f) stableSince = Time.unscaledTime;
            if (Time.unscaledTime - stableSince < 1f) return;
            // A previous species may have left accumulated camera state behind.
            inputPrepared = true; began = Time.unscaledTime; autoArmed = false; transformLogged = false;
            if (RosterMode) { enteredVanity = false; lostDistance = false; }
            GameSystem.settings.autopilotHuman = GameSystem.settings.autopilotActive = GameSystem.settings.autopilotPassive = true;
            keyPending = true; nextAutoTry = 0f;
            CoopActions.ValidationKeySource = (key, edge) => {
                if (key != HoPInput.ToggleAutopilot || edge != 1) return null;
                bool value = keyPending || autoArmed;
                keyPending = false; autoArmed = false;
                return value;
            };
            CoopActions.Instance.ValidationInputSource = () => new CoopActionSample { quickItem = -1 };
            NetworkManager.singleton.GetComponent<CoopPlayerInputFlow>().ValidationInputSource =
                () => new PlayerMovementInput { forward = 1, yaw = 90 };
        }
        else if (player.npcType.name != expectedSpecies) stableSince = 0f;
        if (phase >= 0 && inputPrepared && !keyPending && !aimPrepared)
        {
            // Test a look change after manual control is active; before that, the
            // original autopilot is correctly allowed to turn the character.
            player.horizontalPlaneRotation = 90; player.CoopViewPitch = 23;
            aimPrepared = true;
        }
        if (player.vanityCamera)
        {
            if (enteredVanity && Mathf.Abs(player.vanityCameraDistance - 3.1f) > .01f) lostDistance = true;
            enteredVanity = true; player.vanityCameraDistance = 3.1f;
        }
        // The original toggle key is an edge, and ReplicaView drops it whenever a
        // local UI owns input that frame. Retry until the manual-control preference
        // really applies locally, so the harness measures control and not one lost edge.
        if (RosterMode && phase >= 0 && inputPrepared && Time.unscaledTime - began > 1f && Time.unscaledTime >= nextAutoTry && !autopilotOff)
        {
            nextAutoTry = Time.unscaledTime + 1f;
            autoArmed = true;
        }
        // Some species enter a real original transform state first (Pumpkinhead spawns
        // the Dullahan transform). Input is legitimately blocked until that state ends,
        // so the roster window outlasts it instead of failing early. One line only.
        bool transforming = player.currentAI != null && player.currentAI.currentState is GeneralTransformState;
        // Snapshots apply in LateUpdate, after PlayerScript.Update has updated the
        // camera. A transform ending in this frame needs another view update before
        // camera assertions. Wait a bounded state-only settling window, so a genuinely
        // stuck camera still fails rather than extending the test indefinitely.
        if (transforming) settledSince = 0f;
        else if (settledSince <= 0f) settledSince = Time.unscaledTime;
        if (!transforming) { transformDeadline = 0f; transformLogged = false; }
        else if (transformDeadline <= 0f) transformDeadline = began + 12f;
        else if (!transformLogged && Time.unscaledTime > transformDeadline)
        {
            transformLogged = true;
            Debug.Log("[CoopMonsterControl] transform still running; species=" + player.npcType.name +
                "; state=" + player.currentAI.currentState.GetType().Name +
                "; ticks=" + TransformTicks(player.currentAI.currentState) +
                "; survived=" + (Time.unscaledTime - began).ToString("F1") + "s; reporting now");
        }
        if (RosterMode && phase >= 0 && inputPrepared && Time.unscaledTime - began > 6f && !transforming && Time.unscaledTime - settledSince >= 1f &&
            (autopilotOff || !autoArmed || Time.unscaledTime - began > 14f))
        {
            // The original camera transition, autopilot toggle and remote authority
            // all need a settled window; report only after the client has converged.
            ReportRoster();
        }
        if (!RosterMode && phase >= 0 && inputPrepared && Time.unscaledTime - began > 2)
        {
            toggle = player.currentAI.ObeyPlayerInput();
            bool camera = enteredVanity && !lostDistance && !player.vanityCamera && game.overlayCamera.gameObject.activeSelf && player.basicNavigationStuff.activeSelf && Mathf.Abs(Mathf.DeltaAngle(player.horizontalPlaneRotation, 90)) < 1 && Mathf.Abs(player.CoopViewPitch - 23) < 1;
            Debug.Log("[CoopMonsterControl] " + (camera && toggle ? "CLIENT PASS" : "CLIENT FAIL") + "; enteredVanity=" + enteredVanity + "; lostZoom=" + lostDistance + "; overlay=" + game.overlayCamera.gameObject.activeSelf + "; navigation=" + player.basicNavigationStuff.activeSelf + "; toggle=" + toggle + "; yaw=" + player.horizontalPlaneRotation + "; pitch=" + player.CoopViewPitch);
            NetworkClient.Send(new CoopMonsterControlAck { camera = camera, toggle = toggle });
            CoopActions.ValidationKeySource = null; phase = -1;
        }
    }
    private void ReportRoster()
    {
        var game = GameSystem.instance;
        var player = game.player;
        toggle = player.currentAI.ObeyPlayerInput();
        bool overlay = game.overlayCamera.gameObject.activeSelf, navigation = player.basicNavigationStuff.activeSelf;
        bool look = Mathf.Abs(Mathf.DeltaAngle(player.horizontalPlaneRotation, 90)) < 1 && Mathf.Abs(player.CoopViewPitch - 23) < 1;
        // A post-transform monster normally has no local TF camera left to exit, so
        // camera entry is informational; only a stuck camera can fail the client.
        bool camera = overlay && navigation && look && !player.vanityCamera;
        Debug.Log("[CoopMonsterControl] " + (camera && toggle ? "CLIENT PASS" : "CLIENT FAIL") + "; species=" + player.npcType.name +
            "; enteredVanity=" + enteredVanity + "; lostZoom=" + lostDistance + "; overlay=" + overlay +
            "; navigation=" + navigation + "; toggle=" + toggle + "; obeys=" + toggle +
            "; yaw=" + player.horizontalPlaneRotation + "; pitch=" + player.CoopViewPitch + "; vanity=" + player.vanityCamera +
            "; distance=" + player.vanityCameraDistance + "; floatHeight=" + player.npcType.floatHeight + "; look=" + look +
            "; humanAuto=" + GameSystem.settings.autopilotHuman + "; activeAuto=" + GameSystem.settings.autopilotActive +
            "; passiveAuto=" + GameSystem.settings.autopilotPassive +
            (player.currentAI.currentState == null ? "" : "; state=" + player.currentAI.currentState.GetType().Name));
        NetworkClient.Send(new CoopMonsterControlAck { camera = camera, toggle = toggle });
        CoopActions.ValidationKeySource = null; phase = -1;
    }
    private static void TraceActor(string role, PlayerScript player)
    {
        var state = player.currentAI == null ? null : player.currentAI.currentState;
        Debug.Log("[CoopMonsterTrace] role=" + role + "; id=" + player.CoopPlayerId + "; remote=" + player.IsRemotePlayer +
            "; species=" + player.npcType.name + "; speed=" + player.npcType.movementSpeed + "; position=" + player.transform.position.ToString("F2") +
            "; water=" + (player.currentNode == null ? "none" : player.currentNode.associatedRoom.isWater.ToString()) +
            "; state=" + (state == null ? "none" : state.GetType().Name) + "; wantsVanity=" + (state != null && state.UseVanityCamera()) +
            "; vanity=" + player.vanityCamera + "; overlay=" + GameSystem.instance.overlayCamera.gameObject.activeSelf +
            "; navigation=" + player.basicNavigationStuff.activeSelf + "; activeAuto=" + GameSystem.settings.autopilotActive);
    }
    private void StartRoster()
    {
        finished = false; rosterIndex = 0; rosterFailures = 0;
        var unplayable = new System.Collections.Generic.HashSet<string>(NPCType.unplayableTypes.Where(t => t != null).Select(t => t.name));
        var planned = NPCType.startAsOptions.Select(t => t.name).Distinct().OrderBy(n => n, StringComparer.Ordinal)
            .Where(n => !unplayable.Contains(n)).Skip(RosterSkip).ToList();
        if (RosterTake > 0) planned = planned.Take(RosterTake).ToList();
        if (!string.IsNullOrEmpty(RosterOnly)) planned = planned.Where(n => string.Equals(n, RosterOnly, StringComparison.OrdinalIgnoreCase)).ToList();
        rosterSpecies.AddRange(planned);
        Debug.Log("[CoopMonsterRoster] species=" + rosterSpecies.Count + "; skip=" + RosterSkip + "; take=" + RosterTake + "; planned=" + string.Join(",", rosterSpecies));
        AdvanceRoster();
    }
    private void AdvanceRoster()
    {
        if (rosterIndex >= rosterSpecies.Count)
        {
            finished = true;
            Debug.Log("[CoopMonsterRoster] SWEEP COMPLETE; tested=" + rosterSpecies.Count + "; failures=" + rosterFailures +
                "; FAILED=" + string.Join(",", rosterSpecies.Where((s, i) => rosterResult[i] == "FAIL")));
            return;
        }
        rosterCurrent = rosterSpecies[rosterIndex];
        BeginPhase();
    }
    private void RecordRoster(bool passed, string detail)
    {
        rosterResult.Add(passed ? "PASS" : "FAIL");
        rosterDetail.Add(detail);
        if (!passed) rosterFailures++;
        Debug.Log("[CoopMonsterRoster] " + rosterIndex + "/" + rosterSpecies.Count + " species=" + rosterCurrent + " result=" +
            (passed ? "PASS" : "FAIL") + "; " + detail);
        rosterIndex++;
    }
    private void BeginPhase()
    {
        if (RosterMode && rosterIndex < rosterSpecies.Count) rosterCurrent = rosterSpecies[rosterIndex];
        began = Time.unscaledTime; acknowledgements = 0; clientsPassed = true; origins.Clear();
        GameSystem.settings.autopilotHuman = GameSystem.settings.autopilotActive = GameSystem.settings.autopilotPassive = true;
        var game = GameSystem.instance;
        var actors = NetworkManager.singleton.GetComponent<CoopPlayerRegistry>().Players.Where(p => p.IsRemotePlayer).ToList();
        if (actors.Count == 0) { Debug.LogWarning("[CoopMonsterControl] no remote actors for " + rosterCurrent); return; }
        var types = new[] { Pixie.npcType, Fairy.npcType, Centaur.npcType, Dog.npcType };
        NPCType species = RosterMode
            ? NPCType.GetDerivedType(NPCType.startAsOptions.First(t => t.name == rosterCurrent))
            : (phase > 0 ? NPCType.GetDerivedType(types[phase]) : null);
        // Respect the species' own room rule. Water-locked monsters such as Mermaid and
        // Rusalka (CanAccessRoom = a.isWater) are snapped back every frame in a dry room,
        // which looks exactly like "the player cannot move" but is original behaviour.
        var probe = actors[0];
        var node = game.map.rooms.Where(r => !r.locked && !r.isWater && (species == null || species.CanAccessRoom(r, probe)))
            .SelectMany(r => r.pathNodes).FirstOrDefault(n => n.hasArea && n.area.width >= 10 && n.area.height >= 10 && n.area.width <= 16 && n.area.height <= 16);
        if (node == null)
            node = game.map.rooms.Where(r => !r.locked && r.isWater).SelectMany(r => r.pathNodes)
                .First(n => n.hasArea && n.area.width >= 10 && n.area.height >= 10);
        var location = node.centrePoint;
        for (int i = 0; i < actors.Count; i++)
        {
            var actor = actors[i];
            // Roster phases drive one explicit species; original phases keep the
            // historical Pixie -> Fairy -> Centaur -> Dog progression.
            if (species != null) actor.UpdateToType(species, true, false);
            // Spread the actors: three characters stacked on one point push each other
            // out and can look immobile even with working input.
            actor.ForceRigidBodyPosition(node, node.RandomLocation(2.5f));
            origins[actor] = actor.transform.position;
        }
        remote = actors[0]; origin = origins[remote];
        int wire = RosterMode ? rosterIndex + 1 : phase;
        foreach (var connection in NetworkServer.connections.Values)
            if (connection != NetworkServer.localConnection && connection.isAuthenticated)
                connection.Send(new CoopMonsterControlCommand { phase = wire, species = remote.npcType.name });
    }

}
