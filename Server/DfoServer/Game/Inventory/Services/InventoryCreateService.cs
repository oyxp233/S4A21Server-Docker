using System;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    internal enum ItemCreateReason
    {
        Unknown = 0,
        DungeonDrop = 1,
        MallPurchase = 2,
        NpcShopPurchase = 3,
        PackageOpen = 4,
        QuestReward = 5,
        MailAttachment = 6,
        AdminGrant = 7,
        CharacterCreate = 8,
        DailyRefill = 9,
    }

    internal sealed class InventoryCreateOptions
    {
        public ushort AvatarAbilityNo { get; set; }

        public byte CreatureType { get; set; }

        public int ExpireTime { get; set; }

        public AvatarDetail AvatarDetailTemplate { get; set; }

        public CreatureDetail CreatureDetailTemplate { get; set; }
    }

    internal sealed class InventoryCreateResult
    {
        public ItemCore Core { get; set; }

        public AvatarDetail AvatarDetail { get; set; }

        public CreatureDetail CreatureDetail { get; set; }

        public InventoryInsertPlan InsertPlan { get; set; }

        public InventoryInsertResult InsertResult { get; set; }
    }

    internal static class InventoryCreateService
    {
        private const byte UnidentifiedAmplifyFlag = 0x80;

        internal static InventoryCreateResult Create(
            byte itemKind,
            int itemId,
            ItemCreateReason reason,
            int count)
        {
            return Create(itemKind, itemId, reason, count, null);
        }

        internal static InventoryCreateResult Create(
            byte itemKind,
            int itemId,
            ItemCreateReason reason,
            int count,
            InventoryCreateOptions options)
        {
            return new InventoryCreateResult
            {
                Core = CreateCore(itemKind, itemId, reason, count, options),
            };
        }

        internal static bool TryCreateCore(
            int itemId,
            ItemCreateReason reason,
            int count,
            out ItemCore core)
        {
            core = null;
            if (!ItemMetadataResolver.TryResolveItemKind(itemId, out var itemKind))
                return false;

            core = CreateCore(itemKind, itemId, reason, count, null);
            return true;
        }

        internal static bool TryCreateAndInsert(
            InventoryService inventory,
            int itemId,
            ItemCreateReason reason,
            int count,
            out InventoryCreateResult result)
        {
            return TryCreateAndInsert(inventory, itemId, reason, count, null, out result);
        }

        internal static bool TryCreateAndInsert(
            InventoryService inventory,
            int itemId,
            ItemCreateReason reason,
            int count,
            InventoryCreateOptions options,
            out InventoryCreateResult result)
        {
            result = new InventoryCreateResult();
            if (!ItemMetadataResolver.TryResolveItemKind(itemId, out var itemKind))
                return false;

            var core = CreateCore(itemKind, itemId, reason, count, options);
            return TryCreateAndInsertCore(inventory, core, reason, count, options, out result);
        }

        internal static ItemCore CreateCore(
            byte itemKind,
            int itemId,
            ItemCreateReason reason,
            int count)
        {
            return CreateCore(itemKind, itemId, reason, count, null);
        }

        internal static ItemCore CreateCore(
            byte itemKind,
            int itemId,
            ItemCreateReason reason,
            int count,
            InventoryCreateOptions options)
        {
            var core = ItemCore.Create(itemKind, itemId);
            var metadata = ResolveMetadata(itemId);
            ApplyStaticDefaults(core, metadata, Math.Max(1, count), options);
            ApplyCreateReason(core, metadata, reason);
            ApplyExplicitExpireTime(core, options);
            return core;
        }

        internal static bool TryRegisterInsertedDetails(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            out InventoryCreateResult result)
        {
            return TryRegisterInsertedDetails(inventory, listType, slotIndex, null, out result);
        }

        internal static bool TryRegisterInsertedDetails(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            InventoryCreateOptions options,
            out InventoryCreateResult result)
        {
            return TryRegisterInsertedDetails(
                inventory,
                listType,
                slotIndex,
                ItemCreateReason.Unknown,
                options,
                out result);
        }

        internal static bool TryRegisterInsertedDetails(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            ItemCreateReason reason,
            InventoryCreateOptions options,
            out InventoryCreateResult result)
        {
            result = new InventoryCreateResult();
            if (inventory == null)
                return false;

            var core = inventory.GetItem(listType, slotIndex);
            if (core == null)
                return false;

            result.Core = core;
            if (!TryCreateDetails(inventory, core, reason, options, out result))
                return false;

            inventory.MarkDirty(listType, slotIndex);
            return true;
        }

        internal static bool TryCreateDetails(
            InventoryService inventory,
            ItemCore core,
            InventoryCreateOptions options,
            out InventoryCreateResult result)
        {
            return TryCreateDetails(inventory, core, ItemCreateReason.Unknown, options, out result);
        }

        internal static bool TryCreateDetails(
            InventoryService inventory,
            ItemCore core,
            ItemCreateReason reason,
            InventoryCreateOptions options,
            out InventoryCreateResult result)
        {
            result = new InventoryCreateResult { Core = core };
            if (inventory == null || core == null)
                return false;

            if (core.ItemKind == ItemCore.KindAvatar)
            {
                result.AvatarDetail = inventory.AvatarDetails.CreateDetail(
                    core,
                    inventory.AccountId,
                    inventory.CharacterId,
                    false);
                if (result.AvatarDetail == null)
                    return false;

                ApplyAvatarDefaultSockets(result.AvatarDetail, core.ItemId);
                ApplyAvatarDetailTemplate(result.AvatarDetail, options?.AvatarDetailTemplate);
                ApplyCreateReason(result, reason, options);
                return true;
            }

            if (core.ItemKind == ItemCore.KindCreature)
            {
                if (options != null)
                    core.SealFlag = options.CreatureType;

                result.CreatureDetail = inventory.CreatureDetails.CreateDetail(core, false);
                if (result.CreatureDetail == null)
                    return false;

                ApplyCreatureDetailTemplate(result.CreatureDetail, options?.CreatureDetailTemplate);
                ApplyCreateReason(result, reason, options);
                return true;
            }

            ApplyCreateReason(result, reason, options);
            return true;
        }

        private static void ApplyAvatarDetailTemplate(AvatarDetail target, AvatarDetail template)
        {
            if (target == null || template == null)
                return;

            target.ExpireDate = template.ExpireDate;
            target.ClearAvatarId = template.ClearAvatarId;
            target.JewelSocket = template.JewelSocket;
            target.Color1 = template.Color1;
            target.Color2 = template.Color2;
            target.DeleteDate = template.DeleteDate;
        }

        private static void ApplyAvatarDefaultSockets(AvatarDetail target, int itemTemplateId)
        {
            if (target == null || itemTemplateId <= 0)
                return;

            var socketTypes = ItemMetadataResolver.ResolveAvatarDefaultSocketTypes(itemTemplateId);
            if (socketTypes == null || socketTypes.Count == 0)
                return;

            var socket = target.JewelSocketView;
            for (var index = 0; index < JewelSocket.SocketCount; index++)
            {
                var socketType = index < socketTypes.Count ? socketTypes[index] : (byte)0;
                socket.Set(index, socketType, socketType != 0 ? -1 : 0);
            }

            target.JewelSocketView = socket;
        }

        private static void ApplyCreatureDetailTemplate(CreatureDetail target, CreatureDetail template)
        {
            if (target == null || template == null)
                return;

            target.NameBytes = template.NameBytes;
            target.Field04 = template.Field04;
            target.ModeFlag = template.ModeFlag;
            target.Mode1Field0A = template.Mode1Field0A;
            target.Mode1Field0B = template.Mode1Field0B;
            target.ProgressValue32 = template.ProgressValue32;
            target.FieldAfterValue32 = template.FieldAfterValue32;
            target.ExpireDate = template.ExpireDate;
            target.TailFlag = template.TailFlag;
        }

        internal static void DetachCreatedDetails(
            InventoryService inventory,
            InventoryCreateResult result)
        {
            if (inventory == null || result == null)
                return;

            if (result.AvatarDetail != null)
                inventory.AvatarDetails.Detach(result.AvatarDetail.AvatarUid);
            if (result.CreatureDetail != null)
                inventory.CreatureDetails.Detach(result.CreatureDetail.Uid);
        }

        private static void ApplyStaticDefaults(
            ItemCore core,
            ItemMetadata metadata,
            int count,
            InventoryCreateOptions options)
        {
            if (core == null)
                return;

            switch (core.ItemKind)
            {
                case ItemCore.KindEquipment:
                case ItemCore.KindCreatureEquipment:
                    ApplyEquipmentDefaults(core, metadata);
                    return;
                case ItemCore.KindAvatar:
                    ApplyAvatarDefaults(core, metadata, options);
                    return;
                case ItemCore.KindCreature:
                    ApplyCreatureDefaults(core, metadata, options);
                    return;
                default:
                    ApplyStackableDefaults(core, count);
                    return;
            }
        }

        private static void ApplyEquipmentDefaults(ItemCore core, ItemMetadata metadata)
        {
            core.InstanceValue = ServerRandom.Next();
            core.Durability = metadata != null ? metadata.Durability : (ushort)0;
            core.SealFlag = metadata != null && metadata.IsSealed ? (byte)1 : (byte)0;
            var expireTime = ResolveEquipmentExpireTime(core.ItemId);
            if (expireTime > 0)
                core.ExpireTime = expireTime;
        }

        private static void ApplyAvatarDefaults(
            ItemCore core,
            ItemMetadata metadata,
            InventoryCreateOptions options)
        {
            core.AvatarUid = 0;
            core.AbilityNo = options != null ? options.AvatarAbilityNo : (ushort)0;
            core.SealFlag = metadata != null && metadata.IsSealed ? (byte)1 : (byte)0;
        }

        private static void ApplyCreatureDefaults(
            ItemCore core,
            ItemMetadata metadata,
            InventoryCreateOptions options)
        {
            core.CreatureUid = 0;
            core.SealFlag = options != null ? options.CreatureType : (byte)0;
        }

        private static void ApplyStackableDefaults(ItemCore core, int count)
        {
            core.Count = count;
            var expireTime = ResolveStackableExpireTime(core.ItemId);
            if (expireTime > 0)
                core.ExpireTime = expireTime;

            if (ItemMetadataResolver.TryLoadStackableFile(core.ItemId, out var stackable)
                && stackable.TradeLimit > 0)
                core.StackTradeCount = (byte)Math.Min(7, stackable.TradeLimit);
        }

        private static void ApplyCreateReason(
            ItemCore core,
            ItemMetadata metadata,
            ItemCreateReason reason)
        {
            if (core == null)
                return;

            if (reason == ItemCreateReason.DungeonDrop)
                ApplyDungeonDropCreateReason(core, metadata);
        }

        private static void ApplyCreateReason(
            InventoryCreateResult result,
            ItemCreateReason reason,
            InventoryCreateOptions options)
        {
            if (result == null || result.Core == null)
                return;

            if (options == null || options.ExpireTime <= 0)
                return;

            if (result.Core.ItemKind == ItemCore.KindAvatar
                && result.AvatarDetail != null)
            {
                result.AvatarDetail.ExpireDate = options.ExpireTime;
            }
            else if (result.Core.ItemKind == ItemCore.KindCreature
                && result.CreatureDetail != null)
            {
                result.CreatureDetail.ExpireDate = options.ExpireTime;
            }
        }

        private static void ApplyExplicitExpireTime(
            ItemCore core,
            InventoryCreateOptions options)
        {
            if (core == null || options == null || options.ExpireTime <= 0)
                return;

            if (core.ItemKind == ItemCore.KindAvatar
                || core.ItemKind == ItemCore.KindCreature)
                return;

            core.ExpireTime = options.ExpireTime;
        }

        private static void ApplyDungeonDropCreateReason(ItemCore core, ItemMetadata metadata)
        {
            if (!CanGenerateDungeonDropAmplifyPollution(core, metadata))
                return;

            var rate = ItemUpgradeTableProvider.GetAmplificationRateByRarity(metadata.Rarity);
            if (rate <= 0 || ServerRandom.Next(10000) >= rate)
                return;

            core.AmplifyType = UnidentifiedAmplifyFlag;
            core.AmplifyValue = 0;
        }

        private static bool CanGenerateDungeonDropAmplifyPollution(ItemCore core, ItemMetadata metadata)
        {
            if (core == null
                || metadata == null
                || core.ItemKind != ItemCore.KindEquipment
                || !string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                || core.AmplifyType != 0
                || core.AmplifyValue != 0)
            {
                return false;
            }

            var equipmentType = EquipmentTypeInfo.ParseOrUnknown(metadata.EquipmentType);
            if (!EquipmentTypeInfo.IsUpgradeTargetType(equipmentType))
                return false;

            if (metadata.Rarity < 2)
                return false;

            if (metadata.MinimumLevel < ItemUpgradeTableProvider.GetAmplifyEquipLevelConst())
                return false;

            return metadata.Durability <= 0 || core.Durability >= metadata.Durability;
        }

        private static ItemMetadata ResolveMetadata(int itemId)
        {
            try
            {
                return ItemMetadataResolver.Resolve(itemId);
            }
            catch
            {
                return null;
            }
        }

        private static int ResolveEquipmentExpireTime(int itemId)
        {
            if (!ItemMetadataResolver.TryLoadEquipmentFile(itemId, out var equipment)
                || !EquipmentExpirationPolicyResolver.TryResolve(equipment, out var policy))
                return 0;

            if (policy.UsablePeriodDays > 0)
                return PvfExpirationMetadata.AddDaysFromNow(policy.UsablePeriodDays);

            return policy.AbsoluteExpirationUnixTime;
        }

        private static int ResolveStackableExpireTime(int itemId)
        {
            if (!ItemMetadataResolver.TryLoadStackableFile(itemId, out var stackable))
                return 0;

            if (!StackableExpirationPolicyResolver.TryResolve(stackable, out var policy))
                return 0;

            if (policy.UsablePeriodDays > 0)
                return PvfExpirationMetadata.AddDaysFromNow(policy.UsablePeriodDays);

            return policy.AbsoluteExpirationUnixTime;
        }

        private static bool TryCreateAndInsertCore(
            InventoryService inventory,
            ItemCore core,
            ItemCreateReason reason,
            int count,
            InventoryCreateOptions options,
            out InventoryCreateResult result)
        {
            result = new InventoryCreateResult { Core = core };
            if (inventory == null || core == null)
                return false;

            if (!InventoryInsertService.TryPlanInsertByDefaultRule(inventory, core, count, out var plan))
            {
                result.InsertPlan = plan;
                return false;
            }

            if (!TryCreateDetails(inventory, core, reason, options, out var createResult))
            {
                createResult.InsertPlan = plan;
                result = createResult;
                return false;
            }

            result = createResult;
            result.InsertPlan = plan;
            if (InventoryInsertService.TryApplyInsertPlan(inventory, core, plan, out var insertResult))
            {
                result.InsertResult = insertResult;
                return true;
            }

            result.InsertResult = insertResult;
            DetachCreatedDetails(inventory, result);
            return false;
        }
    }
}
