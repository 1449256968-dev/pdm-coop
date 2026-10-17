using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>The static world the host decided to play. Dynamic entity state is replicated separately.</summary>
public struct CoopWorldStartMessage : NetworkMessage
{
    public string mapSeed;
    public bool isTutorial;
    public string topology;
    public uint generation;
    public string bootstrapSpecies;
}

/// <summary>
/// Host-authoritative world bootstrap. The host picks a concrete seed, starts the shared world and
/// publishes that seed to every authenticated client; a client applies it through the dedicated
/// shared-world path (<c>GameSystem.allowSharedWorldStart</c>) instead of gaining general permission
/// to start its own game.
///
/// The seed is what makes both processes generate the same static map topology. It deliberately does
/// NOT govern later random behaviour, so dynamic entities/spawns still have to be replicated.
/// </summary>
public sealed class CoopWorld : MonoBehaviour
{
    private readonly HashSet<int> publishedTo = new HashSet<int>();
    private float nextPublishCheck;

    public bool ReplicationClient { get; private set; }
    public bool SharedWorldRunning { get; private set; }
    public string SharedSeed { get; private set; }
    public uint Generation { get; private set; }
    private string sharedTopology;
    private bool sharedTutorial;
    private string bootstrapSpecies = "Human";

    private CoopPlayerInputFlow InputFlow => GetComponent<CoopPlayerInputFlow>();

    public void StartServer()
    {
        ReplicationClient = false;
        SharedWorldRunning = false;
        SharedSeed = null;
        Generation = 0;
    }

    public void StopServer()
    {
        SharedWorldRunning = false;
        SharedSeed = null;
    }

    public void StartClient()
    {
        NetworkClient.RegisterHandler<CoopWorldStartMessage>(OnWorldStart);
        ReplicationClient = true;
    }

    public void StopClient()
    {
        NetworkClient.UnregisterHandler<CoopWorldStartMessage>();
        ReplicationClient = false;
        SharedWorldRunning = false;
        SharedSeed = null;
        Generation = 0;
        if (InputFlow != null) InputFlow.Active = false;
    }

