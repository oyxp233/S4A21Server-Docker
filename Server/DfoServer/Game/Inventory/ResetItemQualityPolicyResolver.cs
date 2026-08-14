using DfoServer.Game.ItemUpgrade;
using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class ResetItemQualityPolicy
    {
        private readonly HashSet<EquipmentType> _allowedEquipmentTypes;

        internal ResetItemQualityPolicy(ResetItemQualityMode mode, bool hasExplicitEquipmentTypes, HashSet<EquipmentType> allowedEquipmentTypes)
        {
            Mode = mode;
            HasExplicitEquipmentTypes = hasExplicitEquipmentTypes;
            _allowedEquipmentTypes = allowedEquipmentTypes ?? new HashSet<EquipmentType>();
        }

        internal ResetItemQualityMode Mode { get; }
        internal bool HasExplicitEquipmentTypes { get; }

        internal bool Allows(EquipmentType equipmentType)
        {
            return IsResettableEquipmentType(equipmentType)
                && (!HasExplicitEquipmentTypes || _allowedEquipmentTypes.Contains(equipmentType));
        }

        internal static bool IsResettableEquipmentType(EquipmentType equipmentType)
        {
            return EquipmentTypeInfo.IsUpgradeTargetType(equipmentType)
                || equipmentType == EquipmentType.TitleName;
        }
    }

    internal static class ResetItemQualityPolicyResolver
    {
        internal const int StandardKaleidoBoxItemId = 15;
        internal const int LiberatedKaleidoBoxItemId = 897;

        internal static bool TryResolve(int itemTemplateId, StackableItemFile stackable, out ResetItemQualityPolicy policy)
        {
            policy = null;
            if (stackable == null)
                return false;

            var stackableType = NormalizeToken(stackable.StackableType);
            var isGold = stackableType.IndexOf("gold kaleido", StringComparison.OrdinalIgnoreCase) >= 0;
            var isKaleido = isGold
                || stackableType.IndexOf("kaleido", StringComparison.OrdinalIgnoreCase) >= 0
                || itemTemplateId == StandardKaleidoBoxItemId
                || itemTemplateId == LiberatedKaleidoBoxItemId;
            if (!isKaleido)
                return false;

            var allowedTypes = new HashSet<EquipmentType>();
            var hasExplicitTypes = stackable.UsableEquipTypes != null && stackable.UsableEquipTypes.Count > 0;
            if (hasExplicitTypes)
            {
                foreach (var rawType in stackable.UsableEquipTypes)
                {
                    if (EquipmentTypeInfo.TryParse(rawType, out var equipmentType)
                        && ResetItemQualityPolicy.IsResettableEquipmentType(equipmentType))
                    {
                        allowedTypes.Add(equipmentType);
                    }
                }

                if (allowedTypes.Count == 0)
                    return false;
            }

            policy = new ResetItemQualityPolicy(
                isGold ? ResetItemQualityMode.Highest : ResetItemQualityMode.Random,
                hasExplicitTypes,
                allowedTypes);
            return true;
        }

        private static string NormalizeToken(string value)
        {
            return (value ?? string.Empty).Trim().Trim('`').Trim('[', ']').Trim();
        }
    }
}
