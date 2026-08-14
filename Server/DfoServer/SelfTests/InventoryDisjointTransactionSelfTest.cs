using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class InventoryDisjointTransactionSelfTest
    {
        private const int AccountId = 984900;
        private const int CharacterId = 984901;
        private const int SourceEquipmentId = 33000;
        private const int RewardItemId = 10088692;
        private const int RewardCount = 2;
        private const short SourceSlot = 37;
        private const short AvatarSlot = 0;
        private const int AvatarUid = 984902;

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
                using (var fixture = new Fixture())
                {
                    fixture.ResetNormal(existingRewardCount: 0);
                    fixture.CreateSourceDeleteFailureTrigger();
                    var sourceDeleteCommitted = InventoryDisjointCommitService.TryCommitItem(
                        fixture.Lease,
                        CreateItemRequest(),
                        ResolveItemMaterials,
                        out var failedSourceResult,
                        out var sourcePersistenceFailed);
                    Check(
                        "source DELETE failure rejects ordinary disjoint commit",
                        !sourceDeleteCommitted
                        && failedSourceResult?.ErrorCode == 0
                        && sourcePersistenceFailed,
                        ref failures);
                    Check(
                        "source DELETE failure restores source and reward state",
                        fixture.HasNormalState(sourcePresent: true, rewardCount: 0)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    var sourceRetryCommitted = InventoryDisjointCommitService.TryCommitItem(
                        fixture.Lease,
                        CreateItemRequest(),
                        ResolveItemMaterials,
                        out var sourceRetryResult,
                        out var sourceRetryPersistenceFailed);
                    Check(
                        "ordinary disjoint retries after source persistence recovery",
                        sourceRetryCommitted
                        && sourceRetryResult?.ErrorCode == 0
                        && !sourceRetryPersistenceFailed
                        && fixture.HasNormalState(sourcePresent: false, rewardCount: RewardCount)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    var duplicateCommitted = InventoryDisjointCommitService.TryCommitItem(
                        fixture.Lease,
                        CreateItemRequest(),
                        ResolveItemMaterials,
                        out _,
                        out var duplicatePersistenceFailed);
                    Check(
                        "ordinary disjoint recovery grants the reward only once",
                        !duplicateCommitted
                        && !duplicatePersistenceFailed
                        && fixture.HasNormalState(sourcePresent: false, rewardCount: RewardCount)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.ResetNormal(existingRewardCount: 5);
                    fixture.CreateRewardUpdateFailureTrigger();
                    var updateCommitted = InventoryDisjointCommitService.TryCommitItem(
                        fixture.Lease,
                        CreateItemRequest(),
                        ResolveItemMaterials,
                        out var failedUpdateResult,
                        out var updatePersistenceFailed);
                    Check(
                        "reward UPDATE failure rolls back source and material count",
                        !updateCommitted
                        && failedUpdateResult?.ErrorCode == 0
                        && updatePersistenceFailed
                        && fixture.HasNormalState(sourcePresent: true, rewardCount: 5)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    var updateRetryCommitted = InventoryDisjointCommitService.TryCommitItem(
                        fixture.Lease,
                        CreateItemRequest(),
                        ResolveItemMaterials,
                        out _,
                        out var updateRetryPersistenceFailed);
                    Check(
                        "reward UPDATE retries as one ordinary disjoint transaction",
                        updateRetryCommitted
                        && !updateRetryPersistenceFailed
                        && fixture.HasNormalState(sourcePresent: false, rewardCount: 7)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.ResetNormal(existingRewardCount: 0);
                    fixture.CreateRewardInsertFailureTrigger();
                    var insertCommitted = InventoryDisjointCommitService.TryCommitItem(
                        fixture.Lease,
                        CreateItemRequest(),
                        ResolveItemMaterials,
                        out var failedInsertResult,
                        out var insertPersistenceFailed);
                    Check(
                        "reward INSERT failure rolls back ordinary source deletion",
                        !insertCommitted
                        && failedInsertResult?.ErrorCode == 0
                        && insertPersistenceFailed
                        && fixture.HasNormalState(sourcePresent: true, rewardCount: 0)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    var insertRetryCommitted = InventoryDisjointCommitService.TryCommitItem(
                        fixture.Lease,
                        CreateItemRequest(),
                        ResolveItemMaterials,
                        out _,
                        out var insertRetryPersistenceFailed);
                    Check(
                        "reward INSERT retries as one ordinary disjoint transaction",
                        insertRetryCommitted
                        && !insertRetryPersistenceFailed
                        && fixture.HasNormalState(sourcePresent: false, rewardCount: RewardCount)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.ResetAvatar(existingRewardCount: 0);
                    fixture.CreateAvatarDetailDeleteFailureTrigger();
                    var avatarCommitted = InventoryDisjointCommitService.TryCommitAvatar(
                        fixture.Lease,
                        CreateAvatarRequest(fixture.AvatarItemId),
                        ResolveAvatarMaterials,
                        out var failedAvatarResult,
                        out var avatarPersistenceFailed);
                    Check(
                        "AvatarDetail DELETE failure rejects avatar disjoint commit",
                        !avatarCommitted
                        && failedAvatarResult?.ErrorCode == 0
                        && avatarPersistenceFailed,
                        ref failures);
                    Check(
                        "AvatarDetail DELETE failure restores avatar and reward state",
                        fixture.HasAvatarState(
                            sourcePresent: true,
                            detailPresent: true,
                            rewardCount: 0)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    var avatarRetryCommitted = InventoryDisjointCommitService.TryCommitAvatar(
                        fixture.Lease,
                        CreateAvatarRequest(fixture.AvatarItemId),
                        ResolveAvatarMaterials,
                        out _,
                        out var avatarRetryPersistenceFailed);
                    Check(
                        "avatar disjoint retries with source detail and reward committed once",
                        avatarRetryCommitted
                        && !avatarRetryPersistenceFailed
                        && fixture.HasAvatarState(
                            sourcePresent: false,
                            detailPresent: false,
                            rewardCount: RewardCount)
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    var duplicateAvatarCommitted = InventoryDisjointCommitService.TryCommitAvatar(
                        fixture.Lease,
                        CreateAvatarRequest(fixture.AvatarItemId),
                        ResolveAvatarMaterials,
                        out _,
                        out var duplicateAvatarPersistenceFailed);
                    Check(
                        "avatar disjoint recovery cannot duplicate the material reward",
                        !duplicateAvatarCommitted
                        && !duplicateAvatarPersistenceFailed
                        && fixture.HasAvatarState(
                            sourcePresent: false,
                            detailPresent: false,
                            rewardCount: RewardCount)
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] inventory disjoint transaction selftest threw: "
                    + ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "InventoryDisjointTransactionSelfTest OK"
                    : "InventoryDisjointTransactionSelfTest FAIL ("
                        + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static DisjointItemRequest CreateItemRequest()
        {
            return new DisjointItemRequest
            {
                ItemSpace = InventoryListType.Main,
                TargetSlotIndex = SourceSlot,
                DisjointItemSlotIndex = -1,
            };
        }

        private static AvatarDisjointRequest CreateAvatarRequest(int itemId)
        {
            return new AvatarDisjointRequest
            {
                SlotIndex = AvatarSlot,
                ExpectedItemTemplateId = itemId,
            };
        }

        private static bool ResolveItemMaterials(
            ItemCore source,
            ItemMetadata metadata,
            out List<DisjointMaterialResult> materials,
            out byte errorCode)
        {
            materials = CreateMaterials();
            errorCode = 0;
            return source != null && metadata != null;
        }

        private static bool ResolveAvatarMaterials(
            ItemCore source,
            ItemMetadata metadata,
            out List<DisjointMaterialResult> materials)
        {
            materials = CreateMaterials();
            return source != null && metadata != null;
        }

        private static List<DisjointMaterialResult> CreateMaterials()
        {
            return new List<DisjointMaterialResult>
            {
                new DisjointMaterialResult
                {
                    SlotIndex = -1,
                    ItemTemplateId = RewardItemId,
                    Count = RewardCount,
                },
            };
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
            internal Fixture()
            {
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "inventory-disjoint-transaction-"
                        + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(
                    DatabasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(Database);

                if (!ItemMetadataResolver.TryResolveItemKind(
                        RewardItemId,
                        out var rewardKind)
                    || !ItemSlotBoundService.TryGetSlotRange(
                        rewardKind,
                        ItemSlotBoundService.MainExpandStageFull,
                        out var rewardListType,
                        out var rewardRange)
                    || rewardListType != InventoryListType.Main)
                {
                    throw new InvalidOperationException(
                        "unable to resolve disjoint reward fixture");
                }

                RewardKind = rewardKind;
                RewardSlot = rewardRange.Start;
                AvatarItemId = ResolveAvatarItemId();

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    Database);
                inventory.SetListParam16(
                    InventoryListType.Main,
                    ItemSlotBoundService.MainExpandStageFull);
                var seedLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                {
                    throw new InvalidOperationException(
                        "unable to persist disjoint fixture list state");
                }

                Lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }
            internal byte RewardKind { get; }
            internal short RewardSlot { get; }
            internal int AvatarItemId { get; }

            internal void ResetNormal(int existingRewardCount)
            {
                DropFailureTriggers();
                var inventory = Lease.Inventory;
                var source = ItemCore.Create(
                    ItemCore.KindEquipment,
                    SourceEquipmentId);
                source.Uid = 984910 + existingRewardCount;
                if (!inventory.SetItem(
                        InventoryListType.Main,
                        SourceSlot,
                        source))
                {
                    throw new InvalidOperationException(
                        "unable to seed ordinary disjoint source");
                }

                SetRewardCount(inventory, existingRewardCount);
                PersistFixture();
            }

            internal void ResetAvatar(int existingRewardCount)
            {
                DropFailureTriggers();
                var inventory = Lease.Inventory;
                var source = ItemCore.Create(
                    ItemCore.KindAvatar,
                    AvatarItemId);
                source.AvatarUid = AvatarUid;
                if (!inventory.SetItem(
                        InventoryListType.Avatar,
                        AvatarSlot,
                        source))
                {
                    throw new InvalidOperationException(
                        "unable to seed avatar disjoint source");
                }

                inventory.AvatarDetails.Attach(new AvatarDetail
                {
                    AvatarUid = AvatarUid,
                    OwnerId = AccountId,
                    CharacterId = CharacterId,
                    ItemId = AvatarItemId,
                    JewelSocket = new byte[JewelSocket.Size],
                });
                inventory.AvatarDetails.MarkDirty(AvatarUid);
                SetRewardCount(inventory, existingRewardCount);
                PersistFixture();
            }

            internal bool HasNormalState(
                bool sourcePresent,
                int rewardCount)
            {
                return HasNormalState(
                        Lease.Inventory,
                        sourcePresent,
                        rewardCount)
                    && HasNormalState(
                        LoadPersistedInventory(),
                        sourcePresent,
                        rewardCount);
            }

            internal bool HasAvatarState(
                bool sourcePresent,
                bool detailPresent,
                int rewardCount)
            {
                return HasAvatarState(
                        Lease.Inventory,
                        sourcePresent,
                        detailPresent,
                        rewardCount)
                    && HasAvatarState(
                        LoadPersistedInventory(),
                        sourcePresent,
                        detailPresent,
                        rewardCount);
            }

            internal bool HasNoDirtyState()
            {
                return Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.AvatarDetails.DirtyDetailUids.Count == 0
                    && Lease.Inventory.AvatarDetails.DeletedDetailUids.Count == 0;
            }

            internal void CreateSourceDeleteFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_disjoint_source_delete
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {SourceSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected disjoint source delete failure');
END;");
            }

            internal void CreateRewardInsertFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_disjoint_reward_insert
BEFORE INSERT ON character_inventory_items
WHEN NEW.character_id = {CharacterId}
 AND NEW.list_type = {(int)InventoryListType.Main}
 AND NEW.slot_index = {RewardSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected disjoint reward insert failure');
END;");
            }

            internal void CreateRewardUpdateFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_disjoint_reward_update
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {CharacterId}
 AND OLD.list_type = {(int)InventoryListType.Main}
 AND OLD.slot_index = {RewardSlot}
BEGIN
    SELECT RAISE(ABORT, 'injected disjoint reward update failure');
END;");
            }

            internal void CreateAvatarDetailDeleteFailureTrigger()
            {
                ExecuteNonQuery($@"
CREATE TRIGGER fail_disjoint_avatar_detail_delete
BEFORE DELETE ON character_avatar_detail
WHEN OLD.item_uid = {AvatarUid}
BEGIN
    SELECT RAISE(ABORT, 'injected avatar detail delete failure');
END;");
            }

            internal void DropFailureTriggers()
            {
                if (!File.Exists(DatabasePath))
                    return;

                ExecuteNonQuery(@"
DROP TRIGGER IF EXISTS fail_disjoint_source_delete;
DROP TRIGGER IF EXISTS fail_disjoint_reward_insert;
DROP TRIGGER IF EXISTS fail_disjoint_reward_update;
DROP TRIGGER IF EXISTS fail_disjoint_avatar_detail_delete;");
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

            private void SetRewardCount(
                InventoryService inventory,
                int count)
            {
                if (count <= 0)
                {
                    if (inventory.GetItem(
                            InventoryListType.Main,
                            RewardSlot) != null)
                    {
                        inventory.RemoveItem(
                            InventoryListType.Main,
                            RewardSlot);
                    }
                    return;
                }

                var reward = InventoryCreateService.CreateCore(
                    RewardKind,
                    RewardItemId,
                    ItemCreateReason.Unknown,
                    count);
                if (!InventoryStackRuleService.IsStackable(reward)
                    || !inventory.SetItem(
                        InventoryListType.Main,
                        RewardSlot,
                        reward))
                {
                    throw new InvalidOperationException(
                        "unable to seed disjoint reward stack");
                }
            }

            private void PersistFixture()
            {
                if (!InventoryPersistenceService.SaveDirty(Lease))
                {
                    throw new InvalidOperationException(
                        "unable to persist disjoint fixture");
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

            private bool HasNormalState(
                InventoryService inventory,
                bool sourcePresent,
                int rewardCount)
            {
                var source = inventory.GetItem(
                    InventoryListType.Main,
                    SourceSlot);
                return (source?.ItemId == SourceEquipmentId) == sourcePresent
                    && GetRewardCount(inventory) == rewardCount;
            }

            private bool HasAvatarState(
                InventoryService inventory,
                bool sourcePresent,
                bool detailPresent,
                int rewardCount)
            {
                var source = inventory.GetItem(
                    InventoryListType.Avatar,
                    AvatarSlot);
                return (source?.ItemId == AvatarItemId) == sourcePresent
                    && (inventory.AvatarDetails.GetDetail(AvatarUid) != null)
                        == detailPresent
                    && GetRewardCount(inventory) == rewardCount;
            }

            private int GetRewardCount(InventoryService inventory)
            {
                var reward = inventory.GetItem(
                    InventoryListType.Main,
                    RewardSlot);
                return reward?.ItemId == RewardItemId
                    ? Math.Max(0, reward.Count)
                    : 0;
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

        private static int ResolveAvatarItemId()
        {
            foreach (var itemId in AvatarCandidates)
            {
                var metadata = ItemMetadataResolver.Resolve(itemId);
                if (metadata == null
                    || !string.Equals(
                        metadata.ItemKind,
                        "equipment",
                        StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(metadata.EquipmentType)
                    || metadata.EquipmentType.IndexOf(
                        "avatar",
                        StringComparison.OrdinalIgnoreCase) < 0
                    || ContainsImpossibleDisjoint(metadata.ImpossibleContents))
                {
                    continue;
                }

                return itemId;
            }

            throw new InvalidOperationException(
                "unable to resolve avatar disjoint fixture from current PVF");
        }

        private static bool ContainsImpossibleDisjoint(
            IReadOnlyList<string> impossibleContents)
        {
            if (impossibleContents == null)
                return false;

            foreach (var value in impossibleContents)
            {
                var normalized = (value ?? string.Empty)
                    .Trim()
                    .Trim('`')
                    .Trim()
                    .Trim('[', ']')
                    .Trim();
                if (string.Equals(
                    normalized,
                    "disjoint",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
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
VALUES(@aid, 'inventory-disjoint-transaction', '');
INSERT INTO characters(
    character_id, account_id, name, level,
    town_id, area_id, direction, area_state)
VALUES(@cid, @aid, @name, 86, 1, 0, 5, 3);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        Encoding.UTF8.GetBytes(
                            "InventoryDisjointTransaction"));
                    command.ExecuteNonQuery();
                }
            });
        }
    }
}
