using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    internal static class DeathTowerInventoryProjectionBuilder
    {
        internal static byte[] BuildMoveAckBody(InventoryMoveResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            // The A14 client resolves Main and QuickSlot to the same tower
            // inventory container. Canonicalize only the wire view so its ACK
            // applies the move locally; runtime endpoints remain strongly typed.
            return MoveItemSpaceAckBuilder.Build(new InventoryMoveResult
            {
                SourceListType = NormalizeMoveListType(result.SourceListType),
                SourceSlotIndex = result.SourceSlotIndex,
                MoveValue32 = result.MoveValue32,
                DestinationListType = NormalizeMoveListType(result.DestinationListType),
                DestinationSlotIndex = result.DestinationSlotIndex,
                Mutated = result.Mutated,
            });
        }

        internal static byte[] BuildFullListBody(
            DeathTowerSession tower,
            InventoryService inventory)
        {
            if (tower == null)
                throw new ArgumentNullException(nameof(tower));
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));

            // The A14 client maps Main and QuickSlot to one runtime container.
            // Build one authoritative Main view so a second full-list packet
            // cannot clear the first view again. QuickSlot remains typed in the
            // server overlay; its wire coordinates are the shared 3..8 slots.
            var transientBySlot = new Dictionary<short, TowerInventoryItem>();
            foreach (var pair in tower.InventoryItems)
            {
                var endpoint = pair.Key;
                if (pair.Value == null
                    || !IsProjectableEndpoint(endpoint))
                {
                    continue;
                }

                if (!transientBySlot.ContainsKey(endpoint.SlotIndex)
                    || endpoint.ListType == InventoryListType.QuickSlot)
                {
                    transientBySlot[endpoint.SlotIndex] = pair.Value;
                }
            }

            var entries = new GamePacketWriter();
            ushort count = 0;
            var nowUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            for (short slotIndex = InventoryService.MainVirtualCurrencySlotStart;
                slotIndex <= InventoryService.MainVirtualCurrencySlotEnd;
                slotIndex++)
            {
                ItemListPacketBuilder.WriteMainVirtualCountEntry(
                    entries,
                    inventory,
                    slotIndex,
                    ref count);
            }

            foreach (var item in inventory.GetItems(InventoryListType.Main))
            {
                if (transientBySlot.ContainsKey(item.Key))
                    continue;

                if (ItemListPacketBuilder.TryWriteOnlineEntry(
                        entries,
                        inventory,
                        InventoryListType.Main,
                        item.Key,
                        item.Value,
                        nowUnixTime))
                {
                    count++;
                }
            }

            for (short slotIndex = InventoryService.MainVirtualCubeSlotStart;
                slotIndex <= InventoryService.MainVirtualCubeSlotEnd;
                slotIndex++)
            {
                ItemListPacketBuilder.WriteMainVirtualCountEntry(
                    entries,
                    inventory,
                    slotIndex,
                    ref count);
            }

            foreach (var pair in transientBySlot.OrderBy(pair => pair.Key))
            {
                ItemListProtocolWriter.WriteCommonEntry84(
                    entries,
                    pair.Key,
                    DeathTowerItemSlotPolicy.CreateCore(pair.Value));
                count++;
            }

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)InventoryListType.Main);
            writer.WriteUInt16(inventory.GetListParam16(InventoryListType.Main));
            writer.WriteUInt16(count);
            writer.WriteBytes(entries.ToArray());
            return writer.ToArray();
        }

        private static bool IsProjectableEndpoint(
            DeathTowerInventoryEndpoint endpoint)
        {
            if (endpoint.ListType == InventoryListType.Main)
            {
                return endpoint.SlotIndex >= InventoryService.MainSlotStart
                    && endpoint.SlotIndex <= InventoryService.MainSlotEnd;
            }

            return endpoint.ListType == InventoryListType.QuickSlot
                && ItemSlotBoundService.IsMainQuickSlot(endpoint.SlotIndex);
        }

        private static InventoryListType NormalizeMoveListType(
            InventoryListType listType)
            => listType == InventoryListType.QuickSlot
                ? InventoryListType.Main
                : listType;
    }
}
