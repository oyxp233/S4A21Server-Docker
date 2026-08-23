using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.Game.TitleBook
{
    public sealed class TitleBookStaticDataProvider
    {
        private static readonly string[] CategoryNames = { "general", "specific", "pvp", "despair", "event" };
        private static readonly int[] DefaultCapacities = { 80, 170, 50, 100, 100 };

        private readonly Dictionary<(int Category, int Index), TitleBookSlotDefinition> _slots;
        private readonly Dictionary<int, TitleBookSlotDefinition> _slotsByQuestId;
        private readonly Dictionary<int, TitleBookSlotDefinition> _slotsByAchievementQuestId;
        private readonly Dictionary<int, TitleQuestDefinition> _quests;
        private readonly Dictionary<int, List<TitleQuestDefinition>> _useItemQuestsByItemId;

        private TitleBookStaticDataProvider(
            Dictionary<(int Category, int Index), TitleBookSlotDefinition> slots,
            Dictionary<int, TitleBookSlotDefinition> slotsByQuestId,
            Dictionary<int, TitleBookSlotDefinition> slotsByAchievementQuestId,
            Dictionary<int, TitleQuestDefinition> quests)
        {
            _slots = slots;
            _slotsByQuestId = slotsByQuestId;
            _slotsByAchievementQuestId = slotsByAchievementQuestId;
            _quests = quests;
            _useItemQuestsByItemId = BuildUseItemQuestIndex(quests.Values);
        }

        public static IReadOnlyList<int> CategoryCapacities => DefaultCapacities;

        public static TitleBookStaticDataProvider LoadDefault()
        {
            var titleBookText = ReadTitleBookEtcText();
            if (!string.IsNullOrWhiteSpace(titleBookText))
            {
                var slots = ParseTitleBookEtc(titleBookText);
                var slotsByQuestId = BuildQuestIndex(slots);
                var slotsByAchievementQuestId = BuildAchievementQuestIndex(slots);
                var quests = ParseTitleQuestDefinitions(slots.Values.Select(s => s.QuestId));
                return new TitleBookStaticDataProvider(
                    slots,
                    slotsByQuestId,
                    slotsByAchievementQuestId,
                    quests);
            }

            var fallbackSlots = BuildOpenFallback();
            return new TitleBookStaticDataProvider(
                fallbackSlots,
                BuildQuestIndex(fallbackSlots),
                new Dictionary<int, TitleBookSlotDefinition>(),
                new Dictionary<int, TitleQuestDefinition>());
        }

        public TitleBookSlotDefinition GetSlot(int category, int index)
        {
            if (_slots.TryGetValue((category, index), out var definition))
                return definition;

            return new TitleBookSlotDefinition
            {
                Category = category,
                Index = index,
                SlotType = -1,
                QuestId = -1,
            };
        }

        public bool TryFindByQuestId(int questId, out TitleBookSlotDefinition definition)
        {
            if (_slotsByQuestId.TryGetValue(questId, out definition))
                return true;

            return _slotsByAchievementQuestId.TryGetValue(questId, out definition);
        }

        internal IReadOnlyList<ushort> GetGeneralAchievementQuestIds()
        {
            var result = new List<ushort>();
            var seen = new HashSet<ushort>();
            for (var index = 0; index < DefaultCapacities[0]; index++)
            {
                if (!_slots.TryGetValue((0, index), out var slot)
                    || slot == null
                    || !slot.IsOpen
                    || slot.QuestId <= 0)
                {
                    continue;
                }

                var quest = QuestData.GetQuestFile(slot.QuestId);
                var questIds = QuestData.ParseIntList(quest?.IntData);
                if (questIds.Count == 0)
                    continue;

                var achievementQuestId = questIds[0];
                if (achievementQuestId <= 0
                    || achievementQuestId > ushort.MaxValue
                    || !seen.Add((ushort)achievementQuestId))
                {
                    continue;
                }

                result.Add((ushort)achievementQuestId);
            }

            return result;
        }

        public TitleQuestDefinition GetQuest(int questId)
        {
            return _quests.TryGetValue(questId, out var quest) ? quest : null;
        }

        internal IReadOnlyList<TitleQuestDefinition> GetUseItemQuests(int itemId)
        {
            return _useItemQuestsByItemId.TryGetValue(itemId, out var quests)
                ? quests
                : Array.Empty<TitleQuestDefinition>();
        }

        internal IReadOnlyDictionary<int, ushort> BuildUseItemProgressDeltas(
            IEnumerable<KeyValuePair<int, int>> consumedItems)
        {
            var deltasByQuest = new Dictionary<int, long>();
            foreach (var consumed in consumedItems ?? Enumerable.Empty<KeyValuePair<int, int>>())
            {
                if (consumed.Key <= 0 || consumed.Value <= 0)
                    continue;

                foreach (var quest in GetUseItemQuests(consumed.Key))
                {
                    var progressPerItem = quest.GetUseItemProgressPerItem(consumed.Key);
                    if (progressPerItem <= 0)
                        continue;

                    deltasByQuest.TryGetValue(quest.QuestId, out var current);
                    deltasByQuest[quest.QuestId] = Math.Min(
                        ushort.MaxValue,
                        current + (long)consumed.Value * progressPerItem);
                }
            }

            return deltasByQuest.ToDictionary(
                pair => pair.Key,
                pair => (ushort)pair.Value);
        }

        private static Dictionary<(int Category, int Index), TitleBookSlotDefinition> ParseTitleBookEtc(string text)
        {
            var slots = BuildClosedDefaults();
            var inSection = false;
            var currentCategory = -1;

            foreach (var originalLine in SplitLines(text))
            {
                var line = StripComment(originalLine).Replace("`", "").Trim();
                if (line.Length == 0)
                    continue;

                if (line.Equals("[title collection info]", StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    currentCategory = -1;
                    continue;
                }

                if (!inSection)
                    continue;

                if (line.Equals("[/title collection info]", StringComparison.OrdinalIgnoreCase))
                {
                    inSection = false;
                    currentCategory = -1;
                    continue;
                }

                if (line.StartsWith("[/", StringComparison.Ordinal))
                    continue;

                var tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    continue;

                var category = Array.IndexOf(CategoryNames, tokens[0].ToLowerInvariant());
                if (category >= 0)
                {
                    currentCategory = category;
                    ParseTitleBookSlotTokens(slots, currentCategory, tokens, 2);
                    continue;
                }

                if (currentCategory < 0 || !int.TryParse(tokens[0], out _))
                    continue;

                ParseTitleBookSlotTokens(slots, currentCategory, tokens, 0);
            }

            return slots;
        }

        private static void ParseTitleBookSlotTokens(
            Dictionary<(int Category, int Index), TitleBookSlotDefinition> slots,
            int currentCategory,
            string[] tokens,
            int startIndex)
        {
            var position = startIndex;
            while (position < tokens.Length)
            {
                if (!int.TryParse(tokens[position], out var index))
                    return;
                position++;

                if (position >= tokens.Length || !int.TryParse(tokens[position], out var slotType))
                    return;
                position++;

                var slot = new TitleBookSlotDefinition
                {
                    Category = currentCategory,
                    Index = index,
                    SlotType = slotType,
                    QuestId = -1,
                };

                if (slotType >= 0 && position < tokens.Length && int.TryParse(tokens[position], out var questId))
                {
                    slot.QuestId = questId;
                    position++;
                }

                if (slotType >= 0 && position < tokens.Length && int.TryParse(tokens[position], out var itemCount))
                {
                    position++;
                    for (var i = 0; i < itemCount && position < tokens.Length; i++, position++)
                    {
                        if (int.TryParse(tokens[position], out var itemId))
                            slot.AllowedTitleItemIds.Add(itemId);
                    }
                }

                slots[(currentCategory, index)] = slot;
            }
        }

        private static Dictionary<int, TitleQuestDefinition> ParseTitleQuestDefinitions(IEnumerable<int> questIds)
        {
            var result = new Dictionary<int, TitleQuestDefinition>();
            var requestedQuestIds = new HashSet<int>((questIds ?? Enumerable.Empty<int>()).Where(id => id > 0));
            if (requestedQuestIds.Count == 0)
                return result;

            var configuredRoot = Environment.GetEnvironmentVariable("TITLEBOOK_QUEST_ROOT");
            if (!string.IsNullOrWhiteSpace(configuredRoot) && Directory.Exists(configuredRoot))
            {
                ParseTitleQuestDefinitionsFromFileRoot(configuredRoot, requestedQuestIds, result);
                return result;
            }

            if (TryReadPvfText(out var questListText, "n_quest/quest.lst", "n_Quest/quest.lst"))
            {
                ParseTitleQuestDefinitionsFromPvf(questListText, requestedQuestIds, result);
                return result;
            }

            var fallbackRoot = ResolveTitleQuestRoot();
            if (!string.IsNullOrWhiteSpace(fallbackRoot))
                ParseTitleQuestDefinitionsFromFileRoot(fallbackRoot, requestedQuestIds, result);

            return result;
        }

        private static Dictionary<int, TitleBookSlotDefinition> BuildQuestIndex(
            Dictionary<(int Category, int Index), TitleBookSlotDefinition> slots)
        {
            var result = new Dictionary<int, TitleBookSlotDefinition>();
            for (var category = 0; category < DefaultCapacities.Length; category++)
            {
                for (var index = 0; index < DefaultCapacities[category]; index++)
                {
                    if (!slots.TryGetValue((category, index), out var slot)
                        || slot == null
                        || !slot.IsOpen
                        || slot.QuestId <= 0
                        || result.ContainsKey(slot.QuestId))
                    {
                        continue;
                    }

                    result[slot.QuestId] = slot;
                }
            }

            return result;
        }

        private static Dictionary<int, TitleBookSlotDefinition> BuildAchievementQuestIndex(
            Dictionary<(int Category, int Index), TitleBookSlotDefinition> slots)
        {
            var result = new Dictionary<int, TitleBookSlotDefinition>();
            for (var category = 0; category < DefaultCapacities.Length; category++)
            {
                for (var index = 0; index < DefaultCapacities[category]; index++)
                {
                    if (!slots.TryGetValue((category, index), out var slot)
                        || slot == null
                        || !slot.IsOpen
                        || slot.QuestId <= 0)
                    {
                        continue;
                    }

                    var quest = QuestData.GetQuestFile(slot.QuestId);
                    var questIds = QuestData.ParseIntList(quest?.IntData);
                    if (questIds.Count == 0)
                        continue;

                    var achievementQuestId = questIds[0];
                    if (achievementQuestId <= 0 || result.ContainsKey(achievementQuestId))
                        continue;

                    result[achievementQuestId] = slot;
                }
            }

            return result;
        }

        private static void ParseTitleQuestDefinitionsFromFileRoot(
            string root,
            HashSet<int> requestedQuestIds,
            Dictionary<int, TitleQuestDefinition> result)
        {
            var listPath = Path.Combine(root, "quest.lst");
            if (!File.Exists(listPath))
                return;

            var list = LstFile.Parse(File.ReadAllText(listPath));
            foreach (var entry in list.Entries)
            {
                if (!requestedQuestIds.Contains(entry.Id))
                    continue;

                var qstPath = ResolveQstPath(root, entry.FilePath);
                if (qstPath == null)
                    continue;

                var definition = ParseQst(entry.Id, File.ReadAllText(qstPath));
                if (definition != null)
                    result[entry.Id] = definition;
            }
        }

        private static void ParseTitleQuestDefinitionsFromPvf(
            string questListText,
            HashSet<int> requestedQuestIds,
            Dictionary<int, TitleQuestDefinition> result)
        {
            var list = LstFile.Parse(questListText);
            foreach (var entry in list.Entries)
            {
                if (!requestedQuestIds.Contains(entry.Id))
                    continue;

                if (!TryReadTitleQuestPvfText(entry.FilePath, out var qstText))
                    continue;

                var definition = ParseQst(entry.Id, qstText);
                if (definition != null)
                    result[entry.Id] = definition;
            }
        }

        private static TitleQuestDefinition ParseQst(int questId, string text)
        {
            var lines = SplitLines(text);
            var definition = new TitleQuestDefinition { QuestId = questId };
            var intData = Array.Empty<int>();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = NormalizeTokenLine(lines[i]);
                if (line.Equals("[check count]", StringComparison.OrdinalIgnoreCase))
                {
                    var value = FindNextInt(lines, i + 1);
                    if (value > 0)
                        definition.CheckCount = (ushort)Math.Min(value, ushort.MaxValue);
                    continue;
                }

                if (line.Equals("[reward int data]", StringComparison.OrdinalIgnoreCase))
                {
                    var value = FindNextInt(lines, i + 1);
                    if (value > 0)
                        definition.RewardTitleItemId = value;
                    continue;
                }

                if (line.Equals("[type]", StringComparison.OrdinalIgnoreCase))
                {
                    definition.TriggerType = FindNextText(lines, i + 1);
                    continue;
                }

                if (line.Equals("[int data]", StringComparison.OrdinalIgnoreCase))
                {
                    intData = FindSectionIntegers(lines, i + 1);
                }
            }

            if (definition.IsUseItem)
                ParseUseItemConditions(definition, intData);

            return definition;
        }

        private static Dictionary<int, List<TitleQuestDefinition>> BuildUseItemQuestIndex(
            IEnumerable<TitleQuestDefinition> quests)
        {
            var result = new Dictionary<int, List<TitleQuestDefinition>>();
            foreach (var quest in quests ?? Enumerable.Empty<TitleQuestDefinition>())
            {
                if (quest == null || !quest.IsUseItem)
                    continue;

                foreach (var condition in quest.UseItemConditions)
                {
                    if (condition.ItemId <= 0 || condition.ProgressPerItem <= 0)
                        continue;

                    if (!result.TryGetValue(condition.ItemId, out var matches))
                    {
                        matches = new List<TitleQuestDefinition>();
                        result[condition.ItemId] = matches;
                    }

                    if (!matches.Contains(quest))
                        matches.Add(quest);
                }
            }

            return result;
        }

        private static void ParseUseItemConditions(
            TitleQuestDefinition definition,
            IReadOnlyList<int> values)
        {
            if (definition == null || values == null || values.Count < 3)
                return;

            var declaredCount = Math.Max(0, values[0]);
            var availableCount = (values.Count - 1) / 2;
            var pairCount = Math.Min(declaredCount, availableCount);
            for (var index = 0; index < pairCount; index++)
            {
                var itemId = values[1 + index * 2];
                var progressPerItem = values[2 + index * 2];
                if (itemId <= 0 || progressPerItem <= 0)
                    continue;

                definition.UseItemConditions.Add(new TitleQuestUseItemCondition
                {
                    ItemId = itemId,
                    ProgressPerItem = progressPerItem,
                });
            }
        }

        private static string FindNextText(string[] lines, int start)
        {
            for (var i = start; i < lines.Length; i++)
            {
                var line = NormalizeTokenLine(lines[i]);
                if (line.StartsWith("[/", StringComparison.Ordinal))
                    return string.Empty;
                if (line.Length > 0)
                    return line;
            }

            return string.Empty;
        }

        private static int[] FindSectionIntegers(string[] lines, int start)
        {
            var result = new List<int>();
            for (var i = start; i < lines.Length; i++)
            {
                var line = NormalizeTokenLine(lines[i]);
                if (line.StartsWith("[/", StringComparison.Ordinal))
                    break;

                foreach (Match match in Regex.Matches(line, @"-?\d+"))
                {
                    if (int.TryParse(match.Value, out var value))
                        result.Add(value);
                }
            }

            return result.ToArray();
        }

        private static int FindNextInt(string[] lines, int start)
        {
            for (var i = start; i < lines.Length; i++)
            {
                var line = NormalizeTokenLine(lines[i]);
                if (line.StartsWith("[/", StringComparison.Ordinal))
                    return -1;

                var match = Regex.Match(line, @"-?\d+");
                if (match.Success && int.TryParse(match.Value, out var value))
                    return value;
            }
            return -1;
        }

        private static string ResolveQstPath(string root, string relativePath)
        {
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var candidate = Path.Combine(root, normalized);
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(root, normalized.ToLowerInvariant());
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(root, "title", Path.GetFileName(normalized));
            return File.Exists(candidate) ? candidate : null;
        }

        private static Dictionary<(int Category, int Index), TitleBookSlotDefinition> BuildClosedDefaults()
        {
            var slots = new Dictionary<(int Category, int Index), TitleBookSlotDefinition>();
            for (var category = 0; category < DefaultCapacities.Length; category++)
            {
                for (var index = 0; index < DefaultCapacities[category]; index++)
                {
                    slots[(category, index)] = new TitleBookSlotDefinition
                    {
                        Category = category,
                        Index = index,
                        SlotType = -1,
                        QuestId = -1,
                    };
                }
            }
            return slots;
        }

        private static Dictionary<(int Category, int Index), TitleBookSlotDefinition> BuildOpenFallback()
        {
            var slots = BuildClosedDefaults();
            foreach (var key in slots.Keys.ToList())
            {
                var slot = slots[key];
                slot.SlotType = 1;
                slot.QuestId = -1;
                slot.AllowedTitleItemIds.Add(-1);
            }
            return slots;
        }

        private static string ReadTitleBookEtcText()
        {
            var configured = Environment.GetEnvironmentVariable("TITLEBOOK_ETC_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return File.ReadAllText(configured);

            if (TryReadPvfText(out var pvfText, "etc/titlebook.etc", "Etc/titlebook.etc"))
                return pvfText;

            var fallbackPath = ResolveTitleBookEtcPath();
            return !string.IsNullOrWhiteSpace(fallbackPath) ? File.ReadAllText(fallbackPath) : null;
        }

        private static string ResolveTitleBookEtcPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "doc", "titlebook", "etc", "titlebook.etc");
                if (File.Exists(candidate))
                    return candidate;

                candidate = Path.Combine(dir.FullName, "Data", "titlebook", "titlebook.etc");
                if (File.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        private static bool TryReadTitleQuestPvfText(string relativePath, out string text)
        {
            var normalized = NormalizePvfRelativePath(relativePath);
            var fileName = Path.GetFileName(normalized.Replace('/', Path.DirectorySeparatorChar));
            return TryReadPvfText(
                out text,
                "n_quest/" + normalized,
                "n_Quest/" + normalized,
                "n_quest/" + normalized.ToLowerInvariant(),
                "n_quest/title/" + fileName,
                "n_Quest/title/" + fileName);
        }

        private static bool TryReadPvfText(out string text, params string[] relativePaths)
        {
            foreach (var relativePath in relativePaths)
            {
                try
                {
                    text = PvfArchiveAccessor.ReadText(relativePath);
                    return true;
                }
                catch
                {
                }
            }

            text = null;
            return false;
        }

        private static string NormalizePvfRelativePath(string relativePath)
        {
            return (relativePath ?? string.Empty)
                .Replace('\\', '/')
                .Replace("`", string.Empty)
                .Trim()
                .TrimStart('.', '/');
        }

        private static string[] SplitLines(string text)
        {
            return (text ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');
        }

        private static string ResolveTitleQuestRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "doc", "titlebook", "n_quest");
                if (Directory.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        private static string NormalizeTokenLine(string line)
        {
            return StripComment(line).Replace("`", "").Trim();
        }

        private static string StripComment(string line)
        {
            var index = line.IndexOf('#');
            return index >= 0 ? line.Substring(0, index) : line;
        }
    }
}
