using DfoServer.Game.Guilds;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildManagementPacketBuilder
    {
        // 0118AC80: present:u8; if present: id,u32 + name,msg DSTR + two u32.
        internal static byte[] Pending(GuildPendingApplication pending)
        {
            var w = new GamePacketWriter(); w.WriteByte(pending == null ? (byte)0 : (byte)1);
            if (pending != null)
            {
                w.WriteInt32(pending.Guild.Id); w.WriteClientDstr(pending.Guild.Name);
                w.WriteClientDstr(pending.Message); w.WriteUInt32(0); w.WriteUInt32(0);
            }
            return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.JOIN_GUILD_INFO, w.ToArray());
        }

        // 01125340 reads guild then member; its formatter uses the second
        // string as the player name (confirmed by the current kick dialog).
        internal static byte[] Left(string name, string guildName, bool kicked)
        {
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteByte(kicked ? (byte)2 : (byte)1);
            w.WriteClientDstr(guildName); w.WriteClientDstr(name);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.REQ_GUILD_SECEDE, w.ToArray());
        }

        // 011311A8..01131308: an extra byte, member name and resulting grade.
        internal static byte[] RankChanged(string name, byte rank)
        {
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteByte(0);
            w.WriteClientDstr(name); w.WriteByte(rank);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.SET_SUB_GUILD_MASTER, w.ToArray());
        }

        // 0118EFB0 and 0118E0F0 have no payload, clear the guild model/UI.
        internal static byte[] Cleared(bool disbanded = false) => GamePacketEnvelopeBuilder.Build(0,
            (ushort)(disbanded ? NotiPacketTypeA21.GUILD_DISMISS : NotiPacketTypeA21.GUILD_SECEDE_TO_USER), System.Array.Empty<byte>());
    }
}
