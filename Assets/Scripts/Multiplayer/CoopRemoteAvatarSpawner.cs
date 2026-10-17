using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

/// <summary>
/// Host-side world entry for the co-op session.
/// <list type="bullet">
/// <item>With <c>-coopStartGame</c> the host starts the shared game without menu input, which is what
/// makes the whole world phase scriptable for validation.</item>
/// <item>Creates one remote avatar for every authenticated joining player and binds it to that
/// player's validated network input through <see cref="CoopPlayerRegistry"/>.</item>
/// </list>
/// The avatar is a real <see cref="PlayerScript"/> clone so the original player-faction mechanics keep
/// applying; local-only behaviour (local input, camera, HUD) is suppressed by <c>PlayerScript.IsRemotePlayer</c>.
/// </summary>
public sealed class CoopRemoteAvatarSpawner : MonoBehaviour
{
    private const float PollInterval = 0.5f;

    private readonly Dictionary<int, PlayerScript> spawned = new Dictionary<int, PlayerScript>();
    private readonly Dictionary<int, Vector3> lastLoggedPosition = new Dictionary<int, Vector3>();
    private float nextPoll;
    private float nextMovementLog;
    private bool gameStartAttempted;

    /// <summary>Set from <c>-coopStartGame</c>: start the shared game once the room is up.</summary>
    public bool AutoStartGame { get; set; }

    private void LateUpdate()
    {
        if (!NetworkServer.active) return;
        var game = GameSystem.instance;
        if (game == null || GameSystem.settings == null) return;

        if (AutoStartGame && !gameStartAttempted && !game.gameInProgress)
        {
            gameStartAttempted = true;
            if (CoopPerformanceProbe.Enabled) { GameSystem.settings.mapSeed = "73141"; UnityEngine.Random.InitState(73141); }
            // Pin a concrete seed first: an empty seed makes each process roll its own map.
            if (string.IsNullOrEmpty(GameSystem.settings.mapSeed))
                GameSystem.settings.mapSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue).ToString();
            TryStartGame(game);
            var world = GetComponent<CoopWorld>();
            if (world != null && game.gameInProgress)
                world.PublishHostWorld(GameSystem.settings.mapSeed, isTutorial: false);
        }

