using System.Collections.Generic;
using DfoServer.Game.Friends;

namespace DfoServer.Network.Builders.Friends
{
    internal static class BlacklistPacketBuilder
    {
        internal static byte[] Ack(CmdPacketTypeA21 command, byte error, string name = null)
        {
            var w = new GamePacketWriter(); w.WriteByte(error == 0 ? (byte)1 : (byte)0);
            if (error != 0) w.WriteByte(error);
            else w.WriteClientDstr(name);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)command, w.ToArray());
        }

        internal static byte[] List(IReadOnlyList<BlacklistEntry> entries)
        {
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteByte(checked((byte)entries.Count));
            foreach (var entry in entries)
            {
                var date = entry.CreatedAt.ToLocalTime();
                w.WriteClientDstr(entry.Name);
                w.WriteByte(checked((byte)(date.Year - 1900)));
                w.WriteByte((byte)(date.Month - 1)); w.WriteByte((byte)date.Day);
            }
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.REQUEST_BLACKLIST, w.ToArray());
        }
    }
}
