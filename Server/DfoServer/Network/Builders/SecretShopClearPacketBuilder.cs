using DfoServer.Game.SecretShop;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    /// <summary>
    /// Builds the clear-time secret-shop notifications in the order consumed by
    /// the client: 0x0117 stores the NPC template, then 0x0118 creates/updates
    /// that NPC and attaches its product rows. 0x0118 must arrive before the
    /// player opens the shop UI; sending it in response to 0x0129 is too late.
    /// </summary>
    internal static class SecretShopClearPacketBuilder
    {
        internal static IReadOnlyList<byte[]> Build(SecretShopOffer offer)
        {
            if (offer == null)
                throw new ArgumentNullException(nameof(offer));

            var packets = new List<byte[]>(offer.IsSecretShop ? 2 : 1)
            {
                GamePacketEnvelopeBuilder.Build(0x00, 0x0117, BitConverter.GetBytes(offer.NpcId)),
            };

            if (offer.IsSecretShop)
            {
                packets.Add(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0118,
                    SecretShopItemListBodyBuilder.Build(offer)));
            }

            return packets;
        }
    }
}
