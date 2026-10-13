namespace DfoServer.Game.Inventory
{
    internal static class AvatarInventoryExpansionRule
    {
        internal const int FirstItemTemplateId = 0x0028F3FD;
        internal const int LastItemTemplateId = 0x0028F40B;
        internal const int Param16PerStage = 7;
        internal const int MaxStage = 15;
        internal const int MaxParam16 = Param16PerStage * MaxStage;

        internal static bool TryResolveStage(int itemTemplateId, out int stage, out ushort targetParam16)
        {
            stage = 0;
            targetParam16 = 0;
            if (itemTemplateId < FirstItemTemplateId || itemTemplateId > LastItemTemplateId)
                return false;

            stage = itemTemplateId - FirstItemTemplateId + 1;
            targetParam16 = (ushort)(stage * Param16PerStage);
            return true;
        }

        internal static int NormalizeParam16(int value)
        {
            if (value <= 0)
                return 0;
            if (value > MaxParam16)
                return MaxParam16;
            return value;
        }
    }
}
