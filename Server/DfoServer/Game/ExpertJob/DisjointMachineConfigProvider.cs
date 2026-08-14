using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class DisjointMachineResultRule
    {
        internal int ItemId { get; set; }
        internal double Multiplier { get; set; }
        internal int AdditionalTable { get; set; }
        internal int BigWinTable { get; set; }
        internal int BigWinChancePercent { get; set; }
    }

    internal sealed class DisjointMachineSelectionRule : ExpertJobSelectionRule
    {
        internal double CountDivisor { get; set; }
    }

    internal sealed class DisjointMachineRepairRule
    {
        internal int FullRepairCost { get; set; }
        internal int MaximumEndurance { get; set; }
    }

    internal sealed class DisjointMachineConfig
    {
        internal int InitialEndurance { get; set; }
        internal int MaximumStoreCharge { get; set; }
        internal int BaseConst { get; set; }
        internal int EnduranceReduceMin { get; set; }
        internal int EnduranceReduceMax { get; set; }
        internal int GainExpMin { get; set; }
        internal int GainExpMax { get; set; }
        internal int SelfServiceChancePercent { get; set; }
        internal int SelfServiceItemId { get; set; }
        internal int SelfServiceItemCount { get; set; }
        internal Dictionary<(int MachineGrade, int Rarity, int EquipmentState), DisjointMachineResultRule>
            Results { get; } =
                new Dictionary<(int MachineGrade, int Rarity, int EquipmentState), DisjointMachineResultRule>();
        internal Dictionary<int, List<DisjointMachineSelectionRule>> AdditionalResults { get; } =
            new Dictionary<int, List<DisjointMachineSelectionRule>>();
        internal Dictionary<int, List<DisjointMachineSelectionRule>> BigWinResults { get; } =
            new Dictionary<int, List<DisjointMachineSelectionRule>>();
        internal List<DisjointMachineRepairRule> RepairRules { get; } =
            new List<DisjointMachineRepairRule>();
        internal List<int> ExpertJobExperienceThresholds { get; } = new List<int>();
        internal Dictionary<int, int> UpgradeCosts { get; } = new Dictionary<int, int>();
        internal Dictionary<int, int> CharacterLevelLimits { get; } = new Dictionary<int, int>();

        internal DisjointMachineResultRule GetResult(
            int machineGrade,
            int rarity,
            int equipmentState)
        {
            Results.TryGetValue((machineGrade, rarity, equipmentState), out var result);
            return result;
        }

        internal DisjointMachineRepairRule GetRepairRule(int machineGrade)
        {
            var index = machineGrade - 1;
            return index >= 0 && index < RepairRules.Count ? RepairRules[index] : null;
        }

        internal int GetUpgradeCost(int targetGrade)
            => UpgradeCosts.TryGetValue(targetGrade, out var cost) ? cost : -1;

        internal int GetExpertJobLevel(uint experience)
        {
            var level = 1;
            foreach (var threshold in ExpertJobExperienceThresholds)
            {
                if (experience < threshold)
                    break;
                level++;
            }
            return level;
        }

        internal int GetMinimumCharacterLevel(int targetGrade)
            => CharacterLevelLimits.TryGetValue(targetGrade, out var level) ? level : int.MaxValue;
    }

    internal static class DisjointMachineConfigProvider
    {
        private const string PvfPath = "character/expertjob/disjointer.exj";
        private const string ExpertJobEtcPvfPath = "character/expertjob.etc";
        private static readonly Lazy<DisjointMachineConfig> ConfigValue =
            new Lazy<DisjointMachineConfig>(Load);

        internal static int InitialEndurance => ConfigValue.Value.InitialEndurance;
        internal static DisjointMachineConfig Config => ConfigValue.Value;

        private static DisjointMachineConfig Load()
        {
            var content = PvfArchiveAccessor.ReadText(PvfPath);
            var root = new ScriptParser().Parse(content);
            var config = new DisjointMachineConfig
            {
                InitialEndurance = ReadSingleInt(root, content, "endurance initial value", 0),
                MaximumStoreCharge = ReadSingleInt(root, content, "limit store charge", 0),
                BaseConst = ReadSingleInt(root, content, "base const", 0),
            };
            ReadPair(root, content, "endurance reduce", out var reduceMin, out var reduceMax);
            config.EnduranceReduceMin = reduceMin;
            config.EnduranceReduceMax = reduceMax;
            ReadPair(root, content, "gain exp", out var expMin, out var expMax);
            config.GainExpMin = expMin;
            config.GainExpMax = expMax;

            var selfTokens = ReadTokens(root, content, "disjoint self service");
            if (selfTokens.Length >= 3)
            {
                config.SelfServiceChancePercent = ParseInt(selfTokens[0]);
                config.SelfServiceItemId = ParseInt(selfTokens[1]);
                config.SelfServiceItemCount = ParseInt(selfTokens[2]);
            }

            ParseResults(ReadTokens(root, content, "disjoint result"), config);
            ParseSelections(ReadTokens(root, content, "additional result"), config.AdditionalResults);
            ParseSelections(ReadTokens(root, content, "big win result"), config.BigWinResults);
            ParseRepairRules(ReadTokens(root, content, "endurance repair cost"), config);
            ParseExpertnessExperience(ReadTokens(root, content, "expertness exp"), config);
            ParsePairs(ReadTokens(root, content, "upgrade cost"), config.UpgradeCosts, "upgrade cost");

            var etcContent = PvfArchiveAccessor.ReadText(ExpertJobEtcPvfPath);
            var etcRoot = new ScriptParser().Parse(etcContent);
            ParsePairs(
                ReadTokens(etcRoot, etcContent, "expertjob level limit"),
                config.CharacterLevelLimits,
                "expertjob level limit");
            if (config.InitialEndurance <= 0
                || config.BaseConst <= 0
                || config.MaximumStoreCharge < 0
                || config.EnduranceReduceMin < 0
                || config.EnduranceReduceMax < config.EnduranceReduceMin
                || config.RepairRules.Count == 0
                || config.ExpertJobExperienceThresholds.Count == 0
                || config.UpgradeCosts.Count != config.RepairRules.Count
                || config.CharacterLevelLimits.Count < config.RepairRules.Count
                || config.Results.Count == 0)
            {
                throw new InvalidOperationException($"PVF {PvfPath} has invalid disjointer configuration");
            }

            return config;
        }

        private static void ParseExpertnessExperience(
            string[] tokens,
            DisjointMachineConfig config)
        {
            if (tokens.Length == 0 || tokens.Length % 3 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [expertness exp] row width is not 3");

            var previous = -1;
            for (var index = 0; index < tokens.Length; index += 3)
            {
                var threshold = ParseInt(tokens[index]);
                if (threshold <= previous)
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid expertness thresholds");
                config.ExpertJobExperienceThresholds.Add(threshold);
                previous = threshold;
            }
        }

        private static void ParsePairs(
            string[] tokens,
            Dictionary<int, int> target,
            string tag)
        {
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                throw new InvalidOperationException($"PVF [{tag}] row width is not 2");

            for (var index = 0; index < tokens.Length; index += 2)
            {
                var key = ParseInt(tokens[index]);
                var value = ParseInt(tokens[index + 1]);
                if (key <= 0 || value < 0 || target.ContainsKey(key))
                    throw new InvalidOperationException($"PVF [{tag}] has invalid entry");
                target.Add(key, value);
            }
        }

        private static void ParseRepairRules(string[] tokens, DisjointMachineConfig config)
        {
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [endurance repair cost] row width is not 2");

            for (var index = 0; index < tokens.Length; index += 2)
            {
                var rule = new DisjointMachineRepairRule
                {
                    FullRepairCost = ParseInt(tokens[index]),
                    MaximumEndurance = ParseInt(tokens[index + 1]),
                };
                if (rule.FullRepairCost <= 0 || rule.MaximumEndurance <= 0)
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid repair rule");
                config.RepairRules.Add(rule);
            }
        }

        private static void ParseResults(string[] tokens, DisjointMachineConfig config)
        {
            if (tokens.Length % 8 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [disjoint result] row width is not 8");

            for (var index = 0; index < tokens.Length; index += 8)
            {
                var machineGrade = ParseInt(tokens[index]);
                var rarity = ParseInt(tokens[index + 1]);
                var equipmentState = ParseInt(tokens[index + 2]);
                var key = (machineGrade, rarity, equipmentState);
                if (config.Results.ContainsKey(key))
                    throw new InvalidOperationException($"PVF {PvfPath} has duplicate disjoint result");
                config.Results[key] =
                    new DisjointMachineResultRule
                    {
                        ItemId = ParseInt(tokens[index + 3]),
                        Multiplier = ParseDouble(tokens[index + 4]),
                        AdditionalTable = ParseInt(tokens[index + 5]),
                        BigWinTable = ParseInt(tokens[index + 6]),
                        BigWinChancePercent = ParseInt(tokens[index + 7]),
                    };
            }
        }

        private static void ParseSelections(
            string[] tokens,
            Dictionary<int, List<DisjointMachineSelectionRule>> target)
        {
            if (tokens.Length % 6 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} selection row width is not 6");

            for (var index = 0; index < tokens.Length; index += 6)
            {
                var table = ParseInt(tokens[index]);
                if (!target.TryGetValue(table, out var rows))
                {
                    rows = new List<DisjointMachineSelectionRule>();
                    target.Add(table, rows);
                }
                rows.Add(new DisjointMachineSelectionRule
                {
                    MinimumLevel = ParseInt(tokens[index + 1]),
                    MaximumLevel = ParseInt(tokens[index + 2]),
                    ItemId = ParseInt(tokens[index + 3]),
                    Weight = ParseInt(tokens[index + 4]),
                    CountDivisor = ParseDouble(tokens[index + 5]),
                });
            }
        }

        private static int ReadSingleInt(ScriptNode root, string content, string tag, int fallback)
        {
            var tokens = ReadTokens(root, content, tag);
            return tokens.Length == 0 ? fallback : ParseInt(tokens[0]);
        }

        private static void ReadPair(
            ScriptNode root,
            string content,
            string tag,
            out int minimum,
            out int maximum)
        {
            var tokens = ReadTokens(root, content, tag);
            minimum = tokens.Length > 0 ? ParseInt(tokens[0]) : 0;
            maximum = tokens.Length > 1 ? ParseInt(tokens[1]) : minimum;
        }

        private static string[] ReadTokens(ScriptNode root, string content, string tag)
        {
            var node = root.Children.FirstOrDefault(child =>
                string.Equals(child.Tag, tag, StringComparison.OrdinalIgnoreCase));
            return node == null
                ? Array.Empty<string>()
                : node.GetFirstDataContent(content)
                    .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        private static int ParseInt(string value)
            => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static double ParseDouble(string value)
            => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
