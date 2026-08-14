using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryItemExpirationService
    {
        internal static List<InventoryItem> FilterActive(
            IEnumerable<InventoryItem> items,
            AvatarDetailManager avatarDetails,
            long nowUnixTime)
        {
            var result = new List<InventoryItem>();
            if (items == null)
                return result;

            foreach (var item in items)
            {
                var avatarDetail = avatarDetails?.GetDetail(item.ItemUid);
                if (!IsExpired(item, avatarDetail, nowUnixTime))
                    result.Add(item);
            }

            return result;
        }

        internal static bool IsExpired(InventoryItem item, AvatarDetail avatarDetail, long nowUnixTime)
        {
            return IsExpired(item?.Core, avatarDetail, nowUnixTime);
        }

        internal static bool IsExpired(ItemCore core, AvatarDetail avatarDetail, long nowUnixTime)
        {
            if (core == null)
                return true;

            var expireDate = core.ItemKind == ItemCore.KindAvatar && avatarDetail != null
                ? avatarDetail.ExpireDate
                : core.ExpireTime;

            return expireDate > 0 && expireDate <= nowUnixTime;
        }
    }
}
