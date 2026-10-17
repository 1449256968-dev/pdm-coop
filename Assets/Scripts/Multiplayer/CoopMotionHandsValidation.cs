using System;
using System.Collections;
using System.IO;
using System.Linq;
using Mirror;
using UnityEngine;

public struct CoopMotionHandsPhase : NetworkMessage { public int phase; }
// Opt-in integration exercise: actual input messages, original host simulation,
// rendered client transforms/materials. No gameplay changes outside this flag.
[DefaultExecutionOrder(16000)]
public sealed class CoopMotionHandsValidation : MonoBehaviour
{
    public static bool Moving;
    private bool started;
    private int phase = -1, edges;
    private float began, maxHandOffset;
    private Vector3 handStart;
    private Texture initialTexture;
    private int textureChanges;
    private Vector3 previousPosition;
    private float travel;
    private bool captured;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopMotionHandsValidate"))
            new GameObject("Motion Hands Validation").AddComponent<CoopMotionHandsValidation>();
    }
    private void Start()
    {
        NetworkClient.RegisterHandler<CoopMotionHandsPhase>(m => { if (!NetworkServer.active) StartCoroutine(Client(m.phase)); });
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
        foreach (var npc in game.activeCharacters.OfType<NPCScript>()) npc.enabled = false;
        var actor = NetworkManager.singleton.GetComponent<CoopPlayerRegistry>().Players.First(p => p.IsRemotePlayer);
        var node = game.map.rooms.Where(r => !r.locked && !r.isWater).SelectMany(r => r.pathNodes)
            .First(n => n.hasArea && n.area.width >= 10 && n.area.height >= 10);
        using (new CoopActorContext(actor))
        { actor.UpdateToType(NPCType.GetDerivedType(Human.npcType), true, false); actor.ForceRigidBodyPosition(node, node.centrePoint); actor.weapon = null; actor.selectedItem = null; actor.UpdateStatus(); }
        for (int p = 0; p < 3; p++)
        {
            float cooldownBefore = actor.actionCooldown;
            using (new CoopActorContext(actor))
            {
                if (p == 1) { actor.GainItem(Weapons.Brick.CreateInstance()); actor.weapon = actor.currentItems.OfType<Weapon>().First(i => i.sourceItem == Weapons.Brick); actor.selectedItem = null; }
                if (p == 2) { actor.GainItem(Guns.Rifle.CreateInstance()); actor.weapon = null; actor.selectedItem = actor.currentItems.OfType<Gun>().First(i => i.sourceItem == Guns.Rifle); ((Gun)actor.selectedItem).ammo = 8; }
                actor.UpdateStatus();
            }
            yield return new WaitForSecondsRealtime(3);
            NetworkServer.SendToAll(new CoopMotionHandsPhase { phase = p });
            yield return new WaitForSecondsRealtime(p == 0 ? 18 : 7);
            bool passed = p == 2 ? ((Gun)actor.selectedItem).ammo < 8 : actor.actionCooldown > cooldownBefore;
            Debug.Log("[CoopMotionHands] HOST " + (passed ? "PASS" : "FAIL") + " authoritative attacks; phase=" + p);
            Debug.Log("[CoopMotionHands] HOST phase=" + p + "; cooldown=" + actor.actionCooldown + "; ammo=" + (actor.selectedItem is Gun gun ? gun.ammo : -1) + "; position=" + actor.transform.position);
        }
        Debug.Log("[CoopMotionHands] HOST DONE");
    }
    private IEnumerator Client(int p)
    {
        phase = p; began = Time.unscaledTime; edges = 0; maxHandOffset = 0; textureChanges = 0; travel = 0; captured = false;
        var game = GameSystem.instance; var player = game.player;
        CoopPlayerControl.Apply(0); player.horizontalPlaneRotation = 90;
        Debug.Log("[CoopMotionHands] visual state phase=" + p + "; overlayEnabled=" + game.overlayCamera.enabled + "; overlayActive=" + game.overlayCamera.gameObject.activeInHierarchy + "; manual=" + player.currentAI.ObeyPlayerInput() + "; vanity=" + player.vanityCamera + "; paused=" + game.pauseAllInteraction + "/" + game.pauseActionOnly);
        handStart = game.overlayCamera.equippedSpriteTransform.localPosition; initialTexture = player.equippedWeaponMeshRenderer.material.mainTexture;
        var flow = NetworkManager.singleton.GetComponent<CoopPlayerInputFlow>();
        var actions = CoopActions.Instance; var motion = NetworkManager.singleton.GetComponent<CoopMotion>();
        previousPosition = player.transform.position;
        motion.MeasuredFrames = motion.HeldMovingFrames = motion.SnapshotCorrections = 0; motion.SnapshotCorrectionDistance = motion.CameraYawError = 0;
        flow.ValidationInputSource = () => {
            float t = Time.unscaledTime - began; Moving = p == 0 && t < 10;
            return new PlayerMovementInput { yaw = 90, forward = Moving ? ((int)(t / 1) % 2 == 0 ? .5f : -.5f) : 0 };
        };
        actions.ValidationInputSource = () => {
            var sample = new CoopActionSample { quickItem = -1 };
            float t = Time.unscaledTime - began; float first = p == 0 ? 11 : 1;
            if (t >= first + edges * .5f && edges < 4) { sample.down = (edges % 2 == 0) ? (1u << (int)(p == 2 ? HoPInput.SecondaryAction : HoPInput.PrimaryAction)) : 0; sample.up = edges % 2 != 0 ? (1u << (int)(p == 2 ? HoPInput.SecondaryAction : HoPInput.PrimaryAction)) : 0; edges++; }
            return sample;
        };
        yield return new WaitForSecondsRealtime(p == 0 ? 15 : 4);
        Debug.Log("[CoopMotionHands] CLIENT phase=" + p + "; frames=" + motion.MeasuredFrames + "; heldMoving=" + motion.HeldMovingFrames + "; snapshotCorrections=" + motion.SnapshotCorrections + "; snapshotDistance=" + motion.SnapshotCorrectionDistance.ToString("F3") + "; meanCameraYawError=" + (motion.CameraYawError / Math.Max(1, motion.MeasuredFrames)).ToString("F3") + "; handExtent=" + maxHandOffset.ToString("F3") + "; textureFrames=" + textureChanges + "; edges=" + edges);
        bool passed = maxHandOffset > .05f && edges == 4 && (p != 2 || textureChanges > 0) && (p != 0 || travel > 1);
        Debug.Log("[CoopMotionHands] CLIENT " + (passed ? "PASS" : "FAIL") + " movement/hand presentation; phase=" + p + "; travel=" + travel.ToString("F3"));
        flow.ValidationInputSource = null; actions.ValidationInputSource = null; Moving = false; phase = -1;
        if (p == 2)
        {
            var hands = NetworkManager.singleton.GetComponent<CoopReplicaHands>(); long shown = hands.AttacksShown;
            game.SwapToAndFromMainGameUI(false); game.mainMenuUI.ShowDisplay();
            CoopReplicaHands.InputSample(new CoopActionSample { down = 1u << (int)HoPInput.PrimaryAction, up = 1u << (int)HoPInput.PrimaryAction });
            game.mainMenuUI.ResumeGame();
            yield return new WaitForSecondsRealtime(.5f);
            Vector3 resting = game.overlayCamera.equippedSpriteTransform.localPosition;
            yield return new WaitForSecondsRealtime(.5f);
            bool menu = shown == hands.AttacksShown && game.mainGameUI.activeSelf && Vector3.Distance(resting, game.overlayCamera.equippedSpriteTransform.localPosition) < .005f;
            Debug.Log("[CoopMotionHands] CLIENT " + (menu ? "PASS" : "FAIL") + " menu suppression and animation settles");
        }
    }
    private void LateUpdate()
    {
        if (phase < 0 || NetworkServer.active) return;
        var p = GameSystem.instance.player;
        float distance = Vector3.Distance(previousPosition, p.transform.position); previousPosition = p.transform.position;
        if (distance < 4) travel += distance;
        if (Time.unscaledTime - began >= (phase == 0 ? 11 : 1))
        {
            maxHandOffset = Mathf.Max(maxHandOffset, Vector3.Distance(handStart, GameSystem.instance.overlayCamera.equippedSpriteTransform.localPosition));
            if (p.equippedWeaponMeshRenderer.material.mainTexture != initialTexture) textureChanges++;
            if (!captured && textureChanges > 0) { captured = true; Capture(phase); }
        }
    }
    internal static void Capture(int phase)
    {
        var active = RenderTexture.active;
        var target = new RenderTexture(960, 640, 24);
        foreach (var camera in Camera.allCameras.Where(c => c.enabled && c.targetTexture == null).OrderBy(c => c.depth))
        {
            camera.targetTexture = target;
            try { camera.Render(); } finally { camera.targetTexture = null; }
        }
        RenderTexture.active = target;
        var image = new Texture2D(960, 640, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 960, 640), 0, 0); image.Apply();
        var args = Environment.GetCommandLineArgs(); int log = Array.IndexOf(args, "-logFile");
        File.WriteAllBytes(args[log + 1] + ".hands" + phase + ".png", image.EncodeToPNG());
        RenderTexture.active = active; target.Release(); Destroy(target); Destroy(image);
    }
}
