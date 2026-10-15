using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

public struct CoopActionSample : NetworkMessage
{
    public uint sequence;
    public uint down, up, held;
    public float yaw, pitch;
    public bool skillDown, skillUp;
    public int quickItem;
    public byte controlPreferences;
}

public sealed class CoopActions : MonoBehaviour
{
    public static CoopActions Instance { get; private set; }
    private readonly Dictionary<int, CoopActionSample> inputs = new Dictionary<int, CoopActionSample>();
    private readonly Dictionary<int, float> received = new Dictionary<int, float>();
    private readonly Dictionary<int, uint> sequences = new Dictionary<int, uint>();
    private CoopActionSample local;
    private float nextSend;
    private uint sequence;
    private CoopActionSample consuming;
    private PlayerScript consumer;
    public Func<CoopActionSample> ValidationInputSource { get; set; }
    public static Func<HoPInput, int, bool?> ValidationKeySource { get; set; }
    private const uint Allowed = (1u << (int)HoPInput.Dash) | (1u << (int)HoPInput.PrimaryAction) |
        (1u << (int)HoPInput.SecondaryAction) | (1u << (int)HoPInput.TertiaryAction) |
        (1u << (int)HoPInput.StepTransformation) | (1u << (int)HoPInput.UseItemOnOther) |
        (1u << (int)HoPInput.OrderAI) | (1u << (int)HoPInput.SwapItemPrevious) |
        (1u << (int)HoPInput.SwapItemNext) | (1u << (int)HoPInput.Surrender) |
        (1u << (int)HoPInput.CycleDefaultAI) | (1u << (int)HoPInput.ActivateWeaponAbility) |
        (1u << (int)HoPInput.SwitchWeapon);
    private void Awake() { Instance = this; local.quickItem = -1; }
    public void StartServer() { NetworkServer.RegisterHandler<CoopActionSample>(Receive); inputs.Clear(); received.Clear(); sequences.Clear(); CoopPlayerControl.Reset(); }
    public void StopServer() { NetworkServer.UnregisterHandler<CoopActionSample>(); inputs.Clear(); received.Clear(); sequences.Clear(); CoopPlayerControl.Reset(); }
    public void Release(int id) { inputs.Remove(id); received.Remove(id); sequences.Remove(id); CoopPlayerControl.Release(id); }
    private void Update()
    {
        if (!CoopReplication.IsReplica || GameSystem.instance == null || GameSystem.settings == null) return;
        var game = GameSystem.instance;
        bool blocked = game.inputWatcher.inputWatchers.Count > 0 || !game.mainGameUI.activeSelf;
        local.held = 0;
        if (!blocked)
        {
            foreach (HoPInput key in Enum.GetValues(typeof(HoPInput)))
            {
                uint bit = 1u << (int)key;
                if ((Allowed & bit) == 0 || !GameSystem.settings.keySettingsLists.TryGetValue(key, out var bindings)) continue;
                if (bindings.Any(b => b.CheckForKeyDown())) local.down |= bit;
                if (bindings.Any(b => b.CheckForKeyUp())) local.up |= bit;
                if (bindings.Any(b => b.CheckForKeyHeld())) local.held |= bit;
            }
            local.skillDown |= Input.GetKeyDown(KeyCode.R); local.skillUp |= Input.GetKeyUp(KeyCode.R);
            for (int i = 0; i < 10; i++) if (Input.GetKeyDown(KeyCode.Alpha0 + i)) local.quickItem = GameSystem.settings.quickKeys[i];
        }
        if (Time.unscaledTime < nextSend || !NetworkClient.isConnected || !NetworkClient.connection.isAuthenticated) return;
        nextSend = nextSend <= 0 || Time.unscaledTime - nextSend > 2f / 30f ? Time.unscaledTime + 1f / 30f : nextSend + 1f / 30f;
        if (ValidationInputSource != null) local = ValidationInputSource();
        CoopReplicaHands.InputSample(local);
        local.controlPreferences = CoopPlayerControl.Capture();
        local.sequence = ++sequence;
        local.yaw = game.player.horizontalPlaneRotation;
        local.pitch = game.player.rotationTransformReference.eulerAngles.x;
        if (local.pitch > 180) local.pitch -= 360;
        NetworkClient.Send(local);
        local.down = local.up = 0; local.skillDown = local.skillUp = false; local.quickItem = -1;
    }
    private void Receive(NetworkConnectionToClient connection, CoopActionSample sample)
    {
        var game = GameSystem.instance;
        int id = GetComponent<CoopSession>().GetPlayerId(connection);
        if (id == 0 || !GetComponent<CoopWorld>().SharedWorldRunning || game == null || !game.gameInProgress) return;
        if (float.IsNaN(sample.yaw) || float.IsInfinity(sample.yaw) || float.IsNaN(sample.pitch) || float.IsInfinity(sample.pitch)) return;
        if (sequences.TryGetValue(id, out uint prior) && sample.sequence <= prior) return;
        // Network callbacks can drain several packets at the same frame time.
        // Merge their edges below; a frame-clock rate limit would lose key presses.
        if ((sample.controlPreferences & ~15) != 0) return;
        CoopPlayerControl.Receive(id, sample.controlPreferences);
        sample.down &= Allowed; sample.up &= Allowed; sample.held &= Allowed;
        sample.pitch = Mathf.Clamp(sample.pitch, -80, 80); sample.yaw = Mathf.Repeat(sample.yaw, 360);
        if (sample.quickItem < -1 || sample.quickItem >= ItemData.GameItems.Count) sample.quickItem = -1;
        if (inputs.TryGetValue(id, out var old)) { sample.down |= old.down; sample.up |= old.up; sample.skillDown |= old.skillDown; sample.skillUp |= old.skillUp; }
        inputs[id] = sample; received[id] = Time.unscaledTime; sequences[id] = sample.sequence;
    }
    public void RunOriginal(PlayerScript player, System.Action run)
    {
        if (CoopDialogue.HasPending(player.CoopPlayerId) || CoopUiBridge.HasSession(player.CoopPlayerId)) return;
        // A missing or expired sample is not neutral input: a default struct carries
        // yaw 0 / pitch 0 and would snap the remote actor's facing to world north on
        // connect, during a client stall, or when the action channel drops a packet.
        // Absent input must not overwrite an authoritative pose at all.
        bool fresh = NetworkServer.active && received.TryGetValue(player.CoopPlayerId, out float time) &&
            Time.unscaledTime - time <= 0.25f;
        consuming = fresh ? inputs[player.CoopPlayerId] : default(CoopActionSample);
        consuming.quickItem = fresh ? consuming.quickItem : -1;
        consumer = player;
        try
        {
            using (new CoopActorContext(player))
            {
                if (fresh)
                {
                    if (!player.facingLocked && (player.currentAI == null || player.currentAI.ObeyPlayerInput() ||
                        player.currentAI.currentState.immobilisedState || !GameSystem.settings.autopilotAutoFacing))
                        player.horizontalPlaneRotation = consuming.yaw;
                    player.CoopViewPitch = consuming.pitch;
                    player.rotationTransformReference.rotation = Quaternion.Euler(consuming.pitch, player.horizontalPlaneRotation, 0);
                    if (consuming.quickItem >= 0)
                    {
                        var item = player.currentItems.FirstOrDefault(x => x.sourceItem == ItemData.GameItems[consuming.quickItem]);
                        if (item != null) { GameSystem.instance.inventoryUI.displayedItem = item; GameSystem.instance.inventoryUI.SelectItem(); }
                    }
                }
                run();
            }
        }
        finally
        {
            if (inputs.TryGetValue(player.CoopPlayerId, out var input))
            { input.down = input.up = 0; input.skillDown = input.skillUp = false; input.quickItem = -1; inputs[player.CoopPlayerId] = input; }
            consumer = null;
        }
    }
    public static bool Key(PlayerScript actor, HoPInput key, int phase)
    {
        if (!actor.IsRemotePlayer && ValidationKeySource != null) { bool? test = ValidationKeySource(key, phase); if (test.HasValue) return test.Value; }
        if (!actor.IsRemotePlayer) return GameSystem.settings.keySettingsLists[key].Any(b => phase == 0 ? b.CheckForKeyDown() : phase == 1 ? b.CheckForKeyUp() : b.CheckForKeyHeld());
        if (Instance == null || Instance.consumer != actor) return false;
        uint bits = phase == 0 ? Instance.consuming.down : phase == 1 ? Instance.consuming.up : Instance.consuming.held;
        return (bits & (1u << (int)key)) != 0;
    }
    public static bool RawKey(PlayerScript actor, KeyCode key, int phase)
    {
        if (!actor.IsRemotePlayer) return phase == 0 ? Input.GetKeyDown(key) : phase == 1 ? Input.GetKeyUp(key) : Input.GetKey(key);
        if (Instance == null || Instance.consumer != actor) return false;
        if (key == KeyCode.R) return phase == 0 ? Instance.consuming.skillDown : Instance.consuming.skillUp;
        return false;
    }
    public static float Axis(PlayerScript actor, string axis) => actor.IsRemotePlayer ? 0f : Input.GetAxis(axis);
    public static Ray Aim(PlayerScript actor)
    {
        if (!actor.IsRemotePlayer) return GameSystem.instance.mainCamera.ScreenPointToRay(new Vector3(Screen.width / 2, Screen.height / 2));
        return new Ray(actor.rotationTransformReference.position, actor.rotationTransformReference.forward);
    }
    // Client view and menus are local; all actions/state changes are sent above.
    private static int pauseMenuShownFrame = -1;
    public static void PauseMenuShown() { pauseMenuShownFrame = Time.frameCount; }
    public static void PauseMenuEscape()
    {
        var game = GameSystem.instance;
        // A sub-menu may have consumed this same Escape while returning to the
        // pause menu. Never immediately close/reopen it in another Update order.
        if (pauseMenuShownFrame == Time.frameCount || game.inputWatcher.inputWatchers.Count != 0) return;
        if (game.mainMenuUI.gameObject.activeSelf) game.mainMenuUI.ResumeGame();
        else if (game.mainGameUI.activeSelf)
        {
            game.SwapToAndFromMainGameUI(false);
            game.mainMenuUI.ShowDisplay();
        }
    }
    public static void ReplicaView(PlayerScript player)
    {
        if (player.IsRemotePlayer || player.currentAI == null || player.npcType == null) return;
        var game = GameSystem.instance;
        if (Input.GetKeyDown(KeyCode.Escape)) PauseMenuEscape();
        // The autopilot toggle is evaluated once, before the menu guard. A bridged
        // trading/merchant/alchemy screen (or any other open menu) must never leave the
        // player with no way back to manual control.
        if (Key(player, HoPInput.ToggleAutopilot, 1)) CoopPlayerControl.Toggle(player);
        if (game.inputWatcher.inputWatchers.Count == 0 && game.mainGameUI.activeSelf)
        {
            if (Key(player, HoPInput.OpenInventory, 1)) game.inventoryUI.ShowDisplay();
            if (Key(player, HoPInput.OpenMap, 1)) game.mapUI.ShowDisplay();
            player.UpdateVanityCamera();
            player.CoopUpdateView();
        }
        else
        {
            // An open menu owns the view, but the camera state must still converge.
            player.UpdateVanityCamera();
        }
    }
}
