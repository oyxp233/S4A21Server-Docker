using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.Tournament;
using DfoServer.Game.Inventory;
using DfoServer.Game.Raid;
using DfoServer.Game.ReviveCoin;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class DomainTransactionBoundarySelfTest
    {
        private const int RewardItemId = 10000006;
        private const int EquipmentItemId = 101010653;
        private const int KaleidoBoxItemId =
            ResetItemQualityPolicyResolver.StandardKaleidoBoxItemId;
        private const int WaxItemId = 14;
        private const short EquipmentSlot = 11;
        private const short MaterialSlot = 65;
        private const int RaidTicketItemId =
            RaidEntryCostCommitService.EntryTicketItemId;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== DOMAIN_TRANSACTION_BOUNDARIES selftest ===");

            CheckRaidGoldRewardRollback();
            CheckRaidItemRewardRollback();
            CheckRaidAdmissionRollback();
            CheckHellEntryRollback();
            CheckTournamentEntryRollback();
            CheckResetItemQualityRollback();
            CheckWaxResealRollback();
            CheckReviveCoinConsumableCommit();
            CheckReviveCoinConsumableRollback();
            CheckEquipmentItemLockRollback();
            CheckEquipmentItemUnlockRollback();
            CheckEquipmentItemUnlockCancelRollback();

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void CheckRaidGoldRewardRollback()
        {
            const int accountId = 932000;
            const int characterId = 932001;
            var path = CreateDatabasePath("raid-gold");
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(path, accountId, characterId);
                lease = LoadLease(database, accountId, characterId);
                lease.Inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    1000);
                if (!InventoryPersistenceService.SaveDirty(lease))
                    throw new InvalidOperationException("gold fixture save failed");

                CreateAbortTrigger(
                    database,
                    "fail_raid_gold",
                    "BEFORE UPDATE OF item_core ON character_inventory_items",
                    $"OLD.character_id = {characterId} "
                    + "AND OLD.list_type = 0 AND OLD.slot_index = 0");

                var committed = RaidRewardCommitService.TryGrantGold(
                    lease,
                    100);
                Check(
                    "Raid gold reward failure reloads persisted gold",
                    !committed
                    && lease.Inventory.CountMainItem(0) == 1000
                    && LoadCount(database, accountId, characterId, 0) == 1000);
            }
            finally
            {
                Release(lease);
                DeleteDatabase(path);
            }
        }

        private static void CheckRaidItemRewardRollback()
        {
            const int accountId = 932010;
            const int characterId = 932011;
            var path = CreateDatabasePath("raid-item");
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(path, accountId, characterId);
                lease = LoadLease(database, accountId, characterId);
                CreateAbortTrigger(
                    database,
                    "fail_raid_item",
                    "BEFORE INSERT ON character_inventory_items",
                    $"NEW.character_id = {characterId} AND NEW.list_type = 0");

                var committed = RaidRewardCommitService.TryGrantItem(
                    lease,
                    RewardItemId,
                    1,
                    out var changes);
                Check(
                    "Raid item reward failure removes the uncommitted item",
                    !committed
                    && changes.Length == 0
                    && lease.Inventory.CountMainItem(RewardItemId) == 0
                    && LoadCount(
                        database,
                        accountId,
                        characterId,
                        RewardItemId) == 0);
            }
            finally
            {
                Release(lease);
                DeleteDatabase(path);
            }
        }

        private static void CheckRaidAdmissionRollback()
        {
            const int accountId1 = 932020;
            const int characterId1 = 932021;
            const int accountId2 = 932022;
            const int characterId2 = 932023;
            var path = CreateDatabasePath("raid-admission");
            InventoryLease lease1 = null;
            InventoryLease lease2 = null;
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                SeedIdentity(database, accountId1, characterId1);
                SeedIdentity(database, accountId2, characterId2);
                lease1 = LoadLease(database, accountId1, characterId1);
                lease2 = LoadLease(database, accountId2, characterId2);
                SeedItem(lease1, RaidTicketItemId, 1);
                SeedItem(lease2, RaidTicketItemId, 1);

                CreateAbortTrigger(
                    database,
                    "fail_second_raid_ticket",
                    "BEFORE DELETE ON character_inventory_items",
                    $"OLD.character_id = {characterId2} "
                    + $"AND OLD.list_type = 0");

                var committed = RaidEntryCostCommitService.TryConsume(
                    new[] { lease1, lease2 },
                    out var mutations);
                Check(
                    "Raid admission failure rolls back every member ticket",
                    !committed
                    && mutations.Count == 0
                    && lease1.Inventory.CountMainItem(RaidTicketItemId) == 1
                    && lease2.Inventory.CountMainItem(RaidTicketItemId) == 1
                    && LoadCount(
                        database,
                        accountId1,
                        characterId1,
                        RaidTicketItemId) == 1
                    && LoadCount(
                        database,
                        accountId2,
                        characterId2,
                        RaidTicketItemId) == 1);
            }
            finally
            {
                Release(lease2);
                Release(lease1);
                DeleteDatabase(path);
            }
        }

        private static void CheckHellEntryRollback()
        {
            const int accountId = 932030;
            const int characterId = 932031;
            var path = CreateDatabasePath("hell-entry");
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(path, accountId, characterId);
                lease = LoadLease(database, accountId, characterId);
                SeedItem(lease, RewardItemId, 1);
                var area = new WorldMapArea { AreaId = 1, HellDungeon = true };
                area.HellFreePassItems.Add(new HellTicketItem
                {
                    ItemId = RewardItemId,
                    Count = 1,
                });
                var entryCost = new DungeonEntryCostService(database);
                CreateAbortTrigger(
                    database,
                    "fail_hell_ticket",
                    "BEFORE DELETE ON character_inventory_items",
                    $"OLD.character_id = {characterId} AND OLD.list_type = 0");

                EntryCostResult result = null;
                var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "hell-entry-selftest",
                    (connection, transaction) =>
                    {
                        result = entryCost.TryConsumeAbyssPartyTicket(
                            lease.Inventory,
                            area,
                            dungeonMinLevel: 60);
                        return result != null && result.Success;
                    });
                Check(
                    "Hell entry failure restores the consumed ticket",
                    !committed
                    && result != null
                    && result.Success
                    && lease.Inventory.CountMainItem(RewardItemId) == 1
                    && LoadCount(
                        database,
                        accountId,
                        characterId,
                        RewardItemId) == 1);
            }
            finally
            {
                Release(lease);
                DeleteDatabase(path);
            }
        }

        private static void CheckTournamentEntryRollback()
        {
            const int accountId = 932040;
            const int characterId = 932041;
            var path = CreateDatabasePath("tournament-entry");
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(path, accountId, characterId);
                lease = LoadLease(database, accountId, characterId);
                SeedItem(lease, RewardItemId, 1);
                var definition = CreateTournamentDefinition(RewardItemId);
                var application = new TournamentDungeonApplicationService();
                CreateAbortTrigger(
                    database,
                    "fail_tournament_ticket",
                    "BEFORE DELETE ON character_inventory_items",
                    $"OLD.character_id = {characterId} AND OLD.list_type = 0");

                InventoryMutationSet changes = null;
                var rejection = DungeonAdmissionReject.Unknown;
                var failureReason = string.Empty;
                var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "tournament-entry-selftest",
                    (connection, transaction) =>
                    {
                        return application.TryConsumeEntryItems(
                            lease,
                            definition,
                            missingMemberSlot: 0,
                            out changes,
                            out rejection,
                            out failureReason,
                            deferPersistence: true);
                    });
                Check(
                    "Tournament entry failure restores the consumed item",
                    !committed
                    && changes != null
                    && changes.Slots.Count > 0
                    && lease.Inventory.CountMainItem(RewardItemId) == 1
                    && LoadCount(
                        database,
                        accountId,
                        characterId,
                        RewardItemId) == 1);
            }
            finally
            {
                Release(lease);
                DeleteDatabase(path);
            }
        }

        private static void CheckResetItemQualityRollback()
        {
            const int accountId = 932050;
            const int characterId = 932051;
            const int originalQualitySeed = 12345;
            const int materialCount = 2;
            var path = CreateDatabasePath("reset-item-quality");
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(path, accountId, characterId);
                lease = LoadLease(database, accountId, characterId);
                SeedEquipment(
                    lease,
                    EquipmentItemId,
                    originalQualitySeed,
                    sealFlag: 0,
                    resealCount: 0);
                SeedItemAtSlot(
                    lease,
                    MaterialSlot,
                    KaleidoBoxItemId,
                    materialCount);
                SaveFixture(lease, "reset quality fixture save failed");
                CreateAbortTrigger(
                    database,
                    "fail_reset_item_quality",
                    "BEFORE UPDATE OF item_core ON character_inventory_items",
                    $"OLD.character_id = {characterId} "
                    + $"AND OLD.list_type = 0 AND OLD.slot_index = {EquipmentSlot}");

                var request = new ResetItemQualityRequest
                {
                    TargetSlotIndex = EquipmentSlot,
                    TargetItemTemplateId = EquipmentItemId,
                    MaterialSlotIndex = MaterialSlot,
                };
                ResetItemQualityResult result = null;
                var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "reset-item-quality-selftest",
                    (connection, transaction) =>
                        InventoryEquipmentMutationService.TryResetItemQuality(
                            lease.Inventory,
                            request,
                            out result));
                var persistedTarget = LoadItem(
                    database,
                    accountId,
                    characterId,
                    EquipmentSlot);
                Check(
                    "Quality reset failure restores equipment and material",
                    !committed
                    && result != null
                    && result.ErrorCode == 0
                    && lease.Inventory.GetItem(
                        InventoryListType.Main,
                        EquipmentSlot).Value == originalQualitySeed
                    && lease.Inventory.CountMainItem(KaleidoBoxItemId)
                        == materialCount
                    && persistedTarget != null
                    && persistedTarget.Value == originalQualitySeed
                    && LoadCount(
                        database,
                        accountId,
                        characterId,
                        KaleidoBoxItemId) == materialCount);
            }
            finally
            {
                Release(lease);
                DeleteDatabase(path);
            }
        }

        private static void CheckWaxResealRollback()
        {
            const int accountId = 932060;
            const int characterId = 932061;
            const int waxCount = 100;
            var path = CreateDatabasePath("wax-reseal");
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(path, accountId, characterId);
                lease = LoadLease(database, accountId, characterId);
                SeedEquipment(
                    lease,
                    EquipmentItemId,
                    qualitySeed: 54321,
                    sealFlag: 0,
                    resealCount: 0);
                SeedItemAtSlot(
                    lease,
                    MaterialSlot,
                    WaxItemId,
                    waxCount);
                SaveFixture(lease, "wax reseal fixture save failed");
                CreateAbortTrigger(
                    database,
                    "fail_wax_reseal",
                    "BEFORE UPDATE OF item_core ON character_inventory_items",
                    $"OLD.character_id = {characterId} "
                    + $"AND OLD.list_type = 0 AND OLD.slot_index = {EquipmentSlot}");

                WaxResealResult result = null;
                var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "wax-reseal-selftest",
                    (connection, transaction) =>
                        InventoryEquipmentMutationService.TryWaxReseal(
                            lease.Inventory,
                            EquipmentSlot,
                            EquipmentItemId,
                            MaterialSlot,
                            out result));
                var reloadedTarget = lease.Inventory.GetItem(
                    InventoryListType.Main,
                    EquipmentSlot);
                var persistedTarget = LoadItem(
                    database,
                    accountId,
                    characterId,
                    EquipmentSlot);
                Check(
                    "Wax reseal failure restores equipment and wax",
                    !committed
                    && result != null
                    && result.WaxCost > 0
                    && reloadedTarget != null
                    && reloadedTarget.SealFlag == 0
                    && reloadedTarget.ReSealCount == 0
                    && lease.Inventory.CountMainItem(WaxItemId) == waxCount
                    && persistedTarget != null
                    && persistedTarget.SealFlag == 0
                    && persistedTarget.ReSealCount == 0
                    && LoadCount(
                        database,
                        accountId,
                        characterId,
                        WaxItemId) == waxCount);
            }
            finally
            {
                Release(lease);
                DeleteDatabase(path);
            }
        }

        private static void CheckReviveCoinConsumableRollback()
        {
            const int accountId = 932070;
            const int characterId = 932071;
            const int consumableCount = 2;
            var path = CreateDatabasePath("revive-coin-consumable");
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(path, accountId, characterId);
                lease = LoadLease(database, accountId, characterId);
                SeedItemAtSlot(
                    lease,
                    MaterialSlot,
                    ReviveCoinService.ConsumableItemId,
                    consumableCount);
                SaveFixture(lease, "revive coin consumable fixture save failed");
                CreateAbortTrigger(
                    database,
                    "fail_revive_coin_grant",
                    "BEFORE INSERT ON character_inventory_items",
                    $"NEW.character_id = {characterId} "
                    + $"AND NEW.list_type = 0 "
                    + $"AND NEW.slot_index = {ReviveCoinService.WalletSlot}");

                var service = new ExperienceItemUseService(
                    database,
                    SystemRentalTimeProvider.Instance,
                    new ExperienceItemCooldownTracker());
                var result = service.UseBySlot(
                    characterId,
                    accountId,
                    InventoryListType.Main,
                    MaterialSlot,
                    ExperienceItemUseLocation.Town);
                var onlineConsumableCount = lease.Inventory.CountMainItem(
                    ReviveCoinService.ConsumableItemId);
                var onlineWalletCount = lease.Inventory.GetMainVirtualCount(
                    ReviveCoinService.WalletSlot)?.Count ?? -1;
                var persistedConsumableCount = LoadCount(
                    database,
                    accountId,
                    characterId,
                    ReviveCoinService.ConsumableItemId);
                var persistedWalletCount = LoadVirtualCount(
                    database,
                    accountId,
                    characterId,
                    ReviveCoinService.WalletSlot);
                Check(
                    "Revive coin consumable failure restores box and wallet "
                    + $"status={result.Status} "
                    + $"onlineBox={onlineConsumableCount} "
                    + $"onlineWallet={onlineWalletCount} "
                    + $"dbBox={persistedConsumableCount} "
                    + $"dbWallet={persistedWalletCount}",
                    result.Status == ExperienceItemUseStatus.PersistenceFailed
                    && onlineConsumableCount == consumableCount
                    && onlineWalletCount == 0
                    && persistedConsumableCount == consumableCount
                    && persistedWalletCount == 0);
            }
            finally
            {
                Release(lease);
                DeleteDatabase(path);
            }
        }

        private static void CheckReviveCoinConsumableCommit()
        {
            const int accountId = 932080;
            const int characterId = 932081;
            const int consumableCount = 2;
            var path = CreateDatabasePath("revive-coin-consumable-commit");
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(path, accountId, characterId);
                lease = LoadLease(database, accountId, characterId);
                SeedItemAtSlot(
                    lease,
                    MaterialSlot,
                    ReviveCoinService.ConsumableItemId,
                    consumableCount);
                SaveFixture(lease, "revive coin consumable fixture save failed");

                var service = new ExperienceItemUseService(
                    database,
                    SystemRentalTimeProvider.Instance,
                    new ExperienceItemCooldownTracker());
                var result = service.UseBySlot(
                    characterId,
                    accountId,
                    InventoryListType.Main,
                    MaterialSlot,
                    ExperienceItemUseLocation.Town);
                Check(
                    "Revive coin consumable commits box and wallet together",
                    result.Success
                    && result.ConsumedItem != null
                    && result.ConsumedItem.RemainingStackCount == consumableCount - 1
                    && lease.Inventory.CountMainItem(
                        ReviveCoinService.ConsumableItemId) == consumableCount - 1
                    && lease.Inventory.GetMainVirtualCount(
                        ReviveCoinService.WalletSlot)?.Count == 1
                    && LoadCount(
                        database,
                        accountId,
                        characterId,
                        ReviveCoinService.ConsumableItemId) == consumableCount - 1
                    && LoadVirtualCount(
                        database,
                        accountId,
                        characterId,
                        ReviveCoinService.WalletSlot) == 1);
            }
            finally
            {
                Release(lease);
                DeleteDatabase(path);
            }
        }

        private static void CheckEquipmentItemLockRollback()
        {
            const int accountId = 932090;
            const int characterId = 932091;
            var path = CreateDatabasePath("equipment-item-lock");
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(path, accountId, characterId);
                lease = LoadLease(database, accountId, characterId);
                SeedEquipment(
                    lease,
                    EquipmentItemId,
                    qualitySeed: 10001,
                    sealFlag: 0,
                    resealCount: 0);
                SaveFixture(lease, "equipment lock fixture save failed");
                CreateAbortTrigger(
                    database,
                    "fail_equipment_item_lock",
                    "BEFORE UPDATE OF item_core ON character_inventory_items",
                    $"OLD.character_id = {characterId} "
                    + $"AND OLD.list_type = 0 AND OLD.slot_index = {EquipmentSlot}");

                var committed = EquipmentItemLockCommitService.TryLock(
                    lease,
                    InventoryListType.Main,
                    EquipmentSlot,
                    out var result);
                var onlineTarget = lease.Inventory.GetItem(
                    InventoryListType.Main,
                    EquipmentSlot);
                var persistedTarget = LoadItem(
                    database,
                    accountId,
                    characterId,
                    EquipmentSlot);
                Check(
                    "Equipment lock failure restores item and lock table",
                    !committed
                    && result != null
                    && !result.Success
                    && result.ErrorCode == 19
                    && onlineTarget != null
                    && onlineTarget.EquipmentLockId == 0
                    && lease.Inventory.EquipmentLocks.Locks.Count == 0
                    && persistedTarget != null
                    && persistedTarget.EquipmentLockId == 0
                    && LoadEquipmentLockState(
                        database,
                        characterId,
                        result.EquipmentLockId) == -1);
            }
            finally
            {
                Release(lease);
                DeleteDatabase(path);
            }
        }

        private static void CheckEquipmentItemUnlockRollback()
        {
            const int accountId = 932100;
            const int characterId = 932101;
            var path = CreateDatabasePath("equipment-item-unlock");
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(path, accountId, characterId);
                lease = LoadLease(database, accountId, characterId);
                SeedEquipment(
                    lease,
                    EquipmentItemId,
                    qualitySeed: 10002,
                    sealFlag: 0,
                    resealCount: 0);
                SaveFixture(lease, "equipment unlock fixture save failed");
                if (!EquipmentItemLockCommitService.TryLock(
                        lease,
                        InventoryListType.Main,
                        EquipmentSlot,
                        out var locked)
                    || locked == null
                    || !locked.Success)
                {
                    throw new InvalidOperationException(
                        "equipment unlock fixture lock failed");
                }
                CreateAbortTrigger(
                    database,
                    "fail_equipment_item_unlock",
                    "BEFORE UPDATE OF item_core ON character_inventory_items",
                    $"OLD.character_id = {characterId} "
                    + $"AND OLD.list_type = 0 AND OLD.slot_index = {EquipmentSlot}");

                var committed = EquipmentItemLockCommitService.TryUnlock(
                    lease,
                    InventoryListType.Main,
                    EquipmentSlot,
                    out var result);
                var onlineTarget = lease.Inventory.GetItem(
                    InventoryListType.Main,
                    EquipmentSlot);
                var persistedTarget = LoadItem(
                    database,
                    accountId,
                    characterId,
                    EquipmentSlot);
                Check(
                    "Equipment unlock failure restores item and lock table",
                    !committed
                    && result != null
                    && !result.Success
                    && result.ErrorCode == 19
                    && onlineTarget != null
                    && onlineTarget.EquipmentLockId == locked.EquipmentLockId
                    && lease.Inventory.EquipmentLocks.Get(
                        locked.EquipmentLockId)?.State == 1
                    && persistedTarget != null
                    && persistedTarget.EquipmentLockId == locked.EquipmentLockId
                    && LoadEquipmentLockState(
                        database,
                        characterId,
                        locked.EquipmentLockId) == 1);
            }
            finally
            {
                Release(lease);
                DeleteDatabase(path);
            }
        }

        private static void CheckEquipmentItemUnlockCancelRollback()
        {
            const int accountId = 932110;
            const int characterId = 932111;
            const byte equipmentLockId = 1;
            var path = CreateDatabasePath("equipment-item-unlock-cancel");
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(path, accountId, characterId);
                lease = LoadLease(database, accountId, characterId);
                SeedEquipment(
                    lease,
                    EquipmentItemId,
                    qualitySeed: 10003,
                    sealFlag: 0,
                    resealCount: 0,
                    equipmentLockId: equipmentLockId);
                lease.Inventory.EquipmentLocks.Attach(new EquipmentItemLock
                {
                    EquipmentLockId = equipmentLockId,
                    State = 2,
                    RemainingSeconds = 120,
                });
                SaveFixture(lease, "equipment unlock cancel fixture save failed");
                if (!InventoryEquipmentLockTableService.UpsertLock(
                        characterId,
                        equipmentLockId,
                        InventoryListType.Main,
                        EquipmentSlot,
                        state: 2,
                        remainingSeconds: 120,
                        inventory: lease.Inventory))
                {
                    throw new InvalidOperationException(
                        "equipment unlock cancel lock row fixture failed");
                }
                CreateAbortTrigger(
                    database,
                    "fail_equipment_unlock_cancel",
                    "BEFORE UPDATE OF state ON character_item_locks",
                    $"OLD.character_id = {characterId} "
                    + $"AND OLD.equipment_lock_id = {equipmentLockId}");

                var committed = EquipmentItemLockCommitService.TryCancelUnlock(
                    lease,
                    InventoryListType.Main,
                    EquipmentSlot,
                    out var result);
                var onlineTarget = lease.Inventory.GetItem(
                    InventoryListType.Main,
                    EquipmentSlot);
                Check(
                    "Equipment unlock cancel failure restores pending state",
                    !committed
                    && result != null
                    && !result.Success
                    && result.ErrorCode == 19
                    && onlineTarget != null
                    && onlineTarget.EquipmentLockId == equipmentLockId
                    && lease.Inventory.EquipmentLocks.Get(
                        equipmentLockId)?.State == 2
                    && LoadEquipmentLockState(
                        database,
                        characterId,
                        equipmentLockId) == 2);
            }
            finally
            {
                Release(lease);
                DeleteDatabase(path);
            }
        }

        private static TournamentDungeonDefinition CreateTournamentDefinition(
            int itemId)
        {
            return new TournamentDungeonDefinition(
                dungeonId: 1,
                mapId: 1,
                basicLevel: 1,
                partyLimit: 1,
                coinLimit: 0,
                roundFatigue: 0,
                clearRewardGoldRate: 1.0f,
                experienceByRound: new Dictionary<int, uint>(),
                resultCards:
                    new Dictionary<int, TournamentResultCardDefinition>(),
                rewardItemRates:
                    Array.Empty<TournamentRewardItemRateDefinition>(),
                candidates: Array.Empty<TournamentActorDefinition>(),
                startAreas: Array.Empty<TournamentStartAreaDefinition>(),
                entryItems: new[]
                {
                    new TournamentEntryItemDefinition(itemId, 1, true),
                });
        }

        private static GameDatabase CreateDatabase(
            string path,
            int accountId,
            int characterId)
        {
            var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
            SeedIdentity(database, accountId, characterId);
            return database;
        }

        private static void SeedIdentity(
            IGameDatabase database,
            int accountId,
            int characterId)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, @mid, '');
