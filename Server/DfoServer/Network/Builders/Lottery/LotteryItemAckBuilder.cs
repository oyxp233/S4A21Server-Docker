using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    public static class LotteryItemAckBuilder
    {
        public static byte[] BuildPhaseStart(short slotIndex, int previewItemTemplateId)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(slotIndex);
            writer.WriteUInt16(0);
            writer.WriteInt32(previewItemTemplateId);
            writer.WriteInt32(previewItemTemplateId);
            return writer.ToArray();
        }

        public static byte[] BuildPhaseStartWithoutPreview()
        {
            return BuildPhaseStart(-1, 0);
        }

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
            WriteEmptyEquipmentSocketExtension(writer, rewardItem);
            WriteEmptyInvenItemTail(writer);
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

        private static void WriteEmptyInvenItemTail(GamePacketWriter writer)
        {
            writer.WriteByte(0x00); // empty RandomOption packet
            writer.WriteByte(0x00); // upgrade separate
            writer.WriteByte(0x00); // trade restriction
        }

        private static void WriteEmptyEquipmentSocketExtension(
            GamePacketWriter writer,
            ItemCore item)
        {
            if (ItemMetadataResolver.Resolve(item.ItemId).IsStackable)
                return;

            writer.WriteByte(0xEF);
            writer.WriteInt32(25);
            writer.WriteZeroBytes(25);
        }
    }
}
