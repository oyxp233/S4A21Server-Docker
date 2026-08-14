using DfoServer.Game.Session;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class EnterSelectDungeonStateBuilder
    {
        public static byte[] BuildUserState(PlayerContext player)
            => BuildUserState(new[] { player.UserId }, player.UserState);

        public static byte[] BuildUserState(
            IReadOnlyList<ushort> userIds,
            byte userState)
        {
            var writer = new GamePacketWriter();
            var count = userIds?.Count ?? 0;

            writer.WriteByte((byte)count);
            for (var i = 0; i < count; i++)
            {
                writer.WriteUInt16(userIds[i]);
                writer.WriteByte(userState);
            }
            return writer.ToArray();
        }

        public static byte[] BuildEnterSelectDungeon(
            PlayerContext player,
            int towerOfDespairFloor)
            => BuildEnterSelectDungeon(
                new[] { player.UserId },
                towerOfDespairFloor);

        public static byte[] BuildEnterSelectDungeon(
            IReadOnlyList<ushort> userIds,
            int towerOfDespairFloor)
        {
            var writer = new GamePacketWriter();
            var count = userIds?.Count ?? 0;

            writer.WriteInt32(0x01);
            writer.WriteUInt16(0x0000);
            writer.WriteByte((byte)count);
            for (var i = 0; i < count; i++)
            {
                writer.WriteUInt16(userIds[i]);
                writer.WriteByte(0x00);
            }
            writer.WriteInt32(0x00);
            // For a solo entry the client reads this u16 at body offset 14.
            // A party entry naturally moves it by three bytes per extra member.
            writer.WriteUInt16((ushort)towerOfDespairFloor);
            writer.WriteZeroBytes(3);
            return writer.ToArray();
        }
    }
}
