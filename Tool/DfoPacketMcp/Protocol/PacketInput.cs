using System.Text;

namespace DfoPacketMcp.Protocol;

public static class PacketInput
{
    public static byte[] ParseBytes(string? hex, string? base64)
    {
        if (!string.IsNullOrWhiteSpace(hex)) return ParseHex(hex);
        if (!string.IsNullOrWhiteSpace(base64)) return Convert.FromBase64String(base64);
        return Array.Empty<byte>();
    }

    public static byte[] ParseHex(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var value in text)
        {
            if (Uri.IsHexDigit(value)) builder.Append(value);
        }
        if ((builder.Length & 1) != 0)
            throw new FormatException("hex input must contain an even number of digits");
        return Convert.FromHexString(builder.ToString());
    }

    public static PacketFlow ParseFlow(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "c2s" or "clienttoserver" or "client-to-server" or "inbound" or "ingress" => PacketFlow.ClientToServer,
            "s2c" or "servertoclient" or "server-to-client" or "outbound" or "egress" => PacketFlow.ServerToClient,
            _ => throw new ArgumentException("flow must be c2s or s2c"),
        };

    public static PacketKind ParseKind(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "cmd" => PacketKind.Cmd,
            "noti" => PacketKind.Noti,
            _ => throw new ArgumentException("kind must be cmd or noti"),
        };

    public static PacketTransport ParseTransport(string? value, PacketFlow flow)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => flow == PacketFlow.ClientToServer ? PacketTransport.Ingress : PacketTransport.Egress,
            "ingress" or "recv" or "13" => PacketTransport.Ingress,
            "egress" or "send" or "15" => PacketTransport.Egress,
            _ => throw new ArgumentException("transport must be ingress, egress, or auto"),
        };
}
