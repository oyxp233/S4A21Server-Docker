using DfoServer.Game.Inventory;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class BuyItemAckBuilder
    {
        public static byte[] Build(InventoryMutationResult result, List<CostItemUpdate> costItems = null)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt32(result.UpdatedGold);
            writer.WriteInt32(result.UpdatedSp);
            writer.WriteInt32(0);
            writer.WriteInt32(result.UpdatedCoin);

            if (result.CoreSnapshot != null && result.SlotIndex >= 0)
            {
                ItemListProtocolWriter.WriteCommonEntry84(
                    writer,
                    result.SlotIndex,
                    result.CoreSnapshot);
            }
            else
            {
                WriteLegacyItemSummary(writer, result);
            }

            var count = costItems != null ? costItems.Count : 0;
            writer.WriteByte((byte)count);
            if (costItems != null)
            {
                foreach (var cost in costItems)
                {
                    writer.WriteInt32(cost.ItemTemplateId);
                    writer.WriteInt32(cost.NewStackCount);//应该是本次购买的数量
                }
            }

            return writer.ToArray();
        }

        private static void WriteLegacyItemSummary(
            GamePacketWriter writer,
            InventoryMutationResult result)
        {
            writer.WriteInt16(result.SlotIndex);
            writer.WriteInt32(result.ItemTemplateId);
            writer.WriteInt32(result.InstanceValue);
            writer.WriteUInt16(result.Durability);
            writer.WriteByte(0);
            writer.WriteUInt16(0);
            writer.WriteInt32(result.ExpireTime);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteUInt16(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
        }

        public static byte[] BuildError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }
    }

    public sealed class CostItemUpdate
    {
        public int ItemTemplateId { get; set; }
        public int NewStackCount { get; set; }
    }
}
