using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.TitleBook
{
    internal struct TitleBookSlotKey : IEquatable<TitleBookSlotKey>
    {
        public TitleBookSlotKey(int category, int slotIndex)
        {
            Category = category;
            SlotIndex = slotIndex;
        }

        public int Category { get; }

        public int SlotIndex { get; }

        public bool Equals(TitleBookSlotKey other)
        {
            return Category == other.Category && SlotIndex == other.SlotIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is TitleBookSlotKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Category * 397) ^ SlotIndex;
            }
        }
    }

    internal sealed class TitleBookModel
    {
        public const int LockListTypeBase = 19;

        private readonly ItemCore[][] _items;
        private readonly HashSet<TitleBookSlotKey> _dirtySlots = new HashSet<TitleBookSlotKey>();

        public TitleBookModel()
        {
            _items = new ItemCore[TitleBookStaticDataProvider.CategoryCapacities.Count][];
            for (var category = 0; category < _items.Length; category++)
            {
                var capacity = TitleBookStaticDataProvider.CategoryCapacities[category];
                _items[category] = new ItemCore[capacity];
                for (var slot = 0; slot < capacity; slot++)
                    _items[category][slot] = new ItemCore();
            }
        }

        public IReadOnlyCollection<TitleBookSlotKey> DirtySlots => new List<TitleBookSlotKey>(_dirtySlots);

        public bool HasDirtySlots => _dirtySlots.Count > 0;

        public static InventoryListType GetLockListType(int category)
        {
            return (InventoryListType)(LockListTypeBase + category);
        }

        public static bool TryGetCategoryFromLockListType(InventoryListType listType, out int category)
        {
            category = (int)listType - LockListTypeBase;
            return category >= 0 && category < TitleBookStaticDataProvider.CategoryCapacities.Count;
        }

        public ItemCore GetItem(int category, int slotIndex)
        {
            if (!TryGetCell(category, slotIndex, out var cell))
                return null;

            return cell != null && !cell.IsEmpty ? cell.Copy() : null;
        }

        public IReadOnlyList<KeyValuePair<TitleBookSlotKey, ItemCore>> GetItems()
        {
            var result = new List<KeyValuePair<TitleBookSlotKey, ItemCore>>();
            for (var category = 0; category < _items.Length; category++)
            {
                for (var slot = 0; slot < _items[category].Length; slot++)
                {
                    var item = _items[category][slot];
                    if (item == null || item.IsEmpty)
                        continue;

                    result.Add(new KeyValuePair<TitleBookSlotKey, ItemCore>(
                        new TitleBookSlotKey(category, slot),
                        item.Copy()));
                }
            }

            return result;
        }

        public IReadOnlyList<KeyValuePair<TitleBookSlotKey, ItemCore>> GetDirtyItems()
        {
            var result = new List<KeyValuePair<TitleBookSlotKey, ItemCore>>();
            foreach (var key in _dirtySlots)
                result.Add(new KeyValuePair<TitleBookSlotKey, ItemCore>(key, GetItem(key.Category, key.SlotIndex)));
            return result;
        }

        public void AttachItem(int category, int slotIndex, ItemCore core)
        {
            if (!TryGetCell(category, slotIndex, out _))
                return;

            _items[category][slotIndex] = Normalize(core);
        }

        public bool SetItem(int category, int slotIndex, ItemCore core)
        {
            if (!TryGetCell(category, slotIndex, out _))
                return false;

            _items[category][slotIndex] = Normalize(core);
            _dirtySlots.Add(new TitleBookSlotKey(category, slotIndex));
            return true;
        }

        public bool ClearItem(int category, int slotIndex)
        {
            return SetItem(category, slotIndex, null);
        }

        public TitleBookCategorySnapshot BuildSnapshot(int category)
        {
            if (category < 0 || category >= _items.Length)
                return null;

            var snapshot = new TitleBookCategorySnapshot
            {
                InfoType = 0,
                OwnerId16 = 0,
                Category = category,
            };

            for (var slot = 0; slot < _items[category].Length; slot++)
            {
                var item = _items[category][slot];
                if (item == null || item.IsEmpty)
                    continue;

                snapshot.Entries.Add(TitleBookItemProjection.ToListEntry(slot, item));
            }

            return snapshot;
        }

        public List<TitleBookCategorySnapshot> BuildSnapshots()
        {
            var result = new List<TitleBookCategorySnapshot>();
            for (var category = 0; category < _items.Length; category++)
                result.Add(BuildSnapshot(category));
            return result;
        }

        public void ClearDirtyState()
        {
            _dirtySlots.Clear();
        }

        private bool TryGetCell(int category, int slotIndex, out ItemCore cell)
        {
            cell = null;
            if (category < 0 || category >= _items.Length)
                return false;
            if (slotIndex < 0 || slotIndex >= _items[category].Length)
                return false;

            cell = _items[category][slotIndex];
            return true;
        }

        private static ItemCore Normalize(ItemCore core)
        {
            if (core == null || core.IsEmpty)
                return new ItemCore();

            return core.Copy();
        }
    }
}
