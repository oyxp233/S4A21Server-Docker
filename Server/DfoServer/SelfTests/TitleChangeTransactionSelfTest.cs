using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class TitleChangeTransactionSelfTest
    {
        private const int AccountId = 986000;
        private const int CharacterId = 986001;
        private const short TitleSourceSlot = 118;
        private const short TitleTargetSlot = 47;
        private const int TitleSourceItemId = 10007724;
        private const int TitleTargetItemId = 400330031;
        private const short LimitedSourceSlot = 108;
        private const short LimitedTargetSlot = 55;
        private const int LimitedSourceItemId = 2683522;
        private const int LimitedTargetItemId = 100330789;
        private const int ClearCubeItemId = 3037;
        private const int ClearCubeCount = 20;

        public static int Run()
        {
            var failures = 0;
            try
            {
                RunFault(ChangeKind.Title, failTarget: true, ref failures);
                RunFault(ChangeKind.LimitedCube, failTarget: false, ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] title-change transaction selftest threw: " + ex);
                failures++;
            }

            Console.WriteLine(failures == 0
                ? "TitleChangeTransactionSelfTest OK"
                : "TitleChangeTransactionSelfTest FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void RunFault(ChangeKind kind, bool failTarget, ref int failures)
        {
            using (var fixture = new Fixture(kind))
            {
                if (failTarget)
                    fixture.CreateTargetWriteFailureTrigger();
                else
                    fixture.CreateAdditionalMaterialWriteFailureTrigger();

                var committed = fixture.TryCommit(out var failedResult, out var persistenceFailed);
                Check(
                    fixture.Label + " " + (failTarget ? "target" : "additional material") + " write failure rejects commit",
                    !committed && failedResult?.Success == true && persistenceFailed,
                    ref failures);
                Check(
                    fixture.Label + " write failure restores source, target, and materials",
                    fixture.HasInitialState() && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                committed = fixture.TryCommit(out var result, out persistenceFailed);
                Check(
                    fixture.Label + " retries after persistence recovery",
                    committed
                    && result?.Success == true
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

        private enum ChangeKind
        {
            Title,
            LimitedCube,
        }

        private sealed class Fixture : IDisposable
        {
            private readonly byte[] _initialTargetBytes;
            private readonly byte[] _initialSourceBytes;
            private readonly int _initialClearCubeCount;
            private readonly InventoryTitleChangeRequest _request;
            private readonly InventoryTitleChangeResolution _resolution;

            internal Fixture(ChangeKind kind)
            {
                Kind = kind;
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "title-change-transaction-" + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(DatabasePath, ServerPaths.SchemaFilePath);
                SeedCharacter(Database);

                var inventory = new InventoryService(CharacterId, AccountId, Database);
                inventory.SetListParam16(InventoryListType.Main, ItemSlotBoundService.MainExpandStageFull);

                var sourceSlot = kind == ChangeKind.Title ? TitleSourceSlot : LimitedSourceSlot;
                var targetSlot = kind == ChangeKind.Title ? TitleTargetSlot : LimitedTargetSlot;
                var sourceItemId = kind == ChangeKind.Title ? TitleSourceItemId : LimitedSourceItemId;
                var targetItemId = kind == ChangeKind.Title ? TitleTargetItemId : LimitedTargetItemId;
                var source = ItemCore.Create(ItemCore.KindConsumable, sourceItemId);
                source.Count = 2;
                var target = ItemCore.Create(ItemCore.KindEquipment, targetItemId);
                target.Value = 1234;
                target.Attr = 0x5A;
                inventory.SetItem(InventoryListType.Main, sourceSlot, source);
                inventory.SetItem(InventoryListType.Main, targetSlot, target);

                if (kind == ChangeKind.LimitedCube)
                {
                    InventoryService.TryResolveMainVirtualSlotByItemId(
                        ClearCubeItemId,
                        out var clearCubeSlot,
                        out _);
                    inventory.SetMainVirtualCount(clearCubeSlot, ClearCubeCount);
                    _initialClearCubeCount = ClearCubeCount;
                }

                _request = new InventoryTitleChangeRequest
                {
                    SourceSlotIndex = sourceSlot,
                    TargetSlotIndex = targetSlot,
                    SourceItemId = sourceItemId,
                    TargetItemId = targetItemId,
                };
                var resolved = kind == ChangeKind.Title
                    ? InventoryTitleChangeRuleResolver.TryResolveTitleChange(
                        sourceItemId,
                        targetItemId,
                        out _resolution)
                    : InventoryTitleChangeRuleResolver.TryResolveLimitedCube(
                        sourceItemId,
                        targetItemId,
                        out _resolution);
                if (!resolved || _resolution == null)
                    throw new InvalidOperationException("unable to resolve title-change fixture rule");

                var seedLease = new InventoryLease(Guid.NewGuid(), CharacterId, inventory, 1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                    throw new InvalidOperationException("unable to persist title-change fixture");

                Lease = InventoryContext.Register(Guid.NewGuid(), CharacterId, inventory);
                _initialSourceBytes = source.ToBytes();
                _initialTargetBytes = target.ToBytes();
            }

            internal ChangeKind Kind { get; }
            internal string Label => Kind == ChangeKind.Title ? "title change" : "limited cube";
            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }

            internal bool TryCommit(
                out InventoryTitleChangeResult result,
                out bool persistenceFailed)
            {
                return Kind == ChangeKind.Title
                    ? InventoryTitleChangeCommitService.TryCommitTitleChange(
                        Lease,
                        _request,
                        _resolution,
                        out result,
                        out persistenceFailed)
                    : InventoryTitleChangeCommitService.TryCommitLimitedCube(
                        Lease,
                        _request,
                        _resolution,
                        out result,
                        out persistenceFailed);
            }

            internal bool HasInitialState()
                => HasInitialState(Lease.Inventory)
                    && HasInitialState(Load());

            internal bool HasCommittedState(InventoryTitleChangeResult result)
            {
                var online = Lease.Inventory;
                var persisted = Load();
                var sourceSlot = _request.SourceSlotIndex;
                var targetSlot = _request.TargetSlotIndex;
                var onlineSource = online.GetItem(InventoryListType.Main, sourceSlot);
                var persistedSource = persisted.GetItem(InventoryListType.Main, sourceSlot);
                var onlineTarget = online.GetItem(InventoryListType.Main, targetSlot);
                var persistedTarget = persisted.GetItem(InventoryListType.Main, targetSlot);
                return onlineSource != null
                    && persistedSource != null
                    && onlineTarget != null
                    && persistedTarget != null
                    && onlineSource.Count == result.SourceRemainingCount
                    && persistedSource.Count == result.SourceRemainingCount
                    && onlineTarget.ItemId == result.ResultItemId
                    && persistedTarget.ItemId == result.ResultItemId
                    && onlineTarget.ToBytes().SequenceEqual(persistedTarget.ToBytes())
                    && (Kind != ChangeKind.LimitedCube
                        || GetClearCubeCount(online) < _initialClearCubeCount
                            && GetClearCubeCount(persisted) < _initialClearCubeCount);
            }

            internal bool HasNoDirtyState()
                => Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.DirtyMainVirtualCountSlots.Count == 0;

            internal void CreateTargetWriteFailureTrigger()
                => Execute($@"CREATE TRIGGER fail_title_change_target_update BEFORE UPDATE OF item_core ON character_inventory_items WHEN OLD.character_id={CharacterId} AND OLD.list_type=0 AND OLD.slot_index={_request.TargetSlotIndex} BEGIN SELECT RAISE(ABORT, 'injected title target failure'); END;");

            internal void CreateAdditionalMaterialWriteFailureTrigger()
            {
                InventoryService.TryResolveMainVirtualSlotByItemId(
                    ClearCubeItemId,
                    out var clearCubeSlot,
                    out _);
                Execute($@"CREATE TRIGGER fail_title_change_clear_cube_update BEFORE UPDATE OF cube_clear ON accounts WHEN OLD.account_id={AccountId} BEGIN SELECT RAISE(ABORT, 'injected title additional material failure'); END;");
            }

            internal void DropFailureTriggers()
            {
                if (File.Exists(DatabasePath))
                    Execute("DROP TRIGGER IF EXISTS fail_title_change_target_update; DROP TRIGGER IF EXISTS fail_title_change_clear_cube_update;");
            }

            private bool HasInitialState(InventoryService inventory)
            {
                var source = inventory.GetItem(InventoryListType.Main, _request.SourceSlotIndex);
                var target = inventory.GetItem(InventoryListType.Main, _request.TargetSlotIndex);
                return source != null
                    && target != null
                    && source.ToBytes().SequenceEqual(_initialSourceBytes)
                    && target.ToBytes().SequenceEqual(_initialTargetBytes)
                    && (Kind != ChangeKind.LimitedCube || GetClearCubeCount(inventory) == _initialClearCubeCount);
            }

            private int GetClearCubeCount(InventoryService inventory)
            {
                return InventoryService.TryResolveMainVirtualSlotByItemId(
                        ClearCubeItemId,
                        out var clearCubeSlot,
                        out _)
                    ? inventory.GetMainVirtualCount(clearCubeSlot)?.Count ?? 0
                    : 0;
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

        private static void SeedCharacter(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO accounts(account_id,m_id,password_hash) VALUES(@aid,'title-change-transaction',''); INSERT INTO characters(character_id,account_id,name,level,town_id,area_id,direction,area_state) VALUES(@cid,@aid,@name,86,1,0,5,3);";
                command.Parameters.AddWithValue("@aid", AccountId);
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@name", Encoding.UTF8.GetBytes("TitleChangeTransaction"));
                command.ExecuteNonQuery();
            });
        }
    }
}
