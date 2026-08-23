namespace DfoServer.Game.Characters
{
    internal sealed class GrowupChangeRequest
    {
        public byte TargetGrowType { get; set; }
    }

    internal enum GrowupChangeStatus
    {
        Success = 0,
        InvalidRequest = 1,
        InvalidState = 2,
        InsufficientGold = 3,
        ConfigUnavailable = 4,
        PersistenceFailed = 5,
    }

    internal sealed class GrowupChangeResult
    {
        public const int ResultCodeSuccess = 0;
        public const int ResultCodeInvalidState = 19;
        public const int ResultCodeInsufficientGold = 22;

        public GrowupChangeStatus Status { get; set; } =
            GrowupChangeStatus.InvalidRequest;

        public string Detail { get; set; }

        public int ResultCode { get; set; } = ResultCodeInvalidState;

        public byte TargetGrowType { get; set; }

        public byte NewGrowType { get; set; }

        public int PreviousChangeCount { get; set; }

        public int NewChangeCount { get; set; }

        public int GoldCost { get; set; }

        public int UpdatedGold { get; set; }

        public int RemovedQuestCount { get; set; }

        public bool Success => Status == GrowupChangeStatus.Success;

        public bool GoldChanged => Success && GoldCost > 0;

        public byte AckChangeCount =>
            (byte)(NewChangeCount < 0
                ? 0
                : NewChangeCount > byte.MaxValue
                    ? byte.MaxValue
                    : NewChangeCount);
    }
}
