using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace DfoServer.GameWorld
{
    // The circle-dungeon box is configured outside the QST reward payload.
    // Keep this PVF-owned relation separate from ordinary quest parsing while
    // returning the shared QuestRewardItem shape to the transaction owner.
    internal static class CircleDungeonRewardCatalog
    {
        private static readonly Lazy<IReadOnlyDictionary<int, IReadOnlyList<QuestRewardItem>>>
            RewardsByQuest = new Lazy<IReadOnlyDictionary<int, IReadOnlyList<QuestRewardItem>>>(
                Load,
                isThreadSafe: true);

        internal static bool TryGetRewards(
            int questId,
            out IReadOnlyList<QuestRewardItem> rewards)
        {
            rewards = Array.Empty<QuestRewardItem>();
            if (questId <= 0
                || !RewardsByQuest.Value.TryGetValue(questId, out var configured)
                || configured == null
                || configured.Count == 0)
            {
                return false;
            }

            rewards = configured;
            return true;
        }

        private static IReadOnlyDictionary<int, IReadOnlyList<QuestRewardItem>>
            Load()
        {
            var byWorldmap = new Dictionary<int, List<QuestRewardItem>>();
            var questToWorldmap = new Dictionary<int, int>();
            try
            {
                var text = PvfArchiveAccessor.ReadText(
                    "etc/circledungeoninfo.etc");
                Parse(text, questToWorldmap, byWorldmap);

                var result = new Dictionary<int, IReadOnlyList<QuestRewardItem>>();
                foreach (var pair in questToWorldmap)
                {
                    if (!byWorldmap.TryGetValue(pair.Value, out var rewards)
                        || rewards == null
                        || rewards.Count == 0)
                    {
                        continue;
                    }

                    result[pair.Key] = new ReadOnlyCollection<QuestRewardItem>(
                        new List<QuestRewardItem>(rewards));
                }

                FileLogger.Log(
                    $"[CircleDungeonRewardCatalog] loaded: " +
                    $"worldmaps={byWorldmap.Count} questRewards={result.Count}");
                return result;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[CircleDungeonRewardCatalog] load failed: {ex.Message}");
                return new Dictionary<int, IReadOnlyList<QuestRewardItem>>();
            }
        }

        private static void Parse(
            string text,
            IDictionary<int, int> questToWorldmap,
            IDictionary<int, List<QuestRewardItem>> rewardsByWorldmap)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var section = CircleSection.None;
            var currentTag = string.Empty;
            var currentWorldmap = 0;
            var inDungeonList = false;
            foreach (var rawLine in text.Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    var tag = line.Substring(1, line.Length - 2)
                        .Trim()
                        .ToLowerInvariant();
                    if (tag == "circle dungeon list info")
                    {
                        section = CircleSection.DungeonInfo;
                        currentWorldmap = 0;
                        currentTag = string.Empty;
                        inDungeonList = false;
                        continue;
                    }
                    if (tag == "/circle dungeon list info")
                    {
                        section = CircleSection.None;
                        currentTag = string.Empty;
                        inDungeonList = false;
                        continue;
                    }
                    if (tag == "reward data")
                    {
                        section = CircleSection.Reward;
                        currentWorldmap = 0;
                        currentTag = string.Empty;
                        inDungeonList = false;
                        continue;
                    }
                    if (tag == "/reward data")
                    {
                        section = CircleSection.None;
                        currentTag = string.Empty;
                        inDungeonList = false;
                        continue;
                    }

                    currentTag = tag.TrimStart('/');
                    if (currentTag == "dungeon")
                        inDungeonList = !tag.StartsWith("/");
                    if (tag.StartsWith("/"))
                        currentTag = string.Empty;
                    continue;
                }

                if (section == CircleSection.DungeonInfo)
                {
                    if (currentTag == "worldmap index"
                        && TryParseSingleInt(line, out var worldmap))
                    {
                        currentWorldmap = worldmap;
                    }
                    else if (inDungeonList && currentTag == "dungeon")
                    {
                        AddDungeonQuestPairs(
                            line,
                            currentWorldmap,
                            questToWorldmap);
                    }
                }
                else if (section == CircleSection.Reward
                    && currentTag == "worldmap index"
                    && TryParseSingleInt(line, out var rewardWorldmap))
                {
                    currentWorldmap = rewardWorldmap;
                }
                else if (section == CircleSection.Reward
                    && currentTag == "reward int data")
                {
                    AddRewardPairs(
                        line,
                        currentWorldmap,
                        rewardsByWorldmap);
                }
            }
        }

        private static void AddDungeonQuestPairs(
            string line,
            int worldmap,
            IDictionary<int, int> questToWorldmap)
        {
            if (worldmap <= 0)
                return;

            var tokens = SplitTokens(line);
            for (var index = 0; index + 1 < tokens.Length; index += 2)
            {
                if (!TryParseInt(tokens[index], out var dungeonId)
                    || !TryParseInt(tokens[index + 1], out var questId)
                    || dungeonId <= 0
                    || questId <= 0)
                {
                    continue;
                }

                questToWorldmap[questId] = worldmap;
            }
        }

        private static void AddRewardPairs(
            string line,
            int worldmap,
            IDictionary<int, List<QuestRewardItem>> rewardsByWorldmap)
        {
            if (worldmap <= 0)
                return;

            var tokens = SplitTokens(line);
            if (!rewardsByWorldmap.TryGetValue(worldmap, out var rewards))
            {
                rewards = new List<QuestRewardItem>();
                rewardsByWorldmap[worldmap] = rewards;
            }

            for (var index = 0; index + 1 < tokens.Length; index += 2)
            {
                if (!TryParseInt(tokens[index], out var itemId)
                    || !TryParseInt(tokens[index + 1], out var count)
                    || itemId <= 0
                    || count <= 0)
                {
                    continue;
                }

                rewards.Add(new QuestRewardItem
                {
                    ItemId = itemId,
                    Count = count,
                });
            }
        }

        private static string[] SplitTokens(string line)
            => line.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries);

        private static bool TryParseSingleInt(string line, out int value)
        {
            value = 0;
            var tokens = SplitTokens(line);
            return tokens.Length == 1 && TryParseInt(tokens[0], out value);
        }

        private static bool TryParseInt(string token, out int value)
            => int.TryParse(
                token,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);

        private enum CircleSection
        {
            None,
            DungeonInfo,
            Reward,
        }
    }
}
