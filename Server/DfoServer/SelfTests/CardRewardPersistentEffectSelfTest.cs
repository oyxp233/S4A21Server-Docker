using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class CardRewardPersistentEffectSelfTest
    {
        private const int RewardItemId = 1252;
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine(
                "=== CARD_REWARD_PERSISTENT_EFFECT selftest ===");

            var path = Path.Combine(
                Path.GetTempPath(),
                $"card-reward-persistent-{Guid.NewGuid():N}.db");
            var leases = new List<InventoryLease>();
            IGameDatabase database = null;
            try
            {
                database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                CheckPaidOutboxFailureAndReplay(database, leases);
                CheckLocalCheckpointRecovery(database, leases);
                CheckOfflineRecovery(database, leases);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] unexpected exception: " + ex);
                _fail++;
            }
            finally
            {
                DropTrigger(database, "fail_paid_card_outbox_commit");
                DropTrigger(database, "fail_offline_card_outbox_commit");
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

        private static void CheckPaidOutboxFailureAndReplay(
            IGameDatabase database,
            ICollection<InventoryLease> leases)
        {
            const int accountId = 954000;
            const int characterId = 954001;
            var lease = CreateLease(
                database,
                accountId,
                characterId,
                initialGold: 100);
            leases.Add(lease);
            var run = BuildRun(
                freeItem: false,
                paidCost: 20,
                paidItemCount: 1);
            var persistent = new DungeonPersistentEffectApplicationService(
                database.ConnectionString,
                database: database);
            var service = new CardRewardService(persistent);
            var effectId = CardRewardRules.GetEffectId(
                run,
                CardRewardSide.Paid);

            Check(
                "paid card fixture selects slot zero",
                CardRewardRules.TrySelectCardSlot(run, 1, 0));
            CreateOutboxCommitAbortTrigger(
                database,
                "fail_paid_card_outbox_commit",
                characterId);
            var failed = service.Deliver(
                characterId,
                lease,
                run,
                CardRewardSide.Paid);
            Check(
                "outbox failure rolls back paid gold, reward and local slot",
                !failed.Committed
                && LoadGold(database, accountId, characterId) == 100
                && LoadItemCount(database, accountId, characterId) == 0
                && lease.Inventory.CountMainItem(0) == 100
                && lease.Inventory.CountMainItem(RewardItemId) == 0
                && IsInventoryClean(lease.Inventory)
                && run.PaidCardSlots[0] == 0xFF
                && run.CardFlipCount == 0
                && persistent.Outbox.Get(effectId)?.State
                    == DungeonPersistentEffectState.Failed);

            DropTrigger(database, "fail_paid_card_outbox_commit");
            Check(
                "paid card retry reselects released slot",
                CardRewardRules.TrySelectCardSlot(run, 1, 0));
            var retried = service.Deliver(
                characterId,
                lease,
                run,
                CardRewardSide.Paid);
            Check(
                "paid card retry atomically commits cost, reward and outbox",
                retried.Committed
                && LoadGold(database, accountId, characterId) == 80
                && LoadItemCount(database, accountId, characterId) == 1
                && lease.Inventory.CountMainItem(0) == 80
                && lease.Inventory.CountMainItem(RewardItemId) == 1
                && IsInventoryClean(lease.Inventory)
                && persistent.Outbox.Get(effectId)?.State
                    == DungeonPersistentEffectState.Committed
                && run.Effects.GetState(effectId)
                    == DungeonEffectState.Committed);

            var replayed = new DungeonPersistentEffectApplicationService(
                    database.ConnectionString,
                    database: database)
                .TryApplyCardReward(
                    effectId,
                    lease,
                    lease.SessionId,
                    CardRewardSide.Paid,
                    paidGoldCost: 20,
                    run.CardRewards,
                    out var replayResult,
                    out _);
            Check(
                "committed replay returns stored changes without duplicate charge",
                replayed
                && replayResult?.Changes.Count == retried.Changes.Count
                && LoadGold(database, accountId, characterId) == 80
                && LoadItemCount(database, accountId, characterId) == 1);

            var conflictingCards = BuildCards(
                freeItem: false,
                paidItemCount: 2);
            var conflicting = persistent.TryApplyCardReward(
                effectId,
                lease,
                lease.SessionId,
                CardRewardSide.Paid,
                paidGoldCost: 20,
                conflictingCards,
                out _,
                out _);
            Check(
                "reusing a committed card effect with different input is rejected",
                !conflicting
                && LoadGold(database, accountId, characterId) == 80
                && LoadItemCount(database, accountId, characterId) == 1);
        }

        private static void CheckLocalCheckpointRecovery(
            IGameDatabase database,
            ICollection<InventoryLease> leases)
        {
            const int accountId = 954010;
            const int characterId = 954011;
            var lease = CreateLease(database, accountId, characterId, 0);
            leases.Add(lease);
            var run = BuildRun(
                freeItem: true,
                paidCost: 0,
                paidItemCount: 0);
            var effectId = CardRewardRules.GetEffectId(
                run,
                CardRewardSide.Free);
            var persistent = new DungeonPersistentEffectApplicationService(
                database.ConnectionString,
                database: database);
            var service = new CardRewardService(
                persistent,
                afterDurableCommit: () => throw new InvalidOperationException(
                    "injected local checkpoint failure"));

            Check(
                "local-checkpoint fixture selects free slot zero",
                CardRewardRules.TrySelectCardSlot(run, 0, 0));
            var committed = service.Deliver(
                characterId,
                lease,
                run,
                CardRewardSide.Free);
            Check(
                "durable truth repairs the run-local checkpoint after commit",
                committed.Committed
                && LoadItemCount(database, accountId, characterId) == 1
                && lease.Inventory.CountMainItem(RewardItemId) == 1
                && persistent.Outbox.Get(effectId)?.State
                    == DungeonPersistentEffectState.Committed
                && run.Effects.GetState(effectId)
                    == DungeonEffectState.Committed
                && run.FreeCardRewardDelivered
                && run.SettlementState == DungeonSettlementState.Completed);
        }

        private static void CheckOfflineRecovery(
            IGameDatabase database,
            ICollection<InventoryLease> leases)
        {
            const int accountId = 954020;
            const int characterId = 954021;
            var lease = CreateLease(database, accountId, characterId, 0);
            leases.Add(lease);
            var run = BuildRun(
                freeItem: true,
                paidCost: 0,
                paidItemCount: 0);
            var effectId = CardRewardRules.GetEffectId(
                run,
                CardRewardSide.Free);
            var persistent = new DungeonPersistentEffectApplicationService(
                database.ConnectionString,
                database: database);
            var service = new CardRewardService(persistent);

            Check(
                "offline-recovery fixture selects free slot zero",
                CardRewardRules.TrySelectCardSlot(run, 0, 0));
            CreateOutboxCommitAbortTrigger(
                database,
                "fail_offline_card_outbox_commit",
                characterId);
            var failed = service.Deliver(
                characterId,
                lease,
                run,
                CardRewardSide.Free);
            DropTrigger(database, "fail_offline_card_outbox_commit");
            InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
            leases.Remove(lease);

            var recovery = new DungeonPersistentEffectApplicationService(
                    database.ConnectionString,
                    database: database)
                .RecoverCharacter(characterId);
            Check(
                "character recovery commits failed card reward offline once",
                !failed.Committed
                && recovery.CommittedCount == 1
                && recovery.FailedCount == 0
                && LoadItemCount(database, accountId, characterId) == 1
                && persistent.Outbox.Get(effectId)?.State
                    == DungeonPersistentEffectState.Committed);
        }

        private static DungeonRun BuildRun(
            bool freeItem,
            int paidCost,
            int paidItemCount)
            => new DungeonRun(11008, 0)
            {
                Phase = DungeonRunPhase.CardsRevealed,
                PaidCardCost = paidCost,
                CardRewards = BuildCards(freeItem, paidItemCount),
                FreeCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
                PaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
            };

        private static List<ClearRewardGenerator.CardReward> BuildCards(
            bool freeItem,
            int paidItemCount)
            => new List<ClearRewardGenerator.CardReward>
            {
                new ClearRewardGenerator.CardReward
                {
                    IsGold = true,
                    GoldAmount = 0,
                },
                freeItem
                    ? new ClearRewardGenerator.CardReward
                    {
                        ItemId = RewardItemId,
                        StackCount = 1,
                    }
                    : default,
                default,
                default,
                new ClearRewardGenerator.CardReward
                {
                    IsGold = true,
                    GoldAmount = 0,
                },
                paidItemCount > 0
                    ? new ClearRewardGenerator.CardReward
                    {
                        ItemId = RewardItemId,
                        StackCount = paidItemCount,
                    }
                    : default,
                default,
                default,
            };

        private static InventoryLease CreateLease(
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
                        "card-persistent-" + accountId);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        "card-persistent-" + characterId);
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
            if (initialGold > 0)
            {
                lease.Inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    initialGold);
                if (!InventoryPersistenceService.SaveDirty(lease))
                {
                    throw new InvalidOperationException(
                        "unable to persist card-reward gold fixture");
                }
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

        private static bool IsInventoryClean(InventoryService inventory)
            => inventory != null
               && inventory.DirtyListTypes.Count == 0
               && inventory.DirtyMainVirtualCountSlots.Count == 0
               && inventory.DirtyListParams.Count == 0
               && inventory.AvatarDetails.DirtyDetailUids.Count == 0
               && inventory.AvatarDetails.DeletedDetailUids.Count == 0
               && inventory.CreatureDetails.DirtyDetailUids.Count == 0;

        private static void CreateOutboxCommitAbortTrigger(
            IGameDatabase database,
            string name,
            int characterId)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = $@"
CREATE TRIGGER {name}
BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox
WHEN NEW.character_id = {characterId}
 AND NEW.state = {(int)DungeonPersistentEffectState.Committed}
BEGIN
    SELECT RAISE(ABORT, 'injected card reward outbox failure');
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
