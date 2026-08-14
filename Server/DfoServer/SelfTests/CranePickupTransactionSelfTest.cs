using DfoServer.Game.CraneMiniGame;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class CranePickupTransactionSelfTest
    {
        private const int AccountId = 983600;
        private const int CharacterId = 983601;
        private const int RewardItemId = 1004;
        private const ushort DisplaySlot = 3;
        private const string MailTitle = "Crane pickup reward";
        private const string MailText = "Crane pickup inventory overflow reward";

        public static int Run()
        {
            var failures = 0;
            TestReservationLifecycle(ref failures);
            TestInventoryCommitRollback(ref failures);
            TestMailboxCommitRollback(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "CranePickupTransactionSelfTest OK"
                    : "CranePickupTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void TestReservationLifecycle(ref int failures)
        {
            var sessions = new CraneMiniGameSessionCoordinator();
            var sessionId = Guid.NewGuid();
            var state = CreateState();
            var rollCount = 0;
            sessions.Set(sessionId, state);

            var reserved = sessions.TryReservePickup(
                sessionId,
                DisplaySlot,
                RewardItemId,
                out var reservation,
                _ =>
                {
                    rollCount++;
                    return true;
                });
            Check(
                "crane pickup fixes the selected item and winning result",
                reserved
                && reservation != null
                && reservation.Won
                && reservation.Item.ItemId == RewardItemId
                && rollCount == 1,
                ref failures);
            Check(
                "crane pickup blocks a concurrent claim while reservation is active",
                !sessions.TryReservePickup(
                    sessionId,
                    DisplaySlot,
                    RewardItemId,
                    out _),
                ref failures);
            Check(
                "crane pickup retry reuses the original reservation",
                sessions.ReleasePickup(sessionId, reservation)
                && sessions.TryReservePickup(
                    sessionId,
                    DisplaySlot,
                    RewardItemId,
                    out var retried)
                && ReferenceEquals(retried, reservation)
                && rollCount == 1
                && sessions.CompletePickup(sessionId, retried)
                && !sessions.TryGet(sessionId, out _),
                ref failures);

            var missedSessionId = Guid.NewGuid();
            sessions.Set(missedSessionId, state);
            var missRollCount = 0;
            Check(
                "crane pickup miss consumes the session exactly once",
                sessions.TryReservePickup(
                    missedSessionId,
                    DisplaySlot,
                    RewardItemId,
                    out var miss,
                    _ =>
                    {
                        missRollCount++;
                        return false;
                    })
                && miss != null
                && !miss.Won
                && missRollCount == 1
                && !sessions.TryReservePickup(
                    missedSessionId,
                    DisplaySlot,
                    RewardItemId,
                    out _),
                ref failures);
        }

        private static void TestInventoryCommitRollback(ref int failures)
        {
            var databasePath = CreateDatabasePath("inventory");
            InventoryLease lease = null;
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(database);
                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    database);
                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
                var sessions = new CraneMiniGameSessionCoordinator();
                sessions.Set(lease.SessionId, CreateState());
                var rollCount = 0;
                sessions.TryReservePickup(
                    lease.SessionId,
                    DisplaySlot,
                    RewardItemId,
                    out var reservation,
                    _ =>
                    {
                        rollCount++;
                        return true;
                    });
                var sink = CreateOverflowSink(database);

                CreateInventoryInsertFailureTrigger(databasePath);
                var failed = !CraneMiniGamePickupCommitService.TryCommit(
                    lease,
                    reservation,
                    sink,
                    MailTitle,
                    MailText,
                    out var failedResult);
                sessions.ReleasePickup(lease.SessionId, reservation);
                Check(
                    "crane inventory reward rejects persistence failure",
                    failed && failedResult == null,
                    ref failures);
                Check(
                    "crane inventory failure reloads online and database state",
                    lease.Inventory.CountMainItem(RewardItemId) == 0
                    && LoadPersistedRewardCount(database) == 0
                    && !lease.Inventory.DirtyListTypes.Contains(
                        InventoryListType.Main),
                    ref failures);
                Check(
                    "crane inventory failure keeps the same reservation",
                    sessions.TryReservePickup(
                        lease.SessionId,
                        DisplaySlot,
                        RewardItemId,
                        out var retriedReservation)
                    && ReferenceEquals(retriedReservation, reservation)
                    && rollCount == 1,
                    ref failures);

                DropFailureTriggers(databasePath);
                var retried = CraneMiniGamePickupCommitService.TryCommit(
                    lease,
                    retriedReservation,
                    sink,
                    MailTitle,
                    MailText,
                    out var retryResult);
                if (retried)
                    sessions.CompletePickup(
                        lease.SessionId,
                        retriedReservation);
                Check(
                    "crane inventory retry commits the original reward",
                    retried
                    && retryResult != null
                    && !retryResult.DeliveredByMail
                    && retryResult.Grant != null
                    && retryResult.Grant.Success
                    && lease.Inventory.CountMainItem(RewardItemId) == 1
                    && LoadPersistedRewardCount(database) == 1,
                    ref failures);
                Check(
                    "crane inventory success clears the reservation",
                    !sessions.TryGet(lease.SessionId, out _)
                    && !sessions.TryReservePickup(
                        lease.SessionId,
                        DisplaySlot,
                        RewardItemId,
                        out _),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] crane inventory transaction fixture threw: " + ex);
                failures++;
            }
            finally
            {
                DropFailureTriggers(databasePath);
                Unregister(lease);
                DeleteDatabaseFiles(databasePath);
            }
        }

        private static void TestMailboxCommitRollback(ref int failures)
        {
            var databasePath = CreateDatabasePath("mailbox");
            InventoryLease lease = null;
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(database);
                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    database);
                FillRewardSlots(inventory);
                var fixtureLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                Check(
                    "crane mailbox fixture persists a full reward inventory",
                    InventoryPersistenceService.SaveDirty(fixtureLease),
                    ref failures);
                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
                var sessions = new CraneMiniGameSessionCoordinator();
                sessions.Set(lease.SessionId, CreateState());
                var rollCount = 0;
                sessions.TryReservePickup(
                    lease.SessionId,
                    DisplaySlot,
                    RewardItemId,
                    out var reservation,
                    _ =>
                    {
                        rollCount++;
                        return true;
                    });
                var sink = CreateOverflowSink(database);

                CreateMailboxInsertFailureTrigger(databasePath);
                var failed = !CraneMiniGamePickupCommitService.TryCommit(
                    lease,
                    reservation,
                    sink,
                    MailTitle,
                    MailText,
                    out var failedResult);
                sessions.ReleasePickup(lease.SessionId, reservation);
                Check(
                    "crane mailbox reward rejects mail persistence failure",
                    failed
                    && failedResult == null
                    && CountMailboxMessages(database) == 0
                    && CountRewardAttachments(database) == 0,
                    ref failures);
                Check(
                    "crane mailbox failure keeps the fixed reward reservation",
                    sessions.TryReservePickup(
                        lease.SessionId,
                        DisplaySlot,
                        RewardItemId,
                        out var retriedReservation)
                    && ReferenceEquals(retriedReservation, reservation)
                    && rollCount == 1,
                    ref failures);

                DropFailureTriggers(databasePath);
                var retried = CraneMiniGamePickupCommitService.TryCommit(
                    lease,
                    retriedReservation,
                    sink,
                    MailTitle,
                    MailText,
                    out var retryResult);
                if (retried)
                    sessions.CompletePickup(
                        lease.SessionId,
                        retriedReservation);
                Check(
                    "crane mailbox retry commits the original reward once",
                    retried
                    && retryResult != null
                    && retryResult.DeliveredByMail
                    && retryResult.Grant == null
                    && CountMailboxMessages(database) == 1
                    && CountRewardAttachments(database) == 1,
                    ref failures);
                Check(
                    "crane mailbox success clears the reservation without inventory grant",
                    lease.Inventory.CountMainItem(RewardItemId) == 0
                    && !sessions.TryGet(lease.SessionId, out _),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] crane mailbox transaction fixture threw: " + ex);
                failures++;
            }
            finally
            {
                DropFailureTriggers(databasePath);
                Unregister(lease);
                DeleteDatabaseFiles(databasePath);
            }
        }

        private static CraneMiniGameStartResult CreateState()
        {
            return new CraneMiniGameStartResult
            {
                MachineId = 140,
                DisplayItems = new[]
                {
                    new CraneMiniGameItem
                    {
                        CatalogIndex = DisplaySlot,
                        ItemId = RewardItemId,
                        Count = 1,
                        ViewWeight = 1,
                        PickChance = 50,
                    },
                },
            };
        }

        private static MailboxInventoryOverflowRewardSink CreateOverflowSink(
            IGameDatabase database)
        {
            return new MailboxInventoryOverflowRewardSink(
                new MailboxService(new MailboxRepository(database)));
        }

        private static void FillRewardSlots(InventoryService inventory)
        {
            if (!ItemMetadataResolver.TryResolveItemKind(
                    RewardItemId,
                    out var itemKind)
                || !ItemSlotBoundService.TryGetSlotRange(
                    itemKind,
                    ItemSlotBoundService.MainExpandStageFull,
                    out var listType,
                    out var range)
                || listType != InventoryListType.Main)
            {
                throw new InvalidOperationException(
                    "unable to resolve crane reward slot range");
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
                9_000_000 + slot);
            filler.Count = 1;
            if (!inventory.SetItem(
                    InventoryListType.Main,
                    slot,
                    filler))
            {
                throw new InvalidOperationException(
                    "unable to fill crane reward slot " + slot);
            }
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
VALUES(@aid, 'crane-pickup-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level, town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes("CranePickupTransaction"));
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void CreateInventoryInsertFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_crane_pickup_inventory_insert
BEFORE INSERT ON character_inventory_items
WHEN NEW.character_id = {CharacterId}
 AND NEW.list_type = {(int)InventoryListType.Main}
BEGIN
    SELECT RAISE(ABORT, 'injected crane pickup inventory failure');
END;");
        }

        private static void CreateMailboxInsertFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                @"
CREATE TRIGGER fail_crane_pickup_mailbox_insert
BEFORE INSERT ON mailbox_messages
BEGIN
    SELECT RAISE(ABORT, 'injected crane pickup mailbox failure');
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
DROP TRIGGER IF EXISTS fail_crane_pickup_inventory_insert;
DROP TRIGGER IF EXISTS fail_crane_pickup_mailbox_insert;");
            }
            catch
            {
            }
        }

        private static int LoadPersistedRewardCount(
            IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    database);
                return inventory.CountMainItem(RewardItemId);
            }
        }

        private static int CountMailboxMessages(IGameDatabase database)
        {
            return CountRows(
                database,
                "SELECT COUNT(*) FROM mailbox_messages;");
        }

        private static int CountRewardAttachments(IGameDatabase database)
        {
            return CountRows(
                database,
                "SELECT COUNT(*) FROM mailbox_attachments "
                + "WHERE item_template_id = " + RewardItemId
                + " AND item_count = 1;");
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

        private static void Unregister(InventoryLease lease)
        {
            if (lease != null)
            {
                InventoryContext.Unregister(
                    lease.SessionId,
                    lease.CharacterId);
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

        private static string CreateDatabasePath(string suffix)
        {
            return Path.Combine(
                Path.GetTempPath(),
                "crane-pickup-transaction-" + suffix + "-"
                    + Guid.NewGuid().ToString("N") + ".db");
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
