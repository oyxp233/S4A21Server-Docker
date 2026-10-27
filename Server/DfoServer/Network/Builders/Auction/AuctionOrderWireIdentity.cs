using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Builders.Auction
{
    /// <summary>
    /// Explicit private-server policy: positive persisted OrderId maps one-to-one to opaque8B LE.
    /// Client ordinary paths copy both dwords and compare both for equality (02323023,0232E702..E70D).
    /// This does not infer original native ID subfields and is not an authentication identifier.
    /// </summary>
    public static class AuctionOrderWireIdentity
    {
        public static byte[] Encode(long orderId)
        {
            if (orderId <= 0) throw new ArgumentOutOfRangeException(nameof(orderId), "A positive persisted order ID is required.");
            var identity = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(identity, orderId);
            return identity;
        }

        public static bool TryDecode(ReadOnlySpan<byte> identity, out long orderId)
        {
            orderId = 0;
            if (identity.Length != 8) return false;
            var value = BinaryPrimitives.ReadInt64LittleEndian(identity);
            if (value <= 0) return false;
            orderId = value;
            return true;
        }
    }
}
