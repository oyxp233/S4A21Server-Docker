using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.SelectCharacter
{
    
    
    
    
    
    public static class InitialCharacterEquipment
    {
        private static readonly Dictionary<byte, (short slot, int itemId)[]> _cache
            = new Dictionary<byte, (short slot, int itemId)[]>();
        private static readonly object _lock = new object();

        private static readonly Dictionary<string, short> SlotMap = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase)
        {
            { "[weapon]", 11 }, { "[coat]", 13 }, { "[pants]", 15 },
            { "[shoulder]", 14 }, { "[shoes]", 16 }, { "[waist]", 17 },
            { "[amulet]", 18 }, { "[wrist]", 19 }, { "[ring]", 20 },
            { "[support]", 21 }, { "[magic stone]", 22 }, { "[earring]", 23 },
        };

        public static (short slot, int itemId)[] Get(byte job)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(job, out var cached)) return cached;
                var result = ParseFromPvf(job);
                _cache[job] = result;
                return result;
            }
        }

        private static (short slot, int itemId)[] ParseFromPvf(byte job)
        {
            try
            {
                string lst = PvfArchiveAccessor.ReadText("character/character.lst");
                var lm = Regex.Match(lst ?? "", @"(?<!\d)" + (int)job + @"\s+`([^`]+)`");
                if (!lm.Success) return null;

                string text = PvfArchiveAccessor.ReadText("character/" + lm.Groups[1].Value);
                if (string.IsNullOrEmpty(text)) return null;

                int start = text.IndexOf("[create equipment list]", StringComparison.OrdinalIgnoreCase);
                if (start < 0) return null;
                int end = text.IndexOf("[/create equipment list]", start, StringComparison.OrdinalIgnoreCase);
                string sec = end > start ? text.Substring(start, end - start) : text.Substring(start);

                var items = new List<(short, int)>();
                
                var matches = Regex.Matches(sec, @"\[(\w[\w\s]*)\]`?\s*(\d+)");
                foreach (Match m in matches)
                {
                    string slotName = "[" + m.Groups[1].Value.Trim() + "]";
                    if (SlotMap.TryGetValue(slotName, out short slot) && int.TryParse(m.Groups[2].Value, out int itemId))
                        items.Add((slot, itemId));
                }
                return items.Count > 0 ? items.ToArray() : null;
            }
            catch (Exception ex)
            {
                DfoServer.FileLogger.Log($"[InitialCharacterEquipment] job={job} PVF 读取失败: {ex.Message}");
                return null;
            }
        }
    }
}
