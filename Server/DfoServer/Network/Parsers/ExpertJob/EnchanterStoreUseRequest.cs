using System;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Parsers.ExpertJob
{
    internal static class EnchanterStoreUseRequest
    {
        internal static bool TryParse(byte[] body, out EnchanterStoreUseCommand command)
        {
            command = null;
            if (body == null || body.Length != 13)
                return false;

            var ownerUserId = BitConverter.ToUInt16(body, 0);
            var recipeItemId = BitConverter.ToInt32(body, 2);
            var mode = body[6];
            var targetListType = (InventoryListType)body[7];
            var targetSlotIndex = BitConverter.ToInt16(body, 8);
            var cardListType = (InventoryListType)body[10];
            var cardSlotIndex = BitConverter.ToInt16(body, 11);
            if (ownerUserId == 0
                || recipeItemId <= 0
                || mode != 2
                || targetListType != InventoryListType.Main
                || cardListType != InventoryListType.Main
                || targetSlotIndex < 0
                || cardSlotIndex < 0
                || targetSlotIndex == cardSlotIndex)
            {
                return false;
            }

            command = new EnchanterStoreUseCommand
            {
                OwnerUserId = ownerUserId,
                RecipeItemId = recipeItemId,
                Mode = mode,
                TargetListType = targetListType,
                TargetSlotIndex = targetSlotIndex,
                CardListType = cardListType,
                CardSlotIndex = cardSlotIndex,
            };
            return true;
        }
    }
}
