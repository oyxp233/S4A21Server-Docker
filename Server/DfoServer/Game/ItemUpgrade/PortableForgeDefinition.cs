using DfoServer.Game.Inventory;
using PvfLib;
using System;

namespace DfoServer.Game.ItemUpgrade
{
    internal static class PortableForgeDefinition
    {
        // 便携式锻造炉：[action type] `[portable genuine damage upgrade upgrede]`（PVF 官方拼写即为 upgrede）。
        // 与 SeparateUpgradeTicketDefinition 一致，按 PVF 动作名识别而不是写死物品 id，天然覆盖同类道具。
        internal const string PortableForgeActionType = "[portable genuine damage upgrade upgrede]";

        internal static bool IsPortableForgeItem(int itemTemplateId)
        {
            return IsPortableForge(StackableItemProvider.Load(itemTemplateId));
        }

        internal static bool IsPortableForge(StackableItemFile stackable)
        {
            if (stackable == null)
                return false;

            var action = stackable.ActionTypeName;
            if (string.IsNullOrWhiteSpace(action))
                return false;

            return string.Equals(
                action.Trim().Trim('`').Trim(),
                PortableForgeActionType,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
