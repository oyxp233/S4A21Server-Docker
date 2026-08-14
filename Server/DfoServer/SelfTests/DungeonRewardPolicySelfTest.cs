using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    public static class DungeonRewardPolicySelfTest
    {
        private static readonly string[] QuestTrainingDungeonPaths =
        {
            "dungeon/quest/training.dgn",
            "dungeon/quest/training2.dgn",
            "dungeon/quest/training_asura.dgn",
            "dungeon/quest/training_striker.dgn",
            "dungeon/quest/training_weaponmaster.dgn",
        };

        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_REWARD_POLICY selftest ===");
            var failures = 0;

            var trainingEntries = GameWorld.Dungeon.LoadDungeonLstFile().Entries
                .Where(entry => DungeonRewardPolicyData.IsInteractiveTrainingAssetPath(
                    entry.FilePath))
                .ToList();
            Check(
                "dungeon.lst exposes one interactive training-room asset",
                trainingEntries.Count == 1,
                ref failures);
            if (trainingEntries.Count != 1)
                return failures == 0 ? 1 : failures;

            var trainingEntry = trainingEntries[0];
            var loaded = GameWorld.Dungeon.LoadDungeonFileWithPath(trainingEntry.Id);
            Check(
                "interactive training-room path and DGN structure agree",
                DungeonRewardPolicyData.IsInteractiveTrainingConfiguration(
                    loaded.FilePath,
                    loaded.File),
                ref failures);

            var trainingPolicy = DungeonRewardPolicyData.Resolve(trainingEntry.Id);
            Check(
                "interactive training-room policy disables rewards and progression",
                trainingPolicy.Kind == DungeonRewardPolicyKind.InteractiveTraining
                && !trainingPolicy.AllowsMonsterExperience
                && !trainingPolicy.AllowsMonsterDrops
                && !trainingPolicy.AllowsQuestDrops
                && !trainingPolicy.AllowsQuestProgress
                && !trainingPolicy.AllowsPetExperience
                && !trainingPolicy.AllowsClearCommit
                && !trainingPolicy.AllowsSettlement,
                ref failures);
            var trainingInteraction = DungeonInteractionPolicy.Resolve(trainingPolicy);
            Check(
                "interactive training-room policy rejects parties and persistent item mutation",
                !trainingInteraction.AllowsPartyEntry
                && !trainingInteraction.AllowsItemDiscard
                && !trainingInteraction.ConsumesStackableItems
                && trainingInteraction.AllowsPartyState(isInParty: false)
                && !trainingInteraction.AllowsPartyState(isInParty: true),
                ref failures);

            var questTrainingFilesRemainStandard = true;
            foreach (var path in QuestTrainingDungeonPaths)
            {
                var dungeonFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(path));
                if (DungeonRewardPolicyData.IsInteractiveTrainingAssetPath(path)
                    || DungeonRewardPolicyData.IsInteractiveTrainingConfiguration(
                        path,
                        dungeonFile))
                {
                    questTrainingFilesRemainStandard = false;
                    break;
                }
            }
            Check(
                "quest training dungeons are not interactive no-reward rooms",
                questTrainingFilesRemainStandard,
                ref failures);

            var antonRaid = DungeonRewardPolicyData.Resolve(210);
            Check(
                "Anton raid policy disables every monster-death item drop while keeping progression",
                antonRaid.Kind == DungeonRewardPolicyKind.AntonRaid
                && antonRaid.AllowsMonsterExperience
                && !antonRaid.AllowsMonsterDrops
                && !antonRaid.AllowsQuestDrops
                && antonRaid.AllowsQuestProgress
                && antonRaid.AllowsPetExperience
                && antonRaid.AllowsClearCommit
                && antonRaid.AllowsSettlement,
                ref failures);
            Check(
                "Anton raid policy covers both phases without matching ordinary dungeons",
                DungeonRewardPolicyData.IsAntonRaidDungeonId(210)
                && DungeonRewardPolicyData.IsAntonRaidDungeonId(215)
                && DungeonRewardPolicyData.IsAntonRaidDungeonId(218)
                && DungeonRewardPolicyData.IsAntonRaidDungeonId(224)
                && !DungeonRewardPolicyData.IsAntonRaidDungeonId(217)
                && !DungeonRewardPolicyData.IsAntonRaidDungeonId(225),
                ref failures);
            var standard = DungeonRewardPolicy.Standard;
            Check(
                "standard policy keeps all existing reward and progression paths",
                standard.Kind == DungeonRewardPolicyKind.Standard
                && standard.AllowsMonsterExperience
                && standard.AllowsMonsterDrops
                && standard.AllowsQuestDrops
                && standard.AllowsQuestProgress
                && standard.AllowsPetExperience
                && standard.AllowsClearCommit
                && standard.AllowsSettlement,
                ref failures);
            var standardInteraction = DungeonInteractionPolicy.Resolve(standard);
            Check(
                "standard dungeon policy keeps party and inventory behavior",
                standardInteraction.AllowsPartyEntry
                && standardInteraction.AllowsItemDiscard
                && standardInteraction.ConsumesStackableItems
                && standardInteraction.AllowsPartyState(isInParty: false)
                && standardInteraction.AllowsPartyState(isInParty: true),
                ref failures);

            const short consumableSlot = 24;
            const int consumableItemId = 10088630;
            const int consumableCount = 3;
            var inventory = new InventoryService(990601, 990601);
            Check(
                "training interaction fixture seeds a physical consumable stack",
                inventory.SetItem(
                    InventoryListType.Main,
                    consumableSlot,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindConsumable,
                        ItemId = consumableItemId,
                        Count = consumableCount,
                    }),
                ref failures);
            var trainingUseHandled = InventoryHandler
                .TryBuildDungeonUseStackableResponsePlan(
                    trainingPolicy,
                    inventory,
                    InventoryListType.Main,
                    consumableSlot,
                    consumableCount,
                    consumableItemId,
                    out var trainingUsePlan);
            Check(
                "training consumable use succeeds without reducing the owned stack",
                trainingUseHandled
                && trainingUsePlan?.AckBody?.Length == 11
                && trainingUsePlan.AckBody[0] == 0x00
                && trainingUsePlan.AckBody[1] == 0x00
                && trainingUsePlan.AckBody[2] == (byte)InventoryListType.Main
                && BitConverter.ToInt32(trainingUsePlan.AckBody, 3) == consumableCount
                && BitConverter.ToInt32(trainingUsePlan.AckBody, 7) == consumableItemId
                && trainingUsePlan.Accepted
                && !trainingUsePlan.RefreshSourceSlot
                && inventory.GetItem(InventoryListType.Main, consumableSlot)?.Count
                    == consumableCount,
                ref failures);
            var deleteBody = new byte[]
            {
                (byte)InventoryListType.Main,
                (byte)(consumableSlot & 0xFF),
                (byte)((consumableSlot >> 8) & 0xFF),
                1,
                0,
            };
            var trainingDeleteHandled = InventoryHandler
                .TryBuildDungeonDeleteItemResponsePlan(
                    trainingPolicy,
                    deleteBody,
                    out var deleteRejection,
                    out var rejectedListType);
            Check(
                "training discard is rejected before inventory mutation",
                trainingDeleteHandled
                && rejectedListType == InventoryListType.Main
                && deleteRejection != null
                && deleteRejection.SequenceEqual(
                    new byte[]
                    {
                        0x00,
                        0x17,
                        (byte)InventoryListType.Main,
                    })
                && inventory.GetItem(InventoryListType.Main, consumableSlot)?.Count
                    == consumableCount,
                ref failures);
            const short equipmentSlot = 25;
            var equipmentSeeded = inventory.SetItem(
                InventoryListType.Main,
                equipmentSlot,
                new ItemCore
                {
                    ItemKind = ItemCore.KindEquipment,
                    ItemId = 1,
                    Uid = 1,
                });
            var equipmentUseHandled = InventoryHandler
                .TryBuildDungeonUseStackableResponsePlan(
                    trainingPolicy,
                    inventory,
                    InventoryListType.Main,
                    equipmentSlot,
                    1,
                    1,
                    out var equipmentUsePlan);
            Check(
                "training free-use authorization rejects a non-stackable item",
                equipmentSeeded
                && equipmentUseHandled
                && equipmentUsePlan?.AckBody?[0] == 0
                && !equipmentUsePlan.Accepted
                && !equipmentUsePlan.RefreshSourceSlot
                && inventory.GetItem(InventoryListType.Main, equipmentSlot)?.Uid == 1,
                ref failures);
            Check(
                "standard dungeons do not intercept persistent consumable use",
                !InventoryHandler.TryBuildDungeonUseStackableResponsePlan(
                    standard,
                    inventory,
                    InventoryListType.Main,
                    consumableSlot,
                    consumableCount,
                    consumableItemId,
                    out _),
                ref failures);
            Check(
                "standard dungeons do not intercept item discard",
                !InventoryHandler.TryBuildDungeonDeleteItemResponsePlan(
                    standard,
                    deleteBody,
                    out _,
                    out _),
                ref failures);
            var ordinaryConsumed = InventoryDeleteService.TryUseStackableForClient(
                inventory,
                InventoryListType.Main,
                consumableSlot,
                consumableItemId,
                out _);
            Check(
                "ordinary consumable use still reduces the owned stack",
                ordinaryConsumed
                && inventory.GetItem(InventoryListType.Main, consumableSlot)?.Count
                    == consumableCount - 1,
                ref failures);

            var sharedInstance = new DungeonInstance(
                checked((short)trainingEntry.Id),
                0,
                trainingPolicy);
            var leaderRun = new DungeonRun(
                sharedInstance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var memberRun = new DungeonRun(
                sharedInstance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            Check(
                "party participants share the frozen physical-instance policy",
                ReferenceEquals(leaderRun.Instance, memberRun.Instance)
                && ReferenceEquals(leaderRun.RewardPolicy, memberRun.RewardPolicy)
                && leaderRun.RewardPolicy.Kind
                    == DungeonRewardPolicyKind.InteractiveTraining,
                ref failures);

            using var client = new TcpClient();
            var session = new EnhancedClientSession(
                client,
                new GamePacketHeader());
            DungeonRunLifecycle.BeginRun(
                session,
                trainingEntry.Id,
                difficulty: 0);
            Check(
                "normal run lifecycle freezes the resolved training policy",
                session.Player.CurrentRun != null
                && session.Player.CurrentRun.DungeonId == trainingEntry.Id
                && session.Player.CurrentRun.RewardPolicy.Kind
                    == DungeonRewardPolicyKind.InteractiveTraining,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "DUNGEON_REWARD_POLICY selftest passed."
                    : $"DUNGEON_REWARD_POLICY selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
