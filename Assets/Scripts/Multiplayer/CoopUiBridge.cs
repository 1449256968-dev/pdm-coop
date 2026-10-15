using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mirror;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public struct CoopUiState : NetworkMessage { public uint token; public string kind; public string fields; public bool open; }
public struct CoopUiIntent : NetworkMessage { public uint token; public string method; public long item; public string recipe; public uint sequence; }

// Original trading, crafting and merchant screens keep their callbacks and rules on the host.
// A client only receives display state and may invoke a fixed set of callbacks in its own UI session.
public sealed class CoopUiBridge : MonoBehaviour
{
    private sealed class Session { public uint token; public uint sequence; public MonoBehaviour ui; public PlayerScript actor; public float lastIntent; }
    public static CoopUiBridge Instance { get; private set; }
    private readonly Dictionary<int, Session> sessions = new Dictionary<int, Session>();
    private readonly List<int> staleSessions = new List<int>();
    // A bridge session outlives its screen only until this grace period. Without it a
    // client that closes a trading/merchant/alchemy screen locally never sends another
    // intent, the session entry stays forever, and RunOriginal skips that player's whole
    // update: no movement, no keys, no view refresh on hosts and on the client alike.
    private const float SessionGrace = 30f;
    private uint token, clientToken, sequence;
    private bool closeSent;
    private string clientKind;
    private static readonly Dictionary<string, HashSet<string>> Methods = new Dictionary<string, HashSet<string>> {
        ["TradingUI"] = new HashSet<string> { "PlayerPressedItem", "TargetPressedItem", "GiveItem", "TakeItem", "NPCEquipItem", "RevealTraitor", "Back" },
        ["MerchantUI"] = new HashSet<string> { "ShopperPressedItem", "MerchantPressedItem", "Trade", "Steal", "ForceAnger", "NPCEquipItem", "Back" },
        ["AlchemyUI"] = new HashSet<string> { "PressedItemOption", "PressedItemIngredient", "Mix", "ApplyQuickRecipe", "Back" },
        ["GunDeskUI"] = new HashSet<string> { "PressedItemOption", "PressedItemIngredient", "Mix", "ApplyQuickRecipe", "Back" }
    };
    private void Awake() { Instance = this; }
    public void StartServer() { NetworkServer.RegisterHandler<CoopUiIntent>(Receive); sessions.Clear(); }
    public void StopServer() { NetworkServer.UnregisterHandler<CoopUiIntent>(); sessions.Clear(); }
    public void StartClient() { NetworkClient.RegisterHandler<CoopUiState>(Display); clientToken = 0; }
    public void StopClient() { NetworkClient.UnregisterHandler<CoopUiState>(); clientToken = 0; clientKind = null; }
    public void Release(int id) { sessions.Remove(id); }
    public static bool HasSession(int id) => Instance != null && Instance.sessions.ContainsKey(id);
    public static void Open(MonoBehaviour ui)
    {
        var actor = CoopActorContext.Current;
        if (actor == null || !actor.IsRemotePlayer || !NetworkServer.active || Instance == null) return;
        var session = new Session { token = ++Instance.token, ui = ui, actor = actor, lastIntent = Time.unscaledTime };
        Instance.sessions[actor.CoopPlayerId] = session;
        Instance.Publish(session);
    }
    private void LateUpdate()
    {
        bool server = NetworkServer.active;
        if (!server && CoopReplication.IsReplica && clientToken != 0)
        {
            // The host cannot see that this client closed the screen locally, so the
            // client tells it. Otherwise the host session stays and blocks this
            // player's whole update (no movement, no keys) until they reconnect.
            MonoBehaviour local = LocalUI(clientKind);
            if (local != null && !local.gameObject.activeSelf && !closeSent)
            {
                closeSent = true;
                NetworkClient.Send(new CoopUiIntent { token = clientToken, method = "Back", sequence = ++sequence });
            }
            return;
        }
        // The authoritative screen is the original UI object itself. It is re-shown
        // dormant for each actor context, so a screen the game already closed stays
        // closed and must not keep the player's input blocked.
        if (!server || sessions.Count == 0) return;
        staleSessions.Clear();
        foreach (var pair in sessions)
        {
            Session session = pair.Value;
            bool closed = session.ui == null || !session.ui.gameObject.activeSelf;
            if (closed || Time.unscaledTime - session.lastIntent > SessionGrace) staleSessions.Add(pair.Key);
        }
        foreach (int id in staleSessions)
        {
            Session session = sessions[id];
            sessions.Remove(id);
            // Tell that client to close the bridged screen and restore its game UI.
            if (session.ui != null && session.actor != null &&
                GetComponent<CoopSession>().TryGetConnection(session.actor.CoopPlayerId, out var connection))
                connection.Send(new CoopUiState { token = session.token, kind = session.ui.GetType().Name, fields = "{}", open = false });
            Debug.Log("[Coop] Gameplay UI session closed for player " + id + "; screen=" + (session.ui == null ? "destroyed" : session.ui.GetType().Name));
        }
    }
    private void Publish(Session session)
    {
        if (!GetComponent<CoopSession>().TryGetConnection(session.actor.CoopPlayerId, out var connection)) return;
        var replication = GetComponent<CoopReplication>();
        var graph = new CoopStateGraph(replication.ReferenceKey, replication.ResolveReference);
        connection.Send(new CoopUiState { token = session.token, kind = session.ui.GetType().Name,
            fields = graph.CaptureFields(session.ui).ToString(Formatting.None), open = session.ui.gameObject.activeSelf });
    }
    public static bool Send(MonoBehaviour ui, string method, Item item = null, AlchemyRecipe recipe = null)
    {
        if (!CoopReplication.IsReplica) return false;
        if (Instance.clientToken != 0 && ui.GetType().Name == Instance.clientKind)
            NetworkClient.Send(new CoopUiIntent { token = Instance.clientToken, method = method,
                item = CoopInventory.Identity(item), recipe = recipe?.name, sequence = ++Instance.sequence });
        return true;
    }
    private MonoBehaviour LocalUI(string kind)
    {
        var game = GameSystem.instance;
        if (kind == "TradingUI") return game.tradingUI;
        if (kind == "MerchantUI") return game.merchantUI;
        if (kind == "AlchemyUI") return game.alchemyUI;
        if (kind == "GunDeskUI") return game.GunDeskUI;
        return null;
    }
    public void RefreshClientView()
    {
        if (NetworkServer.active || clientToken == 0) return;
        MonoBehaviour ui = LocalUI(clientKind);
        if (ui != null && ui.gameObject.activeInHierarchy)
        {
            // UI messages use an independent graph from world snapshots. Match items
            // by their network IDs so selection/equipment highlights use world identity.
            if (ui is TradingUI trading)
            {
                trading.playerDisplayedItem = WorldItem(GameSystem.instance.player, trading.playerDisplayedItem);
                trading.targetDisplayedItem = WorldItem(trading.target, trading.targetDisplayedItem);
            }
            ui.GetType().GetMethod("UpdateDisplay", Type.EmptyTypes).Invoke(ui, null);
        }
    }
    private static Item WorldItem(CharacterStatus owner, Item item)
    {
        if (item == null || owner == null) return item;
        long id = CoopInventory.Identity(item);
        Item current = owner.currentItems.FirstOrDefault(candidate => CoopInventory.Identity(candidate) == id);
        // UI state can legitimately precede the snapshot that adds this item.
        return current ?? item;
    }
    private void Display(CoopUiState message)
    {
        if (NetworkServer.active || !CoopReplication.IsReplica || message.fields == null || message.fields.Length > 1024 * 1024) return;
        MonoBehaviour ui = LocalUI(message.kind);
        if (ui == null) return;
        var game = GameSystem.instance;
        if (!message.open)
        {
            ui.gameObject.SetActive(false); game.inputWatcher.inputWatchers.Remove(ui);
            game.SwapToAndFromMainGameUI(true); clientToken = 0; clientKind = null; closeSent = false; return;
        }
        var replication = GetComponent<CoopReplication>();
        var graph = new CoopStateGraph(replication.ReferenceKey, replication.ResolveReference);
        graph.ApplyFields(ui, JObject.Parse(message.fields));
        if (clientToken != message.token)
        {
            closeSent = false;
            game.SwapToAndFromMainGameUI(false); ui.gameObject.SetActive(true);
            if (!game.inputWatcher.inputWatchers.Contains(ui)) game.inputWatcher.inputWatchers.Add(ui);
        }
        clientKind = message.kind; clientToken = message.token;
        RefreshClientView();
        if (Environment.GetCommandLineArgs().Contains("-coopScenario")) Send(ui, "Back");
    }
    private void Receive(NetworkConnectionToClient connection, CoopUiIntent intent)
    {
        int id = GetComponent<CoopSession>().GetPlayerId(connection);
        if (!sessions.TryGetValue(id, out Session session) || session.actor == null || intent.token != session.token ||
            intent.sequence <= session.sequence || !Methods[session.ui.GetType().Name].Contains(intent.method)) return;
        session.sequence = intent.sequence;
        session.lastIntent = Time.unscaledTime;
        MethodInfo method;
        object[] args;
        if (intent.recipe != null)
        {
            // Recipe delegates never come from the client; resolve an existing local definition.
            AlchemyRecipe recipe = Recipes().FirstOrDefault(r => r.name == intent.recipe);
            if (recipe == null) return;
            method = session.ui.GetType().GetMethod(intent.method, new[] { typeof(AlchemyRecipe) }); args = new object[] { recipe };
        }
        else if (intent.item != 0)
        {
            Item item = null;
            // TradingUI builds its grids from character inventories, not Item lists
            // stored on the UI. Resolve only the inventory exposed by this callback.
            if (session.ui is TradingUI trading)
            {
                IEnumerable<Item> source = intent.method == "PlayerPressedItem" ? session.actor.currentItems :
                    intent.method == "TargetPressedItem" && trading.target != null ? trading.target.currentItems : null;
                if (source != null) item = source.FirstOrDefault(candidate => CoopInventory.Identity(candidate) == intent.item);
            }
            foreach (FieldInfo field in session.ui.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (field.GetValue(session.ui) is IEnumerable list)
                    foreach (object value in list) if (value is Item candidate && CoopInventory.Identity(candidate) == intent.item) item = candidate;
            if (item == null) return;
            method = session.ui.GetType().GetMethod(intent.method, new[] { typeof(Item) }); args = new object[] { item };
        }
        else { method = session.ui.GetType().GetMethod(intent.method, Type.EmptyTypes); args = null; }
        if (method == null) return;
        using (new CoopActorContext(session.actor)) method.Invoke(session.ui, args);
        Debug.Log("[Coop] Gameplay UI callback executed; player=" + id + "; screen=" + session.ui.GetType().Name + "; method=" + intent.method);
        Publish(session);
        if (!session.ui.gameObject.activeSelf) sessions.Remove(id);
    }
    private static IEnumerable<AlchemyRecipe> Recipes()
    {
        foreach (Type type in typeof(AlchemyRecipe).Assembly.GetTypes())
        {
            if (!(type.Name.StartsWith("Alchemy") || type.Name.StartsWith("GunDesk"))) continue;
            foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                if (typeof(IEnumerable<AlchemyRecipe>).IsAssignableFrom(f.FieldType) && f.GetValue(null) is IEnumerable<AlchemyRecipe> recipes)
                    foreach (var recipe in recipes) yield return recipe;
        }
    }
}
