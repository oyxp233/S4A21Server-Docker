using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.Lottery;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Lottery;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.SelfTests
{
    public static class LotteryItemSelfTest
    {
        private const int AccountId = 1;
        private const int CharacterId = 999218;
        private const short LotterySlot = 105;
        private const short DoubleLotterySlot = 106;
        private const short UpgradableLegacySlot = 107;
        private const short HeroLotterySlot = 108;
        private const short AncientHeroLotterySlot = 109;
        private const short ConcurrentLotterySlot = 110;
        private const short RequiredItemLotterySlot = 111;
        private const short MagicCapsuleSlot = 112;
        private const short RequiredItemSlot = 157;
        private const short RewardSlot = 120;
        private const int SampleLotteryItemId = 10014964;
        private const int SampleRewardItemId = 400360011;
        private const int MagicBoxItemId = 10007368;
        private const int HeroLotteryItemId = 8095;
        private const int AncientHeroLotteryItemId = 8213;
        private const int RequiredItemLotteryItemId = 10007501;
        private const int RequiredLotteryMaterialItemId = 10007498;
        private const int MagicCapsuleItemId = 10089090;
        private const int MagicCapsulePrimaryRewardItemId = 10089088;
        private const int MagicCapsuleSecondaryRewardItemId = 3116;
        private const int CannedAvatarItemId = 39075;
        private const int LegacyEquipmentItemId = 100150516;
        private const int EpicEquipmentItemId = 101000004;

        public static int Run()
        {
            Console.WriteLine("=== LOTTERY_ITEM selftest ===");
            var failures = 0;

            TestProtocolAndPresentation(ref failures);
            TestDefinitionAndSession(ref failures);
            TestIndependentService(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void TestProtocolAndPresentation(ref int failures)
        {
            Check("parse phase0", LotteryItemUseRequest.TryParse(
                new byte[] { 0x00, 0x00, 0x69, 0x00 }, out var phase0)
                && phase0.Phase == 0
                && phase0.SlotIndex == LotterySlot, ref failures);
            Check("parse phase1", LotteryItemUseRequest.TryParse(
                new byte[] { 0x01, 0x00, 0x6A, 0x00 }, out var phase1)
                && phase1.Phase == 1
                && phase1.SlotIndex == DoubleLotterySlot, ref failures);
            Check("reject short body", !LotteryItemUseRequest.TryParse(
                new byte[] { 0x01, 0x00, 0x6A }, out _), ref failures);
            Check("reject unknown phase", !LotteryItemUseRequest.TryParse(
                new byte[] { 0x02, 0x00, 0x6A, 0x00 }, out _), ref failures);
            Check("exact lottery overflow confirm", LotteryItemHandler.IsLotteryOverflowConfirm(
                new byte[] { 0x01, 0x1B, 0x00 }), ref failures);
            Check("reject unrelated overflow confirm", !LotteryItemHandler.IsLotteryOverflowConfirm(
                new byte[] { 0x01, 0x1A, 0x00 }), ref failures);

            var resetRequestBody = new byte[21];
            Buffer.BlockCopy(BitConverter.GetBytes((short)113), 0, resetRequestBody, 13, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(0x0098B414), 0, resetRequestBody, 17, 4);
            Check("parse increase chance reset request",
                IncreaseChanceLotteryResetRequest.TryParse(resetRequestBody, out var resetRequest)
                && resetRequest.SlotIndex == 113
                && resetRequest.ItemTemplateId == 0x0098B414,
                ref failures);
            Check("reject malformed increase chance reset request",
                !IncreaseChanceLotteryResetRequest.TryParse(new byte[20], out _),
                ref failures);

            var progress = new LotteryProgressSnapshot
            {
                ItemTemplateId = 0x0098B414,
                NewRewardIndex = 6,
            };
            progress.ClaimedRewardIndexes.Add(1);
            progress.ClaimedRewardIndexes.Add(6);
            var progressBody = IncreaseChanceLotteryPacketBuilder.BuildAllState(progress);
            Check("increase chance all-state body layout",
                progressBody.Length == 204
                && BitConverter.ToInt32(progressBody, 0) == 2
                && BitConverter.ToInt32(progressBody, 4) == 0x0098B414
                && BitConverter.ToInt32(progressBody, 8) == 7
                && BitConverter.ToInt32(progressBody, 12) == 0x0098B414
                && progressBody[16] == 2
                && progressBody[17] == 7,
                ref failures);
            var resetResponse = IncreaseChanceLotteryPacketBuilder.BuildResetResponse(0, true);
            Check("increase chance reset response layout",
                 resetResponse.Length == 1
                && resetResponse[0] == 1,
                ref failures);
            var failedResetResponse = IncreaseChanceLotteryPacketBuilder.BuildResetResponse(1, false);
            Check("increase chance reset error response layout",
                failedResetResponse.Length == 2
                && failedResetResponse[0] == 0
                && failedResetResponse[1] == 1,
                ref failures);
            var emptyProgressBody = IncreaseChanceLotteryPacketBuilder.BuildAllState(
                (LotteryProgressSnapshot)null);
            Check("increase chance empty-state body layout",
                emptyProgressBody.Length == 204
                && emptyProgressBody.All(value => value == 0),
                ref failures);

            var phaseStart = LotteryItemAckBuilder.BuildPhaseStartWithoutPreview();
            Check("phase start body length", phaseStart.Length == 13, ref failures);
            Check("phase start hides source slot", BitConverter.ToInt16(phaseStart, 1) == -1, ref failures);
            Check("phase start hides preview", BitConverter.ToInt32(phaseStart, 5) == 0
                && BitConverter.ToInt32(phaseStart, 9) == 0, ref failures);

            var rewardItem = ItemCore.Create(ItemCore.KindEquipment, SampleRewardItemId);
            rewardItem.Value = 0x13572468;
            rewardItem.Durability = 100;
            rewardItem.Attr = 7;
            rewardItem.AmplifyType = 3;
            rewardItem.AmplifyValue = 0x1234;
            rewardItem.ExpireTime = 0x12345678;
            var nativeResult = LotteryItemAckBuilder.BuildCommonItemResult(
                LotterySlot,
                RewardSlot,
                rewardItem,
                2);
            Check("common result body length", nativeResult.Length == 52, ref failures);
            Check("common result source and reward", nativeResult[0] == 1
                && BitConverter.ToInt16(nativeResult, 1) == LotterySlot
                && BitConverter.ToInt16(nativeResult, 3) == RewardSlot
                && BitConverter.ToInt32(nativeResult, 5) == SampleRewardItemId, ref failures);
            Check("common result x2 display", BitConverter.ToInt32(nativeResult, 9) == 2, ref failures);
            Check("common result native tail", nativeResult[19] == 0xEF
                && BitConverter.ToInt32(nativeResult, 20) == 25
                && nativeResult.Skip(24).Take(25).All(value => value == 0)
                && nativeResult.Skip(49).All(value => value == 0), ref failures);

            var duplicateEquipmentRewards = new[]
            {
                new LotteryRewardGrant
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = RewardSlot,
                    ItemTemplateId = SampleRewardItemId,
                    GrantedCount = 1,
                },
                new LotteryRewardGrant
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = RewardSlot + 1,
                    ItemTemplateId = SampleRewardItemId,
                    GrantedCount = 1,
                },
            };
            Check("double presentation aggregates x2", LotteryPresentationPolicy.ResolveDisplayValue(
                rewardItem,
                duplicateEquipmentRewards[0],
                duplicateEquipmentRewards) == 2, ref failures);
            Check("double result flow is isolated", LotteryPresentationPolicy.ShouldUseDoubleRewardResultFlow(
                true,
                duplicateEquipmentRewards), ref failures);
            Check("regular multi reward remains regular", !LotteryPresentationPolicy.ShouldUseDoubleRewardResultFlow(
                false,
                duplicateEquipmentRewards), ref failures);
            Check("duplicate equipment refresh keeps both rows", LotteryPresentationPolicy.ResolveMainRefreshRewards(
                duplicateEquipmentRewards).Count == 2, ref failures);
            Check("regular duplicate notice is suppressed", LotteryPresentationPolicy.ShouldSuppressNotice(
                duplicateEquipmentRewards[0],
                duplicateEquipmentRewards), ref failures);

            var singleReward = new LotteryRewardGrant
            {
                ListType = InventoryListType.Main,
                SlotIndex = RewardSlot,
                ItemTemplateId = SampleLotteryItemId,
                GrantedCount = 1,
            };
            Check("single lottery reward keeps native result only", LotteryItemResponseSender.ResolveMainRewardUpdatesAfterNativeResult(
                singleReward,
                new[] { singleReward },
                false).Count == 0, ref failures);

            var avatarReward = ItemCore.Create(ItemCore.KindAvatar, CannedAvatarItemId);
            avatarReward.Value = 1;
            var avatarBody = LotteryItemAckBuilder.BuildAvatarItemResult(
                LotterySlot,
                3,
                avatarReward,
                new AvatarDetail { AvatarUid = 1, JewelSocket = new byte[30] });
            Check("avatar result body length", avatarBody.Length == 129, ref failures);
            Check("avatar result success", avatarBody[0] == 1
                && BitConverter.ToInt16(avatarBody, 1) == LotterySlot, ref failures);

            Check("legacy reward announcement eligible", LotteryPresentationPolicy.IsNoticeEligible(
                ItemMetadataResolver.Resolve(LegacyEquipmentItemId)), ref failures);
            Check("epic reward announcement eligible", LotteryPresentationPolicy.IsNoticeEligible(
                ItemMetadataResolver.Resolve(EpicEquipmentItemId)), ref failures);
            Check("stackable reward announcement excluded", !LotteryPresentationPolicy.IsNoticeEligible(
                ItemMetadataResolver.Resolve(SampleLotteryItemId)), ref failures);

            var goldInventory = new InventoryService(CharacterId, AccountId);
            goldInventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 123456);
            var goldUpdate = ItemListUpdateBuilder.BuildItemSpaceUpdateBody(
                goldInventory,
                InventoryListType.Main,
                new[] { InventoryService.MainVirtualCurrencySlotStart });
            Check("gold refresh payload", goldUpdate[0] == 0
                && BitConverter.ToUInt16(goldUpdate, 1) == 1
                && BitConverter.ToInt16(goldUpdate, 3) == 0
                && BitConverter.ToInt32(goldUpdate, 9) == 123456, ref failures);

            var buyAck = BuyItemAckBuilder.Build(new InventoryMutationResult
            {
                SlotIndex = RewardSlot,
                ItemTemplateId = SampleLotteryItemId,
                InstanceValue = 1,
                UpdatedCoin = 0x12345678,
                ExpireTime = 0x01020304,
            });
            Check("buy ACK carries coin balance", BitConverter.ToInt32(buyAck, 13) == 0x12345678, ref failures);
            Check("buy ACK carries item expire time", BitConverter.ToInt32(buyAck, 32) == 0x01020304, ref failures);
            var containerBuyAck = BuyItemAckBuilder.Build(new InventoryMutationResult
            {
                SlotIndex = RewardSlot,
                ItemTemplateId = MagicBoxItemId,
                InstanceValue = 1,
                ExpireTime = 0x01020304,
            });
            Check("buy ACK carries container summary", BitConverter.ToInt32(containerBuyAck, 19) == MagicBoxItemId
                && BitConverter.ToInt32(containerBuyAck, 23) == 1
                && BitConverter.ToInt32(containerBuyAck, 32) == 0x01020304, ref failures);
        }

        private static void TestDefinitionAndSession(ref int failures)
        {
            var definitions = new LotteryItemDefinitionProvider();
            Check("PVF ordinary lottery definition", definitions.TryGet(
                SampleLotteryItemId,
                out var ordinaryDefinition)
                && ordinaryDefinition.RewardPool.Count > 0, ref failures);
            Check("PVF magic box is not a lottery", !definitions.TryGet(
                MagicBoxItemId,
                out _), ref failures);
            Check("PVF magic capsule legacy definition", definitions.TryGet(
                MagicCapsuleItemId,
                out var magicCapsuleDefinition)
                && magicCapsuleDefinition.StackableType == StackableItemProvider.LegacyType
                && magicCapsuleDefinition.RewardPool.Count == 2
                && magicCapsuleDefinition.RewardPool.Any(reward =>
                    reward.ItemId == MagicCapsulePrimaryRewardItemId
                    && reward.Weight == 80000
                    && reward.Count == 1)
                && magicCapsuleDefinition.RewardPool.Any(reward =>
                    reward.ItemId == MagicCapsuleSecondaryRewardItemId
                    && reward.Weight == 20000
                    && reward.Count == 1), ref failures);
            Check("hero lottery gold cost comes from PVF", definitions.TryGet(
                HeroLotteryItemId,
                out var heroDefinition)
                && heroDefinition.GoldCost > 0, ref failures);
            Check("ancient hero lottery gold cost comes from PVF", definitions.TryGet(
                AncientHeroLotteryItemId,
                out var ancientDefinition)
                && ancientDefinition.GoldCost > 0, ref failures);
            Check("lottery required item comes from PVF", definitions.TryGet(
                RequiredItemLotteryItemId,
                out var requiredItemDefinition)
                && requiredItemDefinition.RequiredItemTemplateId == RequiredLotteryMaterialItemId
                && requiredItemDefinition.RequiredItemCount == 1, ref failures);
            var syntheticItemId = 7654321;
            var syntheticGoldCost = 1234567;
            var syntheticLottery = new PvfLib.StackableItemFile
            {
                Name = "selftest data driven lottery",
                StackableType = "`[upgradable legacy]` 1",
                LotteryUseCost = syntheticGoldCost,
            };
            syntheticLottery.UpgradableLegacyRewards.Add(new PvfLib.BoosterRewardEntry
            {
                ItemId = SampleRewardItemId,
                Weight = 10000,
                Count = 1,
            });
            Check("arbitrary lottery cost comes only from PVF", LotteryItemDefinitionProvider.TryBuild(
                syntheticItemId,
                syntheticLottery,
                out var syntheticDefinition)
                && syntheticDefinition.ItemTemplateId == syntheticItemId
                && syntheticDefinition.GoldCost == syntheticGoldCost, ref failures);
            Check("ordinary stackable is not a lottery", !definitions.TryGet(2600014, out _), ref failures);

            Check("direct fast open uses double below cap", LotteryOpenPlanner.ResolveDirectFastOpen(
                true,
                true,
                LotteryDoubleRewardPolicy.DailyLimit - 1).UseDoubleReward, ref failures);
            Check("direct fast open falls back at cap", LotteryOpenPlanner.ResolveDirectFastOpen(
                true,
                true,
                LotteryDoubleRewardPolicy.DailyLimit).ShouldSendRegularPhaseStart, ref failures);
            Check("confirmed open never consumes double", !LotteryOpenPlanner.ResolveDirectFastOpen(
                false,
                true,
                0).UseDoubleReward, ref failures);

            var now = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc);
            var sessions = new LotteryOpenSessionCoordinator(
                TimeSpan.FromMinutes(2),
                () => now);
            var sessionId = Guid.NewGuid();
            sessions.Set(sessionId, LotterySlot, LotteryOpenPlan.DirectDoubleReward(0));
            Check("pending open keeps slot and plan", sessions.TryTake(
                sessionId,
                LotterySlot,
                out var pending)
                && pending.OpenPlan.UseDoubleReward, ref failures);
            sessions.Set(sessionId, LotterySlot);
            now = now.AddMinutes(3);
            Check("pending open expires", !sessions.TryTake(sessionId, null, out _), ref failures);
        }

        private static void TestIndependentService(ref int failures)
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "lottery-item-service-selftest.db");
            DeleteTempDatabase(databasePath);
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            Seed(databasePath);

            var dailyReset = new DailyResetService(databasePath, ServerPaths.SchemaFilePath);
            var doublePolicy = new LotteryDoubleRewardPolicy(dailyReset, connectionString);
            var definitions = new LotteryItemDefinitionProvider();
            var service = new LotteryItemOpenService(
                connectionString,
                definitions,
                doublePolicy);
            var planner = new LotteryOpenPlanner(doublePolicy);
            var inventory = CreateLotteryInventory();
            var hasHeroDefinition = definitions.TryGet(
                HeroLotteryItemId,
                out var heroDefinition);
            var hasAncientDefinition = definitions.TryGet(
                AncientHeroLotteryItemId,
                out var ancientDefinition);
            Check("service reads hero pot cost from PVF", hasHeroDefinition
                && heroDefinition.GoldCost > 0, ref failures);
            Check("service reads ancient hero pot cost from PVF", hasAncientDefinition
                && ancientDefinition.GoldCost > 0, ref failures);
            var heroGoldCost = heroDefinition?.GoldCost ?? 0;
            var ancientGoldCost = ancientDefinition?.GoldCost ?? 0;

            Check("generic booster path rejects lottery type", !InventorySpecialConsumableService.TryUseBoosterItem(
                inventory,
                new BoosterUseRequest
                {
                    SlotIndex = LotterySlot,
                    SelectedItemTemplateIds = Array.Empty<int>(),
                },
                null,
                RejectingInventoryOverflowRewardSink.Instance,
                out _), ref failures);
            Check("generic rejection does not consume pot", LoadStackCount(
                inventory,
                LotterySlot,
                SampleLotteryItemId) == 1, ref failures);

            Check("normal lottery precheck", service.CanOpen(
                inventory,
                LotterySlot,
                out var source)
                && source.ItemTemplateId == SampleLotteryItemId, ref failures);
            Check("normal lottery opens through dedicated service", service.TryOpen(
                inventory,
                LotterySlot,
                false,
                RejectingInventoryOverflowRewardSink.Instance,
                out var normalResult), ref failures);
            Check("normal lottery consumes one and grants reward", normalResult != null
                && normalResult.SourceRemainingStackCount == 0
                && normalResult.Rewards.Count > 0
                && !normalResult.UsedDoubleReward, ref failures);

            Check("magic capsule phase0 precheck", service.CanOpen(
                inventory,
                MagicCapsuleSlot,
                out var magicCapsuleSource)
                && magicCapsuleSource.ItemTemplateId == MagicCapsuleItemId, ref failures);
            Check("magic capsule opens and consumes one", service.TryOpen(
                inventory,
                MagicCapsuleSlot,
                false,
                RejectingInventoryOverflowRewardSink.Instance,
                out var magicCapsuleResult)
                && magicCapsuleResult.SourceRemainingStackCount == 0
                && magicCapsuleResult.Rewards.Count == 1
                && (magicCapsuleResult.Rewards[0].ItemTemplateId == MagicCapsulePrimaryRewardItemId
                    || magicCapsuleResult.Rewards[0].ItemTemplateId == MagicCapsuleSecondaryRewardItemId),
                ref failures);
            Check("PVF increase chance lottery definition", definitions.TryGet(
                0x0098B414,
                out var increaseChanceDefinition)
                && increaseChanceDefinition.UsesIncreaseChanceProgress
                && increaseChanceDefinition.ProgressResetCount == 9
                && increaseChanceDefinition.ProgressResetGoldCost == 2000000
                && increaseChanceDefinition.RewardPool.Count == 10,
                ref failures);

            var startConcurrentOpen = new ManualResetEventSlim(false);
            var concurrentResults = new bool[2];
            var concurrentSync = new object();
            var concurrentTasks = Enumerable.Range(0, concurrentResults.Length)
                .Select(index => Task.Run(() =>
                {
                    startConcurrentOpen.Wait();
                    lock (concurrentSync)
                    {
                        concurrentResults[index] = service.TryOpen(
                            inventory,
                            ConcurrentLotterySlot,
                            false,
                            RejectingInventoryOverflowRewardSink.Instance,
                            out _);
                    }
                }))
                .ToArray();
            startConcurrentOpen.Set();
            Task.WaitAll(concurrentTasks);
            startConcurrentOpen.Dispose();
            Check("concurrent open consumes a single source exactly once",
                concurrentResults.Count(value => value) == 1
                && LoadStackCount(
                    inventory,
                    ConcurrentLotterySlot,
                    SampleLotteryItemId) == -1,
                ref failures);

            Check("upgradable legacy opens through dedicated service", service.TryOpen(
                inventory,
                UpgradableLegacySlot,
                false,
                RejectingInventoryOverflowRewardSink.Instance,
                out var legacyResult)
                && legacyResult.Rewards.Count > 0, ref failures);

            Check("hero pot rejects insufficient gold", !service.CanOpen(
                inventory,
                HeroLotterySlot,
                out _), ref failures);
            SetGold(inventory, heroGoldCost);
            Check("hero pot accepts exact PVF gold cost", service.CanOpen(
                inventory,
                HeroLotterySlot,
                out _), ref failures);
            Check("hero pot deducts gold without exchange material", service.TryOpen(
                inventory,
                HeroLotterySlot,
                false,
                RejectingInventoryOverflowRewardSink.Instance,
                out var heroResult)
                && heroResult.ConsumedGold == heroGoldCost
                && heroResult.UpdatedGold == 0, ref failures);

            SetGold(inventory, ancientGoldCost);
            Check("ancient hero pot deducts PVF gold cost", service.TryOpen(
                inventory,
                AncientHeroLotterySlot,
                false,
                RejectingInventoryOverflowRewardSink.Instance,
                out var ancientResult)
                && ancientResult.ConsumedGold == ancientGoldCost
                && ancientResult.UpdatedGold == 0, ref failures);

            Check("required-item lottery precheck accepts exact material", service.CanOpen(
                inventory,
                RequiredItemLotterySlot,
                out _), ref failures);
            Check("required-item lottery consumes PVF material", service.TryOpen(
                inventory,
                RequiredItemLotterySlot,
                false,
                RejectingInventoryOverflowRewardSink.Instance,
                out var requiredItemResult)
                && requiredItemResult.ConsumedRequiredItemTemplateId == RequiredLotteryMaterialItemId
                && requiredItemResult.ConsumedRequiredItemCount == 1
                && requiredItemResult.RequiredItemChangedSlots.Contains(RequiredItemSlot)
                && inventory.CountMainItem(RequiredLotteryMaterialItemId) == 0, ref failures);

            var missingMaterialInventory = new InventoryService(CharacterId, AccountId);
            missingMaterialInventory.SetListParam16(InventoryListType.Main, 24);
            AttachStackable(
                missingMaterialInventory,
                RequiredItemLotterySlot,
                RequiredItemLotteryItemId,
                1);
            Check("required-item lottery rejects missing material", !service.CanOpen(
                missingMaterialInventory,
                RequiredItemLotterySlot,
                out _), ref failures);
            Check("missing material does not consume lottery item", LoadStackCount(
                missingMaterialInventory,
                RequiredItemLotterySlot,
                RequiredItemLotteryItemId) == 1, ref failures);

            var firstDoublePlan = planner.Resolve(CharacterId, AccountId, true);
            Check("active contract plans double open", firstDoublePlan.UseDoubleReward, ref failures);
            Check("double open grants two result units", service.TryOpen(
                inventory,
                DoubleLotterySlot,
                firstDoublePlan.UseDoubleReward,
                RejectingInventoryOverflowRewardSink.Instance,
                out var doubleResult)
                && doubleResult.UsedDoubleReward
                && doubleResult.Rewards.Sum(reward => Math.Max(1, reward.GrantedCount)) == 2, ref failures);
            Check("double open consumes one daily use", doublePolicy.GetUsedCount(CharacterId) == 1, ref failures);

            for (var index = 1; index < LotteryDoubleRewardPolicy.DailyLimit; index++)
            {
                var plan = planner.Resolve(CharacterId, AccountId, true);
                Check($"double plan remains active #{index + 1}", plan.UseDoubleReward, ref failures);
                Check($"double open succeeds #{index + 1}", service.TryOpen(
                    inventory,
                    DoubleLotterySlot,
                    plan.UseDoubleReward,
                    RejectingInventoryOverflowRewardSink.Instance,
                    out _), ref failures);
            }

            Check("daily double count reaches cap", doublePolicy.GetUsedCount(CharacterId)
                == LotteryDoubleRewardPolicy.DailyLimit, ref failures);
            var remainingBeforeRejectedDouble = LoadStackCount(
                inventory,
                DoubleLotterySlot,
                SampleLotteryItemId);
            Check("stale double plan above cap falls back atomically", service.TryOpen(
                inventory,
                DoubleLotterySlot,
                true,
                RejectingInventoryOverflowRewardSink.Instance,
                out var staleDoubleResult)
                && staleDoubleResult != null
                && !staleDoubleResult.UsedDoubleReward
                && staleDoubleResult.Rewards.Sum(reward => Math.Max(1, reward.GrantedCount)) == 1
                && LoadStackCount(inventory, DoubleLotterySlot, SampleLotteryItemId)
                    == remainingBeforeRejectedDouble - 1
                && doublePolicy.GetUsedCount(CharacterId) == LotteryDoubleRewardPolicy.DailyLimit,
                ref failures);

            var cappedPlan = planner.Resolve(CharacterId, AccountId, true);
            Check("planner falls back to regular phase after cap", cappedPlan.ShouldSendRegularPhaseStart
                && !cappedPlan.UseDoubleReward, ref failures);
            Check("regular open still succeeds after cap", service.TryOpen(
                inventory,
                DoubleLotterySlot,
                false,
                RejectingInventoryOverflowRewardSink.Instance,
                out var cappedRegularResult)
                && cappedRegularResult.Rewards.Sum(reward => Math.Max(1, reward.GrantedCount)) == 1, ref failures);

            var serviceData = PremiumService.BuildPremiumServiceData(
                connectionString,
                AccountId,
                doublePolicy.BuildPremiumServiceUsage(CharacterId));
            Check("premium payload is unchanged length", serviceData.Length == 74, ref failures);
            Check("premium payload carries lottery slot usage", BitConverter.ToInt32(
                serviceData,
                10 + LotteryDoubleRewardPolicy.PremiumServiceSlot * 9)
                == LotteryDoubleRewardPolicy.DailyLimit, ref failures);

            DeleteTempDatabase(databasePath);
        }

        private static void Seed(string databasePath)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'lottery-item-service-selftest', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'lottery-item-service-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 0, 24);

