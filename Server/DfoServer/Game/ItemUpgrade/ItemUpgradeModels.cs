using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using PvfLib;

namespace DfoServer.Game.ItemUpgrade
{
    public enum ItemUpgradeMode
    {
        Reinforce = 0,
        Amplify = 1,
    }

    public enum ItemUpgradeMethod
    {
        Reinforce = 0,
        Amplify = 1,
        AdvancedReinforce = 2,
    }

    public enum ItemUpgradeScene
    {
        Npc = 0,
        Ticket = 1,
        Portable = 2,
    }

    public enum ItemUpgradeConsumableKind
    {
        None = 0,
        ReinforcementTicket = 1,
        AmplifyTicket = 2,
        RandomEnchantTicket = 3,
        PortableReinforcement = 4,
        PortableAmplify = 5,
        ProtectReinforcement = 6,
        ProtectAmplify = 7,
    }

    public enum ItemUpgradeTableKind
    {
        Normal = 0,
        Amplify = 1,
        Advanced = 2,
    }

    public sealed class ItemUpgradeChanceEntry
    {
        public int TargetLevel { get; set; } = -1;
        public int BaseFailureWeight { get; set; } = -1;
        public int BaseSuccessWeight { get; set; } = -1;
    }

    public sealed class ItemUpgradeRestriction
    {
        public int SlotRestriction { get; set; }
        public List<int> RarityRestrictions { get; set; } = new List<int>();
        public int SealRestriction { get; set; }
        public int ItemLevelMin { get; set; } = -1;
        public int ItemLevelMax { get; set; } = -1;

        public bool AllowsRarity(int rarity)
        {
            return RarityRestrictions == null || RarityRestrictions.Count == 0 || RarityRestrictions.Contains(rarity);
        }

        public bool AllowsEquipmentType(EquipmentType equipmentType)
        {
            return EquipmentTypeInfo.MatchesSlotRestriction(equipmentType, SlotRestriction);
        }

        public bool AllowsItemLevel(int level)
        {
            if (ItemLevelMin >= 0 && level < ItemLevelMin)
                return false;
            if (ItemLevelMax >= 0 && level > ItemLevelMax)
                return false;
            return true;
        }
    }

    public sealed class ItemUpgradeCost
    {
        public int MaterialItemId { get; set; }
        public int MaterialCount { get; set; }
        public int Gold { get; set; }
    }

    public sealed class ItemUpgradeConsumableConfig
    {
        public int ItemTemplateId { get; set; }
        public ItemUpgradeConsumableKind Kind { get; set; }
        public ItemUpgradeMode Mode { get; set; }
        public ItemUpgradeScene Scene { get; set; }
        public string ActionTypeName { get; set; }
        public List<int> ActionTypeParams { get; set; } = new List<int>();
        public ItemUpgradeRestriction Restriction { get; set; } = new ItemUpgradeRestriction();
        public List<ItemUpgradeChanceEntry> ChanceEntries { get; set; } = new List<ItemUpgradeChanceEntry>();
        public int SuccessRateAddWeight { get; set; }
        public int SuccessRateBonusWeight { get; set; }
        public ItemUpgradeCost Cost { get; set; } = new ItemUpgradeCost();
        public int FailureRetainLevel { get; set; } = -1;
        public int ProtectTriggerLevel => FailureRetainLevel >= 0 ? FailureRetainLevel + 1 : -1;
    }

