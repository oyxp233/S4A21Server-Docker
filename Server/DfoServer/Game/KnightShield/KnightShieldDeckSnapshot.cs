using System;
using System.Collections.Generic;

namespace DfoServer.Game.KnightShield
{
    public sealed class KnightShieldDeckSnapshot
    {
        public const int MainSlotIndex = 0;
        public const int SlotCount = 5;

        private readonly int[] _shieldItemIds;
        private readonly IReadOnlyList<int> _readOnlyShieldItemIds;

        public KnightShieldDeckSnapshot()
        {
            _shieldItemIds = new int[SlotCount];
            _readOnlyShieldItemIds = Array.AsReadOnly(_shieldItemIds);
        }

        public KnightShieldDeckSnapshot(IReadOnlyList<int> shieldItemIds)
            : this()
        {
            if (shieldItemIds == null)
                throw new ArgumentNullException(nameof(shieldItemIds));
            if (shieldItemIds.Count != SlotCount)
                throw new ArgumentException($"盾牌 deck 必须恰好包含 {SlotCount} 个槽", nameof(shieldItemIds));

            for (var slotIndex = 0; slotIndex < SlotCount; slotIndex++)
            {
                var shieldItemId = shieldItemIds[slotIndex];
                ValidateShieldItemId(shieldItemId, nameof(shieldItemIds));
                _shieldItemIds[slotIndex] = shieldItemId;
            }
        }

        public IReadOnlyList<int> ShieldItemIds => _readOnlyShieldItemIds;

        public int MainShieldItemId => _shieldItemIds[MainSlotIndex];

        public int GetShieldItemId(int slotIndex)
        {
            ValidateSlotIndex(slotIndex);
            return _shieldItemIds[slotIndex];
        }

        public int[] ToArray()
        {
            return (int[])_shieldItemIds.Clone();
        }

        internal void SetLoadedSlot(int slotIndex, int shieldItemId)
        {
            ValidateSlotIndex(slotIndex);
            if (shieldItemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(shieldItemId), "已持久化的盾牌物品 ID 必须大于 0");
            _shieldItemIds[slotIndex] = shieldItemId;
        }

        private static void ValidateSlotIndex(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= SlotCount)
                throw new ArgumentOutOfRangeException(nameof(slotIndex), $"盾牌槽必须在 0..{SlotCount - 1} 范围内");
        }

        private static void ValidateShieldItemId(int shieldItemId, string parameterName)
        {
            if (shieldItemId < 0)
                throw new ArgumentOutOfRangeException(parameterName, "盾牌物品 ID 不能为负数");
        }
    }
}
