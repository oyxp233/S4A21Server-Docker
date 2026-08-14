using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class AvatarCompoundTransactionSelfTest
    {
        private const int AccountId = 985000;
        private const int CharacterId = 985001;
        private const int CompoundItemId = 21;
        private const short ConsumeSlot = 7;
        private const short RegularSlot1 = 10;
        private const short RegularSlot2 = 11;
        private const short SetSlotStart = 20;
        private const int FirstAvatarUid = 985100;
        private const ushort AbilityNo = 2;

        private static readonly int[] AvatarCandidates =
        {
            401500167,
            40819,
            41540,
            108550662,
            108560645,
            108570739,
            108520635,
            101520586,
            101520585,
            40303,
        };

        public static int Run()
        {
            var failures = 0;
            try
            {
                using (var fixture = new Fixture(setMode: false))
                {
                    fixture.CreateOldDetailDeleteFailureTrigger();
                    var committed = TryCommitRegular(
                        fixture,
                        out var failedResult,
                        out var persistenceFailed);
                    Check(
                        "old AvatarDetail DELETE failure rejects regular compound",
                        !committed
                        && failedResult?.Success == true
                        && persistenceFailed,
                        ref failures);
                    Check(
                        "old AvatarDetail DELETE failure restores all regular inputs",
                        fixture.HasRegularBeforeState()
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    committed = TryCommitRegular(
                        fixture,
                        out _,
                        out persistenceFailed);
                    Check(
                        "regular compound retries after old detail recovery",
                        committed
                        && !persistenceFailed
                        && fixture.HasRegularAfterState()
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    committed = TryCommitRegular(
                        fixture,
                        out _,
                        out persistenceFailed);
                    Check(
                        "regular compound recovery creates the result only once",
                        !committed
                        && !persistenceFailed
                        && fixture.HasRegularAfterState()
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }

                using (var fixture = new Fixture(setMode: false))
                {
                    fixture.CreateNewDetailInsertFailureTrigger();
                    var committed = TryCommitRegular(
                        fixture,
                        out var failedResult,
                        out var persistenceFailed);
                    Check(
                        "new AvatarDetail INSERT failure rejects regular compound",
                        !committed
                        && failedResult?.Success == true
                        && persistenceFailed,
                        ref failures);
                    Check(
                        "new AvatarDetail INSERT failure restores sources and consumable",
                        fixture.HasRegularBeforeState()
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    committed = TryCommitRegular(
                        fixture,
                        out _,
                        out persistenceFailed);
                    Check(
                        "regular compound retries after new detail recovery",
                        committed
                        && !persistenceFailed
                        && fixture.HasRegularAfterState()
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }

                using (var fixture = new Fixture(setMode: false))
                {
                    fixture.CreateConsumableUpdateFailureTrigger();
                    var committed = TryCommitRegular(
                        fixture,
                        out var failedResult,
                        out var persistenceFailed);
                    Check(
                        "compound consumable UPDATE failure rolls back regular avatars",
                        !committed
                        && failedResult?.Success == true
                        && persistenceFailed
                        && fixture.HasRegularBeforeState()
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    committed = TryCommitRegular(
                        fixture,
                        out _,
                        out persistenceFailed);
                    Check(
                        "regular compound retries with consumable deducted once",
                        committed
                        && !persistenceFailed
                        && fixture.HasRegularAfterState()
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }

                using (var fixture = new Fixture(setMode: true))
                {
                    fixture.CreateSetSourceDeleteFailureTrigger();
                    var committed = TryCommitSet(
                        fixture,
                        out var failedResult,
                        out var persistenceFailed);
                    Check(
                        "set source DELETE failure rejects the eight-avatar compound",
                        !committed
                        && failedResult?.Success == true
                        && persistenceFailed,
                        ref failures);
                    Check(
                        "set source DELETE failure restores all avatars and material",
                        fixture.HasSetBeforeState()
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    committed = TryCommitSet(
                        fixture,
                        out _,
                        out persistenceFailed);
                    Check(
                        "set compound retries as one transaction after recovery",
                        committed
                        && !persistenceFailed
                        && fixture.HasSetAfterState()
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    committed = TryCommitSet(
                        fixture,
                        out _,
                        out persistenceFailed);
                    Check(
                        "set compound recovery cannot duplicate the result avatar",
                        !committed
                        && !persistenceFailed
                        && fixture.HasSetAfterState()
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] avatar compound transaction selftest threw: "
                    + ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "AvatarCompoundTransactionSelfTest OK"
                    : "AvatarCompoundTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static bool TryCommitRegular(
            Fixture fixture,
            out InventoryAvatarCompoundResult result,
            out bool persistenceFailed)
        {
            return InventoryAvatarCompoundCommitService.TryCommit(
                fixture.Lease,
                new InventoryAvatarCompoundRequest
                {
                    ConsumeSlot = ConsumeSlot,
                    Slot1 = RegularSlot1,
                    Slot2 = RegularSlot2,
                    RequestedItemId = fixture.NewAvatarItemId,
                    AbilityNo = AbilityNo,
                },
                (old1, old2, materialId) =>
                    new[] { fixture.NewAvatarItemId },
                out result,
                out persistenceFailed);
        }

        private static bool TryCommitSet(
            Fixture fixture,
            out InventoryAvatarCompoundResult result,
            out bool persistenceFailed)
        {
            return InventoryAvatarCompoundCommitService.TryCommitSet(
                fixture.Lease,
                new InventoryAvatarCompoundSetRequest
                {
                    ConsumeSlot = ConsumeSlot,
                    ConsumeSlots = fixture.SetSlots,
                    ExpectedItemIds = fixture.SetExpectedItemIds,
                    RequestedItemId = fixture.NewAvatarItemId,
                    AbilityNo = AbilityNo,
                },
                materialId => fixture.NewAvatarItemId,
                out result,
                out persistenceFailed);
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

        private sealed class Fixture : IDisposable
        {
            private readonly bool _setMode;
            private readonly int[] _oldAvatarUids;

            internal Fixture(bool setMode)
            {
                _setMode = setMode;
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "avatar-compound-transaction-"
                        + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(
                    DatabasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(Database);

                var avatarIds = ResolveAvatarItemIds(3);
                OldAvatarItemId1 = avatarIds[0];
                OldAvatarItemId2 = avatarIds[1];
                NewAvatarItemId = avatarIds[2];

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    Database);
                inventory.SetListParam16(
                    InventoryListType.Main,
                    ItemSlotBoundService.MainExpandStageFull);

                if (setMode)
                {
                    SetSlots = Enumerable.Range(0, 8)
                        .Select(index => (short)(SetSlotStart + index))
                        .ToArray();
                    SetExpectedItemIds = new int[SetSlots.Length];
                    _oldAvatarUids = new int[SetSlots.Length];
                    for (var index = 0; index < SetSlots.Length; index++)
                    {
                        var itemId = index % 2 == 0
                            ? OldAvatarItemId1
                            : OldAvatarItemId2;
                        var avatarUid = FirstAvatarUid + index;
                        SeedAvatar(
                            inventory,
                            SetSlots[index],
                            itemId,
                            avatarUid);
                        SetExpectedItemIds[index] = itemId;
                        _oldAvatarUids[index] = avatarUid;
                    }

                    SeedConsumable(inventory, 1);
                }
                else
                {
                    SetSlots = Array.Empty<short>();
                    SetExpectedItemIds = Array.Empty<int>();
                    _oldAvatarUids = new[]
                    {
                        FirstAvatarUid,
                        FirstAvatarUid + 1,
                    };
                    SeedAvatar(
                        inventory,
                        RegularSlot1,
                        OldAvatarItemId1,
                        _oldAvatarUids[0]);
                    SeedAvatar(
                        inventory,
                        RegularSlot2,
                        OldAvatarItemId2,
                        _oldAvatarUids[1]);
                    SeedConsumable(inventory, 2);
                }

                var seedLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                {
                    throw new InvalidOperationException(
                        "unable to persist avatar compound fixture");
                }

                Lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }
            internal int OldAvatarItemId1 { get; }
            internal int OldAvatarItemId2 { get; }
            internal int NewAvatarItemId { get; }
            internal short[] SetSlots { get; }
            internal int[] SetExpectedItemIds { get; }

            internal bool HasRegularBeforeState()
            {
                return HasRegularBeforeState(Lease.Inventory)
                    && HasRegularBeforeState(LoadPersistedInventory());
            }

            internal bool HasRegularAfterState()
            {
                return HasRegularAfterState(Lease.Inventory)
                    && HasRegularAfterState(LoadPersistedInventory());
            }

            internal bool HasSetBeforeState()
            {
                return HasSetBeforeState(Lease.Inventory)
                    && HasSetBeforeState(LoadPersistedInventory());
            }

            internal bool HasSetAfterState()
            {
                return HasSetAfterState(Lease.Inventory)
                    && HasSetAfterState(LoadPersistedInventory());
            }

            internal bool HasNoDirtyState()
            {
                return Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.AvatarDetails.DirtyDetailUids.Count == 0
                    && Lease.Inventory.AvatarDetails.DeletedDetailUids.Count == 0;
            }

            internal void CreateOldDetailDeleteFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_compound_old_detail_delete
BEFORE DELETE ON character_avatar_detail
WHEN OLD.item_uid = {_oldAvatarUids[0]}
BEGIN
    SELECT RAISE(ABORT, 'injected compound old detail delete failure');
END;");
            }

            internal void CreateNewDetailInsertFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_compound_new_detail_insert
BEFORE INSERT ON character_avatar_detail
WHEN NEW.item_id = {NewAvatarItemId}
BEGIN
    SELECT RAISE(ABORT, 'injected compound new detail insert failure');
END;");
            }

            internal void CreateConsumableUpdateFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_compound_consumable_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {ConsumeSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected compound consumable update failure');
END;");
            }

            internal void CreateSetSourceDeleteFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_compound_set_source_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Avatar}
 AND OLD.slot_index = {SetSlotStart + 4}
BEGIN
    SELECT RAISE(ABORT, 'injected compound set source delete failure');
END;");
            }

            internal void DropFailureTriggers()
            {
                if (!File.Exists(DatabasePath))
                    return;

                ExecuteNonQuery(@"
DROP TRIGGER IF EXISTS fail_compound_old_detail_delete;
DROP TRIGGER IF EXISTS fail_compound_new_detail_insert;
DROP TRIGGER IF EXISTS fail_compound_consumable_update;
DROP TRIGGER IF EXISTS fail_compound_set_source_delete;");
            }

            public void Dispose()
            {
                try
                {
                    DropFailureTriggers();
                }
                catch
                {
                }

                InventoryContext.Unregister(
                    Lease.SessionId,
                    Lease.CharacterId);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    try
                    {
                        var path = DatabasePath + suffix;
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch
                    {
                    }
                }
            }

            private static void SeedAvatar(
                InventoryService inventory,
                short slotIndex,
                int itemId,
                int avatarUid)
            {
                var core = ItemCore.Create(ItemCore.KindAvatar, itemId);
                core.AvatarUid = avatarUid;
                if (!inventory.SetItem(
                        InventoryListType.Avatar,
                        slotIndex,
                        core))
                {
                    throw new InvalidOperationException(
                        "unable to seed avatar compound source");
                }

                inventory.AvatarDetails.Attach(new AvatarDetail
                {
                    AvatarUid = avatarUid,
                    OwnerId = AccountId,
                    CharacterId = CharacterId,
                    ItemId = itemId,
                    JewelSocket = new byte[JewelSocket.Size],
                });
                inventory.AvatarDetails.MarkDirty(avatarUid);
            }

            private static void SeedConsumable(
                InventoryService inventory,
                int count)
            {
                var item = ItemCore.Create(
                    ItemCore.KindConsumable,
                    CompoundItemId);
                item.Count = count;
                if (!inventory.SetItem(
                        InventoryListType.Main,
                        ConsumeSlot,
                        item))
                {
                    throw new InvalidOperationException(
                        "unable to seed avatar compound consumable");
                }
            }

            private InventoryService LoadPersistedInventory()
            {
                using (var connection = Database.OpenConnection())
                {
                    return InventoryService.LoadFromDb(
                        connection,
                        CharacterId,
                        AccountId,
                        Database);
                }
            }

            private bool HasRegularBeforeState(InventoryService inventory)
            {
                return inventory.GetItem(
                        InventoryListType.Avatar,
                        RegularSlot1)?.ItemId == OldAvatarItemId1
                    && inventory.GetItem(
                        InventoryListType.Avatar,
                        RegularSlot2)?.ItemId == OldAvatarItemId2
                    && GetConsumableCount(inventory) == 2
                    && HasAllOldDetails(inventory);
            }

            private bool HasRegularAfterState(InventoryService inventory)
            {
                if (!TryFindSingleNewAvatar(inventory, out var newCore)
                    || inventory.GetItem(
                        InventoryListType.Avatar,
                        RegularSlot1) != null
                    || inventory.GetItem(
                        InventoryListType.Avatar,
                        RegularSlot2) != null)
                {
                    return false;
                }

                return newCore.AvatarUid > 0
                    && GetConsumableCount(inventory) == 1
                    && !HasAnyOldDetail(inventory)
                    && inventory.AvatarDetails.GetDetail(
                        newCore.AvatarUid)?.ItemId == NewAvatarItemId;
            }

            private bool HasSetBeforeState(InventoryService inventory)
            {
                for (var index = 0; index < SetSlots.Length; index++)
                {
                    if (inventory.GetItem(
                            InventoryListType.Avatar,
                            SetSlots[index])?.ItemId
                        != SetExpectedItemIds[index])
                    {
                        return false;
                    }
                }

                return GetConsumableCount(inventory) == 1
                    && HasAllOldDetails(inventory);
            }

            private bool HasSetAfterState(InventoryService inventory)
            {
                if (!TryFindSingleNewAvatar(inventory, out var newCore)
                    || newCore.AvatarUid <= 0
                    || GetConsumableCount(inventory) != 0
                    || HasAnyOldDetail(inventory)
                    || inventory.AvatarDetails.GetDetail(
                        newCore.AvatarUid)?.ItemId != NewAvatarItemId)
                {
                    return false;
                }

                for (var index = 0; index < SetSlots.Length; index++)
                {
                    if (inventory.GetItem(
                            InventoryListType.Avatar,
                            SetSlots[index]) != null)
                    {
                        return false;
                    }
                }

                return true;
            }

            private bool TryFindSingleNewAvatar(
                InventoryService inventory,
                out ItemCore newCore)
            {
                newCore = null;
                var count = 0;
                for (short slot = 0; slot <= 209; slot++)
                {
                    var item = inventory.GetItem(
                        InventoryListType.Avatar,
                        slot);
                    if (item?.ItemId != NewAvatarItemId)
                        continue;

                    count++;
                    newCore = item;
                }

                return count == 1 && newCore != null;
            }

            private bool HasAllOldDetails(InventoryService inventory)
            {
                foreach (var avatarUid in _oldAvatarUids)
                {
                    if (inventory.AvatarDetails.GetDetail(avatarUid) == null)
                        return false;
                }

                return true;
            }

            private bool HasAnyOldDetail(InventoryService inventory)
            {
                foreach (var avatarUid in _oldAvatarUids)
                {
                    if (inventory.AvatarDetails.GetDetail(avatarUid) != null)
                        return true;
                }

                return false;
            }

            private static int GetConsumableCount(InventoryService inventory)
            {
                return inventory.GetItem(
                    InventoryListType.Main,
                    ConsumeSlot)?.Count ?? 0;
            }

            private void ExecuteNonQuery(string sql)
            {
                using (var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = DatabasePath,
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
        }

        private static int[] ResolveAvatarItemIds(int count)
        {
            var result = new List<int>();
            foreach (var itemId in AvatarCandidates)
            {
                if (!ItemMetadataResolver.TryResolveItemKind(
                        itemId,
                        out var itemKind)
                    || itemKind != ItemCore.KindAvatar)
                {
                    continue;
                }

                result.Add(itemId);
                if (result.Count >= count)
                    return result.ToArray();
            }

            throw new InvalidOperationException(
                "unable to resolve avatar compound fixtures from current PVF");
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
VALUES(@aid, 'avatar-compound-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes(
                            "AvatarCompoundTransaction"));
                    command.ExecuteNonQuery();
                }
            });
        }
    }
}
