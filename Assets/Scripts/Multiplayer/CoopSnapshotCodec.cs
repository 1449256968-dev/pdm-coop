using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

// Lossless binary envelope. Graph leaves remain bytes rather than Base64 strings;
// property names and repeated resource keys use a per-snapshot string table.
public static class CoopSnapshotCodec
{
    private const int Magic = 0x37504F43;
    private const int Limit = 64 * 1024 * 1024;
    public static byte[] Encode(JObject root)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Magic);
            Write(writer, root, new Dictionary<string, int>(), 0);
            if (stream.Length > Limit) throw new InvalidDataException("Snapshot envelope exceeds limit");
            return stream.ToArray();
        }
    }
    public static JObject Decode(byte[] bytes)
    {
        if (bytes.Length > Limit) throw new InvalidDataException("Snapshot envelope exceeds limit");
        using (var stream = new MemoryStream(bytes, false))
        using (var reader = new BinaryReader(stream, Encoding.UTF8))
        {
            if (reader.ReadInt32() != Magic) throw new InvalidDataException("Invalid snapshot format");
            var root = Read(reader, new List<string>(), 0) as JObject;
            if (root == null || stream.Position != stream.Length) throw new InvalidDataException("Invalid snapshot envelope");
            return root;
        }
    }
    private static void WriteString(BinaryWriter w, string value, Dictionary<string, int> strings)
    {
        if (strings.TryGetValue(value, out int id)) { w.Write(id); return; }
        id = strings.Count; strings.Add(value, id); w.Write(~id); w.Write(value);
    }
    private static string ReadString(BinaryReader r, List<string> strings)
    {
        int id = r.ReadInt32();
        if (id >= 0)
        {
            if (id >= strings.Count) throw new InvalidDataException("Invalid snapshot string reference");
            return strings[id];
        }
        if (~id != strings.Count) throw new InvalidDataException("Invalid snapshot string definition");
        string value = r.ReadString(); strings.Add(value); return value;
    }
    private static void Write(BinaryWriter w, JToken token, Dictionary<string, int> strings, int depth)
    {
        if (depth > 128) throw new InvalidDataException("Snapshot nesting exceeds limit");
        if (token == null || token.Type == JTokenType.Null || (token is JValue scalar && scalar.Value == null)) { w.Write((byte)0); return; }
        switch (token.Type)
        {
            case JTokenType.Object:
                w.Write((byte)1); w.Write(((JObject)token).Count);
                foreach (var property in ((JObject)token).Properties())
                { WriteString(w, property.Name, strings); Write(w, property.Value, strings, depth + 1); }
                break;
            case JTokenType.Array:
                w.Write((byte)2); w.Write(((JArray)token).Count);
                foreach (var item in (JArray)token) Write(w, item, strings, depth + 1);
                break;
            case JTokenType.String: w.Write((byte)3); WriteString(w, (string)token, strings); break;
            case JTokenType.Integer: w.Write((byte)4); w.Write((long)token); break;
            case JTokenType.Float: w.Write((byte)5); w.Write((double)token); break;
            case JTokenType.Boolean: w.Write((byte)((bool)token ? 7 : 6)); break;
            case JTokenType.Bytes:
                byte[] bytes = (byte[])token; w.Write((byte)8); w.Write(bytes.Length); w.Write(bytes); break;
            default: throw new InvalidDataException("Unsupported snapshot token " + token.Type);
        }
    }
    private static int Count(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > Limit || count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid snapshot length");
        return count;
    }
    private static JToken Read(BinaryReader r, List<string> strings, int depth)
    {
        if (depth > 128) throw new InvalidDataException("Snapshot nesting exceeds limit");
        switch (r.ReadByte())
        {
            case 0: return JValue.CreateNull();
            case 1:
                int properties = Count(r); var obj = new JObject();
                for (int i = 0; i < properties; i++) obj.Add(ReadString(r, strings), Read(r, strings, depth + 1));
                return obj;
            case 2:
                int count = Count(r); var array = new JArray();
                for (int i = 0; i < count; i++) array.Add(Read(r, strings, depth + 1));
                return array;
            case 3: return new JValue(ReadString(r, strings));
            case 4: return new JValue(r.ReadInt64());
            case 5: return new JValue(r.ReadDouble());
            case 6: return new JValue(false);
            case 7: return new JValue(true);
            case 8:
                int size = Count(r); byte[] data = r.ReadBytes(size);
                if (data.Length != size) throw new EndOfStreamException();
                return new JValue(data);
            default: throw new InvalidDataException("Invalid snapshot tag");
        }
    }
}
