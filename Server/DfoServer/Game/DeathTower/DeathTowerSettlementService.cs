using System;
using System.Collections.Generic;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.DeathTower
{
    public readonly struct DeathTowerRewardItem
    {
        public DeathTowerRewardItem(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }
        public int Count { get; }
    }

    public sealed class DeathTowerSettlementResult
    {
        public int ClearedFloorCount { get; set; }
        public uint ExpGained { get; set; }
        public int GoldGained { get; set; }
        public int UpdatedGold { get; set; }
        public byte PreviousLevel { get; set; }
        public byte UpdatedLevel { get; set; }
        public uint NormalExpGained { get; set; }
        public uint HonorExpGained { get; set; }
        public bool LeveledUp { get; set; }
        public bool CharacterStateChanged { get; set; }
        public AccountExperienceProgressSummary AccountProgress { get; set; }
        public IReadOnlyList<short> ChangedMainSlots { get; set; }
            = Array.Empty<short>();
        public IReadOnlyList<DeathTowerRewardItem> Items { get; set; }
            = Array.Empty<DeathTowerRewardItem>();
        internal ExperienceGrantResult ExperienceGrant { get; set; }
    }

    internal delegate ExperienceGrantResult DeathTowerExperienceGrantInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int characterId,
        int accountId,
        byte currentLevel,
        uint currentExp,
        uint rawGain);

    public sealed class DeathTowerSettlementService
    {
        internal const uint MaximumRewardExperience = 1_100_000;

        private readonly string _connectionString;
        private readonly AccountExperienceProgressService _accountExperience;
        private readonly DeathTowerExperienceGrantInTransaction
            _grantExperienceInTransaction;

        public DeathTowerSettlementService(
            string connectionString,
            AccountExperienceProgressService accountExperience = null)
            : this(connectionString, accountExperience, null)
        {
        }

        internal DeathTowerSettlementService(
            string connectionString,
            AccountExperienceProgressService accountExperience,
            DeathTowerExperienceGrantInTransaction grantExperienceInTransaction)
        {
            _connectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentException(
                    "A database connection string is required.",
                    nameof(connectionString));
            _accountExperience = accountExperience
                ?? throw new ArgumentNullException(nameof(accountExperience));
            _grantExperienceInTransaction = grantExperienceInTransaction
                ?? ((connection, transaction, characterId, accountId, level,
                        exp, rawGain) =>
                    CharacterExperienceService.GrantInTransaction(
                        connection,
                        transaction,
                        characterId,
                        accountId,
                        level,
                        exp,
                        rawGain,
                        normalizeMaxLevelExp: rawGain > 0));
        }

        internal DeathTowerSettlementPlan Prepare(
            DeathTowerSettlementContext context,
            DeathTowerSession tower,
            int clearTimeMilliseconds)
        {
            if (tower == null)
                throw new ArgumentNullException(nameof(tower));

            var rewardConfig = DeathTowerRewardConfig.Load();
            if (!rewardConfig.IsAvailable)
            {
                throw new InvalidOperationException(
                    "Death tower reward configuration is unavailable.");
            }

            var clearedFloorCount = Math.Max(
                1,
                Math.Min(tower.Config.TotalStages, tower.CurrentStage + 1));
            var rewardExp = CalculateRewardExperience(
                context.Level,
                clearedFloorCount,
                rewardConfig.GetExpWeight(
                    tower.Config.RewardProfile,
                    clearedFloorCount));
            var candidateCount = Math.Min(
                DeathTowerRewardConfig.MaximumRewardProgress,
                Math.Min(
                    Math.Max(0, tower.Config.MaxClearItemCount),
                    rewardConfig.GetRewardCardCount(
                        tower.Config.RewardProfile,
                        clearedFloorCount)));
            var lcg = tower.StageLcg ?? new DnfLcg(tower.StageSeed);
            var candidates = new List<DeathTowerRewardCandidate>(candidateCount);
            for (var index = 0; index < candidateCount; index++)
            {
                var kind = rewardConfig.ClassifyCandidate(
                    clearedFloorCount,
                    lcg.Next(rewardConfig.RewardRollScale));
                candidates.Add(CreateCandidate(
                    kind,
                    context.Level,
                    context.Difficulty,
                    rewardConfig.GoldAmountWeight,
                    lcg));
            }

            return new DeathTowerSettlementPlan(
                context,
                tower.Config.DungeonId,
                clearedFloorCount,
                clearTimeMilliseconds,
                rewardExp,
                candidates);
        }

        internal DeathTowerSettlementResult Commit(
            DeathTowerSettlementPlan settlement,
            InventoryLease lease,
            Guid ownerSessionId)
        {
            if (settlement == null)
                throw new ArgumentNullException(nameof(settlement));
            if (lease == null)
                throw new ArgumentNullException(nameof(lease));

            var context = settlement.Context;
            if (lease.CharacterId != context.CharacterId
                || lease.AccountId != context.AccountId
                || !InventoryContext.IsCurrentLease(
                    lease,
                    ownerSessionId,
                    context.CharacterId))
            {
                throw new InvalidOperationException(
                    "Death tower settlement requires the current owned inventory lease.");
            }

            lock (lease.SyncRoot)
            {
                return CommitOwned(settlement, lease, ownerSessionId);
            }
        }

        private DeathTowerSettlementResult CommitOwned(
            DeathTowerSettlementPlan settlement,
            InventoryLease lease,
            Guid ownerSessionId)
        {
            var context = settlement.Context;
            var inventory = lease.Inventory;
            var requests = new List<InventoryRewardGrantRequest>();
            var deliveredItems = new List<DeathTowerRewardItem>();
            long requestedGold = 0;
            foreach (var candidate in settlement.Candidates)
            {
                if (candidate.Kind == DeathTowerRewardCandidateKind.Gold)
                {
                    requestedGold += Math.Max(0, candidate.AddInfo);
                    continue;
                }
                if (candidate.Kind != DeathTowerRewardCandidateKind.Item
                    || candidate.ItemId <= 0
                    || candidate.AddInfo <= 0)
                {
                    continue;
                }

                requests.Add(InventoryRewardGrantRequest.Create(
                    candidate.ItemId,
                    candidate.AddInfo,
                    ItemCreateReason.DungeonDrop));
                deliveredItems.Add(new DeathTowerRewardItem(
                    candidate.ItemId,
                    candidate.AddInfo));
            }

            var currentGold = inventory.CountMainItem(0);
            var carryLimit = Math.Max(
                0,
                InventoryGoldCarryLimitLoader.Load(context.CharacterId));
            var grantedGold = (int)Math.Min(
                Math.Max(0L, requestedGold),
                Math.Max(0L, (long)carryLimit - currentGold));
            if (grantedGold > 0)
            {
                requests.Insert(
                    0,
                    InventoryRewardGrantRequest.Create(
                        0,
                        grantedGold,
                        ItemCreateReason.DungeonDrop));
            }

            if (!InventoryRewardGrantService.TryPlanBatch(
                    inventory,
                    requests,
                    out var inventoryPlan))
            {
                throw new InvalidOperationException(
                    $"Death tower reward inventory planning failed: " +
                    $"{inventoryPlan?.Error.ToString() ?? "unknown"}.");
            }

            var snapshotPlan = new DungeonItemGrantBatchPlan
            {
                Success = true,
                InventoryPlan = inventoryPlan,
            };
            if (!DungeonItemGrantMutationSnapshot.TryCapture(
                    inventory,
                    snapshotPlan,
                    out var rollback))
            {
                throw new InvalidOperationException(
                    "Death tower reward inventory snapshot failed.");
            }

            var committed = false;
            InventoryRewardGrantBatchResult grantBatch = null;
            ExperienceGrantResult experienceGrant = null;
            try
            {
                if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                        inventory,
                        inventoryPlan,
                        out grantBatch)
                    || !grantBatch.Success)
                {
                    throw new InvalidOperationException(
                        $"Death tower reward inventory apply failed: " +
                        $"{grantBatch?.Error.ToString() ?? "unknown"}.");
                }

                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        experienceGrant = _grantExperienceInTransaction(
                            connection,
                            transaction,
                            context.CharacterId,
                            context.AccountId,
                            context.Level,
                            context.Exp,
                            settlement.RewardExp);
                        if (RequiresPersistence(experienceGrant)
                            && !experienceGrant.Persisted)
                        {
                            throw new InvalidOperationException(
                                "Death tower experience persistence returned false.");
                        }
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                connection,
                                transaction,
                                lease))
                        {
                            throw new InvalidOperationException(
                                "Death tower inventory persistence returned false.");
                        }
                        if (!InventoryContext.IsCurrentLease(
                                lease,
                                ownerSessionId,
                                context.CharacterId))
                        {
                            throw new InvalidOperationException(
                                "Death tower inventory lease changed before commit.");
                        }

                        transaction.Commit();
                        committed = true;
                    }
                }

                inventory.ClearDirtyState();
            }
            catch
            {
                if (!committed)
                    rollback.Restore(inventory, snapshotPlan);
                throw;
            }

            var changedMainSlots = CollectChangedMainSlots(grantBatch?.Changes);
            var updatedGold = inventory.CountMainItem(0);
            var accountProgress = BuildAccountProgress(
                context,
                experienceGrant);
            var characterStateChanged = experienceGrant.NewLevel
                    != experienceGrant.PreviousLevel
                || experienceGrant.NewExp != experienceGrant.PreviousExp;

            return new DeathTowerSettlementResult
            {
                ClearedFloorCount = settlement.ClearedFloorCount,
                ExpGained = settlement.RewardExp,
                GoldGained = grantedGold,
                UpdatedGold = updatedGold,
                PreviousLevel = context.Level,
                UpdatedLevel = experienceGrant.NewLevel,
                NormalExpGained = experienceGrant.NormalExpGain,
                HonorExpGained = experienceGrant.HonorExpGain,
                LeveledUp = experienceGrant.LeveledUp,
                CharacterStateChanged = characterStateChanged,
                AccountProgress = accountProgress,
                ChangedMainSlots = changedMainSlots,
                Items = deliveredItems,
                ExperienceGrant = experienceGrant,
            };
        }

        internal static uint CalculateRewardExperience(
            byte characterLevel,
            int clearedFloorCount,
            float floorWeight)
        {
            if (clearedFloorCount <= 0 || floorWeight <= 0)
                return 0;

            var value = MonsterRewardTable.GetMobReward(characterLevel)
                * (double)clearedFloorCount
                * floorWeight;
            if (value <= 0 || double.IsNaN(value))
                return 0;
            return (uint)Math.Min(MaximumRewardExperience, value);
        }

        private static DeathTowerRewardCandidate CreateCandidate(
            DeathTowerRewardCandidateKind kind,
            byte characterLevel,
            byte difficulty,
            float goldAmountWeight,
            DnfLcg lcg)
        {
            if (kind == DeathTowerRewardCandidateKind.Gold)
            {
                var gold = CalculateGold(characterLevel, goldAmountWeight);
                return gold > 0
                    ? DeathTowerRewardCandidate.Gold(gold)
                    : DeathTowerRewardCandidate.Empty();
            }
            if (kind == DeathTowerRewardCandidateKind.Item)
            {
                var card = ClearRewardGenerator.GenerateItemCard(
                    characterLevel,
                    difficulty,
                    lcg);
                return card.ItemId > 0 && card.StackCount > 0
                    ? DeathTowerRewardCandidate.Item(
                        card.ItemId,
                        card.StackCount)
                    : DeathTowerRewardCandidate.Empty();
            }
            return DeathTowerRewardCandidate.Empty();
        }

        private static int CalculateGold(byte level, float weight)
        {
            if (weight <= 0)
                return 0;
            var baseGold = ExpTableProvider.GetMonsterGold(level, out _);
            var value = baseGold * (double)weight;
            if (value <= 0 || double.IsNaN(value))
                return 0;
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }

        private AccountExperienceProgressSummary BuildAccountProgress(
            DeathTowerSettlementContext context,
            ExperienceGrantResult experienceGrant)
        {
            if (experienceGrant == null
                || experienceGrant.HonorExpGain == 0
                || context.AccountId <= 0)
            {
                return null;
            }

            try
            {
                var totals = new AccountExperienceProgressTotals(
                    experienceGrant.TotalHonorExp,
                    experienceGrant.TotalGrowthCapsuleExp,
                    experienceGrant.GrowthCapsuleExpGain);
                var summary = _accountExperience.BuildSummary(
                    context.AccountId,
                    totals);
                if (summary != null)
                {
                    experienceGrant.Honor = summary.Honor;
                    experienceGrant.GrowthCapsule = summary.GrowthCapsule;
                }
                return summary;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DeathTower] account progress projection failed: " +
                    $"account={context.AccountId} cid={context.CharacterId} " +
                    $"error={ex.Message}");
                return null;
            }
        }

        private static bool RequiresPersistence(ExperienceGrantResult result)
            => result != null
                && (result.LeveledUp
                    || result.NormalExpGain > 0
                    || result.NormalizedMaxLevelExp);

        private static IReadOnlyList<short> CollectChangedMainSlots(
            InventoryMutationSet changes)
        {
            if (changes == null || !changes.HasChanges)
                return Array.Empty<short>();

            var result = new List<short>();
            var seen = new HashSet<short>();
            foreach (var change in changes.Slots)
            {
                if (change.ListType == InventoryListType.Main
                    && seen.Add(change.SlotIndex))
                {
                    result.Add(change.SlotIndex);
                }
            }
            return result;
        }
    }
}
