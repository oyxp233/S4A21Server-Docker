using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    public sealed class EnchantByBeadResult
    {
        public const byte ErrorInvalidBead = 0x11;
        public const byte ErrorInvalidTarget = 0x13;
        public const byte ErrorUnsupported = 0x17;

        public bool Success { get; set; }

        public byte ErrorCode { get; set; }

        public EnchantByBeadCommand Command { get; set; }

        public InventoryListType TargetListType { get; set; }

        public short TargetSlotIndex { get; set; }

        public InventoryListType BeadListType { get; set; }

        public short BeadSlotIndex { get; set; }

        public int BeadRemainingStackCount { get; set; }

        public int EnchantCardItemId { get; set; }

        public static EnchantByBeadResult Error(EnchantByBeadCommand command, byte errorCode)
        {
            return new EnchantByBeadResult
            {
                Command = command,
                ErrorCode = errorCode,
            };
        }

        public static EnchantByBeadResult Ok(EnchantByBeadCommand command, int beadRemainingStackCount, int enchantCardItemId)
        {
            return new EnchantByBeadResult
            {
                Success = true,
                Command = command,
                TargetListType = command.TargetListType,
                TargetSlotIndex = command.TargetSlotIndex,
                BeadListType = command.BeadListType,
                BeadSlotIndex = command.BeadSlotIndex,
                BeadRemainingStackCount = beadRemainingStackCount,
                EnchantCardItemId = enchantCardItemId,
            };
        }
    }
}
