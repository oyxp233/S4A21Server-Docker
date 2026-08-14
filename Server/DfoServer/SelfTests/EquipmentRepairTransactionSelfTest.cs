using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class EquipmentRepairTransactionSelfTest
    {
        private const int AccountId = 983200;
        private const int CharacterId = 983201;
        private const int InitialGold = 10_000_000;
        private const short SingleRepairSlot = 65;
        private const short RepairAllSlotA = 11;
        private const short RepairAllSlotB = 12;

        private static readonly int[] EquipmentCandidates =
        {
            0x00006B8B,
            101010653,
            365012,
            385000,
        };

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "equipment-repair-transaction-" +
                Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(database);

                var fixtureResolved = TryResolveFixture(
                    out var equipmentItemId,
                    out var maxDurability);
                Check(
                    "repair fixture resolves a PVF-backed durable equipment",
                    fixtureResolved,
                    ref failures);
                if (!fixtureResolved)
                    return 1;

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    database);
                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);

                var singleDurability = (ushort)Math.Max(
                    1,
                    maxDurability / 3);
                SetDamagedEquipment(
                    lease.Inventory,
                    InventoryListType.Main,
                    SingleRepairSlot,
                    equipmentItemId,
                    singleDurability);
                lease.Inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    InitialGold);
                SaveFixture(lease, "single repair fixture save failed");

                CreateItemUpdateFailureTrigger(
                    databasePath,
                    InventoryListType.Main,
                    SingleRepairSlot,
                    "fail_single_equipment_repair");
                RepairEquipmentResult failedSingleResult = null;
                var failedSingle = !OnlineInventoryMutationCommitCoordinator
                    .TryCommit(
                        lease,
                        "equipment-repair-single-selftest-failure",
                        (connection, transaction) =>
                            InventoryRepairService.TryRepairEquipment(
                                lease.Inventory,
                                InventoryListType.Main,
                                SingleRepairSlot,
                                quickRepair: false,
                                freeRepair: false,
                                out failedSingleResult));
                Check(
                    "single repair rejects an equipment update failure",
                    failedSingle
                    && failedSingleResult != null
                    && failedSingleResult.Cost > 0,
                    ref failures);
                Check(
                    "single repair failure reloads durability, gold and dirty state",
                    GetDurability(
                        lease.Inventory,
                        InventoryListType.Main,
                        SingleRepairSlot) == singleDurability
                    && lease.Inventory.CountMainItem(0) == InitialGold
                    && !lease.Inventory.DirtyListTypes.Contains(
                        InventoryListType.Main)
                    && lease.Inventory.DirtyMainVirtualCountSlots.Count == 0,
                    ref failures);
                Check(
                    "single repair failure leaves database unchanged",
                    LoadPersistedDurability(
                        database,
                        InventoryListType.Main,
                        SingleRepairSlot) == singleDurability
                    && LoadPersistedGold(database) == InitialGold,
                    ref failures);

                DropFailureTriggers(databasePath);
                RepairEquipmentResult singleRetryResult = null;
                var singleRetried = OnlineInventoryMutationCommitCoordinator
                    .TryCommit(
                        lease,
                        "equipment-repair-single-selftest-retry",
                        (connection, transaction) =>
                            InventoryRepairService.TryRepairEquipment(
                                lease.Inventory,
                                InventoryListType.Main,
                                SingleRepairSlot,
                                quickRepair: false,
                                freeRepair: false,
                                out singleRetryResult));
                var singleExpectedGold = InitialGold
                    - (singleRetryResult?.Cost ?? 0);
                Check(
                    "single repair retries after persistence recovery",
                    singleRetried
                    && singleRetryResult != null
                    && singleRetryResult.Cost > 0
                    && GetDurability(
                        lease.Inventory,
                        InventoryListType.Main,
                        SingleRepairSlot) == maxDurability
                    && lease.Inventory.CountMainItem(0)
                        == singleExpectedGold,
                    ref failures);
                Check(
                    "single repair retry persists durability and gold",
                    LoadPersistedDurability(
                        database,
                        InventoryListType.Main,
                        SingleRepairSlot) == maxDurability
                    && LoadPersistedGold(database) == singleExpectedGold,
                    ref failures);

                var repairAllDurabilityA = (ushort)Math.Max(
                    1,
                    maxDurability / 4);
                var repairAllDurabilityB = (ushort)Math.Max(
                    1,
                    maxDurability / 2);
                SetDamagedEquipment(
                    lease.Inventory,
                    InventoryListType.Equipment,
                    RepairAllSlotA,
                    equipmentItemId,
                    repairAllDurabilityA);
                SetDamagedEquipment(
                    lease.Inventory,
                    InventoryListType.Equipment,
                    RepairAllSlotB,
                    equipmentItemId,
                    repairAllDurabilityB);
                lease.Inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    InitialGold);
                SaveFixture(lease, "repair-all fixture save failed");

                CreateItemUpdateFailureTrigger(
                    databasePath,
                    InventoryListType.Equipment,
                    RepairAllSlotB,
                    "fail_repair_all_equipment");
                RepairEquipmentResult failedAllResult = null;
                var failedAll = !OnlineInventoryMutationCommitCoordinator
                    .TryCommit(
                        lease,
                        "equipment-repair-all-selftest-failure",
                        (connection, transaction) =>
                            InventoryRepairService.TryRepairEquipment(
                                lease.Inventory,
                                InventoryListType.Equipment,
                                -1,
                                quickRepair: true,
                                freeRepair: false,
                                out failedAllResult));
                Check(
                    "repair-all rejects a later equipment update failure",
                    failedAll
                    && failedAllResult != null
                    && failedAllResult.Cost > 0,
                    ref failures);
                Check(
                    "repair-all failure reloads every durability and gold value",
                    GetDurability(
                        lease.Inventory,
                        InventoryListType.Equipment,
                        RepairAllSlotA) == repairAllDurabilityA
                    && GetDurability(
                        lease.Inventory,
                        InventoryListType.Equipment,
                        RepairAllSlotB) == repairAllDurabilityB
                    && lease.Inventory.CountMainItem(0) == InitialGold
                    && !lease.Inventory.DirtyListTypes.Contains(
                        InventoryListType.Equipment)
                    && lease.Inventory.DirtyMainVirtualCountSlots.Count == 0,
                    ref failures);
                Check(
                    "repair-all failure leaves database unchanged",
                    LoadPersistedDurability(
                        database,
                        InventoryListType.Equipment,
                        RepairAllSlotA) == repairAllDurabilityA
                    && LoadPersistedDurability(
                        database,
                        InventoryListType.Equipment,
                        RepairAllSlotB) == repairAllDurabilityB
                    && LoadPersistedGold(database) == InitialGold,
                    ref failures);

                DropFailureTriggers(databasePath);
                RepairEquipmentResult repairAllRetryResult = null;
                var repairAllRetried = OnlineInventoryMutationCommitCoordinator
                    .TryCommit(
                        lease,
                        "equipment-repair-all-selftest-retry",
                        (connection, transaction) =>
                            InventoryRepairService.TryRepairEquipment(
                                lease.Inventory,
                                InventoryListType.Equipment,
                                -1,
                                quickRepair: true,
                                freeRepair: false,
                                out repairAllRetryResult));
                var repairAllExpectedGold = InitialGold
                    - (repairAllRetryResult?.Cost ?? 0);
                Check(
                    "repair-all retries after persistence recovery",
                    repairAllRetried
                    && repairAllRetryResult != null
                    && repairAllRetryResult.Cost > 0
                    && GetDurability(
                        lease.Inventory,
                        InventoryListType.Equipment,
                        RepairAllSlotA) == maxDurability
                    && GetDurability(
                        lease.Inventory,
                        InventoryListType.Equipment,
                        RepairAllSlotB) == maxDurability
                    && lease.Inventory.CountMainItem(0)
                        == repairAllExpectedGold,
                    ref failures);
                Check(
                    "repair-all retry persists every durability and gold value",
                    LoadPersistedDurability(
                        database,
                        InventoryListType.Equipment,
                        RepairAllSlotA) == maxDurability
                    && LoadPersistedDurability(
                        database,
                        InventoryListType.Equipment,
                        RepairAllSlotB) == maxDurability
                    && LoadPersistedGold(database)
                        == repairAllExpectedGold,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] equipment repair transaction selftest threw: "
                    + ex);
                failures++;
            }
            finally
            {
                DropFailureTriggers(databasePath);
                if (lease != null)
                {
                    InventoryContext.Unregister(
                        lease.SessionId,
                        lease.CharacterId);
                }

                DeleteDatabaseFiles(databasePath);
            }

            Console.WriteLine(
                failures == 0
                    ? "EquipmentRepairTransactionSelfTest OK"
                    : "EquipmentRepairTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static bool TryResolveFixture(
            out int equipmentItemId,
            out ushort maxDurability)
        {
            equipmentItemId = 0;
            maxDurability = 0;
            foreach (var candidate in EquipmentCandidates)
            {
                if (!ItemMetadataResolver.TryLoadEquipmentFile(
                        candidate,
                        out var equipment)
                    || equipment == null
                    || equipment.Durability <= 1
                    || equipment.RepairPrice <= 0
                    || !ItemMetadataResolver.IsRepairAllEligible(candidate))
                {
                    continue;
                }

                var damagedDurability = Math.Max(
                    1,
                    equipment.Durability / 3);
                if (EquipmentRepairPriceProvider.CalcRepairCost(
                        equipment.RepairPrice,
                        equipment.Grade,
                        equipment.Durability,
                        damagedDurability,
                        upgradeLevel: 0,
                        quickRepair: false) <= 0)
                {
                    continue;
                }

                equipmentItemId = candidate;
                maxDurability = (ushort)equipment.Durability;
                return true;
            }

            return false;
        }

        private static void SetDamagedEquipment(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int itemId,
            ushort durability)
        {
            var core = InventoryCreateService.CreateCore(
                ItemCore.KindEquipment,
                itemId,
                ItemCreateReason.AdminGrant,
                1);
            core.Durability = durability;
            if (!inventory.SetItem(listType, slotIndex, core))
            {
                throw new InvalidOperationException(
                    "unable to prepare repair equipment slot " + slotIndex);
            }
        }

        private static ushort GetDurability(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex)
        {
            return inventory.GetItem(listType, slotIndex)?.Durability ?? 0;
        }

        private static void SaveFixture(
            InventoryLease lease,
            string failureMessage)
        {
            if (!InventoryPersistenceService.SaveDirty(lease))
                throw new InvalidOperationException(failureMessage);
        }

        private static void Seed(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, 'equipment-repair-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level, town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes(
                            "EquipmentRepairTransaction"));
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void CreateItemUpdateFailureTrigger(
            string databasePath,
            InventoryListType listType,
            short slotIndex,
            string triggerName)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER {triggerName}
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)listType}
 AND OLD.slot_index = {slotIndex}
BEGIN
    SELECT RAISE(ABORT, 'injected equipment repair failure');
END;");
        }

        private static void DropFailureTriggers(string databasePath)
        {
            if (!File.Exists(databasePath))
                return;

            try
            {
                ExecuteNonQuery(
                    databasePath,
                    @"
DROP TRIGGER IF EXISTS fail_single_equipment_repair;
DROP TRIGGER IF EXISTS fail_repair_all_equipment;");
            }
            catch
            {
            }
        }

        private static ushort LoadPersistedDurability(
            IGameDatabase database,
            InventoryListType listType,
            short slotIndex)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    database);
                return GetDurability(inventory, listType, slotIndex);
            }
        }

        private static int LoadPersistedGold(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    database);
                return inventory.CountMainItem(0);
            }
        }

        private static void ExecuteNonQuery(
            string databasePath,
            string sql)
        {
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    ForeignKeys = true,
                }.ToString()))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                "[" + (condition ? "PASS" : "FAIL") + "] " + label);
            if (!condition)
                failures++;
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var path = databasePath + suffix;
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }
    }
}
