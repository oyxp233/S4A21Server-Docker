using DfoServer.Game.Inventory;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class EquipmentItemLockBuilder
    {
        public static byte[] BuildLockAck(InventoryListType listType, short slotIndex)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)listType);
            writer.WriteInt16(slotIndex);
            return writer.ToArray();
        }

        public static byte[] BuildLockError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }

        public static byte[] BuildUnlockAck(InventoryListType listType, short slotIndex, int remainingSeconds)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)listType);
            writer.WriteInt16(slotIndex);
            writer.WriteInt32(remainingSeconds);
            return writer.ToArray();
        }

        public static byte[] BuildUnlockError(byte errorCode, int remainingSeconds)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(errorCode);
            writer.WriteInt32(remainingSeconds);
            return writer.ToArray();
        }

        public static byte[] BuildUnlockCancelAck(InventoryListType listType, short slotIndex)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)listType);
            writer.WriteInt16(slotIndex);
            return writer.ToArray();
        }

        public static byte[] BuildUnlockNotice(InventoryListType listType, short slotIndex)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)listType);
            writer.WriteInt16(slotIndex);
            return writer.ToArray();
        }

        public static byte[] BuildLockList(IReadOnlyList<EquipmentItemLockEntry> entries)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16((ushort)(entries != null ? entries.Count : 0));
            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    writer.WriteByte((byte)entry.ListType);
                    writer.WriteInt16(entry.SlotIndex);
                    writer.WriteByte(entry.State);
                    if (entry.State == 2)
                        writer.WriteInt32(entry.RemainingSeconds);
                }
            }

            return writer.ToArray();
        }
    }
}