    public sealed class ItemUpgradeContext
    {
        public ItemUpgradeMode Mode { get; set; }
        public ItemUpgradeScene Scene { get; set; }
        public ItemUpgradeConsumableKind ConsumableKind { get; set; } = ItemUpgradeConsumableKind.None;
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public int CurrentUpgradeLevel { get; set; }
        public EquipmentType EquipmentType { get; set; } = EquipmentType.Unknown;
        public int EquipmentLevel { get; set; } = -1;
        public int EquipmentGrade { get; set; } = -1;
        public int EquipmentRarity { get; set; } = -1;
        public ItemUpgradeRestriction Restriction { get; set; } = new ItemUpgradeRestriction();
        public List<ItemUpgradeChanceEntry> ChanceEntries { get; set; } = new List<ItemUpgradeChanceEntry>();
        public ItemUpgradeCost Cost { get; set; } = new ItemUpgradeCost();
        public int SuccessRateAddWeight { get; set; }
        public int SuccessRateBonusWeight { get; set; }
        public int EquippedUpgradeProbabilityIncrease { get; set; }
        public int FailureRetainLevel { get; set; } = -1;
        public int ProtectTriggerLevel => FailureRetainLevel >= 0 ? FailureRetainLevel + 1 : -1;
    }

    public static class ItemUpgradeConsumableResolver
    {
        public static bool TryResolve(int itemTemplateId, StackableItemFile stackable, out ItemUpgradeConsumableConfig config)
        {
            config = null;
            if (stackable == null)
                return false;

            if (stackable.EquipmentReinforcementTicket != null)
            {
                config = FromTicket(
                    itemTemplateId,
                    ItemUpgradeConsumableKind.ReinforcementTicket,
                    ItemUpgradeMode.Reinforce,
                    stackable.EquipmentReinforcementTicket);
                return true;
            }

            if (stackable.EquipmentAmplifyReinforcementTicket != null)
            {
                config = FromTicket(
                    itemTemplateId,
                    ItemUpgradeConsumableKind.AmplifyTicket,
                    ItemUpgradeMode.Amplify,
                    stackable.EquipmentAmplifyReinforcementTicket);
                return true;
            }

            if (stackable.EnchantRandomUpgrade != null)
            {
                config = FromEnchantRandom(itemTemplateId, stackable);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(stackable.ActionTypeName))
            {
                config = FromActionType(itemTemplateId, stackable);
                return config != null;
            }

            return false;
        }

        private static ItemUpgradeConsumableConfig FromTicket(
            int itemTemplateId,
            ItemUpgradeConsumableKind kind,
            ItemUpgradeMode mode,
            EquipmentUpgradeTicketInfo ticket)
        {
            return new ItemUpgradeConsumableConfig
            {
                ItemTemplateId = itemTemplateId,
                Kind = kind,
                Mode = mode,
                Scene = ItemUpgradeScene.Ticket,
                Cost = new ItemUpgradeCost { MaterialItemId = itemTemplateId, MaterialCount = 1, Gold = 0 },
                ChanceEntries =
                {
                    new ItemUpgradeChanceEntry
                    {
                        TargetLevel = ticket.TargetLevel,
                        BaseSuccessWeight = ticket.SuccessWeight,
                    }
                },
            };
        }

        private static ItemUpgradeConsumableConfig FromEnchantRandom(int itemTemplateId, StackableItemFile stackable)
        {
            var info = stackable.EnchantRandomUpgrade;
            var config = new ItemUpgradeConsumableConfig
            {
                ItemTemplateId = itemTemplateId,
                Kind = ItemUpgradeConsumableKind.RandomEnchantTicket,
                Mode = ResolveModeFromActionType(stackable.ActionTypeName, ItemUpgradeMode.Reinforce),
                Scene = ItemUpgradeScene.Ticket,
                ActionTypeName = stackable.ActionTypeName,
                ActionTypeParams = new List<int>(stackable.ActionTypeParams),
                Restriction = new ItemUpgradeRestriction
                {
                    SlotRestriction = Math.Max(0, info.SlotRestriction),
                    RarityRestrictions = new List<int>(info.RarityRestrictions),
                    SealRestriction = Math.Max(0, info.SealRestriction),
                },
                Cost = new ItemUpgradeCost { MaterialItemId = itemTemplateId, MaterialCount = 1, Gold = 0 },
            };

            foreach (var entry in info.EnchantEntries)
            {
                config.ChanceEntries.Add(new ItemUpgradeChanceEntry
                {
                    TargetLevel = entry.TargetLevel,
                    BaseSuccessWeight = entry.SuccessWeight,
                });
            }

            return config;
        }

