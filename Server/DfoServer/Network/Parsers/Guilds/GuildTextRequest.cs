using DfoServer.Infrastructure;
using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Guilds
{
    // Current A21 captures: int32 byte length followed by GBK, without a terminator.
    internal static class GuildTextRequest
    {
        internal static bool TryParseApplication(byte[] body, out string guildName, out string message)
        {
            guildName = message = string.Empty;
            if (body == null || body.Length < 8) return false;
            int length = BinaryPrimitives.ReadInt32LittleEndian(body);
            if (length < 0 || length > Game.Guilds.GuildCreationRules.MaximumNameBytes || length > body.Length - 8) return false;
            return TryParse(body[..(length + 4)], Game.Guilds.GuildCreationRules.MaximumNameBytes, out _, out guildName)
                && TryParse(body[(length + 4)..], Game.Guilds.GuildCreationRules.MaximumPromotionBytes, out _, out message)
                && Game.Guilds.GuildCreationRules.IsValidPromotion(message);
        }

        internal static bool TryParse(byte[] body, int maximumBytes, out byte[] raw, out string text)
        {
            raw = Array.Empty<byte>();
            text = string.Empty;
            if (body == null || body.Length < 4) return false;
            int length = BinaryPrimitives.ReadInt32LittleEndian(body);
            if (length < 0 || length > maximumBytes || length != body.Length - 4) return false;
            raw = body.AsSpan(4).ToArray();
            return ClientTextEncoding.TryGetStringStrict(raw, out text) && text.IndexOf('\0') < 0;
        }
    }
}
