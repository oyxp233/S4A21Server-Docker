using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;

namespace DfoServer.GameWorld
{
    public struct TutorialRewardEntry
    {
        public int ItemId;
        public int Count;
    }

    public static class TutorialRewardProvider
    {
        private static readonly object _lock = new object();
        private static Dictionary<uint, List<TutorialRewardEntry>> _rewards;

        public static List<TutorialRewardEntry> GetRewards(uint flagIndex)
        {
            EnsureLoaded();
            List<TutorialRewardEntry> entries;
            if (_rewards.TryGetValue(flagIndex, out entries))
                return entries;
            return null;
        }

        private static void EnsureLoaded()
        {
            if (_rewards != null) return;
            lock (_lock)
            {
                if (_rewards != null) return;
                _rewards = new Dictionary<uint, List<TutorialRewardEntry>>();
                try
                {
                    var text = PvfArchiveAccessor.ReadText("etc/serverparameter.etc");
                    Parse(text);
                    int total = 0;
                    foreach (var v in _rewards.Values) total += v.Count;
                    FileLogger.Log($"[TutorialRewardProvider] Loaded {total} reward items across {_rewards.Count} flags");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[TutorialRewardProvider] Failed to load: {ex.Message}");
                }
            }
        }

        private static void Parse(string text)
        {
            const string startTag = "[escalade tutorial reward]";
            const string endTag = "[/escalade tutorial reward]";
            int start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return;
            start += startTag.Length;
            int end = text.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = text.Length;

            var data = text.Substring(start, end - start).Trim();
            var tokens = data.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < tokens.Length; i += 3)
            {
                if (uint.TryParse(tokens[i], out var flagIdx) &&
                    int.TryParse(tokens[i + 1], out var itemId) &&
                    int.TryParse(tokens[i + 2], out var count))
                {
                    if (!_rewards.ContainsKey(flagIdx))
                        _rewards[flagIdx] = new List<TutorialRewardEntry>();
                    _rewards[flagIdx].Add(new TutorialRewardEntry { ItemId = itemId, Count = count });
                }
            }
        }
    }
}