        private static ItemUpgradeConsumableConfig FromActionType(int itemTemplateId, StackableItemFile stackable)
        {
            var action = NormalizeActionName(stackable.ActionTypeName);
            if (string.IsNullOrWhiteSpace(action))
                return null;

            if (action == "[new portable upgrade]" || action == "[portable upgrade]")
                return FromPortable(itemTemplateId, stackable, ItemUpgradeConsumableKind.PortableReinforcement, ItemUpgradeMode.Reinforce);

            if (action == "[new portable amplify]" || action == "[portable amplify]")
                return FromPortable(itemTemplateId, stackable, ItemUpgradeConsumableKind.PortableAmplify, ItemUpgradeMode.Amplify);

            if (action == "[new protect equipment]" || action == "[protect equipment]")
                return FromProtect(itemTemplateId, stackable, ItemUpgradeConsumableKind.ProtectReinforcement, ItemUpgradeMode.Reinforce);

            if (action == "[new amplify protect equipment]" || action == "[amplify protect equipment]")
                return FromProtect(itemTemplateId, stackable, ItemUpgradeConsumableKind.ProtectAmplify, ItemUpgradeMode.Amplify);

            return null;
        }

        private static ItemUpgradeConsumableConfig FromPortable(
            int itemTemplateId,
            StackableItemFile stackable,
            ItemUpgradeConsumableKind kind,
            ItemUpgradeMode mode)
        {
            var config = new ItemUpgradeConsumableConfig
            {
                ItemTemplateId = itemTemplateId,
                Kind = kind,
                Mode = mode,
                Scene = ItemUpgradeScene.Portable,
                ActionTypeName = stackable.ActionTypeName,
                ActionTypeParams = new List<int>(stackable.ActionTypeParams),
                Restriction = new ItemUpgradeRestriction
                {
                    SlotRestriction = 0,
                    ItemLevelMin = stackable.CheckUsableItemLevelMin,
                    ItemLevelMax = stackable.CheckUsableItemLevelMax,
                },
                Cost = new ItemUpgradeCost { MaterialItemId = itemTemplateId, MaterialCount = 1, Gold = 0 },
            };

            if (stackable.ActionTypeParams.Count > 2)
                // 86 [action type] 第3参数按十万分比额外加成保存，30000表示在基础成功率上额外加30%。
                config.SuccessRateBonusWeight = stackable.ActionTypeParams[2];

            return config;
        }

        private static ItemUpgradeConsumableConfig FromProtect(
            int itemTemplateId,
            StackableItemFile stackable,
            ItemUpgradeConsumableKind kind,
            ItemUpgradeMode mode)
        {
            var config = new ItemUpgradeConsumableConfig
            {
                ItemTemplateId = itemTemplateId,
                Kind = kind,
                Mode = mode,
                Scene = ItemUpgradeScene.Ticket,
                ActionTypeName = stackable.ActionTypeName,
                ActionTypeParams = new List<int>(stackable.ActionTypeParams),
                Cost = new ItemUpgradeCost { MaterialItemId = itemTemplateId, MaterialCount = 1, Gold = 0 },
            };

            config.FailureRetainLevel = stackable.ActionTypeParams.Count > 0 ? stackable.ActionTypeParams[0] : 0;

            return config;
        }

        private static ItemUpgradeMode ResolveModeFromActionType(string actionTypeName, ItemUpgradeMode fallback)
        {
            var action = NormalizeActionName(actionTypeName);
            return action != null && action.Contains("amplify")
                ? ItemUpgradeMode.Amplify
                : fallback;
        }

        private static string NormalizeActionName(string actionTypeName)
        {
            return string.IsNullOrWhiteSpace(actionTypeName)
                ? null
                : actionTypeName.Trim().Trim('`').Trim().ToLowerInvariant();
        }
    }
}
