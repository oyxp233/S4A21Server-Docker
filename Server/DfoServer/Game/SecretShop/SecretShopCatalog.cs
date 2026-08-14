using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.SecretShop
{
    internal enum SecretShopPoolSource
    {
        Level,
        CashItem,
        Dungeon,
    }

    internal sealed class SecretShopLevelSection
    {
        internal int Key { get; init; }
        internal int MinimumLevel { get; init; }
        internal int MaximumLevel { get; init; }
    }

    internal sealed class SecretShopNpcWeight
    {
        internal int NpcId { get; init; }
        internal int Weight { get; init; }
    }

    internal sealed class SecretShopItemCandidate
    {
        internal int ItemId { get; init; }
        internal int RawFlag { get; init; }
        internal int Price { get; init; }
        internal int RequiredItemId { get; init; }
        internal int Count { get; init; }
        internal int Weight { get; init; }
    }

    internal sealed class SecretShopItemPool
    {
        internal int SelectorKey { get; init; }
        internal int SelectionCount { get; init; }
        internal SecretShopPoolSource Source { get; init; }
        internal IReadOnlyList<SecretShopItemCandidate> Candidates { get; init; }
            = Array.Empty<SecretShopItemCandidate>();
    }

    internal sealed class SecretShopCatalog
    {
        private readonly Dictionary<int, List<SecretShopNpcWeight>> _dungeonNpcWeights = new();
        private readonly Dictionary<int, List<LevelNpcWeightRow>> _levelNpcWeights = new();
        private readonly Dictionary<int, NpcCatalog> _npcs = new();

        internal int PriceRandomPercent { get; private set; }
        internal int CashUserStackableCount { get; private set; }
        internal IReadOnlyDictionary<int, SecretShopLevelSection> LevelSections => _levelSections;

        private readonly Dictionary<int, SecretShopLevelSection> _levelSections = new();

        internal static SecretShopCatalog Parse(string text)
        {
            var catalog = new SecretShopCatalog();
            var section = string.Empty;
            int? currentNpcId = null;

            foreach (var rawLine in (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line[0] == '[' && line[^1] == ']')
                {
                    if (line.StartsWith("[/", StringComparison.Ordinal))
                    {
                        var closed = NormalizeTag(line.Substring(2, line.Length - 3));
                        if (closed == "npc")
                            currentNpcId = null;
                        section = string.Empty;
                    }
                    else
                    {
                        section = NormalizeTag(line.Substring(1, line.Length - 2));
                    }
                    continue;
                }

                var values = ParseIntegers(line);
                if (values.Count == 0)
                    continue;

                switch (section)
                {
                    case "price random":
                        catalog.PriceRandomPercent = values[0];
                        break;
                    case "cash user stackable count":
                        catalog.CashUserStackableCount = values[0];
                        break;
                    case "dungeon npc":
                        ParseDungeonNpcWeights(values, catalog._dungeonNpcWeights);
                        break;
                    case "level section":
                        ParseLevelSections(values, catalog._levelSections);
                        break;
                    case "level npc":
                        ParseLevelNpcWeights(values, catalog._levelNpcWeights);
                        break;
                    case "npc":
                        currentNpcId = values[0];
                        if (!catalog._npcs.ContainsKey(values[0]))
                            catalog._npcs.Add(values[0], new NpcCatalog());
                        break;
                    case "level":
                    case "cash item":
                    case "dungeon":
                        if (currentNpcId.HasValue)
                            catalog.AddPool(currentNpcId.Value, section, values);
                        break;
                }
            }

            return catalog;
        }

        internal IReadOnlyList<SecretShopNpcWeight> ResolveNpcWeights(int dungeonId, int dungeonBasisLevel, int partySize)
        {
            if (_dungeonNpcWeights.TryGetValue(dungeonId, out var dungeonRows))
                return dungeonRows;

            var levelSection = _levelSections.Values
                .OrderBy(x => x.Key)
                .FirstOrDefault(x => dungeonBasisLevel >= x.MinimumLevel && dungeonBasisLevel <= x.MaximumLevel);
            if (levelSection == null || !_levelNpcWeights.TryGetValue(levelSection.Key, out var levelRows))
                return Array.Empty<SecretShopNpcWeight>();

            var column = Math.Clamp(partySize, 1, 5) - 1;
            return levelRows
                .Select(x => new SecretShopNpcWeight { NpcId = x.NpcId, Weight = x.PartyWeights[column] })
                .ToArray();
        }

        internal SecretShopItemPool ResolvePool(
            int npcId,
            int dungeonId,
            int dungeonBasisLevel,
            bool useCashItems)
        {
            if (!_npcs.TryGetValue(npcId, out var npc))
                return null;

            if (npc.DungeonPools.TryGetValue(dungeonId, out var dungeonPool))
                return dungeonPool;

            var levelSection = _levelSections.Values
                .OrderBy(x => x.Key)
                .FirstOrDefault(x => dungeonBasisLevel >= x.MinimumLevel && dungeonBasisLevel <= x.MaximumLevel);
            if (levelSection == null)
                return null;

            var pools = useCashItems ? npc.CashItemPools : npc.LevelPools;
            return pools.TryGetValue(levelSection.Key, out var pool) ? pool : null;
        }

        private void AddPool(int npcId, string section, IReadOnlyList<int> values)
        {
            if (values.Count < 2)
                return;

            var candidates = new List<SecretShopItemCandidate>();
            for (var i = 2; i + 5 < values.Count; i += 6)
            {
                candidates.Add(new SecretShopItemCandidate
                {
                    ItemId = values[i],
                    RawFlag = values[i + 1],
                    Price = values[i + 2],
                    RequiredItemId = values[i + 3],
                    Count = values[i + 4],
                    Weight = values[i + 5],
                });
            }

            var source = section switch
            {
                "cash item" => SecretShopPoolSource.CashItem,
                "dungeon" => SecretShopPoolSource.Dungeon,
                _ => SecretShopPoolSource.Level,
            };
            var pool = new SecretShopItemPool
            {
                SelectorKey = values[0],
                SelectionCount = values[1],
                Source = source,
                Candidates = candidates,
            };

            var npc = _npcs[npcId];
            var destination = source switch
            {
                SecretShopPoolSource.CashItem => npc.CashItemPools,
                SecretShopPoolSource.Dungeon => npc.DungeonPools,
                _ => npc.LevelPools,
            };
            destination[pool.SelectorKey] = pool;
        }

        private static void ParseDungeonNpcWeights(
            IReadOnlyList<int> values,
            IDictionary<int, List<SecretShopNpcWeight>> destination)
        {
            for (var i = 0; i + 2 < values.Count; i += 3)
            {
                if (!destination.TryGetValue(values[i], out var rows))
                {
                    rows = new List<SecretShopNpcWeight>();
                    destination.Add(values[i], rows);
                }
                rows.Add(new SecretShopNpcWeight { NpcId = values[i + 1], Weight = values[i + 2] });
            }
        }

        private static void ParseLevelSections(
            IReadOnlyList<int> values,
            IDictionary<int, SecretShopLevelSection> destination)
        {
            for (var i = 0; i + 2 < values.Count; i += 3)
            {
                destination[values[i]] = new SecretShopLevelSection
                {
                    Key = values[i],
                    MinimumLevel = values[i + 1],
                    MaximumLevel = values[i + 2],
                };
            }
        }

        private static void ParseLevelNpcWeights(
            IReadOnlyList<int> values,
            IDictionary<int, List<LevelNpcWeightRow>> destination)
        {
            for (var i = 0; i + 6 < values.Count; i += 7)
            {
                if (!destination.TryGetValue(values[i], out var rows))
                {
                    rows = new List<LevelNpcWeightRow>();
                    destination.Add(values[i], rows);
                }
                rows.Add(new LevelNpcWeightRow
                {
                    NpcId = values[i + 1],
                    PartyWeights = new[] { values[i + 2], values[i + 3], values[i + 4], values[i + 5], values[i + 6] },
                });
            }
        }

        private static List<int> ParseIntegers(string line)
        {
            var result = new List<int>();
            foreach (var token in line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, out var value))
                    result.Add(value);
            }
            return result;
        }

        private static string NormalizeTag(string tag)
            => tag.Trim().ToLowerInvariant();

        private sealed class LevelNpcWeightRow
        {
            internal int NpcId { get; init; }
            internal int[] PartyWeights { get; init; } = new int[5];
        }

        private sealed class NpcCatalog
        {
            internal Dictionary<int, SecretShopItemPool> LevelPools { get; } = new();
            internal Dictionary<int, SecretShopItemPool> CashItemPools { get; } = new();
            internal Dictionary<int, SecretShopItemPool> DungeonPools { get; } = new();
        }
    }

    internal static class SecretShopSelector
    {
        internal static int SelectNpc(IReadOnlyList<SecretShopNpcWeight> weights, Func<int, int> next)
        {
            if (weights == null || weights.Count == 0)
                return 1000;

            var positive = weights.Where(x => x.Weight > 0).ToArray();
            return positive.Length == 0 ? 1000 : Pick(positive, x => x.Weight, next).NpcId;
        }

        internal static IReadOnlyList<SecretShopItemCandidate> SelectItems(
            SecretShopItemPool pool,
            Func<int, int> next)
        {
            if (pool == null || pool.SelectionCount <= 0)
                return Array.Empty<SecretShopItemCandidate>();

            var remaining = pool.Candidates.Where(x => x.Weight > 0).ToList();
            var selected = new List<SecretShopItemCandidate>();
            while (selected.Count < pool.SelectionCount && remaining.Count > 0)
            {
                var chosen = Pick(remaining, x => x.Weight, next);
                selected.Add(chosen);
                remaining.Remove(chosen);
            }
            return selected;
        }

        private static T Pick<T>(IReadOnlyList<T> rows, Func<T, int> weight, Func<int, int> next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            var total = rows.Sum(x => checked(weight(x)));
            if (total <= 0)
                throw new InvalidOperationException("Secret-shop weight total must be positive.");

            var roll = next(total);
            if (roll < 0 || roll >= total)
                throw new InvalidOperationException($"Secret-shop random roll {roll} is outside [0,{total}).");

            var cursor = 0;
            foreach (var row in rows)
            {
                cursor += weight(row);
                if (roll < cursor)
                    return row;
            }
            throw new InvalidOperationException("Secret-shop weighted selection did not resolve a row.");
        }
    }
}
