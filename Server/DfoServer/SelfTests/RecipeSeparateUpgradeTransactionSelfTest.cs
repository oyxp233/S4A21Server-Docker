using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class RecipeSeparateUpgradeTransactionSelfTest
    {
        private const int AccountId = 986200;
        private const int CharacterId = 986201;
        private const int RecipeItemId = 2600513;
        private const int SeparateTargetItemId = 101010653;
        private const int SeparateMaterialItemId = 3326;
        private const short SeparateTargetSlot = 11;
        private const short SeparateMaterialSlot = 134;

        public static int Run()
        {
            var failures = 0;
            try
            {
                using (var fixture = new RecipeFixture())
                {
                    fixture.CreateRewardInsertFailureTrigger();
                    var committed = fixture.TryCommit(out var failed, out var persistenceFailed);
                    Check("compound recipe reward INSERT failure rejects commit",
                        !committed && failed?.Success == true && persistenceFailed, ref failures);
                    Check("compound recipe failure restores materials, gold, and reward state",
                        fixture.HasInitialState() && fixture.HasNoDirtyState(), ref failures);

                    fixture.DropFailureTriggers();
                    committed = fixture.TryCommit(out var result, out persistenceFailed);
                    Check("compound recipe retries after persistence recovery",
                        committed && result?.Success == true && !persistenceFailed
                        && fixture.HasCommittedState(result) && fixture.HasNoDirtyState(), ref failures);
                }

                using (var fixture = new SeparateUpgradeFixture())
                {
                    fixture.CreateMaterialWriteFailureTrigger();
                    var committed = fixture.TryCommit(out var failed, out var persistenceFailed);
                    Check("separate upgrade material UPDATE failure rejects commit",
                        !committed && failed != null && persistenceFailed, ref failures);
                    Check("separate upgrade failure restores target and material",
                        fixture.HasInitialState() && fixture.HasNoDirtyState(), ref failures);

                    fixture.DropFailureTriggers();
                    committed = fixture.TryCommit(out var result, out persistenceFailed);
                    Check("separate upgrade retries after persistence recovery",
                        committed && result != null && !persistenceFailed
                        && fixture.HasCommittedState(result) && fixture.HasNoDirtyState(), ref failures);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] recipe/separate-upgrade transaction selftest threw: " + ex);
                failures++;
            }

            Console.WriteLine(failures == 0
                ? "RecipeSeparateUpgradeTransactionSelfTest OK"
                : "RecipeSeparateUpgradeTransactionSelfTest FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string label, bool condition, ref int failures)
        {
            Console.WriteLine("[" + (condition ? "PASS" : "FAIL") + "] " + label);
            if (!condition)
                failures++;
        }

        private abstract class FixtureBase : IDisposable
        {
            protected FixtureBase(string prefix)
            {
                DatabasePath = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(DatabasePath, ServerPaths.SchemaFilePath);
                SeedCharacter(Database);
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; private set; }

            internal abstract void DropFailureTriggers();

            internal bool HasNoDirtyState()
                => Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.DirtyMainVirtualCountSlots.Count == 0;

            protected void Persist(InventoryService inventory)
            {
                var seedLease = new InventoryLease(Guid.NewGuid(), CharacterId, inventory, 1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                    throw new InvalidOperationException("unable to persist transaction fixture");
                Lease = InventoryContext.Register(Guid.NewGuid(), CharacterId, inventory);
            }

            protected InventoryService Load()
            {
                using var connection = Database.OpenConnection();
                return InventoryService.LoadFromDb(connection, CharacterId, AccountId, Database);
            }

            protected void Execute(string sql)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
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
                if (Lease != null)
                    InventoryContext.Unregister(Lease.SessionId, Lease.CharacterId);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    try { if (File.Exists(DatabasePath + suffix)) File.Delete(DatabasePath + suffix); } catch { }
            }
        }

        private sealed class RecipeFixture : FixtureBase
        {
            private readonly CompoundItemRecipeDefinition _recipe;
            private readonly Dictionary<short, byte[]> _initialItems = new Dictionary<short, byte[]>();
            private readonly Dictionary<short, int> _initialVirtualCounts = new Dictionary<short, int>();
            private readonly CompoundItemRecipeRequest _request = new CompoundItemRecipeRequest
            {
                SourceValue = RecipeItemId,
                SourceIsItemId = true,
                RequestedCount = 1,
            };

            internal RecipeFixture() : base("compound-recipe-transaction")
            {
                if (!InventoryCompoundItemRecipeService.TryParseCompoundRecipe(RecipeItemId, out _recipe)
                    || _recipe == null
                    || _recipe.Materials.Count == 0
                    || _recipe.Outputs.Count == 0)
                {
                    throw new InvalidOperationException("current PVF recipe fixture is unavailable");
                }

                var hasNormalOutput = _recipe.Outputs.Any(output =>
                    !InventoryService.TryResolveMainVirtualSlotByItemId(output.ItemTemplateId, out _, out _));
                if (!hasNormalOutput)
                    throw new InvalidOperationException("recipe fixture requires a normal inventory output");

                var inventory = new InventoryService(CharacterId, AccountId, Database);
                inventory.SetListParam16(InventoryListType.Main, ItemSlotBoundService.MainExpandStageFull);
                inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 500_000_000);
                _initialVirtualCounts[InventoryService.MainVirtualCurrencySlotStart] = 500_000_000;
                short slot = 30;
                foreach (var material in _recipe.Materials)
                {
                    var count = material.Count + 5;
                    if (InventoryService.TryResolveMainVirtualSlotByItemId(material.ItemTemplateId, out var virtualSlot, out _))
                    {
                        inventory.SetMainVirtualCount(virtualSlot, count);
                        _initialVirtualCounts[virtualSlot] = count;
                        continue;
                    }

                    ItemMetadataResolver.TryResolveItemKind(material.ItemTemplateId, out var kind);
                    if (kind == ItemCore.KindUnknown)
                        kind = ItemCore.KindConsumable;
                    var item = ItemCore.Create(kind, material.ItemTemplateId);
                    item.Count = count;
                    inventory.SetItem(InventoryListType.Main, slot, item);
                    _initialItems[slot] = item.ToBytes();
                    slot++;
                }

                Persist(inventory);
            }

            internal bool TryCommit(out CompoundItemRecipeResult result, out bool persistenceFailed)
                => InventoryRecipeSeparateUpgradeCommitService.TryCommitCompoundItem(
                    Lease, _request, out result, out persistenceFailed);

            internal bool HasInitialState()
                => HasInitialState(Lease.Inventory) && HasInitialState(Load());

            internal bool HasCommittedState(CompoundItemRecipeResult result)
            {
                var online = Lease.Inventory;
                var persisted = Load();
                return result.Rewards.Count > 0
                    && result.Rewards.All(reward =>
                        online.CountMainItem(reward.ItemTemplateId) >= reward.GrantedCount
                        && persisted.CountMainItem(reward.ItemTemplateId) >= reward.GrantedCount)
                    && result.DeletedEntries.All(entry =>
                        online.CountMainItem(entry.ItemTemplateId) < InitialCount(entry.ItemTemplateId)
                        && persisted.CountMainItem(entry.ItemTemplateId) == online.CountMainItem(entry.ItemTemplateId));
            }

            internal void CreateRewardInsertFailureTrigger()
                => Execute($"CREATE TRIGGER fail_recipe_reward_insert BEFORE INSERT ON character_inventory_items WHEN NEW.character_id={CharacterId} AND NEW.list_type=0 BEGIN SELECT RAISE(ABORT, 'injected recipe reward failure'); END;");

            internal override void DropFailureTriggers()
            {
                if (File.Exists(DatabasePath))
                    Execute("DROP TRIGGER IF EXISTS fail_recipe_reward_insert;");
            }

            private bool HasInitialState(InventoryService inventory)
            {
                foreach (var pair in _initialItems)
                {
                    if (inventory.GetItem(InventoryListType.Main, pair.Key)?.ToBytes().SequenceEqual(pair.Value) != true)
                        return false;
                }

                foreach (var pair in _initialVirtualCounts)
                {
                    if (inventory.GetMainVirtualCount(pair.Key)?.Count != pair.Value)
                        return false;
                }

                return _recipe.Outputs.All(output => inventory.CountMainItem(output.ItemTemplateId) == 0);
            }

            private int InitialCount(int itemTemplateId)
            {
                foreach (var pair in _initialItems)
                {
                    var item = ItemCore.FromBytes(pair.Value);
                    if (item.ItemId == itemTemplateId)
                        return item.Count;
                }

                if (InventoryService.TryResolveMainVirtualSlotByItemId(itemTemplateId, out var slot, out _)
                    && _initialVirtualCounts.TryGetValue(slot, out var count))
                    return count;
                return 0;
            }
        }

        private sealed class SeparateUpgradeFixture : FixtureBase
        {
            private readonly SeparateUpgradeCommand _command;
            private readonly SeparateUpgradeTable _table;
            private readonly ItemMetadata _metadata;
            private readonly byte[] _initialTargetBytes;
            private readonly byte[] _initialMaterialBytes;

            internal SeparateUpgradeFixture() : base("separate-upgrade-transaction")
            {
                _table = SeparateUpgradeTableProvider.Get();
                _metadata = ItemMetadataResolver.Resolve(SeparateTargetItemId);
                _command = new SeparateUpgradeCommand
                {
                    TargetListType = InventoryListType.Main,
                    TargetSlotIndex = SeparateTargetSlot,
                    TargetItemTemplateId = SeparateTargetItemId,
                    MaterialSlotIndex = SeparateMaterialSlot,
                };

                var inventory = new InventoryService(CharacterId, AccountId, Database);
                inventory.SetListParam16(InventoryListType.Main, ItemSlotBoundService.MainExpandStageFull);
                var target = new ItemCore
                {
                    ItemKind = ItemCore.KindEquipment,
                    ItemId = SeparateTargetItemId,
                    Uid = 986202,
                    Durability = _metadata.Durability,
                };
                var material = new ItemCore
                {
                    ItemKind = ItemCore.KindMaterial,
                    ItemId = SeparateMaterialItemId,
                    Count = 1000,
                };
                inventory.SetItem(InventoryListType.Main, SeparateTargetSlot, target);
                inventory.SetItem(InventoryListType.Main, SeparateMaterialSlot, material);
                Persist(inventory);
                _initialTargetBytes = target.ToBytes();
                _initialMaterialBytes = material.ToBytes();
            }

            internal bool TryCommit(out SeparateUpgradeResult result, out bool persistenceFailed)
                => InventoryRecipeSeparateUpgradeCommitService.TryCommitSeparateUpgrade(
                    Lease, _command, _table, _metadata, out result, out persistenceFailed);

            internal bool HasInitialState()
                => HasInitialState(Lease.Inventory) && HasInitialState(Load());

            internal bool HasCommittedState(SeparateUpgradeResult result)
            {
                var online = Lease.Inventory;
                var persisted = Load();
                var onlineTarget = online.GetItem(InventoryListType.Main, SeparateTargetSlot);
                var persistedTarget = persisted.GetItem(InventoryListType.Main, SeparateTargetSlot);
                return onlineTarget != null
                    && persistedTarget != null
                    && onlineTarget.ToBytes().SequenceEqual(persistedTarget.ToBytes())
                    && online.GetItem(InventoryListType.Main, SeparateMaterialSlot)?.Count == result.MaterialRemainingCount
                    && persisted.GetItem(InventoryListType.Main, SeparateMaterialSlot)?.Count == result.MaterialRemainingCount
                    && result.MaterialRemainingCount < 1000;
            }

            internal void CreateMaterialWriteFailureTrigger()
                => Execute($"CREATE TRIGGER fail_separate_material_update BEFORE UPDATE OF item_core ON character_inventory_items WHEN OLD.character_id={CharacterId} AND OLD.list_type=0 AND OLD.slot_index={SeparateMaterialSlot} BEGIN SELECT RAISE(ABORT, 'injected separate material failure'); END;");

            internal override void DropFailureTriggers()
            {
                if (File.Exists(DatabasePath))
                    Execute("DROP TRIGGER IF EXISTS fail_separate_material_update;");
            }

            private bool HasInitialState(InventoryService inventory)
                => inventory.GetItem(InventoryListType.Main, SeparateTargetSlot)?.ToBytes().SequenceEqual(_initialTargetBytes) == true
                    && inventory.GetItem(InventoryListType.Main, SeparateMaterialSlot)?.ToBytes().SequenceEqual(_initialMaterialBytes) == true;
        }

        private static void SeedCharacter(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO accounts(account_id,m_id,password_hash) VALUES(@aid,'recipe-separate-transaction',''); INSERT INTO characters(character_id,account_id,name,level,town_id,area_id,direction,area_state) VALUES(@cid,@aid,@name,86,1,0,5,3);";
                command.Parameters.AddWithValue("@aid", AccountId);
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@name", Encoding.UTF8.GetBytes("RecipeSeparateTransaction"));
                command.ExecuteNonQuery();
            });
        }
    }
}
