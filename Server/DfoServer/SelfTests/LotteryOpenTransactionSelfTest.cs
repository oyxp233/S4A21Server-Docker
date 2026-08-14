using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.Lottery;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class LotteryOpenTransactionSelfTest
    {
        private const short LotterySlot = 105;
        private const int RegularLotteryItemId = 7_654_330;
        private const int ProgressLotteryItemId = 7_654_331;
        private const int RegularRewardItemId = 1004;
        private const int ProgressRewardItemId = 101000004;
        private const int InitialGold = 500;
        private const int GoldCost = 100;

        public static int Run()
        {
            var failures = 0;
            TestReservationLifecycle(ref failures);
            TestRegularCommitRollback(ref failures);
            TestDoubleRewardCommitRollback(ref failures);
            TestProgressMailboxCommitRollback(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "LotteryOpenTransactionSelfTest OK"
                    : "LotteryOpenTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void TestReservationLifecycle(ref int failures)
        {
            var now = new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc);
            var sessions = new LotteryOpenSessionCoordinator(
                TimeSpan.FromMinutes(2),
                () => now);
            var sessionId = Guid.NewGuid();
            var createCount = 0;
            sessions.Set(
                sessionId,
                LotterySlot,
                LotteryOpenPlan.DirectDoubleReward(0));
            var reserved = sessions.TryReserveOpen(
                sessionId,
                LotterySlot,
                _ => CreateSyntheticReservation(ref createCount),
                out var reservation);
            Check(
                "lottery reservation fixes source, plan, and rewards once",
                reserved
                && reservation != null
                && reservation.SourceItemTemplateId == RegularLotteryItemId
                && reservation.OpenPlan.UseDoubleReward
                && reservation.SelectedRewards.Count == 1
                && createCount == 1,
                ref failures);
            Check(
                "lottery reservation blocks a concurrent open",
                !sessions.TryReserveOpen(
                    sessionId,
                    LotterySlot,
                    _ => CreateSyntheticReservation(ref createCount),
                    out _),
                ref failures);
            Check(
                "lottery failed open retries the same reservation",
                sessions.ReleaseOpen(sessionId, reservation)
                && sessions.TryReserveOpen(
                    sessionId,
                    LotterySlot,
                    _ => CreateSyntheticReservation(ref createCount),
                    out var retried)
                && ReferenceEquals(retried, reservation)
                && createCount == 1
                && sessions.CompleteOpen(sessionId, retried)
                && !sessions.TryGet(sessionId, null, out _),
                ref failures);

            var inProgressSessionId = Guid.NewGuid();
            sessions.Set(inProgressSessionId, LotterySlot);
            sessions.TryReserveOpen(
                inProgressSessionId,
                LotterySlot,
                _ => CreateSyntheticReservation(ref createCount),
                out var inProgress);
            now = now.AddMinutes(3);
            Check(
                "lottery cleanup preserves an in-progress reservation",
                sessions.TryGet(inProgressSessionId, LotterySlot, out _)
                && sessions.CompleteOpen(inProgressSessionId, inProgress),
                ref failures);
        }

        private static LotteryOpenReservation CreateSyntheticReservation(
            ref int createCount)
        {
            createCount++;
            return new LotteryOpenReservation(
                LotterySlot,
                RegularLotteryItemId,
                LotteryOpenPlan.DirectDoubleReward(0))
            {
                SelectedRewards = new[]
                {
                    new PvfLib.BoosterRewardEntry
                    {
                        ItemId = RegularRewardItemId,
                        Count = 1,
                        Weight = 10_000,
                    },
                },
            };
        }

        private static void TestRegularCommitRollback(ref int failures)
        {
            var fixture = CreateFixture(
                "regular",
                983900,
                983901,
                usesProgress: false,
                activeDoubleBenefit: false,
                fillRewardSlots: false);
            try
            {
                Check(
                    "regular lottery creates a fixed reservation",
                    Reserve(
                        fixture,
                        LotteryOpenPlan.ConfirmedRegular(),
                        out var reservation)
                    && reservation.SelectedRewards.Count == 1
                    && reservation.SelectedRewards[0].ItemId
                        == RegularRewardItemId,
                    ref failures);

                CreateInventoryInsertFailureTrigger(
                    fixture.DatabasePath,
                    fixture.CharacterId);
                var failed = !fixture.Service.TryOpen(
                    fixture.Lease,
                    fixture.AccountId,
                    reservation,
                    fixture.OverflowSink,
                    out var failedResult);
                Check(
                    "regular lottery rejects inventory persistence failure",
                    failed && failedResult == null,
                    ref failures);
                Check(
                    "regular lottery failure rolls back source, gold, and reward",
                    HasOriginalInventoryState(fixture, RegularRewardItemId)
                    && CountMailboxMessages(fixture.Database) == 0,
                    ref failures);

                fixture.Sessions.ReleaseOpen(
                    fixture.Lease.SessionId,
                    reservation);
                var reused = fixture.Sessions.TryReserveOpen(
                    fixture.Lease.SessionId,
                    LotterySlot,
                    _ => null,
                    out var retriedReservation);
                DropFailureTriggers(fixture.DatabasePath);
                LotteryOpenResult retryResult = null;
                var retried = reused && fixture.Service.TryOpen(
                    fixture.Lease,
                    fixture.AccountId,
                    retriedReservation,
                    fixture.OverflowSink,
                    out retryResult);
                if (retried)
                {
                    fixture.Sessions.CompleteOpen(
                        fixture.Lease.SessionId,
                        retriedReservation);
                }
                Check(
                    "regular lottery retry commits the original reward once",
                    retried
                    && ReferenceEquals(retriedReservation, reservation)
                    && retryResult != null
                    && !retryResult.UsedDoubleReward
                    && retryResult.Rewards.Count == 1
                    && retryResult.Rewards[0].ItemTemplateId
                        == RegularRewardItemId
                    && HasCommittedInventoryState(
                        fixture,
                        RegularRewardItemId,
                        1),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] regular lottery transaction fixture threw: " + ex);
                failures++;
            }
            finally
            {
                DisposeFixture(fixture);
            }
        }

        private static void TestDoubleRewardCommitRollback(ref int failures)
        {
            var fixture = CreateFixture(
                "double",
                983910,
                983911,
                usesProgress: false,
                activeDoubleBenefit: true,
                fillRewardSlots: false);
            try
            {
                Reserve(
                    fixture,
                    LotteryOpenPlan.DirectDoubleReward(0),
                    out var reservation);
                CreateInventoryInsertFailureTrigger(
                    fixture.DatabasePath,
                    fixture.CharacterId);
                var failed = !fixture.Service.TryOpen(
                    fixture.Lease,
                    fixture.AccountId,
                    reservation,
                    fixture.OverflowSink,
                    out _);
                Check(
                    "double lottery failure rolls back daily use and inventory",
                    failed
                    && reservation.AppliedDoubleReward == true
                    && fixture.DoubleRewardPolicy.GetUsedCount(
                        fixture.CharacterId) == 0
                    && HasOriginalInventoryState(
                        fixture,
                        RegularRewardItemId),
                    ref failures);

                fixture.Sessions.ReleaseOpen(
                    fixture.Lease.SessionId,
                    reservation);
                fixture.Sessions.TryReserveOpen(
                    fixture.Lease.SessionId,
                    LotterySlot,
                    _ => null,
                    out var retriedReservation);
                DropFailureTriggers(fixture.DatabasePath);
                var retried = fixture.Service.TryOpen(
                    fixture.Lease,
                    fixture.AccountId,
                    retriedReservation,
                    fixture.OverflowSink,
                    out var retryResult);
                if (retried)
                {
                    fixture.Sessions.CompleteOpen(
                        fixture.Lease.SessionId,
                        retriedReservation);
                }
                Check(
                    "double lottery retry consumes one use and grants x2 once",
                    retried
                    && ReferenceEquals(retriedReservation, reservation)
                    && retryResult != null
                    && retryResult.UsedDoubleReward
                    && fixture.DoubleRewardPolicy.GetUsedCount(
                        fixture.CharacterId) == 1
                    && HasCommittedInventoryState(
                        fixture,
                        RegularRewardItemId,
                        2),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] double lottery transaction fixture threw: " + ex);
                failures++;
            }
            finally
            {
                DisposeFixture(fixture);
            }
        }

        private static void TestProgressMailboxCommitRollback(ref int failures)
        {
            var fixture = CreateFixture(
                "progress-mailbox",
                983920,
                983921,
                usesProgress: true,
                activeDoubleBenefit: false,
                fillRewardSlots: true);
            try
            {
                Check(
                    "progress lottery fixes reward and reward index",
                    Reserve(
                        fixture,
                        LotteryOpenPlan.ConfirmedRegular(),
                        out var reservation)
                    && reservation.SelectedRewards.Count == 1
                    && reservation.SelectedRewards[0].ItemId
                        == ProgressRewardItemId
                    && reservation.ProgressRewardIndex >= 0,
                    ref failures);

                CreateMailboxInsertFailureTrigger(fixture.DatabasePath);
                var failed = !fixture.Service.TryOpen(
                    fixture.Lease,
                    fixture.AccountId,
                    reservation,
                    fixture.OverflowSink,
                    out _);
                Check(
                    "progress mailbox failure leaves no mail or progress",
                    failed
                    && CountMailboxMessages(fixture.Database) == 0
                    && CountProgressRows(fixture.Database, fixture.AccountId) == 0,
                    ref failures);
                Check(
                    "progress mailbox failure rolls back source and gold",
                    HasOriginalInventoryState(
                        fixture,
                        ProgressRewardItemId),
                    ref failures);

                fixture.Sessions.ReleaseOpen(
                    fixture.Lease.SessionId,
                    reservation);
                fixture.Sessions.TryReserveOpen(
                    fixture.Lease.SessionId,
                    LotterySlot,
                    _ => null,
                    out var retriedReservation);
                DropFailureTriggers(fixture.DatabasePath);
                var retried = fixture.Service.TryOpen(
                    fixture.Lease,
                    fixture.AccountId,
                    retriedReservation,
                    fixture.OverflowSink,
                    out var retryResult);
                if (retried)
                {
                    fixture.Sessions.CompleteOpen(
                        fixture.Lease.SessionId,
                        retriedReservation);
                }
                Check(
                    "progress mailbox retry commits the same reward once",
                    retried
                    && ReferenceEquals(retriedReservation, reservation)
                    && retryResult != null
                    && retryResult.DeliveredToMailbox
                    && retryResult.Progress != null
                    && retryResult.Progress.NewRewardIndex
                        == reservation.ProgressRewardIndex
                    && CountMailboxMessages(fixture.Database) == 1
                    && CountRewardAttachments(
                        fixture.Database,
                        ProgressRewardItemId) == 1
                    && HasProgressIndex(
                        fixture.Database,
                        fixture.AccountId,
                        reservation.ProgressRewardIndex)
                    && HasCommittedInventoryState(
                        fixture,
                        ProgressRewardItemId,
                        0),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] progress mailbox transaction fixture threw: "
                        + ex);
                failures++;
            }
            finally
            {
                DisposeFixture(fixture);
            }
        }

        private static bool Reserve(
            LotteryFixture fixture,
            LotteryOpenPlan openPlan,
            out LotteryOpenReservation reservation)
        {
            reservation = null;
            fixture.Sessions.Set(
                fixture.Lease.SessionId,
                LotterySlot,
                openPlan);
            lock (fixture.Lease.SyncRoot)
            {
                return fixture.Sessions.TryReserveOpen(
                    fixture.Lease.SessionId,
                    LotterySlot,
                    pending => fixture.Service.TryCreateReservation(
                        fixture.Lease.Inventory,
                        fixture.AccountId,
                        pending.SlotIndex,
                        pending.OpenPlan,
                        out var created)
                            ? created
                            : null,
                    out reservation);
            }
        }

        private static LotteryFixture CreateFixture(
            string suffix,
            int accountId,
            int characterId,
            bool usesProgress,
            bool activeDoubleBenefit,
            bool fillRewardSlots)
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "lottery-open-transaction-" + suffix + "-"
                    + Guid.NewGuid().ToString("N") + ".db");
            var database = new GameDatabase(
                databasePath,
                ServerPaths.SchemaFilePath);
            Seed(
                database,
                accountId,
                characterId,
                activeDoubleBenefit);

            var inventory = new InventoryService(
                characterId,
                accountId,
                database);
            inventory.SetListParam16(
                InventoryListType.Main,
                ItemSlotBoundService.MainExpandStageFull);
            if (fillRewardSlots)
                FillRewardSlots(inventory, ProgressRewardItemId);
            var source = ItemCore.Create(
                ItemCore.KindConsumable,
                usesProgress
                    ? ProgressLotteryItemId
                    : RegularLotteryItemId);
            source.Count = 1;
            inventory.SetItem(
                InventoryListType.Main,
                LotterySlot,
                source);
            inventory.SetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                InitialGold);
            var fixtureLease = new InventoryLease(
                Guid.NewGuid(),
                characterId,
                inventory,
                1);
            if (!InventoryPersistenceService.SaveDirty(fixtureLease))
            {
                throw new InvalidOperationException(
                    "unable to persist lottery transaction fixture");
            }

            var lease = InventoryContext.Register(
                Guid.NewGuid(),
                characterId,
                inventory);
            var dailyReset = new DailyResetService(database);
            var doubleRewardPolicy = new LotteryDoubleRewardPolicy(
                dailyReset,
                database.ConnectionString);
            var definitions = new LotteryItemDefinitionProvider(
                itemId => itemId == (usesProgress
                        ? ProgressLotteryItemId
                        : RegularLotteryItemId)
                    ? CreateLotteryDefinition(usesProgress)
                    : null);
            return new LotteryFixture
            {
                DatabasePath = databasePath,
                Database = database,
                AccountId = accountId,
                CharacterId = characterId,
                Lease = lease,
                DoubleRewardPolicy = doubleRewardPolicy,
                Service = new LotteryItemOpenService(
                    database.ConnectionString,
                    definitions,
                    doubleRewardPolicy),
                Sessions = new LotteryOpenSessionCoordinator(),
                OverflowSink = new MailboxInventoryOverflowRewardSink(
                    new MailboxService(new MailboxRepository(database))),
            };
        }

        private static PvfLib.StackableItemFile CreateLotteryDefinition(
            bool usesProgress)
        {
            var stackable = new PvfLib.StackableItemFile
            {
                Name = usesProgress
                    ? "progress lottery transaction"
                    : "regular lottery transaction",
                StackableType = "`[upgradable legacy]` 1",
                LotteryUseCost = GoldCost,
                ActionTypeName = usesProgress
                    ? "[increase chance lottery]"
                    : string.Empty,
            };
            if (usesProgress)
            {
                stackable.ActionTypeParams.Add(0);
                stackable.ActionTypeParams.Add(2);
                stackable.ActionTypeParams.Add(0);
                stackable.UpgradableLegacyRewards.Add(
                    CreateReward(ProgressRewardItemId, 6_000));
                stackable.UpgradableLegacyRewards.Add(
                    CreateReward(ProgressRewardItemId, 4_000));
            }
            else
            {
                stackable.UpgradableLegacyRewards.Add(
                    CreateReward(RegularRewardItemId, 10_000));
            }

            return stackable;
        }

        private static PvfLib.BoosterRewardEntry CreateReward(
            int itemId,
            int weight)
        {
            return new PvfLib.BoosterRewardEntry
            {
                ItemId = itemId,
                Count = 1,
                Weight = weight,
            };
        }

        private static void FillRewardSlots(
            InventoryService inventory,
            int rewardItemId)
        {
            if (!ItemMetadataResolver.TryResolveItemKind(
                    rewardItemId,
                    out var itemKind)
                || !ItemSlotBoundService.TryGetSlotRange(
                    itemKind,
                    ItemSlotBoundService.MainExpandStageFull,
                    out var listType,
                    out var range)
                || listType != InventoryListType.Main)
            {
                throw new InvalidOperationException(
                    "unable to resolve lottery reward slot range");
            }

            for (short slot = InventoryService.MainSlotStart;
                 slot <= ItemSlotBoundService.MainQuickSlotEnd;
                 slot++)
            {
                SetFiller(inventory, slot, itemKind);
            }

            for (var slot = range.Start; slot <= range.End; slot++)
                SetFiller(inventory, slot, itemKind);
        }

        private static void SetFiller(
            InventoryService inventory,
            short slot,
            byte itemKind)
        {
            var filler = ItemCore.Create(
                itemKind,
                9_100_000 + slot);
            filler.Count = 1;
            inventory.SetItem(
                InventoryListType.Main,
                slot,
                filler);
        }

        private static void Seed(
            IGameDatabase database,
            int accountId,
            int characterId,
            bool activeDoubleBenefit)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, @mid, '');
