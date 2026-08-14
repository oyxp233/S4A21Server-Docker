using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.BloodAltar;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class DungeonRewardFailureRecoverySelfTest
    {
        private const int RewardItemId = 1252;
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine(
                "=== DUNGEON_REWARD_FAILURE_RECOVERY selftest ===");

            var path = Path.Combine(
                Path.GetTempPath(),
                $"dungeon-reward-recovery-{Guid.NewGuid():N}.db");
            var leases = new List<InventoryLease>();
            IGameDatabase database = null;
            try
            {
                database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                CheckBloodAltarRollback(database, leases);
                CheckCardRewardRollback(database, leases);
                CheckCardInfoProjectionRollback();
                CheckCardMissingLeaseRecovery();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] unexpected exception: " + ex);
                _fail++;
            }
            finally
            {
                DropTrigger(database, "fail_blood_altar_reward_insert");
                DropTrigger(database, "fail_card_reward_insert");
                foreach (var lease in leases)
                    InventoryContext.Unregister(
                        lease.SessionId,
                        lease.CharacterId);
                SqliteConnection.ClearAllPools();
                DeleteDatabase(path);
            }

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void CheckBloodAltarRollback(
            IGameDatabase database,
            ICollection<InventoryLease> leases)
        {
            const int accountId = 953000;
            const int characterId = 953001;
            var lease = CreateLease(database, accountId, characterId);
            leases.Add(lease);
            var service = new DungeonPersistentEffectApplicationService(
                database.ConnectionString,
                database: database);
            var plan = new BloodAltarSettlementPlan(
                completedRounds: 1,
                maxRounds: 1,
                clearTimeMilliseconds: 1000,
                rewardExperience: 0,
                rewards: CreateRewards());

            CreateAbortTrigger(
                database,
                "fail_blood_altar_reward_insert",
                characterId);
            var effectId = new DungeonEffectId(
                Guid.NewGuid(),
                DungeonPersistentEffectKinds.BloodAltarRewardCommit,
                DungeonEffectScope.Player,
                scopeTarget: 53001);
            var failed = !service.TryApplyBloodAltarReward(
                effectId,
                lease,
                lease.SessionId,
                plan,
                out _,
                out _);
            Check(
                "blood altar persistence failure reloads a clean owned lease",
                failed
                && InventoryContext.IsCurrentLease(
                    lease,
                    lease.SessionId,
                    characterId)
                && lease.Inventory.CountMainItem(RewardItemId) == 0
                && IsInventoryClean(lease.Inventory)
                && LoadItemCount(database, accountId, characterId) == 0);

            DropTrigger(database, "fail_blood_altar_reward_insert");
            InventoryPersistenceService.SaveAllDirty();
            Check(
                "blood altar failed reward cannot leak through dirty flush",
                LoadItemCount(database, accountId, characterId) == 0);

            var committed = service.TryApplyBloodAltarReward(
                effectId,
                lease,
                lease.SessionId,
                plan,
                out var result,
                out _);
            Check(
                "blood altar reward retries after reload and commits once",
                committed
                && result != null
                && result.Changes.Count == 1
                && lease.Inventory.CountMainItem(RewardItemId) == 1
                && LoadItemCount(database, accountId, characterId) == 1
                && IsInventoryClean(lease.Inventory));
        }

        private static void CheckCardRewardRollback(
            IGameDatabase database,
            ICollection<InventoryLease> leases)
        {
            const int accountId = 953010;
            const int characterId = 953011;
            var lease = CreateLease(database, accountId, characterId);
            leases.Add(lease);
            var run = BuildCardRun();
            var service = new CardRewardService();

            Check(
                "card reward fixture selects the free reward slot",
                CardRewardRules.TrySelectCardSlot(run, 0, 0));
            CreateAbortTrigger(
                database,
                "fail_card_reward_insert",
                characterId);
            var failed = service.Deliver(
                characterId,
                lease,
                run,
                CardRewardSide.Free);
            Check(
                "card reward persistence failure reloads lease and releases slot",
                !failed.Committed
                && InventoryContext.IsCurrentLease(
                    lease,
                    lease.SessionId,
                    characterId)
                && lease.Inventory.CountMainItem(RewardItemId) == 0
                && IsInventoryClean(lease.Inventory)
                && LoadItemCount(database, accountId, characterId) == 0
                && run.FreeCardSlots[0] == 0xFF
                && run.CardFlipCount == 0
                && run.Effects.GetState(
                    CardRewardRules.GetEffectId(
                        run,
                        CardRewardSide.Free))
                    == DungeonEffectState.Failed);

            DropTrigger(database, "fail_card_reward_insert");
            Check(
                "card reward retry can select the released slot",
                CardRewardRules.TrySelectCardSlot(run, 0, 0));
            var retried = service.Deliver(
                characterId,
                lease,
                run,
                CardRewardSide.Free);
            Check(
                "card reward retry commits once with a clean lease",
                retried.Committed
                && lease.Inventory.CountMainItem(RewardItemId) == 1
                && LoadItemCount(database, accountId, characterId) == 1
                && IsInventoryClean(lease.Inventory)
                && run.Effects.GetState(
                    CardRewardRules.GetEffectId(
                        run,
                        CardRewardSide.Free))
                    == DungeonEffectState.Committed);
        }

        private static void CheckCardInfoProjectionRollback()
        {
            var session = new EnhancedClientSession(new TcpClient(), null);
            try
            {
                session.Player.CharacterId = 953021;
                var run = BuildCardRun();
                session.Player.CurrentRun = run;
                var sender = new ThrowingCardNotificationSender();
                var coordinator = new CardRewardCoordinator(sender: sender);

                coordinator.HandleSelectCard(
                        session,
                        new byte[] { 0, 0 })
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "card-info failure restores the selected slot and timer",
                    sender.CardInfoAttempts == 1
                    && run.FreeCardSlots[0] == 0xFF
                    && run.CardFlipCount == 0
                    && run.Effects.GetState(
                        CardRewardRules.GetEffectId(
                            run,
                            CardRewardSide.Free))
                        == DungeonEffectState.Absent
                    && run.Timers.TryGetCurrentTicket(
                        DungeonRunTimerKeys.SettlementCardAutoFlow,
                        out _));
                Check(
                    "paid card without a positive stack is not chargeable",
                    !CardRewardRules.HasPaidCardReward(
                        new List<ClearRewardGenerator.CardReward>
                        {
                            default,
                            default,
                            default,
                            default,
                            default,
                            new ClearRewardGenerator.CardReward
                            {
                                ItemId = RewardItemId,
                                StackCount = 0,
                            },
                        }));
                DungeonRunLifecycle.CancelAutoFlip(session);
            }
            finally
            {
                session.Close();
            }
        }

        private static void CheckCardMissingLeaseRecovery()
        {
            var session = new EnhancedClientSession(new TcpClient(), null);
            try
            {
                session.Player.CharacterId = 953022;
                var run = BuildCardRun();
                session.Player.CurrentRun = run;
                var sender = new SuccessfulCardNotificationSender();
                var coordinator = new CardRewardCoordinator(sender: sender);

                coordinator.HandleSelectCard(
                        session,
                        new byte[] { 0, 0 })
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "missing owned lease releases the free slot and restores auto retry",
                    sender.CardInfoAttempts == 1
                    && run.FreeCardSlots[0] == 0xFF
                    && run.CardFlipCount == 0
                    && run.Effects.GetState(
                        CardRewardRules.GetEffectId(
                            run,
                            CardRewardSide.Free))
                        == DungeonEffectState.Absent
                    && run.Timers.TryGetCurrentTicket(
                        DungeonRunTimerKeys.SettlementCardAutoFlow,
                        out _));
                DungeonRunLifecycle.CancelAutoFlip(session);
            }
            finally
            {
                session.Close();
            }
        }

        private static DungeonRun BuildCardRun()
            => new DungeonRun(11008, 0)
            {
                Phase = DungeonRunPhase.CardsRevealed,
                CardRewards = new List<ClearRewardGenerator.CardReward>
                {
                    new ClearRewardGenerator.CardReward
                    {
                        IsGold = true,
                        GoldAmount = 0,
                    },
                    new ClearRewardGenerator.CardReward
                    {
                        ItemId = RewardItemId,
                        StackCount = 1,
                    },
                    default,
                    default,
                    default,
                    default,
                    default,
                    default,
                },
                FreeCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
                PaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
            };

        private static IReadOnlyList<ClearRewardGenerator.CardReward>
            CreateRewards()
            => new[]
            {
                new ClearRewardGenerator.CardReward
                {
                    ItemId = RewardItemId,
                    StackCount = 1,
                },
            };

        private static InventoryLease CreateLease(
            IGameDatabase database,
            int accountId,
            int characterId)
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
                        "dungeon-reward-" + accountId);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue(
                        "@name",
                        "dungeon-reward-" + characterId);
                    command.ExecuteNonQuery();
                }
            });

            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return InventoryContext.Register(Guid.NewGuid(), inventory);
            }
        }

        private static int LoadItemCount(
            IGameDatabase database,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return inventory.CountMainItem(RewardItemId);
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
            int characterId)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = $@"
