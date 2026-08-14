namespace DfoServer.Game.Inventory
{
    internal static class InventoryListRule
    {
        internal static bool IsSupportedDeleteOrSellListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet;
        }
    }
}
