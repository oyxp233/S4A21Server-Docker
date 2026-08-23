using DfoServer.Game.Inventory;
using System.Collections.Generic;

namespace DfoServer.Game.Characters
{
    internal sealed class ClassChangeItemRequest
    {
        public short ItemSlotIndex { get; set; }

        public byte TargetGrowType { get; set; }
    }

    internal enum ClassChangeItemMode
    {
        Unknown = 0,
        Beginner = 1,
        Advanced = 2,
    }

    internal enum ClassChangeItemStatus
    {
        Success,
        InvalidRequest,
        SourceMissing,
        SourceChanged,
        SourceEmpty,
        SourceExpired,
        InvalidItem,
        InvalidLifecycle,
        CooltimeActive,
        UsableCountLimitExceeded,
        InvalidState,
        LevelRejected,
        TargetUnchanged,
        MutationFailed,
        PersistenceFailed,
    }

    internal sealed class ClassChangeItemResult
    {
        public ClassChangeItemRequest Request { get; set; }

        public ClassChangeItemStatus Status { get; set; } =
            ClassChangeItemStatus.InvalidRequest;

        public string Detail { get; set; }

        public int ItemTemplateId { get; set; }

        public ClassChangeItemMode Mode { get; set; }

        public int PreviousGrowType { get; set; }

        public int NewGrowType { get; set; }

        public int RemovedQuestCount { get; set; }

        public int MarkedAwakeningQuestCount { get; set; }

        public InventoryMutationResult SourceMutation { get; set; }

        public UsableCountLimitState UsableCountState { get; set; }

        public List<short> MainRefreshSlots { get; } = new List<short>();

        public bool Success => Status == ClassChangeItemStatus.Success;

        public bool SourceExpiredDeleted =>
            Status == ClassChangeItemStatus.SourceExpired
            && SourceMutation != null;
    }
}
