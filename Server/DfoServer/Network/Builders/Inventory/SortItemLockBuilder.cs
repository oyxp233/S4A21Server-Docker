using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    public static class SortItemLockBuilder
    {
        public static byte[] BuildLock(SortItemLockEntry entry)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(entry.State);
            writer.WriteByte((byte)entry.ListType);
            writer.WriteInt16(entry.SlotIndex);
            return writer.ToArray();
        }

        public static byte[] BuildUnlock(InventoryListType listType, short slotIndex)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte((byte)listType);
            writer.WriteInt16(slotIndex);
            return writer.ToArray();
        }
    }
}