        if (Time.unscaledTime < nextPoll) return;
        nextPoll = Time.unscaledTime + PollInterval;
        // Host-only: changing the quality level in the options menu re-applies that level's
        // VSync setting, which would put the host back on the 60/30 cliff. Re-assert the host's
        // unsynced 60 cap (see CoopNetworkManager.OnStartServer); clients are never touched.
        if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
        if (Application.targetFrameRate <= 0 || Application.targetFrameRate > 60) Application.targetFrameRate = 60;
        if ((!game.gameInProgress && !game.postGame) || game.map == null || game.player == null) return;
        var session = GetComponent<CoopSession>();
        var registry = GetComponent<CoopPlayerRegistry>();
        int hostId = session.GetPlayerId(NetworkServer.localConnection);
        if (hostId != 0 && registry.GetPlayerId(game.player) != hostId)
        {
            game.player.CoopPlayerId = hostId;
            registry.Register(hostId, game.player, remote: false);
        }
        var sharedWorld = GetComponent<CoopWorld>();
        if (!sharedWorld.SharedWorldRunning) sharedWorld.PublishHostWorld(game.map.seed.ToString(), game.isTutorial);
        SpawnMissingAvatars(game);
        if (Time.unscaledTime >= nextMovementLog)
        {
            nextMovementLog = Time.unscaledTime + 1f;
            LogRemoteMovement();
        }
    }

    // Movement trace for validation; only logs when a remote avatar actually moved.
    private void LogRemoteMovement()
    {
        var registry = GetComponent<CoopPlayerRegistry>();
        foreach (var pair in spawned)
        {
            var script = pair.Value;
            if (script == null) continue;
            PlayerScript registered;
            if (registry != null && !registry.TryGetPlayer(pair.Key, out registered)) continue;
            Vector3 position = script.transform.position;
            Vector3 last;
            if (lastLoggedPosition.TryGetValue(pair.Key, out last) &&
                (position - last).sqrMagnitude < 0.01f)
                continue;
            lastLoggedPosition[pair.Key] = position;
            Debug.Log("[Coop] Remote player " + pair.Key + " moved to " +
                      position.x.ToString("0.00") + "," + position.y.ToString("0.00") + "," +
                      position.z.ToString("0.00"));
        }
    }

    // Mirrors MainMenuUI.StartNewGame's defaults: a random enabled human image set and Human species.
    private static void TryStartGame(GameSystem game)
    {
        try
        {
            NPCType.GenerateFullTypeList();
            var humans = new List<string>(NPCType.humans);
            string imageSet = humans.Count > 0 ? ExtendRandom.Random(humans) : "";
            Debug.Log("[Coop] Auto-starting shared game; imageSet=" + imageSet +
                      "; seed=" + GameSystem.settings.mapSeed);
            NPCType type = CoopPlayerSelection.ResolveSpecies(CoopPlayerSelection.Argument("-coopSpecies") ?? "Human");
            game.StartGame(imageSet, imageSet, type, isObserver: false, isTutorial: false,
                GameSystem.settings.mapSeed);
            game.SwapToAndFromMainGameUI(swapTo: true);
            game.mainMenuUI.CloseDisclaimer();
            game.mainMenuUI.gameObject.SetActive(false);
            Debug.Log("[Coop] Shared game started; inProgress=" + game.gameInProgress);
        }
        catch (System.Exception exception)
        {
            Debug.LogError("[Coop] Auto-start failed: " + exception);
        }
    }

    private void SpawnMissingAvatars(GameSystem game)
    {
        var session = GetComponent<CoopSession>();
        var registry = GetComponent<CoopPlayerRegistry>();
        if (session == null || registry == null) return;
        foreach (var connection in NetworkServer.connections.Values)
        {
            if (connection == null || !connection.isAuthenticated) continue;
            // The host plays the character already wired into the scene.
            if (connection == NetworkServer.localConnection) continue;
            int playerId = session.GetPlayerId(connection);
            if (playerId == 0) continue;
            if (!GetComponent<CoopPlayerSelection>().TryGet(connection, out var choice)) continue;
            PlayerScript existing;
            if (registry.TryGetPlayer(playerId, out existing)) continue;
            SpawnAvatar(game, registry, playerId, choice);
        }
    }

    private void SpawnAvatar(GameSystem game, CoopPlayerRegistry registry, int playerId, CoopPlayerSelectionMessage choice)
    {
        var source = game.player;
        if (source == null) return;
        var sourceTextures = System.Environment.GetCommandLineArgs().Contains("-coopPlayerIntegrationValidate")
            ? source.spriteStack.Select(s => s.sprite).OfType<RenderTexture>().Where(t => t.IsCreated()).ToArray() : null;

        PathNode node = source.currentNode;
        if (node == null && game.map != null)
        {
            var room = game.map.rooms.FirstOrDefault();
            if (room != null) node = room.RandomNode();
        }
        if (node == null) return;

        var avatar = Instantiate(source.gameObject, source.transform.position, source.transform.rotation);
        avatar.name = "Coop Remote Player " + playerId;
        StripLocalCameraAndListener(avatar);
        var script = avatar.GetComponent<PlayerScript>();
        if (script == null)
        {
            Destroy(avatar);
            return;
        }

        script.IsRemotePlayer = true;
        script.CoopPlayerId = playerId;
        // Instantiate copies the host's sprite entries but shares their Unity textures.
        // Initialise/UpdateToType disposes the old RenderTextures, so inherited entries
        // would destroy the host's live non-human appearance while spawning a joiner.
        script.spriteStack = new List<SpriteSettingDefinition>();
        CoopFirstPersonVisibility.Refresh(script);
        CoopActorContext.Attach(source, script);

        Vector3 location = node.RandomLocation(0.5f);
        // Rejoining during results still needs an owner record so snapshots can carry
        // the result/menu state. Keep this placeholder out of the finished world;
        // the next world will spawn the player's selected species normally.
        bool dormant = choice.observer || game.postGame;
        NPCType initialType = dormant ? Human.npcType : CoopPlayerSelection.ResolveSpecies(choice.species);
        using (new CoopActorContext(script))
        {
            if (!dormant) initialType.PreSpawnSetup(initialType);
            script.Initialise(location.x, location.z, initialType, node, "Player " + playerId, choice.imageSet, "");
            if (!dormant) (script.npcType.PostSpawnSetup ?? initialType.PostSpawnSetup)?.Invoke(script);
        }
        if (dormant)
        {
            script.currentNode.RemoveNPC(script);
            game.activeCharacters.Remove(script);
            script.gameObject.SetActive(false);
        }
        registry.Register(playerId, script, remote: true);
        if (sourceTextures != null)
        {
            if (sourceTextures.Any(t => t == null || !t.IsCreated()))
                throw new System.InvalidOperationException("Spawning a player disposed the host's live appearance");
            Debug.Log("[CoopPlayerTextures] PASS; hostRenderTextures=" + sourceTextures.Length);
        }
        spawned[playerId] = script;
        Debug.Log("[Coop] Remote avatar spawned for player " + playerId + " at " +
                  location.x.ToString("0.00") + "," + location.z.ToString("0.00") +
                  "; type=" + script.npcType.name + "; observer=" + choice.observer);
    }

    public PlayerScript CreateReplica(int playerId)
    {
        var source = GameSystem.instance.player;
        var avatar = Instantiate(source.gameObject, source.transform.position, source.transform.rotation);
        avatar.name = "Coop Replica Player " + playerId;
        StripLocalCameraAndListener(avatar);
        var script = avatar.GetComponent<PlayerScript>();
        script.IsRemotePlayer = true;
        script.CoopPlayerId = playerId;
        CoopFirstPersonVisibility.Refresh(script);
        ClearLocalHudReferences(script);
        return script;
    }

    // A remote avatar must not render a second view or listen twice.
    private static void StripLocalCameraAndListener(GameObject avatar)
    {
        foreach (var camera in avatar.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        foreach (var listener in avatar.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
    }

    // Every one of these points at the single local "Main UI" root.
    private static void ClearLocalHudReferences(PlayerScript script)
    {
        script.healthBarTransform = null;
        script.focusBarTransform = null;
        script.staminaBarTransform = null;
        script.incapacitationBarTransform = null;
        script.cooldownBarTransform = null;
        script.playerViewSpriteTransform = null;
        script.logTransform = null;
        script.statusSectionTransform = null;
        script.characterDetailsSectionTransform = null;
        script.minimapTransform = null;
        script.characterImageRectTransform = null;
        script.armorBarTransform = null;
        script.armorBarBackingTransform = null;
        script.colourFlash = null;
        script.staminaBarImage = null;
        script.playerCrosshair = null;
        script.colourOverlay = null;
        script.hpBarImage = null;
        script.minimapBacking = null;
        script.armorBarImage = null;
        script.characterImage = null;
        script.minimapRaw = null;
        script.hpText = null;
        script.focusText = null;
        script.itemsText = null;
        script.staminaText = null;
        script.incapacitationText = null;
        script.objectiveText = null;
        script.armorText = null;
        script.incapacitationSection = null;
        script.cooldownBar = null;
        script.hideOnTransformStuff = null;
        script.basicNavigationStuff = null;
        script.minimap = null;
        script.timerImage.Clear();
        script.timerText.Clear();
        script.usableItems.Clear();
    }
}
