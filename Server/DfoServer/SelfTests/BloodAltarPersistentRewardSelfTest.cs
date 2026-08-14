using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.BloodAltar;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class BloodAltarPersistentRewardSelfTest
    {
        private const int RewardItemId = 1252;
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine(
                "=== BLOOD_ALTAR_PERSISTENT_REWARD selftest ===");

            var path = Path.Combine(
                Path.GetTempPath(),
                $"blood-altar-persistent-{Guid.NewGuid():N}.db");
            var leases = new List<InventoryLease>();
            IGameDatabase database = null;
            try
            {
                database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                CheckFullInventoryMailAndReplay(database, leases);
                CheckMailboxFailureRollback(database, leases);
                CheckOfflineRecovery(database, leases);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] unexpected exception: " + ex);
                _fail++;
            }
            finally
            {
                DropTrigger(database, "fail_blood_altar_mail_attachment");
                DropTrigger(database, "fail_blood_altar_outbox_commit");
                foreach (var lease in leases)
                {
                    InventoryContext.Unregister(
                        lease.SessionId,
                        lease.CharacterId);
                }
                SqliteConnection.ClearAllPools();
                DeleteDatabase(path);
            }

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void CheckFullInventoryMailAndReplay(
            IGameDatabase database,
            ICollection<InventoryLease> leases)
        {
            const int accountId = 955000;
            const int characterId = 955001;
            var lease = CreateFullLease(
                database,
                accountId,
                characterId,
                initialGold: 100);
            leases.Add(lease);
            var service = CreateService(database);
            var effectId = CreateEffectId(scopeTarget: 54001);
            var plan = CreatePlan(gold: 10);

            var committed = service.TryApplyBloodAltarReward(
                effectId,
                lease,
                lease.SessionId,
                plan,
                out var result,
                out _);
            Check(
                "full inventory commits gold and routes item to mailbox",
                committed
                && result?.RequestedGold == 10
                && result.GrantedGold == 10
                && result.FinalGold == 110
                && result.MailedRewardCount == 1
                && LoadGold(database, accountId, characterId) == 110
                && LoadItemCount(database, accountId, characterId) == 0
                && CountRewardAttachments(database, characterId) == 1
                && service.Outbox.Get(effectId)?.State
                    == DungeonPersistentEffectState.Committed
                && IsInventoryClean(lease.Inventory));

            var replayed = CreateService(database).TryApplyBloodAltarReward(
                effectId,
                lease,
                lease.SessionId,
                plan,
                out var replayResult,
                out _);
            Check(
                "committed blood altar replay does not duplicate gold or mail",
                replayed
                && replayResult?.MailedRewardCount == 1
                && LoadGold(database, accountId, characterId) == 110
                && CountRewardAttachments(database, characterId) == 1);

            var runtime = new BloodAltarParticipantSettlementRuntime(plan);
            var now = DateTime.UtcNow;
            var exitReady = runtime.TryMarkRankingShown(now)
                && runtime.TryMarkRewardShown(now)
                && runtime.TryBeginCommit();
            runtime.ProjectCommitted(result);
            exitReady = exitReady
                && runtime.TryMarkExitReadyProjectionSent(now)
                && runtime.Phase == BloodAltarSettlementPhase.ExitReady;
            Check(
                "mailed full-inventory reward can advance settlement to exit-ready",
                exitReady);
        }

        private static void CheckMailboxFailureRollback(
            IGameDatabase database,
            ICollection<InventoryLease> leases)
        {
            const int accountId = 955010;
            const int characterId = 955011;
            var lease = CreateFullLease(
                database,
                accountId,
                characterId,
                initialGold: 20);
            leases.Add(lease);
            var service = CreateService(database);
            var effectId = CreateEffectId(scopeTarget: 54002);
            var plan = CreatePlan(gold: 10);

            CreateAbortTrigger(
                database,
                "fail_blood_altar_mail_attachment",
                "BEFORE INSERT ON mailbox_attachments");
            var failed = service.TryApplyBloodAltarReward(
                effectId,
                lease,
                lease.SessionId,
                plan,
                out _,
                out _);
            Check(
                "mail failure rolls back gold, attachment and durable effect",
                !failed
                && LoadGold(database, accountId, characterId) == 20
                && CountRewardAttachments(database, characterId) == 0
                && service.Outbox.Get(effectId)?.State
                    == DungeonPersistentEffectState.Failed
                && lease.Inventory.CountMainItem(0) == 20
                && IsInventoryClean(lease.Inventory));

            DropTrigger(database, "fail_blood_altar_mail_attachment");
            var retried = service.TryApplyBloodAltarReward(
                effectId,
                lease,
                lease.SessionId,
                plan,
                out var result,
                out _);
            Check(
                "mail failure retry commits one gold grant and one attachment",
                retried
                && result?.MailedRewardCount == 1
                && LoadGold(database, accountId, characterId) == 30
                && CountRewardAttachments(database, characterId) == 1
                && service.Outbox.Get(effectId)?.State
                    == DungeonPersistentEffectState.Committed);
        }

        private static void CheckOfflineRecovery(
            IGameDatabase database,
            ICollection<InventoryLease> leases)
        {
            const int accountId = 955020;
            const int characterId = 955021;
            var lease = CreateFullLease(
                database,
                accountId,
                characterId,
                initialGold: 40);
            leases.Add(lease);
            var service = CreateService(database);
            var effectId = CreateEffectId(scopeTarget: 54003);
            var plan = CreatePlan(gold: 10);

            CreateAbortTrigger(
                database,
                "fail_blood_altar_outbox_commit",
                "BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox " +
                $"WHEN NEW.character_id = {characterId} " +
                $"AND NEW.state = {(int)DungeonPersistentEffectState.Committed}");
            var failed = service.TryApplyBloodAltarReward(
                effectId,
                lease,
                lease.SessionId,
                plan,
                out _,
                out _);
            DropTrigger(database, "fail_blood_altar_outbox_commit");
            InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
            leases.Remove(lease);

            var recovery = CreateService(database).RecoverCharacter(characterId);
            Check(
                "offline recovery commits full-inventory blood altar reward once",
                !failed
                && recovery.CommittedCount == 1
                && recovery.FailedCount == 0
                && LoadGold(database, accountId, characterId) == 50
                && LoadItemCount(database, accountId, characterId) == 0
                && CountRewardAttachments(database, characterId) == 1
                && service.Outbox.Get(effectId)?.State
                    == DungeonPersistentEffectState.Committed);
        }

        private static DungeonPersistentEffectApplicationService CreateService(
            IGameDatabase database)
        {
            var mailbox = new MailboxService(new MailboxRepository(database));
            return new DungeonPersistentEffectApplicationService(
                database.ConnectionString,
                database: database,
                overflowRewardSink:
                    new MailboxInventoryOverflowRewardSink(mailbox));
        }

        private static BloodAltarSettlementPlan CreatePlan(int gold)
            => new BloodAltarSettlementPlan(
                completedRounds: 1,
                maxRounds: 1,
                clearTimeMilliseconds: 1000,
                rewardExperience: 0,
                rewards: new[]
                {
                    new ClearRewardGenerator.CardReward
                    {
                        IsGold = true,
                        GoldAmount = gold,
                    },
                    new ClearRewardGenerator.CardReward
                    {
                        ItemId = RewardItemId,
                        StackCount = 1,
                    },
                });

        private static DungeonEffectId CreateEffectId(long scopeTarget)
            => new DungeonEffectId(
                Guid.NewGuid(),
                DungeonPersistentEffectKinds.BloodAltarRewardCommit,
                DungeonEffectScope.Player,
                scopeTarget);

        private static InventoryLease CreateFullLease(
            IGameDatabase database,
            int accountId,
            int characterId,
            int initialGold)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, @memberId, '');
