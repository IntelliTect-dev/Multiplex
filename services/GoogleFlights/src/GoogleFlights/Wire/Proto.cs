using System.Text;

namespace Multiplex.Sites.GoogleFlights.Wire;

/// <summary>
/// A protobuf message read without a schema: each field number maps to its values in order. A varint is
/// kept as <see cref="ulong"/>; a length-delimited value as its bytes, read on demand as a string or a
/// nested message. Only the two wire types Google Flights uses (0 and 2) are kept; the others are skipped.
/// </summary>
internal sealed class ProtoMessage
{
    private readonly Dictionary<int, List<object>> _fields;

    private ProtoMessage(Dictionary<int, List<object>> fields) => _fields = fields;

    public static ProtoMessage Parse(ReadOnlySpan<byte> data)
    {
        var fields = new Dictionary<int, List<object>>();
        var pos = 0;
        while (pos < data.Length)
        {
            var key = ReadVarint(data, ref pos);
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);
            object value;
            switch (wire)
            {
                case 0:
                    value = ReadVarint(data, ref pos);
                    break;
                case 1:
                    pos += 8;
                    continue;
                case 2:
                    var length = checked((int)ReadVarint(data, ref pos));
                    if (length < 0 || pos + length > data.Length)
                        throw new FormatException("A protobuf length runs past the end of the message.");
                    value = data.Slice(pos, length).ToArray();
                    pos += length;
                    break;
                case 5:
                    pos += 4;
                    continue;
                default:
                    throw new FormatException($"Unsupported protobuf wire type {wire}.");
            }
            if (!fields.TryGetValue(field, out var list))
                fields[field] = list = [];
            list.Add(value);
        }
        return new ProtoMessage(fields);
    }

    /// <summary>Parses standard or URL-safe base64, with or without padding.</summary>
    public static bool TryParseBase64(string? base64, out ProtoMessage message)
    {
        message = new ProtoMessage([]);
        if (string.IsNullOrEmpty(base64))
            return false;
        var normal = base64.Replace('-', '+').Replace('_', '/');
        normal = normal.PadRight(normal.Length + (4 - normal.Length % 4) % 4, '=');
        try
        {
            message = Parse(Convert.FromBase64String(normal));
            return true;
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            return false;
        }
    }

    public ulong? Varint(int field) =>
        _fields.TryGetValue(field, out var list) && list[0] is ulong v ? v : null;

    public string? String(int field) =>
        _fields.TryGetValue(field, out var list) && list[0] is byte[] b ? Encoding.UTF8.GetString(b) : null;

    public ProtoMessage? Message(int field) => Messages(field).FirstOrDefault();

    public IEnumerable<ProtoMessage> Messages(int field)
    {
        if (!_fields.TryGetValue(field, out var list))
            yield break;
        foreach (var item in list)
        {
            if (item is byte[] b)
                yield return Parse(b);
        }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (pos >= data.Length)
                throw new FormatException("A protobuf varint runs past the end of the message.");
            var b = data[pos++];
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return result;
        }
        throw new FormatException("A protobuf varint is longer than 64 bits.");
    }
}

/// <summary>Builds a protobuf message field by field, in the order written (the <c>tfs</c> encoder needs that order).</summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _buffer = new();

    public ProtoWriter Varint(int field, long value)
    {
        WriteVarint((ulong)field << 3);
        WriteVarint(unchecked((ulong)value));
        return this;
    }

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> value)
    {
        WriteVarint(((ulong)field << 3) | 2);
        WriteVarint((ulong)value.Length);
        _buffer.Write(value);
        return this;
    }

    public ProtoWriter String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    public ProtoWriter Message(int field, ProtoWriter message) => Bytes(field, message.ToArray());

    public byte[] ToArray() => _buffer.ToArray();

    /// <summary>URL-safe base64 without padding, as Google writes <c>tfs</c>.</summary>
    public string ToBase64Url() =>
        Convert.ToBase64String(ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void WriteVarint(ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            _buffer.WriteByte(b);
        } while (value != 0);
    }
}
