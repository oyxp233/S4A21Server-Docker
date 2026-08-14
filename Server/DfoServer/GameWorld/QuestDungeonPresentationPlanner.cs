using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DfoServer.Game.Quests;

namespace DfoServer.GameWorld
{
    // Resolves the client-facing task-dungeon cards from PVF world-map/XUI
    // identity. This projection never changes the persisted active quest set.
    internal static class QuestDungeonPresentationPlanner
    {
        private static readonly Lazy<PresentationCatalog> Catalog =
            new Lazy<PresentationCatalog>(BuildCatalog);

        internal static IReadOnlyList<ActiveQuest> ProjectActiveQuests(
            IReadOnlyCollection<ActiveQuest> activeQuests)
        {
            if (activeQuests == null || activeQuests.Count == 0)
                return Array.Empty<ActiveQuest>();

            var visibleIds = new HashSet<ushort>(
                ProjectQuestIds(
                    activeQuests
                        .Where(quest => quest != null)
                        .Select(quest => quest.QuestId)
                        .ToArray()));
            return activeQuests
                .Where(quest => quest != null && visibleIds.Contains(quest.QuestId))
                .ToArray();
        }

        internal static HashSet<int> ProjectActiveQuestIds(
            IReadOnlyCollection<ActiveQuest> activeQuests,
            IReadOnlyDictionary<int, int> clearedFlags)
        {
            return new HashSet<int>(
                ProjectQuestIds(
                    activeQuests == null
                        ? Array.Empty<ushort>()
                        : activeQuests
                            .Where(quest => quest != null)
                            .Select(quest => quest.QuestId)
                            .ToArray())
                    .Select(questId => (int)questId));
        }

        internal static bool SharesPhysicalSlot(
            int leftQuestId,
            int rightQuestId)
        {
            return leftQuestId > 0
                && rightQuestId > 0
                && Catalog.Value.TryGetQuestSlots(leftQuestId, out var leftSlots)
                && Catalog.Value.TryGetQuestSlots(rightQuestId, out var rightSlots)
                && SharesSlot(leftSlots, rightSlots);
        }

        private static IReadOnlyList<ushort> ProjectQuestIds(
            IReadOnlyCollection<ushort> questIds)
        {
            var input = new HashSet<int>();
            foreach (var questId in questIds ?? Array.Empty<ushort>())
            {
                if (questId > 0)
                    input.Add(questId);
            }

            if (input.Count == 0)
                return Array.Empty<ushort>();

            // A quest may be attached to more than one physical entry. Pick
            // candidates in PVF-defined priority order and reserve all of a
            // winner's slots together; choosing winners independently per slot
            // can leave two quests visible when one of them wins elsewhere.
            var candidates = input.ToList();
            candidates.Sort(CompareQuestPriority);
            var occupiedSlots = new HashSet<PresentationSlotKey>();
            var visibleQuestIds = new HashSet<int>();
            foreach (var questId in candidates)
            {
                if (!Catalog.Value.TryGetQuestSlots(questId, out var slots)
                    || slots.Count == 0)
                {
                    // A quest without a parsed task-dungeon presentation has
                    // no physical slot to arbitrate and remains visible.
                    visibleQuestIds.Add(questId);
                    continue;
                }

                if (slots.Any(occupiedSlots.Contains))
                    continue;

                visibleQuestIds.Add(questId);
                foreach (var slot in slots)
                    occupiedSlots.Add(slot);
            }

            var result = new List<ushort>();
            foreach (var questId in questIds ?? Array.Empty<ushort>())
            {
                if (questId <= 0 || !input.Contains(questId))
                    continue;
                if (visibleQuestIds.Contains(questId))
                    result.Add(questId);
            }

            return result;
        }

        private static bool SharesSlot(
            IReadOnlyList<PresentationSlotKey> left,
            IReadOnlyList<PresentationSlotKey> right)
        {
            if (left == null || right == null)
                return false;

            foreach (var leftSlot in left)
            {
                foreach (var rightSlot in right)
                {
                    if (leftSlot.Equals(rightSlot))
                        return true;
                }
            }
            return false;
        }

        private static int CompareQuestPriority(int leftQuestId, int rightQuestId)
        {
            if (leftQuestId == rightQuestId)
                return 0;

            var leftLevel = GetQuestMinimumLevel(leftQuestId);
            var rightLevel = GetQuestMinimumLevel(rightQuestId);
            var comparison = leftLevel.CompareTo(rightLevel);
            if (comparison != 0)
                return comparison;

            var leftOrder = GetQuestListOrder(leftQuestId);
            var rightOrder = GetQuestListOrder(rightQuestId);
            comparison = leftOrder.CompareTo(rightOrder);
            if (comparison != 0)
                return comparison;

            return leftQuestId.CompareTo(rightQuestId);
        }

