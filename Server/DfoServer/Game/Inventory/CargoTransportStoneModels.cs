using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class CargoTransportStoneRequest
    {
        public short StoneSlotIndex { get; set; }

        public short TargetSlotIndex { get; set; }

        public bool IsCreatureTransportStone { get; set; }

        public int TargetCharacterSlotIndex { get; set; }
    }

    internal enum CargoTransportStoneStatus
    {
        Success = 0,
        InvalidRequest = 1,
        SourceMissing = 2,
        SourceChanged = 3,
        SourceEmpty = 4,
        SourceExpired = 5,
        InvalidStone = 6,
        InvalidLifecycle = 7,
        CooltimeActive = 8,
        TargetCharacterMissing = 9,
        TargetMissing = 10,
        TargetInvalidKind = 11,
        TargetNotAllowed = 12,
        TargetLocked = 13,
        AccountCargoFull = 14,
        UsableCountLimitExceeded = 15,
        MutationFailed = 16,
        MailFailed = 17,
    }

    internal sealed class CargoTransportStoneResult
    {
        public CargoTransportStoneStatus Status { get; set; } =
            CargoTransportStoneStatus.InvalidRequest;

        public string Detail { get; set; }

        public CargoTransportStoneRequest Request { get; set; }

        public int StoneItemTemplateId { get; set; }

        public int TargetItemTemplateId { get; set; }

        public int StoneType { get; set; } = -1;

        public short AccountCargoSlotIndex { get; set; } = -1;

        public int TargetCharacterId { get; set; }

        public int AckRemainingStoneCount { get; set; }

        public int AckParameter { get; set; }

        public byte AckMode { get; set; }

        public bool SourceExpiredDeleted { get; set; }

        public InventoryMutationResult StoneMutation { get; set; }

        public InventoryMutationResult TargetMutation { get; set; }

        public UsableCountLimitState UsableCountState { get; set; }

        public List<short> MainRefreshSlots { get; } = new List<short>();

        public List<short> AccountCargoRefreshSlots { get; } = new List<short>();

        public List<short> PetRefreshSlots { get; } = new List<short>();

        public bool CreatureListChanged { get; set; }

        public bool Success => Status == CargoTransportStoneStatus.Success;
    }

    internal sealed class CargoTransportStoneGradeEntry
    {
        public int LevelStart { get; set; }

        public int Value { get; set; }
    }
}
