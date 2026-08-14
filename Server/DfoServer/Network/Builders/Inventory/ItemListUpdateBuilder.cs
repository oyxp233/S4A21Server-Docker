using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class ItemListUpdateBuilder
    {
        public static byte[] BuildUpdateBody(
            int characterId,
            int accountId,
            InventoryListType itemSpace,
            short slotIndex)
        {
            return BuildUpdateBody(characterId, accountId, itemSpace, new[] { slotIndex });
        }

        public static byte[] BuildUpdateBody(
            int characterId,
            int accountId,
            InventoryListType itemSpace,
            IReadOnlyList<short> slotIndexes)
        {
            var lease = ItemListPacketBuilder.GetOnlineInventoryLease(characterId);
            lock (lease.SyncRoot)
                return BuildItemSpaceUpdateBody(lease.Inventory, itemSpace, slotIndexes);
        }

        internal static byte[] BuildItemSpaceUpdateBody(
            InventoryService inventory,
            InventoryListType itemSpace,
            IReadOnlyList<short> slotIndexes)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));

            var slots = slotIndexes != null
                ? NormalizeSlots(slotIndexes)
                : new List<short>();
            var entries = new GamePacketWriter();
            ushort count = 0;
            var nowUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var slotIndex in slots)
                WriteOnlineUpdateEntry(entries, inventory, itemSpace, slotIndex, nowUnixTime, ref count);

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)itemSpace);
            writer.WriteUInt16(count);
            writer.WriteBytes(entries.ToArray());
            return writer.ToArray();
        }

        private static void WriteOnlineUpdateEntry(
            GamePacketWriter writer,
            InventoryService inventory,
            InventoryListType itemSpace,
            short slotIndex,
            long nowUnixTime,
            ref ushort count)
        {
            if (itemSpace == InventoryListType.Main && InventoryService.IsVirtualMainSlot(slotIndex))
            {
                ItemListPacketBuilder.WriteMainVirtualCountEntry(writer, inventory, slotIndex, ref count);
                return;
            }

            var core = inventory.GetItem(itemSpace, slotIndex);
            if (ItemListPacketBuilder.TryWriteOnlineEntry(
                    writer,
                    inventory,
                    itemSpace,
                    slotIndex,
                    core,
                    nowUnixTime))
            {
                count++;
                return;
            }

            ItemListProtocolWriter.WriteEmptyEntry(writer, itemSpace, slotIndex);
            count++;
        }

        private static List<short> NormalizeSlots(IReadOnlyList<short> slotIndexes)
        {
            var result = new List<short>();
            var seen = new HashSet<short>();
            foreach (var slotIndex in slotIndexes)
            {
                if (seen.Add(slotIndex))
                    result.Add(slotIndex);
            }

            return result;
        }

        public static byte[] BuildEmptyUpdates(InventoryListType itemSpace, IReadOnlyList<short> slotIndexes)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)itemSpace);
            writer.WriteUInt16((ushort)(slotIndexes != null ? slotIndexes.Count : 0));

            if (slotIndexes != null)
            {
                var entrySize = GetUpdateEntrySize(itemSpace);
                foreach (var slotIndex in slotIndexes)
                    WriteEmptyEntry(writer, slotIndex, entrySize);
            }

            return writer.ToArray();
        }

        private static int GetUpdateEntrySize(InventoryListType itemSpace)
        {
            switch (itemSpace)
            {
                case InventoryListType.Avatar:
                    return 0x7E;
                case InventoryListType.Equipment:
                case InventoryListType.Main:
                case InventoryListType.PersonalCargo:
                case InventoryListType.AccountCargo:
                case InventoryListType.QuickSlot:
                case InventoryListType.Pet:
                    return 0x54;
                default:
                    return 0x54;
            }
        }

        private static void WriteEmptyEntry(GamePacketWriter writer, short slotIndex, int entrySize)
        {
            writer.WriteInt16(slotIndex);
            writer.WriteInt32(-1);
            WriteEmptyEntryTail(writer, entrySize);
        }

        private static void WriteEmptyEntryTail(GamePacketWriter writer, int entrySize)
        {
            writer.WriteZeroBytes(entrySize - 6);
        }

    }
}
