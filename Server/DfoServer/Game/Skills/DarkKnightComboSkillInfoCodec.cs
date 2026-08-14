using System;
using System.Collections.Generic;

namespace DfoServer.Game.Skills
{
    public static class DarkKnightComboSkillInfoCodec
    {
        public static byte[] NormalizePageBlock(byte[] body)
        {
            if (!TryParsePageBlock(body, out var pageIndex, out var roots))
                return Copy(body);

            var lastOccurrence = new Dictionary<ushort, ChildOccurrence>();
            for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                var children = roots[rootIndex].ChildSkillIds;
                for (var childIndex = 0; childIndex < children.Count; childIndex++)
                    lastOccurrence[children[childIndex]] = new ChildOccurrence(rootIndex, childIndex);
            }

            var output = new List<byte>();
            output.Add(pageIndex);
            output.Add((byte)roots.Count);
            for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                var root = roots[rootIndex];
                WriteUInt16(output, root.RootSkillId);

                var children = new List<ushort>();
                for (var childIndex = 0; childIndex < root.ChildSkillIds.Count; childIndex++)
                {
                    var child = root.ChildSkillIds[childIndex];
                    if (lastOccurrence.TryGetValue(child, out var occurrence)
                        && occurrence.RootIndex == rootIndex
                        && occurrence.ChildIndex == childIndex)
                    {
                        children.Add(child);
                    }
                }

                output.Add((byte)children.Count);
                foreach (var child in children)
                    WriteUInt16(output, child);
            }

            return output.ToArray();
        }

        public static bool IsValidPageBlock(byte[] body)
            => TryParsePageBlock(body, out _, out _);

        public static bool IsExtensionQuickSlot(int slot)
            => slot >= 198 && slot < 204;

        public static bool IsOrdinaryQuickSlot(int slot)
            => slot >= 0 && slot < 12;

        public static bool IsShortcutSlot(int slot)
            => IsOrdinaryQuickSlot(slot) || IsExtensionQuickSlot(slot);

        public static bool TryBuildNotificationBody(IReadOnlyList<byte[]> pageBlocks, out byte[] body)
        {
            body = null;
            if (pageBlocks == null || pageBlocks.Count == 0)
                return false;

            var normalizedBlocks = new List<byte[]>();
            foreach (var block in pageBlocks)
            {
                var normalized = NormalizePageBlock(block);
                if (IsValidPageBlock(normalized))
                    normalizedBlocks.Add(normalized);
            }

            if (normalizedBlocks.Count == 0 || normalizedBlocks.Count > byte.MaxValue)
                return false;

            var output = new List<byte>();
            output.Add(0);
            output.Add((byte)normalizedBlocks.Count);
            foreach (var block in normalizedBlocks)
                output.AddRange(block);

            body = output.ToArray();
            return true;
        }

        public static HashSet<ushort> GetRootSkillIds(IReadOnlyList<byte[]> pageBlocks)
        {
            var ids = new HashSet<ushort>();
            if (pageBlocks == null)
                return ids;

            foreach (var block in pageBlocks)
            {
                foreach (var skillId in GetRootSkillIds(block))
                    ids.Add(skillId);
            }

            return ids;
        }

        public static HashSet<ushort> GetRootSkillIds(byte[] body)
        {
            var ids = new HashSet<ushort>();
            var normalized = NormalizePageBlock(body);
            if (!TryParsePageBlock(normalized, out _, out var roots))
                return ids;

            foreach (var root in roots)
                ids.Add(root.RootSkillId);

            return ids;
        }

        public static HashSet<ushort> GetChildSkillIds(byte[] body)
        {
            var ids = new HashSet<ushort>();
            var normalized = NormalizePageBlock(body);
            if (!TryParsePageBlock(normalized, out _, out var roots))
                return ids;

            foreach (var root in roots)
            {
                foreach (var child in root.ChildSkillIds)
                    ids.Add(child);
            }

            return ids;
        }

        private static bool TryParsePageBlock(byte[] body, out byte pageIndex, out List<RootBlock> roots)
        {
            pageIndex = 0;
            roots = null;
            if (body == null || body.Length < 2)
                return false;

            var offset = 0;
            pageIndex = body[offset++];
            if (pageIndex > 1)
                return false;

            var rootCount = body[offset++];
            roots = new List<RootBlock>(rootCount);

            for (var i = 0; i < rootCount; i++)
            {
                if (offset + 3 > body.Length)
                    return false;

                var root = new RootBlock
                {
                    RootSkillId = ReadUInt16(body, offset),
                };
                offset += 2;

                var childCount = body[offset++];
                if (offset + childCount * 2 > body.Length)
                    return false;

                for (var childIndex = 0; childIndex < childCount; childIndex++)
                {
                    root.ChildSkillIds.Add(ReadUInt16(body, offset));
                    offset += 2;
                }

                roots.Add(root);
            }

            return offset == body.Length;
        }

        private static ushort ReadUInt16(byte[] body, int offset)
            => (ushort)(body[offset] | (body[offset + 1] << 8));

        private static void WriteUInt16(List<byte> output, ushort value)
        {
            output.Add((byte)(value & 0xFF));
            output.Add((byte)(value >> 8));
        }

        private static byte[] Copy(byte[] body)
        {
            if (body == null)
                return null;

            var copy = new byte[body.Length];
            Buffer.BlockCopy(body, 0, copy, 0, body.Length);
            return copy;
        }

        private sealed class RootBlock
        {
            public ushort RootSkillId { get; set; }

            public List<ushort> ChildSkillIds { get; } = new List<ushort>();
        }

        private readonly struct ChildOccurrence
        {
            public ChildOccurrence(int rootIndex, int childIndex)
            {
                RootIndex = rootIndex;
                ChildIndex = childIndex;
            }

            public int RootIndex { get; }

            public int ChildIndex { get; }
        }
    }
}
