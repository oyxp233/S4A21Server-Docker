using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class QuestGiveupTransactionSelfTest
    {
        private const int AccountId = 986500;
        private const int CharacterId = 986501;
        private const ushort PrerequisiteQuestId = 2041;
        private const ushort QuestId = 2042;
        private const int EventItemId = 10089292;

        public static int Run()
        {
            var failures = 0;
            try
            {
                using var fixture = new Fixture();
                Check("quest giveup fixture accepts event-item quest",
                    fixture.Accepted
                    && fixture.HasActiveQuestAndEventItem()
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.CreateActiveQuestDeleteFailureTrigger();
                var result = fixture.Giveup();
                Check("active quest DELETE failure rejects giveup",
                    !result.Success,
                    ref failures);
                Check("active quest failure reloads event item and clears dirty state",
                    fixture.HasActiveQuestAndEventItem()
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                fixture.CreateEventItemDeleteFailureTrigger();
                result = fixture.Giveup();
                Check("event item DELETE failure rolls back quest giveup",
                    !result.Success
                    && fixture.HasActiveQuestAndEventItem()
                    && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                result = fixture.Giveup();
                Check("quest giveup retries once after persistence recovery",
                    result.Success
                    && fixture.HasCommittedGiveupState()
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    "[FAIL] quest giveup transaction selftest threw: "
                    + exception);
                failures++;
            }

            Console.WriteLine(failures == 0
                ? "QuestGiveupTransactionSelfTest OK"
                : "QuestGiveupTransactionSelfTest FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine("[" + (condition ? "PASS" : "FAIL") + "] " + label);
            if (!condition)
                failures++;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly QuestService _questService;
            private readonly short _eventItemSlot;

            internal Fixture()
            {
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "quest-giveup-transaction-"
                    + Guid.NewGuid().ToString("N")
                    + ".db");
                Database = new GameDatabase(
                    DatabasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(Database);

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    Database);
                Lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
                _questService = new QuestService(Database.ConnectionString);
                var accept = _questService.HandleAcceptQuest(
                    CharacterId,
                    BitConverter.GetBytes(QuestId),
                    AccountId);
                Accepted = accept.Success;
                _eventItemSlot = Lease.Inventory
                    .GetItems(InventoryListType.Main)
                    .Where(pair => pair.Value?.ItemId == EventItemId)
                    .Select(pair => pair.Key)
                    .DefaultIfEmpty((short)-1)
                    .First();
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }
            internal bool Accepted { get; }

            internal QuestGiveupResult Giveup()
                => _questService.HandleGiveupQuest(
                    CharacterId,
                    BitConverter.GetBytes(QuestId));

            internal bool HasActiveQuestAndEventItem()
            {
                var persisted = LoadInventory();
                return HasActiveQuest()
                    && Lease.Inventory.CountMainItem(EventItemId) == 1
                    && persisted.CountMainItem(EventItemId) == 1;
            }

            internal bool HasCommittedGiveupState()
            {
                var persisted = LoadInventory();
                return !HasActiveQuest()
                    && Lease.Inventory.CountMainItem(EventItemId) == 0
                    && persisted.CountMainItem(EventItemId) == 0;
            }

            internal bool HasNoDirtyState()
                => Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.DirtyMainVirtualCountSlots.Count == 0
                    && Lease.Inventory.DirtyListParams.Count == 0
                    && Lease.Inventory.PendingHappyTokenCeraGrant == 0;

            internal void CreateActiveQuestDeleteFailureTrigger()
                => Execute($@"
CREATE TRIGGER fail_quest_giveup_active_delete
BEFORE DELETE ON character_active_quests
WHEN OLD.character_id={CharacterId} AND OLD.quest_id={QuestId}
BEGIN
    SELECT RAISE(ABORT, 'injected quest giveup active delete failure');
END;");

            internal void CreateEventItemDeleteFailureTrigger()
            {
                if (_eventItemSlot < 0)
                {
                    throw new InvalidOperationException(
                        "quest giveup event item slot was not resolved");
                }

                Execute($@"
CREATE TRIGGER fail_quest_giveup_item_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id={CharacterId}
  AND OLD.list_type=0
  AND OLD.slot_index={_eventItemSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected quest giveup item delete failure');
END;");
            }

            internal void DropFailureTriggers()
            {
                if (!File.Exists(DatabasePath))
                    return;
                Execute(@"
DROP TRIGGER IF EXISTS fail_quest_giveup_active_delete;
DROP TRIGGER IF EXISTS fail_quest_giveup_item_delete;");
            }

            private bool HasActiveQuest()
                => new QuestRepository(Database.ConnectionString)
                    .LoadActiveQuests(CharacterId)
                    .Any(quest => quest.QuestId == QuestId);

            private InventoryService LoadInventory()
            {
                using var connection = Database.OpenConnection();
                return InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    Database);
            }

            private void Execute(string sql)
            {
                using var connection = Database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            public void Dispose()
            {
                try { DropFailureTriggers(); } catch { }
                InventoryContext.Unregister(Lease.SessionId, Lease.CharacterId);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    try
                    {
                        if (File.Exists(DatabasePath + suffix))
                            File.Delete(DatabasePath + suffix);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void SeedCharacter(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO accounts(account_id,m_id,password_hash)
VALUES(@aid,'quest-giveup-transaction','');
INSERT INTO characters(
    character_id,account_id,name,job,grow_type,level,
    town_id,area_id,direction,area_state)
VALUES(@cid,@aid,@name,0,0,49,1,0,5,3);
INSERT INTO character_quest_completions(
    character_id,quest_id,completion_value)
VALUES(@cid,@prerequisite,1);";
                command.Parameters.AddWithValue("@aid", AccountId);
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue(
                    "@name",
                    Encoding.UTF8.GetBytes("QuestGiveupTransaction"));
                command.Parameters.AddWithValue(
                    "@prerequisite",
                    PrerequisiteQuestId);
                command.ExecuteNonQuery();
            });
        }
    }
}
