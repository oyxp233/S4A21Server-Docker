using System.Buffers.Binary;
using System.Text;

namespace DfoPacketMcp.Protocol;

public enum PacketFlow
{
    ClientToServer,
    ServerToClient,
}

public enum PacketKind
{
    Cmd,
    Noti,
}

public enum PacketTransport
{
    Auto,
    Ingress,
    Egress,
}

public enum PacketSchemaStatus
{
    Structured,
    Partial,
    Inferred,
    Empty,
    Opaque,
    RawFallback,
}

public sealed record PacketFieldDefinition(
    string Name,
    string Type,
    int Offset,
    bool Optional,
    string Source);

public sealed record PacketBodySchema(
    int? ExactLength,
    int? MinimumLength,
    bool BodyIgnored,
    PacketFieldDefinition[] Fields,
    string[] Sources);

public sealed record PacketVariant(
    string Name,
    string? BodyBuilder,
    string[] Sources)
{
    public string? Discriminator { get; init; }
    public string Confidence { get; init; } = "source-evidence";
    public PacketBodySchema? Schema { get; init; }
    public string? FixedBodyHex { get; init; }
}

public sealed record PacketTypeDefinition(
    PacketFlow Flow,
    PacketKind Kind,
    ushort Type,
    string Name,
    string EnumName,
    bool Supported,
    PacketSchemaStatus SchemaStatus,
    string Semantic,
    string[] Sources,
    PacketVariant[] Variants,
    PacketBodySchema? InferredSchema);

public sealed record PacketHeader(
    byte CommandClass,
    ushort Type,
    uint Length,
    int HeaderSize,
    uint FirstControl,
    uint? SecondControl,
    ushort? Sequence);

public sealed record ParsedPacket(
    PacketHeader Header,
    PacketFlow Flow,
    PacketKind Kind,
    PacketTypeDefinition? Definition,
    string Variant,
    byte[] Body,
    IReadOnlyDictionary<string, object?> Fields,
    IReadOnlyList<string> Diagnostics)
{
    public string RawBodyHex => Convert.ToHexString(Body);
    public byte[] RawPacket { get; init; } = Array.Empty<byte>();
    public string RawPacketHex => Convert.ToHexString(RawPacket);
}

public sealed class PacketReader
{
    private readonly byte[] _body;
    private int _offset;

    public PacketReader(byte[] body) => _body = body ?? Array.Empty<byte>();

    public int Offset => _offset;
    public int Remaining => _body.Length - _offset;

    public bool TryReadByte(out byte value)
    {
        if (Remaining < 1) { value = 0; return false; }
        value = _body[_offset++];
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        if (Remaining < 2) { value = 0; return false; }
        value = BinaryPrimitives.ReadUInt16LittleEndian(_body.AsSpan(_offset, 2));
        _offset += 2;
        return true;
    }

    public bool TryReadInt16(out short value)
    {
        if (Remaining < 2) { value = 0; return false; }
        value = BinaryPrimitives.ReadInt16LittleEndian(_body.AsSpan(_offset, 2));
        _offset += 2;
        return true;
    }

    public bool TryReadUInt32(out uint value)
    {
        if (Remaining < 4) { value = 0; return false; }
        value = BinaryPrimitives.ReadUInt32LittleEndian(_body.AsSpan(_offset, 4));
        _offset += 4;
        return true;
    }

    public bool TryReadInt32(out int value)
    {
        if (Remaining < 4) { value = 0; return false; }
        value = BinaryPrimitives.ReadInt32LittleEndian(_body.AsSpan(_offset, 4));
        _offset += 4;
        return true;
    }

    public bool TryReadBytes(int count, out byte[] value)
    {
        if (count < 0 || Remaining < count) { value = Array.Empty<byte>(); return false; }
        value = _body.AsSpan(_offset, count).ToArray();
        _offset += count;
        return true;
    }

    public bool TryReadDString(Encoding encoding, out string value)
    {
        value = string.Empty;
        if (!TryReadInt32(out var length) || length < 0 || length > 1024 * 1024)
            return false;
        if (!TryReadBytes(length, out var bytes))
            return false;
        value = encoding.GetString(bytes);
        return true;
    }
}
