using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon
{
    public static class WorldDropSystem
    {
        private const int MaxGenerateLevel = 199;
        private const int DropDenominator = 100000;
        private const int BaseDropRate = 100;

        private sealed class WorldDropLevel
        {
            public int TotalWeight;
            public List<(int ItemId, int CumulativeWeight)> Items;
        }

        private static readonly object _lock = new object();
        private static bool _loaded;
        private static WorldDropLevel[] _levels;

        public static List<DropInfo> GenerateDrops(int monsterLevel, DnfLcg lcg, ref ushort slotCounter)
        {
            EnsureLoaded();
            var result = new List<DropInfo>();
            if (_levels == null || monsterLevel < 1 || monsterLevel > MaxGenerateLevel || monsterLevel >= _levels.Length)
                return result;

            var level = _levels[monsterLevel];
            if (level == null || level.TotalWeight <= 0 || level.Items == null || level.Items.Count == 0)
                return result;

            long threshold = (long)level.TotalWeight * BaseDropRate / 100;
            if (threshold <= 0 || threshold <= lcg.Next(DropDenominator))
                return result;

            int roll = lcg.Next(level.TotalWeight);
            int itemId = level.Items[level.Items.Count - 1].ItemId;
            for (int i = 0; i < level.Items.Count; i++)
            {
                if (roll < level.Items[i].CumulativeWeight)
                {
                    itemId = level.Items[i].ItemId;
                    break;
                }
            }

            slotCounter++;
            result.Add(DropInfo.CreateItem(slotCounter, itemId, 1));
            return result;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try { _levels = LoadWorldDrop(); }
                catch (Exception ex) { FileLogger.Log($"[WorldDrop] INIT FAILED: {ex.Message}"); }
                _loaded = true;
            }
        }

        private static WorldDropLevel[] LoadWorldDrop()
        {
            var result = new WorldDropLevel[201];
            string text;
            try { text = GameWorld.PvfArchiveAccessor.ReadText("Etc/WorldDrop.etc"); }
            catch (Exception ex)
            {
                FileLogger.Log($"[WorldDrop] WorldDrop.etc not found: {ex.Message}");
                return result;
            }

            var match = Regex.Match(text, @"\[world drop\]\s*([\s\S]*?)\s*\[/world drop\]", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                FileLogger.Log("[WorldDrop] [world drop] section not found");
                return result;
            }

            var values = ParseInts(RemoveLineComments(match.Groups[1].Value));
            int idx = 0;
            int levelCount = 0;
            int itemCount = 0;

            while (idx + 1 < values.Count)
            {
                int level = values[idx++];
                idx++;

                int totalWeight = 0;
                var items = new List<(int ItemId, int CumulativeWeight)>();

                while (idx < values.Count)
                {
                    int itemId = values[idx++];
                    if (itemId == -1)
                        break;
                    if (idx >= values.Count)
                        break;

                    int weight = values[idx++];
                    if (itemId <= 0 || weight <= 0)
                        continue;

                    totalWeight += weight;
                    items.Add((itemId, totalWeight));
                }

                if (level >= 1 && level < result.Length && totalWeight > 0 && items.Count > 0)
                {
                    result[level] = new WorldDropLevel
                    {
                        TotalWeight = totalWeight,
                        Items = items
                    };
                    levelCount++;
                    itemCount += items.Count;
                }
            }

            FileLogger.Log($"[WorldDrop] Loaded WorldDrop.etc: {levelCount} levels, {itemCount} items");
            return result;
        }

        private static string RemoveLineComments(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"//.*$", string.Empty, RegexOptions.Multiline);
        }

        private static List<int> ParseInts(string text)
        {
            var result = new List<int>();
            var tokens = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (int.TryParse(tokens[i], out var value))
                    result.Add(value);
            }
            return result;
        }
    }
}