    /// <summary>Host: start the shared world once and publish the exact seed to everyone.</summary>
    public bool StartHostWorld(string mapSeed, bool isTutorial)
    {
        if (!NetworkServer.active || SharedWorldRunning) return false;
        var game = GameSystem.instance;
        if (game == null || GameSystem.settings == null) return false;
        if (string.IsNullOrEmpty(mapSeed))
            mapSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue).ToString();
        if (!StartWorld(game, mapSeed, isTutorial)) return false;
        SharedWorldRunning = true;
        SharedSeed = mapSeed;
        Generation++;
        sharedTopology = CoopTopology.Capture(game.map);
        sharedTutorial = isTutorial;
        Broadcast();
        return true;
    }

    /// <summary>
    /// Host: the world was already started by another path (e.g. the auto-start spawner); record its
    /// seed and publish it without starting anything again.
    /// </summary>
    public void PublishHostWorld(string mapSeed, bool isTutorial)
    {
        if (!NetworkServer.active) return;
        SharedWorldRunning = true;
        SharedSeed = mapSeed;
        Generation++;
        sharedTopology = CoopTopology.Capture(GameSystem.instance.map);
        sharedTutorial = isTutorial;
        Broadcast();
    }

    /// <summary>Host: hand the current world description to a late joiner.</summary>
    public void SendTo(NetworkConnectionToClient connection)
    {
        if (!NetworkServer.active || !SharedWorldRunning || connection == null ||
            !connection.isAuthenticated) return;
        if (connection == NetworkServer.localConnection) return;
        publishedTo.Add(connection.connectionId);
        connection.Send(new CoopWorldStartMessage { mapSeed = SharedSeed, isTutorial = sharedTutorial, topology = sharedTopology, generation = Generation, bootstrapSpecies = bootstrapSpecies });
    }

    private void Broadcast()
    {
        publishedTo.Clear();
        foreach (var connection in NetworkServer.connections.Values)
        {
            if (connection == null || !connection.isAuthenticated) continue;
            if (connection == NetworkServer.localConnection) continue;
            publishedTo.Add(connection.connectionId);
            connection.Send(new CoopWorldStartMessage { mapSeed = SharedSeed, isTutorial = sharedTutorial, topology = sharedTopology, generation = Generation, bootstrapSpecies = bootstrapSpecies });
        }
        Debug.Log("[Coop] Shared world published; seed=" + SharedSeed);
    }

    // A client may authenticate before or after the host starts the world, so late joiners are covered here.
    private void LateUpdate()
    {
        if (!NetworkServer.active || !SharedWorldRunning) return;
        if (Time.unscaledTime < nextPublishCheck) return;
        nextPublishCheck = Time.unscaledTime + 0.5f;
        if (publishedTo.Count > 0)
        {
            var stale = new List<int>();
            foreach (int connectionId in publishedTo)
                if (!NetworkServer.connections.ContainsKey(connectionId)) stale.Add(connectionId);
            foreach (int connectionId in stale) publishedTo.Remove(connectionId);
        }
        foreach (var connection in NetworkServer.connections.Values)
        {
            if (connection == null || !connection.isAuthenticated) continue;
            if (connection == NetworkServer.localConnection) continue;
            if (!publishedTo.Add(connection.connectionId)) continue;
            connection.Send(new CoopWorldStartMessage { mapSeed = SharedSeed, isTutorial = sharedTutorial, topology = sharedTopology, generation = Generation, bootstrapSpecies = bootstrapSpecies });
            Debug.Log("[Coop] Shared world sent to connectionId=" + connection.connectionId);
        }
    }

    private void OnWorldStart(CoopWorldStartMessage message)
    {
        if (NetworkServer.active || message.generation <= Generation)
        {
            Debug.Log("[Coop] Shared world already running; ignoring seed=" + message.mapSeed);
            return;
        }
        var game = GameSystem.instance;
        if (game == null) return;
        if (SharedWorldRunning) GetComponent<CoopReplication>().ResetClientWorld();
        SharedWorldRunning = false;
        Debug.Log("[Coop] Shared world received; seed=" + message.mapSeed);
        if (!StartWorld(game, message.mapSeed, message.isTutorial, message.bootstrapSpecies)) return;
        CoopTopology.Validate(game.map, message.topology);
        SharedWorldRunning = true;
        SharedSeed = message.mapSeed;
        Generation = message.generation;
        if (InputFlow != null) InputFlow.Active = true;
        Debug.Log("[Coop] Shared world built on this client; seed=" + SharedSeed +
                  "; inputActive=" + (InputFlow != null && InputFlow.Active));
    }
    public static void BeforeHostStart(NPCType type)
    {
        if (!NetworkServer.active || NetworkManager.singleton == null) return;
        var world = NetworkManager.singleton.GetComponent<CoopWorld>();
        world.bootstrapSpecies = type.name;
        world.SharedWorldRunning = false;
        world.publishedTo.Clear();
        NetworkManager.singleton.GetComponent<CoopPlayerRegistry>().Clear();
        NetworkManager.singleton.GetComponent<CoopReplication>().ResetHostWorld();
    }

    // Rebuild the host's initial species-dependent geometry, then snapshots bind each client's own actor.
    private static bool StartWorld(GameSystem game, string mapSeed, bool isTutorial, string species = "Human")
    {
        try
        {
            NPCType.GenerateFullTypeList();
            // completeSideGroupingAffiliations is DERIVED data: the game itself rebuilds it after
            // cloning a setup (Settings.CreateNewDiplomacySetup). Do the same so a snapshot-restored
            // client is consistent instead of holding an empty affiliation dictionary.
            if (GameSystem.settings.diplomacySetups != null)
                foreach (var setup in GameSystem.settings.diplomacySetups)
                    if (setup != null) setup.GenerateFullAffiliationAndSideList();
            var humans = new List<string>(NPCType.humans);
            string imageSet = humans.Count > 0 ? ExtendRandom.Random(humans) : "";
            bool previous = game.AllowSharedWorldStart;
            game.AllowSharedWorldStart = true;
            try
            {
                NPCType type = CoopPlayerSelection.ResolveSpecies(species) ?? throw new System.InvalidOperationException("Unknown bootstrap species: " + species);
                game.StartGame(imageSet, imageSet, type, isObserver: false,
                    isTutorial: isTutorial, mapSeed);
            }
            finally
            {
                game.AllowSharedWorldStart = previous;
            }
            game.SwapToAndFromMainGameUI(swapTo: true);
            game.mainMenuUI.CloseDisclaimer();
            game.mainMenuUI.gameObject.SetActive(false);
            Debug.Log("[Coop] World built; inProgress=" + game.gameInProgress + "; seed=" + mapSeed +
                      "; playerAt=" + game.player.transform.position +
                      "; rooms=" + (game.map != null ? game.map.rooms.Count : -1));
            return game.gameInProgress;
        }
        catch (System.Exception exception)
        {
            Debug.LogError("[Coop] World build failed: " + exception);
            return false;
        }
    }
}
