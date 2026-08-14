using System;
using System.Collections.Generic;
using System.IO;
using PvfLib;

namespace DfoServer.GameWorld
{
    public sealed class WorldMapDungeonEntry
    {
        public int DungeonId { get; set; }
        public int QuestId { get; set; }
        public bool HasExplicitQuestId { get; set; }
        public bool InProgressOnly { get; set; }
    }

    public sealed class HellTicketItem
    {
        public int ItemId { get; set; }
        public int Count { get; set; }
    }

    public sealed class WorldMapArea
    {
        public int AreaId { get; set; }
        public string FilePath { get; set; }
        public List<WorldMapDungeonEntry> Dungeons { get; } = new List<WorldMapDungeonEntry>();
        public bool HellDungeon { get; set; }
        public List<int> HellQuestIds { get; } = new List<int>();
        public List<HellTicketItem> HellFreePassItems { get; } = new List<HellTicketItem>();
        public List<int> HellNormalTicketItemIds { get; } = new List<int>();
    }

    public static class WorldMap
    {
        private static readonly Lazy<WorldMapIndex> Index =
            new Lazy<WorldMapIndex>(LoadIndex);

        public static IReadOnlyList<WorldMapArea> Areas => Index.Value.Areas;

        internal static int AdmissionSourceCount =>
            Index.Value.AdmissionSourceCount;

        internal static IReadOnlyList<DungeonAdmissionDefinition>
            AdmissionDefinitions => Index.Value.AdmissionDefinitions;

        public static WorldMapArea GetAreaByDungeonId(int dungeonId)
        {
            if (dungeonId <= 0)
                return null;

            Index.Value.AreaByDungeonId.TryGetValue(dungeonId, out var area);
            return area;
        }

        public static bool IsTaskExclusiveDungeon(int dungeonId)
        {
            if (Index.Value.AdmissionsByDungeonId.TryGetValue(
                    dungeonId,
                    out var definition))
            {
                return definition.IsTaskExclusive;
            }

            return IsQuestDungeonAsset(dungeonId);
        }

        public static bool IsTaskExclusiveDungeonAvailable(
            int dungeonId,
            ISet<int> activeQuestIds)
        {
            return EvaluateDungeonAdmission(
                dungeonId,
                activeQuestIds,
                clearedQuestIds: null).Allowed;
        }

        internal static DungeonAdmissionDecision EvaluateDungeonAdmission(
            int dungeonId,
            ISet<int> activeQuestIds,
            ISet<int> clearedQuestIds)
        {
            if (Index.Value.AdmissionsByDungeonId.TryGetValue(
                    dungeonId,
                    out var definition))
            {
                return definition.Evaluate(activeQuestIds, clearedQuestIds);
            }

            if (!IsQuestDungeonAsset(dungeonId))
            {
                return new DungeonAdmissionDecision(
                    allowed: true,
                    mode: DungeonAdmissionMode.Unrestricted,
                    reason: "no_worldmap_admission_rule",
                    requiredQuestIds: Array.Empty<int>());
            }

            var requiredQuestIds = GetDungeonQuestConnectionIds(dungeonId);
            if (activeQuestIds != null)
            {
                foreach (var questId in requiredQuestIds)
                {
                    if (activeQuestIds.Contains(questId))
                    {
                        return new DungeonAdmissionDecision(
                            allowed: true,
                            mode: DungeonAdmissionMode.ActiveQuestOnly,
                            reason: "quest_asset_connection_active",
                            requiredQuestIds: requiredQuestIds);
                    }
                }

                foreach (var questId in activeQuestIds)
                {
                    if (QuestData.ReferencesDungeon(questId, dungeonId))
                    {
                        return new DungeonAdmissionDecision(
                            allowed: true,
                            mode: DungeonAdmissionMode.ActiveQuestOnly,
                            reason: "quest_asset_reference_active",
                            requiredQuestIds: requiredQuestIds);
                    }
                }
            }

            return new DungeonAdmissionDecision(
                allowed: false,
                mode: DungeonAdmissionMode.ActiveQuestOnly,
                reason: "quest_asset_state_miss",
                requiredQuestIds: requiredQuestIds);
        }

        internal static bool TryGetAdmissionDefinition(
            int dungeonId,
            out DungeonAdmissionDefinition definition)
        {
            return Index.Value.AdmissionsByDungeonId.TryGetValue(
                dungeonId,
                out definition);
        }

        private static IReadOnlyList<int> GetWorldMapActiveQuestIds(
            int dungeonId)
        {
            if (Index.Value.AdmissionsByDungeonId.TryGetValue(
                    dungeonId,
                    out var definition))
            {
                return definition.ActiveQuestIds;
            }

            return Array.Empty<int>();
        }

