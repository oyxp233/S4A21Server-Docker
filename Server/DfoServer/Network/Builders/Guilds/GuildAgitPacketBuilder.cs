using DfoServer.Network;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildAgitPacketBuilder
    {
        // DNF.exe 1F8244D3...B20B00DD, NOTI GUILD_AGIT_INFO handler 0118F230.
        // The handler reads one byte and 00C82EF0 stores it at the agit subobject +4.
        // Map load at 025A8C34 uses that dword: below 2 selects town 8 area 0,
        // below 4 selects area 1, otherwise area 3. Bytes 2 and 4 also call
        // 0270BD00 immediately, but only when the character is already in town 8.
        internal static byte[] Entrance(int agitLevel)
        {
            var writer = new GamePacketWriter();
            byte level = agitLevel >= 4 ? (byte)4 : agitLevel >= 2 ? (byte)2 : (byte)1;
            writer.WriteByte(level);
            return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.GUILD_AGIT_INFO, writer.ToArray());
        }
    }
}
