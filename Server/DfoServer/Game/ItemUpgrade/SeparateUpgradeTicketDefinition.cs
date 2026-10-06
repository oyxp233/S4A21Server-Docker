using DfoServer.Game.Inventory;
using PvfLib;
using System;

namespace DfoServer.Game.ItemUpgrade
{
    internal sealed class SeparateUpgradeTicketDefinition
    {
        // 幸运锻造券：[action type] `[lucky enchant deed]` 2。
        // PVF 里这类券不写 [equipment separate reinforcement ticket]。
        // 按原版物品描述：50% 锻造阶段 +1；失败时锻造阶段不发生变化（不掉级），只消耗券。
        // 参数 2 表示锻造（0=强化、1=增幅、2=锻造）。
        internal const string LuckyEnchantDeedActionType = "[lucky enchant deed]";
        internal const int LuckyEnchantDeedRefineParam = 2;
        private const int LuckyRefineTargetLevel = 1;
        // 独立锻造的 roll 取值是 ServerRandom.Next(10000)，所以 5000 = 50%。
        private const int LuckyRefineSuccessWeight = 5000;
        // 原版描述为「失败后锻造阶段不发生变化」，故为 0（不下降）。
        // 若想让失败掉 1 级，把这里改成 1 即可（InventorySeparateUpgradeService 已支持）。
        private const int LuckyRefineFailureDecrement = 0;

        internal int ItemTemplateId { get; private set; }
        internal byte TargetLevel { get; private set; }
        internal int SuccessWeight { get; private set; }
        internal bool IsFixed { get; private set; }
        internal bool IsAdditional { get; private set; }
        internal int ApplyValue { get; private set; }
        internal bool IsLuckyRefineTicket { get; private set; }
        // 失败时下降的锻造阶段数；0 表示失败不掉级（普通券）。
        internal int FailureDecrementLevel { get; private set; }

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
                return TryParseLuckyRefineTicket(itemTemplateId, stackable, out definition);
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
                FailureDecrementLevel = 0,
            };
            return true;
        }

        private static bool TryParseLuckyRefineTicket(
            int itemTemplateId,
            StackableItemFile stackable,
            out SeparateUpgradeTicketDefinition definition)
        {
            definition = null;
            if (stackable == null)
                return false;

            var action = stackable.ActionTypeName;
            if (string.IsNullOrWhiteSpace(action))
                return false;

            if (!string.Equals(
                    action.Trim().Trim('`').Trim(),
                    LuckyEnchantDeedActionType,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (stackable.ActionTypeParams.Count == 0
                || stackable.ActionTypeParams[0] != LuckyEnchantDeedRefineParam)
            {
                return false;
            }

            definition = new SeparateUpgradeTicketDefinition
            {
                ItemTemplateId = itemTemplateId,
                TargetLevel = LuckyRefineTargetLevel,
                SuccessWeight = LuckyRefineSuccessWeight,
                IsFixed = false,
                IsAdditional = true,
                ApplyValue = -1,
                IsLuckyRefineTicket = true,
                FailureDecrementLevel = LuckyRefineFailureDecrement,
            };
            return true;
        }
    }
}