        public static IReadOnlyList<int> GetTaskExclusiveQuestIds(int dungeonId)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            foreach (var questId in GetWorldMapActiveQuestIds(dungeonId))
            {
                if (seen.Add(questId))
                    result.Add(questId);
            }

            foreach (var questId in GetDungeonQuestConnectionIds(dungeonId))
            {
                if (seen.Add(questId))
                    result.Add(questId);
            }

            return result;
        }

        public static bool ShouldPersistDungeonPermission(int dungeonId) =>
            !IsTaskExclusiveDungeon(dungeonId);

        private static bool IsQuestDungeonAsset(int dungeonId)
        {
            if (dungeonId <= 0)
                return false;

            try
            {
                var loaded = Dungeon.LoadDungeonFileWithPath(dungeonId);
                var path = (loaded.FilePath ?? string.Empty)
                    .Replace('\\', '/')
                    .TrimStart('/');
                return path.StartsWith("quest/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("dungeon/quest/", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static IReadOnlyList<int> GetDungeonQuestConnectionIds(
            int dungeonId)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            try
            {
                var dungeon = Dungeon.GetDungeonFile(dungeonId);
                AddQuestConnectionId(dungeon?.QuestConnection, seen, result);
                if (dungeon?.Mazes != null)
                {
                    foreach (var maze in dungeon.Mazes)
                        AddQuestConnectionId(maze?.QuestConnection, seen, result);
                }
            }
            catch
            {
                // Missing or malformed PVF data cannot grant a task-only entry.
            }

            return result;
        }

        private static void AddQuestConnectionId(
            int[] connection,
            HashSet<int> seen,
            List<int> result)
        {
            if (connection == null || connection.Length < 2)
                return;

            var questId = connection[1];
            if (questId > 0 && seen.Add(questId))
                result.Add(questId);
        }

        public static bool IsHellDungeon(int dungeonId)
        {
            var area = GetAreaByDungeonId(dungeonId);
            return area != null && area.HellDungeon;
        }

        public static int GetHellNormalTicketNeedCount(int dungeonMinLevel)
        {
            if (dungeonMinLevel <= 44)
                return 0;

            return ((40 * dungeonMinLevel - 1800) / 100) + 10;
        }

        private static WorldMapIndex LoadIndex()
        {
            var areas = new List<WorldMapArea>();
            var byDungeon = new Dictionary<int, WorldMapArea>();
            var admissionSources = new List<WorldMapArea>();

            try
            {
                var entries = ParseWorldMapList(PvfArchiveAccessor.ReadText("worldmap/worldmap.lst"));
                foreach (var entry in entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
                        continue;

                    var area = LoadArea(entry.Id, entry.FilePath);
                    if (area == null)
                        continue;

                    areas.Add(area);
                    foreach (var dungeon in area.Dungeons)
                    {
                        if (dungeon.DungeonId <= 0 || byDungeon.ContainsKey(dungeon.DungeonId))
                            continue;

                        byDungeon[dungeon.DungeonId] = area;
                    }
                }

                admissionSources.AddRange(LoadAdmissionSources());

                FileLogger.Log(
                    $"[WorldMap] loaded areas={areas.Count} " +
                    $"dungeonRefs={byDungeon.Count} " +
                    $"admissionSources={admissionSources.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[WorldMap] load failed: {ex.Message}");
            }

            return new WorldMapIndex(areas, byDungeon, admissionSources);
        }

        private static List<WorldMapArea> LoadAdmissionSources()
        {
            var result = new List<WorldMapArea>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawPath in PvfArchiveAccessor.FindPathsContaining(
                         "worldmap/"))
            {
                var path = NormalizeWorldMapPath(rawPath);
                if (!IsTopLevelWorldMapDefinition(path) || !seen.Add(path))
                    continue;

                try
                {
                    var source = new WorldMapArea
                    {
                        AreaId = -1,
                        FilePath = path,
                    };
                    ParseDungeons(
                        "dungeon",
                        PvfArchiveAccessor.ReadText(path),
                        source);
                    result.Add(source);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[WorldMap] admission source load failed: " +
                        $"file={path} {ex.Message}");
                }
            }

