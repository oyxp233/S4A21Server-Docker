using System;
using System.Collections.Generic;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class DungeonNpcItemDropSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_NPC_ITEM_DROP selftest ===");
            var failures = 0;

            VerifyNpcItemDropPvfData(ref failures);
            VerifyTemplateSceneDrop(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyNpcItemDropPvfData(ref int failures)
        {
            var action = ActFile.Parse(@"
[BEHAVIOR]
[NPC ITEM DROP]
`particle.ptl`
[/BEHAVIOR]");
            Check("ACT parser detects nested NPC ITEM DROP behavior",
                action.HasNpcItemDrop,
                ref failures);

            Check("non-item ACT does not report NPC ITEM DROP",
                !ActFile.Parse("[BEHAVIOR]\n[DIALOG]\n`test`\n[/BEHAVIOR]")
                    .HasNpcItemDrop,
                ref failures);

            var resolved = DungeonNpcItemDropData.TryResolve(
                19006,
                out var scene,
                out var rejectReason);
            Check("time illusion room resolves one PVF NPC item drop action",
                resolved
                && scene != null
                && scene.MapId == 19006
                && scene.ObjectCode == 48548
                && scene.X == 448
                && scene.Y == 248
                && scene.ActionPath.EndsWith(
                    "action/bossunique_siran.act",
                    StringComparison.OrdinalIgnoreCase),
                ref failures);
            Check("resolved NPC item drop has no ambiguity diagnostic",
                resolved && string.IsNullOrEmpty(rejectReason),
                ref failures);

            Check("get-item-check quest parses explicit dungeon scope and items",
                QuestData.TryGetNpcItemDropQuestTarget(
                    2358,
                    3066,
                    0,
                    out var target)
                && target.DungeonId == 3066
                && target.Difficulty == -1
                && target.ItemIds.Count == 30,
                ref failures);
            Check("get-item-check quest rejects an unrelated dungeon",
                !QuestData.TryGetNpcItemDropQuestTarget(
                    2358,
                    3065,
                    0,
                    out _),
                ref failures);

            var activation = QuestActivationId.New();
            var snapshot = QuestRunSnapshot.Capture(new List<ActiveQuest>
            {
                new ActiveQuest
                {
                    QuestId = 2358,
                    TriggerValue = 1,
                    ActivationId = activation,
                },
            });
            VerifyNpcDropJobCandidates(snapshot, 0, 5, "swordman", ref failures);
            VerifyNpcDropJobCandidates(snapshot, 1, 6, "fighter", ref failures);
            VerifyNpcDropJobCandidates(snapshot, 2, 5, "gunner", ref failures);
            VerifyNpcDropJobCandidates(snapshot, 3, 5, "mage", ref failures);
            VerifyNpcDropJobCandidates(snapshot, 4, 4, "priest", ref failures);
            VerifyNpcDropJobCandidates(snapshot, 6, 5, "thief", ref failures);

            var completedSnapshot = QuestRunSnapshot.Capture(
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        QuestId = 2358,
                        TriggerValue = 0,
                        ActivationId = activation,
                    },
                });
            Check("completed get-item-check quest no longer matches NPC drop",
                DungeonNpcItemDropCoordinator.ResolveQuestMatches(
                    completedSnapshot,
                    3066,
                    0,
                    0).Count == 0,
                ref failures);
            Check("quest accepted after entry is absent from the NPC drop snapshot",
                DungeonNpcItemDropCoordinator.ResolveQuestMatches(
                    QuestRunSnapshot.Empty,
                    3066,
                    0,
                    0).Count == 0,
                ref failures);

            var run = new DungeonRun();
            var replacementActivation = QuestActivationId.New();
            Check("NPC item drop run marker accepts an activation once",
                run.TryMarkNpcItemDropGenerated(activation),
                ref failures);
            Check("NPC item drop run marker rejects a duplicate command",
                !run.TryMarkNpcItemDropGenerated(activation),
                ref failures);
            Check("NPC marker does not conflate a reaccepted activation",
                run.TryMarkNpcItemDropGenerated(replacementActivation),
                ref failures);
            run.UnmarkNpcItemDropGenerated(activation);
            Check("failed NPC item registration can release its run marker",
                run.TryMarkNpcItemDropGenerated(activation),
                ref failures);

            Check("EVENT_NPC_DROP_ITEM command keeps extracted packet id",
                (ushort)CmdPacketType.EVENT_NPC_DROP_ITEM_ == 0x0253,
                ref failures);
            var success = CommonPacketBodyBuilder.BuildSuccessAck();
            Check("EVENT_NPC_DROP_ITEM success ACK is one byte 01",
                success.Length == 1 && success[0] == 1,
                ref failures);
        }

        private static void VerifyNpcDropJobCandidates(
            QuestRunSnapshot snapshot,
            byte job,
            int expectedCount,
            string label,
            ref int failures)
        {
            var matches = DungeonNpcItemDropCoordinator.ResolveQuestMatches(
                snapshot,
                3066,
                0,
                job);
            Check($"NPC item drop filters exact {label} usable-job candidates",
                matches.Count == 1
                && matches[0].QuestId == 2358
                && matches[0].ActivationId.IsValid
                && matches[0].ItemIds.Count == expectedCount,
                ref failures);
        }

        private static void VerifyTemplateSceneDrop(ref int failures)
        {
            var drops = new DropService();
            var run = new DungeonRun();
            var registered = drops.TryRegisterTemplateDrop(
                run,
                101030189,
                1,
                out var drop);
            Check("fixed-template scene drop registers in the dungeon run",
                registered
                && drop.SceneSlot == 1
                && drop.TemplateId == 101030189
                && drop.StackCount == 1
                && run.Drops.TryGetValue(drop.SceneSlot, out var registeredDrop)
                && registeredDrop.TemplateId == drop.TemplateId,
                ref failures);

            var body = DropItemBuilder.BuildDrop(
                0x03F1,
                448,
                248,
                drop,
                0x03F1);
            Check("NPC scene drop notification writes actor, PVF position and item",
                body.Length == 48
                && BitConverter.ToUInt16(body, 0) == 0x03F1
                && BitConverter.ToUInt16(body, 2) == 448
                && BitConverter.ToUInt16(body, 4) == 248
                && BitConverter.ToUInt32(body, 8) == 101030189
                && BitConverter.ToUInt16(body, 46) == 0x03F1,
                ref failures);

            var beforeSlot = run.SceneSlotCounter;
            Check("invalid fixed-template drop is rejected without consuming a slot",
                !drops.TryRegisterTemplateDrop(run, int.MaxValue, 1, out _)
                && run.SceneSlotCounter == beforeSlot,
                ref failures);
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
