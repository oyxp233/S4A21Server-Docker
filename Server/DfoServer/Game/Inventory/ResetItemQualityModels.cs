namespace DfoServer.Game.Inventory
{
    public enum ResetItemQualityMode : byte
    {
        Random = 0,
        Highest = 1,
    }

    public sealed class ResetItemQualityRequest
    {
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public short MaterialSlotIndex { get; set; }
    }

    public sealed class ResetItemQualityResult
    {
        public const byte ErrorInvalidRequest = 0x01;
        public const byte ErrorInvalidTarget = 0x02;
        public const byte ErrorInvalidMaterial = 0x03;
        public const byte ErrorUnsupported = 0x04;
        public const byte ErrorLocked = 0x05;

        public ResetItemQualityRequest Request { get; set; }
        public byte ErrorCode { get; set; }
        public ResetItemQualityMode Mode { get; set; }
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public short MaterialSlotIndex { get; set; }
        public int MaterialItemTemplateId { get; set; }
        public int MaterialRemainingCount { get; set; }
        public int OldQualitySeed { get; set; }
        public int NewQualitySeed { get; set; }
    }
}
