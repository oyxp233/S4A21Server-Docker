using DfoServer.Game.Inventory;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class SortItemAckBuilder
    {
        public static byte[] Build(InventoryListType listType)
        {
            return new[] { (byte)0x01, (byte)listType };
        }
    }
}