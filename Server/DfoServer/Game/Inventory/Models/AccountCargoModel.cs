using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class AccountCargoModel
    {
        public const short SlotStart = 0;
        public const short SlotEnd = 63;
        public const int SlotCount = SlotEnd - SlotStart + 1;

        private readonly ItemCore[] _items = new ItemCore[SlotCount];
        private readonly HashSet<short> _dirtySlots = new HashSet<short>();
        private ushort _capacity;
        private int _money;
        private bool _stateDirty;

        public AccountCargoModel(int accountId, ushort selectionKey = 0, int money = 0)
        {
            AccountId = accountId;
            SelectionKey = selectionKey;
            Money = money;
            InitEmptySlots();
        }

        public int AccountId { get; }

        public ushort SelectionKey
        {
            get => _capacity;
            set
            {
                var normalized = NormalizeSelectionKey(value);
                if (_capacity == normalized)
                    return;

                _capacity = normalized;
                _stateDirty = true;
            }
        }

        public ushort Capacity
        {
            get => _capacity;
            set => SelectionKey = value;
        }

        public int Money
        {
            get => _money;
            set
            {
                if (_money == value)
                    return;

                _money = value;
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

        public static ushort NormalizeSelectionKey(ushort value)
        {
            return value > SlotCount ? (ushort)SlotCount : value;
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
