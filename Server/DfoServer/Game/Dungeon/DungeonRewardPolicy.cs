namespace DfoServer.Game.Dungeon
{
    public enum DungeonRewardPolicyKind
    {
        Standard = 0,
        InteractiveTraining = 1,
        AntonRaid = 2,
    }

    // Immutable per-instance policy. Configuration is resolved before a run is
    // created, then every participant in the physical instance shares it.
    public sealed class DungeonRewardPolicy
    {
        public static DungeonRewardPolicy Standard { get; } = new DungeonRewardPolicy(
            DungeonRewardPolicyKind.Standard,
            allowsMonsterExperience: true,
            allowsMonsterDrops: true,
            allowsQuestDrops: true,
            allowsQuestProgress: true,
            allowsPetExperience: true,
            allowsClearCommit: true,
            allowsSettlement: true);

        public static DungeonRewardPolicy AntonRaid { get; } = new DungeonRewardPolicy(
            DungeonRewardPolicyKind.AntonRaid,
            allowsMonsterExperience: true,
            allowsMonsterDrops: false,
            allowsQuestDrops: false,
            allowsQuestProgress: true,
            allowsPetExperience: true,
            allowsClearCommit: true,
            allowsSettlement: true);

        public static DungeonRewardPolicy InteractiveTraining { get; } = new DungeonRewardPolicy(
            DungeonRewardPolicyKind.InteractiveTraining,
            allowsMonsterExperience: false,
            allowsMonsterDrops: false,
            allowsQuestDrops: false,
            allowsQuestProgress: false,
            allowsPetExperience: false,
            allowsClearCommit: false,
            allowsSettlement: false);

        private DungeonRewardPolicy(
            DungeonRewardPolicyKind kind,
            bool allowsMonsterExperience,
            bool allowsMonsterDrops,
            bool allowsQuestDrops,
            bool allowsQuestProgress,
            bool allowsPetExperience,
            bool allowsClearCommit,
            bool allowsSettlement)
        {
            Kind = kind;
            AllowsMonsterExperience = allowsMonsterExperience;
            AllowsMonsterDrops = allowsMonsterDrops;
            AllowsQuestDrops = allowsQuestDrops;
            AllowsQuestProgress = allowsQuestProgress;
            AllowsPetExperience = allowsPetExperience;
            AllowsClearCommit = allowsClearCommit;
            AllowsSettlement = allowsSettlement;
        }

        public DungeonRewardPolicyKind Kind { get; }
        public bool AllowsMonsterExperience { get; }
        public bool AllowsMonsterDrops { get; }
        public bool AllowsQuestDrops { get; }
        public bool AllowsQuestProgress { get; }
        public bool AllowsPetExperience { get; }
        public bool AllowsClearCommit { get; }
        public bool AllowsSettlement { get; }
    }
}
