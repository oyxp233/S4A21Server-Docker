using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    // 装备实例的品质常量。服务端生成装备统一写这个种子:
    // 999999998 = 最上级(真机实证); 0 会导致修理后装备消失, 禁用。
    public static class ItemQuality
    {
        public const uint TopQualitySeed = 999999998u;
    }

    public sealed class InventoryMoveRequest
    {
        public InventoryListType SourceListType { get; set; }

        public short SourceSlotIndex { get; set; }

        public int MoveCount { get; set; }

        public int SourceInstanceValue { get; set; }

        public InventoryListType DestinationListType { get; set; }

        public short DestinationSlotIndex { get; set; }

        public int DestinationInstanceValue { get; set; }
    }

    public sealed class InventoryMoveResult
    {
        public InventoryListType SourceListType { get; set; }

        public short SourceSlotIndex { get; set; }

        public int MoveValue32 { get; set; }

        public InventoryListType DestinationListType { get; set; }

        public short DestinationSlotIndex { get; set; }

        public bool Mutated { get; set; }

        public bool AckError { get; set; }

        public InventoryMoveFailureReason FailureReason { get; set; }

        public short AffectedEquipmentSlot { get; set; } = -1;

        public Subtype0TailMoveMutation Subtype0TailMutation { get; set; }

        public bool PetCreatureStateChanged { get; set; }

        public bool PetItemStateChanged { get; set; }

        public bool PetItemFullRefresh { get; set; }

        public List<short> PetCreatureRefreshSlots { get; } = new List<short>();

        public List<short> EquipmentRefreshSlots { get; } = new List<short>();
    }

    public enum InventoryMoveFailureReason
    {
        None = 0,
        CharmCarryLimit = 1,
    }

    public sealed class Subtype0TailMoveMutation
    {
        public bool ForgingChanged { get; set; }

        public byte Forging { get; set; }

        public bool NameTagChanged { get; set; }

        public uint NameTagItemId { get; set; }

        public uint NameTagExpireTime { get; set; }

        public bool EquippedCreatureChanged { get; set; }

        public uint EquippedCreatureItemId { get; set; }

        public byte[] EquippedCreatureNameBytes { get; set; } = Array.Empty<byte>();

        public byte EquippedCreatureAliveState { get; set; }
    }

    internal enum EquipOutcome
    {
        Equipped,
        Unequipped,
        ReverseError,
        NoOp,
    }

    public sealed class InventoryMutationResult
    {
        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int RemainingStackCount { get; set; }

        public int InstanceValue { get; set; }

        public ushort Durability { get; set; }

        public byte ExtData0 { get; set; }

        public int ExpireTime { get; set; }

        public int UpdatedGold { get; set; }

        public int UpdatedSp { get; set; }

        public int UpdatedCoin { get; set; }

        public int UpdatedTokenCera { get; set; }

        public int UpdatedHappyTokenCera { get; set; }

        public short RequestedCount { get; set; }

        public short AppliedCount { get; set; }

        // 本次购买是否扣了金币(用于商城回包决定是否刷新主背包 slot0 金币显示)。
        public bool GoldSpent { get; set; }

        // 主背包虚拟资源发生变化，方向不限；资源身份由 SlotIndex 统一解析。
        public bool MainVirtualCountChanged { get; set; }

        // 契约等道具购买即消耗，不入库；为 true 时跳过 ITEM_LIST 更新通知。
        public bool ConsumedOnPurchase { get; set; }

        public int CostItemTemplateId { get; set; }

        public int CostItemNewStackCount { get; set; }

        public short CostItemSlotIndex { get; set; }

        public List<InventoryMutationResult> ExtraResults { get; } = new List<InventoryMutationResult>();

        public int PetCreatureKey { get; set; }

        public int PetSatietyBefore { get; set; }

        public int PetSatietyAfter { get; set; }

        public bool PetSatietyChanged { get; set; }

        public bool NameTagEquipped { get; set; }

        // 购买奖励因背包溢出转邮件时置 true，客户端只需要邮箱提醒。
        public bool DeliveredByMail { get; set; }
    }

    public enum PersonalCargoUpgradeTicketStatus
    {
        NotApplicable,
        Upgraded,
        MissingItem,
        Maxed,
        Locked,
    }

    public sealed class PersonalCargoUpgradeTicketResult
    {
        public PersonalCargoUpgradeTicketStatus Status { get; set; }

        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public ushort PreviousListParam16 { get; set; }

        public ushort NewListParam16 { get; set; }

        public InventoryMutationResult ConsumedItem { get; set; }

        public bool Handled => Status != PersonalCargoUpgradeTicketStatus.NotApplicable;

        public bool Success => Status == PersonalCargoUpgradeTicketStatus.Upgraded;
    }

    public enum AccountCargoUpgradeToolStatus
    {
        NotApplicable,
        Upgraded,
        MissingItem,
        Maxed,
        NotOpened,
        Locked,
    }

    public sealed class AccountCargoUpgradeToolResult
    {
        public AccountCargoUpgradeToolStatus Status { get; set; }

        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int PreviousSelectionKey { get; set; }

        public int NewSelectionKey { get; set; }

        public InventoryMutationResult ConsumedItem { get; set; }

        public bool Handled => Status != AccountCargoUpgradeToolStatus.NotApplicable;

        public bool Success => Status == AccountCargoUpgradeToolStatus.Upgraded;
    }

    public sealed class CreatureHatchResult
    {
        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int EggItemTemplateId { get; set; }

        public int HatchedItemTemplateId { get; set; }

        public int PetSerialOrHandle { get; set; }
    }

    public sealed class PetCreatureRenameRequest
    {
        public InventoryListType SourceListType { get; set; } = InventoryListType.Pet;

        public short SourceSlotIndex { get; set; }

        public byte[] NameBytes { get; set; } = Array.Empty<byte>();
    }

    public sealed class PetCreatureRenameResult
    {
        public InventoryListType SourceListType { get; set; } = InventoryListType.Pet;

        public short SourceSlotIndex { get; set; }

        public int PetItemTemplateId { get; set; }

        public int CreatureSerial { get; set; }

        public byte[] NameBytes { get; set; } = Array.Empty<byte>();

        public bool SourceItemConsumed { get; set; }

        public int SourceRemainingCount { get; set; }
    }

    public sealed class RepairEquipmentResult
    {
        public short SlotIndex { get; set; }
        public int UpdatedGold { get; set; }
        public int Cost { get; set; }
    }

    public sealed class BoosterRewardResult
    {
        public InventoryListType ListType { get; set; } = InventoryListType.Main;

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int StackCount { get; set; }

        public int GrantedCount { get; set; }

        public SpecialRewardOutcome SpecialOutcome { get; set; }

        internal static BoosterRewardResult FromSpecialOutcome(SpecialRewardOutcome outcome)
        {
            if (outcome == null) return null;

            if (outcome.Kind == SpecialRewardKind.ReviveCoin)
            {
                return new BoosterRewardResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = outcome.WalletSlot,
                    ItemTemplateId = ReviveCoin.ReviveCoinService.ItemId,
                    StackCount = outcome.WalletNewTotal,
                    GrantedCount = outcome.Count,
                    SpecialOutcome = outcome,
                };
            }

            if (outcome.Kind == SpecialRewardKind.HappyTokenCera)
            {
                return new BoosterRewardResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = -1,
                    ItemTemplateId = outcome.ItemTemplateId,
                    StackCount = 0,
                    GrantedCount = outcome.Count,
                    SpecialOutcome = outcome,
                };
            }

            // 契约: 不入库, SlotIndex=0 不对应真实槽位
            return new BoosterRewardResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = 0,
                ItemTemplateId = outcome.ItemTemplateId,
                StackCount = 0,
                GrantedCount = outcome.Count,
                SpecialOutcome = outcome,
            };
        }
    }

    public sealed class BoosterUseRequest
    {
        public short? SlotIndex { get; set; }

        public IReadOnlyList<int> SelectedItemTemplateIds { get; set; } = Array.Empty<int>();

        public int ExpectedItemTemplateId { get; set; }

        public short? MaterialSlotIndex { get; set; }

        public int ExpectedMaterialItemTemplateId { get; set; }

        public int RequestedCount { get; set; } = 1;
    }

    public sealed class BoosterUseResult
    {
        public const byte ErrorInvalidRequest = 0x13;

        public const byte ErrorInventoryFull = 0x04;

        public const byte ErrorMaterialNotEnough = 0x11;

        public byte ErrorCode { get; set; } = ErrorInvalidRequest;

        public short SourceSlotIndex { get; set; }

        public int SourceItemTemplateId { get; set; }

        public int SourceRemainingStackCount { get; set; }

        public int SourceInstanceValue { get; set; }

        public int ConsumedSourceCount { get; set; }

        public int ConsumedMaterialItemTemplateId { get; set; }

        public int ConsumedMaterialCount { get; set; }

        public short ConsumedMaterialSlotIndex { get; set; }

        public int ConsumedMaterialRemainingStackCount { get; set; }

        public int RequiredMaterialItemTemplateId { get; set; }

        public string RequiredMaterialName { get; set; }

        public int RequiredMaterialCount { get; set; }

        public int AvailableMaterialCount { get; set; }

        public List<BoosterRewardResult> Rewards { get; } = new List<BoosterRewardResult>();

        public List<PackageGrantedItem> DisplayRewards { get; } = new List<PackageGrantedItem>();

        public List<PackageGrantedItem> DoubleRewards { get; } = new List<PackageGrantedItem>();

        public bool IsSeriaLuckValueSource { get; set; }

        public int SeriaLuckValueBefore { get; set; }

        public int SeriaLuckValueAfter { get; set; }

        public int SeriaLuckValueMax { get; set; }

        public bool SeriaLuckDoubleTriggered { get; set; }

        public byte MagicBoxClientType { get; set; }

        public List<(int itemTemplateId, int count)> ActivatedPremiums { get; } = new List<(int itemTemplateId, int count)>();
    }

    public sealed class EquipmentSocketMutationResult
    {
        public InventoryMutationResult MaterialItem { get; set; }

        public bool MaterialConsumed { get; set; }
    }

    public sealed class EquipmentEmblemApplyRequest
    {
        public short EmblemSlot { get; set; }

        public int EmblemItemTemplateId { get; set; }

        public byte SocketIndex { get; set; }
    }

    public sealed class EquipmentEmblemMutationResult
    {
        public InventoryListType TargetListType { get; set; } = InventoryListType.Main;

        public short TargetSlotIndex { get; set; }

        public bool TargetEquipped { get; set; }

        public List<InventoryMutationResult> ConsumedEmblems { get; } = new List<InventoryMutationResult>();
    }

    public sealed class AvatarSocketMutationResult
    {
        public InventoryMutationResult MaterialItem { get; set; }

        public bool MaterialConsumed { get; set; }
    }

    public enum PurifyItemAction
    {
        Unknown = 0,
        Purify = 1,
        Clear = 2,
    }

    public sealed class PurifyItemRequest
    {
        public short TargetSlotIndex { get; set; }

        public int TargetItemTemplateId { get; set; }

        public short MaterialSlotIndex { get; set; }

        public int MaterialItemTemplateId { get; set; }
    }

    public sealed class PurifyItemResult
    {
        public const byte ErrorInvalidRequest = 0x01;
        public const byte ErrorInvalidTarget = 0x02;
        public const byte ErrorInvalidMaterial = 0x03;
        public const byte ErrorUnsupported = 0x04;
        public const byte ErrorLocked = 0x05;

        public PurifyItemRequest Request { get; set; }

        public byte ErrorCode { get; set; }

        public PurifyItemAction Action { get; set; }

        public short TargetSlotIndex { get; set; }

        public short MaterialSlotIndex { get; set; }

        public int MaterialRemainingCount { get; set; }

        public byte AmplifyType { get; set; }

        public ushort AmplifyValue { get; set; }
    }

    public enum InvestItemAmplifyOptionAction
    {
        Invest = 0,
        Twist = 1,
        PureGold = 2,
    }

    public sealed class InvestItemAmplifyOptionRequest
    {
        public InvestItemAmplifyOptionAction Action { get; set; }

        public short TargetSlotIndex { get; set; }

        public int TargetItemTemplateId { get; set; }

        public short MaterialSlotIndex { get; set; }

        public int MaterialItemTemplateId { get; set; }

        public byte SelectedOption { get; set; }
    }

    public sealed class InvestItemAmplifyOptionResult
    {
        public const byte ErrorInvalidRequest = 17;
        public const byte ErrorInvalidTarget = 17;
        public const byte ErrorInvalidMaterial = 17;
        public const byte ErrorUnsupported = 8;
        public const byte ErrorLocked = 17;
        public const byte ErrorSameOption = 23;
        public const byte ErrorAlreadyHasAmplifyOption = 20;
        public const byte ErrorNoAmplifyOption = 21;
        public const byte ErrorAlreadyUpgraded = 18;

        public InvestItemAmplifyOptionRequest Request { get; set; }

        public byte ErrorCode { get; set; }

        public short TargetSlotIndex { get; set; }

        public short MaterialSlotIndex { get; set; }

        public int MaterialRemainingCount { get; set; }

        public byte AmplifyType { get; set; }

        public ushort AmplifyValue { get; set; }

        public byte AmplifyLevel { get; set; }
    }

    public sealed class AvatarEmblemMutationResult
    {
        public InventoryListType TargetListType { get; set; } = InventoryListType.Avatar;

        public short TargetSlotIndex { get; set; }

        public bool TargetEquipped { get; set; }

        public List<InventoryMutationResult> ConsumedEmblems { get; } = new List<InventoryMutationResult>();
    }
}
