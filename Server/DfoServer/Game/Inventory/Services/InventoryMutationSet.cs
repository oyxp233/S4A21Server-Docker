using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal readonly struct InventorySlotMutation
    {
        public InventorySlotMutation(InventoryListType listType, short slotIndex)
        {
            ListType = listType;
            SlotIndex = slotIndex;
        }

        public InventoryListType ListType { get; }

        public short SlotIndex { get; }
    }

    internal sealed class InventoryMutationSet
    {
        private readonly List<InventorySlotMutation> _slots = new List<InventorySlotMutation>();

        public IReadOnlyList<InventorySlotMutation> Slots => _slots;

        public bool HasChanges => _slots.Count > 0;

        public void AddSlot(InventoryListType listType, short slotIndex)
        {
            for (var index = 0; index < _slots.Count; index++)
            {
                var existing = _slots[index];
                if (existing.ListType == listType && existing.SlotIndex == slotIndex)
                    return;
            }

            _slots.Add(new InventorySlotMutation(listType, slotIndex));
        }

        public void AddRange(InventoryMutationSet other)
        {
            if (other == null)
                return;

            foreach (var slot in other.Slots)
                AddSlot(slot.ListType, slot.SlotIndex);
        }
    }
}
