using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class QuestChainAvailabilitySelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== QUEST_CHAIN_AVAILABILITY selftest ===");
            var failures = 0;
            try
            {
                VerifyPrerequisites(ref failures);
                VerifyCatalog(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] quest chain check threw: {ex}");
                failures++;
            }
            Console.WriteLine(failures == 0
                ? "QUEST_CHAIN_AVAILABILITY selftest passed."
                : $"QUEST_CHAIN_AVAILABILITY selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyPrerequisites(ref int failures)
        {
            var grouped = Parse(
                "[pre required quest]\n10 11\n[/pre required quest]\n"
                + "[pre required quest]\n12\n[/pre required quest]\n");
            Check("repeated prerequisite tags preserve both candidate groups",
                grouped.IsValid && grouped.CompletedQuestGroups.Count == 2
                && grouped.CompletedQuestGroups[0].SequenceEqual(new[] { 10, 11 })
                && grouped.CompletedQuestGroups[1].SequenceEqual(new[] { 12 }),
                ref failures);
            Expect("a partial AND group cannot unlock a quest", grouped,
                State(new[] { 10 }), QuestPrerequisiteBlockReason.MissingCompletedQuest,
                ref failures);
            Expect("completing the AND group unlocks the quest", grouped,
                State(new[] { 10, 11 }), QuestPrerequisiteBlockReason.None, ref failures);
            Expect("completing an alternative OR group unlocks the quest", grouped,
                State(new[] { 12 }), QuestPrerequisiteBlockReason.None, ref failures);

            var answers = Parse(
                "[pre required quest answer]\n10 2\n[/pre required quest answer]\n"
                + "[pre required quest answer]\n11 0\n[/pre required quest answer]\n");
            Check("repeated answer tags preserve every requirement",
                answers.IsValid && answers.RequiredAnswers.Count == 2, ref failures);
            Expect("a cleared quest without its answer flag is insufficient", answers,
                State(new[] { 10, 11 }), QuestPrerequisiteBlockReason.RequiredAnswerMismatch,
                ref failures);
            Expect("a neighboring answer flag cannot unlock the third branch", answers,
                State(flags: new Dictionary<int, int> { [10] = 2, [11] = 1 }),
                QuestPrerequisiteBlockReason.RequiredAnswerMismatch, ref failures);
            Expect("every exact answer flag is required", answers,
                State(flags: new Dictionary<int, int> { [10] = 3, [11] = 1 }),
                QuestPrerequisiteBlockReason.None, ref failures);
            Expect("a missing second answer cannot be ignored", answers,
                State(flags: new Dictionary<int, int> { [10] = 3 }),
                QuestPrerequisiteBlockReason.RequiredAnswerMismatch, ref failures);

            var collision = Parse("[collision quest]\n10\n[/collision quest]\n"
                + "[collision quest]\n11\n[/collision quest]\n");
            Check("repeated collision tags are retained",
                collision.IsValid && collision.CollisionQuestIds.SequenceEqual(new[] { 10, 11 }),
                ref failures);
            Expect("a completed character collision blocks acceptance", collision,
                State(new[] { 10 }), QuestPrerequisiteBlockReason.CharacterCollision,
                ref failures);
            Expect("an active character collision blocks acceptance", collision,
                State(active: new[] { 11 }), QuestPrerequisiteBlockReason.CharacterCollision,
                ref failures);
            Expect("no character collision permits acceptance", collision,
                State(), QuestPrerequisiteBlockReason.None, ref failures);

            var account = Parse(
                "[account collision quest]\n10\n[/account collision quest]\n");
            Expect("an unavailable account snapshot blocks acceptance", account,
                State(), QuestPrerequisiteBlockReason.AccountCollisionStateUnavailable,
                ref failures);
            Expect("an explicitly empty account snapshot permits acceptance", account,
                State(accountCleared: Array.Empty<int>()),
                QuestPrerequisiteBlockReason.None, ref failures);
            Expect("a collision on another character in the account blocks acceptance", account,
                State(accountCleared: new[] { 10 }),
                QuestPrerequisiteBlockReason.AccountCollision, ref failures);

            var empty = Parse("[pre required quest]\n[/pre required quest]\n"
                + "[pre required quest answer]\n[/pre required quest answer]\n"
                + "[collision quest]\n[/collision quest]\n"
                + "[account collision quest]\n[/account collision quest]\n");
            Expect("empty placeholder tags impose no constraints", empty,
                State(), QuestPrerequisiteBlockReason.None, ref failures);

            foreach (var malformed in new[]
            {
                "[pre required quest]\n0\n[/pre required quest]",
                "[pre required quest]\n100\n[/pre required quest]",
                "[pre required quest]\n999\n[/pre required quest]",
                "[pre required quest]\n10 bad\n[/pre required quest]",
                "[pre required quest answer]\n10\n[/pre required quest answer]",
                "[pre required quest answer]\n999 0\n[/pre required quest answer]",
                "[pre required quest answer]\n10 -1\n[/pre required quest answer]",
                "[collision quest]\n-1\n[/collision quest]",
                "[account collision quest]\n100\n[/account collision quest]",
            })
            {
                var definition = Parse(malformed);
                Check("malformed prerequisite fails closed: " + malformed.Replace('\n', ' '),
                    !definition.IsValid
                    && definition.Evaluate(State()).Reason
                        == QuestPrerequisiteBlockReason.InvalidDefinition,
                    ref failures);
            }
        }

        private static void VerifyCatalog(ref int failures)
        {
            var expected = LstFile.Parse(PvfArchiveAccessor.ReadText("n_quest/quest.lst"))
                .Entries.GroupBy(entry => entry.Id).Select(group => group.First()).ToArray();
            var definitions = QuestPrerequisiteCatalog.GetAll();
            Check("production catalog is exactly the distinct quest.lst entries",
                expected.Length > 0
                && QuestCatalog.OrderedIds.SequenceEqual(expected.Select(entry => entry.Id))
                && definitions.Count == expected.Length
                && expected.All(entry => QuestCatalog.TryGetPath(entry.Id, out var path)
                    && path == "n_quest/" + entry.FilePath
                    && definitions.ContainsKey(entry.Id)), ref failures);

            var invalid = definitions.Values.Where(definition => !definition.IsValid).ToArray();
            Check("every invalid catalog definition denies authorization",
                invalid.All(definition => definition.Evaluate(State()).Reason
                    == QuestPrerequisiteBlockReason.InvalidDefinition), ref failures);
            var flags = QuestCatalog.OrderedIds.ToDictionary(id => id, _ => 1);
            var acceptable = QuestData.ComputeAcceptableQuests(
                86, 0, 0, new HashSet<int>(), flags);
            Check("invalid definitions are absent from the acceptable quest projection",
                invalid.All(definition => !acceptable.Contains((ushort)definition.QuestId)),
                ref failures);
            Console.WriteLine($"[CATALOG] catalog={definitions.Count} "
                + $"valid={definitions.Count - invalid.Length} invalid={invalid.Length} "
                + $"answerPrerequisites={definitions.Values.Count(d => d.RequiredAnswers.Count > 0)}");
        }

        private static QuestPrerequisiteDefinition Parse(string text)
            => QuestPrerequisiteDefinition.Parse(100, QuestFile.Parse(text),
                id => id is >= 10 and <= 12 || id == 100);

        private static QuestPrerequisiteEvaluationState State(
            IEnumerable<int> cleared = null, Dictionary<int, int> flags = null,
            IEnumerable<int> active = null, IEnumerable<int> accountCleared = null)
            => new QuestPrerequisiteEvaluationState(
                new HashSet<int>(cleared ?? Array.Empty<int>()), flags,
                new HashSet<int>(active ?? Array.Empty<int>()),
                accountCleared == null ? null : new HashSet<int>(accountCleared));

        private static void Expect(string name, QuestPrerequisiteDefinition definition,
            QuestPrerequisiteEvaluationState state, QuestPrerequisiteBlockReason reason,
            ref int failures)
        {
            var result = definition.Evaluate(state);
            Check(name, definition.IsValid && result.Reason == reason
                && result.IsAllowed == (reason == QuestPrerequisiteBlockReason.None), ref failures);
        }

        private static void Check(string name, bool passed, ref int failures)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed)
                failures++;
        }
    }
}
