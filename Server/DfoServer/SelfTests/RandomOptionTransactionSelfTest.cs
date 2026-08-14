using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class RandomOptionTransactionSelfTest
    {
        private const int AccountId = 985900;
        private const int CharacterId = 985901;
        private const short TargetSlot = 10;
        private const int TargetUid = 985902;
        private const int InitialGold = 500_000_000;

        public static int Run()
        {
            var failures = 0;
            try
            {
                var targetItemId = ResolveTargetItemId();
                RunFault(targetItemId, change: false, failGold: false, ref failures);
                RunFault(targetItemId, change: true, failGold: true, ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] random-option transaction selftest threw: " + ex);
                failures++;
            }

            Console.WriteLine(failures == 0
                ? "RandomOptionTransactionSelfTest OK"
                : "RandomOptionTransactionSelfTest FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void RunFault(
            int targetItemId,
            bool change,
            bool failGold,
            ref int failures)
        {
            using (var fixture = new Fixture(targetItemId, change))
            {
                if (failGold)
                    fixture.CreateGoldWriteFailureTrigger();
                else
                    fixture.CreateTargetWriteFailureTrigger();

                var committed = fixture.TryCommit(out var failedResult, out var persistenceFailed);
                Check(
                    fixture.Label + " " + (failGold ? "gold" : "target") + " write failure rejects commit",
                    !committed
                    && failedResult?.GoldCost > 0
                    && persistenceFailed,
                    ref failures);
                Check(
                    fixture.Label + " write failure restores equipment and gold",
                    fixture.HasInitialState() && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                committed = fixture.TryCommit(out var result, out persistenceFailed);
                Check(
                    fixture.Label + " retries after persistence recovery",
                    committed
                    && result?.GoldCost > 0
                    && !persistenceFailed
                    && fixture.HasCommittedState(result)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
        }

        private static void Check(string label, bool condition, ref int failures)
        {
            Console.WriteLine("[" + (condition ? "PASS" : "FAIL") + "] " + label);
            if (!condition)
                failures++;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly byte[] _initialTargetBytes;

            internal Fixture(int targetItemId, bool change)
            {
                TargetItemId = targetItemId;
                Change = change;
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "random-option-transaction-" + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(DatabasePath, ServerPaths.SchemaFilePath);
                SeedCharacter(Database);

                var metadata = ItemMetadataResolver.Resolve(TargetItemId);
                var target = new ItemCore
                {
                    ItemKind = ItemCore.KindEquipment,
                    ItemId = TargetItemId,
                    Uid = TargetUid,
                    Durability = metadata.Durability,
                };
                if (change)
                {
                    if (!RandomOptionResolver.TryRollOptions(metadata, out var entries))
                        throw new InvalidOperationException("unable to seed current PVF random options");
                    target.SetRandomOptions(ToRandomOptions(entries));
                }

                var inventory = new InventoryService(CharacterId, AccountId, Database);
                inventory.SetListParam16(InventoryListType.Main, ItemSlotBoundService.MainExpandStageFull);
                inventory.SetItem(InventoryListType.Main, TargetSlot, target);
                inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    InitialGold);

                var seedLease = new InventoryLease(Guid.NewGuid(), CharacterId, inventory, 1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                    throw new InvalidOperationException("unable to persist random-option fixture");

                Lease = InventoryContext.Register(Guid.NewGuid(), CharacterId, inventory);
                _initialTargetBytes = target.ToBytes();
            }

            internal bool Change { get; }
            internal string Label => Change ? "change random option" : "unseal random option";
            internal int TargetItemId { get; }
            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }

            internal bool TryCommit(
                out RandomOptionUnsealResult result,
                out bool persistenceFailed)
            {
                return Change
                    ? InventoryRandomOptionCommitService.TryCommitChange(
                        Lease,
                        TargetSlot,
                        TargetItemId,
                        0,
                        out result,
                        out persistenceFailed)
                    : InventoryRandomOptionCommitService.TryCommitUnseal(
                        Lease,
                        TargetSlot,
                        TargetItemId,
                        out result,
                        out persistenceFailed);
            }

            internal bool HasInitialState()
                => HasInitialState(Lease.Inventory)
                    && HasInitialState(Load());

            internal bool HasCommittedState(RandomOptionUnsealResult result)
            {
                var online = Lease.Inventory;
                var persisted = Load();
                var onlineTarget = online.GetItem(InventoryListType.Main, TargetSlot);
                var persistedTarget = persisted.GetItem(InventoryListType.Main, TargetSlot);
                return onlineTarget != null
                    && persistedTarget != null
                    && onlineTarget.ToBytes().SequenceEqual(persistedTarget.ToBytes())
                    && onlineTarget.RandomOptions.Any(option => option != null && !option.IsEmpty)
                    && GetGold(online) == result.UpdatedGold
                    && GetGold(persisted) == result.UpdatedGold
                    && result.UpdatedGold < InitialGold;
            }

            internal bool HasNoDirtyState()
                => Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.DirtyMainVirtualCountSlots.Count == 0;

            internal void CreateTargetWriteFailureTrigger()
                => Execute($@"CREATE TRIGGER fail_random_option_target_update BEFORE UPDATE OF item_core ON character_inventory_items WHEN OLD.character_id={CharacterId} AND OLD.list_type={(int)InventoryListType.Main} AND OLD.slot_index={TargetSlot} BEGIN SELECT RAISE(ABORT, 'injected random-option target failure'); END;");

            internal void CreateGoldWriteFailureTrigger()
                => Execute($@"CREATE TRIGGER fail_random_option_gold_update BEFORE UPDATE OF item_core ON character_inventory_items WHEN OLD.character_id={CharacterId} AND OLD.list_type={(int)InventoryListType.Main} AND OLD.slot_index={InventoryService.MainVirtualCurrencySlotStart} BEGIN SELECT RAISE(ABORT, 'injected random-option gold failure'); END;");

            internal void DropFailureTriggers()
            {
                if (File.Exists(DatabasePath))
                    Execute("DROP TRIGGER IF EXISTS fail_random_option_target_update; DROP TRIGGER IF EXISTS fail_random_option_gold_update;");
            }

            private bool HasInitialState(InventoryService inventory)
            {
                var target = inventory.GetItem(InventoryListType.Main, TargetSlot);
                return target != null
                    && target.ToBytes().SequenceEqual(_initialTargetBytes)
                    && GetGold(inventory) == InitialGold;
            }

            private InventoryService Load()
            {
                using var connection = Database.OpenConnection();
                return InventoryService.LoadFromDb(connection, CharacterId, AccountId, Database);
            }

            private void Execute(string sql)
            {
                using var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = DatabasePath,
                        ForeignKeys = true,
                    }.ToString());
                connection.Open();
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
                    try { if (File.Exists(DatabasePath + suffix)) File.Delete(DatabasePath + suffix); } catch { }
            }
        }

        private static int GetGold(InventoryService inventory)
            => inventory.GetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;

        private static List<RandomOption> ToRandomOptions(
            IReadOnlyList<RandomOptionEntry> entries)
        {
            var result = new List<RandomOption>();
            if (entries == null)
                return result;

            foreach (var entry in entries.Take(3))
            {
                if (entry == null)
                    continue;
                result.Add(new RandomOption
                {
                    Type = entry.Type,
                    Value1 = entry.Value1,
                    Value2 = entry.Value2,
                });
            }
            return result;
        }

        private static int ResolveTargetItemId()
        {
            foreach (var itemId in new[] { 101010653, 0x00006B8B, 33000 })
                if (CanUseTarget(itemId))
                    return itemId;

            var list = LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst"));
            foreach (var entry in list.Entries)
                if (entry != null && entry.Id > 0 && CanUseTarget(entry.Id))
                    return entry.Id;

            throw new InvalidOperationException("unable to resolve current PVF random-option equipment target");
        }

        private static bool CanUseTarget(int itemId)
        {
            try
            {
                var metadata = ItemMetadataResolver.Resolve(itemId);
                return metadata != null
                    && RandomOptionResolver.ResolveBreakSealGoldCost(metadata) > 0
                    && RandomOptionResolver.ResolveOptionModificationGoldCost(metadata) > 0
                    && RandomOptionResolver.TryRollOptions(metadata, out var entries)
                    && entries != null
                    && entries.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void SeedCharacter(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO accounts(account_id,m_id,password_hash) VALUES(@aid,'random-option-transaction',''); INSERT INTO characters(character_id,account_id,name,level,town_id,area_id,direction,area_state) VALUES(@cid,@aid,@name,86,1,0,5,3);";
                command.Parameters.AddWithValue("@aid", AccountId);
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@name", Encoding.UTF8.GetBytes("RandomOptionTransaction"));
                command.ExecuteNonQuery();
            });
        }
    }
}
