using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class TeleportConsumableTransactionSelfTest
    {
        private const int AccountId = 983400;
        private const int CharacterId = 983401;
        private const int TeleportItemId = 1004;
        private const int InitialItemCount = 2;
        private const byte InitialTown = 1;
        private const byte InitialArea = 0;
        private const short InitialX = 10;
        private const short InitialY = 20;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "teleport-consumable-transaction-" +
                Guid.NewGuid().ToString("N") + ".db");
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
                var granted = InventoryRewardGrantService.TryCreateAndInsert(
                    inventory,
                    TeleportItemId,
                    ItemCreateReason.AdminGrant,
                    InitialItemCount,
                    out var grant);
                var fixtureLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                Check(
                    "teleport fixture persists a physical consumable stack",
                    granted
                    && grant != null
                    && grant.Success
                    && InventoryPersistenceService.SaveDirty(fixtureLease),
                    ref failures);
                if (!granted || grant == null || !grant.Success)
                    return 1;

                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);

                CreatePositionFailureTrigger(databasePath);
                var positionFailed = !TeleportConsumableCommitService.TryCommit(
                    lease,
                    TeleportItemId,
                    townId: 2,
                    areaId: 1,
                    posX: 100,
                    posY: 200,
                    direction: 0,
                    areaState: 3,
                    persistPosition: true,
                    out var failedPositionConsume);
                Check(
                    "teleport rejects a position persistence failure",
                    positionFailed
                    && failedPositionConsume != null
                    && failedPositionConsume.Success,
                    ref failures);
                Check(
                    "position failure reloads consumable and dirty state",
                    lease.Inventory.CountMainItem(TeleportItemId)
                        == InitialItemCount
                    && !lease.Inventory.DirtyListTypes.Contains(
                        InventoryListType.Main),
                    ref failures);
                Check(
                    "position failure leaves item and location unchanged",
                    LoadPersistedCount(database) == InitialItemCount
                    && IsPersistedPosition(
                        database,
                        InitialTown,
                        InitialArea,
                        InitialX,
                        InitialY),
                    ref failures);

                DropFailureTriggers(databasePath);
                var firstRetried = TeleportConsumableCommitService.TryCommit(
                    lease,
                    TeleportItemId,
                    townId: 2,
                    areaId: 1,
                    posX: 100,
                    posY: 200,
                    direction: 0,
                    areaState: 3,
                    persistPosition: true,
                    out var firstRetryConsume);
                Check(
                    "teleport retries after position persistence recovery",
                    firstRetried
                    && firstRetryConsume != null
                    && firstRetryConsume.Success
                    && lease.Inventory.CountMainItem(TeleportItemId) == 1
                    && LoadPersistedCount(database) == 1
                    && IsPersistedPosition(database, 2, 1, 100, 200),
                    ref failures);

                CreateItemDeleteFailureTrigger(databasePath, grant.SlotIndex);
                var itemFailed = !TeleportConsumableCommitService.TryCommit(
                    lease,
                    TeleportItemId,
                    townId: 3,
                    areaId: 2,
                    posX: 300,
                    posY: 400,
                    direction: 0,
                    areaState: 3,
                    persistPosition: true,
                    out var failedItemConsume);
                Check(
                    "teleport rejects a consumable delete failure",
                    itemFailed
                    && failedItemConsume != null
                    && failedItemConsume.Success,
                    ref failures);
                Check(
                    "item failure reloads consumable and rolls back location",
                    lease.Inventory.CountMainItem(TeleportItemId) == 1
                    && LoadPersistedCount(database) == 1
                    && IsPersistedPosition(database, 2, 1, 100, 200)
                    && !lease.Inventory.DirtyListTypes.Contains(
                        InventoryListType.Main),
                    ref failures);

                DropFailureTriggers(databasePath);
                var secondRetried = TeleportConsumableCommitService.TryCommit(
                    lease,
                    TeleportItemId,
                    townId: 3,
                    areaId: 2,
                    posX: 300,
                    posY: 400,
                    direction: 0,
                    areaState: 3,
                    persistPosition: true,
                    out var secondRetryConsume);
                Check(
                    "teleport delete retries and commits item with location",
                    secondRetried
                    && secondRetryConsume != null
                    && secondRetryConsume.Success
                    && lease.Inventory.CountMainItem(TeleportItemId) == 0
                    && LoadPersistedCount(database) == 0
                    && IsPersistedPosition(database, 3, 2, 300, 400),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] teleport consumable transaction selftest threw: "
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
                    ? "TeleportConsumableTransactionSelfTest OK"
                    : "TeleportConsumableTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
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
VALUES(@aid, 'teleport-consumable-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, pos_x, pos_y, direction, area_state)
VALUES(
    @cid, @aid, @name, 86,
    @town, @area, @px, @py, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes(
                            "TeleportConsumableTransaction"));
                    command.Parameters.AddWithValue("@town", InitialTown);
                    command.Parameters.AddWithValue("@area", InitialArea);
                    command.Parameters.AddWithValue("@px", InitialX);
                    command.Parameters.AddWithValue("@py", InitialY);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void CreatePositionFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_teleport_position_update
BEFORE UPDATE OF town_id, area_id, pos_x, pos_y ON characters
WHEN OLD.character_id = {CharacterId}
BEGIN
    SELECT RAISE(ABORT, 'injected teleport position failure');
END;");
        }

        private static void CreateItemDeleteFailureTrigger(
            string databasePath,
            short slotIndex)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_teleport_item_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {slotIndex}
BEGIN
    SELECT RAISE(ABORT, 'injected teleport item delete failure');
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
DROP TRIGGER IF EXISTS fail_teleport_position_update;
DROP TRIGGER IF EXISTS fail_teleport_item_delete;");
            }
            catch
            {
            }
        }

        private static int LoadPersistedCount(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    database);
                return inventory.CountMainItem(TeleportItemId);
            }
        }

        private static bool IsPersistedPosition(
            IGameDatabase database,
            byte townId,
            byte areaId,
            short posX,
            short posY)
        {
            var repository = new SqliteCharacterRepository(database);
            var character = repository.GetById(CharacterId);
            return character != null
                && character.TownId == townId
                && character.AreaId == areaId
                && character.PosX == posX
                && character.PosY == posY;
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
