using DfoServer.Game.Guilds;
using DfoServer.GameWorld;
using System.Linq;
using System.Collections.Generic;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildDungeonPacketBuilder
    {
        // Current DNF.exe SHA256 1F8244D3...B20B00DD: registration 02EF1EB0,
        // handler 005BC2A0 reads u8, four u32, then count*(u32,u32).
        // +34 = style; +38 = deadline (0271E969 subtracts server seconds);
        // +3C is consumed but its behavior is unconfirmed and remains zero.
        internal static byte[] Build(GuildDungeonSnapshot snapshot)
        {
            var w = new GamePacketWriter();
            w.WriteByte(snapshot == null ? (byte)0 : (byte)1);
            w.WriteInt32(snapshot?.StyleId ?? -1);
            w.WriteUInt32(checked((uint)(snapshot?.Period.EndUnix ?? 0)));
            w.WriteUInt32(0);
            var ids = snapshot == null ? System.Array.Empty<int>() : GuildDungeonCatalog.Current.Dungeons.Keys.OrderBy(i => i).ToArray();
            w.WriteInt32(ids.Length);
            foreach (var id in ids) { w.WriteInt32(id); w.WriteInt32(snapshot.ClearCounts.GetValueOrDefault(id)); }
            return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.GUILD_DUNGEON_INFO, w.ToArray());
        }
    }
}