INSERT OR REPLACE INTO account_premiums (account_id, premium_type, end_time)
VALUES (@accountId, @premiumType, @endTime);
";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue(
                        "@premiumType",
                        DevilContractCatalog.SlotToPremiumType(
                            LotteryDoubleRewardPolicy.PremiumServiceSlot));
                    command.Parameters.AddWithValue(
                        "@endTime",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static InventoryService CreateLotteryInventory()
        {
            var inventory = new InventoryService(CharacterId, AccountId);
            inventory.SetListParam16(InventoryListType.Main, 24);
            AttachStackable(inventory, LotterySlot, SampleLotteryItemId, 1);
            AttachStackable(inventory, DoubleLotterySlot, SampleLotteryItemId, 12);
            AttachStackable(inventory, UpgradableLegacySlot, SampleLotteryItemId, 1);
            AttachStackable(inventory, HeroLotterySlot, HeroLotteryItemId, 1);
            AttachStackable(inventory, AncientHeroLotterySlot, AncientHeroLotteryItemId, 1);
            AttachStackable(inventory, ConcurrentLotterySlot, SampleLotteryItemId, 1);
            AttachStackable(inventory, RequiredItemLotterySlot, RequiredItemLotteryItemId, 1);
            AttachStackable(inventory, MagicCapsuleSlot, MagicCapsuleItemId, 1);
            AttachStackable(inventory, RequiredItemSlot, RequiredLotteryMaterialItemId, 1);
            inventory.ClearDirtyState();
            return inventory;
        }

        private static void AttachStackable(
            InventoryService inventory,
            short slotIndex,
            int itemTemplateId,
            int count)
        {
            if (!InventoryCreateService.TryCreateCore(
                    itemTemplateId,
                    ItemCreateReason.Unknown,
                    count,
                    out var core))
            {
                throw new InvalidOperationException(
                    $"Unable to create PVF-backed lottery fixture item {itemTemplateId}.");
            }

            core.Count = count;
            core.ExpireTime = 0;
            inventory.AttachItem(InventoryListType.Main, slotIndex, core);
        }

        private static void SetGold(InventoryService inventory, int gold)
        {
            inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, gold);
        }

        private static int LoadStackCount(
            InventoryService inventory,
            short slotIndex,
            int itemTemplateId)
        {
            var item = inventory.GetItem(InventoryListType.Main, slotIndex);
            return item != null && item.ItemId == itemTemplateId
                ? item.Count
                : -1;
        }

        private static void DeleteTempDatabase(string path)
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file))
                    File.Delete(file);
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
