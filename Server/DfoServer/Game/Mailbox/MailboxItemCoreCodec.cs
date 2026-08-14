using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Game.Mailbox
{
    internal static class MailboxItemCoreCodec
    {
        internal static ItemCore Decode(MailboxAttachmentEntry attachment)
        {
            if (attachment == null)
                return null;

            return Decode(
                attachment.ItemCoreData,
                attachment.ItemTemplateId,
                attachment.ItemKind,
                attachment.ItemCount,
                attachment.InstanceValue,
                attachment.Durability,
                attachment.SealFlag,
                attachment.OptionValue,
                attachment.ExpireTime,
                attachment.Marker16,
                attachment.PetSerialOrHandle);
        }

        internal static ItemCore Decode(MailboxAttachmentSnapshot attachment)
        {
            if (attachment == null)
                return null;

            return Decode(
                attachment.ItemCoreData,
                attachment.ItemTemplateId,
                attachment.ItemKind,
                attachment.ItemCount,
                attachment.InstanceValue,
                attachment.Durability,
                attachment.SealFlag,
                attachment.OptionValue,
                attachment.ExpireTime,
                attachment.Marker16,
                attachment.PetSerialOrHandle);
        }

        internal static ItemCore Decode(
            byte[] itemCoreData,
            int itemId,
            string itemKind,
            int itemCount,
            int instanceValue,
            int durability,
            int sealFlag,
            int optionValue,
            int expireTime,
            int marker16,
            int petSerialOrHandle)
        {
            if (itemCoreData != null && itemCoreData.Length >= ItemCore.Size)
            {
                var core = ItemCore.FromBytes(itemCoreData);
                if (core != null && core.ItemId > 0)
                {
                    RestoreLegacyExpireTime(core, itemId, expireTime);
                    return core;
                }
            }

            if (itemId <= 0)
                return null;

            if (!ItemMetadataResolver.TryResolveItemKind(itemId, out var resolvedKind))
                resolvedKind = ResolveLegacyItemKind(itemKind);

            var legacy = new ItemCore
            {
                ItemKind = resolvedKind,
                ItemId = itemId,
                Value = instanceValue,
                Durability = ClampUInt16(durability),
                SealFlag = ClampByte(sealFlag),
                ExpireTime = expireTime,
                Marker16 = marker16,
            };

            if (legacy.ItemKind == ItemCore.KindCreature && petSerialOrHandle > 0)
                legacy.CreatureUid = petSerialOrHandle;
            if (legacy.ItemKind == ItemCore.KindAvatar)
                legacy.AbilityNo = ClampUInt16(optionValue);
            if (InventoryStackRuleService.IsStackable(legacy))
                legacy.Count = Math.Max(1, itemCount);

            return legacy;
        }

        internal static byte[] Encode(ItemCore core)
        {
            return core?.ToBytes() ?? Array.Empty<byte>();
        }

        internal static string GetLegacyKindName(ItemCore core)
        {
            if (core == null)
                return "unknown";

            switch (core.ItemKind)
            {
                case ItemCore.KindEquipment:
                case ItemCore.KindCreatureEquipment:
                    return "equipment";
                case ItemCore.KindAvatar:
                    return "avatar";
                case ItemCore.KindCreature:
                    return "creature";
                default:
                    return "stackable";
            }
        }

        private static byte ResolveLegacyItemKind(string itemKind)
        {
            switch ((itemKind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "equipment":
                    return ItemCore.KindEquipment;
                case "avatar":
                    return ItemCore.KindAvatar;
                case "creature":
                case "pet":
                    return ItemCore.KindCreature;
                default:
                    return ItemCore.KindConsumable;
            }
        }

        private static void RestoreLegacyExpireTime(ItemCore core, int itemId, int expireTime)
        {
            if (core == null || expireTime <= 0 || core.ExpireTime > 0)
                return;

            if (itemId > 0 && core.ItemId != itemId)
                return;

            core.ExpireTime = expireTime;
        }

        private static byte ClampByte(int value)
        {
            return (byte)Math.Max(byte.MinValue, Math.Min(byte.MaxValue, value));
        }

        private static ushort ClampUInt16(int value)
        {
            return (ushort)Math.Max(ushort.MinValue, Math.Min(ushort.MaxValue, value));
        }
    }
}
