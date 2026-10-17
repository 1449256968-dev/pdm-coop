using System;
using System.Collections;
using System.Linq;
using Mirror;
using UnityEngine;

public struct CoopReportedBugPhase : NetworkMessage { public int phase; }

// Opt-in two-peer reproduction of short clicks and the moving UFO after tube TF.
[DefaultExecutionOrder(17000)]
public sealed class CoopReportedBugsValidation : MonoBehaviour
{
    private bool started;
    private int phase = -1, edges, visibleArcs;
    private float began, travel, maxStep;
    private Vector3 previous, origin;
    private long shown, arcs;
    private bool captured;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopReportedBugsValidate"))
            new GameObject("Reported Bugs Validation").AddComponent<CoopReportedBugsValidation>();
    }
    private void Start()
    {
        NetworkClient.RegisterHandler<CoopReportedBugPhase>(m => { if (!NetworkServer.active) StartCoroutine(Client(m.phase)); });
    }
    private void Update()
    {
        if (started || !NetworkServer.active || GameSystem.instance == null || !GameSystem.instance.gameInProgress ||
            NetworkManager.singleton.GetComponent<CoopPlayerRegistry>().Count < 2) return;
        started = true; StartCoroutine(Host());
    }
    private IEnumerator Host()
    {
        var game = GameSystem.instance;
        // Isolate tube conversion from nearby humans breaking the tube to rescue
        // its occupant. This fixture measures correctness, never performance.
        foreach (var npc in game.activeCharacters.OfType<NPCScript>()) npc.enabled = false;
        var actor = NetworkManager.singleton.GetComponent<CoopPlayerRegistry>().Players.First(p => p.IsRemotePlayer);
        using (new CoopActorContext(actor))
        {
            actor.UpdateToType(NPCType.GetDerivedType(Human.npcType), true, false);
            actor.currentAI.UpdateState(new WanderState(actor.currentAI));
            actor.weapon = null; actor.selectedItem = null; actor.UpdateStatus();
        }
        yield return new WaitForSecondsRealtime(4);
        float cooldown = actor.actionCooldown;
        NetworkServer.SendToAll(new CoopReportedBugPhase { phase = 0 });
        yield return new WaitForSecondsRealtime(13);
        Debug.Log("[CoopReported] HOST taps executed=" + (actor.actionCooldown > cooldown));
        if (game.ufoRoom == null) throw new InvalidOperationException("UFO missing from test world");
        var tube = game.ufoRoom.alienTubes.First();
        using (new CoopActorContext(actor))
        {
            actor.ForceRigidBodyPosition(tube.containingNode, tube.directTransformReference.position);
            tube.currentOccupant = actor;
            actor.currentAI.UpdateState(new AlienTransformState(actor.currentAI, tube, true));
        }
        float deadline = Time.unscaledTime + 90;
        while (!actor.npcType.SameAncestor(Alien.npcType) && Time.unscaledTime < deadline) yield return null;
        if (!actor.npcType.SameAncestor(Alien.npcType)) throw new InvalidOperationException("Original alien tube transformation timed out");
        Debug.Log("[CoopReported] HOST original Alien tube transformation completed");
        yield return new WaitForSecondsRealtime(3);
        // Different host/client preferences reproduce the extra unscoped UFO AI
        // tick taking manual control away from the newly converted remote player.
        bool hostAutopilot = GameSystem.settings.autopilotActive;
        GameSystem.settings.autopilotActive = true;
        NetworkServer.SendToAll(new CoopReportedBugPhase { phase = 1 });
        yield return new WaitForSecondsRealtime(14);
        GameSystem.settings.autopilotActive = hostAutopilot;
        Debug.Log("[CoopReported] HOST DONE; room=" + actor.currentNode.associatedRoom.name);
    }
    private IEnumerator Client(int next)
    {
        phase = next; edges = visibleArcs = 0; began = Time.unscaledTime; travel = maxStep = 0;
        var player = GameSystem.instance.player;
        CoopPlayerControl.Apply(0);
        var hands = NetworkManager.singleton.GetComponent<CoopReplicaHands>();
        var effects = NetworkManager.singleton.GetComponent<CoopAttackEffects>();
        arcs = effects.ArcsShown;
        shown = hands.AttacksShown; origin = previous = player.transform.position;
        CoopActions.Instance.ValidationInputSource = () => {
            var sample = new CoopActionSample { quickItem = -1 };
            if (phase == 0 && edges < 6 && Time.unscaledTime - began >= 1 + edges * 1.7f)
            {
                // A complete click inside one 30Hz action packet.
                sample.down = sample.up = 1u << (int)HoPInput.PrimaryAction; edges++;
            }
            return sample;
        };
        var flow = NetworkManager.singleton.GetComponent<CoopPlayerInputFlow>();
        flow.ValidationInputSource = () => new PlayerMovementInput {
            yaw = player.horizontalPlaneRotation,
            forward = phase == 1 && Time.unscaledTime - began >= 4 && Time.unscaledTime - began < 5 ? .5f : 0
        };
        yield return new WaitForSecondsRealtime(12);
        Debug.Log("[CoopReported] CLIENT phase=" + phase + "; clicks=" + edges + "; shown=" + (hands.AttacksShown - shown) +
            "; arcs=" + (effects.ArcsShown - arcs) + "; visibleArcFrames=" + visibleArcs + "; travel=" + travel.ToString("F3") + "; maxStep=" + maxStep.ToString("F3") +
            "; net=" + Vector3.Distance(origin, player.transform.position).ToString("F3") +
            "; species=" + player.npcType.name + "; room=" + player.currentNode.associatedRoom.name +
            "; prediction=" + CoopPrediction.Owns(player));
        phase = -1; flow.ValidationInputSource = null; CoopActions.Instance.ValidationInputSource = null;
    }
    private void LateUpdate()
    {
        if (phase < 0 || NetworkServer.active) return;
        var position = GameSystem.instance.player.transform.position;
        float step = Vector3.Distance(previous, position); previous = position;
        travel += step; maxStep = Mathf.Max(maxStep, step);
        if (FindObjectsOfType<AttackArc>().Any(a => a.lineRenderer.enabled && a.lineRenderer.colorGradient.alphaKeys.Any(k => k.alpha > .05f)))
        {
            visibleArcs++;
            if (!captured && phase == 0)
            {
                captured = true;
                CoopMotionHandsValidation.Capture(90);
            }
        }
    }
}
