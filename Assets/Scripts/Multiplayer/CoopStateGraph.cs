using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Wire graph for the recovered game's polymorphic state. Constructors/Activate are never
// invoked on replicas. References are restored before fields, including cycles and shared items.
public sealed class CoopStateGraph
{
    private sealed class CallbackToken { public string method; public object target; }
    private sealed class Identity : IEqualityComparer<object>
    {
        public new bool Equals(object a, object b) => ReferenceEquals(a, b);
        public int GetHashCode(object a) => RuntimeHelpers.GetHashCode(a);
    }
    private readonly Dictionary<object, int> written = new Dictionary<object, int>(new Identity());
    private readonly Dictionary<int, object> read = new Dictionary<int, object>();
    private readonly Dictionary<object, string> catalog;
    private readonly Dictionary<string, object> catalogByKey;
    private readonly Func<UnityEngine.Object, string> encodeUnity;
    private readonly Func<string, UnityEngine.Object> decodeUnity;
    private readonly List<RoomData> rooms;
    private struct Coordinate { public int room, node; }
    private readonly Dictionary<PathNode, Coordinate> nodeCoordinates;
    private sealed class WorldCatalog
    {
        public readonly Dictionary<object, string> byObject = new Dictionary<object, string>(new Identity());
        public readonly Dictionary<string, object> byKey = new Dictionary<string, object>();
        public readonly Dictionary<PathNode, Coordinate> nodes = new Dictionary<PathNode, Coordinate>();
        public readonly Dictionary<string, NPCType> species = new Dictionary<string, NPCType>();
    }
    private static Map catalogMap;
    private static WorldCatalog worldCatalog;
    public static void InvalidateDefinitions() { catalogMap = null; worldCatalog = null; }
    public bool TryDefinition(object value, out string key) => catalog.TryGetValue(value, out key);
    public object Definition(string key) => catalogByKey.TryGetValue(key, out object value) ? value : throw new InvalidOperationException("Missing definition " + key);
    public static bool AllowedType(Type type) => Allowed(type);
    public static NPCType Species(string name) => SpeciesSource(name);
    public static object Callback(string method, object target) => new CallbackToken { method = method, target = target };
    public static bool DescribeCallback(object value, out string method, out object target)
    {
        if (value is CallbackToken token) { method = token.method; target = token.target; return true; }
        if (value is Delegate function) { method = function.Method.DeclaringType.FullName + ":" + function.Method.Name; target = function.Target; return true; }
        method = null; target = null; return false;
    }
    public int[] Coordinates(PathNode node)
    {
        Coordinates(node, out int room, out int index);
        return new[] { room, index };
    }
    public void Coordinates(PathNode node, out int room, out int index)
    {
        if (!nodeCoordinates.TryGetValue(node, out Coordinate coordinate) ||
            coordinate.room < 0 || coordinate.room >= rooms.Count || !ReferenceEquals(rooms[coordinate.room], node.associatedRoom) ||
            coordinate.node < 0 || coordinate.node >= node.associatedRoom.pathNodes.Count || !ReferenceEquals(node.associatedRoom.pathNodes[coordinate.node], node))
        {
            coordinate = new Coordinate { room = rooms.IndexOf(node.associatedRoom), node = node.associatedRoom.pathNodes.IndexOf(node) };
            nodeCoordinates[node] = coordinate;
        }
        room = coordinate.room; index = coordinate.node;
    }
    public PathNode Node(int room, int node) => room >= 0 && node >= 0 ? rooms[room].pathNodes[node] : null;
    private readonly bool binaryLeaves;
    private readonly CoopFrozenGraph frozen;
    private static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
    private static readonly Dictionary<FieldInfo, string> fieldKeys = new Dictionary<FieldInfo, string>();
    private static readonly Dictionary<string, Type> wireTypes = new Dictionary<string, Type>();
    private static readonly Dictionary<Type, bool> allowedTypes = new Dictionary<Type, bool>();
    private static readonly Type[] definitionTypes = { typeof(NPCType), typeof(ItemData), typeof(Items), typeof(Weapons), typeof(Guns), typeof(SpecialItems), typeof(Trap), typeof(StandardActions) };
    private const int CommonTypeBase = 1000000;
    private static readonly Type[] commonTypes = {
        typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Quaternion), typeof(Rect), typeof(Color), typeof(Bounds), typeof(LayerMask),
        typeof(Vector2Int), typeof(Vector3Int),
        typeof(List<CharacterStatus>), typeof(List<PathConnection>), typeof(List<PathNode>), typeof(List<RoomData>),
        typeof(List<StrikeableLocation>), typeof(List<ItemOrb>), typeof(List<ItemCrate>), typeof(List<Item>), typeof(List<Timer>), typeof(List<SpriteSettingDefinition>)
    };
    private static readonly Dictionary<Type, int> commonTypeIds = MakeCommonTypeIds();
    private static Dictionary<Type, int> MakeCommonTypeIds()
    {
        var ids = new Dictionary<Type, int>();
        for (int i = 0; i < commonTypes.Length; i++) ids.Add(commonTypes[i], CommonTypeBase + i);
        return ids;
    }

    public CoopStateGraph(Func<UnityEngine.Object, string> encode, Func<string, UnityEngine.Object> decode, bool binaryLeaves = false, CoopFrozenGraph frozen = null)
    {
        encodeUnity = encode;
        decodeUnity = decode;
        this.binaryLeaves = binaryLeaves;
        this.frozen = frozen;
        rooms = CoopTopology.Rooms(GameSystem.instance.map);
        bool buildCatalog = worldCatalog == null || !ReferenceEquals(catalogMap, GameSystem.instance.map);
        if (buildCatalog)
        {
            catalogMap = GameSystem.instance.map; worldCatalog = new WorldCatalog();
            foreach (NPCType source in NPCType.types) if (!worldCatalog.species.ContainsKey(source.name)) worldCatalog.species[source.name] = source;
            foreach (NPCType source in NPCType.baseTypes) if (!worldCatalog.species.ContainsKey(source.name)) worldCatalog.species[source.name] = source;
            for (int i = 0; i < rooms.Count; i++)
                for (int j = 0; j < rooms[i].pathNodes.Count; j++) worldCatalog.nodes[rooms[i].pathNodes[j]] = new Coordinate { room = i, node = j };
        }
        catalog = worldCatalog.byObject; catalogByKey = worldCatalog.byKey; nodeCoordinates = worldCatalog.nodes;
        if (!buildCatalog) return;
        foreach (Type type in definitionTypes)
        {
            // Catalog only initialized gameplay definitions; avoid triggering unrelated static initializers.
            if (!(type == typeof(NPCType) || type == typeof(ItemData) || type == typeof(Items) ||
                  type == typeof(Weapons) || type == typeof(Guns) || type == typeof(SpecialItems) ||
                  type == typeof(Trap) || type == typeof(StandardActions))) continue;
            foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                object value = f.GetValue(null);
                string key = type.FullName + "." + f.Name;
                if (value is IList list)
                    for (int i = 0; i < list.Count; i++) AddCatalog(list[i], key + "/" + i);
                else AddCatalog(value, key);
            }
        }
    }
    private void AddCatalog(object value, string key)
    {
        if (!(value is NPCType || value is Item || value is Trap || value is global::Action)) return;
        if (!catalog.ContainsKey(value)) catalog.Add(value, key);
        catalogByKey[key] = value;
    }
    public static FieldInfo[] Fields(Type type)
    {
        if (fieldCache.TryGetValue(type, out var fields)) return fields;
        var result = new List<FieldInfo>();
        for (Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(Component) && t != typeof(UnityEngine.Object); t = t.BaseType)
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (f.IsStatic || (f.IsInitOnly && typeof(Component).IsAssignableFrom(type)) || f.IsNotSerialized || f.Name.Contains("k__BackingField") ||
                    typeof(Delegate).IsAssignableFrom(f.FieldType) || f.FieldType == typeof(Type) ||
                    (typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType) && !DomainReference(f.FieldType)) ||
                    f.FieldType == typeof(Coroutine) || f.Name.StartsWith("reused") || f.Name.StartsWith("cached") ||
                    f.Name.StartsWith("lastTimer") || f.Name.EndsWith("PropertyBlock") ||
                    f.Name == "sprite" || f.Name == "timerImage" || f.Name == "timerText" || f.Name == "usableItems" ||
                    PresentationType(f.FieldType)) continue;
                // Components retain their own Unity wiring; plain state classes also carry private state.
                if (typeof(Component).IsAssignableFrom(type) && !f.IsPublic) continue;
                result.Add(f);
            }
        fields = result.ToArray();
        fieldCache[type] = fields;
        return fields;
    }
    private static bool DomainReference(Type t) => typeof(CharacterStatus).IsAssignableFrom(t) ||
        typeof(StrikeableLocation).IsAssignableFrom(t) || typeof(RoomData).IsAssignableFrom(t) ||
        t == typeof(ItemOrb) || t == typeof(ItemCrate);
    private static bool PresentationType(Type t)
    {
        if (t.IsArray) return PresentationType(t.GetElementType());
        if (t.IsGenericType) return t.GetGenericArguments().Any(PresentationType);
        return typeof(Delegate).IsAssignableFrom(t) || typeof(Type).IsAssignableFrom(t) ||
            (typeof(UnityEngine.Object).IsAssignableFrom(t) && !DomainReference(t));
    }

    // Binary leaves avoid hundreds of thousands of JSON property allocations per
    // tick. The outer snapshot stays self-contained; graph IDs retain aliases/cycles.
    private readonly System.IO.MemoryStream output = new System.IO.MemoryStream();
    private readonly Dictionary<Type, int> writeTypeIds = new Dictionary<Type, int>();
    private readonly List<Type> readTypeIds = new List<Type>();
    private static readonly Dictionary<Type, string> typeNames = new Dictionary<Type, string>();
    private static readonly Dictionary<Type, MethodInfo> collectionAdds = new Dictionary<Type, MethodInfo>();
    public JObject CaptureFields(object value) => Encode(w => WriteFields(w, value));
    public void ApplyFields(object value, JObject fields) => Decode(fields, r => { ReadFields(r, value); return null; });
    public JToken Write(object value) => Encode(w => WriteValue(w, value));
    public object Read(JToken token, Type expected) => Decode(token, r => ReadValue(r, expected));
    private JObject Encode(System.Action<System.IO.BinaryWriter> writeValue)
    {
        if (frozen != null)
        {
            int leaf = frozen.BeginLeaf(); writeTypeIds.Clear(); writeValue(frozen.Writer);
            return new JObject { ["b"] = leaf };
        }
        output.SetLength(0); output.Position = 0; writeTypeIds.Clear();
        using (var writer = new System.IO.BinaryWriter(output, System.Text.Encoding.UTF8, true)) writeValue(writer);
        return new JObject { ["b"] = binaryLeaves ? new JValue(output.ToArray()) : new JValue(Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length)) };
    }
    private object Decode(JToken token, Func<System.IO.BinaryReader, object> readValue)
    {
        readTypeIds.Clear();
        byte[] bytes = token["b"].Type == JTokenType.Bytes ? (byte[])token["b"] : Convert.FromBase64String((string)token["b"]);
        using (var stream = new System.IO.MemoryStream(bytes, false))
        using (var reader = new System.IO.BinaryReader(stream))
        {
            object value = readValue(reader);
            if (stream.Position != stream.Length) throw new InvalidOperationException("Trailing gameplay graph data");
            return value;
        }
    }
    private static NPCType SpeciesSource(string name) => worldCatalog != null && worldCatalog.species.TryGetValue(name, out NPCType source)
        ? source : NPCType.types.FirstOrDefault(x => x.name == name) ?? NPCType.baseTypes.FirstOrDefault(x => x.name == name);
    private static bool SameDefinitionValue(object a, object b) => Equals(a, b) || (a is List<string> left && b is List<string> right && left.SequenceEqual(right));
    private void WriteFields(System.IO.BinaryWriter w, object value)
    {
        var fields = CoopFieldAccess.For(value.GetType(), Fields(value.GetType()));
        NPCType source = value is NPCType npc ? SpeciesSource(npc.name) : null;
        long countAt = w.BaseStream.Position;
        w.Write((ushort)0); ushort count = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo f = fields[i].field;
            if (f.Name == "buttons" || f.Name == "buttonTexts" || f.Name == "inventoryButtons") continue;
            object current = fields[i].get(value);
            if (source != null && f.DeclaringType.IsInstanceOfType(source) && SameDefinitionValue(current, fields[i].get(source))) continue;
            w.Write((ushort)i); WriteValue(w, current); count++;
        }
        long end = w.BaseStream.Position;
        w.BaseStream.Position = countAt; w.Write(count); w.BaseStream.Position = end;
    }
    private void ReadFields(System.IO.BinaryReader r, object value)
    {
        var fields = CoopFieldAccess.For(value.GetType(), Fields(value.GetType()));
        int count = r.ReadUInt16();
        if (count > fields.Length) throw new InvalidOperationException("Invalid gameplay field count");
        for (int i = 0; i < count; i++)
        {
            int index = r.ReadUInt16();
            if (index >= fields.Length) throw new InvalidOperationException("Invalid gameplay field index");
            FieldInfo f = fields[index].field; fields[index].set(value, ReadValue(r, f.FieldType));
        }
    }
    private void WriteType(System.IO.BinaryWriter w, Type t)
    {
        if (commonTypeIds.TryGetValue(t, out int common)) { w.Write(common); return; }
        if (writeTypeIds.TryGetValue(t, out int id)) { w.Write(id); return; }
        id = writeTypeIds.Count + 1; writeTypeIds[t] = id; w.Write(-id);
        if (!typeNames.TryGetValue(t, out string name)) typeNames[t] = name = t.AssemblyQualifiedName;
        w.Write(name);
    }
    private Type ReadType(System.IO.BinaryReader r, Type expected)
    {
        int id = r.ReadInt32(); Type t;
        if (id >= CommonTypeBase)
        {
            int common = id - CommonTypeBase;
            if (common >= commonTypes.Length) throw new InvalidOperationException("Invalid common gameplay type");
            t = commonTypes[common];
        }
        else if (id < 0)
        {
            if (-id != readTypeIds.Count + 1) throw new InvalidOperationException("Invalid gameplay type definition");
            string name = r.ReadString();
            if (!wireTypes.TryGetValue(name, out t)) wireTypes[name] = t = Type.GetType(name, true);
            readTypeIds.Add(t);
        }
        else
        {
            if (id == 0 || id > readTypeIds.Count) throw new InvalidOperationException("Invalid gameplay type reference");
            t = readTypeIds[id - 1];
        }
        if (!Allowed(t) || (expected != typeof(object) && !expected.IsAssignableFrom(t))) throw new InvalidOperationException("Invalid gameplay type " + t);
        return t;
    }
    private void WriteValue(System.IO.BinaryWriter w, object value)
    {
        if (value == null) { w.Write((byte)0); return; }
        Type t = value.GetType();
        if (t.IsEnum) { w.Write((byte)7); WriteType(w, t); w.Write(Convert.ToInt64(value)); return; }
        if (t.IsPrimitive || value is string || value is decimal)
        {
            w.Write((byte)1); TypeCode code = Type.GetTypeCode(t); w.Write((byte)code);
            switch (code)
            {
                case TypeCode.Boolean: w.Write((bool)value); break;
                case TypeCode.Char: w.Write((char)value); break;
                case TypeCode.String: w.Write((string)value); break;
                case TypeCode.Single: w.Write((float)value); break;
                case TypeCode.Double: w.Write((double)value); break;
                case TypeCode.Decimal: w.Write((decimal)value); break;
                case TypeCode.UInt64: w.Write((ulong)value); break;
                case TypeCode.Byte: w.Write((byte)value); break;
                case TypeCode.SByte: w.Write((sbyte)value); break;
                case TypeCode.Int16: w.Write((short)value); break;
                case TypeCode.UInt16: w.Write((ushort)value); break;
                case TypeCode.Int32: w.Write((int)value); break;
                case TypeCode.UInt32: w.Write((uint)value); break;
                default: w.Write(Convert.ToInt64(value)); break;
            }
            return;
        }
        if (value is UnityEngine.Object unity)
        {
            string key = encodeUnity(unity);
            if (key == null) w.Write((byte)0); else { w.Write((byte)2); w.Write(key); }
            return;
        }
        if (value is PathNode node)
        {
            w.Write((byte)3);
            if (!nodeCoordinates.TryGetValue(node, out Coordinate coordinates) || coordinates.room < 0 || coordinates.node < 0 || coordinates.room >= rooms.Count ||
                !ReferenceEquals(rooms[coordinates.room], node.associatedRoom) || coordinates.node >= node.associatedRoom.pathNodes.Count ||
                !ReferenceEquals(node.associatedRoom.pathNodes[coordinates.node], node))
            {
                coordinates = new Coordinate { room = rooms.IndexOf(node.associatedRoom), node = node.associatedRoom.pathNodes.IndexOf(node) };
                nodeCoordinates[node] = coordinates;
            }
            w.Write(coordinates.room); w.Write(coordinates.node);
            return;
        }
        if (catalog.TryGetValue(value, out string canonical)) { w.Write((byte)4); w.Write(canonical); return; }
        if (written.TryGetValue(value, out int old)) { w.Write((byte)5); w.Write(old); return; }
        int id = written.Count + 1; written[value] = id;
        if (value is Delegate || value is CallbackToken)
        {
            w.Write((byte)6); w.Write(id);
            var descriptor = value as CallbackToken; var function = value as Delegate;
            w.Write(descriptor != null ? descriptor.method : function.Method.DeclaringType.FullName + ":" + function.Method.Name);
            WriteValue(w, descriptor != null ? descriptor.target : function.Target); return;
        }
        if (!Allowed(t)) throw new InvalidOperationException("Unsupported gameplay state " + t);
        if (value is Vector2 || value is Vector3 || value is Vector4 || value is Quaternion || value is Rect || value is Color ||
            value is Vector2Int || value is Vector3Int)
        {
            w.Write((byte)12); w.Write(id); WriteType(w, t);
            if (value is Vector2 v2) { w.Write(v2.x); w.Write(v2.y); }
            else if (value is Vector3 v3) { w.Write(v3.x); w.Write(v3.y); w.Write(v3.z); }
            else if (value is Vector4 v4) { w.Write(v4.x); w.Write(v4.y); w.Write(v4.z); w.Write(v4.w); }
            else if (value is Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
            else if (value is Rect rect) { w.Write(rect.x); w.Write(rect.y); w.Write(rect.width); w.Write(rect.height); }
            else if (value is Color c) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
            // Integer vectors keep exact values instead of round-tripping through float.
            else if (value is Vector2Int i2) { w.Write(i2.x); w.Write(i2.y); }
            else { var i3 = (Vector3Int)value; w.Write(i3.x); w.Write(i3.y); w.Write(i3.z); }
            return;
        }
        if (value is Array array)
        {
            w.Write((byte)9); w.Write(id); WriteType(w, t); w.Write(array.Rank);
            for (int i = 0; i < array.Rank; i++) w.Write(array.GetLength(i));
            foreach (object item in array) WriteValue(w, item); return;
        }
        if (value is IDictionary dictionary)
        {
            w.Write((byte)10); w.Write(id); WriteType(w, t);
            long countAt = w.BaseStream.Position; w.Write(0); int count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                // A destroyed Unity wrapper is a tombstone, not a valid replica key.
                // Preserve all live keys; an unresolved live key is a lifecycle bug.
                if (entry.Key is UnityEngine.Object unityKey)
                {
                    if (unityKey == null) continue;
                    if (encodeUnity(unityKey) == null)
                        throw new InvalidOperationException("Unregistered live gameplay dictionary key: " + t + "; key=" + unityKey.GetType() + "; name=" + unityKey.name);
                }
                WriteValue(w, entry.Key); WriteValue(w, entry.Value); count++;
            }
            long end = w.BaseStream.Position; w.BaseStream.Position = countAt; w.Write(count); w.BaseStream.Position = end;
            return;
        }
        if (value is IEnumerable enumerable && !(value is NPCType))
        {
            w.Write((byte)11); w.Write(id); WriteType(w, t);
            long countAt = w.BaseStream.Position; w.Write(0); int count = 0;
            foreach (object item in enumerable) { WriteValue(w, item); count++; }
            long end = w.BaseStream.Position; w.BaseStream.Position = countAt; w.Write(count); w.BaseStream.Position = end; return;
        }
        w.Write((byte)8); w.Write(id); WriteType(w, t);
        if (value is NPCType npc) w.Write(npc.name);
        if (value is Item inventory) { WriteValue(w, inventory.sourceItem); w.Write(CoopInventory.Identity(inventory)); }
        WriteFields(w, value);
    }
    private object ReadValue(System.IO.BinaryReader r, Type expected)
    {
        byte tag = r.ReadByte();
        switch (tag)
        {
            case 0: return null;
            case 1:
                TypeCode code = (TypeCode)r.ReadByte();
                switch (code)
                {
                    case TypeCode.Boolean: return r.ReadBoolean();
                    case TypeCode.Char: return r.ReadChar();
                    case TypeCode.String: return r.ReadString();
                    case TypeCode.Single: return r.ReadSingle();
                    case TypeCode.Double: return r.ReadDouble();
                    case TypeCode.Decimal: return r.ReadDecimal();
                    case TypeCode.UInt64: return r.ReadUInt64();
                    case TypeCode.Byte: return r.ReadByte();
                    case TypeCode.SByte: return r.ReadSByte();
                    case TypeCode.Int16: return r.ReadInt16();
                    case TypeCode.UInt16: return r.ReadUInt16();
                    case TypeCode.Int32: return r.ReadInt32();
                    case TypeCode.UInt32: return r.ReadUInt32();
                    default: return Convert.ChangeType(r.ReadInt64(), code);
                }
            case 2: return decodeUnity(r.ReadString());
            case 3:
                int room = r.ReadInt32(), node = r.ReadInt32();
                return room >= 0 && node >= 0 ? rooms[room].pathNodes[node] : null;
            case 4:
                string key = r.ReadString();
                if (!catalogByKey.TryGetValue(key, out object canonical)) throw new InvalidOperationException("Missing definition " + key);
                return canonical;
            case 5: return read[r.ReadInt32()];
            case 6:
                int callbackId = r.ReadInt32(); var callback = new CallbackToken { method = r.ReadString() }; read[callbackId] = callback;
                callback.target = ReadValue(r, typeof(object)); return callback;
            case 7:
                Type enumType = ReadType(r, expected); return Enum.ToObject(enumType, r.ReadInt64());
        }
        if (tag < 8 || tag > 12) throw new InvalidOperationException("Invalid gameplay graph tag");
        int id = r.ReadInt32(); Type t = ReadType(r, expected); object value;
        if (tag == 12)
        {
            // The wire type decides how many components follow, so integer vectors
            // can share the tag without a separate schema change.
            if (t == typeof(Vector2Int)) { value = new Vector2Int(r.ReadInt32(), r.ReadInt32()); read[id] = value; return value; }
            if (t == typeof(Vector3Int)) { value = new Vector3Int(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()); read[id] = value; return value; }
            float x = r.ReadSingle(), y = r.ReadSingle();
            if (t == typeof(Vector2)) value = new Vector2(x, y);
            else if (t == typeof(Vector3)) value = new Vector3(x, y, r.ReadSingle());
            else if (t == typeof(Vector4)) value = new Vector4(x, y, r.ReadSingle(), r.ReadSingle());
            else if (t == typeof(Quaternion)) value = new Quaternion(x, y, r.ReadSingle(), r.ReadSingle());
            else if (t == typeof(Rect)) value = new Rect(x, y, r.ReadSingle(), r.ReadSingle());
            else if (t == typeof(Color)) value = new Color(x, y, r.ReadSingle(), r.ReadSingle());
            else throw new InvalidOperationException("Invalid vector type");
            read[id] = value; return value;
        }
        if (tag == 9)
        {
            int rank = r.ReadInt32(); if (rank < 1 || rank > 8) throw new InvalidOperationException("Invalid array rank");
            var shape = new int[rank]; for (int i = 0; i < rank; i++) shape[i] = ReadCount(r);
            var array = Array.CreateInstance(t.GetElementType(), shape); read[id] = array;
            var indices = new int[rank];
            for (int linear = 0; linear < array.Length; linear++)
            {
                object item = ReadValue(r, t.GetElementType());
                if (rank == 1) array.SetValue(item, linear);
                else { int remainder = linear; for (int i = rank - 1; i >= 0; i--) { indices[i] = remainder % shape[i]; remainder /= shape[i]; } array.SetValue(item, indices); }
            }
            return array;
        }
        if (tag == 10 || tag == 11)
        {
            value = Activator.CreateInstance(t); read[id] = value; int count = ReadCount(r); Type[] args = t.GetGenericArguments();
            if (value is IDictionary dictionary)
                for (int i = 0; i < count; i++) dictionary.Add(ReadValue(r, args[0]), ReadValue(r, args[1]));
            else if (value is IList list)
                for (int i = 0; i < count; i++) list.Add(ReadValue(r, args[0]));
            else
            {
                if (!collectionAdds.TryGetValue(t, out MethodInfo add)) collectionAdds[t] = add = t.GetMethod("Add", new[] { args[0] });
                for (int i = 0; i < count; i++) add.Invoke(value, new[] { ReadValue(r, args[0]) });
            }
            return value;
        }
        if (typeof(NPCType).IsAssignableFrom(t))
        {
            string name = r.ReadString(); NPCType source = SpeciesSource(name);
            if (source == null) throw new InvalidOperationException("Missing NPC definition " + name);
            value = NPCType.CreateDuplicate(source);
        }
        else if (typeof(Item).IsAssignableFrom(t))
        {
            Item source = (Item)ReadValue(r, typeof(Item));
            value = source != null ? source.CreateInstance() : FormatterServices.GetUninitializedObject(t);
            CoopInventory.Bind((Item)value, r.ReadInt64());
        }
        else value = t.IsValueType ? Activator.CreateInstance(t) : FormatterServices.GetUninitializedObject(t);
        read[id] = value; ReadFields(r, value); return value;
    }
    private static int ReadCount(System.IO.BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > 1000000) throw new InvalidOperationException("Invalid gameplay collection size");
        return count;
    }
    private static bool Allowed(Type t)
    {
        if (!allowedTypes.TryGetValue(t, out bool allowed)) allowedTypes[t] = allowed = ComputeAllowed(t);
        return allowed;
    }
    private static bool ComputeAllowed(Type t)
    {
        if (t.IsArray) return Allowed(t.GetElementType());
        if (t.IsGenericType)
        {
            Type definition = t.GetGenericTypeDefinition();
            // Original spirit possession stores its saved name/image set in an
            // immutable anonymous DTO. It is game state and must survive sync.
            if (definition.Assembly == typeof(CharacterStatus).Assembly &&
                definition.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal) &&
                definition.IsDefined(typeof(CompilerGeneratedAttribute), false))
                return t.GetGenericArguments().All(Allowed);
            return (definition == typeof(List<>) || definition == typeof(Dictionary<,>) || definition == typeof(HashSet<>) ||
                    definition == typeof(Nullable<>) || definition == typeof(KeyValuePair<,>)) && t.GetGenericArguments().All(Allowed);
        }
        return t == typeof(object) || t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(decimal) ||
               t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) || t == typeof(Quaternion) ||
               t == typeof(Color) || t == typeof(Rect) || t == typeof(Bounds) || t == typeof(LayerMask) ||
               t == typeof(Vector2Int) || t == typeof(Vector3Int) ||
               (t.Assembly == typeof(CharacterStatus).Assembly && !typeof(Delegate).IsAssignableFrom(t) &&
                (!typeof(UnityEngine.Object).IsAssignableFrom(t) || DomainReference(t)));
    }
}
