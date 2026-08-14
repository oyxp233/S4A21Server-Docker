using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal readonly struct CollectBoxSlotKey : IEquatable<CollectBoxSlotKey>
    {
        public CollectBoxSlotKey(int boxIndex, int slotIndex)
        {
            BoxIndex = boxIndex;
            SlotIndex = slotIndex;
        }

        public int BoxIndex { get; }

        public int SlotIndex { get; }

        public bool Equals(CollectBoxSlotKey other)
        {
            return BoxIndex == other.BoxIndex && SlotIndex == other.SlotIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is CollectBoxSlotKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (BoxIndex * 397) ^ SlotIndex;
            }
        }
    }

    internal sealed class CollectBoxModel
    {
        private readonly Dictionary<CollectBoxSlotKey, int> _items =
            new Dictionary<CollectBoxSlotKey, int>();
        private readonly HashSet<CollectBoxSlotKey> _dirtySlots =
            new HashSet<CollectBoxSlotKey>();

        public IReadOnlyCollection<CollectBoxSlotKey> DirtySlots => new List<CollectBoxSlotKey>(_dirtySlots);

        public bool HasDirtySlots => _dirtySlots.Count > 0;

        public int GetItemId(int boxIndex, int slotIndex)
        {
            return _items.TryGetValue(new CollectBoxSlotKey(boxIndex, slotIndex), out var itemId)
                ? itemId
                : 0;
        }

        public IReadOnlyList<CollectBoxSlotEntry> GetSlots(int boxIndex)
        {
            var result = new List<CollectBoxSlotEntry>();
            foreach (var pair in _items)
            {
                if (pair.Key.BoxIndex != boxIndex || pair.Value <= 0)
                    continue;

                result.Add(new CollectBoxSlotEntry
                {
                    BoxIndex = pair.Key.BoxIndex,
                    SlotIndex = pair.Key.SlotIndex,
                    ItemId = pair.Value,
                });
            }

            result.Sort((left, right) => left.SlotIndex.CompareTo(right.SlotIndex));
            return result;
        }

        public IReadOnlyList<CollectBoxSlotEntry> GetDirtySlots()
        {
            var result = new List<CollectBoxSlotEntry>();
            foreach (var key in _dirtySlots)
            {
                result.Add(new CollectBoxSlotEntry
                {
                    BoxIndex = key.BoxIndex,
                    SlotIndex = key.SlotIndex,
                    ItemId = GetItemId(key.BoxIndex, key.SlotIndex),
                });
            }

            return result;
        }

        public void AttachItem(int boxIndex, int slotIndex, int itemId)
        {
            if (!IsValidKey(boxIndex, slotIndex) || itemId <= 0)
                return;

            _items[new CollectBoxSlotKey(boxIndex, slotIndex)] = itemId;
        }

        public bool SetItem(int boxIndex, int slotIndex, int itemId)
        {
            if (!IsValidKey(boxIndex, slotIndex) || itemId <= 0)
                return false;

            var key = new CollectBoxSlotKey(boxIndex, slotIndex);
            if (_items.TryGetValue(key, out var current) && current == itemId)
                return true;

            _items[key] = itemId;
            _dirtySlots.Add(key);
            return true;
        }

        public bool ClearSlot(int boxIndex, int slotIndex)
        {
            if (!IsValidKey(boxIndex, slotIndex))
                return false;

            var key = new CollectBoxSlotKey(boxIndex, slotIndex);
            if (!_items.Remove(key))
                return false;

            _dirtySlots.Add(key);
            return true;
        }

        public bool TryFindSlotByItem(int itemId, out int boxIndex, out int slotIndex)
        {
            boxIndex = 0;
            slotIndex = 0;
            if (itemId <= 0)
                return false;

            var found = false;
            foreach (var pair in _items)
            {
                if (pair.Value != itemId)
                    continue;

                if (!found
                    || pair.Key.BoxIndex < boxIndex
                    || (pair.Key.BoxIndex == boxIndex && pair.Key.SlotIndex < slotIndex))
                {
                    boxIndex = pair.Key.BoxIndex;
                    slotIndex = pair.Key.SlotIndex;
                    found = true;
                }
            }

            return found;
        }

        public bool HasItemInBox(int boxIndex, int itemId)
        {
            if (itemId <= 0)
                return false;

            foreach (var pair in _items)
                if (pair.Key.BoxIndex == boxIndex && pair.Value == itemId)
                    return true;

            return false;
        }

        public void ClearDirtyState()
        {
            _dirtySlots.Clear();
        }

        private static bool IsValidKey(int boxIndex, int slotIndex)
        {
            return boxIndex >= 0 && slotIndex >= 0;
        }
    }
}
