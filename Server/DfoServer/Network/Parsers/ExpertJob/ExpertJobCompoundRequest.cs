using System;
using DfoServer.Game.ExpertJob;

namespace DfoServer.Network.Parsers.ExpertJob
{
    internal static class ExpertJobCompoundRequest
    {
        internal static bool TryParse(byte[] body, out ExpertJobCompoundCommand command)
        {
            command = null;
            if (body == null || body.Length != 8)
                return false;

            var recipeItemId = BitConverter.ToInt32(body, 0);
            var requestedCount = BitConverter.ToUInt16(body, 4);
            var cardSlotIndex = BitConverter.ToInt16(body, 6);
            if (recipeItemId <= 0 || requestedCount == 0 || cardSlotIndex < -1)
                return false;

            command = new ExpertJobCompoundCommand
            {
                RecipeItemId = recipeItemId,
                RequestedCount = requestedCount,
                CardSlotIndex = cardSlotIndex,
            };
            return true;
        }
    }
}
