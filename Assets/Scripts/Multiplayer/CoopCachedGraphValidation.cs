using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public sealed class CoopCachedGraphValidation : MonoBehaviour
{
    private sealed class Node
    {
        public Node next, alias;
        public List<Node> list;
        public int[,] matrix;
        public float hp;
        public NPCType species;
        public Item item;
        public Vector2 v2;
        public Vector3 v3;
        public Vector4 v4;
        public Quaternion rotation;
        public Rect rect;
        public Color color;
        public Bounds bounds;
        public LayerMask mask;
        public object originalAppearance;
        public Dictionary<int, int> lookup;
        public HashSet<int> unique;
    }
    private bool done;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopGraphValidate"))
            new GameObject("Coop Cached Graph Validation").AddComponent<CoopCachedGraphValidation>();
    }
    private void Update()
    {
        if (done || GameSystem.instance == null || !GameSystem.instance.gameInProgress) return;
        done = true;
        var a = new Node { hp = 12.5f, matrix = new int[2, 3], species = NPCType.CreateDuplicate(Human.npcType), item = Items.HealthPotion.CreateInstance() };
        var b = new Node { hp = 3 }; a.next = b; a.alias = b; b.next = a; a.list = new List<Node> { a, b, b };
        a.v2 = new Vector2(-1.25f, 3.5f); a.v3 = new Vector3(13, -27, 0.125f); a.v4 = new Vector4(1, 2, 3, 4);
        a.rotation = Quaternion.Euler(45, 125, 0); a.rect = new Rect(-4, 8, 0.125f, 300);
        a.color = new Color(0.1f, 0.2f, 0.3f, 0.4f); a.bounds = new Bounds(a.v3, new Vector3(1, 2, 3)); a.mask = 123;
        a.originalAppearance = new { Saved_SpiritName = "Saved spirit", spriteName = "Spirit", usedImageSet = "Mei" };
        a.lookup = new Dictionary<int, int> { { 4, 5 }, { 6, 7 } }; a.unique = new HashSet<int> { 2, 3 };
        var sender = new CoopCachedStateGraph(_ => null, _ => null);
        var receiver = new CoopCachedStateGraph(_ => null, _ => null);
        sender.BeginCapture(); var token = sender.Write(a); var first = sender.FinishCapture();
        receiver.ApplyRows(CoopCachedStateGraph.DecodeRows(CoopCachedStateGraph.EncodeRows(first)));
        var result = (Node)receiver.Read(token, typeof(Node));
        if (!ReferenceEquals(result.next.next, result) || !ReferenceEquals(result.next, result.alias) || !ReferenceEquals(result.list[1], result.list[2]))
            throw new InvalidOperationException("Shared references/cycles were not preserved");
        if (result.hp != a.hp || result.species.name != a.species.name || result.item.GetType() != a.item.GetType())
            throw new InvalidOperationException("Species/item factories or primitive data differ");
        if (!result.v2.Equals(a.v2) || !result.v3.Equals(a.v3) || !result.v4.Equals(a.v4) || !result.rotation.Equals(a.rotation) ||
            !result.rect.Equals(a.rect) || !result.color.Equals(a.color) || !result.bounds.Equals(a.bounds) || result.mask.value != a.mask.value)
            throw new InvalidOperationException("Fixed geometry schema changed values");
        var originalGraph = new CoopStateGraph(_ => null, _ => null);
        if (!Newtonsoft.Json.Linq.JToken.DeepEquals(originalGraph.Write(a.originalAppearance),
            new CoopStateGraph(_ => null, _ => null).Write(result.originalAppearance)))
            throw new InvalidOperationException("Immutable spirit appearance DTO was not restored");
        sender.BeginCapture(); sender.Write(a); var unchanged = sender.FinishCapture();
        if (first.Any(row => unchanged.Single(other => other.id == row.id).version != row.version))
            throw new InvalidOperationException("Unchanged object versions advanced");
        result.list.Add(result); result.matrix[0, 0] = 99;
        result.lookup.Remove(4); result.lookup[6] = 8; result.unique.Clear(); result.unique.Add(99);
        receiver.ApplyRows(unchanged);
        if (result.list.Count != 3 || result.matrix[0, 0] != 0)
            throw new InvalidOperationException("Local presentation collection mutation survived an unchanged authoritative snapshot");
        if (result.lookup[4] != 5 || result.lookup[6] != 7 || !result.unique.SetEquals(new[] { 2, 3 }))
            throw new InvalidOperationException("Dictionary/set mutation was not detected and restored");
        b.hp = 19; a.matrix[1, 2] = 7; a.species.hp = Human.npcType.hp + 17;
        sender.BeginCapture(); token = sender.Write(a); var changed = sender.FinishCapture();
        receiver.ApplyRows(changed); var updated = (Node)receiver.Read(token, typeof(Node));
        if (!ReferenceEquals(result, updated) || updated.next.hp != 19 || updated.matrix[1, 2] != 7 || updated.species.hp != a.species.hp)
            throw new InvalidOperationException("Changed children or stable instances were not restored");
        var lateJoin = new CoopCachedStateGraph(_ => null, _ => null); lateJoin.ApplyRows(changed);
        var joined = (Node)lateJoin.Read(token, typeof(Node));
        if (joined.next.hp != 19 || !ReferenceEquals(joined.next.next, joined)) throw new InvalidOperationException("Full snapshot requires an earlier baseline");
        a.species.hp = Human.npcType.hp;
        sender.BeginCapture(); token = sender.Write(a); receiver.ApplyRows(sender.FinishCapture());
        if (((Node)receiver.Read(token, typeof(Node))).species.hp != Human.npcType.hp)
            throw new InvalidOperationException("Species override removal retained the previous value");
        Debug.Log("[CoopGraphTest] PASS: cycles/aliases, 2D arrays, species/item factories, stable unchanged versions, changed children, and late join without prior packets");
        enabled = false;
    }
}