INSERT INTO characters(character_id, account_id, name, level)
VALUES(@cid, @aid, @name, 86);";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue(
                        "@memberId",
                        "blood-persistent-" + accountId);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        "blood-persistent-" + characterId);
                    command.ExecuteNonQuery();
                }
            });

            InventoryLease lease;
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                lease = InventoryContext.Register(Guid.NewGuid(), inventory);
            }
            lease.Inventory.SetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                initialGold);
            for (short slot = InventoryService.MainSlotStart;
                slot <= InventoryService.MainSlotEnd;
                slot++)
            {
                var filler = ItemCore.Create(
                    ItemCore.KindEquipment,
                    9_100_000 + slot);
                filler.Count = 1;
                if (!lease.Inventory.SetItem(
                        InventoryListType.Main,
                        slot,
                        filler))
                {
                    throw new InvalidOperationException(
                        "unable to fill blood altar reward slot " + slot);
                }
            }
            if (!InventoryPersistenceService.SaveDirty(lease))
            {
                throw new InvalidOperationException(
                    "unable to persist full blood altar inventory fixture");
            }
            return lease;
        }

        private static int LoadGold(
            IGameDatabase database,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            {
                return InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database)
                    .CountMainItem(
                        InventoryService.MainVirtualCurrencySlotStart);
            }
        }

        private static int LoadItemCount(
            IGameDatabase database,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            {
                return InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database)
                    .CountMainItem(RewardItemId);
            }
        }

        private static int CountRewardAttachments(
            IGameDatabase database,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*)
FROM mailbox_attachments a
JOIN mailbox_recipients r ON r.message_id = a.message_id
WHERE r.character_id = @cid
  AND a.item_template_id = @itemId
  AND a.item_count = 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@itemId", RewardItemId);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static bool IsInventoryClean(InventoryService inventory)
            => inventory != null
               && inventory.DirtyListTypes.Count == 0
               && inventory.DirtyMainVirtualCountSlots.Count == 0
               && inventory.DirtyListParams.Count == 0
               && inventory.AvatarDetails.DirtyDetailUids.Count == 0
               && inventory.AvatarDetails.DeletedDetailUids.Count == 0
               && inventory.CreatureDetails.DirtyDetailUids.Count == 0;

        private static void CreateAbortTrigger(
            IGameDatabase database,
            string name,
            string clause)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = $@"
CREATE TRIGGER {name}
{clause}
BEGIN
    SELECT RAISE(ABORT, 'injected blood altar persistent failure');
END;";
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void DropTrigger(
            IGameDatabase database,
            string name)
        {
            if (database == null)
                return;
            try
            {
                database.Write((connection, transaction) =>
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText =
                            $"DROP TRIGGER IF EXISTS {name};";
                        command.ExecuteNonQuery();
                    }
                });
            }
            catch
            {
            }
        }

        private static void DeleteDatabase(string path)
        {
            foreach (var candidate in new[]
            {
                path,
                path + "-wal",
                path + "-shm",
            })
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

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok)
                _pass++;
            else
                _fail++;
        }
    }
}
