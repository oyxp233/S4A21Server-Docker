using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class CargoModel
    {
        public const short SlotStart = 0;
        public const short SlotEnd = 151;
        public const int SlotCount = SlotEnd - SlotStart + 1;
        public const ushort DefaultCapacity = 8;

        private readonly ItemCore[] _items = new ItemCore[SlotCount];
        private readonly HashSet<short> _dirtySlots = new HashSet<short>();
        private ushort _capacity;
        private bool _stateDirty;

        public CargoModel(int characterId, int accountId, ushort capacity = DefaultCapacity)
        {
            CharacterId = characterId;
            AccountId = accountId;
            Capacity = capacity;
            InitEmptySlots();
        }

        public int CharacterId { get; }

        public int AccountId { get; }

        public ushort Capacity
        {
            get => _capacity;
            set
            {
                var normalized = NormalizeCapacity(value);
                if (_capacity == normalized)
                    return;

                _capacity = normalized;
                _stateDirty = true;
            }
        }

        public IReadOnlyCollection<short> DirtySlots => _dirtySlots;

        public bool IsStateDirty => _stateDirty;

        public ItemCore GetItem(short slotIndex)
        {
            return TryGetIndex(slotIndex, out var index) ? NormalizeItem(_items[index]) : null;
        }

        public IReadOnlyList<KeyValuePair<short, ItemCore>> GetItems()
        {
            var result = new List<KeyValuePair<short, ItemCore>>();
            for (var index = 0; index < _items.Length; index++)
            {
                var item = NormalizeItem(_items[index]);
                if (item != null)
                    result.Add(new KeyValuePair<short, ItemCore>((short)(SlotStart + index), item));
            }

            return result;
        }

        public void AttachItem(short slotIndex, ItemCore core)
        {
            if (!TryGetIndex(slotIndex, out var index))
                return;

            _items[index].CopyFrom(core ?? new ItemCore());
        }

        public bool SetItem(short slotIndex, ItemCore core)
        {
            if (!IsOpenSlot(slotIndex) || !TryGetIndex(slotIndex, out var index))
                return false;

            if (core == null || core.IsEmpty)
                _items[index].Init();
            else
                _items[index].CopyFrom(core);
            _dirtySlots.Add(slotIndex);
            return true;
        }

        public bool RemoveItem(short slotIndex)
        {
            if (!IsOpenSlot(slotIndex) || !TryGetIndex(slotIndex, out var index))
                return false;
            if (NormalizeItem(_items[index]) == null)
                return false;

            _items[index].Init();
            _dirtySlots.Add(slotIndex);
            return true;
        }

        public void ClearDirtyState()
        {
            _dirtySlots.Clear();
            _stateDirty = false;
        }

        public bool IsOpenSlot(short slotIndex)
        {
            return TryGetIndex(slotIndex, out _) && slotIndex < Capacity;
        }

        public static ushort NormalizeCapacity(ushort capacity)
        {
            if (capacity == 0)
                return DefaultCapacity;
            return capacity > SlotCount ? (ushort)SlotCount : capacity;
        }

        private static bool TryGetIndex(short slotIndex, out int index)
        {
            if (slotIndex < SlotStart || slotIndex > SlotEnd)
            {
                index = -1;
                return false;
            }

            index = slotIndex - SlotStart;
            return true;
        }

        private void InitEmptySlots()
        {
            for (var index = 0; index < _items.Length; index++)
                _items[index] = new ItemCore();
        }

        private static ItemCore NormalizeItem(ItemCore core)
        {
            return core != null && !core.IsEmpty ? core : null;
        }
    }
}
