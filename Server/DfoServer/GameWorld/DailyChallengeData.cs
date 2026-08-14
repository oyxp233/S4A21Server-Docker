using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    internal sealed class DailyChallengeRewardDefinition
    {
        internal int GroupIndex { get; set; }
        internal int RequiredCompletionCount { get; set; }
        internal int ItemId { get; set; }
        internal int ItemCount { get; set; }
    }

    internal static class DailyChallengeData
    {
        private const string ConfigPath = "etc/dailychallengetable.etc";

        private static readonly Lazy<DailyChallengeCatalog> Catalog =
            new Lazy<DailyChallengeCatalog>(LoadCatalog);

        internal static bool IsConfiguredQuest(int questId) =>
            Catalog.Value.QuestIds.Contains(questId);

        internal static bool TryResolveReward(
            int groupIndex,
            int characterLevel,
            int activeEntryCount,
            out DailyChallengeRewardDefinition reward)
        {
            reward = null;
            if (groupIndex < 0 || groupIndex >= Catalog.Value.Groups.Count)
                return false;

            var group = Catalog.Value.Groups[groupIndex];
            if (characterLevel < group.MinimumLevel || characterLevel > group.MaximumLevel)
                return false;

            DailyChallengeLevelReward levelReward = null;
            foreach (var candidate in group.Rewards)
            {
                if (characterLevel >= candidate.MinimumLevel
                    && characterLevel <= candidate.MaximumLevel)
                {
                    levelReward = candidate;
                    break;
                }
            }

            if (levelReward == null || levelReward.ItemId <= 0 || levelReward.ItemCount <= 0)
                return false;

            var required = group.RequiredCompletionCount;
            if (required <= 0)
                required = group.ResolveActiveSlotCount(characterLevel);
            if (required <= 0)
                required = activeEntryCount;
            if (required <= 0)
                return false;

            reward = new DailyChallengeRewardDefinition
            {
                GroupIndex = groupIndex,
                RequiredCompletionCount = required,
                ItemId = levelReward.ItemId,
                ItemCount = levelReward.ItemCount,
            };
            return true;
        }

        private static DailyChallengeCatalog LoadCatalog()
        {
            var catalog = new DailyChallengeCatalog();
            try
            {
                var text = PvfArchiveAccessor.ReadText(ConfigPath);
                var root = new ScriptParser().Parse(text);
                var groupIndex = 0;
                foreach (var node in root.GetChildren("group"))
                {
                    var group = new DailyChallengeGroupDefinition
                    {
                        GroupIndex = groupIndex++,
                    };

                    var levels = ParseInts(node.GetChild("level")?.GetFirstDataContent(text));
                    if (levels.Count >= 2)
                    {
                        group.MinimumLevel = levels[0];
                        group.MaximumLevel = levels[1];
                    }

                    var required = ParseInts(
                        node.GetChild("reward challenge num")?.GetFirstDataContent(text));
                    if (required.Count > 0)
                        group.RequiredCompletionCount = required[0];

                    var slotCounts = ParseInts(
                        node.GetChild("slot num table")?.GetFirstDataContent(text));
                    for (var index = 0; index + 2 < slotCounts.Count; index += 3)
                    {
                        group.SlotCounts.Add(new DailyChallengeSlotCount
                        {
                            MinimumLevel = slotCounts[index],
                            MaximumLevel = slotCounts[index + 1],
                            Count = slotCounts[index + 2],
                        });
                    }

                    foreach (var slot in node.GetChildren("slot"))
                    {
                        var values = ParseInts(slot.GetFirstDataContent(text));
                        for (var index = 1; index < values.Count; index++)
                        {
                            if (values[index] > 0)
                                catalog.QuestIds.Add(values[index]);
                        }
                    }

                    var rewards = ParseInts(
                        node.GetChild("reward table")?.GetFirstDataContent(text));
                    for (var index = 0; index + 3 < rewards.Count; index += 4)
                    {
                        group.Rewards.Add(new DailyChallengeLevelReward
                        {
                            MinimumLevel = rewards[index],
                            MaximumLevel = rewards[index + 1],
                            ItemId = rewards[index + 2],
                            ItemCount = rewards[index + 3],
                        });
                    }

                    catalog.Groups.Add(group);
                }

                FileLogger.Log(
                    $"[DailyChallengeData] groups={catalog.Groups.Count} "
                    + $"quests={catalog.QuestIds.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DailyChallengeData] failed to load {ConfigPath}: {ex.Message}");
            }

            return catalog;
        }

        private static List<int> ParseInts(string data)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(data))
                return result;

            foreach (var token in data.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, out var value))
                    result.Add(value);
            }

            return result;
        }

        private sealed class DailyChallengeCatalog
        {
            internal List<DailyChallengeGroupDefinition> Groups { get; } =
                new List<DailyChallengeGroupDefinition>();

            internal HashSet<int> QuestIds { get; } = new HashSet<int>();
        }

        private sealed class DailyChallengeGroupDefinition
        {
            internal int GroupIndex { get; set; }
            internal int MinimumLevel { get; set; }
            internal int MaximumLevel { get; set; } = int.MaxValue;
            internal int RequiredCompletionCount { get; set; }
            internal List<DailyChallengeSlotCount> SlotCounts { get; } =
                new List<DailyChallengeSlotCount>();
            internal List<DailyChallengeLevelReward> Rewards { get; } =
                new List<DailyChallengeLevelReward>();

            internal int ResolveActiveSlotCount(int level)
            {
                foreach (var entry in SlotCounts)
                {
                    if (level >= entry.MinimumLevel && level <= entry.MaximumLevel)
                        return entry.Count;
                }

                return 0;
            }
        }

        private sealed class DailyChallengeSlotCount
        {
            internal int MinimumLevel { get; set; }
            internal int MaximumLevel { get; set; }
            internal int Count { get; set; }
        }

        private sealed class DailyChallengeLevelReward
        {
            internal int MinimumLevel { get; set; }
            internal int MaximumLevel { get; set; }
            internal int ItemId { get; set; }
            internal int ItemCount { get; set; }
        }
    }
}
