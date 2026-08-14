using DfoServer.Game.Inventory;

namespace DfoServer.Game.ItemUpgrade
{
    internal sealed class SeparateUpgradeCommand
    {
        public InventoryListType TargetListType { get; set; }
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public short MaterialSlotIndex { get; set; }
    }

    internal sealed class SeparateUpgradeResult
    {
        public const byte ErrorMaterialCommit = 17;
        public const byte ErrorInvalidTarget = 4;
        public const byte ErrorDurability = 7;
        public const byte ErrorUnsupported = 13;
        public const byte ErrorNotWeapon = 19;
        public const byte ErrorInvalidMaterial = 22;
        public const byte ErrorMaxLevel = 95;

        public SeparateUpgradeCommand Command { get; set; }
        public byte ErrorCode { get; set; }
        public bool UpgradeSucceeded { get; set; }
        public byte OldLevel { get; set; }
        public byte NewLevel { get; set; }
        public byte TargetReinforceLevel { get; set; }
        public int SuccessWeight { get; set; }
        public int MaterialItemTemplateId { get; set; }
        public int MaterialCost { get; set; }
        public int MaterialRemainingCount { get; set; }
        public bool NoticeRequired { get; set; }
        public ItemCore TargetItemSnapshot { get; set; }

        public static SeparateUpgradeResult Error(SeparateUpgradeCommand command, byte errorCode)
            => new SeparateUpgradeResult { Command = command, ErrorCode = errorCode };
    }
}
