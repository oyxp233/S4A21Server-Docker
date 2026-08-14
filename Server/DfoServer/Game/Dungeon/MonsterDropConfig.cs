using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Dungeon
{
    public static class MonsterDropConfig
    {
        private struct DropProb
        {
            public int LevelMin, LevelMax;
            public int GoldRate, Type1Rate, Type2Rate, Type3Rate;
        }

        private static DropProb[] _dropProbs;
        private static (int Down, int Up)[] _gradeRanges;
        private static int[,] _rarityThresholds;
        private static float[,] _monsterTypeBonuses;
        private static Dictionary<long, List<(int Id, int Weight)>> _equipPool;
        private static Dictionary<long, List<int>> _stackablePool;

        private static readonly object _lock = new object();
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    LoadDropInfo();
                    _equipPool = EquipmentDropPoolProvider.GetPool();
                    LoadStackablePool();
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[MonsterDropConfig] INIT FAILED: {ex}");
                }
                _loaded = true;
            }
        }

        public static void GetAllDropRates(int monsterLevel, int monsterType,
            out int goldRate, out int type1Rate, out int type2Rate,
            out int type3Rate)
        {
            EnsureLoaded();
            var prob = FindDropProb(monsterLevel);
            goldRate = (int)(prob.GoldRate * GetMonsterTypeBonus(0, monsterType));
            type1Rate = (int)(prob.Type1Rate * GetMonsterTypeBonus(1, monsterType));
            type2Rate = (int)(prob.Type2Rate * GetMonsterTypeBonus(2, monsterType));
            type3Rate = (int)(prob.Type3Rate * GetMonsterTypeBonus(3, monsterType));
        }

        public static int RollRarity(DnfLcg lcg)
        {
            EnsureLoaded();
            if (_rarityThresholds == null) return 0;
            int roll = lcg.Next(1000000) + 1;
            for (int r = 0; r < 7; r++)
            {
                if (roll <= _rarityThresholds[0, r])
                    return r;
            }
            return 0;
        }

        public static int ChooseEquipment(DnfLcg lcg, int monsterLevel, int rarity)
        {
            EnsureLoaded();
            if (_gradeRanges == null || _equipPool == null) return -1;
            if (monsterLevel < 1 || monsterLevel > _gradeRanges.Length) return -1;

            var range = _gradeRanges[monsterLevel - 1];
            var candidates = CollectWeightedFromPool(_equipPool, monsterLevel, range, rarity);

            if (candidates.Count == 0 && rarity > 0)
                candidates = CollectWeightedFromPool(_equipPool, monsterLevel, range, 0);

            if (candidates.Count == 0) return -1;

            int totalWeight = 0;
            for (int i = 0; i < candidates.Count; i++)
                totalWeight += candidates[i].Weight;

            int roll = lcg.Next(totalWeight);
            int cum = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                cum += candidates[i].Weight;
                if (roll < cum)
                    return candidates[i].Id;
            }
            return candidates[candidates.Count - 1].Id;
        }

        public static int ChooseStackable(DnfLcg lcg, int monsterLevel, int rarity)
        {
            EnsureLoaded();
            if (_gradeRanges == null || _stackablePool == null || _stackablePool.Count == 0) return -1;
            if (monsterLevel < 1 || monsterLevel > _gradeRanges.Length) return -1;

            var range = _gradeRanges[monsterLevel - 1];
            var candidates = CollectFromPool(_stackablePool, monsterLevel, range, rarity);

            if (candidates.Count == 0 && rarity > 0)
                candidates = CollectFromPool(_stackablePool, monsterLevel, range, 0);

            if (candidates.Count == 0) return -1;
            return candidates[lcg.Next(candidates.Count)];
        }

        private static List<int> CollectFromPool(Dictionary<long, List<int>> pool,
            int monsterLevel, (int Down, int Up) range, int rarity)
        {
            var result = new List<int>();
            for (int m = -range.Down; m < range.Up; m++)
            {
                int grade = monsterLevel + m;
                if (grade < 1 || grade > 200) continue;
                long key = (long)grade * 10 + rarity;
                if (pool.TryGetValue(key, out var items))
                    result.AddRange(items);
            }
            return result;
        }

        private static List<(int Id, int Weight)> CollectWeightedFromPool(
            Dictionary<long, List<(int Id, int Weight)>> pool,
            int monsterLevel, (int Down, int Up) range, int rarity)
        {
            var result = new List<(int, int)>();
            for (int m = -range.Down; m < range.Up; m++)
            {
                int grade = monsterLevel + m;
                if (grade < 1 || grade > 200) continue;
                long key = (long)grade * 10 + rarity;
                if (pool.TryGetValue(key, out var items))
                    result.AddRange(items);
            }
            return result;
        }

        private static DropProb FindDropProb(int level)
        {
            if (_dropProbs != null)
            {
                for (int i = 0; i < _dropProbs.Length; i++)
                {
                    if (level >= _dropProbs[i].LevelMin && level <= _dropProbs[i].LevelMax)
                        return _dropProbs[i];
                }
            }
            return new DropProb { GoldRate = 750, Type2Rate = 135 };
        }

        private static float GetMonsterTypeBonus(int category, int monsterType)
        {
            if (_monsterTypeBonuses == null) return 1.0f;
            if (category < 0 || category >= _monsterTypeBonuses.GetLength(0)) return 1.0f;
            int mt = Math.Min(Math.Max(monsterType, 0), _monsterTypeBonuses.GetLength(1) - 1);
            return _monsterTypeBonuses[category, mt];
        }

        private static void LoadDropInfo()
        {
            var text = GameWorld.PvfArchiveAccessor.ReadText("Etc/ItemDropInfo_Monseter.etc");
            if (string.IsNullOrWhiteSpace(text))
            {
                FileLogger.Log("[MonsterDropConfig] ItemDropInfo_Monseter.etc empty/not found");
                return;
            }

            ParseDropProb(text);
            ParseGradeRanges(text);
            ParseRarityThresholds(text);
            ParseMonsterTypeBonuses(text);
        }

        private static string ExtractSection(string text, string name)
        {
            string tag = "[" + name + "]";
            int start = text.IndexOf(tag, StringComparison.Ordinal);
            if (start < 0) return null;
            start += tag.Length;
            int end = text.IndexOf("[", start, StringComparison.Ordinal);
            if (end < 0) end = text.Length;
            return text.Substring(start, end - start);
        }

        private static int[] ParseInts(string section)
        {
            if (string.IsNullOrWhiteSpace(section)) return Array.Empty<int>();
            var tokens = section.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<int>(tokens.Length);
            foreach (var t in tokens)
            {
                if (int.TryParse(t, out var v))
                    result.Add(v);
            }
            return result.ToArray();
        }

        private static float[] ParseFloats(string section)
        {
            if (string.IsNullOrWhiteSpace(section)) return Array.Empty<float>();
            var tokens = section.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<float>(tokens.Length);
            foreach (var t in tokens)
            {
                if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    result.Add(v);
            }
            return result.ToArray();
        }

        private static void ParseDropProb(string text)
        {
            var section = ExtractSection(text, "drop prob");
            var vals = ParseInts(section);
            if (vals.Length < 7) return;

            int count = vals.Length / 7;
            _dropProbs = new DropProb[count];
            for (int i = 0; i < count; i++)
            {
                int b = i * 7;
                // vals[b + 6] is an uninterpreted PVF rate column; its semantics are not established.
                _dropProbs[i] = new DropProb
                {
                    LevelMin = vals[b], LevelMax = vals[b + 1],
                    GoldRate = vals[b + 2], Type1Rate = vals[b + 3],
                    Type2Rate = vals[b + 4], Type3Rate = vals[b + 5]
                };
            }
            FileLogger.Log($"[MonsterDropConfig] [drop prob]: {count} entries, lv1-15 type2={_dropProbs[0].Type2Rate}");
        }

        private static void ParseGradeRanges(string text)
        {
            var section = ExtractSection(text, "item drop ref table");
            var vals = ParseInts(section);
            if (vals.Length < 3) return;

            _gradeRanges = new (int, int)[200];
            for (int i = 0; i + 2 < vals.Length; i += 3)
            {
                int level = vals[i];
                if (level >= 1 && level <= 200)
                    _gradeRanges[level - 1] = (vals[i + 1], vals[i + 2]);
            }
            FileLogger.Log($"[MonsterDropConfig] [item drop ref table]: {vals.Length / 3} entries");
        }

        private static void ParseRarityThresholds(string text)
        {
            var section = ExtractSection(text, "basis of rarity dicision");
            var vals = ParseInts(section);
            if (vals.Length < 28) return;

            _rarityThresholds = new int[4, 7];
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 7; col++)
                    _rarityThresholds[row, col] = vals[row * 7 + col];
            FileLogger.Log($"[MonsterDropConfig] [basis of rarity dicision]: row0 = {_rarityThresholds[0, 0]},{_rarityThresholds[0, 1]},{_rarityThresholds[0, 2]}");
        }

        private static void ParseMonsterTypeBonuses(string text)
        {
            var section = ExtractSection(text, "monster type drop bonusrate");
            var vals = ParseFloats(section);
            if (vals.Length < 20) return;

            _monsterTypeBonuses = new float[5, 4];
            for (int cat = 0; cat < 5; cat++)
                for (int mt = 0; mt < 4; mt++)
                    _monsterTypeBonuses[cat, mt] = vals[cat * 4 + mt];
            FileLogger.Log($"[MonsterDropConfig] [monster type drop bonusrate]: normal_equip={_monsterTypeBonuses[2, 0]}, boss_equip={_monsterTypeBonuses[2, 3]}");
        }

        private static void LoadEquipmentPool()
        {
            _equipPool = new Dictionary<long, List<(int, int)>>();
            try
            {
                var text = GameWorld.PvfArchiveAccessor.ReadText("Etc/ItemDictionary/ItemDictionary.etc");
                if (string.IsNullOrWhiteSpace(text))
                {
                    FileLogger.Log("[MonsterDropConfig] ItemDictionary.etc empty/not found");
                    return;
                }

                var allTokens = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var nums = new List<int>(16);
                bool inName = false;
                int added = 0;

                for (int ti = 0; ti < allTokens.Length; ti++)
                {
                    var token = allTokens[ti];

                    if (inName)
                    {
                        if (token.IndexOf('`') >= 0)
                        {
                            inName = false;
                            if (TryAddEquip(nums)) added++;
                            nums.Clear();
                        }
                        continue;
                    }

                    int bq = token.IndexOf('`');
                    if (bq >= 0)
                    {
                        if (token.LastIndexOf('`') > bq)
                        {
                            if (TryAddEquip(nums)) added++;
                            nums.Clear();
                        }
                        else
                        {
                            inName = true;
                        }
                        continue;
                    }

                    if (int.TryParse(token, out var n))
                        nums.Add(n);
                }

                FileLogger.Log($"[MonsterDropConfig] Equipment pool: {added} items across {_equipPool.Count} (grade,rarity) buckets");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[MonsterDropConfig] ItemDictionary parse error: {ex.Message}");
            }
        }

        private static bool TryAddEquip(List<int> nums)
        {
            
            if (nums.Count < 6) return false;

            int itemId = nums[0];
            int rarity = nums[1];
            int grade = nums[2];
            int equipCategory = nums[4];
            int genRate = nums[5];

            if (genRate <= 0) return false;
            if (grade <= 0) return false;
            if (rarity > 5) return false;

            int mainCat = equipCategory / 1000;
            if (mainCat < 10 || mainCat > 12) return false;

            long key = (long)grade * 10 + rarity;
            if (!_equipPool.TryGetValue(key, out var list))
            {
                list = new List<(int, int)>();
                _equipPool[key] = list;
            }
            list.Add((itemId, genRate));
            return true;
        }

        private static readonly Regex StkGradeRe = new Regex(@"\[grade\]\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex StkRarityRe = new Regex(@"\[rarity\]\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex StkCreationRateRe = new Regex(@"\[creation rate\]\s*(\d+)", RegexOptions.Compiled);

        private static void LoadStackablePool()
        {
            _stackablePool = new Dictionary<long, List<int>>();
            string lstText;
            try { lstText = GameWorld.PvfArchiveAccessor.ReadText("stackable/stackable.lst"); }
            catch
            {
                FileLogger.Log("[MonsterDropConfig] stackable.lst not found");
                return;
            }

            var entries = Regex.Matches(lstText, @"(\d+)\s+`([^`]+)`");
            int added = 0, skipped = 0, errors = 0;

            foreach (Match entry in entries)
            {
                if (!int.TryParse(entry.Groups[1].Value, out var itemId)) continue;
                var path = entry.Groups[2].Value.Replace('\\', '/');

                if (itemId <= 2) { skipped++; continue; }
                if (path.StartsWith("cash/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("quest/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("recipe/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("temp/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("event/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("emblem/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("monsterCard/", StringComparison.OrdinalIgnoreCase))
                { skipped++; continue; }

                string stkText;
                try { stkText = GameWorld.PvfArchiveAccessor.ReadText("stackable/" + path); }
                catch { errors++; continue; }

                var cm = StkCreationRateRe.Match(stkText);
                int creationRate = cm.Success ? int.Parse(cm.Groups[1].Value) : 0;
                if (creationRate <= 0) { skipped++; continue; }

                var gm = StkGradeRe.Match(stkText);
                if (!gm.Success) { skipped++; continue; }
                int grade = int.Parse(gm.Groups[1].Value);
                if (grade <= 0) { skipped++; continue; }

                var rm = StkRarityRe.Match(stkText);
                int rarity = rm.Success ? int.Parse(rm.Groups[1].Value) : 0;

                long key = (long)grade * 10 + rarity;
                if (!_stackablePool.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    _stackablePool[key] = list;
                }
                list.Add(itemId);
                added++;
            }

            FileLogger.Log($"[MonsterDropConfig] Stackable pool: {added} items, {skipped} skipped, {errors} errors, {_stackablePool.Count} buckets");
        }

    }
}
