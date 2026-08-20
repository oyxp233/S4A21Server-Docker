using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    public static class LotteryItemAckBuilder
    {
        private const int A21CommonResultTailSize = 31;

        internal static byte[] BuildCommonItemResult(
            short sourceSlotIndex,
            short rewardSlotIndex,
            ItemCore rewardItem,
            int displayValue)
        {
            if (rewardItem == null || rewardItem.ItemId <= 0)
                return BuildError();

            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(sourceSlotIndex);
            writer.WriteInt16(rewardSlotIndex);
            writer.WriteInt32(rewardItem.ItemId);
            writer.WriteInt32(displayValue);
            writer.WriteUInt16(rewardItem.Durability);
            writer.WriteByte(rewardItem.Attr);
            writer.WriteByte(rewardItem.AmplifyType);
            writer.WriteUInt16(rewardItem.AmplifyValue);
            WriteA21CommonResultTail(writer, rewardItem);
            return writer.ToArray();
        }

        internal static byte[] BuildAvatarItemResult(
            short sourceSlotIndex,
            short rewardSlotIndex,
            ItemCore rewardItem,
            AvatarDetail detail)
        {
            if (rewardItem == null || rewardItem.ItemId <= 0)
                return BuildError();

            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(sourceSlotIndex);
            ItemListProtocolWriter.WriteAvatarEntry126(writer, rewardSlotIndex, rewardItem, detail);
            return writer.ToArray();
        }

        internal static byte[] BuildGoldResult(short sourceSlotIndex, int grantedGold)
        {
            if (grantedGold <= 0)
                return BuildError();

            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(sourceSlotIndex);
            writer.WriteInt16(0);
            writer.WriteInt32(0);
            writer.WriteInt32(grantedGold);
            writer.WriteUInt16(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteUInt16(0);
            WriteA21CommonResultTail(writer, null);
            return writer.ToArray();
        }

        public static byte[] BuildError()
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteInt16(-1);
            writer.WriteUInt16(0);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            return writer.ToArray();
        }

        private static void WriteA21CommonResultTail(
            GamePacketWriter writer,
            ItemCore item)
        {
            writer.WriteInt32(item?.ExpireTime ?? 0);
            writer.WriteZeroBytes(A21CommonResultTailSize - 4);
        }
    }
}
