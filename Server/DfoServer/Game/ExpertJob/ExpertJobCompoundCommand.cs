namespace DfoServer.Game.ExpertJob
{
    internal sealed class ExpertJobCompoundCommand
    {
        internal int RecipeItemId { get; set; }
        internal ushort RequestedCount { get; set; }
        internal short CardSlotIndex { get; set; }
        internal bool IsCardCraft => CardSlotIndex >= 0;
        internal bool IsProductCraft => CardSlotIndex == -1;
    }
}
