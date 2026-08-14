using DfoServer.Game.SecretShop;
using System;

namespace DfoServer.Network.Builders
{
    internal static class SecretShopBuyAckBuilder
    {
        internal static byte[] BuildSuccess(SecretShopPurchaseResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(result.UpdatedGold);
            writer.WriteUInt16(unchecked((ushort)result.AssignedSlot));
            writer.WriteInt32(result.ItemId);
            writer.WriteInt32(result.ItemValue);
            writer.WriteByte(result.ExtData0);
            writer.WriteUInt16(result.Durability);
            writer.WriteInt32(result.RequiredItemId > 0 ? result.RequiredItemId : -1);
            writer.WriteInt32(result.RequiredItemId > 0 ? result.CostItemRemainingCount : 0);
            writer.WriteInt32(result.OfferRemainingCount);
            return writer.ToArray();
        }

        internal static byte[] BuildFailure(byte errorCode = 0x04)
            => new byte[] { 0, errorCode };
    }
}
