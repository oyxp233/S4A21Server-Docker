using System;
using System.Collections.Generic;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Game.Inventory
{
    internal static class ItemStateKinds
    {
        public const string Cooltime = "cooltime";
        public const string Effect = "effect";

        public static bool IsKnown(string stateKind)
        {
            return string.Equals(stateKind, Cooltime, StringComparison.Ordinal)
                || string.Equals(stateKind, Effect, StringComparison.Ordinal);
        }
    }

    internal sealed class ItemStateEntry
    {
        public string StateKind { get; set; }

        public int ItemId { get; set; }

        public int ExpireTime { get; set; }
    }

    internal sealed class InventoryItemStateBook
    {
        private readonly Dictionary<ItemStateKey, ItemStateEntry> _entries =
            new Dictionary<ItemStateKey, ItemStateEntry>();

        public bool IsDirty { get; private set; }

        public void Attach(string stateKind, int itemId, int expireTime)
        {
            if (!TryNormalize(stateKind, itemId, expireTime, out var key, out var normalized))
                return;

            _entries[key] = normalized;
        }

        public bool Upsert(string stateKind, int itemId, int expireTime)
        {
            if (!TryNormalize(stateKind, itemId, expireTime, out var key, out var normalized))
                return false;

            if (_entries.TryGetValue(key, out var current)
                && current.ExpireTime == normalized.ExpireTime)
                return true;

            _entries[key] = normalized;
            IsDirty = true;
            return true;
        }

        public bool Remove(string stateKind, int itemId)
        {
            if (!TryNormalizeKey(stateKind, itemId, out var key))
                return false;

            if (!_entries.Remove(key))
                return false;

            IsDirty = true;
            return true;
        }

        public bool TryGetExpireTime(string stateKind, int itemId, out int expireTime)
        {
            expireTime = 0;
            if (!TryNormalizeKey(stateKind, itemId, out var key))
                return false;

            if (!_entries.TryGetValue(key, out var entry))
                return false;

            expireTime = entry.ExpireTime;
            return true;
        }

        public int RemoveExpired(long nowUnixSeconds)
        {
            var removeKeys = new List<ItemStateKey>();
            foreach (var pair in _entries)
            {
                if (pair.Value.ExpireTime <= nowUnixSeconds)
                    removeKeys.Add(pair.Key);
            }

            foreach (var key in removeKeys)
                _entries.Remove(key);

            if (removeKeys.Count > 0)
                IsDirty = true;

            return removeKeys.Count;
        }

        public List<ItemStateEntrySnapshot> BuildActiveSnapshots(
            string stateKind,
            long nowUnixSeconds)
        {
            var result = new List<ItemStateEntrySnapshot>();
            if (!ItemStateKinds.IsKnown(stateKind))
                return result;

            foreach (var entry in _entries.Values)
            {
                var remainingSeconds = entry.ExpireTime - nowUnixSeconds;
                if (!string.Equals(entry.StateKind, stateKind, StringComparison.Ordinal)
                    || remainingSeconds <= 0)
                    continue;

                result.Add(new ItemStateEntrySnapshot
                {
                    ItemId = entry.ItemId,
                    ExpireTime = ClampProtocolRemainingSeconds(remainingSeconds),
                });
            }

            result.Sort((left, right) => left.ItemId.CompareTo(right.ItemId));
            return result;
        }

        private static int ClampProtocolRemainingSeconds(long remainingSeconds)
        {
            if (remainingSeconds <= 0)
                return 0;

            if (remainingSeconds > int.MaxValue)
                return int.MaxValue;

            return (int)remainingSeconds;
        }

        public List<ItemStateEntry> GetEntries()
        {
            var result = new List<ItemStateEntry>();
            foreach (var entry in _entries.Values)
            {
                result.Add(new ItemStateEntry
                {
                    StateKind = entry.StateKind,
                    ItemId = entry.ItemId,
                    ExpireTime = entry.ExpireTime,
                });
            }

            result.Sort((left, right) =>
            {
                var kindCompare = string.CompareOrdinal(left.StateKind, right.StateKind);
                return kindCompare != 0
                    ? kindCompare
                    : left.ItemId.CompareTo(right.ItemId);
            });
            return result;
        }

        public void ClearDirtyState()
        {
            IsDirty = false;
        }

        private static bool TryNormalize(
            string stateKind,
            int itemId,
            int expireTime,
            out ItemStateKey key,
            out ItemStateEntry entry)
        {
            entry = null;
            if (!TryNormalizeKey(stateKind, itemId, out key)
                || expireTime <= 0)
                return false;

            entry = new ItemStateEntry
            {
                StateKind = key.StateKind,
                ItemId = key.ItemId,
                ExpireTime = expireTime,
            };
            return true;
        }

        private static bool TryNormalizeKey(string stateKind, int itemId, out ItemStateKey key)
        {
            key = default(ItemStateKey);
            if (!ItemStateKinds.IsKnown(stateKind) || itemId <= 0)
                return false;

            key = new ItemStateKey(stateKind, itemId);
            return true;
        }

        private struct ItemStateKey : IEquatable<ItemStateKey>
        {
            public ItemStateKey(string stateKind, int itemId)
            {
                StateKind = stateKind;
                ItemId = itemId;
            }

            public string StateKind { get; }

            public int ItemId { get; }

            public bool Equals(ItemStateKey other)
            {
                return ItemId == other.ItemId
                    && string.Equals(StateKind, other.StateKind, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is ItemStateKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((StateKind != null ? StateKind.GetHashCode() : 0) * 397) ^ ItemId;
                }
            }
        }
    }
}
