using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

public struct CoopProbeReady : NetworkMessage { }

// Opt-in real-player regression scenario. It is inert in normal games and does not change assets/settings.
public sealed class CoopScenarioProbe : MonoBehaviour
{
    private float began = -1, nextCheck;
    private int stage;
    private bool readySent, itemSent;
    private int clientStep;
    private static bool Enabled => Environment.GetCommandLineArgs().Contains("-coopScenario");
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Enabled) new GameObject("Coop Runtime Scenario").AddComponent<CoopScenarioProbe>();
    }
    private void Start()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopHost"))
            NetworkServer.RegisterHandler<CoopProbeReady>((connection, message) => {
                if (began < 0) { began = Time.unscaledTime; Debug.Log("[CoopProbe] Ready from authenticated client"); }
            });
    }
    private void Update()
    {
        if (!Enabled || CoopReplication.Instance == null || GameSystem.instance == null) return;
        var game = GameSystem.instance;
        if (!NetworkServer.active)
        {
            if (CoopReplication.Instance.LocalPlayerId == 0) return;
            if (!readySent) { NetworkClient.Send(new CoopProbeReady()); readySent = true; began = Time.unscaledTime; }
            var flow = NetworkManager.singleton.GetComponent<CoopPlayerInputFlow>();
            flow.ValidationInputSource = () => new PlayerMovementInput { forward = Time.unscaledTime - began < 3f ? 0.5f : 0f, yaw = 90f };
            var actions = CoopActions.Instance;
            actions.ValidationInputSource = () => {
                float elapsed = Time.unscaledTime - began;
                int step = (int)(elapsed / 1.5f);
                uint down = 0, up = 0;
                if (step != clientStep) { clientStep = step; up = 1u << (int)HoPInput.PrimaryAction; down = 1u << (int)HoPInput.Dash; }
                return new CoopActionSample { down = down, up = up, yaw = 90f, pitch = 0f, quickItem = -1 };
            };
            if (!itemSent && Time.unscaledTime - began > 6f)
            {
                Item item = game.player.currentItems.FirstOrDefault(x => x.sourceItem == Items.HealthPotion);
                if (item != null && CoopInventory.Identity(item) > 0) { CoopInventory.Send(item, 1); itemSent = true; }
            }
            if (Time.unscaledTime >= nextCheck)
            {
                nextCheck = Time.unscaledTime + 2f;
                Debug.Log("[CoopProbe] Client view; hp=" + game.player.hp + "; will=" + game.player.will + "; armor=" + game.player.armor?.current +
                    "; type=" + game.player.npcType.name + "; timers=" + game.player.timers.Count + "; items=" + game.player.currentItems.Count + "; characters=" + game.activeCharacters.Count);
            }
            return;
        }
        if (began < 0 || Time.unscaledTime - began < stage * 4f) return;
        var registry = NetworkManager.singleton.GetComponent<CoopPlayerRegistry>();
        var arguments = Environment.GetCommandLineArgs();
        int targetIndex = Array.IndexOf(arguments, "-coopScenarioPlayer");
        int targetPlayer = targetIndex >= 0 && targetIndex + 1 < arguments.Length &&
            int.TryParse(arguments[targetIndex + 1], out int parsedTarget) ? parsedTarget : 0;
        PlayerScript actor = null;
        foreach (var connection in NetworkServer.connections.Values)
            if ((targetPlayer == 0 ? connection != NetworkServer.localConnection :
                NetworkManager.singleton.GetComponent<CoopSession>().GetPlayerId(connection) == targetPlayer) &&
                registry.TryGetPlayer(NetworkManager.singleton.GetComponent<CoopSession>().GetPlayerId(connection), out var candidate)) { actor = candidate; break; }
        if (actor == null) return;
        using (new CoopActorContext(actor))
        {
            switch (stage)
            {
                case 0:
                    actor.TakeDamage(13); actor.armor = new ArmorStatus(11, 20);
                    actor.GainItem(Items.HealthPotion.CreateInstance()); actor.GainItem(Weapons.Brick.CreateInstance());
                    actor.timers.Add(new Concentrate(actor, 10f, 2f));
                    Debug.Log("[CoopProbe] Stage 0: damage/armor/items/Concentrate"); break;
                case 1:
                    game.questionUI.ShowDisplay("Coop regression choice", () => Debug.Log("[CoopProbe] Cancel choice"), () => {
                        actor.ReceiveHealing(7); Debug.Log("[CoopProbe] Dialogue callback executed on host"); });
                    Debug.Log("[CoopProbe] Stage 1: owner dialogue"); break;
                case 2:
                    actor.UpdateToType(NPCType.GetDerivedType(Pixie.npcType), heal: true, initialisation: false);
                    if (Environment.GetCommandLineArgs().Contains("-coopRenderValidate"))
                    {
                        Texture first = actor.spriteStack[0].sprite;
                        Texture second = LoadedResourceManager.GetSprite(actor.usedImageSet + "/Human").texture;
                        actor.spriteStack.Add(new SpriteSettingDefinition(actor, RenderFunctions.FadeImages(first, second, 0.5f)));
                        var direct = new RenderTexture(first.width, first.height, 0, RenderTextureFormat.ARGB32);
                        Graphics.Blit(first, direct);
                        actor.spriteStack.Add(new SpriteSettingDefinition(actor, direct));
                        actor.RefreshSpriteDisplay();
                        Debug.Log("[CoopProbe] Generated texture regression: original FadeImages plus direct RenderTexture pixels");
                    }
                    Debug.Log("[CoopProbe] Stage 2: Pixie original UpdateToType"); break;
                case 3:
                    actor.UpdateToType(NPCType.GetDerivedType(Human.npcType), heal: true, initialisation: false);
                    game.alchemyUI.ShowDisplay();
                    Debug.Log("[CoopProbe] Stage 3: Human original UpdateToType"); break;
                case 4:
                    actor.TakeDamage(actor.hp + 1000);
                    actor.Die();
                    Debug.Log("[CoopProbe] Stage 4: original death"); break;
                case 5:
                    Debug.Log("[CoopProbe] Stage 5: after death/respawn; active=" + actor.gameObject.activeSelf + "; hp=" + actor.hp); break;
                case 6:
                    var node = actor.currentNode;
                    NPCScript fake = game.GetObject<NPCScript>();
                    Vector3 location = node.RandomLocation(0.5f);
                    fake.Initialise(location.x, location.z, NPCType.GetDerivedType(Fake.npcType), node);
                    if (fake.spriteStack.Any(image => image.sprite == null || image.sprite.width <= 1))
                        throw new InvalidOperationException("Original Fake standing-image fallback failed");
                    Debug.Log("[CoopProbe] Missing Fake Human frame falls back to original Fake image");
                    game.SpawnRandomEnemy(); Debug.Log("[CoopProbe] Stage 6: original NPC spawn"); break;
                case 7:
                    game.EndGame("Coop regression result"); Debug.Log("[CoopProbe] Stage 7: shared original result"); break;
            }
            stage++;
        }
        // Ending inside a remote actor's hidden UI scope must still present the
        // shared result on the host after that scope restores scene bindings.
        if (stage == 8)
        {
            if (!game.postGame || game.gameInProgress || !game.resultsDisplay.activeSelf ||
                game.resultsText.text.IndexOf("Coop regression result", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("Remote actor ended the round without presenting the host result");
            Debug.Log("[CoopResultTest] PASS: remote-triggered result is visible on host after actor scope restores");
        }
    }
}
