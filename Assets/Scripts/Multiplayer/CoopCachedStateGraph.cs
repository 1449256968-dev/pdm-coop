using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

// A self-contained snapshot of a stable object registry. Every reachable definition
// travels in a full snapshot; unchanged definitions retain their version and instance.
// This has no dependency on a previously received packet and is safe to coalesce.
public sealed class CoopCachedStateGraph
{
    public sealed class Row { public long id; public uint version; public byte[] body; }
    public sealed class Frame : List<Row>
    {
        public readonly string[] typeNames;
        public Frame(IEnumerable<Row> rows, string[] names) : base(rows) { typeNames = names; }
        public Frame(int capacity, string[] names) : base(capacity) { typeNames = names; }
    }
    private sealed class Identity { public long id; public byte[] body; public uint version; }
    private sealed class Replica { public object value; public uint version; public Type type; public Func<object, int> collectionVersion; public int appliedCollectionVersion; }
    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public new bool Equals(object a, object b) => ReferenceEquals(a, b);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
    private readonly Dictionary<object, Identity> identities = new Dictionary<object, Identity>(new ReferenceComparer());
    private readonly HashSet<object> seen = new HashSet<object>(new ReferenceComparer());
    private readonly Dictionary<long, Replica> replicas = new Dictionary<long, Replica>();
    private readonly Dictionary<Type, int> typeIds = new Dictionary<Type, int>();
    private readonly List<string> typeNames = new List<string>();
    private readonly Dictionary<string, Type> types = new Dictionary<string, Type>();
    private Type[] readTypes;
    private readonly Func<UnityEngine.Object, string> encodeUnity;
    private readonly Func<string, UnityEngine.Object> decodeUnity;
    private readonly MemoryStream scratch = new MemoryStream();
    private readonly BinaryWriter writer;
    private readonly Queue<object> pending = new Queue<object>();
    private readonly HashSet<long> reached = new HashSet<long>();
    private readonly List<Row> rows = new List<Row>();
    private CoopStateGraph context;
    private long nextId;
    private readonly Dictionary<long, Row> incomingRows = new Dictionary<long, Row>();
    private readonly List<long> staleReplicas = new List<long>();
    private readonly List<object> staleIdentities = new List<object>();
    private bool inFrame;
    private readonly HashSet<long> applying = new HashSet<long>();
    private static readonly Dictionary<Type, FieldInfo[]> roomFields = new Dictionary<Type, FieldInfo[]>();
    private static FieldInfo[] RoomFields(Type type)
    {
        if (!roomFields.TryGetValue(type, out FieldInfo[] fields))
            roomFields[type] = fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f =>
                f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType == typeof(Vector3) || f.FieldType == typeof(Rect) ||
                f.Name == "containedNPCs" || f.Name == "containedOrbs" || f.Name == "containedCrates" ||
                f.Name == "interactableLocations" || f.Name == "connectedRooms").ToArray();
        return fields;
    }
    public CoopCachedStateGraph(Func<UnityEngine.Object, string> encode, Func<string, UnityEngine.Object> decode)
    { encodeUnity = encode; decodeUnity = decode; writer = new BinaryWriter(scratch, Encoding.UTF8, true); }
    public void BeginCapture()
    {
        context = new CoopStateGraph(encodeUnity, decodeUnity);
        pending.Clear(); reached.Clear(); rows.Clear(); seen.Clear();
    }
    private Identity Id(object value)
    {
        if (!identities.TryGetValue(value, out Identity identity))
        { identity = new Identity { id = ++nextId }; identities.Add(value, identity); }
        seen.Add(value);
        return identity;
    }
    public JObject CaptureFields(object value)
    {
        Identity identity = Id(value);
        scratch.SetLength(0); scratch.Position = 0; WriteFields(writer, value);
        return new JObject { ["b"] = Freeze(identity) };
    }
    public JToken Write(object value)
    {
        scratch.SetLength(0); scratch.Position = 0; WriteValue(writer, value);
        return new JObject { ["b"] = scratch.ToArray() };
    }
    public byte[] CaptureRoom(RoomData room)
    {
        scratch.SetLength(0); scratch.Position = 0;
        WriteValue(writer, room.transform.position); WriteValue(writer, room.transform.rotation);
        writer.Write(room is UFOMover);
        if (room is UFOMover ufo) WriteValue(writer, ufo.actualMover.transform.position);
        FieldInfo[] fields = RoomFields(room.GetType()); writer.Write((ushort)fields.Length);
        foreach (FieldInfo field in fields) WriteValue(writer, field.GetValue(room));
        writer.Write(room.pathNodes.Count);
        foreach (PathNode node in room.pathNodes)
        {
            WriteValue(writer, node.pathConnections); WriteValue(writer, node.containedNPCs);
            WriteValue(writer, node.containedLocations); WriteValue(writer, node.area); WriteValue(writer, node.centrePoint);
        }
        return scratch.ToArray();
    }
    private void ReadRoomPose(BinaryReader reader, out Vector3 position, out Quaternion rotation, out Vector3? mover)
    {
        position = (Vector3)ReadValue(reader, typeof(Vector3)); rotation = (Quaternion)ReadValue(reader, typeof(Quaternion));
        mover = reader.ReadBoolean() ? (Vector3?)ReadValue(reader, typeof(Vector3)) : null;
    }
    public void RoomPose(byte[] bytes, out Vector3 position, out Quaternion rotation, out Vector3? mover)
    {
        using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
            ReadRoomPose(reader, out position, out rotation, out mover);
    }
    public void ApplyRoom(RoomData room, byte[] bytes)
    {
        using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
        {
            ReadRoomPose(reader, out _, out _, out _);
            FieldInfo[] fields = RoomFields(room.GetType());
            if (reader.ReadUInt16() != fields.Length) throw new InvalidDataException("Room state schema mismatch");
            foreach (FieldInfo field in fields) field.SetValue(room, ReadValue(reader, field.FieldType));
            if (Count(reader) != room.pathNodes.Count) throw new InvalidDataException("Room node count mismatch");
            foreach (PathNode node in room.pathNodes)
            {
                node.pathConnections = (List<PathConnection>)ReadValue(reader, typeof(List<PathConnection>));
                node.containedNPCs = (List<CharacterStatus>)ReadValue(reader, typeof(List<CharacterStatus>));
                node.containedLocations = (List<StrikeableLocation>)ReadValue(reader, typeof(List<StrikeableLocation>));
                node.area = (Rect)ReadValue(reader, typeof(Rect)); node.centrePoint = (Vector3)ReadValue(reader, typeof(Vector3));
                node.bestChoiceToNode.Clear();
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing room state bytes");
        }
    }
    private byte[] Freeze(Identity identity)
    {
        int length = (int)scratch.Length; byte[] buffer = scratch.GetBuffer();
        // Unchanged bodies are the common case and used to be compared one byte at a
        // time; the spans comparison is vectorized in the player runtime.
        bool equal = identity.body != null && identity.body.Length == length &&
            ((ReadOnlySpan<byte>)identity.body).SequenceEqual(new ReadOnlySpan<byte>(buffer, 0, length));
        if (!equal) { identity.body = scratch.ToArray(); identity.version++; }
        return identity.body;
    }
    public Frame FinishCapture()
    {
        while (pending.Count != 0)
        {
            object value = pending.Dequeue(); Identity identity = Id(value);
            scratch.SetLength(0); scratch.Position = 0;
            Type type = value.GetType();
            byte kind = value is Array ? (byte)2 : value is IDictionary ? (byte)3 : value is IList ? (byte)4 :
                value is IEnumerable && !(value is NPCType) ? (byte)5 : value is NPCType ? (byte)6 : value is Item ? (byte)7 :
                CoopStateGraph.DescribeCallback(value, out _, out _) ? (byte)8 : (byte)1;
            writer.Write(kind); WriteType(writer, type);
            if (kind == 6) writer.Write(((NPCType)value).name);
            if (kind == 7) { WriteValue(writer, ((Item)value).sourceItem); writer.Write(CoopInventory.Identity((Item)value)); }
            if (kind == 8)
            {
                CoopStateGraph.DescribeCallback(value, out string method, out object target);
                writer.Write(method); WriteValue(writer, target);
            }
            else if (kind == 2)
            {
                var array = (Array)value; writer.Write(array.Rank);
                for (int i = 0; i < array.Rank; i++) writer.Write(array.GetLength(i));
                foreach (object item in array) WriteValue(writer, item);
            }
            else if (kind == 3)
            {
                var dictionary = (IDictionary)value; long countAt = scratch.Position; writer.Write(0); int count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is UnityEngine.Object key && key == null) continue;
                    if (entry.Key is UnityEngine.Object live && encodeUnity(live) == null) throw new InvalidOperationException("Unregistered live dictionary key " + live.GetType());
                    WriteValue(writer, entry.Key); WriteValue(writer, entry.Value); count++;
                }
                long end = scratch.Position; scratch.Position = countAt; writer.Write(count); scratch.Position = end;
            }
            else if (kind == 4 || kind == 5)
            {
                long countAt = scratch.Position; writer.Write(0); int count = 0;
                foreach (object item in (IEnumerable)value) { WriteValue(writer, item); count++; }
                long end = scratch.Position; scratch.Position = countAt; writer.Write(count); scratch.Position = end;
            }
            else WriteFields(writer, value);
            byte[] body = Freeze(identity);
            rows.Add(new Row { id = identity.id, version = identity.version, body = body });
        }
        staleIdentities.Clear();
        foreach (object value in identities.Keys) if (!seen.Contains(value)) staleIdentities.Add(value);
        foreach (object value in staleIdentities) identities.Remove(value);
        return new Frame(rows, typeNames.ToArray());
    }
    public static byte[] EncodeRows(Frame rows)
    {
        using (var stream = new MemoryStream()) using (var output = new BinaryWriter(stream))
        {
            output.Write(rows.typeNames.Length);
            foreach (string name in rows.typeNames) output.Write(name);
            output.Write(rows.Count);
            foreach (Row row in rows) { output.Write(row.id); output.Write(row.version); output.Write(row.body.Length); output.Write(row.body); }
            return stream.ToArray();
        }
    }
    public static Frame DecodeRows(byte[] bytes)
    {
        using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
        {
            int typeCount = Count(reader);
            if (typeCount > 4096) throw new InvalidDataException("Too many state types");
            var names = new string[typeCount];
            for (int i = 0; i < typeCount; i++) { names[i] = reader.ReadString(); if (names[i].Length > 8192) throw new InvalidDataException("State type name too long"); }
            int count = Count(reader); var rows = new Frame(count, names);
            for (int i = 0; i < count; i++)
            {
                long id = reader.ReadInt64(); uint version = reader.ReadUInt32(); int size = Count(reader);
                byte[] body = reader.ReadBytes(size);
                if (id <= 0 || version == 0 || body.Length != size) throw new InvalidDataException("Invalid state object definition");
                rows.Add(new Row { id = id, version = version, body = body });
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing state definitions");
            return rows;
        }
    }
    public void ApplyRows(Frame rows)
    {
        // The complete table travels with every frame, including late joins.
        // Row bodies use stable numeric IDs, avoiding repeated type-name encoding
        // and string allocations for every collection/object definition.
        readTypes = new Type[rows.typeNames.Length];
        for (int i = 0; i < readTypes.Length; i++)
        {
            string name = rows.typeNames[i];
            if (!types.TryGetValue(name, out Type type)) types[name] = type = Type.GetType(name, true);
            if (!CoopStateGraph.AllowedType(type) && !typeof(Delegate).IsAssignableFrom(type)) throw new InvalidDataException("Invalid state type " + type);
            readTypes[i] = type;
        }
        // Buffers are reused across snapshots: a full definition table arrives every
        // frame, so rebuilding the lookup and the removal list was pure allocation churn.
        context = new CoopStateGraph(encodeUnity, decodeUnity); applying.Clear();
        incomingRows.Clear(); inFrame = true;
        foreach (Row row in rows)
        {
            if (incomingRows.ContainsKey(row.id)) throw new InvalidDataException("Duplicate state object definition");
            incomingRows.Add(row.id, row);
        }
        foreach (Row row in rows) Allocate(row.id);
        foreach (Row row in rows) ApplyRow(row.id);
        staleReplicas.Clear();
        foreach (long id in replicas.Keys) if (!incomingRows.ContainsKey(id)) staleReplicas.Add(id);
        foreach (long id in staleReplicas) replicas.Remove(id);
        inFrame = false;
    }
    private object Allocate(long id)
    {
        if (replicas.TryGetValue(id, out Replica existing)) return existing.value;
        if (!inFrame || !incomingRows.TryGetValue(id, out Row row)) throw new InvalidDataException("Missing state object " + id);
        using (var stream = new MemoryStream(row.body, false)) using (var reader = new BinaryReader(stream))
        {
            byte kind = reader.ReadByte(); Type type = ReadType(reader); object value;
            if (kind == 2)
            {
                int rank = reader.ReadInt32(); if (rank < 1 || rank > 8) throw new InvalidDataException("Invalid array rank");
                var shape = new int[rank]; for (int i = 0; i < rank; i++) shape[i] = Count(reader);
                value = Array.CreateInstance(type.GetElementType(), shape);
            }
            else if (kind == 3 || kind == 4 || kind == 5) value = Activator.CreateInstance(type);
            else if (kind == 6)
            {
                string name = reader.ReadString(); NPCType source = CoopStateGraph.Species(name);
                if (source == null) throw new InvalidDataException("Missing species " + name);
                value = NPCType.CreateDuplicate(source);
            }
            else if (kind == 7)
            {
                Item source = (Item)ReadValue(reader, typeof(Item)); long item = reader.ReadInt64();
                value = source != null ? source.CreateInstance() : FormatterServices.GetUninitializedObject(type);
                CoopInventory.Bind((Item)value, item);
            }
            else if (kind == 8) value = CoopStateGraph.Callback(reader.ReadString(), null);
            else if (kind == 1) value = type.IsValueType ? Activator.CreateInstance(type) : FormatterServices.GetUninitializedObject(type);
            else throw new InvalidDataException("Invalid state object kind");
            if (!type.IsInstanceOfType(value) && kind != 8) throw new InvalidDataException("State factory type mismatch");
            replicas[id] = new Replica { value = value, type = type, collectionVersion = CoopCollectionVersion.For(value.GetType()) }; return value;
        }
    }
    private void ApplyRow(long id)
    {
        Row row = incomingRows[id]; Replica replica = replicas[id];
        // Original client presentation code can append/remove entries from lists
        // such as spriteStack. An unchanged server version does not imply that a
        // locally exposed collection still matches it. Restore these in place on
        // every snapshot; keep version skipping for ordinary object definitions.
        bool exposedCollection = replica.value is IEnumerable && !(replica.value is NPCType);
        bool locallyChanged = exposedCollection && (replica.collectionVersion == null || replica.collectionVersion(replica.value) != replica.appliedCollectionVersion);
        if ((replica.version == row.version && !locallyChanged) || !applying.Add(id)) return;
        using (var stream = new MemoryStream(row.body, false)) using (var reader = new BinaryReader(stream))
        {
            byte kind = reader.ReadByte(); Type type = ReadType(reader); object value = replica.value;
            if (type != replica.type) throw new InvalidDataException("State object identity changed type");
            if (kind == 6) ((NPCType)value).name = reader.ReadString();
            if (kind == 7) { ReadValue(reader, typeof(Item)); reader.ReadInt64(); }
            if (kind == 8)
            {
                string method = reader.ReadString(); object target = ReadValue(reader, typeof(object));
                var members = CoopFieldAccess.For(value.GetType(), CoopStateGraph.Fields(value.GetType()));
                foreach (var member in members) member.set(value, member.field.Name == "method" ? (object)method : target);
            }
            else if (kind == 2)
            {
                var array = (Array)value; int rank = reader.ReadInt32(); var shape = new int[rank];
                for (int i = 0; i < rank; i++) { shape[i] = Count(reader); if (shape[i] != array.GetLength(i)) throw new InvalidDataException("Array identity changed shape"); }
                var indices = new int[rank];
                for (int linear = 0; linear < array.Length; linear++)
                {
                    object item = ReadValue(reader, type.GetElementType());
                    if (rank == 1) array.SetValue(item, linear);
                    else { int n = linear; for (int i = rank - 1; i >= 0; i--) { indices[i] = n % shape[i]; n /= shape[i]; } array.SetValue(item, indices); }
                }
            }
            else if (kind == 3)
            {
                var dictionary = (IDictionary)value; dictionary.Clear(); int count = Count(reader); Type[] args = type.GetGenericArguments();
                for (int i = 0; i < count; i++) dictionary.Add(ReadValue(reader, args[0]), ReadValue(reader, args[1]));
            }
            else if (kind == 4)
            {
                var list = (IList)value; list.Clear(); int count = Count(reader); Type element = type.GetGenericArguments()[0];
                for (int i = 0; i < count; i++) list.Add(ReadValue(reader, element));
            }
            else if (kind == 5)
            {
                type.GetMethod("Clear").Invoke(value, null); int count = Count(reader); Type element = type.GetGenericArguments()[0];
                MethodInfo add = type.GetMethod("Add", new[] { element });
                for (int i = 0; i < count; i++) add.Invoke(value, new[] { ReadValue(reader, element) });
            }
            else ReadFields(reader, value);
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing state object data");
        }
        replica.version = row.version;
        if (replica.collectionVersion != null) replica.appliedCollectionVersion = replica.collectionVersion(replica.value);
        applying.Remove(id);
    }
    public void ApplyFields(object value, JObject fields)
    { using (var stream = new MemoryStream((byte[])fields["b"], false)) using (var reader = new BinaryReader(stream)) { ReadFields(reader, value); if (stream.Position != stream.Length) throw new InvalidDataException("Trailing entity fields"); } }
    public object Read(JToken token, Type expected)
    { using (var stream = new MemoryStream((byte[])token["b"], false)) using (var reader = new BinaryReader(stream)) { object value = ReadValue(reader, expected); if (stream.Position != stream.Length) throw new InvalidDataException("Trailing state value"); return value; } }
    private void WriteFields(BinaryWriter output, object value)
    {
        var fields = CoopFieldAccess.For(value.GetType(), CoopStateGraph.Fields(value.GetType()));
        NPCType source = value is NPCType species ? CoopStateGraph.Species(species.name) : null;
        long countAt = output.BaseStream.Position; output.Write((ushort)0); ushort count = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            string name = fields[i].field.Name;
            if (name == "buttons" || name == "buttonTexts" || name == "inventoryButtons") continue;
            object current = fields[i].get(value);
            if (source != null && fields[i].field.DeclaringType.IsInstanceOfType(source) && Equals(current, fields[i].get(source))) continue;
            output.Write((ushort)i); WriteValue(output, current); count++;
        }
        long end = output.BaseStream.Position; output.BaseStream.Position = countAt; output.Write(count); output.BaseStream.Position = end;
    }
    private void ReadFields(BinaryReader reader, object value)
    {
        var fields = CoopFieldAccess.For(value.GetType(), CoopStateGraph.Fields(value.GetType())); int count = reader.ReadUInt16();
        // A species row contains overrides of its canonical definition. Reset omitted
        // fields on every changed version, including references shared with that definition.
        NPCType source = value is NPCType species ? CoopStateGraph.Species(species.name) : null;
        if (source != null) foreach (var field in fields)
            if (field.field.DeclaringType.IsInstanceOfType(source)) field.set(value, field.get(source));
        if (count > fields.Length) throw new InvalidDataException("Invalid state field count");
        for (int i = 0; i < count; i++) { int index = reader.ReadUInt16(); if (index >= fields.Length) throw new InvalidDataException("Invalid state field index"); fields[index].set(value, ReadValue(reader, fields[index].field.FieldType)); }
    }
    private void WriteType(BinaryWriter output, Type type)
    {
        if (!typeIds.TryGetValue(type, out int id))
        { id = typeNames.Count; typeIds[type] = id; typeNames.Add(type.AssemblyQualifiedName); }
        output.Write(id);
    }
    private Type ReadType(BinaryReader reader)
    {
        int id = reader.ReadInt32();
        if (readTypes == null || id < 0 || id >= readTypes.Length) throw new InvalidDataException("Invalid state type ID");
        return readTypes[id];
    }
    private void WriteValue(BinaryWriter output, object value)
    {
        if (value == null) { output.Write((byte)0); return; }
        Type type = value.GetType();
        // Frequently captured geometry has a fixed lossless schema. Avoid reflection,
        // nested boxing, and assembly-qualified type names for every coordinate.
        if (value is Vector3 v3) { output.Write((byte)10); output.Write(v3.x); output.Write(v3.y); output.Write(v3.z); return; }
        if (value is Quaternion q) { output.Write((byte)11); output.Write(q.x); output.Write(q.y); output.Write(q.z); output.Write(q.w); return; }
        if (value is Rect rect) { output.Write((byte)12); output.Write(rect.x); output.Write(rect.y); output.Write(rect.width); output.Write(rect.height); return; }
        if (value is Vector2 v2) { output.Write((byte)13); output.Write(v2.x); output.Write(v2.y); return; }
        if (value is Vector4 v4) { output.Write((byte)14); output.Write(v4.x); output.Write(v4.y); output.Write(v4.z); output.Write(v4.w); return; }
        if (value is Color color) { output.Write((byte)15); output.Write(color.r); output.Write(color.g); output.Write(color.b); output.Write(color.a); return; }
        if (value is Bounds bounds) { output.Write((byte)16); Vector3 centre = bounds.center, extents = bounds.extents; output.Write(centre.x); output.Write(centre.y); output.Write(centre.z); output.Write(extents.x); output.Write(extents.y); output.Write(extents.z); return; }
        if (value is LayerMask mask) { output.Write((byte)17); output.Write(mask.value); return; }
        if (type.IsEnum) { output.Write((byte)7); WriteType(output, type); output.Write(Convert.ToInt64(value)); return; }
        if (type.IsPrimitive || value is string || value is decimal)
        {
            output.Write((byte)1); TypeCode code = Type.GetTypeCode(type); output.Write((byte)code);
            switch (code)
            {
                case TypeCode.Boolean: output.Write((bool)value); break;
                case TypeCode.Char: output.Write((char)value); break;
                case TypeCode.String: output.Write((string)value); break;
                case TypeCode.Single: output.Write((float)value); break;
                case TypeCode.Double: output.Write((double)value); break;
                case TypeCode.Decimal: output.Write((decimal)value); break;
                case TypeCode.UInt64: output.Write((ulong)value); break;
                default: output.Write(Convert.ToInt64(value)); break;
            }
            return;
        }
        if (value is UnityEngine.Object unity) { string key = encodeUnity(unity); if (key == null) output.Write((byte)0); else { output.Write((byte)2); output.Write(key); } return; }
        if (value is PathNode node) { context.Coordinates(node, out int room, out int index); output.Write((byte)3); output.Write(room); output.Write(index); return; }
        if (context.TryDefinition(value, out string definition)) { output.Write((byte)4); output.Write(definition); return; }
        if (!CoopStateGraph.AllowedType(type) && !(value is Delegate)) throw new InvalidOperationException("Unsupported state type " + type);
        if (type.IsValueType) { output.Write((byte)9); WriteType(output, type); WriteFields(output, value); return; }
        Identity identity = Id(value); output.Write((byte)5); output.Write(identity.id);
        if (reached.Add(identity.id)) pending.Enqueue(value);
    }
    private object ReadValue(BinaryReader reader, Type expected)
    {
        switch (reader.ReadByte())
        {
            case 0: return null;
            case 1:
                TypeCode code = (TypeCode)reader.ReadByte();
                switch (code)
                {
                    case TypeCode.Boolean: return reader.ReadBoolean(); case TypeCode.Char: return reader.ReadChar(); case TypeCode.String: return reader.ReadString();
                    case TypeCode.Single: return reader.ReadSingle(); case TypeCode.Double: return reader.ReadDouble(); case TypeCode.Decimal: return reader.ReadDecimal();
                    case TypeCode.UInt64: return reader.ReadUInt64(); default: return Convert.ChangeType(reader.ReadInt64(), code);
                }
            case 2: return decodeUnity(reader.ReadString());
            case 3: return context.Node(reader.ReadInt32(), reader.ReadInt32());
            case 4: return context.Definition(reader.ReadString());
            case 5:
                long id = reader.ReadInt64(); object target = Allocate(id);
                if (expected != typeof(object) && !expected.IsInstanceOfType(target)) throw new InvalidDataException("State reference type mismatch");
                return target;
            case 7: return Enum.ToObject(ReadType(reader), reader.ReadInt64());
            case 9: Type type = ReadType(reader); object value = Activator.CreateInstance(type); ReadFields(reader, value); return value;
            case 10: return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            case 11: return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            case 12: return new Rect(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            case 13: return new Vector2(reader.ReadSingle(), reader.ReadSingle());
            case 14: return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            case 15: return new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            case 16: var bounds = new Bounds(); bounds.center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); bounds.extents = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); return bounds;
            case 17: return (LayerMask)reader.ReadInt32();
            default: throw new InvalidDataException("Invalid state value tag");
        }
    }
    private static int Count(BinaryReader reader)
    { int count = reader.ReadInt32(); if (count < 0 || count > 16 * 1024 * 1024) throw new InvalidDataException("Invalid state length"); return count; }
}
