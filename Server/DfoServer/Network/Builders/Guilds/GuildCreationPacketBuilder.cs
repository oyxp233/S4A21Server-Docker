namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildCreationPacketBuilder
    {
        // DNF.exe SHA256 1F8244D3...B20B00DD: CMD callback 0111A2C0 uses
        // the common success byte, and on failure one error byte (6A/6B/6C).
        internal static byte[] Ack(CmdPacketTypeA21 type, byte error = 0)
            => GamePacketEnvelopeBuilder.Build(1, (ushort)type,
                error == 0 ? new byte[] { 1 } : new byte[] { 0, error });

        // NOTI callback 0118AE20 reads exactly one byte; 1 sets create state to approved.
        internal static byte[] SinglePlayerPermit()
            => GamePacketEnvelopeBuilder.Build(0,
                (ushort)NotiPacketTypeA21.REPLY_GUILD_CREATE_PERMIT, new byte[] { 1 });

        internal static byte[] Failure(CmdPacketTypeA21 type)
            => GamePacketEnvelopeBuilder.Build(1, (ushort)type, new byte[] { 0, 0 });

        // 0118E350 reads u32 guild ID and GBK DSTR name, then updates the creator.
        internal static byte[] Created(Game.Guilds.GuildRecord guild)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(guild.Id);
            writer.WriteClientDstr(guild.Name);
            return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.GUILD_CREATE, writer.ToArray());
        }
    }
}
