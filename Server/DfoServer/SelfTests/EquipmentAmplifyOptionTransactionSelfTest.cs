using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
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
    internal static class EquipmentAmplifyOptionTransactionSelfTest
    {
        private const int AccountId = 985800;
        private const int CharacterId = 985801;
        private const int TargetItemId = 101010653;
        private const short TargetSlot = 10;
        private const short MaterialSlot = 65;
        private const int TargetUid = 985802;

        public static int Run()
        {
            var failures = 0;
            try
            {
                RunFault(MutationKind.Purify, failTarget: true, ref failures);
                RunFault(MutationKind.Clear, failTarget: false, ref failures);
                RunFault(MutationKind.Invest, failTarget: true, ref failures);
                RunFault(MutationKind.Twist, failTarget: false, ref failures);
                RunFault(MutationKind.PureGold, failTarget: true, ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] equipment amplify-option transaction selftest threw: " + ex);
                failures++;
            }

            Console.WriteLine(failures == 0
                ? "EquipmentAmplifyOptionTransactionSelfTest OK"
                : "EquipmentAmplifyOptionTransactionSelfTest FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void RunFault(MutationKind kind, bool failTarget, ref int failures)
        {
            using (var fixture = new Fixture(kind))
            {
                if (failTarget)
                    fixture.CreateTargetWriteFailureTrigger();
                else
                    fixture.CreateMaterialWriteFailureTrigger();

                var committed = fixture.TryCommit(out var failedResult, out var persistenceFailed);
                Check(
                    fixture.Label + " " + (failTarget ? "target" : "material") + " write failure rejects commit",
                    !committed && failedResult && persistenceFailed,
                    ref failures);
                Check(
                    fixture.Label + " write failure restores target and material",
                    fixture.HasInitialState() && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                committed = fixture.TryCommit(out var result, out persistenceFailed);
                Check(
                    fixture.Label + " retries after persistence recovery",
                    committed
                    && result
                    && !persistenceFailed
                    && fixture.HasCommittedState()
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

        private enum MutationKind
        {
            Purify,
            Clear,
            Invest,
            Twist,
            PureGold,
        }

        private sealed class Fixture : IDisposable
        {
            private readonly byte _initialAmplifyType;
            private readonly ushort _initialAmplifyValue;
            private readonly byte _initialUpgrade;
            private readonly int _initialMaterialCount;
            private PurifyItemResult _purifyResult;
            private InvestItemAmplifyOptionResult _investResult;

            internal Fixture(MutationKind kind)
            {
                Kind = kind;
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "equipment-amplify-option-transaction-" + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(DatabasePath, ServerPaths.SchemaFilePath);
                SeedCharacter(Database);

                var metadata = ItemMetadataResolver.Resolve(TargetItemId);
                if (metadata == null
                    || metadata.ItemKind != "equipment"
                    || metadata.MinimumLevel < ItemUpgradeTableProvider.GetAmplifyEquipLevelConst()
                    || metadata.Rarity < 2)
                {
                    throw new InvalidOperationException("amplify transaction target is not eligible in current PVF");
                }

                var config = AmplifyItemFile.Parse(PvfArchiveAccessor.ReadText("etc/amplifyitem.etc"));
                ResolveMaterial(config, kind, out var materialItemId, out var materialCount, out var optionType);
                MaterialItemId = materialItemId;
                _initialMaterialCount = materialCount + 1;

                var selectedType = (byte)0;
                var selectedOption = (byte)0;
                if (kind != MutationKind.Purify && kind != MutationKind.Clear)
                    selectedType = ResolveSelectedType(optionType, out selectedOption);
                SelectedOption = selectedOption;
                _initialUpgrade = 0;
                if (kind == MutationKind.Purify || kind == MutationKind.Clear)
                {
                    _initialAmplifyType = 0x80;
                    _initialAmplifyValue = 0;
                }
                else if (kind == MutationKind.Invest)
                {
                    _initialAmplifyType = 0;
                    _initialAmplifyValue = 0;
                }
                else
                {
                    _initialAmplifyType = selectedType == (byte)AmplifyAttributeType.Strength
                        ? (byte)AmplifyAttributeType.Intelligence
                        : (byte)AmplifyAttributeType.Strength;
                    _initialAmplifyValue = 1;
                }

                var inventory = new InventoryService(CharacterId, AccountId, Database);
                inventory.SetListParam16(InventoryListType.Main, ItemSlotBoundService.MainExpandStageFull);
                inventory.SetItem(InventoryListType.Main, TargetSlot, new ItemCore
                {
                    ItemKind = ItemCore.KindEquipment,
                    ItemId = TargetItemId,
                    Uid = TargetUid,
                    Durability = metadata.Durability,
                    AmplifyType = _initialAmplifyType,
                    AmplifyValue = _initialAmplifyValue,
                    Upgrade = _initialUpgrade,
                });
                inventory.SetItem(InventoryListType.Main, MaterialSlot, new ItemCore
                {
                    ItemKind = ItemCore.KindConsumable,
                    ItemId = MaterialItemId,
                    Count = _initialMaterialCount,
                });

                var seedLease = new InventoryLease(Guid.NewGuid(), CharacterId, inventory, 1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                    throw new InvalidOperationException("unable to persist amplify-option fixture");

                Lease = InventoryContext.Register(Guid.NewGuid(), CharacterId, inventory);
            }

            internal MutationKind Kind { get; }
            internal string Label => Kind.ToString().ToLowerInvariant();
            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }
            internal int MaterialItemId { get; }
            internal byte SelectedOption { get; }

            internal bool TryCommit(out bool hasResult, out bool persistenceFailed)
            {
                if (Kind == MutationKind.Purify || Kind == MutationKind.Clear)
                {
                    var committed = InventoryEquipmentAmplifyOptionCommitService.TryCommitPurify(
                        Lease,
                        new PurifyItemRequest
                        {
                            TargetSlotIndex = TargetSlot,
                            TargetItemTemplateId = TargetItemId,
                            MaterialSlotIndex = MaterialSlot,
                            MaterialItemTemplateId = MaterialItemId,
                        },
                        out _purifyResult,
                        out persistenceFailed);
                    hasResult = _purifyResult != null && _purifyResult.ErrorCode == 0;
                    return committed;
                }

                var action = Kind == MutationKind.Twist
                    ? InvestItemAmplifyOptionAction.Twist
                    : Kind == MutationKind.PureGold
                        ? InvestItemAmplifyOptionAction.PureGold
                        : InvestItemAmplifyOptionAction.Invest;
                var investCommitted = InventoryEquipmentAmplifyOptionCommitService.TryCommitInvest(
                    Lease,
                    new InvestItemAmplifyOptionRequest
                    {
                        Action = action,
                        TargetSlotIndex = TargetSlot,
                        TargetItemTemplateId = TargetItemId,
                        MaterialSlotIndex = MaterialSlot,
                        MaterialItemTemplateId = MaterialItemId,
                        SelectedOption = SelectedOption,
                    },
                    out _investResult,
                    out persistenceFailed);
                hasResult = _investResult != null && _investResult.ErrorCode == 0;
                return investCommitted;
            }

            internal bool HasInitialState()
                => HasState(Lease.Inventory, initial: true)
                    && HasState(Load(), initial: true);

            internal bool HasCommittedState()
                => HasState(Lease.Inventory, initial: false)
                    && HasState(Load(), initial: false);

            internal bool HasNoDirtyState()
                => Lease.Inventory.DirtyListTypes.Count == 0;

            internal void CreateTargetWriteFailureTrigger()
                => Execute($@"CREATE TRIGGER fail_amplify_option_target_update BEFORE UPDATE OF item_core ON character_inventory_items WHEN OLD.character_id={CharacterId} AND OLD.list_type=0 AND OLD.slot_index={TargetSlot} BEGIN SELECT RAISE(ABORT, 'injected amplify target failure'); END;");

            internal void CreateMaterialWriteFailureTrigger()
                => Execute($@"CREATE TRIGGER fail_amplify_option_material_update BEFORE UPDATE OF item_core ON character_inventory_items WHEN OLD.character_id={CharacterId} AND OLD.list_type=0 AND OLD.slot_index={MaterialSlot} BEGIN SELECT RAISE(ABORT, 'injected amplify material failure'); END;");

            internal void DropFailureTriggers()
            {
                if (File.Exists(DatabasePath))
                    Execute("DROP TRIGGER IF EXISTS fail_amplify_option_target_update; DROP TRIGGER IF EXISTS fail_amplify_option_material_update;");
            }

            private bool HasState(InventoryService inventory, bool initial)
            {
                var target = inventory.GetItem(InventoryListType.Main, TargetSlot);
                var material = inventory.GetItem(InventoryListType.Main, MaterialSlot);
                if (target == null || material == null || material.ItemId != MaterialItemId)
                    return false;

                if (initial)
                {
                    return target.AmplifyType == _initialAmplifyType
                        && target.AmplifyValue == _initialAmplifyValue
                        && target.Upgrade == _initialUpgrade
                        && material.Count == _initialMaterialCount;
                }

                var resultAmplifyType = _purifyResult != null
                    ? _purifyResult.AmplifyType
                    : _investResult?.AmplifyType ?? 0;
                var resultAmplifyValue = _purifyResult != null
                    ? _purifyResult.AmplifyValue
                    : _investResult?.AmplifyValue ?? 0;
                var resultUpgrade = _investResult?.AmplifyLevel ?? _initialUpgrade;
                var resultMaterialCount = _purifyResult != null
                    ? _purifyResult.MaterialRemainingCount
                    : _investResult?.MaterialRemainingCount ?? -1;
                return target.AmplifyType == resultAmplifyType
                    && target.AmplifyValue == resultAmplifyValue
                    && target.Upgrade == resultUpgrade
                    && material.Count == resultMaterialCount
                    && resultMaterialCount < _initialMaterialCount;
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

            private static void ResolveMaterial(
                AmplifyItemFile config,
                MutationKind kind,
                out int itemId,
                out int count,
                out AmplifyOptionType optionType)
            {
                itemId = 0;
                count = 0;
                optionType = AmplifyOptionType.None;
                if (kind == MutationKind.Purify)
                {
                    ResolveDictionaryMaterial(config.PurifyMaterials, out itemId, out count);
                    return;
                }

                if (kind == MutationKind.Clear)
                {
                    var values = config.PurifyOnlyMaterials.Count > 0
                        ? config.PurifyOnlyMaterials
                        : config.PurifyOnlyCeraMaterials;
                    ResolveDictionaryMaterial(values, out itemId, out count);
                    return;
                }

                IReadOnlyList<AmplifyMaterialOption> options = kind == MutationKind.Twist
                    ? config.ReinvestOptions
                    : kind == MutationKind.PureGold
                        ? config.RandomInvestUpgradeOptions
                        : config.InvestOptions;
                var option = options.FirstOrDefault(value => value != null && value.ItemId > 0 && value.Count > 0)
                    ?? throw new InvalidOperationException("current PVF has no material for " + kind);
                itemId = option.ItemId;
                count = option.Count;
                optionType = option.OptionType;
            }

            private static void ResolveDictionaryMaterial(
                IReadOnlyDictionary<int, int> values,
                out int itemId,
                out int count)
            {
                var entry = values.FirstOrDefault(value => value.Key > 0 && value.Value > 0);
                if (entry.Key <= 0 || entry.Value <= 0)
                    throw new InvalidOperationException("current PVF has no purification material");
                itemId = entry.Key;
                count = entry.Value;
            }

            private static byte ResolveSelectedType(AmplifyOptionType optionType, out byte selectedOption)
            {
                selectedOption = optionType == AmplifyOptionType.All ? (byte)3 : (byte)0;
                switch (optionType)
                {
                    case AmplifyOptionType.PhysicalAttack:
                        return (byte)AmplifyAttributeType.Strength;
                    case AmplifyOptionType.MagicalAttack:
                        return (byte)AmplifyAttributeType.Intelligence;
                    case AmplifyOptionType.PhysicalDefense:
                        return (byte)AmplifyAttributeType.Vitality;
                    case AmplifyOptionType.MagicalDefense:
                        return (byte)AmplifyAttributeType.Spirit;
                    case AmplifyOptionType.All:
                        return (byte)AmplifyAttributeType.Strength;
                    default:
                        throw new InvalidOperationException("current PVF amplify material has no supported option");
                }
            }
        }

        private static void SeedCharacter(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO accounts(account_id,m_id,password_hash) VALUES(@aid,'equipment-amplify-option-transaction',''); INSERT INTO characters(character_id,account_id,name,level,town_id,area_id,direction,area_state) VALUES(@cid,@aid,@name,86,1,0,5,3);";
                command.Parameters.AddWithValue("@aid", AccountId);
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@name", Encoding.UTF8.GetBytes("EquipmentAmplifyOptionTransaction"));
                command.ExecuteNonQuery();
            });
        }
    }
}
