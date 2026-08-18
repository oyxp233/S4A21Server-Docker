using System;
using System.Globalization;
using DfoServer.Game.Currency;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryAuditRepository
    {
        internal static void Insert(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryAuditEvent auditEvent)
        {
            if (auditEvent == null || string.IsNullOrWhiteSpace(auditEvent.ActionName))
                return;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO inventory_audit_log (
    created_at,
    session_id,
    owner_scope,
    owner_id,
    character_id,
    account_id,
    action_name,
    list_type,
    slot_index,
    item_id,
    item_kind,
    value_before,
    value_after,
    count_before,
    count_after,
    count_delta,
    before_core_hash,
    after_core_hash,
    payload_json
) VALUES (
    @createdAt,
    @sessionId,
    @ownerScope,
    @ownerId,
    @characterId,
    @accountId,
    @actionName,
    @listType,
    @slotIndex,
    @itemId,
    @itemKind,
    @valueBefore,
    @valueAfter,
    @countBefore,
    @countAfter,
    @countDelta,
    @beforeCoreHash,
    @afterCoreHash,
    @payloadJson
);";
                command.Parameters.AddWithValue("@createdAt", auditEvent.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("@sessionId", auditEvent.SessionId == Guid.Empty ? (object)DBNull.Value : auditEvent.SessionId.ToString("D"));
                command.Parameters.AddWithValue("@ownerScope", string.IsNullOrWhiteSpace(auditEvent.OwnerScope) ? "character" : auditEvent.OwnerScope);
                command.Parameters.AddWithValue("@ownerId", auditEvent.OwnerId);
                command.Parameters.AddWithValue("@characterId", auditEvent.CharacterId);
                command.Parameters.AddWithValue("@accountId", auditEvent.AccountId);
                command.Parameters.AddWithValue("@actionName", auditEvent.ActionName);
                command.Parameters.AddWithValue("@listType", auditEvent.ListType.HasValue ? (object)(int)auditEvent.ListType.Value : DBNull.Value);
                command.Parameters.AddWithValue("@slotIndex", auditEvent.SlotIndex.HasValue ? (object)auditEvent.SlotIndex.Value : DBNull.Value);
                command.Parameters.AddWithValue("@itemId", auditEvent.ItemId);
                command.Parameters.AddWithValue("@itemKind", auditEvent.ItemKind);
                command.Parameters.AddWithValue("@valueBefore", auditEvent.ValueBefore);
                command.Parameters.AddWithValue("@valueAfter", auditEvent.ValueAfter);
                command.Parameters.AddWithValue("@countBefore", auditEvent.CountBefore);
                command.Parameters.AddWithValue("@countAfter", auditEvent.CountAfter);
                command.Parameters.AddWithValue("@countDelta", auditEvent.CountDelta);
                command.Parameters.AddWithValue("@beforeCoreHash", string.IsNullOrEmpty(auditEvent.BeforeCoreHash) ? (object)DBNull.Value : auditEvent.BeforeCoreHash);
                command.Parameters.AddWithValue("@afterCoreHash", string.IsNullOrEmpty(auditEvent.AfterCoreHash) ? (object)DBNull.Value : auditEvent.AfterCoreHash);
                command.Parameters.AddWithValue("@payloadJson", string.IsNullOrWhiteSpace(auditEvent.PayloadJson) ? "{}" : auditEvent.PayloadJson);
                command.ExecuteNonQuery();
            }
        }

        internal static ItemCore LoadPersistedSlotCore(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));

            if (listType == InventoryListType.AccountCargo)
                return LoadPersistedAccountCargoCore(connection, transaction, inventory.AccountId, slotIndex);

            return LoadPersistedCharacterCore(connection, transaction, inventory.CharacterId, listType, slotIndex);
        }

        internal static int LoadPersistedVirtualCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            short slotIndex)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));

            if (slotIndex >= InventoryService.MainVirtualCurrencySlotStart
                && slotIndex <= InventoryService.MainVirtualCurrencySlotEnd)
                return InventoryMainVirtualCountRepository.LoadCurrencyCount(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    slotIndex);

            if (slotIndex >= InventoryService.MainVirtualCubeSlotStart
                && slotIndex <= InventoryService.MainVirtualCubeSlotEnd)
            {
                foreach (var cube in CurrencyService.LoadCubeFragments(connection, transaction, inventory.AccountId))
                {
                    if (cube.Slot == slotIndex)
                        return Math.Max(0, cube.Count);
                }
            }

            if (slotIndex >= InventoryService.MainVirtualSoulSlotStart
                && slotIndex <= InventoryService.MainVirtualSoulSlotEnd)
            {
                foreach (var soul in CurrencyService.LoadSoulWarehouseCounts(connection, transaction, inventory.AccountId))
                {
                    if (soul.Slot == slotIndex)
                        return Math.Max(0, soul.Count);
                }
            }

            return 0;
        }

        private static ItemCore LoadPersistedCharacterCore(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType listType,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_core
FROM character_inventory_items
WHERE character_id = @characterId
  AND list_type = @listType
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                return ReadCore(command.ExecuteScalar());
            }
        }

        private static ItemCore LoadPersistedAccountCargoCore(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_core
FROM account_inventory_items
WHERE account_id = @accountId
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                return ReadCore(command.ExecuteScalar());
            }
        }

        private static ItemCore ReadCore(object value)
        {
            if (value == null || value == DBNull.Value)
                return null;

            var data = (byte[])value;
            if (data.Length < ItemCore.Size)
                return null;

            var core = ItemCore.FromBytes(data);
            return core.IsEmpty ? null : core;
        }
    }
}