INSERT INTO characters(character_id, account_id, name, level)
VALUES(@cid, @aid, @name, 86);";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue(
                        "@mid",
                        "domain-boundary-" + accountId);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        "domain-boundary-" + characterId);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static InventoryLease LoadLease(
            IGameDatabase database,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return InventoryContext.Register(Guid.NewGuid(), inventory);
            }
        }

        private static void SeedItem(
            InventoryLease lease,
            int itemId,
            int count)
        {
            SeedItemAtSlot(
                lease,
                InventoryService.MainSlotStart,
                itemId,
                count);
            SaveFixture(lease, $"item fixture save failed item={itemId}");
        }

        private static void SeedItemAtSlot(
            InventoryLease lease,
            short slotIndex,
            int itemId,
            int count)
        {
            var core = ItemCore.Create(ItemCore.KindConsumable, itemId);
            core.Count = count;
            if (!lease.Inventory.SetItem(
                    InventoryListType.Main,
                    slotIndex,
                    core))
                throw new InvalidOperationException($"item fixture set failed item={itemId}");
        }

        private static void SeedEquipment(
            InventoryLease lease,
            int itemId,
            int qualitySeed,
            byte sealFlag,
            byte resealCount,
            byte equipmentLockId = 0)
        {
            var core = ItemCore.Create(ItemCore.KindEquipment, itemId);
            core.Value = qualitySeed;
            core.Durability = 40;
            core.SealFlag = sealFlag;
            core.ReSealCount = resealCount;
            core.EquipmentLockId = equipmentLockId;
            if (!lease.Inventory.SetItem(
                    InventoryListType.Main,
                    EquipmentSlot,
                    core))
                throw new InvalidOperationException($"equipment fixture set failed item={itemId}");
        }

        private static void SaveFixture(InventoryLease lease, string message)
        {
            if (!InventoryPersistenceService.SaveDirty(lease))
                throw new InvalidOperationException(message);
        }

        private static int LoadCount(
            IGameDatabase database,
            int accountId,
            int characterId,
            int itemId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return inventory.CountMainItem(itemId);
            }
        }

        private static ItemCore LoadItem(
            IGameDatabase database,
            int accountId,
            int characterId,
            short slotIndex)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return inventory.GetItem(
                    InventoryListType.Main,
                    slotIndex)?.Copy();
            }
        }

        private static int LoadVirtualCount(
            IGameDatabase database,
            int accountId,
            int characterId,
            short slotIndex)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return inventory.GetMainVirtualCount(slotIndex)?.Count ?? 0;
            }
        }

        private static int LoadEquipmentLockState(
            IGameDatabase database,
            int characterId,
            byte equipmentLockId)
        {
            if (equipmentLockId == 0)
                return -1;

            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT state
FROM character_item_locks
WHERE character_id = @cid
  AND equipment_lock_id = @lockId
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@lockId", equipmentLockId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? -1
                    : Convert.ToInt32(value);
            }
        }

        private static void CreateAbortTrigger(
            IGameDatabase database,
            string name,
            string timingAndEvent,
            string condition)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = $@"
CREATE TRIGGER {name}
{timingAndEvent}
WHEN {condition}
BEGIN
    SELECT RAISE(ABORT, 'injected domain transaction failure');
END;";
                    command.ExecuteNonQuery();
                }
            });
        }

        private static string CreateDatabasePath(string suffix)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"domain_transaction_{suffix}_{Guid.NewGuid():N}.db");
            DeleteDatabase(path);
            return path;
        }

        private static void Release(InventoryLease lease)
        {
            if (lease != null)
                InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
        }

        private static void DeleteDatabase(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
                catch
                {
                }
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok)
                _pass++;
            else
                _fail++;
        }
    }
}
