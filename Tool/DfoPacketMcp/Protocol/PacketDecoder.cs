using System.Buffers.Binary;
using System.Text;

namespace DfoPacketMcp.Protocol;

public sealed class PacketDecoder
{
    private readonly ProtocolCatalog _catalog;

    public PacketDecoder(ProtocolCatalog catalog) => _catalog = catalog;

    public ParsedPacket Decode(ReadOnlySpan<byte> packet, PacketTransport transport = PacketTransport.Auto)
    {
        var diagnostics = new List<string>();
        if (packet.Length < 13)
            throw new InvalidDataException($"packet requires at least 13 bytes, got {packet.Length}");

        transport = ResolveTransport(packet, transport, diagnostics);
        var headerSize = transport == PacketTransport.Egress ? 15 : 13;
        if (packet.Length < headerSize)
            throw new InvalidDataException($"{transport} packet requires at least {headerSize} bytes, got {packet.Length}");

        var header = new PacketHeader(
            packet[0],
            BinaryPrimitives.ReadUInt16LittleEndian(packet[1..3]),
            BinaryPrimitives.ReadUInt32LittleEndian(packet[3..7]),
            headerSize,
            BinaryPrimitives.ReadUInt32LittleEndian(packet[7..11]),
            transport == PacketTransport.Egress
                ? BinaryPrimitives.ReadUInt32LittleEndian(packet[11..15])
                : null,
            transport == PacketTransport.Ingress
                ? BinaryPrimitives.ReadUInt16LittleEndian(packet[11..13])
                : null);
        var body = packet[headerSize..].ToArray();
        if (header.Length != 0 && header.Length != packet.Length)
            diagnostics.Add($"header.length={header.Length} differs from input={packet.Length}");

        var flow = transport == PacketTransport.Ingress
            ? PacketFlow.ClientToServer
            : PacketFlow.ServerToClient;
        var kind = header.CommandClass == 0 ? PacketKind.Noti : PacketKind.Cmd;
        if (flow == PacketFlow.ClientToServer && kind == PacketKind.Noti)
            diagnostics.Add("client-to-server command class 0 is not registered by the current server");
        _catalog.TryGet(flow, kind, header.Type, out var definition);
        var decoded = PacketSchemaRegistry.Decode(definition, body, diagnostics);
        return new ParsedPacket(header, flow, kind, definition, decoded.Variant, body, decoded.Fields, diagnostics)
        {
            RawPacket = packet.ToArray(),
        };
    }

    public static byte[] Encode(
        byte cmd,
        ushort type,
        ReadOnlySpan<byte> body,
        PacketTransport transport = PacketTransport.Egress,
        uint firstControl = 0,
        uint secondControl = 0,
        ushort sequence = 0)
    {
        if (transport == PacketTransport.Auto) transport = PacketTransport.Egress;
        var headerSize = transport == PacketTransport.Egress ? 15 : 13;
        var packet = new byte[headerSize + body.Length];
        packet[0] = cmd;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3, 4), (uint)(body.Length + headerSize));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7, 4), firstControl);
        if (transport == PacketTransport.Egress)
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(11, 4), secondControl);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(11, 2), sequence);
        body.CopyTo(packet.AsSpan(headerSize));
        return packet;
    }

    private static PacketTransport ResolveTransport(
        ReadOnlySpan<byte> packet,
        PacketTransport requested,
        List<string> diagnostics)
    {
        if (requested != PacketTransport.Auto) return requested;
        if (packet.Length >= 15 && packet[13] == 0 && packet[14] == 0)
        {
            diagnostics.Add("transport auto-detected as egress (15-byte server envelope)");
            return PacketTransport.Egress;
        }
        diagnostics.Add("transport auto-detected as ingress (13-byte client header); pass transport explicitly when ambiguous");
        return PacketTransport.Ingress;
    }
}

internal static class LegacySemanticFieldDecoder
{
    public static IReadOnlyDictionary<string, object?> Decode(
        PacketTypeDefinition? definition,
        byte[] body,
        List<string> diagnostics)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (definition is null)
        {
            fields["raw"] = Convert.ToHexString(body);
            diagnostics.Add("unknown packet type; body returned as raw");
            return fields;
        }

        fields["bodyLength"] = body.Length;
        fields["raw"] = Convert.ToHexString(body);
        switch (definition.Name)
        {
            case "LOGIN":
                DecodeLogin(body, fields, diagnostics);
                break;
            case "MOVE_MAP":
                DecodeMoveMap(body, fields, diagnostics);
                break;
            case "INCREASE_STATUS":
                if (body.Length == 2) fields["slotIndex"] = BinaryPrimitives.ReadInt16LittleEndian(body);
                else diagnostics.Add("INCREASE_STATUS expects 2-byte body");
                break;
            case "TOURNAMENT_REWARD_SELECT":
                if (body.Length == 2)
                {
                    fields["cardType"] = body[0];
                    fields["cardIndex"] = body[1];
                }
                else diagnostics.Add("TOURNAMENT_REWARD_SELECT expects 2-byte body");
                break;
            case "SELECT_ULTIMATE_DIFFICULTY":
                if (body.Length == 1) fields["difficulty"] = body[0];
                else diagnostics.Add("SELECT_ULTIMATE_DIFFICULTY expects 1-byte body");
                break;
            case "DIE_BLOOD_MONSTER":
                DecodeU16List(body, "sequenceIds", fields, diagnostics);
                break;
        }
        return fields;
    }

    private static void DecodeLogin(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        var reader = new PacketReader(body);
        if (!reader.TryReadDString(Encoding.ASCII, out var mid) ||
            !reader.TryReadDString(Encoding.ASCII, out var password))
        {
            diagnostics.Add("LOGIN body does not contain two valid dstr values");
            return;
        }
        fields["mId"] = mid;
        fields["passwordHash"] = password;
        fields["consumedBytes"] = reader.Offset;
    }

    private static void DecodeMoveMap(byte[] body, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 64) { diagnostics.Add("MOVE_MAP expects 64-byte body"); return; }
        var offset = 0;
        fields["nextX"] = body[offset++];
        fields["nextY"] = body[offset++];
        fields["pathPositionX"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4)); offset += 4;
        fields["pathPositionY"] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4)); offset += 4;
        fields["moveMode"] = body[offset++];
        fields["trapBits"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2)); offset += 2;
        var clear = new ushort[8];
        for (var i = 0; i < clear.Length; i++, offset += 2) clear[i] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2));
        var elapsed = new uint[8];
        for (var i = 0; i < elapsed.Length; i++, offset += 4) elapsed[i] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4));
        fields["memberMapClearValues"] = clear;
        fields["memberMapElapsedValues"] = elapsed;
        fields["clientTimingToken"] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2)); offset += 2;
        fields["clientStateFlag"] = body[offset];
    }

    private static void DecodeU16List(byte[] body, string name, Dictionary<string, object?> fields, List<string> diagnostics)
    {
        if (body.Length < 1) { diagnostics.Add("list body is empty"); return; }
        var count = body[0];
        if (body.Length != 1 + count * 2) diagnostics.Add($"list length mismatch count={count} body={body.Length}");
        var values = new List<ushort>();
        for (var i = 0; i < count && 1 + i * 2 + 2 <= body.Length; i++) values.Add(BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1 + i * 2, 2)));
        fields[name] = values;
    }
}