INSERT INTO characters(
    character_id, account_id, name, level, town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue(
                        "@mid",
                        "lottery-open-transaction-" + characterId);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes("LotteryOpenTransaction"));
                    command.ExecuteNonQuery();
                }

                if (activeDoubleBenefit)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
INSERT INTO account_premiums(account_id, premium_type, end_time)
VALUES(@aid, @premiumType, @endTime);";
                        command.Parameters.AddWithValue("@aid", accountId);
                        command.Parameters.AddWithValue(
                            "@premiumType",
                            DevilContractCatalog.SlotToPremiumType(
                                LotteryDoubleRewardPolicy.PremiumServiceSlot));
                        command.Parameters.AddWithValue(
                            "@endTime",
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400);
                        command.ExecuteNonQuery();
                    }
                }
            });
        }

        private static bool HasOriginalInventoryState(
            LotteryFixture fixture,
            int rewardItemId)
        {
            var persisted = LoadPersistedInventory(fixture);
            return fixture.Lease.Inventory.CountMainItem(
                    fixture.SourceItemTemplateId) == 1
                && fixture.Lease.Inventory.CountMainItem(0) == InitialGold
                && fixture.Lease.Inventory.CountMainItem(rewardItemId) == 0
                && persisted.CountMainItem(fixture.SourceItemTemplateId) == 1
                && persisted.CountMainItem(0) == InitialGold
                && persisted.CountMainItem(rewardItemId) == 0
                && fixture.Lease.Inventory.DirtyListTypes.Count == 0
                && fixture.Lease.Inventory.DirtyMainVirtualCountSlots.Count == 0;
        }

        private static bool HasCommittedInventoryState(
            LotteryFixture fixture,
            int rewardItemId,
            int rewardCount)
        {
            var persisted = LoadPersistedInventory(fixture);
            return fixture.Lease.Inventory.CountMainItem(
                    fixture.SourceItemTemplateId) == 0
                && fixture.Lease.Inventory.CountMainItem(0)
                    == InitialGold - GoldCost
                && fixture.Lease.Inventory.CountMainItem(rewardItemId)
                    == rewardCount
                && persisted.CountMainItem(fixture.SourceItemTemplateId) == 0
                && persisted.CountMainItem(0) == InitialGold - GoldCost
                && persisted.CountMainItem(rewardItemId) == rewardCount;
        }

        private static InventoryService LoadPersistedInventory(
            LotteryFixture fixture)
        {
            using (var connection = fixture.Database.OpenConnection())
            {
                return InventoryService.LoadFromDb(
                    connection,
                    fixture.CharacterId,
                    fixture.AccountId,
                    fixture.Database);
            }
        }

        private static int CountMailboxMessages(IGameDatabase database)
        {
            return CountRows(
                database,
                "SELECT COUNT(*) FROM mailbox_messages;");
        }

        private static int CountRewardAttachments(
            IGameDatabase database,
            int rewardItemId)
        {
            return CountRows(
                database,
                "SELECT COUNT(*) FROM mailbox_attachments "
                    + "WHERE item_template_id = " + rewardItemId
                    + " AND item_count = 1;");
        }

        private static int CountProgressRows(
            IGameDatabase database,
            int accountId)
        {
            return CountRows(
                database,
                "SELECT COUNT(*) FROM account_increase_chance_lottery_progress "
                    + "WHERE account_id = " + accountId
                    + " AND item_template_id = " + ProgressLotteryItemId + ";");
        }

        private static bool HasProgressIndex(
            IGameDatabase database,
            int accountId,
            int rewardIndex)
        {
            return CountRows(
                database,
                "SELECT COUNT(*) FROM account_increase_chance_lottery_progress "
                    + "WHERE account_id = " + accountId
                    + " AND item_template_id = " + ProgressLotteryItemId
                    + " AND reward_index = " + rewardIndex + ";") == 1;
        }

        private static int CountRows(
            IGameDatabase database,
            string sql)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static void CreateInventoryInsertFailureTrigger(
            string databasePath,
            int characterId)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_lottery_open_inventory_insert
