using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Dungeon
{
    public static class MonsterDropTable
    {
        public struct DropPoolEntry
        {
            public int ItemId;
            public int Weight;
        }

        private static readonly ConcurrentDictionary<int, IReadOnlyList<DropPoolEntry>> _cache
            = new ConcurrentDictionary<int, IReadOnlyList<DropPoolEntry>>();

        private static readonly Lazy<Dictionary<int, string>> _monsterPaths
            = new Lazy<Dictionary<int, string>>(LoadMonsterLst);

        private static readonly IReadOnlyList<DropPoolEntry> Empty = Array.Empty<DropPoolEntry>();

        public static IReadOnlyList<DropPoolEntry> GetDropPool(int monsterCode)
        {
            return _cache.GetOrAdd(monsterCode, code =>
            {
                if (!_monsterPaths.Value.TryGetValue(code, out var mobPath))
                    return Empty;

                try
                {
                    var text = GameWorld.PvfArchiveAccessor.ReadText("monster/" + mobPath);
                    return ParseItemTag(text);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[MonsterDropTable] Failed to load mob code={code} path={mobPath}: {ex.Message}");
                    return Empty;
                }
            });
        }

        private static Dictionary<int, string> LoadMonsterLst()
        {
            var dict = new Dictionary<int, string>();
            try
            {
                var text = GameWorld.PvfArchiveAccessor.ReadText("monster/monster.lst");
                var matches = Regex.Matches(text, @"(\d+)\s+`([^`]+)`");
                foreach (Match m in matches)
                {
                    if (int.TryParse(m.Groups[1].Value, out var code))
                        dict[code] = m.Groups[2].Value;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[MonsterDropTable] Failed to load monster.lst: {ex.Message}");
            }
            return dict;
        }

        private static IReadOnlyList<DropPoolEntry> ParseItemTag(string mobText)
        {
            var match = Regex.Match(mobText, @"\[item\]\s*([\s\S]*?)\s*\[/item\]");
            if (!match.Success) return Empty;

            var tokens = match.Groups[1].Value.Split(
                new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var result = new List<DropPoolEntry>();
            for (int i = 0; i + 1 < tokens.Length; i += 2)
            {
                if (int.TryParse(tokens[i], out var itemId) && int.TryParse(tokens[i + 1], out var weight))
                    result.Add(new DropPoolEntry { ItemId = itemId, Weight = weight });
            }
            return result;
        }
    }
}
