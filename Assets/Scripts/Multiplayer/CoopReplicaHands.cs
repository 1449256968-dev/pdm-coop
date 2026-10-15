using System.Linq;
using UnityEngine;

// Client presentation only. Never calls UseItem, attacks, skills or simulation
// coroutines. Original host code remains the sole authority for their results.
[DefaultExecutionOrder(12000)]
public sealed class CoopReplicaHands : MonoBehaviour
{
    private static CoopReplicaHands instance;
    private PlayerScript actor;
    private NPCType form;
    private long itemId;
    private bool charging, secondaryHeld, texturesApplied;
    private float chargeStart, swingStart = -10, recoilStart = -10, nextAuto, secondaryStart;
    private float frameStart, frameDuration;
    private string[] frames;
    private Item animatedItem;
    public long AttacksShown { get; private set; }
    private void Awake() { instance = this; }
    private static bool Bit(uint bits, HoPInput key) => (bits & (1u << (int)key)) != 0;
    private bool Available(PlayerScript p)
    {
        var g = GameSystem.instance;
        return CoopReplication.IsReplica && p != null && !p.IsRemotePlayer && p.gameObject.activeInHierarchy &&
            p.npcType != null && p.currentAI != null && p.currentAI.ObeyPlayerInput() && !p.vanityCamera &&
            g.gameInProgress && !g.pauseAllInteraction && !g.pauseActionOnly && g.mainGameUI.activeSelf && g.inputWatcher.inputWatchers.Count == 0;
    }
    public static void InputSample(CoopActionSample sample)
    {
        if (instance != null) instance.InputLocal(sample);
    }
    private void InputLocal(CoopActionSample sample)
    {
        var p = GameSystem.instance.player;
        if (!Available(p)) { Clear(); return; }
        Bind(p);
        bool dualGun = p.weapon == null && p.selectedItem is Gun gun && gun.DualGun;
        if (Bit(sample.down, HoPInput.PrimaryAction) && !dualGun) { charging = true; chargeStart = Time.unscaledTime; }
        if (Bit(sample.up, HoPInput.PrimaryAction))
        {
            charging = false;
            if (p.actionCooldown <= GameSystem.instance.totalGameTime)
            {
                if (dualGun) Fire(p.selectedItem as Gun);
                else { swingStart = Time.unscaledTime; AttacksShown++; Animate(p.weapon, p.weapon?.attackImage, .1f); }
            }
        }
        if (Bit(sample.down, HoPInput.SecondaryAction))
        { secondaryHeld = true; secondaryStart = Time.unscaledTime; nextAuto = secondaryStart + .3f; }
        if (Bit(sample.up, HoPInput.SecondaryAction))
        {
            if (!secondaryHeld || Time.unscaledTime - secondaryStart < .3f || !(p.selectedItem is Gun g) || !g.fullAuto)
                Fire(p.weapon == null ? p.selectedItem as Gun : null);
            secondaryHeld = false;
        }
        if (Bit(sample.up, HoPInput.UseItemOnOther) || Bit(sample.up, HoPInput.TertiaryAction) || sample.skillUp || Bit(sample.up, HoPInput.ActivateWeaponAbility))
        { recoilStart = Time.unscaledTime; Animate(p.weapon, p.weapon?.usingImage, .1f); }
    }
    private void Bind(PlayerScript p)
    {
        long id = CoopInventory.Identity(p.weapon != null ? (Item)p.weapon : p.selectedItem);
        if (actor != p || form != p.npcType || itemId != id) { Clear(); actor = p; form = p.npcType; itemId = id; }
    }
    private void Fire(Gun gun)
    {
        recoilStart = Time.unscaledTime; AttacksShown++;
        if (gun != null) Animate(gun, gun.ammo > 0 ? gun.usingImage : gun.GunReloadImage, gun.ammo > 0 ? .05f : .1f);
    }
    private void Animate(Item item, string images, float duration)
    {
        if (item == null || string.IsNullOrWhiteSpace(images)) return;
        animatedItem = item; frames = images.Split(',').Select(f => f.Trim()).Where(f => f.Length != 0).ToArray();
        frameStart = Time.unscaledTime; frameDuration = duration;
    }
    private void Clear()
    {
        charging = secondaryHeld = false; swingStart = recoilStart = -10; frames = null; animatedItem = null;
        if (texturesApplied && GameSystem.instance != null && actor != null && actor.gameObject.activeInHierarchy) actor.UpdateStatus();
        texturesApplied = false; actor = null; form = null; itemId = 0;
    }
    private void LateUpdate()
    {
        var game = GameSystem.instance;
        if (game != null && CoopReplication.IsReplica)
        {
            RefreshBase(game.overlayCamera); RefreshBase(game.itemOverlay);
        }
        if (game == null || !Available(game.player)) { Clear(); return; }
        Bind(game.player);
        if (secondaryHeld && actor.weapon == null && actor.selectedItem is Gun gun && gun.fullAuto && gun.ammo > 0 && Time.unscaledTime >= nextAuto)
        { nextAuto = Time.unscaledTime + Mathf.Max(.05f, gun.fireRate); Fire(gun); }
        Vector3 offset = Vector3.zero, rotation = Vector3.zero;
        if (charging)
        {
            float t = Mathf.Clamp01((Time.unscaledTime - chargeStart) / .18f);
            offset += new Vector3(.2f, .7f, 0) * t; rotation.z -= 15 * t;
        }
        float swing = 1 - Mathf.Clamp01((Time.unscaledTime - swingStart) / .28f);
        float recoil = 1 - Mathf.Clamp01((Time.unscaledTime - recoilStart) / .24f);
        offset += new Vector3(-.25f, -.3f, .35f) * swing + new Vector3(-.2f, .3f, -.15f) * recoil;
        rotation.z += 35 * swing;
        // OverlayCamera writes the base pose every Update. Apply a transient
        // offset afterward; it cannot accumulate or survive a menu/respawn.
        ApplyOffset(game.overlayCamera, offset, rotation);
        ApplyOffset(game.itemOverlay, new Vector3(-offset.x, offset.y * .6f, offset.z), -rotation * .6f);
        if (frames != null)
        {
            int index = (int)((Time.unscaledTime - frameStart) / frameDuration);
            if (index >= frames.Length) { frames = null; if (texturesApplied) actor.UpdateStatus(); texturesApplied = false; }
            else if (CoopInventory.Identity(animatedItem) == itemId)
            {
                Sprite sprite = null;
                if (ModificationWeapons.IsModWeapon(animatedItem.name))
                {
                    string folder = ModificationWeapons.GetWeaponModFolder(animatedItem.name);
                    if (!string.IsNullOrEmpty(folder)) sprite = LoadedResourceManager.GetCustomSprite(folder + "/Images/" + frames[index]);
                }
                if (sprite == null) sprite = LoadedResourceManager.GetSprite("Items/" + frames[index]);
                if (sprite != null)
                {
                    SetTexture(actor.equippedWeaponMeshRenderer, sprite.texture);
                    if (actor.weapon != null && actor.weapon.DualWeapon) SetTexture(actor.equippedItemMeshRenderer, sprite.texture);
                    texturesApplied = true;
                }
            }
        }
    }
    private static void ApplyOffset(OverlayCamera overlay, Vector3 position, Vector3 rotation)
    {
        if (overlay == null || !overlay.gameObject.activeInHierarchy || overlay.equippedSpriteTransform == null) return;
        overlay.equippedSpriteTransform.localPosition += position;
        overlay.equippedSpriteTransform.localRotation *= Quaternion.Euler(rotation);
    }
    private static void RefreshBase(OverlayCamera overlay)
    {
        if (overlay != null && !overlay.enabled && overlay.gameObject.activeInHierarchy) overlay.CoopUpdatePresentation();
    }
    private static void SetTexture(Renderer renderer, Texture texture)
    {
        if (renderer == null || renderer.material.mainTexture == texture) return;
        if (renderer.material.mainTexture is RenderTexture previous) Destroy(previous);
        renderer.material.mainTexture = texture;
    }
    private void OnDisable() { Clear(); if (instance == this) instance = null; }
}
