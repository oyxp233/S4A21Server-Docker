using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class EquipmentAugmentationTransactionSelfTest
    {
        private const int AccountId = 985700;
        private const int CharacterId = 985701;
        private const int EquipmentItemId = 33000;
        private const int MaterialItemId = 1004;
        private const short TargetSlot = 10;
        private const short AvatarSlot = 0;
        private const short MaterialSlot = 65;
        private const int AvatarUid = 985702;

        private static readonly int[] AvatarCandidates =
        {
            401500167, 40819, 41540, 108550662, 108560645,
            108570739, 108520635, 101520586, 101520585, 40303,
        };

        public static int Run()
        {
            var failures = 0;
            try
            {
                using (var fixture = new Fixture(avatar: false))
                {
                    fixture.CreateEquipmentTargetFailureTrigger();
                    var committed = InventoryEquipmentAugmentationCommitService.TryCommitEquipmentSocket(
                        fixture.Lease, TargetSlot, EquipmentItemId, MaterialSlot,
                        out var failedResult, out var persistenceFailed);
                    Check("equipment socket target UPDATE failure rejects commit",
                        !committed && failedResult?.MaterialConsumed == true && persistenceFailed,
                        ref failures);
                    Check("equipment socket target UPDATE failure restores target and material",
                        fixture.HasEquipmentState(false, 2) && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    committed = InventoryEquipmentAugmentationCommitService.TryCommitEquipmentSocket(
                        fixture.Lease, TargetSlot, EquipmentItemId, MaterialSlot,
                        out _, out persistenceFailed);
                    Check("equipment socket retries after persistence recovery",
                        committed && !persistenceFailed
                        && fixture.HasEquipmentState(true, 1)
                        && fixture.HasNoDirtyState(), ref failures);
                }

                using (var fixture = new Fixture(avatar: true))
                {
                    fixture.CreateAvatarDetailFailureTrigger();
                    var committed = InventoryEquipmentAugmentationCommitService.TryCommitAvatarSocket(
                        fixture.Lease, AvatarSlot, fixture.AvatarItemId, MaterialSlot,
                        out var failedResult, out var persistenceFailed);
                    Check("avatar socket detail INSERT failure rejects commit",
                        !committed && failedResult?.MaterialConsumed == true && persistenceFailed,
                        ref failures);
                    Check("avatar socket detail INSERT failure restores detail and material",
                        fixture.HasAvatarState(false, 2) && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    committed = InventoryEquipmentAugmentationCommitService.TryCommitAvatarSocket(
                        fixture.Lease, AvatarSlot, fixture.AvatarItemId, MaterialSlot,
                        out _, out persistenceFailed);
                    Check("avatar socket retries with detail and material committed together",
                        committed && !persistenceFailed
                        && fixture.HasAvatarState(true, 1)
                        && fixture.HasNoDirtyState(), ref failures);

                    committed = InventoryEquipmentAugmentationCommitService.TryCommitAvatarSocket(
                        fixture.Lease, AvatarSlot, fixture.AvatarItemId, MaterialSlot,
                        out var idempotent, out persistenceFailed);
                    Check("opened avatar socket does not consume material twice",
                        committed && !persistenceFailed
                        && idempotent?.MaterialConsumed == false
                        && fixture.HasAvatarState(true, 1)
                        && fixture.HasNoDirtyState(), ref failures);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] equipment augmentation transaction selftest threw: " + ex);
                failures++;
            }

            Console.WriteLine(failures == 0
                ? "EquipmentAugmentationTransactionSelfTest OK"
                : "EquipmentAugmentationTransactionSelfTest FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string label, bool condition, ref int failures)
        {
            Console.WriteLine("[" + (condition ? "PASS" : "FAIL") + "] " + label);
            if (!condition) failures++;
        }

        private sealed class Fixture : IDisposable
        {
            internal Fixture(bool avatar)
            {
                DatabasePath = Path.Combine(Path.GetTempPath(),
                    "equipment-augmentation-transaction-" + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(DatabasePath, ServerPaths.SchemaFilePath);
                SeedCharacter(Database);
                var inventory = new InventoryService(CharacterId, AccountId, Database);
                inventory.SetListParam16(InventoryListType.Main, ItemSlotBoundService.MainExpandStageFull);
                var material = ItemCore.Create(ItemCore.KindConsumable, MaterialItemId);
                material.Count = 2;
                inventory.SetItem(InventoryListType.Main, MaterialSlot, material);

                if (avatar)
                {
                    AvatarItemId = ResolveAvatarItemId();
                    var core = ItemCore.Create(ItemCore.KindAvatar, AvatarItemId);
                    core.AvatarUid = AvatarUid;
                    inventory.SetItem(InventoryListType.Avatar, AvatarSlot, core);
                    inventory.AvatarDetails.Attach(new AvatarDetail
                    {
                        AvatarUid = AvatarUid,
                        OwnerId = AccountId,
                        CharacterId = CharacterId,
                        ItemId = AvatarItemId,
                        JewelSocket = new byte[JewelSocket.Size],
                    });
                    inventory.AvatarDetails.MarkDirty(AvatarUid);
                }
                else
                {
                    var target = ItemCore.Create(ItemCore.KindEquipment, EquipmentItemId);
                    target.Uid = 985703;
                    inventory.SetItem(InventoryListType.Main, TargetSlot, target);
                }

                var seedLease = new InventoryLease(Guid.NewGuid(), CharacterId, inventory, 1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                    throw new InvalidOperationException("unable to persist augmentation fixture");
                Lease = InventoryContext.Register(Guid.NewGuid(), CharacterId, inventory);
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }
            internal int AvatarItemId { get; }

            internal bool HasEquipmentState(bool opened, int materialCount)
                => HasEquipmentState(Lease.Inventory, opened, materialCount)
                    && HasEquipmentState(Load(), opened, materialCount);

            internal bool HasAvatarState(bool opened, int materialCount)
                => HasAvatarState(Lease.Inventory, opened, materialCount)
                    && HasAvatarState(Load(), opened, materialCount);

            internal bool HasNoDirtyState()
                => Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.AvatarDetails.DirtyDetailUids.Count == 0;

            internal void CreateEquipmentTargetFailureTrigger()
                => Execute($@"CREATE TRIGGER fail_augmentation_equipment_update BEFORE UPDATE OF item_core ON character_inventory_items WHEN OLD.character_id={CharacterId} AND OLD.list_type=0 AND OLD.slot_index={TargetSlot} BEGIN SELECT RAISE(ABORT, 'injected equipment socket failure'); END;");

            internal void CreateAvatarDetailFailureTrigger()
                => Execute($@"CREATE TRIGGER fail_augmentation_avatar_detail BEFORE INSERT ON character_avatar_detail WHEN NEW.item_uid={AvatarUid} BEGIN SELECT RAISE(ABORT, 'injected avatar socket failure'); END;");

            internal void DropFailureTriggers()
            {
                if (File.Exists(DatabasePath))
                    Execute("DROP TRIGGER IF EXISTS fail_augmentation_equipment_update; DROP TRIGGER IF EXISTS fail_augmentation_avatar_detail;");
            }

            private InventoryService Load()
            {
                using var connection = Database.OpenConnection();
                return InventoryService.LoadFromDb(connection, CharacterId, AccountId, Database);
            }

            private static bool HasEquipmentState(InventoryService inventory, bool opened, int materialCount)
                => (inventory.GetItem(InventoryListType.Main, TargetSlot)?.EmblemSocketCount > 0) == opened
                    && inventory.GetItem(InventoryListType.Main, MaterialSlot)?.Count == materialCount;

            private static bool HasAvatarState(InventoryService inventory, bool opened, int materialCount)
                => (inventory.AvatarDetails.GetDetail(AvatarUid)?.JewelSocketView.OpenCount > 0) == opened
                    && inventory.GetItem(InventoryListType.Main, MaterialSlot)?.Count == materialCount;

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

        private static int ResolveAvatarItemId()
        {
            foreach (var itemId in AvatarCandidates)
                if (ItemMetadataResolver.TryResolveItemKind(itemId, out var kind)
                    && kind == ItemCore.KindAvatar
                    && ItemMetadataResolver.ResolveAvatarOpenSocketTypes(itemId).Count > 0)
                    return itemId;
            throw new InvalidOperationException("unable to resolve socket-capable avatar fixture");
        }

        private static void SeedCharacter(IGameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO accounts(account_id,m_id,password_hash) VALUES(@aid,'equipment-augmentation-transaction',''); INSERT INTO characters(character_id,account_id,name,level,town_id,area_id,direction,area_state) VALUES(@cid,@aid,@name,86,1,0,5,3);";
                command.Parameters.AddWithValue("@aid", AccountId);
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@name", Encoding.UTF8.GetBytes("EquipmentAugmentationTransaction"));
                command.ExecuteNonQuery();
            });
        }
    }
}
