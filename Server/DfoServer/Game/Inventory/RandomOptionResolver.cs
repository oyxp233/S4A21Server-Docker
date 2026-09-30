using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{

    internal static class RandomOptionResolver
    {
        private static readonly Lazy<Dictionary<int, List<WeightedOption>>> OptionGroups =
            new Lazy<Dictionary<int, List<WeightedOption>>>(LoadOptionGroups);

        private static readonly Lazy<Dictionary<int, string>> OptionFiles =
            new Lazy<Dictionary<int, string>>(LoadOptionFiles);

        private static readonly Lazy<string> OptionGroupSelection =
            new Lazy<string>(() => PvfArchiveAccessor.ReadText("etc/randomoption/optiongroupselection.etc"));

        private static readonly Lazy<Dictionary<string, List<QuantityWeight>>> QuantityWeights =
            new Lazy<Dictionary<string, List<QuantityWeight>>>(LoadQuantityWeights);

        private static readonly Lazy<List<BreakSealCostEntry>> BreakSealCosts =
            new Lazy<List<BreakSealCostEntry>>(LoadBreakSealCosts);

        private static readonly Lazy<List<OptionModificationCostEntry>> OptionModificationCosts =
            new Lazy<List<OptionModificationCostEntry>>(LoadOptionModificationCosts);

        private static readonly Lazy<Dictionary<int, List<GradeModificationBand>>> GradeModificationBands =
            new Lazy<Dictionary<int, List<GradeModificationBand>>>(LoadGradeModificationBands);

        private static readonly Lazy<Dictionary<int, List<DifferentWeightEntry>>> DifferentWeights =
            new Lazy<Dictionary<int, List<DifferentWeightEntry>>>(LoadDifferentWeights);

        private static readonly Lazy<HashSet<int>> UnableToModifyPostfixGrades =
            new Lazy<HashSet<int>>(() => new HashSet<int>(ReadSectionInts(
                PvfArchiveAccessor.ReadText("etc/randomoption/randomizedoptionoverall2.etc"),
                "unable to modify postfix grade")));

        private static readonly Lazy<HashSet<int>> GradelessPostfixes =
            new Lazy<HashSet<int>>(() => new HashSet<int>(ReadSectionInts(
                PvfArchiveAccessor.ReadText("etc/randomoption/randomizedoptionoverall2.etc"),
                "gradeless postfix")));

        public static bool TryRollOptions(ItemMetadata metadata, out List<RandomOptionEntry> entries)
        {
            entries = null;
            if (metadata == null || !string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
                return false;

            var groups = ResolveGroups(metadata);
            if (groups.Count == 0)
                return false;

            var quantity = RollQuantity(metadata);
            var picked = new List<RandomOptionEntry>();
            var usedOptionIds = new HashSet<int>();

            for (var i = 0; i < quantity && picked.Count < 3; i++)
            {
                var groupId = groups[Math.Min(i, groups.Count - 1)];
                if (!OptionGroups.Value.TryGetValue(groupId, out var weightedOptions) || weightedOptions.Count == 0)
                    continue;

                for (var attempt = 0; attempt < 12; attempt++)
                {
                    var optionId = RollWeighted(weightedOptions).OptionId;
                    if (!usedOptionIds.Add(optionId))
                        continue;

                    picked.Add(RollOptionValue(optionId, metadata.MinimumLevel, metadata.Rarity, picked.Count + 1));
                    break;
                }
            }

            if (picked.Count == 0)
                return false;

            entries = picked;
            return true;
        }

        public static bool TryRollReplacementOption(ItemMetadata metadata, int optionIndex, IReadOnlyList<RandomOptionEntry> existingEntries, out RandomOptionEntry entry)
        {
            entry = null;
            if (metadata == null || !string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
                return false;

            var groups = ResolveModifiedGroups(metadata);
            if (groups.Count == 0)
                groups = ResolveGroups(metadata);
            if (groups.Count == 0)
                return false;

            var safeIndex = Math.Max(0, optionIndex);
            var groupId = groups[Math.Min(safeIndex, groups.Count - 1)];
            if (!OptionGroups.Value.TryGetValue(groupId, out var weightedOptions) || weightedOptions.Count == 0)
                return false;

            var usedOptionIds = new HashSet<int>();
            if (existingEntries != null)
            {
                for (var i = 0; i < existingEntries.Count; i++)
                {
                    if (i == safeIndex || existingEntries[i] == null)
                        continue;

                    usedOptionIds.Add(existingEntries[i].Type);
                }
            }

            for (var attempt = 0; attempt < 24; attempt++)
            {
                var optionId = RollWeighted(weightedOptions).OptionId;
                if (usedOptionIds.Contains(optionId))
                    continue;

                entry = RollOptionValue(optionId, metadata.MinimumLevel, metadata.Rarity, safeIndex + 1);
                return true;
            }

            foreach (var candidate in weightedOptions)
            {
                if (usedOptionIds.Contains(candidate.OptionId))
                    continue;

                entry = RollOptionValue(candidate.OptionId, metadata.MinimumLevel, metadata.Rarity, safeIndex + 1);
                return true;
            }

            entry = RollOptionValue(RollWeighted(weightedOptions).OptionId, metadata.MinimumLevel, metadata.Rarity, safeIndex + 1);
            return true;
        }

        public static List<int> ResolveChangeOptionCandidates(ItemMetadata metadata, int optionIndex)
        {
            var groups = ResolveModifiedGroups(metadata);
            if (groups.Count == 0)
                groups = ResolveGroups(metadata);
            if (groups.Count == 0)
                return new List<int>();

            var safeIndex = Math.Max(0, optionIndex);
            var groupId = groups[Math.Min(safeIndex, groups.Count - 1)];
            if (!OptionGroups.Value.TryGetValue(groupId, out var weightedOptions) || weightedOptions.Count == 0)
                return new List<int>();

            return weightedOptions
                .Select(x => x.OptionId)
                .Distinct()
                .Take(14)
                .ToList();
        }

        public static int ResolveBreakSealGoldCost(ItemMetadata metadata)
        {
            if (metadata == null)
                return 0;

            return ResolveBreakSealGoldCost(metadata.Rarity, metadata.MinimumLevel, 1);
        }

        public static int ResolveOptionModificationGoldCost(ItemMetadata metadata)
        {
            if (metadata == null)
                return 0;

            return ResolveOptionModificationGoldCost(metadata.Rarity, metadata.MinimumLevel);
        }

        private static List<int> ResolveGroups(ItemMetadata metadata)
        {
            return ResolveGroups(metadata, "choose option group");
        }

        private static List<int> ResolveModifiedGroups(ItemMetadata metadata)
        {
            return ResolveGroups(metadata, "modified option selection");
        }

        private static List<int> ResolveGroups(ItemMetadata metadata, string sectionName)
        {
            var equipmentKeys = BuildEquipmentKeys(metadata);
            if (equipmentKeys.Count == 0)
                return new List<int>();

            var sectionText = ReadSectionText(OptionGroupSelection.Value, sectionName);
            if (string.IsNullOrWhiteSpace(sectionText))
                return new List<int>();

            foreach (var equipmentKey in equipmentKeys)
            {
                var groups = ResolveGroupsForKey(sectionText, equipmentKey, metadata.Rarity);
                if (groups.Count > 0)
                    return groups;
            }

            return new List<int>();
        }

        private static List<int> ResolveGroupsForKey(string sectionText, string equipmentKey, int itemRarity)
        {
            var result = new List<int>();
            var tokens = Tokenize(sectionText);
            for (var i = 0; i < tokens.Count; i++)
            {
                if (!string.Equals(tokens[i], equipmentKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                var rarityMatches = i >= 2
                    && int.TryParse(tokens[i - 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rarity)
                    && rarity == itemRarity;

                if (!rarityMatches && result.Count > 0)
                    continue;

                var groups = new List<int>();
                for (var j = i + 1; j < tokens.Count && groups.Count < 3; j++)
                {
                    if (!int.TryParse(tokens[j], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                        break;

                    if (value >= 1000 && value <= 1999)
                        groups.Add(value);
                }

                if (groups.Count == 0)
                    continue;

                if (rarityMatches)
                    return groups;

                result = groups;
            }

            return result;
        }

        private static List<string> BuildEquipmentKeys(ItemMetadata metadata)
        {
            var result = new List<string>();
            var equipmentKey = NormalizeEquipmentKey(metadata.EquipmentType);
            if (string.IsNullOrWhiteSpace(equipmentKey))
                return result;

            if (string.Equals(equipmentKey, "weapon", StringComparison.OrdinalIgnoreCase))
            {
                var weaponKey = ResolveWeaponKey(metadata.PvfFilePath);
                if (!string.IsNullOrWhiteSpace(weaponKey))
                    result.Add(weaponKey);
            }

            var armorPrefix = ResolveArmorPrefix(metadata.PvfFilePath);
            if (!string.IsNullOrWhiteSpace(armorPrefix) && IsArmorRandomOptionPart(equipmentKey))
                result.Add(armorPrefix + " " + equipmentKey);

            result.Add(equipmentKey);
            return result;
        }

        private static bool IsArmorRandomOptionPart(string equipmentKey)
        {
            return string.Equals(equipmentKey, "coat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(equipmentKey, "pants", StringComparison.OrdinalIgnoreCase)
                || string.Equals(equipmentKey, "shoulder", StringComparison.OrdinalIgnoreCase)
                || string.Equals(equipmentKey, "waist", StringComparison.OrdinalIgnoreCase)
                || string.Equals(equipmentKey, "shoes", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveArmorPrefix(string pvfFilePath)
        {
            var path = (pvfFilePath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            if (path.Contains("/cloth/")) return "cl";
            if (path.Contains("/leather/")) return "lt";
            if (path.Contains("/larmor/")) return "la";
            if (path.Contains("/harmor/")) return "ha";
            if (path.Contains("/plate/")) return "mt";
            return null;
        }

        private static string ResolveWeaponKey(string pvfFilePath)
        {
            var path = (pvfFilePath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i + 1 < parts.Length; i++)
            {
                if (!string.Equals(parts[i], "weapon", StringComparison.OrdinalIgnoreCase))
                    continue;

                return NormalizeWeaponPart(parts[i + 1]);
            }

            return null;
        }

        private static string NormalizeWeaponPart(string part)
        {
            switch ((part ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "boxglove": return "bglove";
                case "hsword": return "lswd";
                case "beamsword": return "beamswd";
                case "twinsword": return "twinswd";
                case "chakram": return "chakraweapon";
                default: return string.IsNullOrWhiteSpace(part) ? null : part.Trim().ToLowerInvariant();
            }
        }

        private static int RollQuantity(ItemMetadata metadata)
        {
            var key = metadata.Rarity >= 3 ? "unique" : "common";
            if (!QuantityWeights.Value.TryGetValue(key, out var weights) || weights.Count == 0)
                return 1;

            return Math.Max(1, Math.Min(3, RollWeighted(weights).Quantity));
        }

        // 解封/变换词条: Value2 的语义是品级百分比 p(客户端按 [choose postfix] 阈值表直接查
        // 字母 C/B/A/S), 不是数值上限; p 按 [different weight] 表按 (稀有度, 槽位) roll,
        // Value1 = 官方 PvE 区间按 p 线性插值。internal: SelfTest 需要定点验证 roll 行为。
        internal static RandomOptionEntry RollOptionValue(int optionId, int itemLevel, int rarity, int slotIndex)
        {
            if (!OptionFiles.Value.TryGetValue(optionId, out var relativePath))
                return new RandomOptionEntry { Type = ClampByte(optionId), Value1 = 1, Value2 = 0 };

            try
            {
                var text = PvfArchiveAccessor.ReadText("etc/randomoption/" + relativePath);
                var values = ResolveLevelValues(text, itemLevel);
                var (minEff, maxEff) = ClampRangeToByteDomain(values.value1, values.value2);
                var p = RollGradePercent(rarity, slotIndex);
                var value = minEff + (maxEff - minEff) * p / 100;
                value = Math.Max(minEff, Math.Min(value, maxEff));
                return new RandomOptionEntry
                {
                    Type = ClampByte(optionId),
                    Value1 = ClampByte(value),
                    Value2 = ClampByte(p),
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[RandomOption] option=0x{optionId:X2} load failed: {ex.Message}");
                return new RandomOptionEntry { Type = ClampByte(optionId), Value1 = 1, Value2 = 0 };
            }
        }

        // 按 [different weight] 表 roll 品级百分比 p(槽位从 1 开始, clamp 到表内最大槽位);
        // 稀有度/槽位无条目时回退均匀 [0,56]。
        private static int RollGradePercent(int rarity, int slotIndex)
        {
            if (DifferentWeights.Value.TryGetValue(rarity, out var slots) && slots.Count > 0)
            {
                var slot = slots[Math.Max(1, Math.Min(slotIndex, slots.Count)) - 1];
                return Random.Shared.Next(slot.PctMin, slot.PctMax + 1);
            }

            return Random.Shared.Next(0, 57);
        }

        // 品级调整箱规则: 按 [postfix grade modification] 权重选档, 档内均匀取百分比 p,
        // 对属性官方 PvE 区间做线性插值重 roll 数值; 属性类型不变。
        // 返回 false 表示该属性不参与调整(跳过名单 / 无该稀有度档位表 / 数据缺失), 调用方保持原值。
        internal static bool TryRerollOptionValueForGradeChange(
            ItemMetadata metadata,
            int optionId,
            out RandomOptionEntry entry)
        {
            entry = null;
            if (metadata == null)
                return false;
            if (UnableToModifyPostfixGrades.Value.Contains(optionId) || GradelessPostfixes.Value.Contains(optionId))
                return false;
            if (!GradeModificationBands.Value.TryGetValue(metadata.Rarity, out var bands) || bands.Count == 0)
                return false;
            if (!OptionFiles.Value.TryGetValue(optionId, out var relativePath))
                return false;

            try
            {
                var text = PvfArchiveAccessor.ReadText("etc/randomoption/" + relativePath);
                var values = ResolveLevelValues(text, metadata.MinimumLevel);
                var (minEff, maxEff) = ClampRangeToByteDomain(values.value1, values.value2);

                var band = RollWeighted(bands);
                var p = Random.Shared.Next(band.PctMin, band.PctMax + 1);
                var value = minEff + (maxEff - minEff) * p / 100;
                value = Math.Max(minEff, Math.Min(value, maxEff));

                entry = new RandomOptionEntry
                {
                    Type = ClampByte(optionId),
                    Value1 = ClampByte(value),
                    Value2 = ClampByte(p),
                };
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[RandomOption] grade reroll option=0x{optionId:X2} failed: {ex.Message}");
                return false;
            }
        }

        private static (int minEff, int maxEff) ClampRangeToByteDomain(int minValue, int maxValue)
        {
            var maxEff = Math.Min(maxValue, 255);
            var minEff = Math.Min(minValue, maxEff);
            if (minEff > maxEff)
                minEff = maxEff;

            return (minEff, maxEff);
        }

        private static (int value1, int value2) ResolveLevelValues(string text, int itemLevel)
        {
            var bestLevel = -1;
            var bestValues = (value1: 1, value2: 1);

            // 只取第一个 [dungeon] 块内的等级行; 部分属性文件在 [pvp] 段还有一份数值更低的行,
            // 全文扫描会让同级的 PvP 行覆盖 PvE 行。无 [dungeon] 段的文件回退为全文扫描。
            var source = text ?? string.Empty;
            var dungeonMatch = Regex.Match(source, @"\[dungeon\](.*?)\[/dungeon\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (dungeonMatch.Success)
                source = dungeonMatch.Groups[1].Value;

            var matches = Regex.Matches(source, @"\[level\]\s*(.*?)\[/level\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                var ints = Regex.Matches(match.Groups[1].Value, @"-?\d+")
                    .Cast<Match>()
                    .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
                    .ToList();
                if (ints.Count < 3)
                    continue;

                var level = ints[0];
                if (level > itemLevel || level < bestLevel)
                    continue;

                bestLevel = level;
                bestValues = (ints[ints.Count - 2], ints[ints.Count - 1]);
            }

            return bestValues;
        }

        private static Dictionary<int, List<WeightedOption>> LoadOptionGroups()
        {
            var result = new Dictionary<int, List<WeightedOption>>();
            var tokens = Tokenize(PvfArchiveAccessor.ReadText("etc/randomoption/optiongrouping.etc"));
            for (var i = 0; i < tokens.Count; i++)
            {
                if (!string.Equals(tokens[i], "option group", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (i + 1 >= tokens.Count || !int.TryParse(tokens[i + 1], out var groupId))
                    continue;

                var entries = new List<WeightedOption>();
                for (var j = i + 2; j + 1 < tokens.Count; j += 2)
                {
                    if (!int.TryParse(tokens[j], NumberStyles.Integer, CultureInfo.InvariantCulture, out var optionId)
                        || !int.TryParse(tokens[j + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight))
                        break;

                    if (optionId >= 0 && weight > 0)
                        entries.Add(new WeightedOption { OptionId = optionId, Weight = weight });
                }

                if (entries.Count > 0)
                    result[groupId] = entries;
            }

            return result;
        }

        private static Dictionary<int, string> LoadOptionFiles()
        {
            var result = new Dictionary<int, string>();
            var tokens = Tokenize(PvfArchiveAccessor.ReadText("etc/randomoption/randomoption.lst"));
            for (var i = 0; i + 1 < tokens.Count; i += 2)
            {
                if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var optionId))
                    continue;

                var path = tokens[i + 1].Replace('\\', '/').Trim('`', ' ', '\t', '\r', '\n').ToLowerInvariant();
                result[optionId] = path;
            }

            return result;
        }

        private static int ResolveBreakSealGoldCost(int rarity, int itemLevel, int optionIndex)
        {
            BreakSealCostEntry best = null;
            foreach (var entry in BreakSealCosts.Value)
            {
                if (entry.Rarity != rarity || entry.OptionIndex != optionIndex || entry.Level > itemLevel)
                    continue;

                if (best == null || entry.Level > best.Level)
                    best = entry;
            }

            return Math.Max(0, best?.Cost ?? 0);
        }

        private static int ResolveOptionModificationGoldCost(int rarity, int itemLevel)
        {
            OptionModificationCostEntry best = null;
            foreach (var entry in OptionModificationCosts.Value)
            {
                if (entry.Level > itemLevel)
                    continue;

                if (best == null || entry.Level > best.Level)
                    best = entry;
            }

            if (best == null)
                return 0;

            return Math.Max(0, rarity >= 3 ? best.UniqueCost : best.CommonCost);
        }

        private static List<BreakSealCostEntry> LoadBreakSealCosts()
        {
            var text = PvfArchiveAccessor.ReadText("etc/randomoption/randomizedoptionoverall2.etc");
            var ints = ReadSectionInts(text, "break seal cost");
            var result = new List<BreakSealCostEntry>();
            for (var i = 0; i + 3 < ints.Count; i += 4)
            {
                result.Add(new BreakSealCostEntry
                {
                    Rarity = ints[i],
                    Level = ints[i + 1],
                    OptionIndex = ints[i + 2],
                    Cost = ints[i + 3],
                });
            }

            return result;
        }

        private static List<OptionModificationCostEntry> LoadOptionModificationCosts()
        {
            var text = PvfArchiveAccessor.ReadText("etc/randomoption/randomizedoptionoverall2.etc");
            var ints = ReadSectionInts(text, "option modification");
            var result = new List<OptionModificationCostEntry>();
            for (var i = 0; i + 2 < ints.Count; i += 3)
            {
                result.Add(new OptionModificationCostEntry
                {
                    Level = ints[i],
                    CommonCost = ints[i + 1],
                    UniqueCost = ints[i + 2],
                });
            }

            return result;
        }

        // [postfix grade modification] 每稀有度一组档位行: `稀有度 档序号 百分比下限 百分比上限 权重`,
        // 权重合计 1000。全文是连续数字, 按 5 个一组切分, 跳过不完整组。
        private static Dictionary<int, List<GradeModificationBand>> LoadGradeModificationBands()
        {
            var text = PvfArchiveAccessor.ReadText("etc/randomoption/randomizedoptionoverall2.etc");
            var ints = ReadSectionInts(text, "postfix grade modification");
            var result = new Dictionary<int, List<GradeModificationBand>>();
            for (var i = 0; i + 4 < ints.Count; i += 5)
            {
                var rarity = ints[i];
                if (rarity <= 0)
                    continue;

                if (!result.TryGetValue(rarity, out var bands))
                {
                    bands = new List<GradeModificationBand>();
                    result[rarity] = bands;
                }

                bands.Add(new GradeModificationBand
                {
                    Slot = ints[i + 1],
                    PctMin = ints[i + 2],
                    PctMax = ints[i + 3],
                    Weight = ints[i + 4],
                });
            }

            return result;
        }

        // [different weight] 每 4 个 int 一组: (稀有度, 槽位(从1开始), 百分比下限, 百分比上限),
        // 解封时按 (稀有度, 槽位)  roll 品级百分比 p。连续数字按 4 个一组切分, 跳过不完整组。
        private static Dictionary<int, List<DifferentWeightEntry>> LoadDifferentWeights()
        {
            var text = PvfArchiveAccessor.ReadText("etc/randomoption/randomizedoptionoverall2.etc");
            var ints = ReadSectionInts(text, "different weight");
            var result = new Dictionary<int, List<DifferentWeightEntry>>();
            for (var i = 0; i + 3 < ints.Count; i += 4)
            {
                var rarity = ints[i];
                if (rarity <= 0)
                    continue;

                if (!result.TryGetValue(rarity, out var slots))
                {
                    slots = new List<DifferentWeightEntry>();
                    result[rarity] = slots;
                }

                slots.Add(new DifferentWeightEntry
                {
                    Slot = ints[i + 1],
                    PctMin = ints[i + 2],
                    PctMax = ints[i + 3],
                });
            }

            return result;
        }

        private static string ReadSectionText(string text, string sectionName)
        {
            var match = Regex.Match(text ?? string.Empty, @"\[" + Regex.Escape(sectionName) + @"\]\s*(.*?)\[/" + Regex.Escape(sectionName) + @"\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static List<int> ReadSectionInts(string text, string sectionName)
        {
            var match = Regex.Match(text ?? string.Empty, @"\[" + Regex.Escape(sectionName) + @"\]\s*(.*?)\[/" + Regex.Escape(sectionName) + @"\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!match.Success)
                return new List<int>();

            return Regex.Matches(match.Groups[1].Value, @"-?\d+")
                .Cast<Match>()
                .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
                .ToList();
        }

        private static Dictionary<string, List<QuantityWeight>> LoadQuantityWeights()
        {
            var result = new Dictionary<string, List<QuantityWeight>>(StringComparer.OrdinalIgnoreCase);
            var text = PvfArchiveAccessor.ReadText("etc/randomoption/optionquantity.etc");
            foreach (var key in new[] { "common", "unique" })
            {
                var match = Regex.Match(text, @"\[" + key + @"\]\s*(.*?)\[/"+ key + @"\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!match.Success)
                    continue;

                var ints = Regex.Matches(match.Groups[1].Value, @"-?\d+")
                    .Cast<Match>()
                    .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
                    .ToList();
                var entries = new List<QuantityWeight>();
                for (var i = 0; i + 1 < ints.Count; i += 2)
                {
                    if (ints[i] >= 1 && ints[i] <= 3 && ints[i + 1] > 0)
                        entries.Add(new QuantityWeight { Quantity = ints[i], Weight = ints[i + 1] });
                }

                if (entries.Count > 0)
                    result[key] = entries;
            }

            return result;
        }

        private static T RollWeighted<T>(IReadOnlyList<T> entries) where T : IWeighted
        {
            var total = entries.Sum(x => Math.Max(0, x.Weight));
            if (total <= 0)
                return entries[0];

            var roll = Random.Shared.Next(total);
            foreach (var entry in entries)
            {
                roll -= Math.Max(0, entry.Weight);
                if (roll < 0)
                    return entry;
            }

            return entries[entries.Count - 1];
        }

        private static List<string> Tokenize(string text)
        {
            return Regex.Matches(text ?? string.Empty, @"`([^`]*)`|\[([^\]]+)\]|([^\s]+)")
                .Cast<Match>()
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value
                    : m.Groups[2].Success ? m.Groups[2].Value
                    : m.Groups[3].Value)
                .Where(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("/", StringComparison.Ordinal))
                .ToList();
        }

        private static string NormalizeEquipmentKey(string equipmentType)
        {
            return string.IsNullOrWhiteSpace(equipmentType)
                ? null
                : equipmentType.Trim('[', ']', '`', ' ', '\t', '\r', '\n').ToLowerInvariant();
        }

        private static byte ClampByte(int value)
        {
            if (value < 0)
                return 0;
            if (value > 255)
                return 255;
            return (byte)value;
        }

        private interface IWeighted
        {
            int Weight { get; }
        }

        private sealed class WeightedOption : IWeighted
        {
            public int OptionId { get; set; }

            public int Weight { get; set; }
        }

        private sealed class QuantityWeight : IWeighted
        {
            public int Quantity { get; set; }

            public int Weight { get; set; }
        }

        private sealed class BreakSealCostEntry
        {
            public int Rarity { get; set; }

            public int Level { get; set; }

            public int OptionIndex { get; set; }

            public int Cost { get; set; }
        }

        private sealed class OptionModificationCostEntry
        {
            public int Level { get; set; }

            public int CommonCost { get; set; }

            public int UniqueCost { get; set; }
        }

        private sealed class GradeModificationBand : IWeighted
        {
            public int Slot { get; set; }

            public int PctMin { get; set; }

            public int PctMax { get; set; }

            public int Weight { get; set; }
        }

        private sealed class DifferentWeightEntry
        {
            public int Slot { get; set; }

            public int PctMin { get; set; }

            public int PctMax { get; set; }
        }
    }
}