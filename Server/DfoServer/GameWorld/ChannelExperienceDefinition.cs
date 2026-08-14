using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DfoServer.GameWorld
{
    // Immutable channel definition loaded once from the server ETC.
    internal sealed class ChannelExperienceDefinition
    {
        private readonly HashSet<int> _dungeonIds;

        internal ChannelExperienceDefinition(
            int channelId,
            int channelType,
            string dungeonClassification,
            double bonusRate,
            IEnumerable<int> dungeonIds)
        {
            ChannelId = channelId;
            ChannelType = channelType;
            DungeonClassification = dungeonClassification ?? string.Empty;
            BonusRate = NormalizeRate(bonusRate);
            _dungeonIds = new HashSet<int>(dungeonIds ?? Array.Empty<int>());
        }

        internal int ChannelId { get; }
        internal int ChannelType { get; }
        internal string DungeonClassification { get; }
        internal double BonusRate { get; }
        internal int DungeonCount => _dungeonIds.Count;

        internal bool MatchesDungeon(int dungeonId)
            => dungeonId > 0 && _dungeonIds.Contains(dungeonId);

        private static double NormalizeRate(double value)
            => value > 0.0 && value <= 1.0
                && !double.IsNaN(value)
                && !double.IsInfinity(value)
                ? value
                : 0.0;
    }

    // Frozen entry result. A zero-rate result is still resolved so settlement
    // retries never reread channel configuration.
    internal sealed class ChannelExperienceSelection
    {
        private ChannelExperienceSelection(
            bool resolved,
            int channelId,
            int channelType,
            string dungeonClassification,
            double bonusRate)
        {
            IsResolved = resolved;
            ChannelId = channelId;
            ChannelType = channelType;
            DungeonClassification = dungeonClassification ?? string.Empty;
            BonusRate = bonusRate;
        }

        internal static ChannelExperienceSelection None { get; } =
            new ChannelExperienceSelection(
                resolved: true,
                channelId: 0,
                channelType: 0,
                dungeonClassification: string.Empty,
                bonusRate: 0.0);

        internal bool IsResolved { get; }
        internal int ChannelId { get; }
        internal int ChannelType { get; }
        internal string DungeonClassification { get; }
        internal double BonusRate { get; }

        internal static ChannelExperienceSelection Create(
            int channelId,
            int channelType,
            string dungeonClassification,
            double bonusRate)
            => new ChannelExperienceSelection(
                resolved: true,
                channelId,
                channelType,
                dungeonClassification,
                bonusRate > 0.0 ? bonusRate : 0.0);
    }

    internal static class ChannelExperienceDefinitionCatalog
    {
        private static readonly Lazy<ChannelExperienceCatalog> Catalog =
            new Lazy<ChannelExperienceCatalog>(LoadCatalog);

        internal static ChannelExperienceSelection Resolve(
            int channelId,
            int dungeonId)
        {
            if (channelId <= 0 || dungeonId <= 0)
                return ChannelExperienceSelection.None;

            return Catalog.Value.Resolve(channelId, dungeonId);
        }

        internal static int ConfiguredChannelCountForTest()
            => Catalog.Value.Definitions.Count;

        internal static ChannelExperienceSelection ResolveForTest(
            string text,
            int channelId,
            int dungeonId)
            => Parse(text ?? string.Empty).Resolve(channelId, dungeonId);

        private static ChannelExperienceCatalog LoadCatalog()
        {
            try
            {
                var path = ServerPaths.ChannelInfoFilePath;
                var text = File.ReadAllText(path);
                var catalog = Parse(text);
                FileLogger.Log(
                    $"[ChannelExperienceDefinition] loaded path={path} "
                    + $"channels={catalog.Definitions.Count} "
                    + $"dungeonGroups={catalog.DungeonGroups.Count}");
                return catalog;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ChannelExperienceDefinition] failed to load "
                    + $"{ServerPaths.ChannelInfoFilePath}: {ex.Message}");
                return new ChannelExperienceCatalog();
            }
        }

        private static ChannelExperienceCatalog Parse(string text)
        {
            var catalog = new ChannelExperienceCatalog();
            var root = new ScriptParser().Parse(text ?? string.Empty);
            foreach (var node in root.GetChildren("dungeon"))
                ParseDungeonGroup(node, text, catalog);

            foreach (var server in root.GetChildren("server"))
            {
                foreach (var item in server.DataItems)
                    ParseServerEntry(item.GetContent(text), catalog);
            }

            return catalog;
        }

        private static void ParseDungeonGroup(
            ScriptNode node,
            string text,
            ChannelExperienceCatalog catalog)
        {
            if (node == null)
                return;

            var classification = string.Empty;
            var dungeonIds = new HashSet<int>();
            foreach (var item in node.DataItems)
            {
                var tokens = ScriptValueTokenizer.Tokenize(item.GetContent(text));
                foreach (var token in tokens)
                {
                    var normalized = NormalizeClassification(token);
                    if (normalized.StartsWith("[", StringComparison.Ordinal)
                        && normalized.EndsWith("]", StringComparison.Ordinal))
                    {
                        if (string.IsNullOrEmpty(classification))
                            classification = normalized;
                        continue;
                    }

                    if (int.TryParse(
                            token,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var dungeonId)
                        && dungeonId > 0)
                    {
                        dungeonIds.Add(dungeonId);
                    }
                }
            }

            if (!string.IsNullOrEmpty(classification))
                catalog.AddDungeonGroup(classification, dungeonIds);
        }

        private static void ParseServerEntry(
            string line,
            ChannelExperienceCatalog catalog)
        {
            var tokens = ScriptValueTokenizer.Tokenize(line);
            if (tokens.Count == 0
                || !int.TryParse(
                    tokens[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var channelId)
                || channelId <= 0)
            {
                return;
            }

            // [server] starts with the group-count scalar. It is not a
            // channel row and must not poison a real channel with the same ID.
            if (tokens.Count == 1)
                return;

            if (tokens.Count < 5
                || !int.TryParse(
                    tokens[2],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var channelType)
                || channelType < 0)
            {
                catalog.InvalidateChannel(channelId);
                return;
            }

            var classification = NormalizeClassification(tokens[3]);
            if (!int.TryParse(
                    tokens[4],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var percentage)
                || percentage < 0
                || percentage > 100)
            {
                catalog.InvalidateChannel(channelId);
                return;
            }

            catalog.AddDefinition(new ChannelExperienceDefinition(
                channelId,
                channelType,
                classification,
                percentage / 100.0,
                catalog.DungeonGroups.TryGetValue(
                    classification,
                    out var dungeonIds)
                    ? dungeonIds
                    : Array.Empty<int>()));
        }

        private static string NormalizeClassification(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim().Trim('`');
            return normalized;
        }

        private sealed class ChannelExperienceCatalog
        {
            internal Dictionary<int, ChannelExperienceDefinition> Definitions { get; } =
                new Dictionary<int, ChannelExperienceDefinition>();

            internal Dictionary<string, HashSet<int>> DungeonGroups { get; } =
                new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

            private readonly HashSet<int> _invalidChannelIds =
                new HashSet<int>();
            private readonly HashSet<string> _invalidDungeonGroups =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            internal void AddDungeonGroup(
                string classification,
                HashSet<int> dungeonIds)
            {
                if (string.IsNullOrWhiteSpace(classification)
                    || _invalidDungeonGroups.Contains(classification))
                {
                    return;
                }

                if (DungeonGroups.ContainsKey(classification))
                {
                    DungeonGroups.Remove(classification);
                    _invalidDungeonGroups.Add(classification);
                    return;
                }

                DungeonGroups[classification] = dungeonIds
                    ?? new HashSet<int>();
            }

            internal void AddDefinition(ChannelExperienceDefinition definition)
            {
                if (definition == null
                    || _invalidChannelIds.Contains(definition.ChannelId))
                {
                    return;
                }

                if (Definitions.ContainsKey(definition.ChannelId))
                {
                    Definitions.Remove(definition.ChannelId);
                    _invalidChannelIds.Add(definition.ChannelId);
                    return;
                }

                Definitions[definition.ChannelId] = definition;
            }

            internal void InvalidateChannel(int channelId)
            {
                if (channelId <= 0)
                    return;

                Definitions.Remove(channelId);
                _invalidChannelIds.Add(channelId);
            }

            internal ChannelExperienceSelection Resolve(
                int channelId,
                int dungeonId)
            {
                if (!Definitions.TryGetValue(channelId, out var definition))
                    return ChannelExperienceSelection.None;

                var rate = definition.MatchesDungeon(dungeonId)
                    ? definition.BonusRate
                    : 0.0;
                return ChannelExperienceSelection.Create(
                    definition.ChannelId,
                    definition.ChannelType,
                    definition.DungeonClassification,
                    rate);
            }
        }
    }
}
