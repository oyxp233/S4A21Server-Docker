using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class CardEmblemTransactionSelfTest
    {
        private const int AccountId = 986100;
        private const int CharacterId = 986101;
        private const short SourceSlot = 10;
        private const short MaterialSlot = 11;
        private const int InitialGold = 10_000;
        private const short BindFirstCardSlot = 11;

        public static int Run()
        {
            var failures = 0;
            try
            {
                RunEmblemCompound(ref failures);
                RunMonsterCardBind(ref failures);
                RunMonsterCardUpgrade(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] card/emblem transaction selftest threw: " + ex);
                failures++;
            }

            Console.WriteLine(failures == 0
                ? "CardEmblemTransactionSelfTest OK"
                : "CardEmblemTransactionSelfTest FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void RunEmblemCompound(ref int failures)
        {
            var emblemItemId = ResolveAvatarEmblemItemId();
            using (var fixture = new EmblemFixture(emblemItemId))
            {
                fixture.CreateSourceDeleteFailureTrigger();
                var committed = fixture.TryCommit(out var failed, out var persistenceFailed);
                Check("emblem compound source DELETE failure rejects commit",
                    !committed && failed && persistenceFailed, ref failures);
                Check("emblem compound failure restores source and reward state",
                    fixture.HasInitialState() && fixture.HasNoDirtyState(), ref failures);

                fixture.DropFailureTriggers();
                committed = fixture.TryCommit(out var result, out persistenceFailed);
                Check("emblem compound retries after persistence recovery",
                    committed && result && !persistenceFailed
                    && fixture.HasCommittedState() && fixture.HasNoDirtyState(), ref failures);
            }
        }

        private static void RunMonsterCardUpgrade(ref int failures)
        {
            using (var fixture = new MonsterCardUpgradeFixture())
            {
                fixture.CreateGoldWriteFailureTrigger();
                var committed = fixture.TryCommit(out var failed, out var persistenceFailed);
                Check("monster card upgrade gold UPDATE failure rejects commit",
                    !committed && failed && persistenceFailed, ref failures);
                Check("monster card upgrade failure restores cards and gold",
                    fixture.HasInitialState() && fixture.HasNoDirtyState(), ref failures);

                fixture.DropFailureTriggers();
                committed = fixture.TryCommit(out var result, out persistenceFailed);
                Check("monster card upgrade retries after persistence recovery",
                    committed && result && !persistenceFailed
                    && fixture.HasCommittedState() && fixture.HasNoDirtyState(), ref failures);
            }
        }

        private static void RunMonsterCardBind(ref int failures)
        {
            var binderItemId = ResolveMonsterCardBinderItemId();
            using (var fixture = new MonsterCardBindFixture(binderItemId))
            {
                fixture.CreateBinderDeleteFailureTrigger();
                var committed = fixture.TryCommit(out var failed, out var persistenceFailed);
                Check("monster card bind binder DELETE failure rejects commit",
                    !committed && failed && persistenceFailed, ref failures);
                Check("monster card bind failure restores binder, cards, and reward state",
                    fixture.HasInitialState() && fixture.HasNoDirtyState(), ref failures);

                fixture.DropFailureTriggers();
                committed = fixture.TryCommit(out var result, out persistenceFailed);
                Check("monster card bind retries after persistence recovery",
                    committed && result && !persistenceFailed
                    && fixture.HasCommittedState() && fixture.HasNoDirtyState(), ref failures);
            }
        }

        private static void Check(string label, bool condition, ref int failures)
        {
            Console.WriteLine("[" + (condition ? "PASS" : "FAIL") + "] " + label);
            if (!condition)
                failures++;
        }

        private sealed class EmblemFixture : IDisposable
        {
            private readonly int _emblemItemId;
            private readonly byte[] _initialSourceBytes;
            private readonly EmblemCompoundRequest _request;
            private EmblemCompoundResult _result;

            internal EmblemFixture(int emblemItemId)
            {
                _emblemItemId = emblemItemId;
                DatabasePath = Path.Combine(Path.GetTempPath(), "emblem-compound-transaction-" + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(DatabasePath, ServerPaths.SchemaFilePath);
                SeedCharacter(Database);
                var inventory = new InventoryService(CharacterId, AccountId, Database);
                inventory.SetListParam16(InventoryListType.Main, ItemSlotBoundService.MainExpandStageFull);
                var source = ItemCore.Create(ItemCore.KindConsumable, emblemItemId);
                source.Count = 2;
                inventory.SetItem(InventoryListType.Main, SourceSlot, source);
                _request = new EmblemCompoundRequest();
                _request.Inputs.Add(new EmblemCompoundInput { SlotIndex = SourceSlot, ItemTemplateId = emblemItemId });
                _request.Inputs.Add(new EmblemCompoundInput { SlotIndex = SourceSlot, ItemTemplateId = emblemItemId });
                var seedLease = new InventoryLease(Guid.NewGuid(), CharacterId, inventory, 1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                    throw new InvalidOperationException("unable to persist emblem fixture");
                Lease = InventoryContext.Register(Guid.NewGuid(), CharacterId, inventory);
                _initialSourceBytes = source.ToBytes();
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }

            internal bool TryCommit(out bool success, out bool persistenceFailed)
            {
                var committed = InventoryCardEmblemCommitService.TryCommitEmblemCompound(
                    Lease, _request, out _result, out persistenceFailed);
                success = _result != null && _result.ErrorCode == 0;
                return committed;
            }

            internal bool HasInitialState()
                => HasInitialState(Lease.Inventory) && HasInitialState(Load());

            internal bool HasCommittedState()
            {
                var item = Lease.Inventory.GetItem(InventoryListType.Main, SourceSlot);
                var persisted = Load().GetItem(InventoryListType.Main, SourceSlot);
                return _result != null
                    && item == null
                    && persisted == null;
            }

            internal bool HasNoDirtyState() => Lease.Inventory.DirtyListTypes.Count == 0;

            internal void CreateSourceDeleteFailureTrigger()
                => Execute($"CREATE TRIGGER fail_emblem_source_delete BEFORE DELETE ON character_inventory_items WHEN OLD.character_id={CharacterId} AND OLD.list_type=0 AND OLD.slot_index={SourceSlot} BEGIN SELECT RAISE(ABORT, 'injected emblem source failure'); END;");

            internal void DropFailureTriggers()
            {
                if (File.Exists(DatabasePath)) Execute("DROP TRIGGER IF EXISTS fail_emblem_source_delete;");
            }

            private bool HasInitialState(InventoryService inventory)
            {
                var source = inventory.GetItem(InventoryListType.Main, SourceSlot);
                return source != null && source.ToBytes().SequenceEqual(_initialSourceBytes);
            }

            private InventoryService Load()
            {
                using var connection = Database.OpenConnection();
                return InventoryService.LoadFromDb(connection, CharacterId, AccountId, Database);
            }

            private void Execute(string sql)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true }.ToString());
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

        private sealed class MonsterCardUpgradeFixture : IDisposable
        {
            private const int TargetCardId = 3619;
            private const int MaterialCardId = 3620;
            private readonly byte[] _initialTargetBytes;
            private readonly byte[] _initialMaterialBytes;
            private readonly MonsterCardUpgradeService _service = new MonsterCardUpgradeService();

            internal MonsterCardUpgradeFixture()
            {
                DatabasePath = Path.Combine(Path.GetTempPath(), "monster-card-upgrade-transaction-" + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(DatabasePath, ServerPaths.SchemaFilePath);
                SeedCharacter(Database);
                var inventory = new InventoryService(CharacterId, AccountId, Database);
                inventory.SetListParam16(InventoryListType.Main, ItemSlotBoundService.MainExpandStageFull);
                var target = ItemCore.Create(ItemCore.KindExpertJobMaterial, TargetCardId);
                target.Count = 2;
                var material = ItemCore.Create(ItemCore.KindExpertJobMaterial, MaterialCardId);
                material.Count = 1;
                inventory.SetItem(InventoryListType.Main, SourceSlot, target);
                inventory.SetItem(InventoryListType.Main, MaterialSlot, material);
                inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 10_000);
                var seedLease = new InventoryLease(Guid.NewGuid(), CharacterId, inventory, 1);
                if (!InventoryPersistenceService.SaveDirty(seedLease)) throw new InvalidOperationException("unable to persist card fixture");
                Lease = InventoryContext.Register(Guid.NewGuid(), CharacterId, inventory);
                _initialTargetBytes = target.ToBytes();
                _initialMaterialBytes = material.ToBytes();
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }

            internal bool TryCommit(out bool success, out bool persistenceFailed)
            {
                var committed = InventoryCardEmblemCommitService.TryCommitMonsterCardUpgrade(
                    Lease, _service, InventoryListType.Main, SourceSlot, MaterialSlot, 1,
                    out var result, out _, out persistenceFailed);
                success = result != null;
                return committed;
            }

            internal bool HasInitialState()
                => HasInitialState(Lease.Inventory) && HasInitialState(Load());

            internal bool HasCommittedState()
            {
                var online = Lease.Inventory;
                var persisted = Load();
                return GetGold(online) < 10_000
                    && GetGold(persisted) == GetGold(online)
                    && online.GetItem(InventoryListType.Main, SourceSlot)?.ToBytes().SequenceEqual(persisted.GetItem(InventoryListType.Main, SourceSlot)?.ToBytes() ?? Array.Empty<byte>()) == true;
            }

            internal bool HasNoDirtyState() => Lease.Inventory.DirtyListTypes.Count == 0 && Lease.Inventory.DirtyMainVirtualCountSlots.Count == 0;
            internal void CreateGoldWriteFailureTrigger() => Execute($"CREATE TRIGGER fail_card_gold_update BEFORE UPDATE OF item_core ON character_inventory_items WHEN OLD.character_id={CharacterId} AND OLD.list_type=0 AND OLD.slot_index={InventoryService.MainVirtualCurrencySlotStart} BEGIN SELECT RAISE(ABORT, 'injected card gold failure'); END;");
            internal void DropFailureTriggers() { if (File.Exists(DatabasePath)) Execute("DROP TRIGGER IF EXISTS fail_card_gold_update;"); }
            private bool HasInitialState(InventoryService inventory)
                => inventory.GetItem(InventoryListType.Main, SourceSlot)?.ToBytes().SequenceEqual(_initialTargetBytes) == true
                    && inventory.GetItem(InventoryListType.Main, MaterialSlot)?.ToBytes().SequenceEqual(_initialMaterialBytes) == true
                    && GetGold(inventory) == 10_000;
            private InventoryService Load() { using var connection = Database.OpenConnection(); return InventoryService.LoadFromDb(connection, CharacterId, AccountId, Database); }
            private static int GetGold(InventoryService inventory) => inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
            private void Execute(string sql) { using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true }.ToString()); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            public void Dispose() { try { DropFailureTriggers(); } catch { } InventoryContext.Unregister(Lease.SessionId, Lease.CharacterId); SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) try { if (File.Exists(DatabasePath + suffix)) File.Delete(DatabasePath + suffix); } catch { } }
        }

        private sealed class MonsterCardBindFixture : IDisposable
        {
            private const int CardItemId = 3619;
            private readonly byte[] _initialBinderBytes;
            private readonly byte[] _initialCardBytes;
            private readonly MonsterCardBindService _service = new MonsterCardBindService(
                MonsterCardBindConfigProvider.Current,
                _ => 0);
            private MonsterCardBindResult _result;

            internal MonsterCardBindFixture(int binderItemId)
            {
                DatabasePath = Path.Combine(Path.GetTempPath(), "monster-card-bind-transaction-" + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(DatabasePath, ServerPaths.SchemaFilePath);
                SeedCharacter(Database);
                var inventory = new InventoryService(CharacterId, AccountId, Database);
                inventory.SetListParam16(InventoryListType.Main, ItemSlotBoundService.MainExpandStageFull);
                var binder = ItemCore.Create(ItemCore.KindConsumable, binderItemId);
                binder.Count = 1;
                var card = ItemCore.Create(ItemCore.KindExpertJobMaterial, CardItemId);
                card.Count = 2;
                inventory.SetItem(InventoryListType.Main, SourceSlot, binder);
                inventory.SetItem(InventoryListType.Main, BindFirstCardSlot, card);
                var seedLease = new InventoryLease(Guid.NewGuid(), CharacterId, inventory, 1);
                if (!InventoryPersistenceService.SaveDirty(seedLease)) throw new InvalidOperationException("unable to persist bind fixture");
                Lease = InventoryContext.Register(Guid.NewGuid(), CharacterId, inventory);
                _initialBinderBytes = binder.ToBytes();
                _initialCardBytes = card.ToBytes();
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }
            internal bool TryCommit(out bool success, out bool persistenceFailed)
            {
                var committed = InventoryCardEmblemCommitService.TryCommitMonsterCardBind(
                    Lease, _service, SourceSlot, BindFirstCardSlot, BindFirstCardSlot,
                    out _result, out _, out persistenceFailed);
                success = _result != null;
                return committed;
            }
            internal bool HasInitialState() => HasInitialState(Lease.Inventory) && HasInitialState(Load());
            internal bool HasCommittedState()
            {
                var online = Lease.Inventory;
                var persisted = Load();
                var onlineReward = _result == null ? null : online.GetItem(InventoryListType.Main, _result.Grant.SlotIndex);
                var persistedReward = _result == null ? null : persisted.GetItem(InventoryListType.Main, _result.Grant.SlotIndex);
                return _result != null
                    && online.GetItem(InventoryListType.Main, SourceSlot) == null
                    && persisted.GetItem(InventoryListType.Main, SourceSlot) == null
                    && online.GetItem(InventoryListType.Main, BindFirstCardSlot) == null
                    && persisted.GetItem(InventoryListType.Main, BindFirstCardSlot) == null
                    && onlineReward?.ItemId == _result.ResultItemId
                    && persistedReward?.ItemId == _result.ResultItemId;
            }
            internal bool HasNoDirtyState() => Lease.Inventory.DirtyListTypes.Count == 0;
            internal void CreateBinderDeleteFailureTrigger() => Execute($"CREATE TRIGGER fail_card_bind_binder_delete BEFORE DELETE ON character_inventory_items WHEN OLD.character_id={CharacterId} AND OLD.list_type=0 AND OLD.slot_index={SourceSlot} BEGIN SELECT RAISE(ABORT, 'injected card bind failure'); END;");
            internal void DropFailureTriggers() { if (File.Exists(DatabasePath)) Execute("DROP TRIGGER IF EXISTS fail_card_bind_binder_delete;"); }
            private bool HasInitialState(InventoryService inventory)
                => inventory.GetItem(InventoryListType.Main, SourceSlot)?.ToBytes().SequenceEqual(_initialBinderBytes) == true
                    && inventory.GetItem(InventoryListType.Main, BindFirstCardSlot)?.ToBytes().SequenceEqual(_initialCardBytes) == true;
            private InventoryService Load() { using var connection = Database.OpenConnection(); return InventoryService.LoadFromDb(connection, CharacterId, AccountId, Database); }
            private void Execute(string sql) { using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true }.ToString()); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            public void Dispose() { try { DropFailureTriggers(); } catch { } InventoryContext.Unregister(Lease.SessionId, Lease.CharacterId); SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) try { if (File.Exists(DatabasePath + suffix)) File.Delete(DatabasePath + suffix); } catch { } }
        }

        private static int ResolveAvatarEmblemItemId()
        {
            var list = LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst"));
            foreach (var entry in list.Entries)
            {
                if (entry == null || entry.Id <= 0) continue;
                var metadata = ItemMetadataResolver.Resolve(entry.Id);
                if (metadata != null && metadata.IsStackable && metadata.Grade > 0 && metadata.StackableType != null && metadata.StackableType.IndexOf("avatar emblem", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (EmblemCompoundConfigProvider.TryRollReward(metadata.Grade, 2, out _, out _, out _)) return entry.Id;
                }
            }
            throw new InvalidOperationException("unable to resolve avatar emblem fixture");
        }

        private static int ResolveMonsterCardBinderItemId()
        {
            var list = LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst"));
            foreach (var entry in list.Entries)
            {
                if (entry == null || entry.Id <= 0
                    || !ItemMetadataResolver.TryLoadStackableFile(entry.Id, out var file)
                    || file.MonsterCardBind < 0)
                    continue;

                var inventory = new InventoryService(1, 1);
                var binder = ItemCore.Create(ItemCore.KindConsumable, entry.Id);
                binder.Count = 1;
                var card = ItemCore.Create(ItemCore.KindExpertJobMaterial, 3619);
                card.Count = 2;
                inventory.SetItem(InventoryListType.Main, SourceSlot, binder);
                inventory.SetItem(InventoryListType.Main, BindFirstCardSlot, card);
                var service = new MonsterCardBindService(MonsterCardBindConfigProvider.Current, _ => 0);
                if (service.TryBind(inventory, SourceSlot, BindFirstCardSlot, BindFirstCardSlot, out _, out _))
                    return entry.Id;
            }
            throw new InvalidOperationException("unable to resolve monster card binder fixture");
        }

        private static void SeedCharacter(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = @"INSERT INTO accounts(account_id,m_id,password_hash) VALUES(@aid,'card-emblem-transaction',''); INSERT INTO characters(character_id,account_id,name,level,town_id,area_id,direction,area_state) VALUES(@cid,@aid,@name,86,1,0,5,3);";
                command.Parameters.AddWithValue("@aid", AccountId); command.Parameters.AddWithValue("@cid", CharacterId); command.Parameters.AddWithValue("@name", Encoding.UTF8.GetBytes("CardEmblemTransaction")); command.ExecuteNonQuery();
            });
        }
    }
}