            return result;
        }

        private static string NormalizeWorldMapPath(string path) =>
            (path ?? string.Empty)
                .Replace('\\', '/')
                .Trim()
                .TrimStart('/');

        private static bool IsTopLevelWorldMapDefinition(string path)
        {
            const string prefix = "worldmap/";
            if (string.IsNullOrWhiteSpace(path)
                || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(".wdm", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return path.IndexOf('/', prefix.Length) < 0;
        }

        private static WorldMapArea LoadArea(int areaId, string relativeFilePath)
        {
            try
            {
                var normalized = relativeFilePath.Replace('\\', '/').TrimStart('/');
                var pvfPath = normalized.StartsWith("worldmap/", StringComparison.OrdinalIgnoreCase)
                    ? normalized
                    : Path.Combine("worldmap", normalized);
                var content = PvfArchiveAccessor.ReadText(pvfPath);

                var area = new WorldMapArea
                {
                    AreaId = areaId,
                    FilePath = normalized,
                };

                ParseDungeons("dungeon", content, area);
                area.HellDungeon = ParseFirstInt("hell dungeon", content) == 1;
                ParseIntList("hell quest", content, area.HellQuestIds);
                ParseTicketPairs("hell freepass item", content, area.HellFreePassItems);
                ParseIntList("item condition", content, area.HellNormalTicketItemIds);
                FileLogger.Log($"[WorldMap] area={area.AreaId} file={area.FilePath} dungeons={area.Dungeons.Count} hell={area.HellDungeon} freepass={area.HellFreePassItems.Count} normalTickets={area.HellNormalTicketItemIds.Count}");
                return area;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[WorldMap] area load failed: area={areaId} file={relativeFilePath} {ex.Message}");
                return null;
            }
        }

        private static List<LstEntry> ParseWorldMapList(string content)
        {
            var lst = LstFile.Parse(content);
            if (lst.Entries.Count > 0)
                return lst.Entries;

            var result = new List<LstEntry>();
            if (string.IsNullOrWhiteSpace(content))
                return result;

            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var tokens = Tokenize(rawLine);
                if (tokens.Count < 2 || !int.TryParse(tokens[0], out var id))
                    continue;

                result.Add(new LstEntry { Id = id, FilePath = tokens[1] });
            }

            return result;
        }

        private static void ParseDungeons(string sectionName, string content, WorldMapArea area)
        {
            var tokens = new List<string>();
            foreach (var line in EnumerateSectionLines(sectionName, content))
                tokens.AddRange(Tokenize(line));

            // WDM entries are a continuous stream even when [in progress] is on
            // its own line: dungeonId, optional marker, then questId.
            for (var i = 0; i < tokens.Count;)
            {
                if (!int.TryParse(tokens[i], out var dungeonId))
                {
                    i++;
                    continue;
                }

                i++;
                var inProgressOnly = false;
                if (IsInProgressMarker(tokens, i, out var markerTokenCount))
                {
                    inProgressOnly = true;
                    i += markerTokenCount;
                }

                var questId = -1;
                var hasExplicitQuestId =
                    i < tokens.Count && int.TryParse(tokens[i], out questId);
                if (hasExplicitQuestId)
                    i++;

                if (dungeonId <= 0)
                    continue;

                area.Dungeons.Add(new WorldMapDungeonEntry
                {
                    DungeonId = dungeonId,
                    QuestId = questId,
                    HasExplicitQuestId = hasExplicitQuestId,
                    InProgressOnly = inProgressOnly,
                });
            }
        }

        private static bool IsInProgressMarker(List<string> tokens, int index, out int tokenCount)
        {
            tokenCount = 0;
            if (tokens == null || index < 0 || index >= tokens.Count)
                return false;

            if (tokens[index].Equals("[in progress]", StringComparison.OrdinalIgnoreCase))
            {
                tokenCount = 1;
                return true;
            }

            if (index + 1 < tokens.Count
                && tokens[index].Equals("[in", StringComparison.OrdinalIgnoreCase)
                && tokens[index + 1].Equals("progress]", StringComparison.OrdinalIgnoreCase))
            {
                tokenCount = 2;
                return true;
            }

            return false;
        }

        private static void ParseTicketPairs(string sectionName, string content, List<HellTicketItem> result)
        {
            foreach (var line in EnumerateSectionLines(sectionName, content))
            {
                var tokens = Tokenize(line);
                for (var i = 0; i + 1 < tokens.Count; i += 2)
                {
                    if (!int.TryParse(tokens[i], out var itemId))
                        continue;
                    if (!int.TryParse(tokens[i + 1], out var count))
                        count = 1;
                    if (itemId > 0 && count > 0)
                        result.Add(new HellTicketItem { ItemId = itemId, Count = count });
                }
            }
        }

        private static void ParseIntList(string sectionName, string content, List<int> result)
        {
            foreach (var line in EnumerateSectionLines(sectionName, content))
            {
                var tokens = Tokenize(line);
                foreach (var token in tokens)
                    if (int.TryParse(token, out var value) && value > 0)
                        result.Add(value);
            }
        }

        private static int ParseFirstInt(string sectionName, string content)
        {
            foreach (var line in EnumerateSectionLines(sectionName, content))
            {
                var tokens = Tokenize(line);
                foreach (var token in tokens)
                    if (int.TryParse(token, out var value))
                        return value;
            }

            return 0;
        }

        private static IEnumerable<string> EnumerateSectionLines(string sectionName, string content)
        {
            if (string.IsNullOrWhiteSpace(sectionName) || string.IsNullOrEmpty(content))
                yield break;

            var startTag = "[" + sectionName + "]";
            var endTag = "[/" + sectionName + "]";
            var start = content.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                yield break;

            start += startTag.Length;
            var end = content.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                var next = content.IndexOf("[", start, StringComparison.Ordinal);
                end = next >= 0 ? next : content.Length;
            }

            var section = content.Substring(start, end - start);
            var lines = section.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var line = StripLineComment(rawLine).Trim();
                if (line.Length > 0)
                    yield return line;
            }
        }

        private static List<string> Tokenize(string line)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(line))
                return result;

            var tokens = line.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                var trimmed = StripBacktick(token.Trim());
                if (trimmed.Length > 0)
                    result.Add(trimmed);
            }

            return result;
        }

        private static string StripBacktick(string value)
        {
            if (value != null && value.Length >= 2 && value[0] == '`' && value[value.Length - 1] == '`')
                return value.Substring(1, value.Length - 2);
            return value ?? string.Empty;
        }

        private static string StripLineComment(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
            return commentIndex >= 0 ? line.Substring(0, commentIndex) : line;
        }

        private sealed class WorldMapIndex
        {
            public WorldMapIndex(
                List<WorldMapArea> areas,
                Dictionary<int, WorldMapArea> areaByDungeonId,
                IReadOnlyList<WorldMapArea> admissionSources)
            {
                Areas = areas;
                AreaByDungeonId = areaByDungeonId;
                EntriesByDungeonId = new Dictionary<int, List<WorldMapDungeonEntry>>();
                foreach (var area in admissionSources ?? Array.Empty<WorldMapArea>())
                {
                    foreach (var entry in area.Dungeons)
                    {
                        if (entry.DungeonId <= 0)
                            continue;
                        if (!EntriesByDungeonId.TryGetValue(
                                entry.DungeonId,
                                out var entries))
                        {
                            entries = new List<WorldMapDungeonEntry>();
                            EntriesByDungeonId[entry.DungeonId] = entries;
                        }
                        entries.Add(entry);
                    }
                }

                AdmissionsByDungeonId = BuildAdmissionDefinitions(
                    EntriesByDungeonId);
                AdmissionSourceCount = admissionSources?.Count ?? 0;
                var definitions = new List<DungeonAdmissionDefinition>(
                    AdmissionsByDungeonId.Values);
                definitions.Sort((left, right) =>
                    left.DungeonId.CompareTo(right.DungeonId));
                AdmissionDefinitions = definitions;
            }

            public List<WorldMapArea> Areas { get; }
            public Dictionary<int, WorldMapArea> AreaByDungeonId { get; }
            public Dictionary<int, List<WorldMapDungeonEntry>> EntriesByDungeonId { get; }
            public Dictionary<int, DungeonAdmissionDefinition>
                AdmissionsByDungeonId { get; }
            public int AdmissionSourceCount { get; }
            public IReadOnlyList<DungeonAdmissionDefinition>
                AdmissionDefinitions { get; }

            private static Dictionary<int, DungeonAdmissionDefinition>
                BuildAdmissionDefinitions(
                    IReadOnlyDictionary<int, List<WorldMapDungeonEntry>> entriesByDungeonId)
            {
                var result = new Dictionary<int, DungeonAdmissionDefinition>();
                foreach (var pair in entriesByDungeonId)
                {
                    var hasUnrestrictedEntry = false;
                    var hasMalformedEntry = false;
                    var persistentQuestIds = new List<int>();
                    var activeQuestIds = new List<int>();

                    foreach (var entry in pair.Value)
                    {
                        if (entry == null)
                        {
                            hasMalformedEntry = true;
                            continue;
                        }

                        if (!entry.HasExplicitQuestId || entry.QuestId <= 0)
                        {
                            if (entry.InProgressOnly)
                                hasMalformedEntry = true;
                            else
                                hasUnrestrictedEntry = true;
                            continue;
                        }

                        var target = entry.InProgressOnly
                            ? activeQuestIds
                            : persistentQuestIds;
                        if (!target.Contains(entry.QuestId))
                            target.Add(entry.QuestId);
                    }

                    result[pair.Key] = new DungeonAdmissionDefinition(
                        pair.Key,
                        hasUnrestrictedEntry,
                        persistentQuestIds.ToArray(),
                        activeQuestIds.ToArray(),
                        hasMalformedEntry);
                }
                return result;
            }
        }
    }
}
