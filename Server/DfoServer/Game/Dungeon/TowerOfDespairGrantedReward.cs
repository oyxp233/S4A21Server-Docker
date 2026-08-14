using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct TowerOfDespairGrantedReward
    {
        internal TowerOfDespairGrantedReward(
            ClearRewardGenerator.CardReward reward,
            InventoryListType listType,
            short slot)
        {
            Reward = reward;
            ListType = listType;
            Slot = slot;
        }

        internal ClearRewardGenerator.CardReward Reward { get; }
        internal InventoryListType ListType { get; }
        internal short Slot { get; }
    }
}
