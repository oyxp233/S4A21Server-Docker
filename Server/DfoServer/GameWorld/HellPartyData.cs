using System;
using System.Collections.Generic;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal sealed class HellPartyGroupEntry
    {
        public int Code { get; set; }
        public byte EntityType { get; set; }
    }

    public sealed class HellPartyDifficultyRule
    {
        public char Key { get; set; }
        public int RewardRollCount { get; set; }
        public int Unknown1 { get; set; }
        public int RatioProbability { get; set; }
        public int Probability { get; set; }
        public int Unknown4 { get; set; }
    }

    internal static class HellPartyData
    {
        private static readonly Lazy<Dictionary<int, HellPartyMonsterGroup>> Groups =
            new Lazy<Dictionary<int, HellPartyMonsterGroup>>(LoadGroups);

        private static readonly Lazy<Dictionary<char, HellPartyDifficultyRule>> DifficultyRules =
            new Lazy<Dictionary<char, HellPartyDifficultyRule>>(LoadDifficultyRules);

        public static IReadOnlyList<HellPartyGroupEntry> GetEntries(int groupId, byte difficulty)
        {
            if (!Groups.Value.TryGetValue(groupId, out var group))
                return Array.Empty<HellPartyGroupEntry>();

            return group.GetEntries(HellModeToGroupKey(difficulty));
        }

        public static bool HasEntries(int groupId, byte difficulty)
        {
            if (!Groups.Value.TryGetValue(groupId, out var group))
                return false;

            return group.HasEntries(HellModeToGroupKey(difficulty));
        }

        public static HellPartyDifficultyRule GetDifficultyRule(byte difficulty)
        {
            DifficultyRules.Value.TryGetValue(HellModeToGroupKey(difficulty), out var rule);
            return rule;
        }

        public static byte PickManualHellPartyMode()
        {
            var rules = DifficultyRules.Value;
            rules.TryGetValue('A', out var veryHard);
            rules.TryGetValue('B', out var hard);

            // [difficulty] 第 4 项是手动开启深渊时的 A/B 权重；配置缺失时沿用非常困难。
            var veryHardWeight = Math.Max(0, veryHard?.Probability ?? 0);
            var hardWeight = Math.Max(0, hard?.Probability ?? 0);
            var total = veryHardWeight + hardWeight;
            if (total <= 0)
                return 1;

            var roll = Infrastructure.ServerRandom.Next(total);
            return roll < veryHardWeight ? (byte)1 : (byte)2;
        }

        private static Dictionary<int, HellPartyMonsterGroup> LoadGroups()
        {
            var result = new Dictionary<int, HellPartyMonsterGroup>();
            var content = ReadHellPartyScript();
            if (string.IsNullOrWhiteSpace(content))
                return result;

            var root = new ScriptParser().Parse(content);
            foreach (var node in root.GetChildren("hellparty monster group"))
                ParseSequentialGroupNodes(node.Children, content, result);

            ParseSequentialGroupNodes(root.Children, content, result);

            return result;
        }

        private static Dictionary<char, HellPartyDifficultyRule> LoadDifficultyRules()
        {
            var result = new Dictionary<char, HellPartyDifficultyRule>();
            var content = ReadHellPartyScript();
            if (string.IsNullOrWhiteSpace(content))
                return result;

            var root = new ScriptParser().Parse(content);
            foreach (var node in root.GetChildren("difficulty"))
                ParseDifficultyNode(node, content, result);

            return result;
        }

        private static void ParseSequentialGroupNodes(
            IReadOnlyList<ScriptNode> nodes,
            string content,
            Dictionary<int, HellPartyMonsterGroup> result)
        {
            HellPartyMonsterGroup current = null;
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.Tag.Equals("group index", StringComparison.OrdinalIgnoreCase))
                {
                    var groupIndex = ParseFirstInt(node.GetFirstDataContent(content));
                    if (groupIndex < 0)
                    {
                        current = null;
                        continue;
                    }

                    if (!result.TryGetValue(groupIndex, out current))
                    {
                        current = new HellPartyMonsterGroup();
                        result[groupIndex] = current;
                    }
                    continue;
                }

                if (current != null && node.Tag.Equals("group", StringComparison.OrdinalIgnoreCase))
                    ParseGroupNode(node, content, current);
            }
        }

        private static string ReadHellPartyScript()
        {
            try { return PvfArchiveAccessor.ReadText("etc/hellparty.etc"); }
            catch
            {
                try { return PvfArchiveAccessor.ReadText("Etc/hellparty.etc"); }
                catch { return string.Empty; }
            }
        }

        private static void ParseGroupNode(ScriptNode node, string content, HellPartyMonsterGroup target)
        {
            var tokens = JoinDataItems(node, content)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var difficulty = '\0';
            int? pendingCode = null;
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = StripBacktick(tokens[i]);
                if (token.Length == 1 && char.IsLetter(token[0]))
                {
                    difficulty = char.ToUpperInvariant(token[0]);
                    pendingCode = null;
                    target.Ensure(difficulty);
                    continue;
                }

                if (!int.TryParse(token, out var value))
                    continue;

                if (difficulty == '\0')
                    difficulty = 'A';

                if (!pendingCode.HasValue)
                {
                    pendingCode = value;
                    continue;
                }

                target.Ensure(difficulty).Add(new HellPartyGroupEntry
                {
                    Code = pendingCode.Value,
                    EntityType = value == 1 ? (byte)1 : (byte)0,
                });
                pendingCode = null;
            }
        }

        private static void ParseDifficultyNode(
            ScriptNode node,
            string content,
            Dictionary<char, HellPartyDifficultyRule> result)
        {
            var tokens = JoinDataItems(node, content)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var i = 0;
            if (i < tokens.Length && int.TryParse(StripBacktick(tokens[i]), out _))
                i++;

            while (i < tokens.Length)
            {
                var keyToken = StripBacktick(tokens[i++]);
                if (keyToken.Length != 1 || !char.IsLetter(keyToken[0]))
                    continue;

                var values = new int[5];
                var valueCount = 0;
                while (i < tokens.Length && valueCount < values.Length)
                {
                    var token = StripBacktick(tokens[i++]);
                    if (int.TryParse(token, out var value))
                        values[valueCount++] = value;
                }

                if (valueCount != values.Length)
                    continue;

                result[char.ToUpperInvariant(keyToken[0])] = new HellPartyDifficultyRule
                {
                    Key = char.ToUpperInvariant(keyToken[0]),
                    RewardRollCount = values[0],
                    Unknown1 = values[1],
                    RatioProbability = values[2],
                    Probability = values[3],
                    Unknown4 = values[4],
                };
            }
        }

        private static string JoinDataItems(ScriptNode node, string content)
        {
            if (node == null || node.DataItems.Count == 0)
                return string.Empty;

            var parts = new List<string>();
            foreach (var item in node.DataItems)
                parts.Add(item.GetContent(content).Trim());
            return string.Join(" ", parts);
        }

        private static int ParseFirstInt(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return -1;

            var tokens = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++)
                if (int.TryParse(StripBacktick(tokens[i]), out var value))
                    return value;
            return -1;
        }

        private static string StripBacktick(string value)
        {
            if (value != null && value.Length >= 2 && value[0] == '`' && value[value.Length - 1] == '`')
                return value.Substring(1, value.Length - 2);
            return value ?? string.Empty;
        }

        private static char HellModeToGroupKey(byte difficulty)
        {
            return difficulty == 2 ? 'B' : 'A';
        }

        private sealed class HellPartyMonsterGroup
        {
            private readonly Dictionary<char, List<HellPartyGroupEntry>> _entries =
                new Dictionary<char, List<HellPartyGroupEntry>>();

            public List<HellPartyGroupEntry> Ensure(char difficulty)
            {
                if (!_entries.TryGetValue(difficulty, out var list))
                {
                    list = new List<HellPartyGroupEntry>();
                    _entries[difficulty] = list;
                }
                return list;
            }

            public IReadOnlyList<HellPartyGroupEntry> GetEntries(char difficulty)
            {
                if (_entries.TryGetValue(difficulty, out var exact) && exact.Count > 0)
                    return exact;

                return Array.Empty<HellPartyGroupEntry>();
            }

            public bool HasEntries(char difficulty)
            {
                return _entries.TryGetValue(difficulty, out var exact) && exact.Count > 0;
            }
        }
    }
}
