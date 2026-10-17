using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Mirror;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Unity.Profiling;

public struct CoopStateChunk : NetworkMessage
{
    public uint revision;
    public int owner;
    public int index;
    public int count;
    public byte[] data;
    public uint world;
}
public struct CoopRecipeChunk : NetworkMessage
{
    public uint world;
    public string key;
    public int index, count;
    public byte[] data;
}

// All simulation remains in the original game on the host. Replicas only apply fields
// and refresh presentation. A complete bounded snapshot also covers late join/despawn.
public sealed class CoopReplication : MonoBehaviour
{
    public static CoopReplication Instance { get; private set; }
    public static bool IsReplica => Instance != null && !NetworkServer.active && NetworkClient.active &&
        Instance.GetComponent<CoopWorld>().SharedWorldRunning;
    private readonly Dictionary<Component, long> hostIds = new Dictionary<Component, long>();
    private readonly Dictionary<long, Component> replicas = new Dictionary<long, Component>();
    private readonly HashSet<Component> createdReplicas = new HashSet<Component>();
    private readonly Dictionary<MonoBehaviour, bool> originalEnabled = new Dictionary<MonoBehaviour, bool>();
    private readonly Dictionary<Rigidbody, bool> originalKinematic = new Dictionary<Rigidbody, bool>();
    private readonly Dictionary<Timer, string> timerDisplays = new Dictionary<Timer, string>();
    private readonly Dictionary<Component, string> anchors = new Dictionary<Component, string>();
    private readonly Dictionary<string, Component> anchorObjects = new Dictionary<string, Component>();
    private uint revision, receivedRevision, appliedRevision;
    private byte[][] pending;
    private int pendingOwner;
    private int pendingBytes;
    private byte[] latestSnapshot;
    private int latestOwner;
    private uint latestRevision;
    private sealed class EncodedSnapshot { public byte[] bytes; public uint world; public int entities; public double milliseconds; public Dictionary<string, byte[]> recipes; public HashSet<int> owners; }
    private sealed class DecodedSnapshot { public JObject root; public uint world, revision; public int owner; public double milliseconds; public CoopCachedStateGraph.Frame nodes; }
    private CoopCachedStateGraph captureGraph, applyGraph;
    private Task<EncodedSnapshot> encoding;
    private Task<DecodedSnapshot> decoding;
    private sealed class PeerSnapshotState
    {
        public readonly Dictionary<string, float> recipes = new Dictionary<string, float>();
        public readonly CoopSnapshotPacer pacer = new CoopSnapshotPacer(Time.unscaledTime);
    }
    private readonly Dictionary<NetworkConnectionToClient, PeerSnapshotState> snapshotPeers = new Dictionary<NetworkConnectionToClient, PeerSnapshotState>();
    private string pendingRecipeKey;
    private byte[][] pendingRecipe;
    private int pendingRecipeBytes;
    private static readonly ProfilerMarker EncodeWorkerMarker = new ProfilerMarker("Coop.EncodeWorker");
    private static readonly ProfilerMarker DecodeWorkerMarker = new ProfilerMarker("Coop.DecodeWorker");
    private float nextSend, nextTrace;
    // Reliable snapshots are the bulk of the host's upstream traffic: a full payload is ~64KB, so
    // 10Hz is ~640KB/s *per client*. Home and hole-punched links cannot carry that, kcp2k's send
    // queue grows past 10000 segments and the server drops that member ("the player froze").
    // The interval is therefore derived from the payload size and the worst measured link RTT.
    private float snapshotInterval = 0.1f;
    // Reused apply-scratch buffers: the tail runs on every snapshot and the previous
    // implementation allocated a list, two hash sets and a renderer array each time.
    private readonly List<Texture> retainedTextures = new List<Texture>();
    private readonly List<Renderer> retainedRenderers = new List<Renderer>();
    private readonly List<string> appliedAppearanceKeys = new List<string>();
    // Capture-side scratch that used to be allocated on every snapshot.
    private readonly HashSet<Component> availableComponents = new HashSet<Component>();
    private readonly List<Component> staleHostIds = new List<Component>();
    private static readonly int MainTexProperty = Shader.PropertyToID("_MainTex");
    // Generated-texture lease upkeep only has to beat the 60 second definition eviction,
    // so it does not need to walk every replica on every snapshot.
    private float nextTextureMaintenance;
    private CoopMotion cachedMotion;
    private CoopWorld cachedWorld;
    private CoopUiBridge cachedUiBridge;
    private CoopRemoteAvatarSpawner cachedSpawner;
    private CoopPlayerRegistry cachedRegistry;
    private CoopSession cachedSession;
    private bool prepared;
    private bool resultPresented;
    private bool entityCaptureValidated;
    private int uiTraceFrames;
    private const int ChunkSize = 4000;
    private sealed class SnapshotTransfer
    {
        public uint world;
        public IEnumerator<TransferChunk> chunks;
        public TransferChunk next;
        public bool inRecipes;
    }
    private struct TransferChunk { public string key; public int index, count, owner; public uint world, revision; public byte[] data; }
    private readonly Dictionary<NetworkConnectionToClient, SnapshotTransfer> transfers = new Dictionary<NetworkConnectionToClient, SnapshotTransfer>();
    private readonly List<NetworkConnectionToClient> completedTransfers = new List<NetworkConnectionToClient>();
    private float nextTransferTrace;
    private int droppedStale;
    private static readonly double UploadCap = UploadLimit();
    private static double UploadLimit()
    {
        var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "-coopUploadKBps");
        if (at >= 0 && at + 1 < args.Length && double.TryParse(args[at + 1],
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) &&
            !double.IsInfinity(value) && value > 0) return value * 1024;
        // No flag: still cap the HOST uplink. An uncapped host (old default 0) saturated a home
        // upstream within seconds and the reliable backlog then delivered the world seconds late.
        // This is a host total, shared across peers; -coopUploadKBps overrides it.
        return 1024 * 1024;
    }
    private void ClearTransfers()
    {
        foreach (var transfer in transfers.Values) transfer.chunks.Dispose();
        transfers.Clear();
        snapshotPeers.Clear();
        droppedStale = 0;
    }
    public void ReleaseConnection(NetworkConnectionToClient connection)
    {
        if (transfers.TryGetValue(connection, out var transfer))
        {
            transfer.chunks.Dispose();
            transfers.Remove(connection);
        }
        snapshotPeers.Remove(connection);
        if (CommandLine.Contains("-coopLifecycleValidate"))
            Debug.Log("[CoopPacingTest] Peer released; snapshotPeers=" + snapshotPeers.Count + "; transfers=" + transfers.Count);
    }
    private const int MaxSnapshot = 8 * 1024 * 1024;
    private static readonly string[] CommandLine = Environment.GetCommandLineArgs();
    private static readonly bool ValidateState = CommandLine.Contains("-coopValidate");
    private static readonly bool GraphDiagnostics = CommandLine.Contains("-coopGraphDiagnostics");
    private static readonly bool ValidateRender = CommandLine.Contains("-coopRenderValidate");
    public int LocalPlayerId { get; private set; }
    // Diagnostics for the four-player performance baseline: how much world state the
    // client has actually applied, and how large the replicated set was on each side.
    public uint AppliedRevision => appliedRevision;
    public int ReplicaCount => replicas.Count;
    public int EntityCount { get; private set; }
    public bool TryHostId(Component component, out long id) => hostIds.TryGetValue(component, out id);
    public List<Component> VisibleReplicas<T>() where T : Component => replicas.Values.OfType<T>()
        .Where(component => component != null && component.gameObject.activeInHierarchy).Cast<Component>().ToList();
    public IEnumerable<Component> ReplicaEntities => replicas.Values;
    public static List<Component> MapCharacters()
    {
        if (IsReplica) return Instance.VisibleReplicas<CharacterStatus>().Where(actor => actor != GameSystem.instance.player).ToList();
        List<Component> npcs = GameSystem.instance.ActiveObjects[typeof(NPCScript)];
        if (!NetworkServer.active) return npcs;
        return npcs.Concat(NetworkManager.singleton.GetComponent<CoopPlayerRegistry>().Players
            .Where(actor => actor != GameSystem.instance.player && actor.gameObject.activeInHierarchy).Cast<Component>()).ToList();
    }
    private static readonly string[] GameFields = {
        "activeCharacters", "strikeableLocations", "hives", "families", "directorOrganizations", "timers",
        "hostilityGrid", "sideGroupingAffiliations", "sealingRitualRecipe", "banishPrecursorRecipe",
        "banishArtifactRecipe", "dressupKitRecipe", "satinDiarySeen", "silkDiarySeen", "triedToIDTraitor",
        "totalGameTime", "nextItemSpawnTime", "nextEnemySpawnTime", "nextHumanSpawnTime", "nextWeaponSpawnTime",
        "gameDifficultyMultiplier", "reputation", "trust", "score", "postGame", "gameInProgress",
        "dollMaker", "demonLord", "throne", "demonLordID", "throneID"
    };
    private void Awake()
    {
        Instance = this;
    }
    // CoopNetworkManager adds CoopReplication before CoopMotion/CoopUiBridge exist, so the
    // component references are resolved on first use instead of in Awake.
    private void CacheComponents()
    {
        if (cachedMotion == null) cachedMotion = GetComponent<CoopMotion>();
        if (cachedWorld == null) cachedWorld = GetComponent<CoopWorld>();
        if (cachedUiBridge == null) cachedUiBridge = GetComponent<CoopUiBridge>();
        if (cachedSpawner == null) cachedSpawner = GetComponent<CoopRemoteAvatarSpawner>();
        if (cachedRegistry == null) cachedRegistry = GetComponent<CoopPlayerRegistry>();
        if (cachedSession == null) cachedSession = GetComponent<CoopSession>();
    }
    private void OnDestroy() { if (Instance == this) Instance = null; }
    public void StartClient() { NetworkClient.RegisterHandler<CoopStateChunk>(Receive); NetworkClient.RegisterHandler<CoopRecipeChunk>(ReceiveRecipe); }
    public void StopClient()
    {
        NetworkClient.UnregisterHandler<CoopStateChunk>();
        NetworkClient.UnregisterHandler<CoopRecipeChunk>();
        ResetClientWorld();
    }
    public void ResetClientWorld()
    {
        GetComponent<CoopMotion>()?.Reset();
        if (prepared && GameSystem.instance != null)
        {
            var game = GameSystem.instance;
            game.gameInProgress = false;
            // Room movers and scene locations can tick even outside the main game loop.
            // Keep the discarded replica world dormant until original StartGame recycles it.
            foreach (Component c in replicas.Values) if (c != null) c.gameObject.SetActive(false);
            foreach (RoomData room in CoopTopology.Rooms(game.map)) if (room != null) room.gameObject.SetActive(false);
            if (game.ufoRoom != null && game.ufoRoom.actualMover != null) game.ufoRoom.actualMover.gameObject.SetActive(false);
            // The original next StartGame runs UFO Cleanup before destroying rooms.
            // Remove our owned clones from room lists before destroying them, so that
            // cleanup cannot dereference their now-destroyed Transform wrappers.
            foreach (RoomData room in CoopTopology.Rooms(game.map))
            {
                if (room == null) continue;
                room.containedNPCs.RemoveAll(actor => actor == null || createdReplicas.Contains(actor));
                room.containedOrbs.RemoveAll(orb => orb == null || createdReplicas.Contains(orb));
                room.containedCrates.RemoveAll(crate => crate == null || createdReplicas.Contains(crate));
                room.interactableLocations.RemoveAll(location => location == null || createdReplicas.Contains(location));
            }
            game.observer.gameObject.SetActive(false);
        }
        foreach (Component c in createdReplicas) if (c != null)
        {
            if (c is PlayerScript actor) CoopActorContext.Release(actor);
            c.gameObject.SetActive(false); Destroy(c.gameObject);
        }
        foreach (var pair in originalEnabled) if (pair.Key != null) pair.Key.enabled = pair.Value;
        foreach (var pair in originalKinematic) if (pair.Key != null) pair.Key.isKinematic = pair.Value;
        createdReplicas.Clear(); originalEnabled.Clear(); originalKinematic.Clear();
        if (prepared && GameSystem.instance != null)
        {
            GameSystem.instance.gameInProgress = false;
            GameSystem.instance.player.MovementInputSource = null;
            GameSystem.instance.mainMenuUI.ShowDisplay();
        }
        replicas.Clear(); pending = null; latestSnapshot = null; latestRevision = 0; prepared = false; appliedRevision = receivedRevision = 0;
        appliedStates.Clear(); staleAppliedStates.Clear();
        decoding = null;
        applyGraph = null;
        pendingRecipe = null; pendingRecipeKey = null;
        CoopTextureReference.Reset();
        LocalPlayerId = 0;
        resultPresented = false;
    }
    public void StopServer() { ClearTransfers(); hostIds.Clear(); anchors.Clear(); anchorObjects.Clear(); revision = 0; prepared = false; encoding = null; captureGraph = null; }
    public void ResetHostWorld() { ClearTransfers(); hostIds.Clear(); anchors.Clear(); anchorObjects.Clear(); prepared = false; encoding = null; captureGraph = null; CoopTextureReference.Reset(); }
    public static void Checkout(Component entity)
    {
        if (Instance != null && NetworkServer.active) Instance.hostIds.Remove(entity);
    }
    private bool Entity(Component c) => c is CharacterStatus || c is StrikeableLocation || c is ItemOrb || c is ItemCrate ||
        (c != null && PrefabTypes().Contains(c.GetType()) && !(c is FloatingText) && !(c is AttackArc));
    // ActiveObjects holds thousands of components and the active set is far smaller.
    // Caching the pooled prefab types keeps the per-component filter off reflection-heavy
    // dictionary probing, which dominated host capture time.
    private static HashSet<Type> prefabTypes;
    private static Dictionary<Type, UnityEngine.Object> prefabTypesSource;
    private static HashSet<Type> PrefabTypes()
    {
        var source = GameSystem.instance.Prefabs;
        if (!ReferenceEquals(prefabTypesSource, source))
        {
            prefabTypesSource = source;
            prefabTypes = new HashSet<Type>(source.Keys);
        }
        return prefabTypes;
    }
    private List<Component> Entities()
    {
        var game = GameSystem.instance;
        var result = new HashSet<Component>();
        foreach (CharacterStatus c in game.activeCharacters) if (c != null) result.Add(c);
        foreach (PlayerScript c in GetComponent<CoopPlayerRegistry>().Players) if (c != null) result.Add(c);
        if (game.player != null) result.Add(game.player);
        foreach (StrikeableLocation c in game.strikeableLocations) if (c != null) result.Add(c);
        foreach (var list in game.ActiveObjects.Values)
            foreach (Component c in list) if (c != null && c.gameObject.activeInHierarchy && Entity(c)) result.Add(c);
        // Original AI dictionaries can retain inactive, pooled characters. Keep their
        // identities resolvable until checkout or actual destruction, rather than
        // turning a still-live dictionary key into null on the replica.
        if (NetworkServer.active)
            foreach (Component c in hostIds.Keys) if (c != null) result.Add(c);
        return result.ToList();
    }
    private void BuildAnchors()
    {
        anchors.Clear(); anchorObjects.Clear();
        var rooms = CoopTopology.Rooms(GameSystem.instance.map);
        for (int i = 0; i < rooms.Count; i++)
        {
            var room = rooms[i];
            anchors[room] = "room/" + i;
            anchorObjects["room/" + i] = room;
            foreach (Component c in room.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!Entity(c)) continue;
                string key = "room/" + i + "/" + RelativePath(room.transform, c.transform) + "/" + c.GetType().FullName;
                anchors[c] = key; anchorObjects[key] = c;
            }
        }
        // Some original locations are scene-wired and deployed without a pool prefab.
        foreach (FieldInfo field in typeof(GameSystem).GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (field.GetValue(GameSystem.instance) is Component c && Entity(c) && !(c is CharacterStatus))
            {
                string key = "scene/GameSystem/" + field.Name;
                anchors[c] = key; anchorObjects[key] = c;
            }
    }
    private static string RelativePath(Transform root, Transform child)
    {
        string path = "";
        while (child != root && child != null) { path = child.GetSiblingIndex() + "/" + path; child = child.parent; }
        return path;
    }
    private string UnityKey(UnityEngine.Object obj)
    {
        if (!(obj is Component c) || c == null) return null;
        if (c is RoomData && anchors.TryGetValue(c, out string room)) return room;
        if (hostIds.TryGetValue(c, out long id)) return "entity/" + id;
        if (!NetworkServer.active)
            foreach (var pair in replicas) if (pair.Value == c) return "entity/" + pair.Key;
        if (anchors.TryGetValue(c, out string key)) return key;
        return null;
    }
    private UnityEngine.Object UnityObject(string key)
    {
        if (key.StartsWith("entity/")) return replicas.TryGetValue(long.Parse(key.Substring(7)), out Component c) ? c : null;
        return anchorObjects.TryGetValue(key, out Component a) ? a : null;
    }
    public string ReferenceKey(UnityEngine.Object obj) => UnityKey(obj);
    public UnityEngine.Object ResolveReference(string key) => UnityObject(key);
    public static string StateSignature(CharacterStatus actor)
    {
        var graph = new CoopStateGraph(Instance.UnityKey, Instance.UnityObject);
        var state = new JObject();
        foreach (string name in new[] { "hp", "will", "armor", "stamina", "currentItems", "weapon", "npcType", "currentAI", "typeMetadata", "timers", "draggedCharacters", "followingPlayer", "holdingPosition", "shouldRemove", "knowledge" })
        {
            FieldInfo field = typeof(CharacterStatus).GetField(name);
            state[name] = graph.Write(field.GetValue(actor));
        }
        return CoopTopology.Digest(state.ToString(Formatting.None));
    }
    private static JObject FieldSignatures(CharacterStatus actor)
    {
        var checks = new JObject();
        foreach (string name in new[] { "hp", "will", "armor", "stamina", "currentItems", "weapon", "npcType", "currentAI", "typeMetadata", "timers", "draggedCharacters", "followingPlayer", "holdingPosition", "shouldRemove", "knowledge" })
        {
            var graph = new CoopStateGraph(Instance.UnityKey, Instance.UnityObject);
            checks[name] = CoopTopology.Digest(graph.Write(typeof(CharacterStatus).GetField(name).GetValue(actor)).ToString(Newtonsoft.Json.Formatting.None));
        }
        return checks;
    }
    private static JObject SpeciesChecks(NPCType species)
    {
        var checks = new JObject();
        foreach (FieldInfo field in CoopStateGraph.Fields(species.GetType()))
        {
            var graph = new CoopStateGraph(Instance.UnityKey, Instance.UnityObject);
            checks[field.DeclaringType.FullName + "/" + field.Name] = CoopTopology.Digest(graph.Write(field.GetValue(species)).ToString(Newtonsoft.Json.Formatting.None));
        }
        return checks;
    }
    public static string DisplayTimer(Timer timer) => IsReplica && Instance.timerDisplays.TryGetValue(timer, out string value) ? value : timer.DisplayValue();
    private void LateUpdate()
    {
        var world = GetComponent<CoopWorld>();
        var game = GameSystem.instance;
        if (NetworkServer.active) PumpTransfers();
        // Full snapshots supersede earlier snapshots. Network callbacks only assemble
        // the newest one; never replay a queue of obsolete worlds in a single frame.
        if (!NetworkServer.active && decoding != null && decoding.IsCompleted)
        {
            Task<DecodedSnapshot> completed = decoding; decoding = null;
            var decoded = completed.GetAwaiter().GetResult();
            CoopPerformanceProbe.AddStage("decodeWorker", decoded.milliseconds);
            if (world.SharedWorldRunning && decoded.world == world.Generation && decoded.revision > appliedRevision)
            {
                long perfApply = CoopPerformanceProbe.Begin("Coop.Apply");
                try { Apply(decoded.root, decoded.owner, decoded.nodes); appliedRevision = decoded.revision; }
                finally { CoopPerformanceProbe.End("apply", perfApply); }
            }
        }
        if (!NetworkServer.active && latestSnapshot != null && decoding == null)
        {
            byte[] all = latestSnapshot; latestSnapshot = null;
            uint incomingRevision = latestRevision, incomingWorld = world.Generation; int owner = latestOwner;
            decoding = Task.Run(() => DecodeSnapshot(all, incomingWorld, incomingRevision, owner));
        }
        if (NetworkServer.active && encoding != null && encoding.IsCompleted)
        {
            Task<EncodedSnapshot> completed = encoding; encoding = null;
            try
            {
                var encoded = completed.GetAwaiter().GetResult();
                CoopPerformanceProbe.AddStage("encodeWorker", encoded.milliseconds, encoded.bytes.Length);
                if (world.SharedWorldRunning && encoded.world == world.Generation)
                {
                    long perfSend = CoopPerformanceProbe.Begin("Coop.Send");
                    try { SendSnapshot(encoded); }
                    finally { CoopPerformanceProbe.End("sendSnapshot", perfSend); }
                    UpdateSnapshotInterval(encoded.bytes.Length);
                }
            }
            catch (Exception e) { Debug.LogError("[Coop] State encode failed: " + e); nextSend = Time.unscaledTime + 5f; }
        }
        if (!NetworkServer.active || !world.SharedWorldRunning || game == null || game.map == null) return;
        if (!NetworkServer.connections.Values.Any(c => c != null && c.isAuthenticated && c != NetworkServer.localConnection)) return;
        if (!NetworkServer.connections.Values.Any(c => c != null && c.isAuthenticated && c != NetworkServer.localConnection && !transfers.ContainsKey(c) && !Backlogged(c))) return;
        if (Time.unscaledTime < nextSend) return;
        if (encoding != null) return; // One immutable full snapshot in flight; no backlog.
        // Diagnostic floor measurement only: leave the world frozen while the window records.
        if (CoopPerformanceProbe.SkipStateReplication && CoopPerformanceProbe.Recording) return;
        nextSend = Time.unscaledTime + snapshotInterval;
        try { Publish(); }
        catch (Exception e) { Debug.LogError("[Coop] State publish failed: " + e); nextSend = Time.unscaledTime + 5f; }
    }
    private void Publish()
    {
        CaptureSnapshot();
    }
    private static JObject CaptureEntityFields(CoopCachedStateGraph graph, Component component)
    {
        long began = CoopPerformanceProbe.Begin("Coop.CaptureEntityFields");
        try { return graph.CaptureFields(component); }
        finally { CoopPerformanceProbe.End("captureEntityFields", began); }
    }
    // Main-thread values only. No Unity objects escape into the encoder task.
    private sealed class EntityCapture
    {
        public long id;
        public string kind, anchor, locationIcon, check;
        public int player, followOwner;
        public bool active;
        public string[] timerDisplays, spriteTextures, textureChecks;
        public Color? spriteColor;
        public Vector3 position;
        public Quaternion rotation;
        public byte[] appearance;
        public JObject fields, fieldChecks, speciesChecks;
        public JObject ToRecord()
        {
            return new JObject {
                ["id"] = id, ["kind"] = kind, ["anchor"] = anchor,
                ["player"] = player, ["active"] = active, ["followOwner"] = followOwner,
                ["locationIcon"] = locationIcon, ["check"] = check,
                ["fieldChecks"] = fieldChecks, ["speciesChecks"] = speciesChecks,
                ["timerDisplays"] = timerDisplays == null ? null : new JArray(timerDisplays),
                ["spriteTextures"] = spriteTextures == null ? null : new JArray(spriteTextures),
                ["spriteColor"] = spriteColor.HasValue ? new JArray(spriteColor.Value.r, spriteColor.Value.g, spriteColor.Value.b, spriteColor.Value.a) : null,
                ["textureChecks"] = textureChecks == null ? null : new JArray(textureChecks),
                ["position"] = new JArray(position.x, position.y, position.z),
                ["rotation"] = new JArray(rotation.x, rotation.y, rotation.z, rotation.w),
                ["appearance"] = appearance == null ? null : new JValue(appearance),
                ["fields"] = fields
            };
        }
    }
    private static byte[] CaptureAppearance(Component component, List<string> textureReferences)
    {
        long began = CoopPerformanceProbe.Begin("Coop.CaptureAppearance");
        try { return CoopAppearance.CaptureCached(component, textureReferences); }
        finally { CoopPerformanceProbe.End("captureAppearance", began); }
    }
    private static IEnumerable<string> SnapshotTextures(JObject root)
    {
        foreach (JObject entity in (JArray)root["entities"])
        {
            yield return (string)entity["locationIcon"];
            if (entity["spriteTextures"] is JArray sprites)
                foreach (JToken texture in sprites) yield return (string)texture;
            if (entity["appearance"] is JArray appearance)
                foreach (JObject renderer in appearance)
                    if (renderer["textures"] is JArray textures)
                        foreach (JToken texture in textures) yield return (string)texture;
        }
    }
    private void CaptureSnapshot()
    {
        long perfCapture = CoopPerformanceProbe.Begin("Coop.Capture");
        var game = GameSystem.instance;
        CacheComponents();
        if (!prepared) { BuildAnchors(); prepared = true; }
        long perfCollect = CoopPerformanceProbe.Begin("Coop.CaptureCollect");
        List<Component> entities = Entities();
        EntityCount = entities.Count;
        foreach (Component c in entities)
            if (!hostIds.ContainsKey(c)) hostIds[c] = cachedSession.AllocateEntityId();
        // Membership over a 400+ entry list was quadratic here; a set keeps it linear.
        availableComponents.Clear();
        foreach (Component c in entities) availableComponents.Add(c);
        staleHostIds.Clear();
        foreach (Component c in hostIds.Keys)
            if (c == null || !availableComponents.Contains(c)) staleHostIds.Add(c);
        foreach (Component c in staleHostIds) hostIds.Remove(c);
        CoopPerformanceProbe.End("captureCollect", perfCollect);
        var graph = captureGraph ?? (captureGraph = new CoopCachedStateGraph(UnityKey, UnityObject));
        graph.BeginCapture();
        var records = new List<EntityCapture>(entities.Count);
        var appearanceTextures = new List<string>();
        var owners = new HashSet<int>();
        long perfEntities = CoopPerformanceProbe.Begin("Coop.CaptureEntities");
        foreach (Component c in entities)
        {
            string anchor = anchors.TryGetValue(c, out var a) ? a : null;
            var player = c as PlayerScript;
            int playerId = player == null ? 0 : cachedRegistry.GetPlayerId(player);
            if (player == game.player) playerId = cachedSession.GetPlayerId(NetworkServer.localConnection);
            if (playerId > 0) owners.Add(playerId);
            var actor = c as CharacterStatus;
            var record = new EntityCapture {
                id = hostIds[c], kind = c.GetType().FullName, anchor = anchor,
                player = playerId, active = c.gameObject.activeSelf,
                followOwner = actor == null ? 0 : actor.CoopFollowPlayerId,
                locationIcon = c is StrikeableLocation location ? CoopTextureReference.Capture(location.iconTexture) : null,
                check = ValidateState && actor != null ? StateSignature(actor) : null,
                fieldChecks = GraphDiagnostics && actor != null ? FieldSignatures(actor) : null,
                speciesChecks = GraphDiagnostics && actor != null ? SpeciesChecks(actor.npcType) : null,
                position = c.transform.position, rotation = c.transform.rotation,
                appearance = actor != null ? null : CaptureAppearance(c, appearanceTextures),
                fields = CaptureEntityFields(graph, c)
            };
            appearanceTextures.Add(record.locationIcon);
            if (actor != null)
            {
                record.timerDisplays = new string[actor.timers.Count];
                for (int i = 0; i < record.timerDisplays.Length; i++) record.timerDisplays[i] = actor.timers[i].DisplayValue();
                record.spriteTextures = new string[actor.spriteStack.Count];
                if (ValidateRender) record.textureChecks = new string[record.spriteTextures.Length];
                for (int i = 0; i < record.spriteTextures.Length; i++)
                {
                    var texture = actor.spriteStack[i].sprite;
                    record.spriteTextures[i] = CoopTextureReference.Capture(texture);
                    appearanceTextures.Add(record.spriteTextures[i]);
                    if (ValidateRender) record.textureChecks[i] = texture is RenderTexture ? CoopTextureRecipe.PixelSignature(texture) : null;
                }
                var renderer = actor.mainSpriteRenderer;
                if (renderer != null && renderer.sharedMaterial.HasProperty("_Color"))
                    record.spriteColor = renderer.sharedMaterial.color;
            }
            if (!entityCaptureValidated && CommandLine.Contains("-coopGraphValidate"))
            {
            var expected = new JObject { ["id"] = hostIds[c], ["kind"] = c.GetType().FullName,
                ["anchor"] = anchor, ["player"] = playerId, ["active"] = c.gameObject.activeSelf,
                ["followOwner"] = c is CharacterStatus follower ? follower.CoopFollowPlayerId : 0,
                ["locationIcon"] = c is StrikeableLocation checkedLocation ? CoopTextureReference.Capture(checkedLocation.iconTexture) : null,
                ["check"] = ValidateState && c is CharacterStatus status ? StateSignature(status) : null,
                ["fieldChecks"] = GraphDiagnostics && c is CharacterStatus checkedActor ? FieldSignatures(checkedActor) : null,
                ["speciesChecks"] = GraphDiagnostics && c is CharacterStatus checkedSpecies ? SpeciesChecks(checkedSpecies.npcType) : null,
                ["timerDisplays"] = c is CharacterStatus timed ? new JArray(timed.timers.Select(t => t.DisplayValue())) : null,
                ["spriteTextures"] = c is CharacterStatus pictured ? new JArray(pictured.spriteStack.Select(s => CoopTextureReference.Capture(s.sprite))) : null,
                ["spriteColor"] = c is CharacterStatus tinted && tinted.mainSpriteRenderer != null && tinted.mainSpriteRenderer.sharedMaterial.HasProperty("_Color") ? new JArray(tinted.mainSpriteRenderer.sharedMaterial.color.r, tinted.mainSpriteRenderer.sharedMaterial.color.g, tinted.mainSpriteRenderer.sharedMaterial.color.b, tinted.mainSpriteRenderer.sharedMaterial.color.a) : null,
                ["textureChecks"] = ValidateRender && c is CharacterStatus checkedPicture ? new JArray(checkedPicture.spriteStack.Select(s => s.sprite is RenderTexture ? CoopTextureRecipe.PixelSignature(s.sprite) : null)) : null,
                ["position"] = new JArray(c.transform.position.x, c.transform.position.y, c.transform.position.z),
                ["rotation"] = new JArray(c.transform.rotation.x, c.transform.rotation.y, c.transform.rotation.z, c.transform.rotation.w),
                ["appearance"] = c is CharacterStatus ? null : new JValue(record.appearance),
                ["fields"] = record.fields.DeepClone() };
                if (!JToken.DeepEquals(expected, record.ToRecord()))
                    throw new InvalidOperationException("Entity capture differs from original record: " + record.kind + "; id=" + record.id);
            }
            records.Add(record);
        }
        if (!entityCaptureValidated && CommandLine.Contains("-coopGraphValidate"))
        {
            entityCaptureValidated = true;
            Debug.Log("[CoopEntityCapture] PASS original record equivalence; entities=" + records.Count);
        }
        CoopPerformanceProbe.End("captureEntities", perfEntities);
        long perfGlobals = CoopPerformanceProbe.Begin("Coop.CaptureGlobals");
        var globals = new JObject();
        foreach (string name in GameFields)
        {
            FieldInfo f = typeof(GameSystem).GetField(name);
            if (f != null) globals[name] = graph.Write(f.GetValue(game));
        }
        CoopPerformanceProbe.End("captureGlobals", perfGlobals);
        long perfRooms = CoopPerformanceProbe.Begin("Coop.CaptureRooms");
        var roomState = new JArray();
        foreach (RoomData room in CoopTopology.Rooms(game.map))
            roomState.Add(new JValue(graph.CaptureRoom(room)));
        CoopPerformanceProbe.End("captureRooms", perfRooms);
        var root = new JObject { ["game"] = globals, ["rooms"] = roomState, ["time"] = NetworkTime.localTime,
            ["result"] = game.postGame ? game.resultsText.text : null };
        long perfObjects = CoopPerformanceProbe.Begin("Coop.CaptureObjects");
        var objectRows = graph.FinishCapture();
        CoopPerformanceProbe.End("captureObjects", perfObjects);
        var resources = CoopTextureRecipe.Collect(appearanceTextures);
        CoopPerformanceProbe.End("capture", perfCapture);
        uint generation = cachedWorld.Generation; int entityCount = entities.Count;
        encoding = Task.Run(() => EncodeSnapshot(root, records, objectRows, generation, entityCount, resources, owners));
    }
    private static EncodedSnapshot EncodeSnapshot(JObject root, List<EntityCapture> records, CoopCachedStateGraph.Frame nodes, uint generation, int entityCount, Dictionary<string, byte[]> resources, HashSet<int> owners)
    {
        // A four-player session on one machine runs five heavy processes; the encoder must never
        // take CPU at the same priority as a main thread, or the host (which owns the largest
        // encode) loses frames to its own worker.
        var previousPriority = LowerWorkerPriority();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (EncodeWorkerMarker.Auto())
            {
                var entities = new JArray();
                foreach (var record in records) entities.Add(record.ToRecord());
                root["entities"] = entities;
                root["nodes"] = new JValue(CoopCachedStateGraph.EncodeRows(nodes));
                byte[] bytes = Compress(CoopSnapshotCodec.Encode(root));
                if (bytes.Length > MaxSnapshot) throw new InvalidOperationException("World snapshot exceeds limit");
                return new EncodedSnapshot { bytes = bytes, world = generation, entities = entityCount, milliseconds = watch.Elapsed.TotalMilliseconds, recipes = resources, owners = owners };
            }
        }
        finally { RestoreWorkerPriority(previousPriority); }
    }
    private static DecodedSnapshot DecodeSnapshot(byte[] bytes, uint generation, uint incomingRevision, int owner)
    {
        var previousPriority = LowerWorkerPriority();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (DecodeWorkerMarker.Auto())
            {
                var root = CoopSnapshotCodec.Decode(Decompress(bytes));
                // Appearance bytes stay raw here: the apply path hashes them and only pays for
                // decoding plus writing renderers when an entity's appearance actually changed.
                var nodes = CoopCachedStateGraph.DecodeRows((byte[])root["nodes"]); root.Remove("nodes");
                return new DecodedSnapshot { root = root, nodes = nodes, world = generation,
                    revision = incomingRevision, owner = owner, milliseconds = watch.Elapsed.TotalMilliseconds };
            }
        }
        finally { RestoreWorkerPriority(previousPriority); }
    }
    // Encoding/decoding runs on a pool thread at below-normal priority so a busy main thread
    // always wins the CPU. The thread is shared with other Unity work, so it is restored.
    private static System.Threading.ThreadPriority LowerWorkerPriority()
    {
        var previous = System.Threading.Thread.CurrentThread.Priority;
        try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal; }
        catch (Exception) { }
        return previous;
    }
    private static void RestoreWorkerPriority(System.Threading.ThreadPriority previous)
    {
        try { System.Threading.Thread.CurrentThread.Priority = previous; }
        catch (Exception) { }
    }
    /// <summary>
    /// Keeps reliable snapshot traffic inside what the slowest joined link can actually carry.
    /// Without this a saturated upstream (a full payload at 10Hz is ~640KB/s per client) lets
    /// kcp2k's send queue grow past its 10000-segment limit, and the server then disconnects that
    /// member mid-game - which the players see as a frozen teammate.
    /// </summary>
    private void UpdateSnapshotInterval(int payloadBytes)
    {
        double worstRtt = 0;
        foreach (var connection in NetworkServer.connections.Values)
            if (connection != null && connection.isAuthenticated && connection != NetworkServer.localConnection)
                worstRtt = Math.Max(worstRtt, connection.rtt);
        // Per-client upstream budget: a LAN can take a megabyte per second, a 60ms link cannot.
        double budget = worstRtt < 0.03 ? 1200000 : worstRtt < 0.08 ? 500000 : worstRtt < 0.15 ? 250000 : worstRtt < 0.30 ? 150000 : 90000;
        float byBudget = (float)(payloadBytes / budget);
        float updated = Mathf.Clamp(Mathf.Max(0.1f, byBudget), 0.1f, 1f);
        if (Mathf.Abs(updated - snapshotInterval) > 0.005f && (SnapshotDiagnostics || updated > 0.11f))
            Debug.Log("[Coop] Snapshot interval " + snapshotInterval.ToString("F3") + "s -> " + updated.ToString("F3") +
                "s; payload=" + (payloadBytes / 1024) + "KB; worstRttMs=" + (worstRtt * 1000.0).ToString("F0") +
                "; upstream=" + (payloadBytes / Math.Max(0.0001f, updated) / 1024f).ToString("F0") + "KB/s per client");
        snapshotInterval = updated;
    }
    private static readonly bool SnapshotDiagnostics = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-coopInputTrace") >= 0;
    private CoopKcpTransport pacingTransport;
    // KCP counts MTU-sized segments, not our 4KB chunks; convert for a queue-delay estimate.
    private const double SegmentBytes = 1200;
    private double QueueDelayMs(NetworkConnectionToClient connection, double budget)
    {
        if (pacingTransport == null) pacingTransport = GetComponent<CoopKcpTransport>();
        if (pacingTransport == null) return 0;
        int pending = pacingTransport.PendingReliable(connection.connectionId);
        return pending * SegmentBytes / Math.Max(1.0, budget) * 1000.0;
    }
    // A deep reliable backlog is exactly the "the joiner's world is seconds behind" symptom:
    // KCP delivers in order, so every queued segment is another slice of stale world. The gate is
    // therefore expressed as queue delay (~0.5s of this connection's own budget) instead of a
    // fixed 128-segment ceiling, which at a narrow link allowed several seconds of backlog.
    private bool Backlogged(NetworkConnectionToClient connection)
    {
        return QueueDelayMs(connection, PacingBudget(connection)) >= 500;
    }
    private double PacingBudget(NetworkConnectionToClient connection)
    {
        // One host has one uplink, and a snapshot is broadcast to every peer: the link budget below
        // is HOST-WIDE and shared out per peer. Reading it as a per-connection allowance multiplied
        // the real traffic by the player count (2 peers on a 250KB/s link were fed ~1MB/s), which is
        // what pushed snapshots several seconds deep into KCP on the narrow profiles.
        int peers = 0;
        double worstRtt = 0;
        foreach (var peer in NetworkServer.connections.Values)
        {
            if (peer == null || !peer.isAuthenticated || peer == NetworkServer.localConnection) continue;
            peers++;
            worstRtt = Math.Max(worstRtt, peer.rtt);
        }
        double total = worstRtt < .03 ? 1200000 : worstRtt < .08 ? 500000 : worstRtt < .15 ? 250000 : worstRtt < .30 ? 150000 : 90000;
        if (UploadCap > 0) total = Math.Min(total, UploadCap * .85);
        var game = GameSystem.instance;
        int actors = game == null ? 24 : game.activeCharacters.Count;
        double motionReserve = actors * 36 * (actors > 24 ? 30 : 60) + 9000;
        return Math.Max(8000, total / Math.Max(1, peers) - motionReserve);
    }
    private IEnumerable<TransferChunk> TransferChunks(EncodedSnapshot snapshot, uint rev, int owner, Dictionary<string, float> seen)
    {
        foreach (var recipe in snapshot.recipes)
        {
            if (seen.TryGetValue(recipe.Key, out float sentAt) && Time.unscaledTime - sentAt < 20) continue;
            int pieces = (recipe.Value.Length + ChunkSize - 1) / ChunkSize;
            for (int part = 0; part < pieces; part++)
            {
                int length = Math.Min(ChunkSize, recipe.Value.Length - part * ChunkSize);
                var data = new byte[length]; Buffer.BlockCopy(recipe.Value, part * ChunkSize, data, 0, length);
                yield return new TransferChunk { key = recipe.Key, index = part, count = pieces, data = data, world = snapshot.world };
            }
            seen[recipe.Key] = Time.unscaledTime;
        }
        int count = (snapshot.bytes.Length + ChunkSize - 1) / ChunkSize;
        for (int part = 0; part < count; part++)
        {
            int length = Math.Min(ChunkSize, snapshot.bytes.Length - part * ChunkSize);
            var data = new byte[length]; Buffer.BlockCopy(snapshot.bytes, part * ChunkSize, data, 0, length);
            yield return new TransferChunk { index = part, count = count, data = data,
                world = snapshot.world, revision = rev, owner = owner };
        }
    }
    private void PumpTransfers()
    {
        completedTransfers.Clear();
        double now = Time.unscaledTime;
        uint activeWorld = GetComponent<CoopWorld>().Generation;
        int peakQueue = 0;
        double peakQueueMs = 0;
        foreach (var pair in transfers)
        {
            var connection = pair.Key; var transfer = pair.Value;
            if (transfer.world != activeWorld || !NetworkServer.connections.TryGetValue(connection.connectionId, out var live) || live != connection)
            { completedTransfers.Add(connection); continue; }
            double budget = PacingBudget(connection);
            var pacer = snapshotPeers[connection].pacer;
            double queueMs = QueueDelayMs(connection, budget);
            pacer.Advance(now, budget, queueMs);
            // State snapshots are self-contained, so a stale one has no value: once the reliable
            // queue would deliver it more than a second late, drop it and let the next snapshot win.
            // Recipe chunks are never dropped (the receiver needs them to resolve appearance).
            if (queueMs > 1000 && !transfer.inRecipes) { droppedStale++; completedTransfers.Add(connection); continue; }
            while (!Backlogged(connection) && pacer.TrySpend(transfer.next.data.Length + 96))
            {
                var chunk = transfer.next;
                transfer.inRecipes = chunk.key != null;
                if (chunk.key == null)
                    connection.Send(new CoopStateChunk { revision = chunk.revision, owner = chunk.owner,
                        index = chunk.index, count = chunk.count, data = chunk.data, world = chunk.world });
                else
                    connection.Send(new CoopRecipeChunk { world = chunk.world, key = chunk.key,
                        index = chunk.index, count = chunk.count, data = chunk.data });
                if (!transfer.chunks.MoveNext()) { completedTransfers.Add(connection); break; }
                transfer.next = transfer.chunks.Current;
            }
            if (pacingTransport != null) peakQueue = Math.Max(peakQueue, pacingTransport.PendingReliable(connection.connectionId));
            peakQueueMs = Math.Max(peakQueueMs, queueMs);
        }
        foreach (var connection in completedTransfers) { transfers[connection].chunks.Dispose(); transfers.Remove(connection); }
        if (SnapshotDiagnostics && transfers.Count > 0 && Time.unscaledTime >= nextTransferTrace)
        {
            nextTransferTrace = Time.unscaledTime + 5;
            Debug.Log("[CoopPacing] activeTransfers=" + transfers.Count + "; pendingReliableSegments=" + peakQueue +
                "; queueMs=" + peakQueueMs.ToString("F0") +
                "; droppedStale=" + droppedStale +
                "; chunkBytes=" + ChunkSize + "; uploadCapKBps=" + (UploadCap / 1024).ToString("F0"));
        }
    }
    private void SendSnapshot(EncodedSnapshot snapshot)
    {
        byte[] bytes = snapshot.bytes;
        uint rev = ++revision;
        foreach (var connection in NetworkServer.connections.Values)
        {
            if (connection == null || !connection.isAuthenticated || connection == NetworkServer.localConnection) continue;
            int owner = GetComponent<CoopSession>().GetPlayerId(connection);
            if (!snapshot.owners.Contains(owner)) continue;
            // One in-progress world per peer. Later worlds supersede ones that
            // have not started; never accumulate an application snapshot queue.
            if (transfers.ContainsKey(connection) || Backlogged(connection)) continue;
            if (!snapshotPeers.TryGetValue(connection, out var peer))
                snapshotPeers[connection] = peer = new PeerSnapshotState();
            var chunks = TransferChunks(snapshot, rev, owner, peer.recipes).GetEnumerator();
            if (chunks.MoveNext()) transfers[connection] = new SnapshotTransfer {
                world = snapshot.world, chunks = chunks, next = chunks.Current };
            else chunks.Dispose();
        }
        if (Time.unscaledTime >= nextTrace)
        {
            nextTrace = Time.unscaledTime + 5f;
            Debug.Log("[Coop] State published; rev=" + rev + "; entities=" + snapshot.entities + "; bytes=" + bytes.Length);
        }
    }
    private void ReceiveRecipe(CoopRecipeChunk message)
    {
        if (NetworkServer.active || !GetComponent<CoopWorld>().SharedWorldRunning || message.world != GetComponent<CoopWorld>().Generation) return;
        if (string.IsNullOrEmpty(message.key) || !message.key.StartsWith("recipe-id/") || message.key.Length != 74 ||
            message.count <= 0 || message.count > MaxSnapshot / ChunkSize + 1 || message.index < 0 || message.index >= message.count ||
            message.data == null || message.data.Length > ChunkSize) throw new InvalidDataException("Invalid generated texture chunk");
        if (pendingRecipeKey != message.key)
        { pendingRecipeKey = message.key; pendingRecipe = new byte[message.count][]; pendingRecipeBytes = 0; }
        if (pendingRecipe.Length != message.count) throw new InvalidDataException("Generated texture chunk mismatch");
        if (pendingRecipe[message.index] != null) return;
        pendingRecipe[message.index] = message.data; pendingRecipeBytes += message.data.Length;
        if (pendingRecipeBytes > MaxSnapshot) throw new InvalidDataException("Generated texture definition exceeds limit");
        if (pendingRecipe.Any(p => p == null)) return;
        byte[] payload = new byte[pendingRecipeBytes]; int at = 0;
        foreach (byte[] piece in pendingRecipe) { Buffer.BlockCopy(piece, 0, payload, at, piece.Length); at += piece.Length; }
        CoopTextureRecipe.Register(message.key, payload); pendingRecipe = null; pendingRecipeKey = null;
    }
    private void Receive(CoopStateChunk message)
    {
        if (NetworkServer.active || !GetComponent<CoopWorld>().SharedWorldRunning || message.world != GetComponent<CoopWorld>().Generation || message.revision <= appliedRevision || message.revision <= latestRevision) return;
        if (message.count <= 0 || message.count > MaxSnapshot / ChunkSize + 1 || message.index < 0 ||
            message.index >= message.count || message.data == null || message.data.Length > ChunkSize || message.owner <= 0)
            throw new InvalidDataException("Invalid state chunk");
        if (message.revision < receivedRevision) return;
        if (message.revision != receivedRevision)
        {
            receivedRevision = message.revision; pending = new byte[message.count][]; pendingOwner = message.owner; pendingBytes = 0;
        }
        if (pending.Length != message.count || pendingOwner != message.owner) throw new InvalidDataException("State chunk mismatch");
        if (pending[message.index] != null) return;
        pending[message.index] = message.data; pendingBytes += message.data.Length;
        if (pendingBytes > MaxSnapshot) throw new InvalidDataException("State too large");
        if (pending.Any(c => c == null)) return;
        var all = new byte[pendingBytes]; int offset = 0;
        foreach (byte[] chunk in pending) { Buffer.BlockCopy(chunk, 0, all, offset, chunk.Length); offset += chunk.Length; }
        latestSnapshot = all; latestOwner = pendingOwner; latestRevision = message.revision; pending = null;
    }
    // Per-replica "already applied" fingerprints. The host resends whole entity payloads,
    // but re-decoding and re-writing several hundred renderers or field bodies that did not
    // change is pure main-thread cost, so the client skips identical payloads. A freshly
    // created replica has no fingerprint and always receives the full payload.
    private sealed class AppliedState
    {
        public bool hasFields, hasAppearance;
        public long fieldsA, fieldsB, appearanceA, appearanceB, collections;
        public readonly List<string> appearanceKeys = new List<string>();
    }
    private readonly Dictionary<long, AppliedState> appliedStates = new Dictionary<long, AppliedState>();
    private readonly List<long> staleAppliedStates = new List<long>();
    private sealed class CollectionProbe
    {
        public Func<object, object> get;
        public Func<object, int> version;
    }
    private static readonly Dictionary<Type, CollectionProbe[]> collectionProbes = new Dictionary<Type, CollectionProbe[]>();
    private static readonly CollectionProbe[] noProbes = new CollectionProbe[0];
    // Exposed BCL collections can be mutated in place by local presentation code, exactly
    // like the graph rows handled by CoopCachedStateGraph.ApplyRow. Their version counters
    // take part in the skip decision so an unchanged field body is still restored whenever a
    // local writer touched one of those collections.
    private static CollectionProbe[] Probes(Type type)
    {
        if (collectionProbes.TryGetValue(type, out CollectionProbe[] cached)) return cached;
        // Must reuse the graph's full member table: CoopFieldAccess caches per type, so a
        // partial field list here would desync the indices used everywhere else.
        FieldInfo[] fields = CoopStateGraph.Fields(type);
        CoopFieldAccess.Member[] members = CoopFieldAccess.For(type, fields);
        var probes = new List<CollectionProbe>();
        for (int i = 0; i < fields.Length; i++)
        {
            Func<object, int> version = CoopCollectionVersion.For(fields[i].FieldType);
            if (version == null) continue;
            probes.Add(new CollectionProbe { get = members[i].get, version = version });
        }
        CollectionProbe[] result = probes.Count == 0 ? noProbes : probes.ToArray();
        collectionProbes[type] = result; return result;
    }
    private static long CollectionFingerprint(object value, CollectionProbe[] probes)
    {
        if (probes.Length == 0) return 0;
        long hash = 17;
        for (int i = 0; i < probes.Length; i++)
        {
            object collection = probes[i].get(value);
            int version = collection == null ? 0 : probes[i].version(collection);
            int count = collection is System.Collections.ICollection sized ? sized.Count : -1;
            hash = unchecked(hash * 31 + version * 397 + count + i);
        }
        return hash;
    }
    private static void Hash(byte[] bytes, out long a, out long b)
    {
        // Two independent 64-bit accumulators so an unchanged payload cannot be mistaken
        // for a changed one by a single-accumulator collision.
        long first = 1469598103934665603L, second = unchecked((long)0x9E3779B97F4A7C15UL);
        for (int i = 0; i < bytes.Length; i++)
        {
            first = unchecked((first ^ bytes[i]) * 1099511628211L);
            second = unchecked((second ^ bytes[i]) * 0x100000001B3L);
        }
        a = first; b = second;
    }
    private void Apply(JObject root, int owner, CoopCachedStateGraph.Frame nodes)
    {
        long perfFields = CoopPerformanceProbe.Begin("Coop.ApplyFields");
        var game = GameSystem.instance;
        CacheComponents();
        if (!prepared)
        {
            BuildAnchors();
            foreach (Component c in Entities())
                if (!(c is PlayerScript)) { c.gameObject.SetActive(false); }
            prepared = true;
        }
        LocalPlayerId = owner;
        // Opt-in trace: the first snapshots after a world build show whether the local HUD
        // state was left paused/menu-owned, which several scripted suites assert on.
        if (ValidateState && uiTraceFrames < 3)
        {
            uiTraceFrames++;
            Debug.Log("[CoopUiState] apply=" + uiTraceFrames + "; uis=" + game.uisBeforeMainUI + "; pause=" + game.pauseAllInteraction +
                "; actionPause=" + game.pauseActionOnly + "; mainGame=" + game.mainGameUI.activeSelf + "; menu=" + game.mainMenuUI.gameObject.activeSelf +
                "; playerInactive=" + game.playerInactive + "; observer=" + game.observer.gameObject.activeSelf +
                "; watchers=" + game.inputWatcher.inputWatchers.Count + "; inventory=" + game.inventoryUI.gameObject.activeSelf);
        }
        var records = (JArray)root["entities"];
        var alive = new HashSet<long>();
        long perfAllocate = CoopPerformanceProbe.Begin("Coop.ApplyAllocate");
        // Allocate every entity first; fields can point to an entity later in this packet.
        foreach (JObject r in records)
        {
            long id = (long)r["id"]; alive.Add(id);
            if (replicas.ContainsKey(id)) continue;
            Component c;
            int player = (int)r["player"];
            if (player == owner) { c = game.player; game.player.CoopPlayerId = owner; }
            else if ((string)r["kind"] == typeof(PlayerScript).FullName)
            {
                c = cachedSpawner.CreateReplica(player);
                createdReplicas.Add(c);
            }
            else if (r["anchor"].Type != JTokenType.Null && anchorObjects.TryGetValue((string)r["anchor"], out c)) { }
            else
            {
                Type t = typeof(CharacterStatus).Assembly.GetType((string)r["kind"], true);
                if (!game.Prefabs.TryGetValue(t, out UnityEngine.Object prefab))
                    throw new InvalidOperationException("No replica prefab for " + t);
                var obj = (GameObject)Instantiate(prefab);
                c = obj.GetComponent(t);
                createdReplicas.Add(c);
            }
            replicas[id] = c;
            // A replica that was just created must receive its full payload.
            appliedStates.Remove(id);
            foreach (Rigidbody body in c.GetComponentsInChildren<Rigidbody>(true))
            {
                if (!originalKinematic.ContainsKey(body)) originalKinematic[body] = body.isKinematic;
                body.isKinematic = true;
            }
            // Cosmetic code is refreshed explicitly; no replica executes original Update/trigger simulation.
            foreach (MonoBehaviour behaviour in c.GetComponentsInChildren<MonoBehaviour>(true))
                if (!(behaviour is PlayerScript))
                {
                    if (!originalEnabled.ContainsKey(behaviour)) originalEnabled[behaviour] = behaviour.enabled;
                    behaviour.enabled = false;
                }
        }
        CoopPerformanceProbe.End("applyAllocate", perfAllocate);
        var graph = applyGraph ?? (applyGraph = new CoopCachedStateGraph(UnityKey, UnityObject));
        long perfRows = CoopPerformanceProbe.Begin("Coop.ApplyRows");
        try { graph.ApplyRows(nodes); }
        finally { CoopPerformanceProbe.End("applyRows", perfRows); }
        timerDisplays.Clear();
        // Move parent rooms before their child entities receive absolute world poses.
        var movingRooms = CoopTopology.Rooms(game.map);
        for (int i = 0; i < movingRooms.Count; i++)
        {
            graph.RoomPose((byte[])root["rooms"][i], out Vector3 roomPosition, out Quaternion roomRotation, out Vector3? moverPosition);
            movingRooms[i].transform.SetPositionAndRotation(roomPosition, roomRotation);
            if (movingRooms[i] is UFOMover ufo)
            {
                Rigidbody body = ufo.actualMover.rigidbodyReference;
                if (!originalKinematic.ContainsKey(body)) originalKinematic[body] = body.isKinematic;
                body.isKinematic = true;
                if (!moverPosition.HasValue) throw new InvalidDataException("Missing mover pose");
                ufo.actualMover.transform.position = moverPosition.Value;
            }
        }
        long perfRecords = CoopPerformanceProbe.Begin("Coop.ApplyRecords");
        var motion = cachedMotion;
        uint generation = cachedWorld.Generation;
        // Local view state is restored after the authoritative fields are applied; read it
        // once instead of four times per replicated entity.
        float localYaw = game.player.horizontalPlaneRotation;
        float localMovementTrack = game.player.movementTrack;
        bool localVanity = game.player.vanityCamera;
        float localVanityDistance = game.player.vanityCameraDistance;
        // Per-apply attribution for the three per-entity costs. Two timestamp reads per call
        // keep the measurement much cheaper than a probe marker per replicated entity.
        long fieldsTicks = 0, appearanceTicks = 0, motionTicks = 0;
        foreach (JObject r in records)
        {
            long entityId = (long)r["id"];
            var c = replicas[entityId];
            if (!appliedStates.TryGetValue(entityId, out AppliedState applied))
                appliedStates[entityId] = applied = new AppliedState();
            CollectionProbe[] probes = Probes(c.GetType());
            JToken p = r["position"], q = r["rotation"];
            var position = new Vector3((float)p[0], (float)p[1], (float)p[2]);
            var rotation = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            bool tracked = motion.Snapshot(entityId, c, generation, (double)root["time"], position, rotation);
            motionTicks += System.Diagnostics.Stopwatch.GetTimestamp() - stamp;
            if (!tracked) c.transform.SetPositionAndRotation(position, rotation);
            // Field bodies and appearances travel in full every snapshot; skip the decode and
            // the Unity writes when this replica already holds exactly these bytes.
            var fieldBody = (byte[])((JObject)r["fields"])["b"];
            Hash(fieldBody, out long fieldHashA, out long fieldHashB);
            long collections = probes.Length == 0 ? 0 : CollectionFingerprint(c, probes);
            if (CoopCompat.FullApply || !applied.hasFields || applied.fieldsA != fieldHashA || applied.fieldsB != fieldHashB || applied.collections != collections)
            {
                applied.hasFields = true; applied.fieldsA = fieldHashA; applied.fieldsB = fieldHashB; applied.collections = collections;
                stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                graph.ApplyFields(c, (JObject)r["fields"]);
                fieldsTicks += System.Diagnostics.Stopwatch.GetTimestamp() - stamp;
            }
            if (c is CharacterStatus follower) follower.CoopFollowPlayerId = (int)r["followOwner"];
            if (c is StrikeableLocation location)
            {
                location.iconTexture = CoopTextureReference.Resolve((string)r["locationIcon"]) as Texture2D;
                if (location.mapMarked && r["locationIcon"].Type == JTokenType.String && location.iconTexture == null)
                    throw new InvalidOperationException("Map location icon could not be resolved: " + r["locationIcon"]);
            }
            if (c is CharacterStatus timed && r["timerDisplays"] is JArray displays)
                for (int i = 0; i < timed.timers.Count; i++) timerDisplays[timed.timers[i]] = (string)displays[i];
            if (c is CharacterStatus pictured && r["spriteTextures"] is JArray textures)
                for (int i = 0; i < pictured.spriteStack.Count; i++) pictured.spriteStack[i].sprite = CoopTextureReference.Resolve((string)textures[i]);
            if (c is CharacterStatus tinted && r["spriteColor"] is JArray tint)
                tinted.mainSpriteRenderer.material.color = new Color((float)tint[0], (float)tint[1], (float)tint[2], (float)tint[3]);
            if (c is CharacterStatus checkedPicture && r["textureChecks"] is JArray textureChecks)
                for (int i = 0; i < textureChecks.Count; i++)
                    if (textureChecks[i].Type == JTokenType.String && CoopTextureRecipe.PixelSignature(checkedPicture.spriteStack[i].sprite) != (string)textureChecks[i])
                        throw new InvalidOperationException("Generated texture pixel verification failed; entity=" + r["id"]);
            if (c == game.player)
            {
                game.player.movementTrack = localMovementTrack;
                // Host remote actors never enter the local TF camera. Keep the client's
                // actual mode until UpdateVanityCamera performs its enter/exit transition.
                game.player.vanityCamera = localVanity;
                game.player.vanityCameraDistance = localVanityDistance;
                if (!game.player.facingLocked && (game.player.currentAI.ObeyPlayerInput() ||
                    game.player.currentAI.currentState.immobilisedState || !GameSystem.settings.autopilotAutoFacing || localVanity))
                    game.player.horizontalPlaneRotation = localYaw;
            }
            c.gameObject.SetActive((bool)r["active"]);
            if (c == game.player)
            {
                bool observer = !c.gameObject.activeSelf;
                if (observer && !game.playerInactive)
                    game.observer.directTransformReference.SetPositionAndRotation(game.player.rotationTransformReference.position, game.player.rotationTransformReference.rotation);
                game.playerInactive = observer;
                game.observer.gameObject.SetActive(observer);
                game.activePrimaryCameraTransform = observer ? game.observer.directTransformReference : game.player.rotationTransformReference;
            }
            if (r["appearance"] is JToken appearanceToken && appearanceToken.Type == JTokenType.Bytes)
            {
                byte[] appearanceBytes = (byte[])appearanceToken;
                Hash(appearanceBytes, out long appearanceA, out long appearanceB);
                if (CoopCompat.FullApply || !applied.hasAppearance || applied.appearanceA != appearanceA || applied.appearanceB != appearanceB)
                {
                    applied.hasAppearance = true; applied.appearanceA = appearanceA; applied.appearanceB = appearanceB;
                    stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    // The decode also records this replica's texture keys so the throttled
                    // generated-texture lease refresh can still renew them without re-decoding.
                    applied.appearanceKeys.Clear();
                    CoopAppearance.Apply(c, CoopAppearance.DecodeBinary(appearanceBytes), applied.appearanceKeys);
                    appearanceTicks += System.Diagnostics.Stopwatch.GetTimestamp() - stamp;
                }
            }
            else if (r["appearance"] is JArray decodedAppearance)
            {
                stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                CoopAppearance.Apply(c, decodedAppearance);
                appearanceTicks += System.Diagnostics.Stopwatch.GetTimestamp() - stamp;
            }
        }
        CoopPerformanceProbe.AddStage("applyEntityFields", fieldsTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        CoopPerformanceProbe.AddStage("applyAppearance", appearanceTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        CoopPerformanceProbe.AddStage("applyMotion", motionTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        CoopPerformanceProbe.End("applyRecords", perfRecords);
        foreach (string name in GameFields)
        {
            FieldInfo f = typeof(GameSystem).GetField(name);
            if (f != null && root["game"][name] != null) f.SetValue(game, graph.Read(root["game"][name], f.FieldType));
        }
        var replicatedRooms = CoopTopology.Rooms(game.map);
        for (int i = 0; i < replicatedRooms.Count; i++)
            graph.ApplyRoom(replicatedRooms[i], (byte[])root["rooms"][i]);
        foreach (JObject r in records)
            if (r["check"]?.Type == JTokenType.String && replicas[(long)r["id"]] is CharacterStatus status)
            {
                string actual = StateSignature(status);
                if (actual != (string)r["check"])
                {
                    string differences = "diagnostics disabled";
                    if (r["fieldChecks"] is JObject expectedFields)
                    {
                        var fields = FieldSignatures(status);
                        differences = string.Join(",", expectedFields.Properties().Where(p => (string)fields[p.Name] != (string)p.Value).Select(p => p.Name));
                    }
                    if (r["speciesChecks"] is JObject expectedSpecies)
                    {
                        var species = SpeciesChecks(status.npcType);
                        differences += "; species=" + string.Join(",", expectedSpecies.Properties().Where(p => (string)species[p.Name] != (string)p.Value).Select(p => p.Name));
                    }
                    throw new InvalidOperationException("Character state verification failed; entity=" + r["id"] + "; fields=" + differences + "; expected=" + r["check"] + "; actual=" + actual);
                }
            }
        staleAppliedStates.Clear();
        foreach (long id in replicas.Keys) if (!alive.Contains(id)) staleAppliedStates.Add(id);
        foreach (long id in staleAppliedStates) appliedStates.Remove(id);
        foreach (long id in replicas.Keys.Where(id => !alive.Contains(id)).ToList())
        {
            Component c = replicas[id];
            if (c != null) c.gameObject.SetActive(false);
            if (c != null && createdReplicas.Remove(c)) Destroy(c.gameObject);
            replicas.Remove(id);
        }
        CoopPerformanceProbe.End("applyFields", perfFields);
        cachedMotion.Retain(alive);
        long perfPresentation = CoopPerformanceProbe.Begin("Coop.Presentation");
        foreach (Component c in replicas.Values)
        {
            if (c is CharacterStatus character && character.npcType != null && character.currentAI != null)
            {
                character.latestRigidBodyPosition = character.transform.position;
                foreach (SpriteSettingDefinition sprite in character.spriteStack)
                {
                    if (sprite.sprite != null) continue;
                    if (string.IsNullOrEmpty(sprite.originalSpriteString)) continue;
                    var loaded = new SpriteSettingDefinition(character, sprite.originalSpriteString, sprite.key,
                        sprite.heightAdjust, sprite.overrideHover, sprite.extraXRotation, sprite.extraHeadOffset,
                        sprite.extraStatusSectionOffset, sprite.flipX, sprite.explicitSpritePath, sprite.explicitPathCustomSprite);
                    sprite.sprite = loaded.sprite;
                }
                character.RefreshSpriteDisplay(); character.UpdateStatus();
                if (character is NPCScript npc) { npc.UpdateSpriteFacing(); npc.UpdateTimerDisplay(); }
                if (character == game.player) { game.player.UpdateTimerDisplay(); game.player.UpdateStamina(); }
            }
            if (c is ItemOrb orb && orb.containedItem != null) orb.UpdateSprite();
        }
        CoopPerformanceProbe.End("presentation", perfPresentation);
        if (game.postGame && root["result"]?.Type == JTokenType.String)
        {
            game.resultsText.text = (string)root["result"];
            if (!resultPresented) { game.resultsDisplay.SetActive(true); game.SwapToAndFromMainGameUI(false); resultPresented = true; }
        }
        else resultPresented = false;
        long perfTail = CoopPerformanceProbe.Begin("Coop.ApplyTail");
        // Generated texture leases only need to be renewed before the 60s definition
        // eviction; pruning every snapshot cost a full renderer walk per entity.
        if (Time.unscaledTime >= nextTextureMaintenance)
        {
            nextTextureMaintenance = Time.unscaledTime + 5f;
            retainedTextures.Clear();
            foreach (Component component in replicas.Values)
            {
                if (component is StrikeableLocation location) retainedTextures.Add(location.iconTexture);
                if (component is CharacterStatus character) retainedTextures.AddRange(character.spriteStack.Select(sprite => sprite.sprite));
                else
                {
                    retainedRenderers.Clear();
                    component.gameObject.GetComponentsInChildren(true, retainedRenderers);
                    for (int i = 0; i < retainedRenderers.Count; i++)
                    {
                        Material[] materials = retainedRenderers[i].sharedMaterials;
                        for (int m = 0; m < materials.Length; m++)
                            if (materials[m] != null && materials[m].HasProperty(MainTexProperty)) retainedTextures.Add(materials[m].mainTexture);
                    }
                }
            }
            CoopTextureReference.PrunePixels(retainedTextures);
            CoopTextureRecipe.PruneResolved(retainedTextures);
            CoopTextureRecipe.Collect(SnapshotTextures(root)); // Touch the complete dependency closure before lease eviction.
            appliedAppearanceKeys.Clear();
            foreach (AppliedState state in appliedStates.Values) appliedAppearanceKeys.AddRange(state.appearanceKeys);
            CoopTextureRecipe.Collect(appliedAppearanceKeys);
        }
        if (game.inventoryUI.gameObject.activeSelf)
        {
            long selected = CoopInventory.Identity(game.inventoryUI.displayedItem);
            game.inventoryUI.displayedItem = game.player.currentItems.FirstOrDefault(x => CoopInventory.Identity(x) == selected);
            game.inventoryUI.UpdateDisplay();
        }
        // A UI reply may arrive before the snapshot carrying the changed inventory.
        // Repaint bridged grids after applying it, otherwise transferred items leave
        // stale icons until the player clicks another control.
        cachedUiBridge.RefreshClientView();
        CoopPerformanceProbe.End("applyTail", perfTail);
        if (Time.unscaledTime >= nextTrace)
        {
            nextTrace = Time.unscaledTime + 5f;
            Debug.Log("[Coop] State applied; rev=" + receivedRevision + "; owner=" + owner + "; entities=" + replicas.Count +
                "; hp=" + game.player.hp + "; type=" + game.player.npcType.name + "; timers=" + game.player.timers.Count);
        }
    }
    public static byte[] Compress(byte[] source)
    {
        using (var output = new MemoryStream())
        { using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, true)) gzip.Write(source, 0, source.Length); return output.ToArray(); }
    }
    private static byte[] Decompress(byte[] source)
    {
        using (var input = new MemoryStream(source)) using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            var buffer = new byte[8192]; int length;
            while ((length = gzip.Read(buffer, 0, buffer.Length)) != 0)
            { if (output.Length + length > 64 * 1024 * 1024) throw new InvalidDataException("Expanded state too large"); output.Write(buffer, 0, length); }
            return output.ToArray();
        }
    }
}
