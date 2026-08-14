using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class EquipmentUpgradeRegenerationTransactionSelfTest
    {
        private const int AccountId = 985500;
        private const int CharacterId = 985501;
        private const int UpgradeTargetItemId = 0x00006B8B;
        private const short SourceSlot = 10;
        private const short ClearCubeSlot = 358;
        private const int InitialGold = 500_000_000;
        private const int InitialCubeCount = 1000;
        private const byte InitialUpgradeLevel = 12;

        public static int Run()
        {
            var failures = 0;
            try
            {
                RunUpgradeFault(
                    "target equipment write",
                    fixture => fixture.CreateUpgradeTargetWriteFailureTriggers(),
                    ref failures);
                RunUpgradeFault(
                    "gold write",
                    fixture => fixture.CreateGoldWriteFailureTrigger(),
                    ref failures);
                RunUpgradeFault(
                    "material write",
                    fixture => fixture.CreateCubeWriteFailureTrigger(),
                    ref failures);

                using (var fixture = new RegenerationFixture())
                {
                    fixture.CreateSourceDeleteFailureTrigger();
                    var committed = fixture.TryCommit(
                        out var failedResult,
                        out var persistenceFailed);
                    Check(
                        "regeneration source DELETE failure rejects commit",
                        !committed
                        && failedResult?.Success == true
                        && persistenceFailed,
                        ref failures);
                    Check(
                        "regeneration source DELETE failure restores source and materials",
                        fixture.HasInitialState()
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    committed = fixture.TryCommit(
                        out var result,
                        out persistenceFailed);
                    Check(
                        "regeneration retries after source persistence recovery",
                        committed
                        && !persistenceFailed
                        && fixture.HasCommittedState(result)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    committed = fixture.TryCommit(
                        out _,
                        out persistenceFailed);
                    Check(
                        "regeneration recovery consumes the original source only once",
                        !committed
                        && !persistenceFailed
                        && fixture.HasCommittedState(result)
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }

                using (var fixture = new RegenerationFixture())
                {
                    fixture.CreateResultInsertFailureTrigger();
                    var committed = fixture.TryCommit(
                        out var failedResult,
                        out var persistenceFailed);
                    Check(
                        "regeneration result INSERT failure rejects commit",
                        !committed
                        && failedResult?.Success == true
                        && persistenceFailed,
                        ref failures);
                    Check(
                        "regeneration result INSERT failure restores source and materials",
                        fixture.HasInitialState()
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    committed = fixture.TryCommit(
                        out var result,
                        out persistenceFailed);
                    Check(
                        "regeneration retries after result persistence recovery",
                        committed
                        && !persistenceFailed
                        && fixture.HasCommittedState(result)
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] equipment upgrade/regeneration transaction selftest threw: "
                    + ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "EquipmentUpgradeRegenerationTransactionSelfTest OK"
                    : "EquipmentUpgradeRegenerationTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void RunUpgradeFault(
            string label,
            Action<UpgradeFixture> createFault,
            ref int failures)
        {
            using (var fixture = new UpgradeFixture())
            {
                createFault(fixture);
                var committed = fixture.TryCommit(
                    out var failedResult,
                    out var persistenceFailed);
                Check(
                    "upgrade " + label + " failure rejects commit",
                    !committed
                    && failedResult?.Success == true
                    && persistenceFailed,
                    ref failures);
                Check(
                    "upgrade " + label + " failure restores equipment and costs",
                    fixture.HasInitialState()
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                committed = fixture.TryCommit(
                    out var result,
                    out persistenceFailed);
                Check(
                    "upgrade retries after " + label + " recovery",
                    committed
                    && result?.Success == true
                    && !persistenceFailed
                    && fixture.HasCommittedState(result)
                    && fixture.HasNoDirtyState(),
                    ref failures);
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

        private abstract class FixtureBase : IDisposable
        {
            protected FixtureBase(string prefix)
            {
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    prefix + "-" + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(
                    DatabasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(Database);
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; private set; }

            internal abstract void DropFailureTriggers();

            internal bool HasNoDirtyState()
            {
                return Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.DirtyMainVirtualCountSlots.Count == 0;
            }

            protected InventoryService LoadPersistedInventory()
            {
                using (var connection = Database.OpenConnection())
                {
                    return InventoryService.LoadFromDb(
                        connection,
                        CharacterId,
                        AccountId,
                        Database);
                }
            }

            protected void Persist(InventoryService inventory)
            {
                var seedLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                    throw new InvalidOperationException("unable to persist transaction fixture");

                Lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
            }

            protected void ExecuteNonQuery(string sql)
            {
                using (var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = DatabasePath,
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

            public void Dispose()
            {
                try
                {
                    DropFailureTriggers();
                }
                catch
                {
                }

                if (Lease != null)
                {
                    InventoryContext.Unregister(
                        Lease.SessionId,
                        Lease.CharacterId);
                }

                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    try
                    {
                        var path = DatabasePath + suffix;
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private sealed class UpgradeFixture : FixtureBase
        {
            private readonly string _initialTargetBytes;

            internal UpgradeFixture()
                : base("equipment-upgrade-transaction")
            {
                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    Database);
                inventory.SetListParam16(
                    InventoryListType.Main,
                    ItemSlotBoundService.MainExpandStageFull);
                var metadata = ItemMetadataResolver.Resolve(UpgradeTargetItemId);
                var target = ItemCore.Create(
                    ItemCore.KindEquipment,
                    UpgradeTargetItemId);
                target.Uid = 985510;
                target.Durability = metadata.Durability;
                target.Upgrade = InitialUpgradeLevel;
                inventory.SetItem(
                    InventoryListType.Main,
                    SourceSlot,
                    target);
                inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    InitialGold);
                inventory.SetMainVirtualCount(
                    ClearCubeSlot,
                    3037,
                    InitialCubeCount);
                _initialTargetBytes = Convert.ToHexString(target.ToBytes());
                Persist(inventory);
            }

            internal bool TryCommit(
                out ItemUpgradeResult result,
                out bool persistenceFailed)
            {
                return InventoryItemUpgradeCommitService.TryCommit(
                    Lease,
                    new ItemUpgradeCommand
                    {
                        Method = ItemUpgradeMethod.Reinforce,
                        Mode = ItemUpgradeMode.Reinforce,
                        TargetSlotIndex = SourceSlot,
                        TargetItemTemplateId = UpgradeTargetItemId,
                        MaterialSlotIndex = ClearCubeSlot,
                        OptionalTicketSlotIndex = -1,
                    },
                    out result,
                    out persistenceFailed);
            }

            internal bool HasInitialState()
            {
                return HasInitialState(Lease.Inventory)
                    && HasInitialState(LoadPersistedInventory());
            }

            internal bool HasCommittedState(ItemUpgradeResult result)
            {
                if (result == null || !result.Success)
                    return false;

                var online = Lease.Inventory;
                var persisted = LoadPersistedInventory();
                var onlineTarget = online.GetItem(
                    InventoryListType.Main,
                    SourceSlot);
                var persistedTarget = persisted.GetItem(
                    InventoryListType.Main,
                    SourceSlot);
                var targetsMatch = onlineTarget == null
                    ? persistedTarget == null
                    : persistedTarget != null
                        && onlineTarget.ToBytes().SequenceEqual(
                            persistedTarget.ToBytes());
                return targetsMatch
                    && GetGold(online) == result.UpdatedGold
                    && GetGold(persisted) == result.UpdatedGold
                    && GetCube(online) == result.MaterialRemainingStackCount
                    && GetCube(persisted) == result.MaterialRemainingStackCount
                    && result.UpdatedGold < InitialGold
                    && result.MaterialRemainingStackCount < InitialCubeCount;
            }

            internal void CreateUpgradeTargetWriteFailureTriggers()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_upgrade_target_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {SourceSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected upgrade target update failure');
END;
CREATE TRIGGER fail_upgrade_target_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {SourceSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected upgrade target delete failure');
END;");
            }

            internal void CreateGoldWriteFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_upgrade_gold_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {InventoryService.MainVirtualCurrencySlotStart}
BEGIN
    SELECT RAISE(ABORT, 'injected upgrade gold update failure');
END;");
            }

            internal void CreateCubeWriteFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_upgrade_cube_update
BEFORE UPDATE OF cube_clear ON accounts
WHEN OLD.account_id = {AccountId}
BEGIN
    SELECT RAISE(ABORT, 'injected upgrade cube update failure');
END;");
            }

            internal override void DropFailureTriggers()
            {
                if (!File.Exists(DatabasePath))
                    return;

                ExecuteNonQuery(@"
DROP TRIGGER IF EXISTS fail_upgrade_target_update;
DROP TRIGGER IF EXISTS fail_upgrade_target_delete;
DROP TRIGGER IF EXISTS fail_upgrade_gold_update;
DROP TRIGGER IF EXISTS fail_upgrade_cube_update;");
            }

            private bool HasInitialState(InventoryService inventory)
            {
                var target = inventory.GetItem(
                    InventoryListType.Main,
                    SourceSlot);
                return target != null
                    && Convert.ToHexString(target.ToBytes()) == _initialTargetBytes
                    && GetGold(inventory) == InitialGold
                    && GetCube(inventory) == InitialCubeCount;
            }

            private static int GetGold(InventoryService inventory)
            {
                return inventory.GetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
            }

            private static int GetCube(InventoryService inventory)
            {
                return inventory.GetMainVirtualCount(ClearCubeSlot)?.Count ?? 0;
            }
        }

        private sealed class RegenerationFixture : FixtureBase
        {
            private readonly Dictionary<int, int> _initialMaterialCounts =
                new Dictionary<int, int>();

            internal RegenerationFixture()
                : base("equipment-regeneration-transaction")
            {
                var spec = ResolveRegenerationSpec();
                SourceItemId = spec.SourceItemId;
                Requirements = spec.Requirements;

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    Database);
                inventory.SetListParam16(
                    InventoryListType.Main,
                    ItemSlotBoundService.MainExpandStageFull);
                var source = InventoryCreateService.CreateCore(
                    ItemCore.KindEquipment,
                    SourceItemId,
                    ItemCreateReason.Unknown,
                    1);
                source.Uid = 985520;
                inventory.SetItem(
                    InventoryListType.Main,
                    SourceSlot,
                    source);

                foreach (var requirement in Requirements)
                {
                    var count = checked(requirement.Count + 10);
                    if (!InventoryRewardGrantService.TryCreateAndInsert(
                            inventory,
                            requirement.ItemTemplateId,
                            ItemCreateReason.Unknown,
                            count,
                            out var grant)
                        || !grant.Success)
                    {
                        throw new InvalidOperationException(
                            "unable to seed regeneration material "
                            + requirement.ItemTemplateId);
                    }

                    _initialMaterialCounts[requirement.ItemTemplateId] =
                        inventory.CountMainItem(requirement.ItemTemplateId);
                }

                Persist(inventory);
            }

            internal int SourceItemId { get; }
            internal IReadOnlyList<EquipmentRegenerationMaterial> Requirements { get; }

            internal bool TryCommit(
                out EquipmentRegenerationResult result,
                out bool persistenceFailed)
            {
                return InventoryEquipmentRegenerationCommitService.TryCommit(
                    Lease,
                    new EquipmentRegenerationRequest
                    {
                        SourceSlotIndex = SourceSlot,
                        Mode = 1,
                        Part = 0,
                    },
                    out result,
                    out persistenceFailed);
            }

            internal bool HasInitialState()
            {
                return HasInitialState(Lease.Inventory)
                    && HasInitialState(LoadPersistedInventory());
            }

            internal bool HasCommittedState(EquipmentRegenerationResult result)
            {
                if (result == null || !result.Success)
                    return false;

                return HasCommittedState(Lease.Inventory, result)
                    && HasCommittedState(LoadPersistedInventory(), result);
            }

            internal void CreateSourceDeleteFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_regeneration_source_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {SourceSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected regeneration source delete failure');
END;");
            }

            internal void CreateResultInsertFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_regeneration_result_insert
BEFORE INSERT ON character_inventory_items
WHEN NEW.character_id = {CharacterId}
 AND NEW.list_type = {(int)InventoryListType.Main}
 AND NEW.slot_index >= 9
 AND NEW.slot_index <= 64
BEGIN
    SELECT RAISE(ABORT, 'injected regeneration result insert failure');
END;");
            }

            internal override void DropFailureTriggers()
            {
                if (!File.Exists(DatabasePath))
                    return;

                ExecuteNonQuery(@"
DROP TRIGGER IF EXISTS fail_regeneration_source_delete;
DROP TRIGGER IF EXISTS fail_regeneration_result_insert;");
            }

            private bool HasInitialState(InventoryService inventory)
            {
                if (inventory.GetItem(
                        InventoryListType.Main,
                        SourceSlot)?.ItemId != SourceItemId)
                {
                    return false;
                }

                foreach (var pair in _initialMaterialCounts)
                {
                    if (inventory.CountMainItem(pair.Key) != pair.Value)
                        return false;
                }

                return true;
            }

            private bool HasCommittedState(
                InventoryService inventory,
                EquipmentRegenerationResult result)
            {
                if (inventory.GetItem(
                        InventoryListType.Main,
                        SourceSlot) != null
                    || inventory.GetItem(
                        InventoryListType.Main,
                        result.ResultSlotIndex)?.ItemId
                        != result.ResultItemTemplateId)
                {
                    return false;
                }

                var consumedByItem = result.ConsumedEntries
                    .GroupBy(entry => entry.ItemTemplateId)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Sum(entry => entry.Count));
                foreach (var pair in _initialMaterialCounts)
                {
                    var consumed = consumedByItem.TryGetValue(
                        pair.Key,
                        out var count)
                        ? count
                        : 0;
                    if (inventory.CountMainItem(pair.Key)
                        != pair.Value - consumed)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private sealed class RegenerationSpec
        {
            internal int SourceItemId { get; set; }
            internal IReadOnlyList<EquipmentRegenerationMaterial> Requirements { get; set; }
        }

        private static RegenerationSpec ResolveRegenerationSpec()
        {
            var config = EquipmentRegenerationConfigProvider.LoadCurrent();
            EquipmentRegenerationCandidateCatalog.Warmup();
            for (var rarity = 3; rarity <= 6; rarity++)
            {
                for (var level = 1; level <= 90; level++)
                {
                    foreach (var candidate in
                        EquipmentRegenerationCandidateCatalog.GetCandidates(
                            rarity,
                            level))
                    {
                        if (!ItemMetadataResolver.TryLoadEquipmentFile(
                                candidate.ItemTemplateId,
                                out var source)
                            || source == null
                            || !IsSealing(source.AttachType)
                            || config.ExceptItemIds.Contains(candidate.ItemTemplateId)
                            || EquipmentRegenerationCandidateCatalog.IsGeneratedModifiedOption(
                                source.Rarity,
                                source.CreationRate,
                                source.ForceResultItemRule)
                            || !InventoryEquipmentRegenerationService.HasCompoundDropEligibility(
                                source.Rarity,
                                source.MinimumLevel,
                                false,
                                source.CreationRate,
                                config.ExceptionWeights.ContainsKey(
                                    candidate.ItemTemplateId)))
                        {
                            continue;
                        }

                        var requirements = config.GetMaterials(
                            source.Rarity,
                            false,
                            source.MinimumLevel);
                        var pools = InventoryEquipmentRegenerationService.BuildCandidatePools(
                            source.Rarity,
                            source.MinimumLevel,
                            source.ItemGroupName,
                            false,
                            0,
                            source.Rarity,
                            config);
                        if (requirements.Count == 0
                            || pools.SelectMany(pool => pool.Candidates).Any() == false)
                        {
                            continue;
                        }

                        return new RegenerationSpec
                        {
                            SourceItemId = candidate.ItemTemplateId,
                            Requirements = requirements,
                        };
                    }
                }
            }

            throw new InvalidOperationException(
                "unable to resolve regeneration fixture from current PVF");
        }

        private static bool IsSealing(string attachType)
        {
            return string.Equals(
                (attachType ?? string.Empty)
                    .Replace("`", string.Empty)
                    .Replace("[", string.Empty)
                    .Replace("]", string.Empty)
                    .Trim(),
                "sealing",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void SeedCharacter(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, 'equipment-upgrade-regeneration-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes(
                            "EquipmentUpgradeRegenerationTransaction"));
                    command.ExecuteNonQuery();
                }
            });
        }
    }
}
