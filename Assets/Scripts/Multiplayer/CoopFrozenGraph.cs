using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

// Capture immutable scalar write operations on the main thread, then perform UTF8
// encoding and buffer construction on a worker. No live game object enters this tape.
public sealed class CoopFrozenGraph : IDisposable
{
    [StructLayout(LayoutKind.Explicit)]
    private struct Number
    {
        [FieldOffset(0)] public long integer;
        [FieldOffset(0)] public ulong unsigned;
        [FieldOffset(0)] public float single;
        [FieldOffset(0)] public double real;
        [FieldOffset(0)] public decimal money;
    }
    private struct Op { public byte kind; public Number number; public string text; }
    private sealed class Leaf { public int start, count, position; }
    private static readonly Stack<CoopFrozenGraph> pool = new Stack<CoopFrozenGraph>();
    private Op[] ops = new Op[16384];
    private int used;
    private readonly List<Leaf> leaves = new List<Leaf>();
    private Leaf current;
    private readonly TapeWriter writer;
    private bool returned;
    private CoopFrozenGraph() { writer = new TapeWriter(this); }
    public static CoopFrozenGraph Rent()
    {
        lock (pool)
        {
            var result = pool.Count == 0 ? new CoopFrozenGraph() : pool.Pop();
            result.returned = false; return result;
        }
    }
    public BinaryWriter Writer => writer;
    public int BeginLeaf()
    {
        current = new Leaf { start = used };
        leaves.Add(current); return leaves.Count - 1;
    }
    private void Append(Op op)
    {
        int index = current.start + current.position++;
        if (index >= ops.Length) Array.Resize(ref ops, ops.Length * 2);
        ops[index] = op;
        current.count = Math.Max(current.count, current.position);
        used = Math.Max(used, index + 1);
    }
    public byte[] ReplayLeaf(int index)
    {
        if (index < 0 || index >= leaves.Count) throw new InvalidDataException("Invalid frozen graph leaf");
        Leaf leaf = leaves[index];
        using (var stream = new MemoryStream())
        using (var output = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            for (int i = leaf.start; i < leaf.start + leaf.count; i++)
            {
                Op op = ops[i];
                switch (op.kind)
                {
                    case 1: output.Write((byte)op.number.integer); break;
                    case 2: output.Write((sbyte)op.number.integer); break;
                    case 3: output.Write((short)op.number.integer); break;
                    case 4: output.Write((ushort)op.number.integer); break;
                    case 5: output.Write((int)op.number.integer); break;
                    case 6: output.Write((uint)op.number.integer); break;
                    case 7: output.Write(op.number.integer); break;
                    case 8: output.Write(op.number.unsigned); break;
                    case 9: output.Write(op.number.single); break;
                    case 10: output.Write(op.number.real); break;
                    case 11: output.Write(op.number.money); break;
                    case 12: output.Write(op.number.integer != 0); break;
                    case 13: output.Write((char)op.number.integer); break;
                    case 14: output.Write(op.text); break;
                    default: throw new InvalidDataException("Invalid frozen graph operation");
                }
            }
            return stream.ToArray();
        }
    }
    // Called exactly once by the encoding worker on its exclusively-owned DTO.
    public void ResolveLeaves(JToken token)
    {
        if (token is JObject obj)
        {
            if (obj.Count == 1 && obj["b"]?.Type == JTokenType.Integer)
            { obj["b"] = new JValue(ReplayLeaf((int)obj["b"])); return; }
            foreach (JProperty property in obj.Properties()) ResolveLeaves(property.Value);
        }
        else if (token is JArray array) foreach (JToken item in array) ResolveLeaves(item);
    }
    public void Dispose()
    {
        if (returned) return;
        Array.Clear(ops, 0, used); used = 0; leaves.Clear(); current = null; returned = true;
        lock (pool) pool.Push(this);
    }
    private sealed class TapeStream : Stream
    {
        private readonly CoopFrozenGraph tape;
        public TapeStream(CoopFrozenGraph tape) { this.tape = tape; }
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => true;
        public override long Length => tape.current.count;
        public override long Position
        {
            get => tape.current.position;
            set
            {
                if (value < 0 || value > tape.current.count) throw new InvalidDataException("Invalid frozen graph seek");
                tape.current.position = (int)value;
            }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
    private sealed class TapeWriter : BinaryWriter
    {
        private readonly CoopFrozenGraph tape;
        public TapeWriter(CoopFrozenGraph tape) : base(new TapeStream(tape), Encoding.UTF8, true) { this.tape = tape; }
        private void Integer(byte kind, long value) { tape.Append(new Op { kind = kind, number = new Number { integer = value } }); }
        public override void Write(byte value) => Integer(1, value);
        public override void Write(sbyte value) => Integer(2, value);
        public override void Write(short value) => Integer(3, value);
        public override void Write(ushort value) => Integer(4, value);
        public override void Write(int value) => Integer(5, value);
        public override void Write(uint value) => Integer(6, value);
        public override void Write(long value) => Integer(7, value);
        public override void Write(ulong value) { tape.Append(new Op { kind = 8, number = new Number { unsigned = value } }); }
        public override void Write(float value) { tape.Append(new Op { kind = 9, number = new Number { single = value } }); }
        public override void Write(double value) { tape.Append(new Op { kind = 10, number = new Number { real = value } }); }
        public override void Write(decimal value) { tape.Append(new Op { kind = 11, number = new Number { money = value } }); }
        public override void Write(bool value) => Integer(12, value ? 1 : 0);
        public override void Write(char value) => Integer(13, value);
        public override void Write(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            tape.Append(new Op { kind = 14, text = value });
        }
    }
}
