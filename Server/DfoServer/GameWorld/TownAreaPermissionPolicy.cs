using System;
using PvfLib;

namespace DfoServer.GameWorld
{
    // 城镇区域进入门槛(need level / need quest)的判定。数据来自 PVF
    // town/*.twn 的 [permission] 段(area 级), 客户端用同一份数据做本地门禁,
    // 服务端在组队传送队员过滤时复算。exceptional character(外传职业例外
    // 门槛)语义未验证, 暂不参与判定。
    internal static class TownAreaPermissionPolicy
    {
        // 满足门槛返回 null; 不满足返回原因(用于日志)。
        // isQuestCleared 仅在 NeedQuest > 0 时调用。
        internal static string GetDenyReason(
            TownPermission permission,
            int characterLevel,
            Func<int, bool> isQuestCleared)
        {
            if (permission == null)
                return null;
            if (permission.NeedLevel > 0
                && characterLevel < permission.NeedLevel)
            {
                return $"level={characterLevel}<{permission.NeedLevel}";
            }

            if (permission.NeedQuest > 0
                && (isQuestCleared == null
                    || !isQuestCleared(permission.NeedQuest)))
            {
                return $"quest={permission.NeedQuest} not cleared";
            }

            return null;
        }

        internal static bool IsSatisfied(
            TownPermission permission,
            int characterLevel,
            Func<int, bool> isQuestCleared)
            => GetDenyReason(permission, characterLevel, isQuestCleared) == null;
    }
}
