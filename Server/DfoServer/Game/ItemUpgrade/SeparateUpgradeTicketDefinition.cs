using DfoServer.Game.Inventory;
using PvfLib;
using System;

namespace DfoServer.Game.ItemUpgrade
{
    internal sealed class SeparateUpgradeTicketDefinition
    {
        internal int ItemTemplateId { get; private set; }
        internal byte TargetLevel { get; private set; }
        internal int SuccessWeight { get; private set; }
        internal bool IsFixed { get; private set; }
        internal bool IsAdditional { get; private set; }
        internal int ApplyValue { get; private set; }

        internal static bool TryLoad(int itemTemplateId, out SeparateUpgradeTicketDefinition definition)
        {
            definition = null;
            var stackable = StackableItemProvider.Load(itemTemplateId);
            return TryParse(itemTemplateId, stackable, out definition);
        }

        internal static bool TryParse(
            int itemTemplateId,
            StackableItemFile stackable,
            out SeparateUpgradeTicketDefinition definition)
        {
            definition = null;
            var source = stackable?.EquipmentSeparateReinforcementTicket;
            if (source == null)
                return false;
            var isFixed = string.Equals(source.ApplyMode, "fixed", StringComparison.OrdinalIgnoreCase);
            var isAdditional = string.Equals(source.ApplyMode, "additional", StringComparison.OrdinalIgnoreCase);
            if (source.TargetLevel <= 0 || source.TargetLevel > byte.MaxValue
                || source.SuccessRatePercent < 0 || source.SuccessRatePercent > 100
                || (!isFixed && !isAdditional))
            {
                return false;
            }

            definition = new SeparateUpgradeTicketDefinition
            {
                ItemTemplateId = itemTemplateId,
                TargetLevel = checked((byte)source.TargetLevel),
                SuccessWeight = source.SuccessRatePercent * 100,
                IsFixed = isFixed,
                IsAdditional = isAdditional,
                ApplyValue = source.ApplyValue,
            };
            return true;
        }
    }
}
