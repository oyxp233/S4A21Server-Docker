using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.SelectCharacter
{
    public static class DarkKnightInitialSkillLayout
    {
        private const byte DarkKnightJob = 9;
        private static readonly object ComboSetLock = new object();
        private static readonly Dictionary<ushort, ComboSetCacheEntry> ComboSetCache =
            new Dictionary<ushort, ComboSetCacheEntry>();

        public static List<byte[]> BuildDefaultComboSkillInfoBodies(SkillInfoSnapshot skills)
        {
            var bodies = new List<byte[]>();
            if (skills?.Pages == null)
                return bodies;

            for (var pageIndex = 0; pageIndex < skills.Pages.Count && pageIndex <= 1; pageIndex++)
            {
                var body = BuildPageBlock((byte)pageIndex, skills.Pages[pageIndex]);
                if (body != null)
                    bodies.Add(body);
            }

            return bodies;
        }

        private static byte[] BuildPageBlock(byte pageIndex, SkillInfoPageSnapshot page)
        {
            if (page?.Entries == null || page.Entries.Count == 0)
                return null;

            var learnedSkillIds = new HashSet<ushort>();
            foreach (var entry in page.Entries)
            {
                if (entry.Level > 0 && entry.SkillId > 0)
                    learnedSkillIds.Add(entry.SkillId);
            }

            if (learnedSkillIds.Count == 0)
                return null;

            var roots = new List<InitialComboRoot>();
            foreach (var entry in page.Entries)
            {
                if (!learnedSkillIds.Contains(entry.SkillId)
                    || !TryGetComboSet(entry.SkillId, out var comboSet))
                {
                    continue;
                }

                var root = new InitialComboRoot(entry.SkillId);
                foreach (var childSkillId in comboSet)
                {
                    if (learnedSkillIds.Contains(childSkillId))
                        root.ChildSkillIds.Add(childSkillId);
                }
                roots.Add(root);
            }

            if (roots.Count == 0 || roots.Count > byte.MaxValue)
                return null;

            var bytes = new List<byte>();
            bytes.Add(pageIndex);
            bytes.Add((byte)roots.Count);
            foreach (var root in roots)
            {
                WriteUInt16(bytes, root.RootSkillId);
                bytes.Add((byte)root.ChildSkillIds.Count);
                foreach (var childSkillId in root.ChildSkillIds)
                    WriteUInt16(bytes, childSkillId);
            }

            var normalized = Skills.DarkKnightComboSkillInfoCodec.NormalizePageBlock(bytes.ToArray());
            return Skills.DarkKnightComboSkillInfoCodec.IsValidPageBlock(normalized) ? normalized : null;
        }

        private static bool TryGetComboSet(ushort rootSkillId, out ushort[] comboSet)
        {
            lock (ComboSetLock)
            {
                if (ComboSetCache.TryGetValue(rootSkillId, out var cached))
                {
                    comboSet = cached.ComboSet;
                    return cached.IsComboRoot;
                }
            }

            var loaded = LoadComboSet(rootSkillId);
            lock (ComboSetLock)
                ComboSetCache[rootSkillId] = loaded;

            comboSet = loaded.ComboSet;
            return loaded.IsComboRoot;
        }

        private static ComboSetCacheEntry LoadComboSet(ushort rootSkillId)
        {
            var data = Skills.SkillDataProvider.GetSkill(DarkKnightJob, rootSkillId);
            var path = data?.PvfPath?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path)
                || !Regex.IsMatch(path, @"(^|/)comboset\d+\.skl$", RegexOptions.IgnoreCase))
            {
                return ComboSetCacheEntry.NotComboRoot;
            }

            try
            {
                var text = PvfArchiveAccessor.ReadText("skill/" + path);
                var match = Regex.Match(
                    text ?? string.Empty,
                    @"\[combo set\](.*?)\[/combo set\]",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var comboSet = new List<ushort>();
                if (match.Success)
                {
                    foreach (Match number in Regex.Matches(match.Groups[1].Value, @"\d+"))
                    {
                        if (ushort.TryParse(number.Value, out var skillId))
                            comboSet.Add(skillId);
                    }
                }

                return new ComboSetCacheEntry(true, comboSet.ToArray());
            }
            catch (Exception ex)
            {
                DfoServer.FileLogger.Log(
                    $"[DarkKnightInitialSkillLayout] combo root={rootSkillId} PVF read failed: {ex.Message}");
                return new ComboSetCacheEntry(true, Array.Empty<ushort>());
            }
        }

        private static void WriteUInt16(List<byte> output, ushort value)
        {
            output.Add((byte)(value & 0xFF));
            output.Add((byte)(value >> 8));
        }

        private sealed class InitialComboRoot
        {
            public InitialComboRoot(ushort rootSkillId)
            {
                RootSkillId = rootSkillId;
            }

            public ushort RootSkillId { get; }

            public List<ushort> ChildSkillIds { get; } = new List<ushort>();
        }

        private readonly struct ComboSetCacheEntry
        {
            public static readonly ComboSetCacheEntry NotComboRoot =
                new ComboSetCacheEntry(false, Array.Empty<ushort>());

            public ComboSetCacheEntry(bool isComboRoot, ushort[] comboSet)
            {
                IsComboRoot = isComboRoot;
                ComboSet = comboSet ?? Array.Empty<ushort>();
            }

            public bool IsComboRoot { get; }

            public ushort[] ComboSet { get; }
        }
    }
}
