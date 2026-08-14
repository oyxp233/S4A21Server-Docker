using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Quests
{
    internal readonly struct QuestCommandOwnerContext
    {
        internal QuestCommandOwnerContext(
            int characterId,
            int accountId,
            Guid sessionId,
            InventoryLease inventoryLease,
            uint? currentExp = null)
        {
            CharacterId = characterId;
            AccountId = accountId;
            SessionId = sessionId;
            InventoryLease = inventoryLease;
            CurrentExp = currentExp;
        }

        internal int CharacterId { get; }
        internal int AccountId { get; }
        internal Guid SessionId { get; }
        internal InventoryLease InventoryLease { get; }
        internal uint? CurrentExp { get; }

        internal bool IsCurrentInventoryOwner()
        {
            return InventoryContext.IsCurrentLease(
                InventoryLease,
                SessionId,
                CharacterId);
        }
    }

    internal readonly struct QuestAcceptCommand
    {
        internal QuestAcceptCommand(ushort questId) => QuestId = questId;
        internal ushort QuestId { get; }
    }

    internal readonly struct QuestGiveupCommand
    {
        internal QuestGiveupCommand(ushort questId) => QuestId = questId;
        internal ushort QuestId { get; }
    }

    internal readonly struct QuestFinishCommand
    {
        internal QuestFinishCommand(
            ushort questId,
            bool hasRewardSelection,
            ushort rewardSelectionIndex,
            ushort completionCount)
        {
            QuestId = questId;
            HasRewardSelection = hasRewardSelection;
            RewardSelectionIndex = rewardSelectionIndex;
            CompletionCount = completionCount;
        }

        internal ushort QuestId { get; }
        internal bool HasRewardSelection { get; }
        internal ushort RewardSelectionIndex { get; }
        internal ushort CompletionCount { get; }
    }
}