CREATE TRIGGER {name}
BEFORE INSERT ON character_inventory_items
WHEN NEW.character_id = {characterId}
BEGIN
    SELECT RAISE(ABORT, 'injected dungeon reward failure');
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

        private sealed class ThrowingCardNotificationSender
            : ICardRewardNotificationSender
        {
            internal int CardInfoAttempts { get; private set; }

            public Task SendLayoutAsync(EnhancedClientSession session)
                => Task.CompletedTask;

            public Task SendCardInfoAsync(
                EnhancedClientSession session,
                DungeonRun run)
            {
                CardInfoAttempts++;
                return Task.FromException(
                    new InvalidOperationException(
                        "injected card-info projection failure"));
            }

            public Task SendExitAsync(
                EnhancedClientSession session,
                byte state,
                byte option)
                => Task.CompletedTask;

            public Task SendItemUpdatesAsync(
                EnhancedClientSession session,
                IReadOnlyList<InventorySlotMutation> changes)
                => Task.CompletedTask;
        }

        private sealed class SuccessfulCardNotificationSender
            : ICardRewardNotificationSender
        {
            internal int CardInfoAttempts { get; private set; }

            public Task SendLayoutAsync(EnhancedClientSession session)
                => Task.CompletedTask;

            public Task SendCardInfoAsync(
                EnhancedClientSession session,
                DungeonRun run)
            {
                CardInfoAttempts++;
                return Task.CompletedTask;
            }

            public Task SendExitAsync(
                EnhancedClientSession session,
                byte state,
                byte option)
                => Task.CompletedTask;

            public Task SendItemUpdatesAsync(
                EnhancedClientSession session,
                IReadOnlyList<InventorySlotMutation> changes)
                => Task.CompletedTask;
        }
    }
}
