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

        // A21 NOTI 27. The first tutorial selection observed in the current
        // capture uses a 37-byte body; later selection entries use the
        // 39-byte variant. The caller supplies the tutorial-state variant;
        // dungeon ids are deliberately not interpreted here.
        // These layouts are intentionally separate from the retired A12 19B
        // builder because the user id/count offsets moved in the A21 client.
        public static byte[] BuildA21EnterSelectDungeon(
            ushort userId,
            bool initialTutorialLayout)
        {
            var writer = new GamePacketWriter();
            if (initialTutorialLayout)
            {
                writer.WriteZeroBytes(10);
                writer.WriteByte(1);
                writer.WriteUInt16(userId);
                writer.WriteZeroBytes(5);
                writer.WriteByte(1);
                writer.WriteZeroBytes(18);
                return writer.ToArray();
            }

            writer.WriteZeroBytes(8);
            writer.WriteByte(1);
            writer.WriteZeroBytes(3);
            writer.WriteByte(1);
            writer.WriteUInt16(userId);
            writer.WriteZeroBytes(5);
            writer.WriteByte(1);
            writer.WriteZeroBytes(18);
            return writer.ToArray();
        }
    }
}