        private static int GetQuestMinimumLevel(int questId)
        {
            var quest = QuestCatalog.Get(questId);
            if (quest?.Level == null || quest.Level.Length == 0)
                return int.MaxValue;
            return quest.Level[0] > 0 ? quest.Level[0] : int.MaxValue;
        }

        private static int GetQuestListOrder(int questId)
        {
            var order = 0;
            foreach (var orderedId in QuestCatalog.OrderedIds)
            {
                if (orderedId == questId)
                    return order;
                order++;
            }
            return int.MaxValue;
        }

        private static PresentationCatalog BuildCatalog()
        {
            var catalog = new PresentationCatalog();
            foreach (var rawPath in PvfArchiveAccessor.FindPathsContaining("worldmap/"))
            {
                var path = NormalizePath(rawPath);
                if (!IsTopLevelWorldMap(path))
                    continue;

                try
                {
                    AddWorldMap(catalog, path, PvfArchiveAccessor.ReadText(path));
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[QuestDungeonPresentationPlanner] worldmap parse failed: " +
                        $"path={path} error={ex.Message}");
                }
            }

            FileLogger.Log(
                $"[QuestDungeonPresentationPlanner] catalog built: " +
                $"quests={catalog.QuestSlots.Count} slots={catalog.SlotQuestIds.Count}");
            return catalog;
        }

        private static void AddWorldMap(
            PresentationCatalog catalog,
            string worldMapPath,
            string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return;

            var uiPath = ParseUiPath(content);
            if (string.IsNullOrWhiteSpace(uiPath))
                return;

            var taskEntries = ParseInProgressEntries(content);
            if (taskEntries.Count == 0)
                return;

            var positions = ParseXuiPositions(uiPath);
            foreach (var entry in taskEntries)
            {
                if (entry.QuestId <= 0 || entry.DungeonId <= 0)
                    continue;
                if (!positions.TryGetValue(entry.DungeonId, out var dungeonPositions))
                    continue;

                foreach (var position in dungeonPositions)
                {
                    var key = new PresentationSlotKey(
                        NormalizePath(uiPath),
                        position.X,
                        position.Y);
                    catalog.Add(entry.QuestId, key);
                }
            }
        }

        private static Dictionary<int, List<PresentationPosition>> ParseXuiPositions(
            string uiPath)
        {
            var result = new Dictionary<int, List<PresentationPosition>>();
            var content = PvfArchiveAccessor.ReadText(uiPath);
            var document = XDocument.Parse(
                StripInvalidXmlCharacters(content),
                LoadOptions.PreserveWhitespace);
            foreach (var element in document.Descendants())
            {
                if (!string.Equals(
                        element.Name.LocalName,
                        "CNUIControlWorldmapBalloonForRDAR",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var dungeonValue = (string)element.Attribute("dungeonIndex");
                var positionValue = (string)element.Attribute("Pos");
                if (!TryParseTypedInt(dungeonValue, out var dungeonId)
                    || !TryParsePosition(positionValue, out var position))
                {
                    continue;
                }

                if (!result.TryGetValue(dungeonId, out var positions))
                {
                    positions = new List<PresentationPosition>();
                    result[dungeonId] = positions;
                }
                if (!positions.Contains(position))
                    positions.Add(position);
            }
            return result;
        }

        private static List<TaskDungeonEntry> ParseInProgressEntries(string content)
        {
            var result = new List<TaskDungeonEntry>();
            var lines = content.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            var inProgress = false;
            foreach (var rawLine in lines)
            {
                var line = StripLineComment(rawLine).Trim();
                if (line.Length == 0)
                    continue;
                if (line.Equals("[in progress]", StringComparison.OrdinalIgnoreCase))
                {
                    inProgress = true;
                    continue;
                }
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inProgress = false;
                    continue;
                }
                if (!inProgress)
                    continue;

                var tokens = Tokenize(line);
                for (var index = 0; index + 1 < tokens.Count; index += 2)
                {
                    if (int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var questId)
                        && int.TryParse(tokens[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dungeonId))
                    {
                        result.Add(new TaskDungeonEntry(questId, dungeonId));
                    }
                }
                inProgress = false;
            }
            return result;
        }

        private static string ParseUiPath(string content)
        {
            foreach (var line in EnumerateSectionLines("ui path", content))
            {
                var tokens = Tokenize(line);
                if (tokens.Count > 0)
                    return tokens[0];
            }
            return null;
        }

        private static IEnumerable<string> EnumerateSectionLines(
            string sectionName,
            string content)
        {
            var startTag = "[" + sectionName + "]";
            var start = content.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                yield break;
            start += startTag.Length;
            var next = content.IndexOf("[", start, StringComparison.Ordinal);
            var section = content.Substring(
                start,
                (next >= 0 ? next : content.Length) - start);
            foreach (var rawLine in section.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                var line = StripLineComment(rawLine).Trim();
                if (line.Length > 0)
                    yield return line;
            }
        }

        private static List<string> Tokenize(string line)
        {
            return line
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => StripBacktick(token.Trim()))
                .Where(token => token.Length > 0)
                .ToList();
        }

