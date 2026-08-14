using DfoServer.Game.Inventory;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class SelectablePackageAckBuilder
    {
        public static byte[] BuildSuccess(short slotIndex, IReadOnlyList<PackageGrantedItem> grantedItems)
        {
            var count = grantedItems != null ? grantedItems.Count : 0;
            if (count > ushort.MaxValue)
                count = ushort.MaxValue;

            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(slotIndex);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteUInt16((ushort)count);

            for (var i = 0; i < count; i++)
            {
                var item = grantedItems[i];
                writer.WriteInt32(item.ItemTemplateId);
                writer.WriteInt32(item.DisplayCount <= 0 ? 1 : item.DisplayCount);
            }

            return writer.ToArray();
        }

        public static byte[] BuildError()
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteInt32(-1);
            writer.WriteInt32(-1);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            return writer.ToArray();
        }
    }
}
