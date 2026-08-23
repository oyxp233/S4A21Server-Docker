using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal sealed class CargoTransportStoneDefinition
    {
        public int StoneType { get; set; }

        public List<CargoTransportStoneGradeEntry> GradeEntries { get; } =
            new List<CargoTransportStoneGradeEntry>();

        public HashSet<EquipmentType> EnabledEquipmentTypes { get; } =
            new HashSet<EquipmentType>();

        public HashSet<int> ExceptItemIds { get; } = new HashSet<int>();

        public HashSet<int> IncludeItemIds { get; } = new HashSet<int>();

        public int UiIndex { get; set; } = -1;

        public bool AllowsLevel(int level)
        {
            if (GradeEntries.Count == 0)
                return true;

            var normalizedLevel = Math.Max(1, level);
            foreach (var entry in GradeEntries)
            {
                if (entry == null || entry.LevelStart <= 0 || entry.Value <= 0)
                    continue;

                if (normalizedLevel >= entry.LevelStart
                    && normalizedLevel < entry.LevelStart + 10)
                    return true;
            }

            return false;
        }

        public bool AllowsEquipmentType(EquipmentType equipmentType)
        {
            return EnabledEquipmentTypes.Count == 0
                || EnabledEquipmentTypes.Contains(equipmentType);
        }

        public bool AllowsItemId(int itemTemplateId)
        {
            if (ExceptItemIds.Contains(itemTemplateId))
                return false;

            return IncludeItemIds.Count == 0
                || IncludeItemIds.Contains(itemTemplateId);
        }
    }

    internal static class CargoTransportStoneConfigProvider
    {
        private const string ConfigPath = "etc/cargotransportstone.etc";

        private static readonly Lazy<IReadOnlyDictionary<int, CargoTransportStoneDefinition>>
            Definitions = new Lazy<IReadOnlyDictionary<int, CargoTransportStoneDefinition>>(Load);

        internal static bool TryGetDefinition(
            int stoneType,
            out CargoTransportStoneDefinition definition)
        {
            return Definitions.Value.TryGetValue(stoneType, out definition);
        }

        internal static IReadOnlyDictionary<int, CargoTransportStoneDefinition> Parse(
            string content)
        {
            var result = new Dictionary<int, CargoTransportStoneDefinition>();
            if (string.IsNullOrWhiteSpace(content))
                return result;

            var root = new ScriptParser().Parse(content);
            foreach (var node in root.Children.Where(IsStoneTypeNode))
            {
                var values = ReadIntegers(node, content);
                if (values.Count == 0 || values[0] < 0)
                    continue;

                var definition = new CargoTransportStoneDefinition
                {
                    StoneType = values[0],
                };

                foreach (var child in node.Children)
                {
                    switch ((child.Tag ?? string.Empty).Trim().ToLowerInvariant())
                    {
                        case "cargo transport stone grade":
                            ParseGrade(child, content, definition);
                            break;
                        case "cargo transport stone enable equip type":
                            ParseEquipmentTypes(child, content, definition);
                            break;
                        case "cargo transport stone except index":
                            AddPositiveIds(child, content, definition.ExceptItemIds);
                            break;
                        case "cargo transport stone include index":
                            AddPositiveIds(child, content, definition.IncludeItemIds);
                            break;
                        case "ui index":
                            definition.UiIndex = ReadIntegers(child, content)
                                .FirstOrDefault();
                            break;
                    }
                }

                result[definition.StoneType] = definition;
            }

            return result;
        }

        private static IReadOnlyDictionary<int, CargoTransportStoneDefinition> Load()
        {
            try
            {
                return Parse(PvfArchiveAccessor.ReadText(ConfigPath));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[CargoTransportStoneConfig] load failed path={ConfigPath}: {ex.Message}");
                return new Dictionary<int, CargoTransportStoneDefinition>();
            }
        }

        private static bool IsStoneTypeNode(ScriptNode node)
        {
            return string.Equals(
                node?.Tag,
                "stone type",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void ParseGrade(
            ScriptNode node,
            string content,
            CargoTransportStoneDefinition definition)
        {
            var values = ReadIntegers(node, content);
            for (var index = 0; index + 1 < values.Count; index += 2)
            {
                if (values[index] <= 0)
                    continue;

                definition.GradeEntries.Add(new CargoTransportStoneGradeEntry
                {
                    LevelStart = values[index],
                    Value = values[index + 1],
                });
            }
        }

        private static void ParseEquipmentTypes(
            ScriptNode node,
            string content,
            CargoTransportStoneDefinition definition)
        {
            foreach (var token in ReadBracketTokens(node, content))
            {
                var equipmentType = EquipmentTypeInfo.ParseOrUnknown(token);
                if (equipmentType != EquipmentType.Unknown)
                    definition.EnabledEquipmentTypes.Add(equipmentType);
            }
        }

        private static void AddPositiveIds(
            ScriptNode node,
            string content,
            ISet<int> result)
        {
            foreach (var value in ReadIntegers(node, content))
            {
                if (value > 0)
                    result.Add(value);
            }
        }

        private static List<int> ReadIntegers(ScriptNode node, string content)
        {
            var result = new List<int>();
            if (node?.DataItems == null)
                return result;

            foreach (var item in node.DataItems)
            {
                var raw = item.GetContent(content) ?? string.Empty;
                foreach (Match match in Regex.Matches(raw, @"-?\d+"))
                {
                    if (int.TryParse(match.Value, out var value))
                        result.Add(value);
                }
            }

            return result;
        }

        private static List<string> ReadBracketTokens(ScriptNode node, string content)
        {
            var result = new List<string>();
            if (node?.DataItems == null)
                return result;

            foreach (var item in node.DataItems)
            {
                var raw = item.GetContent(content) ?? string.Empty;
                var quoted = Regex.Matches(raw, "`([^`]*)`");
                foreach (Match match in quoted)
                    AddToken(result, match.Groups[1].Value);

                if (quoted.Count > 0)
                    continue;

                foreach (Match match in Regex.Matches(raw, @"\[[^\]]+\]"))
                    AddToken(result, match.Value);
            }

            return result;
        }

        private static void AddToken(ICollection<string> result, string raw)
        {
            var token = (raw ?? string.Empty).Trim();
            if (token.Length == 0)
                return;

            if (!token.StartsWith("[", StringComparison.Ordinal))
                token = "[" + token;
            if (!token.EndsWith("]", StringComparison.Ordinal))
                token += "]";

            result.Add(token);
        }
    }
}
