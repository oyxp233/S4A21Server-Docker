using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryNewCharacterSeedService
    {
        internal static void SeedInitialEquipment(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            (short slot, int itemId)[] equipment)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (characterId <= 0 || equipment == null || equipment.Length == 0)
                return;

            foreach (var (slot, itemId) in equipment)
                SeedInitialEquipmentSlot(connection, transaction, characterId, slot, itemId);
        }

        private static void SeedInitialEquipmentSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slot,
            int itemId)
        {
            if (!ItemMetadataResolver.TryResolveItemKind(itemId, out var itemKind))
                throw new InvalidDataException(
                    $"[SeedNewCharacter] 初始装备 itemId={itemId} 无法从 PVF 解析 itemKind");

            if (!ItemSlotBoundService.IsValidSlotForKind(
                    itemKind,
                    InventoryListType.Equipment,
                    slot,
                    ItemSlotBoundService.MainExpandStageFull))
            {
                throw new InvalidDataException(
                    $"[SeedNewCharacter] 初始装备 itemId={itemId} itemKind={itemKind} 不能放入穿戴槽 slot={slot}");
            }

            var core = InventoryCreateService.CreateCore(
                itemKind,
                itemId,
                ItemCreateReason.CharacterCreate,
                1);

            InventoryItemRepository.UpsertCharacterSlot(
                connection,
                transaction,
                characterId,
                InventoryListType.Equipment,
                slot,
                core);

            FileLogger.Log($"[SeedNewCharacter] 新表初始穿戴 slot={slot} itemId={itemId} kind={itemKind}");
        }
    }
}
