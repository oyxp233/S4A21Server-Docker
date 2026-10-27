using System;
using System.IO;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class UnlimitedStackableUseSelfTest
    {
        public static int Run()
        {
            int checks = 0, failures = 0;
            void Check(string name, bool ok) { checks++; Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (!ok) failures++; }
            var tempDbPath = BuildTempDatabasePath("unlimited-stackable");
            try
            {
                var reported = new[] { 10008052, 490700049, 490700050, 490700692, 10002345, 490700369 };
                // 这些来源不再由商店出售，因此按物品定义和已报告编号验证；线上 PVF 与官方原版
                // PVF 的 stackable 无限使用物品集合相同（已按 stackable.lst 逐条核对）。
                Check("current PVF marks the reported unlimited potions as [unlimited waste]",
                    reported.All(StackableItemProvider.RetainsSourceOnUse));
                foreach (int id in reported)
                {
                    var inventory = new InventoryService(99101, 99100);
                    inventory.SetItem(InventoryListType.Main, 65, Stack(id, 1));
                    Check("PVF unlimited use keeps the final source: " + id,
                        InventoryDeleteService.TryUseStackableForClient(inventory, InventoryListType.Main, 65, id, out var change)
                        && change.AppliedCount == 0 && change.RemainingStackCount == 1
                        && inventory.GetItem(InventoryListType.Main, 65).Count == 1);
                }
                var normal = new InventoryService(99101, 99100);
                normal.SetItem(InventoryListType.Main, 65, Stack(2600021, 2));
                Check("ordinary waste subtype zero still consumes one", !StackableItemProvider.RetainsSourceOnUse(2600021)
                    && InventoryDeleteService.TryUseStackableForClient(normal, InventoryListType.Main, 65, 2600021, out var normalChange)
                    && normalChange.AppliedCount == 1 && normalChange.RemainingStackCount == 1);

                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                int character = 99110;
                foreach (int id in reported)
                {
                    character++;
                    int account = character + 100;
                    SeedOwner(database, account, character, "unlimited-" + id);
                    Guid session = Guid.NewGuid();
                    var lease = LoadAndRegister(database, session, account, character);
                    try
                    {
                        lease.Inventory.SetItem(InventoryListType.Main, 65, Stack(id, 1));
                        if (!InventoryPersistenceService.SaveDirty(lease)) throw new Exception("stock save failed");
                        var use = InventoryDeleteCommitService.TryCommitStackableUseDetailed(lease, InventoryListType.Main, 65, id);
                        Check("successful use and actual consumption are distinct: " + id,
                            use?.Committed == true && use.Used && !use.Consumed && use.Mutation.AppliedCount == 0
                            && lease.Inventory.GetItem(InventoryListType.Main, 65).Count == 1);
                        var response = InventoryHandler.BuildUseStackableResponsePlan(use?.Used == true, use?.Mutation,
                            InventoryListType.Main, 65, 1, id);
                        Check("retained use receives the normal success ACK: " + id, response.Accepted && response.AckBody[0] == 1);
                        InventoryDeleteCommitService.TryCommitStackableUseDetailed(lease, InventoryListType.Main, 65, id);
                        Check("repeated use cannot remove the source: " + id, lease.Inventory.GetItem(InventoryListType.Main, 65)?.Count == 1);
                        ReleaseLease(session, character, lease);
                        var oldLease = lease; session = Guid.NewGuid();
                        lease = LoadAndRegister(database, session, account, character);
                        Check("return to selection and fresh load preserve the source: " + id,
                            lease.Inventory.GetItem(InventoryListType.Main, 65)?.Count == 1
                            && InventoryDeleteCommitService.TryCommitStackableUseDetailed(oldLease, InventoryListType.Main, 65, id) == null);
                        Check("wrong expected item cannot use a retained source: " + id,
                            InventoryDeleteCommitService.TryCommitStackableUseDetailed(lease, InventoryListType.Main, 65, 36) == null
                            && lease.Inventory.GetItem(InventoryListType.Main, 65).Count == 1);
                        if (id == 10008052)
                        {
                            // 留一条待落库的脏记录，让提交事务真正 UPDATE 库存行，再由触发器强制失败。
                            lease.Inventory.SetItem(InventoryListType.Main, 65, Stack(id, 1));
                            database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                                cmd.CommandText = "CREATE TRIGGER unlimited_fail BEFORE UPDATE ON character_inventory_items FOR EACH ROW BEGIN SELECT RAISE(ABORT, 'retained use rollback test'); END;"; cmd.ExecuteNonQuery(); });
                            Check("failed commit does not report a successful retained use",
                                InventoryDeleteCommitService.TryCommitStackableUseDetailed(lease, InventoryListType.Main, 65, id) == null
                                && lease.Inventory.GetItem(InventoryListType.Main, 65).Count == 1);
                            database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                                cmd.CommandText = "DROP TRIGGER unlimited_fail;"; cmd.ExecuteNonQuery(); });
                        }
                        Check("explicit discard still removes an unlimited source: " + id,
                            InventoryDeleteCommitService.TryCommit(lease, InventoryListType.Main, 65, 1, out var discarded)
                            && discarded.AppliedCount == 1 && lease.Inventory.GetItem(InventoryListType.Main, 65) == null);
                        lease.Inventory.SetItem(InventoryListType.Main, 65, Stack(id, 1, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1));
                        InventoryPersistenceService.SaveDirty(lease);
                        var expired = InventoryDeleteCommitService.TryCommitStackableUseDetailed(lease, InventoryListType.Main, 65, id);
                        Check("expiration cleanup is retained without accepting the use: " + id,
                            expired?.SourceExpiredDeleted == true && !expired.Used && !expired.Consumed
                            && lease.Inventory.GetItem(InventoryListType.Main, 65) == null);
                    }
                    finally { ReleaseLease(session, character, lease); }
                }
            }
            catch (Exception ex) { Check(ex.ToString(), false); }
            finally { TryDeleteDatabase(tempDbPath); }
            Console.WriteLine($"UNLIMITED_STACKABLE_USE checks={checks} failures={failures}"); return failures == 0 ? 0 : 1;
        }

        private static ItemCore Stack(int id, int count, int expiry = 0)
            => new ItemCore { ItemKind = ItemCore.KindConsumable, ItemId = id, Count = count, ExpireTime = expiry };

        private static void SeedOwner(GameDatabase database, int accountId, int characterId, string name)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @name, '');
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@name", name);
                command.ExecuteNonQuery();
            }
        }

        private static InventoryLease LoadAndRegister(
            GameDatabase database,
            Guid sessionId,
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
                return InventoryContext.Register(sessionId, characterId, inventory);
            }
        }

        private static void ReleaseLease(Guid sessionId, int characterId, InventoryLease lease)
        {
            if (lease == null)
                return;

            InventoryPersistenceService.SaveDirty(lease);
            InventoryContext.Unregister(sessionId, characterId);
        }

        private static string BuildTempDatabasePath(string suffix)
        {
            return Path.Combine(
                Path.GetTempPath(),
                $"dfo_unlimited_stackable_{suffix}_{Guid.NewGuid():N}.db");
        }

        private static void TryDeleteDatabase(string path)
        {
            SqliteConnection.ClearAllPools();
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
    }
}