        private static bool TryParseTypedInt(string value, out int result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var separator = value.IndexOf(':');
            var number = separator >= 0 ? value.Substring(separator + 1) : value;
            return int.TryParse(
                number.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out result);
        }

        private static bool TryParsePosition(
            string value,
            out PresentationPosition position)
        {
            position = default;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var separator = value.IndexOf(':');
            var numbers = (separator >= 0 ? value.Substring(separator + 1) : value)
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length < 2
                || !int.TryParse(numbers[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(numbers[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                return false;
            }
            position = new PresentationPosition(x, y);
            return true;
        }

        private static string NormalizePath(string path) =>
            (path ?? string.Empty).Replace('\\', '/').TrimStart('/');

        private static string StripInvalidXmlCharacters(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var buffer = new char[value.Length];
            var count = 0;
            foreach (var character in value)
            {
                if (character == '\t'
                    || character == '\n'
                    || character == '\r'
                    || (character >= 0x20
                        && character != 0xFFFE
                        && character != 0xFFFF))
                {
                    buffer[count++] = character;
                }
            }
            return new string(buffer, 0, count);
        }

        private static bool IsTopLevelWorldMap(string path)
        {
            const string prefix = "worldmap/";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".wdm", StringComparison.OrdinalIgnoreCase)
                && path.IndexOf('/', prefix.Length) < 0;
        }

        private static string StripBacktick(string value)
        {
            return value.Length >= 2 && value[0] == '`' && value[value.Length - 1] == '`'
                ? value.Substring(1, value.Length - 2)
                : value;
        }

        private static string StripLineComment(string line)
        {
            var index = line.IndexOf("//", StringComparison.Ordinal);
            return index >= 0 ? line.Substring(0, index) : line;
        }

        private readonly struct TaskDungeonEntry
        {
            internal TaskDungeonEntry(int questId, int dungeonId)
            {
                QuestId = questId;
                DungeonId = dungeonId;
            }

            internal int QuestId { get; }
            internal int DungeonId { get; }
        }

        private readonly struct PresentationPosition : IEquatable<PresentationPosition>
        {
            internal PresentationPosition(int x, int y)
            {
                X = x;
                Y = y;
            }

            internal int X { get; }
            internal int Y { get; }

            public bool Equals(PresentationPosition other) => X == other.X && Y == other.Y;
            public override bool Equals(object obj) => obj is PresentationPosition other && Equals(other);
            public override int GetHashCode() => (X * 397) ^ Y;
        }

        private readonly struct PresentationSlotKey : IEquatable<PresentationSlotKey>
        {
            internal PresentationSlotKey(string uiPath, int x, int y)
            {
                UiPath = uiPath ?? string.Empty;
                X = x;
                Y = y;
            }

            private string UiPath { get; }
            private int X { get; }
            private int Y { get; }

            public bool Equals(PresentationSlotKey other) =>
                X == other.X
                && Y == other.Y
                && string.Equals(UiPath, other.UiPath, StringComparison.OrdinalIgnoreCase);

            public override bool Equals(object obj) =>
                obj is PresentationSlotKey other && Equals(other);

            public override int GetHashCode() =>
                StringComparer.OrdinalIgnoreCase.GetHashCode(UiPath) ^ (X * 397) ^ Y;
        }

        private sealed class PresentationCatalog
        {
            internal Dictionary<int, List<PresentationSlotKey>> QuestSlots { get; } =
                new Dictionary<int, List<PresentationSlotKey>>();
            internal Dictionary<PresentationSlotKey, List<int>> SlotQuestIds { get; } =
                new Dictionary<PresentationSlotKey, List<int>>();

            internal void Add(int questId, PresentationSlotKey slot)
            {
                if (!QuestSlots.TryGetValue(questId, out var questSlots))
                {
                    questSlots = new List<PresentationSlotKey>();
                    QuestSlots[questId] = questSlots;
                }
                if (!questSlots.Contains(slot))
                    questSlots.Add(slot);

                if (!SlotQuestIds.TryGetValue(slot, out var questIds))
                {
                    questIds = new List<int>();
                    SlotQuestIds[slot] = questIds;
                }
                if (!questIds.Contains(questId))
                    questIds.Add(questId);
            }

            internal bool TryGetQuestSlots(
                int questId,
                out List<PresentationSlotKey> slots)
                => QuestSlots.TryGetValue(questId, out slots);
        }
    }
}
