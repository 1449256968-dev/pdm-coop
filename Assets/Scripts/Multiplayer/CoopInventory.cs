using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Mirror;
using UnityEngine;

public struct CoopInventoryIntent : NetworkMessage { public long item; public byte action; public uint sequence; }

public sealed class CoopInventory : MonoBehaviour
{
    public static CoopInventory Instance { get; private set; }
    private sealed class ItemIdentity { public long value; }
    private ConditionalWeakTable<Item, ItemIdentity> ids = new ConditionalWeakTable<Item, ItemIdentity>();
    private readonly Dictionary<long, Item> items = new Dictionary<long, Item>();
    private readonly Dictionary<int, uint> sequences = new Dictionary<int, uint>();
    private uint sequence;
    private void Awake() { Instance = this; }
    public void StartServer() { NetworkServer.RegisterHandler<CoopInventoryIntent>(Receive); ids = new ConditionalWeakTable<Item, ItemIdentity>(); items.Clear(); sequences.Clear(); }
    public void StopServer() { NetworkServer.UnregisterHandler<CoopInventoryIntent>(); ids = new ConditionalWeakTable<Item, ItemIdentity>(); items.Clear(); sequences.Clear(); }
    public void Release(int id) { sequences.Remove(id); }
    public static long Identity(Item item)
    {
        if (Instance == null || item == null) return 0;
        if (Instance.ids.TryGetValue(item, out ItemIdentity known)) return known.value;
        if (!NetworkServer.active) return 0;
        long id = Instance.GetComponent<CoopSession>().AllocateEntityId();
        Instance.ids.Add(item, new ItemIdentity { value = id }); Instance.items[id] = item;
        return id;
    }
    public static void Bind(Item item, long id)
    {
        if (Instance == null || item == null || id <= 0) return;
        Instance.items[id] = item;
        Instance.ids.Remove(item); Instance.ids.Add(item, new ItemIdentity { value = id });
    }
    public static bool Send(Item item, byte action)
    {
        if (!CoopReplication.IsReplica) return false;
        long id = Identity(item);
        if (id > 0) NetworkClient.Send(new CoopInventoryIntent { item = id, action = action, sequence = ++Instance.sequence });
        return true;
    }
    private void Receive(NetworkConnectionToClient connection, CoopInventoryIntent intent)
    {
        int id = GetComponent<CoopSession>().GetPlayerId(connection);
        if (!GetComponent<CoopWorld>().SharedWorldRunning || !GameSystem.instance.gameInProgress ||
            !GetComponent<CoopPlayerRegistry>().TryGetPlayer(id, out PlayerScript actor) || actor == null ||
            !items.TryGetValue(intent.item, out Item item) || !actor.currentItems.Contains(item) ||
            intent.action > 2 || (sequences.TryGetValue(id, out uint old) && intent.sequence <= old)) return;
        using (new CoopActorContext(actor))
        {
            // ObeyPlayerInput reads the scoped Settings preferences. Evaluating it
            // before entering this actor's context incorrectly used the host's AI mode.
            if (!actor.currentAI.ObeyPlayerInput()) return;
            sequences[id] = intent.sequence;
            var inventory = GameSystem.instance.inventoryUI;
            inventory.displayedItem = item;
            if (intent.action == 0) inventory.SelectItem();
            else if (intent.action == 1) inventory.Drop();
            else inventory.Destroy();
        }
        Debug.Log("[Coop] Inventory intent executed; player=" + id + "; action=" + intent.action + "; item=" + item.name);
    }
}
