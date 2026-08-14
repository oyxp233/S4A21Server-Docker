namespace DfoServer.Game.Dungeon
{
    // Immutable command/admission rules derived from the frozen dungeon policy.
    internal sealed class DungeonInteractionPolicy
    {
        private static DungeonInteractionPolicy Standard { get; } =
            new DungeonInteractionPolicy(
                allowsPartyEntry: true,
                allowsItemDiscard: true,
                consumesStackableItems: true);

        private static DungeonInteractionPolicy InteractiveTraining { get; } =
            new DungeonInteractionPolicy(
                allowsPartyEntry: false,
                allowsItemDiscard: false,
                consumesStackableItems: false);

        private DungeonInteractionPolicy(
            bool allowsPartyEntry,
            bool allowsItemDiscard,
            bool consumesStackableItems)
        {
            AllowsPartyEntry = allowsPartyEntry;
            AllowsItemDiscard = allowsItemDiscard;
            ConsumesStackableItems = consumesStackableItems;
        }

        internal bool AllowsPartyEntry { get; }

        internal bool AllowsItemDiscard { get; }

        internal bool ConsumesStackableItems { get; }

        internal bool AllowsPartyState(bool isInParty)
            => !isInParty || AllowsPartyEntry;

        internal static DungeonInteractionPolicy Resolve(
            DungeonRewardPolicy rewardPolicy)
        {
            return rewardPolicy?.Kind
                    == DungeonRewardPolicyKind.InteractiveTraining
                ? InteractiveTraining
                : Standard;
        }
    }
}
