using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class ExpertJobGiveupResult
    {
        internal const byte ErrorPersistence = 1;
        internal const byte ErrorInvalidState = 8;
        internal const byte ErrorInsufficientGold = 21;

        internal byte ErrorCode { get; set; }

        internal int CurrentGold { get; set; }

        internal byte GiveupCount { get; set; }

        internal InventoryMutationSet InventoryChanges { get; } =
            new InventoryMutationSet();

        internal bool Success => ErrorCode == 0;

        internal static ExpertJobGiveupResult Fail(byte errorCode)
            => new ExpertJobGiveupResult { ErrorCode = errorCode };
    }
}
