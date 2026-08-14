using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class SkillRefundTransactionSelfTest
    {
        private const int AccountId = 984000;
        private const int CharacterId = 984001;
        private const byte Job = 0;
        private const byte Level = 86;
        private const ushort SkillId = 64;
        private const short ConsumableSlot = 105;
        private const int InitialConsumableCount = 2;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "skill-refund-transaction-"
                    + Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(database);
                var repository = new SqliteCharacterProgressRepository(database);
                var baseline = ResolveRefundBaseline();
                SeedSkill(repository, baseline + 1);

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    database);
                inventory.SetListParam16(
                    InventoryListType.Main,
                    ItemSlotBoundService.MainExpandStageFull);
                SetConsumableCount(inventory, InitialConsumableCount);
                var fixtureLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                Check(
                    "skill refund fixture persists skill and consumable",
                    InventoryPersistenceService.SaveDirty(fixtureLease)
                    && LoadSkillLevel(repository) == baseline + 1
                    && LoadPersistedConsumableCount(database)
                        == InitialConsumableCount,
                    ref failures);
                lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);

                CreateSkillDeleteFailureTrigger(databasePath);
                var skillFailed = BuySkillService.ExecuteWithRefundConsumable(
                    lease,
                    repository,
                    CharacterId,
                    AccountId,
                    Job,
                    0,
                    CreateRefundRequest(),
                    level: Level);
                Check(
                    "skill refund rejects skill persistence failure",
                    skillFailed != null && !skillFailed.Success,
                    ref failures);
                Check(
                    "skill persistence failure restores skill and consumable",
                    HasOriginalState(lease, repository, database, baseline),
                    ref failures);

                DropFailureTriggers(databasePath);
                var skillRetry = BuySkillService.ExecuteWithRefundConsumable(
                    lease,
                    repository,
                    CharacterId,
                    AccountId,
                    Job,
                    0,
                    CreateRefundRequest(),
                    level: Level);
                Check(
                    "skill refund retries after skill persistence recovery",
                    HasCommittedState(
                        skillRetry,
                        lease,
                        repository,
                        database,
                        baseline),
                    ref failures);

                ResetFixture(lease, repository, baseline + 1);
                Check(
                    "skill refund fixture resets for inventory failure",
                    HasOriginalState(lease, repository, database, baseline),
                    ref failures);

                CreateConsumableUpdateFailureTrigger(databasePath);
                var inventoryFailed = BuySkillService.ExecuteWithRefundConsumable(
                    lease,
                    repository,
                    CharacterId,
                    AccountId,
                    Job,
                    0,
                    CreateRefundRequest(),
                    level: Level);
                Check(
                    "skill refund rejects consumable persistence failure",
                    inventoryFailed != null && !inventoryFailed.Success,
                    ref failures);
                Check(
                    "consumable persistence failure restores skill and item",
                    HasOriginalState(lease, repository, database, baseline),
                    ref failures);

                DropFailureTriggers(databasePath);
                var inventoryRetry = BuySkillService.ExecuteWithRefundConsumable(
                    lease,
                    repository,
                    CharacterId,
                    AccountId,
                    Job,
                    0,
                    CreateRefundRequest(),
                    level: Level);
                Check(
                    "skill refund commits skill and consumable after recovery",
                    HasCommittedState(
                        inventoryRetry,
                        lease,
                        repository,
                        database,
                        baseline),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] skill refund transaction selftest threw: " + ex);
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
                    ? "SkillRefundTransactionSelfTest OK"
                    : "SkillRefundTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static int ResolveRefundBaseline()
        {
            var skill = SkillDataProvider.GetSkill(Job, SkillId);
            if (skill == null)
            {
                throw new InvalidOperationException(
                    "skill refund fixture cannot load skill 64");
            }

            var baseline = SkillPointLedger.BuildFreeBaseline(Job, 0, 0);
            baseline.TryGetValue(SkillId, out var freeLevel);
            var maximum = skill.GetMaxLearnableLevel(Level, 0, 0);
            if (maximum <= freeLevel)
            {
                throw new InvalidOperationException(
                    "skill refund fixture has no refundable level");
            }

            return freeLevel;
        }

        private static IList<BuySkillEntry> CreateRefundRequest()
        {
            return new[]
            {
                new BuySkillEntry
                {
                    SkillIndex = SkillId,
                    IsRefund = 1,
                    Level = 1,
                },
            };
        }

        private static void SeedSkill(
            SqliteCharacterProgressRepository repository,
            int skillLevel)
        {
            var snapshot = new SkillInfoSnapshot();
            var page = new SkillInfoPageSnapshot();
            page.Entries.Add(new SkillInfoEntrySnapshot
            {
                Slot = 0,
                SkillId = SkillId,
                Level = (byte)skillLevel,
            });
            snapshot.Pages.Add(page);
            snapshot.Pages.Add(new SkillInfoPageSnapshot());
            repository.SaveSkillProgress(CharacterId, snapshot);
        }

        private static void SetConsumableCount(
            InventoryService inventory,
            int count)
        {
            var item = ItemCore.Create(
                ItemCore.KindConsumable,
                SkillResetConsumableService.ForgetRiverWaterItemTemplateId);
            item.Count = count;
            if (!inventory.SetItem(
                    InventoryListType.Main,
                    ConsumableSlot,
                    item))
            {
                throw new InvalidOperationException(
                    "unable to seed forget river water");
            }
        }

        private static void ResetFixture(
            InventoryLease lease,
            SqliteCharacterProgressRepository repository,
            int skillLevel)
        {
            DropFailureTriggers(lease.Inventory.Database.DatabasePath);
            SeedSkill(repository, skillLevel);
            SetConsumableCount(
                lease.Inventory,
                InitialConsumableCount);
            if (!InventoryPersistenceService.SaveDirty(lease))
            {
                throw new InvalidOperationException(
                    "unable to reset skill refund inventory fixture");
            }
        }

        private static bool HasOriginalState(
            InventoryLease lease,
            SqliteCharacterProgressRepository repository,
            IGameDatabase database,
            int baseline)
        {
            return lease.Inventory.CountMainItem(
                    SkillResetConsumableService.ForgetRiverWaterItemTemplateId)
                    == InitialConsumableCount
                && LoadPersistedConsumableCount(database)
                    == InitialConsumableCount
                && LoadSkillLevel(repository) == baseline + 1
                && lease.Inventory.DirtyListTypes.Count == 0;
        }

        private static bool HasCommittedState(
            BuySkillResult result,
            InventoryLease lease,
            SqliteCharacterProgressRepository repository,
            IGameDatabase database,
            int baseline)
        {
            return result != null
                && result.Success
                && result.ConsumedForgetRiverWater
                && result.ConsumedForgetRiverWaterSlot == ConsumableSlot
                && result.ConsumedForgetRiverWaterItem != null
                && result.ConsumedForgetRiverWaterItem.RemainingStackCount
                    == InitialConsumableCount - 1
                && lease.Inventory.CountMainItem(
                    SkillResetConsumableService.ForgetRiverWaterItemTemplateId)
                    == InitialConsumableCount - 1
                && LoadPersistedConsumableCount(database)
                    == InitialConsumableCount - 1
                && LoadSkillLevel(repository) == baseline;
        }

        private static int LoadSkillLevel(
            SqliteCharacterProgressRepository repository)
        {
            var skills = repository.LoadSkills(CharacterId);
            var entry = skills.Pages[0].Entries.Find(
                item => item.SkillId == SkillId);
            return entry?.Level ?? 0;
        }

        private static int LoadPersistedConsumableCount(
            IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    database);
                return inventory.CountMainItem(
                    SkillResetConsumableService.ForgetRiverWaterItemTemplateId);
            }
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
VALUES(@aid, 'skill-refund-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, job, level,
    grow_type, bonus_sp, bonus_tp, town_id, area_id, direction, area_state)
VALUES(
    @cid, @aid, @name, @job, @level,
    0, 0, 0, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes("SkillRefundTransaction"));
                    command.Parameters.AddWithValue("@job", Job);
                    command.Parameters.AddWithValue("@level", Level);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void CreateSkillDeleteFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_skill_refund_skill_delete
BEFORE DELETE ON character_skills
WHEN OLD.character_id = {CharacterId}
BEGIN
    SELECT RAISE(ABORT, 'injected skill refund skill failure');
END;");
        }

        private static void CreateConsumableUpdateFailureTrigger(
            string databasePath)
        {
            ExecuteNonQuery(
                databasePath,
                $@"
CREATE TRIGGER fail_skill_refund_item_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {ConsumableSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected skill refund item failure');
END;");
        }

        private static void DropFailureTriggers(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath)
                || !File.Exists(databasePath))
            {
                return;
            }

            try
            {
                ExecuteNonQuery(
                    databasePath,
                    @"
DROP TRIGGER IF EXISTS fail_skill_refund_skill_delete;
DROP TRIGGER IF EXISTS fail_skill_refund_item_update;");
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
    }
}
