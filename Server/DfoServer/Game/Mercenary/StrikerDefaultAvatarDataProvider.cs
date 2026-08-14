using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Mercenary
{
    // 解析 PVF 支援角色默认外观，按 job/grow 返回 avatar slot 0..10。
    // 负数表示该槽没有默认物品。
    internal static class StrikerDefaultAvatarDataProvider
    {
        private const int HeaderFieldCount = 2;
        private const int AvatarSlotCount = 11;
        private const int RowFieldCount = 2 + AvatarSlotCount;

        private static readonly Lazy<IReadOnlyDictionary<(int Job, int Grow), IReadOnlyList<int>>> Rows
            = new Lazy<IReadOnlyDictionary<(int Job, int Grow), IReadOnlyList<int>>>(LoadRows);

        public static void Warmup()
        {
            _ = Rows.Value;
        }

        public static IReadOnlyList<int> ResolveExact(int job, int growType)
        {
            var key = (job, StrikerSkillDataProvider.NormalizeGrowType(growType));
            return Rows.Value.TryGetValue(key, out var row)
                ? row
                : null;
        }

        internal static IReadOnlyDictionary<(int Job, int Grow), IReadOnlyList<int>> GetAllForTest()
        {
            return Rows.Value;
        }

        private static IReadOnlyDictionary<(int Job, int Grow), IReadOnlyList<int>> LoadRows()
        {
            var candidates = new Dictionary<(int Job, int Grow), List<int[]>>();
            var paths = PvfArchiveAccessor.FindPathsContaining("defaultavatarinfo")
                .Where(path => path.StartsWith("character/", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith(".chr", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (paths.Count == 0)
                throw new InvalidOperationException("PVF中不存在defaultavatarinfo表");

            foreach (var path in paths)
            {
                var tokens = Regex.Matches(PvfArchiveAccessor.ReadText(path), @"-?\d+")
                    .Cast<Match>()
                    .Select(match => int.Parse(match.Value))
                    .ToList();
                if (tokens.Count < HeaderFieldCount)
                    throw new InvalidOperationException($"默认外观表为空: {path}");

                var declaredJobCount = tokens[0];
                var rowCount = tokens[1];
                var expectedTokenCount = HeaderFieldCount + rowCount * RowFieldCount;
                if (declaredJobCount <= 0 || rowCount < 0 || tokens.Count != expectedTokenCount)
                    throw new InvalidOperationException(
                        $"默认外观表结构错误: {path}, rows={rowCount}, tokens={tokens.Count}, expected={expectedTokenCount}");

                for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var offset = HeaderFieldCount + rowIndex * RowFieldCount;
                    var key = (tokens[offset], tokens[offset + 1]);
                    if (key.Item1 < 0 || key.Item1 > byte.MaxValue
                        || key.Item2 < 0 || key.Item2 > 0x0F)
                        throw new InvalidOperationException(
                            $"默认外观表键越界: {path}, row={rowIndex}, job={key.Item1}, grow={key.Item2}");
                    var values = new int[AvatarSlotCount];
                    for (var slot = 0; slot < values.Length; slot++)
                        values[slot] = tokens[offset + 2 + slot];
                    if (!candidates.TryGetValue(key, out var rows))
                    {
                        rows = new List<int[]>();
                        candidates[key] = rows;
                    }
                    rows.Add(values);
                }

                var parsedJobCount = Enumerable.Range(0, rowCount)
                    .Select(rowIndex => tokens[HeaderFieldCount + rowIndex * RowFieldCount])
                    .Distinct()
                    .Count();
                if (parsedJobCount != declaredJobCount)
                    throw new InvalidOperationException(
                        $"默认外观表职业数不匹配: {path}, declared={declaredJobCount}, parsed={parsedJobCount}");
            }

            var result = new Dictionary<(int Job, int Grow), IReadOnlyList<int>>();
            foreach (var pair in candidates)
            {
                var distinct = pair.Value
                    .GroupBy(values => string.Join(",", values))
                    .Select(group => group.First())
                    .ToList();
                if (distinct.Count != 1)
                    throw new InvalidOperationException(
                        $"默认外观表重复键冲突 job={pair.Key.Job} grow={pair.Key.Grow} matches={distinct.Count}");
                result[pair.Key] = Array.AsReadOnly((int[])distinct[0].Clone());
            }
            return new ReadOnlyDictionary<(int Job, int Grow), IReadOnlyList<int>>(result);
        }
    }
}
