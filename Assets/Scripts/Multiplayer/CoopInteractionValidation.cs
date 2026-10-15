using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mirror;
using UnityEngine;

public struct CoopInteractionReady : NetworkMessage { }
public struct CoopInteractionCommand : NetworkMessage { public int phase; }
public struct CoopInteractionAck : NetworkMessage { public int phase; public bool passed; }

// Opt-in only. Real client UI callbacks use the normal authenticated message paths.
public sealed class CoopInteractionValidation : MonoBehaviour
{
    private bool ready, sent, started, ack, clientPassed, phasePassed;
    private int phase, failures;
    private PlayerScript actor;
    private NPCScript partner, merchant;
    private bool dialogueDone;
    private string enteredText;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopInteractionValidate"))
            new GameObject("Coop Interaction Validation").AddComponent<CoopInteractionValidation>();
    }
    private void Start()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopHost"))
        {
            NetworkServer.RegisterHandler<CoopInteractionReady>((c, m) => ready = true);
            NetworkServer.RegisterHandler<CoopInteractionAck>((c, m) => { if (m.phase == phase) { ack = true; clientPassed = m.passed; } });
        }
        else NetworkClient.RegisterHandler<CoopInteractionCommand>(m => StartCoroutine(ClientPhase(m.phase)));
    }
    private void Update()
    {
        var game = GameSystem.instance;
        if (game == null || !game.gameInProgress || NetworkManager.singleton == null) return;
        if (NetworkServer.active)
        {
            if (!started && ready && NetworkManager.singleton.GetComponent<CoopPlayerRegistry>().Count == 2)
            { started = true; StartCoroutine(Host()); }
        }
        else if (!sent && CoopReplication.Instance.LocalPlayerId != 0)
        { sent = true; CoopPlayerControl.Apply(0); NetworkClient.Send(new CoopInteractionReady()); }
    }
    private static IEnumerator Wait(Func<bool> predicate, float seconds = 4f)
    {
        float until = Time.unscaledTime + seconds;
        while (!predicate() && Time.unscaledTime < until) yield return null;
    }
    private void Check(bool ok, string detail)
    {
        phasePassed &= ok;
        if (!ok) Debug.LogWarning("[CoopInteraction] assertion FAIL; phase=" + phase + "; " + detail);
    }
    private static bool Has(CharacterStatus c, Item definition) => c.currentItems.Any(i => i.sourceItem == definition);
    private static Item Find(CharacterStatus c, Item definition) => c.currentItems.FirstOrDefault(i => i.sourceItem == definition);
    private static bool UiFree() => GameSystem.instance.mainGameUI.activeSelf && GameSystem.instance.uisBeforeMainUI == 0 && GameSystem.instance.inputWatcher.inputWatchers.Count == 0;
    private IEnumerator Host()
    {
        var game = GameSystem.instance;
        byte preferences = CoopPlayerControl.Capture(); float trust = game.trust;
        var npcStates = game.activeCharacters.OfType<NPCScript>().ToDictionary(n => n, n => n.enabled);
        foreach (var npc in npcStates.Keys) npc.enabled = false;
        actor = NetworkManager.singleton.GetComponent<CoopPlayerRegistry>().Players.First(p => p.IsRemotePlayer);
        var node = game.map.rooms.Where(r => !r.locked && !r.isWater).SelectMany(r => r.pathNodes)
            .First(n => n.hasArea && n.area.width >= 10 && n.area.height >= 10);
        using (new CoopActorContext(actor))
        {
            actor.UpdateToType(NPCType.GetDerivedType(Human.npcType), true, false);
            actor.ForceRigidBodyPosition(node, node.centrePoint);
            foreach (var item in actor.currentItems.ToArray()) actor.LoseItem(item);
            actor.GainItem(Weapons.Brick.CreateInstance()); actor.GainItem(Items.HealthPotion.CreateInstance()); actor.GainItem(Items.WillPotion.CreateInstance());
            actor.weapon = null; actor.selectedItem = null; actor.UpdateStatus();
        }
        partner = game.GetObject<NPCScript>();
        partner.Initialise(node.centrePoint.x, node.centrePoint.z + 3, NPCType.GetDerivedType(Human.npcType), node);
        partner.enabled = false;
        foreach (var item in partner.currentItems.ToArray()) partner.LoseItem(item);
        partner.GainItem(Items.WillPotion.CreateInstance());
        merchant = game.GetObject<NPCScript>();
        merchant.Initialise(node.centrePoint.x + 3, node.centrePoint.z + 3, NPCType.GetDerivedType(Aurumite.npcType), node);
        merchant.enabled = false;
        var stock = ((AurumiteMetadata)merchant.typeMetadata).stock; stock.Clear(); stock.Add(Items.HealthPotion.CreateInstance());
        game.trust = 1000;
        // Deliberately differ from the client's manual setting: this catches authority
        // checks accidentally reading the host's autopilot preferences.
        CoopPlayerControl.Apply(15);
        yield return new WaitForSecondsRealtime(2f); // Entity IDs and items must reach a snapshot before UI references.
        for (phase = 0; phase <= 10; phase++)
        {
            ack = false; clientPassed = false; phasePassed = true; dialogueDone = false;
            using (new CoopActorContext(actor))
            {
                if (phase == 1) { actor.selectedItem = null; actor.UpdateStatus(); }
                if (phase == 4)
                {
                    foreach (var item in actor.currentItems.Where(i => i.sourceItem == Items.HealthPotion || i.sourceItem == Items.WillPotion || i.sourceItem == Items.Rose).ToArray()) actor.LoseItem(item);
                    actor.GainItem(Items.Rose.CreateInstance()); actor.GainItem(Items.WillPotion.CreateInstance());
                    game.alchemyUI.ShowDisplay();
                }
                if (phase == 5) { if (!Has(actor, Items.HealthPotion)) actor.GainItem(Items.HealthPotion.CreateInstance()); game.tradingUI.ShowDisplay(partner); }
                if (phase == 7)
                {
                    if (!Has(actor, Weapons.Brick)) actor.GainItem(Weapons.Brick.CreateInstance());
                    actor.GainItem(Weapons.Longsword.CreateInstance());
                    game.merchantUI.ShowDisplay(actor, merchant);
                }
                if (phase == 8) game.questionUI.ShowDisplay("Interaction regression choice", () => { }, () => dialogueDone = true);
                if (phase == 9) game.textInputUI.ShowDisplay("Interaction regression text", () => { }, text => { enteredText = text; dialogueDone = true; });
            }
            foreach (var connection in NetworkServer.connections.Values)
                if (connection != NetworkServer.localConnection && connection.isAuthenticated) connection.Send(new CoopInteractionCommand { phase = phase });
            yield return Wait(() => ack, 30f);
            Check(ack && clientPassed, "client report");
            switch (phase)
            {
                case 0: Check(actor.weapon != null && actor.weapon.sourceItem == Weapons.Brick, "host equipped brick"); break;
                case 1: Check(actor.selectedItem != null && actor.selectedItem.sourceItem == Items.HealthPotion, "host selected potion"); break;
                case 2: Check(!Has(actor, Items.HealthPotion), "host dropped potion"); break;
                case 3: Check(!Has(actor, Items.WillPotion), "host destroyed potion"); break;
                case 4: Check(Has(actor, Items.HealthPotion) && !Has(actor, Items.Rose) && !CoopUiBridge.HasSession(actor.CoopPlayerId), "alchemy result and close"); break;
                case 5: Check(!Has(actor, Items.HealthPotion) && Has(partner, Items.HealthPotion), "trade give"); break;
                case 6: Check(Has(actor, Items.WillPotion) && !Has(partner, Items.WillPotion) && !CoopUiBridge.HasSession(actor.CoopPlayerId), "trade take and close"); break;
                case 7: Check(Has(actor, Items.HealthPotion) && !Has(actor, Weapons.Longsword) && !CoopUiBridge.HasSession(actor.CoopPlayerId), "merchant trade and close"); break;
                case 8: Check(dialogueDone && !CoopDialogue.HasPending(actor.CoopPlayerId), "choice callback released"); break;
                case 9: Check(dialogueDone && enteredText == "Guest 测试" && !CoopDialogue.HasPending(actor.CoopPlayerId), "text callback released"); break;
            }
            Check(CoopPlayerControl.Capture() == 15, "host personal controls isolated");
            if (!phasePassed) failures++;
            Debug.Log("[CoopInteraction] HOST " + (phasePassed ? "PASS" : "FAIL") + "; phase=" + phase);
        }
        foreach (var pair in npcStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
        CoopPlayerControl.Apply(preferences); game.trust = trust;
        Debug.Log("[CoopInteraction] COMPLETE; phases=11; failures=" + failures);
    }
    private IEnumerator ClientPhase(int value)
    {
        phase = value; phasePassed = true;
        var game = GameSystem.instance; var player = game.player;
        var inventory = game.inventoryUI;
        CoopPlayerControl.Apply(0);
        yield return new WaitForSecondsRealtime(.5f);
        if (phase <= 3)
        {
            if (!inventory.gameObject.activeSelf) inventory.ShowDisplay();
            var definition = phase == 0 ? (Item)Weapons.Brick : phase == 3 ? Items.WillPotion : Items.HealthPotion;
            yield return Wait(() => Find(player, definition) != null);
            var item = Find(player, definition);
            Check(item != null && CoopInventory.Identity(item) > 0, "inventory identity available");
            inventory.PressedItem(item);
            Check(inventory.itemDisplayImage.sprite != null && inventory.itemDisplayImage.sprite.texture.width > 1, "inventory item texture");
            if (phase <= 1) inventory.SelectItem(); else if (phase == 2) inventory.Drop(); else inventory.Destroy();
            yield return Wait(() => phase == 0 ? player.weapon != null && player.weapon.sourceItem == definition :
                phase == 1 ? player.selectedItem != null && player.selectedItem.sourceItem == definition : !Has(player, definition));
            Check(phase == 0 ? player.weapon != null && player.weapon.sourceItem == definition :
                phase == 1 ? player.selectedItem != null && player.selectedItem.sourceItem == definition : !Has(player, definition), "inventory action reflected in authoritative snapshot");
            inventory.Back(); Check(UiFree(), "inventory close restores HUD/input");
        }
        else if (phase == 4)
        {
            var ui = game.alchemyUI;
            yield return Wait(() => ui.gameObject.activeSelf && ui.optionsList.Any(i => i.sourceItem == Items.Rose) && ui.optionsList.Any(i => i.sourceItem == Items.WillPotion));
            var rose = ui.optionsList.FirstOrDefault(i => i.sourceItem == Items.Rose);
            ui.PressedItemOption(rose);
            yield return Wait(() => ui.ingredientsList.Count == 1);
            Check(ui.ingredientsList.Count == 1, "alchemy choose ingredient");
            ui.PressedItemIngredient(ui.ingredientsList.FirstOrDefault());
            yield return Wait(() => ui.ingredientsList.Count == 0);
            ui.ApplyQuickRecipe(AlchemyUI.alchemyRecipes.First(r => r.name == "Convert To Health Potion"));
            yield return Wait(() => ui.ingredientsList.Count == 2);
            Check(ui.ingredientsList.Count == 2, "alchemy quick recipe");
            ui.Mix();
            yield return Wait(() => !ui.gameObject.activeSelf && Has(player, Items.HealthPotion));
            Check(Has(player, Items.HealthPotion) && !Has(player, Items.Rose) && UiFree(), "alchemy result, icon and close");
        }
        else if (phase == 5 || phase == 6)
        {
            var ui = game.tradingUI;
            yield return Wait(() => ui.gameObject.activeSelf && ui.target != null);
            if (phase == 5)
            {
                yield return Wait(() => Has(player, Items.HealthPotion));
                ui.PlayerPressedItem(Find(player, Items.HealthPotion));
                yield return Wait(() => ui.playerDisplayedItem != null);
                Check(ui.playerDisplayedItem != null, "trade select player's item");
                Check(ui.playerInventoryButtons.Any(b => b.shownItem != null && b.shownItem.sourceItem == Items.HealthPotion && b.backdrop.color == Color.gray), "trade selected item highlight");
                ui.GiveItem();
                yield return Wait(() => !Has(player, Items.HealthPotion) && Has(ui.target, Items.HealthPotion));
                Check(!Has(player, Items.HealthPotion) && Has(ui.target, Items.HealthPotion), "trade give snapshot");
                yield return new WaitForSecondsRealtime(.3f);
                Check(!ui.playerInventoryButtons.Any(b => b.shownItem != null && b.shownItem.sourceItem == Items.HealthPotion), "no stale icon after giving");
            }
            else
            {
                ui.TargetPressedItem(Find(ui.target, Items.WillPotion));
                yield return Wait(() => ui.targetDisplayedItem != null);
                Check(ui.targetDisplayedItem != null, "trade select target's item");
                ui.TakeItem();
                yield return Wait(() => Has(player, Items.WillPotion) && !Has(ui.target, Items.WillPotion));
                Check(Has(player, Items.WillPotion) && !Has(ui.target, Items.WillPotion), "trade take snapshot");
                yield return new WaitForSecondsRealtime(.3f);
                Check(!ui.targetInventoryButtons.Any(b => b.shownItem != null && b.shownItem.sourceItem == Items.WillPotion), "no stale icon after taking");
                ui.Back(); yield return Wait(UiFree); Check(UiFree(), "trade close restores control");
            }
        }
        else if (phase == 7)
        {
            var ui = game.merchantUI;
            yield return Wait(() => ui.gameObject.activeSelf && ui.shopperItems.Any(i => i.sourceItem == Weapons.Longsword));
            ui.ShopperPressedItem(ui.shopperItems.FirstOrDefault(i => i.sourceItem == Weapons.Longsword));
            yield return Wait(() => ui.offeredItems.Count == 1);
            // Merchant grids intentionally do not show equipment badges in the
            // original game; their visual contract is offered/requested highlighting.
            Check(ui.shopperInventoryButtons.Any(b => b.shownItem != null && b.shownItem.sourceItem == Weapons.Longsword && b.backdrop.color == Color.gray), "merchant offered item highlight");
            ui.ShopperPressedItem(ui.shopperItems.FirstOrDefault(i => i.sourceItem == Weapons.Longsword));
            yield return Wait(() => ui.offeredItems.Count == 0);
            Check(ui.offeredItems.Count == 0, "merchant deselect offer");
            ui.ShopperPressedItem(ui.shopperItems.FirstOrDefault(i => i.sourceItem == Weapons.Longsword));
            yield return Wait(() => ui.offeredItems.Count == 1);
            ui.ShopperPressedItem(ui.shopperItems.FirstOrDefault(i => i.sourceItem == Weapons.Brick));
            ui.MerchantPressedItem(ui.merchantItems.FirstOrDefault(i => i.sourceItem == Items.HealthPotion));
            yield return Wait(() => ui.offeredItems.Count == 2 && ui.requestedItems.Count == 1);
            Check(ui.offeredItems.Count == 2 && ui.requestedItems.Count == 1 && ui.CalculateDealChance() == 1f, "merchant deterministic valid offer");
            ui.Trade(); yield return Wait(() => !ui.gameObject.activeSelf && !Has(player, Weapons.Longsword));
            Check(Has(player, Items.HealthPotion) && !Has(player, Weapons.Longsword) && UiFree(), "merchant result and close");
        }
        else if (phase == 8)
        {
            yield return Wait(() => game.multiOptionUI.gameObject.activeSelf);
            game.multiOptionUI.ClickButton(game.multiOptionUI.buttons[1]);
            yield return null; Check(UiFree(), "choice dialog releases input");
        }
        else if (phase == 9)
        {
            yield return Wait(() => game.textInputUI.gameObject.activeSelf);
            game.textInputUI.textInput.text = "Guest 测试"; game.textInputUI.Confirm();
            yield return null; Check(UiFree(), "text dialog releases input");
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                inventory.ShowDisplay(); inventory.NextPage(); inventory.PreviousPage(); inventory.Back();
                game.mapUI.ShowDisplay(); game.mapUI.Back(); Check(UiFree(), "inventory/map cycle " + i);
            }
            var billboards = NetworkManager.singleton.GetComponent<CoopBillboards>();
            var npc = CoopReplication.Instance.VisibleReplicas<NPCScript>().Cast<NPCScript>().First(n => !n.facingLocked);
            for (int i = 0; i < 4; i++)
            {
                Vector3 eye = npc.transform.position + Quaternion.Euler(0, i * 90, 0) * Vector3.forward * 3;
                billboards.FaceView(eye);
                Vector3 forward = npc.transform.position - eye; forward.y = 0;
                Check(Vector3.Dot(npc.directTransformReference.forward, forward.normalized) > .99f, "NPC billboard from angle " + i);
                foreach (PlayerScript remote in CoopReplication.Instance.VisibleReplicas<PlayerScript>().Cast<PlayerScript>().Where(p => p.IsRemotePlayer))
                {
                    Vector3 direction = remote.transform.position - eye; direction.y = 0;
                    Vector3 normal = remote.playerGameSpriteTransform.forward; normal.y = 0;
                    Check(Vector3.Dot(normal.normalized, direction.normalized) > .99f, "remote body billboard from angle " + i);
                }
            }
            billboards.FaceView(game.activePrimaryCameraTransform.position);
            CaptureWorld();
            Vector3 position = player.transform.position;
            var flow = NetworkManager.singleton.GetComponent<CoopPlayerInputFlow>();
            flow.ValidationInputSource = () => new PlayerMovementInput { forward = 1, yaw = 90 };
            yield return new WaitForSecondsRealtime(2f); flow.ValidationInputSource = null;
            Check(Vector3.ProjectOnPlane(player.transform.position - position, Vector3.up).magnitude > .1f, "movement after all interactions");
        }
        Debug.Log("[CoopInteraction] CLIENT " + (phasePassed ? "PASS" : "FAIL") + "; phase=" + phase);
        NetworkClient.Send(new CoopInteractionAck { phase = phase, passed = phasePassed });
    }
    private static void CaptureWorld()
    {
        var game = GameSystem.instance; var camera = game.mainCamera;
        var oldTarget = camera.targetTexture; var oldActive = RenderTexture.active;
        var target = new RenderTexture(960, 640, 24); camera.targetTexture = target;
        camera.Render(); RenderTexture.active = target;
        var image = new Texture2D(960, 640, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 960, 640), 0, 0); image.Apply();
        var args = Environment.GetCommandLineArgs(); int log = Array.IndexOf(args, "-logFile");
        string path = args[log + 1] + ".world.png"; File.WriteAllBytes(path, image.EncodeToPNG());
        Debug.Log("[CoopInteraction] Actual camera render: " + path);
        camera.targetTexture = oldTarget; RenderTexture.active = oldActive;
        target.Release(); Destroy(target); Destroy(image);
    }
}