BEFORE INSERT ON character_inventory_items
WHEN NEW.character_id = {characterId}
 AND NEW.list_type = {(int)InventoryListType.Main}
BEGIN
    SELECT RAISE(ABORT, 'injected lottery inventory failure');
END;");
        }

        private static void CreateMailboxInsertFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                @"
CREATE TRIGGER fail_lottery_open_mailbox_insert
BEFORE INSERT ON mailbox_messages
BEGIN
    SELECT RAISE(ABORT, 'injected lottery mailbox failure');
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
DROP TRIGGER IF EXISTS fail_lottery_open_inventory_insert;
DROP TRIGGER IF EXISTS fail_lottery_open_mailbox_insert;");
            }
            catch
            {
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

        private static void DisposeFixture(LotteryFixture fixture)
        {
            if (fixture == null)
                return;

            DropFailureTriggers(fixture.DatabasePath);
            if (fixture.Lease != null)
            {
                InventoryContext.Unregister(
                    fixture.Lease.SessionId,
                    fixture.Lease.CharacterId);
            }

            DeleteDatabaseFiles(fixture.DatabasePath);
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            SqliteConnection.ClearAllPools();
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

        private sealed class LotteryFixture
        {
            internal string DatabasePath { get; set; }

            internal IGameDatabase Database { get; set; }

            internal int AccountId { get; set; }

            internal int CharacterId { get; set; }

            internal InventoryLease Lease { get; set; }

            internal LotteryDoubleRewardPolicy DoubleRewardPolicy { get; set; }

            internal LotteryItemOpenService Service { get; set; }

            internal LotteryOpenSessionCoordinator Sessions { get; set; }

            internal MailboxInventoryOverflowRewardSink OverflowSink { get; set; }

            internal int SourceItemTemplateId =>
                DatabasePath != null
                && DatabasePath.IndexOf(
                    "progress-mailbox",
                    StringComparison.OrdinalIgnoreCase) >= 0
                    ? ProgressLotteryItemId
                    : RegularLotteryItemId;
        }
    }
}
