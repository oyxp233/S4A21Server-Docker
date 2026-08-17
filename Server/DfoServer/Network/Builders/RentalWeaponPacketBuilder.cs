using System;

namespace DfoServer.Network.Builders
{
    /// A21 RENT_EQUIPMENT_ITEM 回包体。
    public static class RentalWeaponPacketBuilder
    {
        public const uint TooManyActiveItemsResult = 1;
        public const uint InventoryFullResult = 2;

        public static byte[] BuildSuccessAck()
            => BuildResultAck(0);

        public static byte[] BuildResultAck(uint result)
        {
            var body = new byte[5];
            body[0] = 1;
            Buffer.BlockCopy(BitConverter.GetBytes(result), 0, body, 1, 4);
            return body;
        }

        public static byte[] BuildFailureAck()
            => new byte[] { 0 };
    }
}
