using System;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct DungeonParticipantExperienceSnapshot
    {
        internal DungeonParticipantExperienceSnapshot(
            uint monsterBaseExperience,
            uint monsterGrowthContractBonusExperience,
            uint bossBaseExperience,
            uint championBaseExperience,
            uint superChampionBaseExperience,
            uint namedMonsterBaseExperience,
            uint monsterChannelBonusExperience = 0)
        {
            MonsterBaseExperience = monsterBaseExperience;
            MonsterGrowthContractBonusExperience =
                monsterGrowthContractBonusExperience;
            MonsterChannelBonusExperience = monsterChannelBonusExperience;
            BossBaseExperience = bossBaseExperience;
            ChampionBaseExperience = championBaseExperience;
            SuperChampionBaseExperience = superChampionBaseExperience;
            NamedMonsterBaseExperience = namedMonsterBaseExperience;
        }

        internal uint MonsterBaseExperience { get; }
        internal uint MonsterGrowthContractBonusExperience { get; }
        internal uint MonsterChannelBonusExperience { get; }
        internal uint MonsterTotalExperience => AddSaturating(
            MonsterBaseExperience,
            AddSaturating(
                MonsterGrowthContractBonusExperience,
                MonsterChannelBonusExperience));
        internal uint BossBaseExperience { get; }
        internal uint ChampionBaseExperience { get; }
        internal uint SuperChampionBaseExperience { get; }
        internal uint NamedMonsterBaseExperience { get; }

        private static uint AddSaturating(uint left, uint right)
        {
            var sum = (ulong)left + right;
            return sum >= uint.MaxValue ? uint.MaxValue : (uint)sum;
        }
    }

    // Per-participant awarded EXP ledger. World kill counts remain on
    // DungeonInstance; this runtime only records this player's projection.
    internal sealed class DungeonParticipantExperienceRuntime
    {
        private DungeonParticipantExperienceBonusSnapshot _bonusSnapshot =
            DungeonParticipantExperienceBonusSnapshot.None;
        private bool _bonusSnapshotFrozen;

        internal uint MonsterBaseExperience { get; private set; }
        internal uint MonsterGrowthContractBonusExperience { get; private set; }
        internal uint MonsterChannelBonusExperience { get; private set; }
        internal uint MonsterTotalExperience => AddSaturating(
            MonsterBaseExperience,
            AddSaturating(
                MonsterGrowthContractBonusExperience,
                MonsterChannelBonusExperience));
        internal uint BossBaseExperience { get; private set; }
        internal uint ChampionBaseExperience { get; private set; }
        internal uint SuperChampionBaseExperience { get; private set; }
        internal uint NamedMonsterBaseExperience { get; private set; }

        internal bool TryFreezeBonusSnapshot(
            DungeonParticipantExperienceBonusSnapshot snapshot)
        {
            if (_bonusSnapshotFrozen || !snapshot.IsCaptured)
                return false;

            _bonusSnapshot = snapshot;
            _bonusSnapshotFrozen = true;
            return true;
        }

        internal DungeonParticipantExperienceBonusSnapshot
            CaptureBonusSnapshot() => _bonusSnapshot;

        internal void RecordMonster(
            uint baseExperience,
            uint growthContractBonusExperience,
            bool isBoss,
            bool isChampion,
            bool isSuperChampion,
            bool isNamedMonster,
            uint channelBonusExperience = 0)
        {
            MonsterBaseExperience = AddSaturating(
                MonsterBaseExperience,
                baseExperience);
            MonsterGrowthContractBonusExperience = AddSaturating(
                MonsterGrowthContractBonusExperience,
                growthContractBonusExperience);
            MonsterChannelBonusExperience = AddSaturating(
                MonsterChannelBonusExperience,
                channelBonusExperience);
            if (isBoss)
                BossBaseExperience = AddSaturating(
                    BossBaseExperience,
                    baseExperience);
            if (isChampion)
                ChampionBaseExperience = AddSaturating(
                    ChampionBaseExperience,
                    baseExperience);
            if (isSuperChampion)
                SuperChampionBaseExperience = AddSaturating(
                    SuperChampionBaseExperience,
                    baseExperience);
            if (isNamedMonster)
                NamedMonsterBaseExperience = AddSaturating(
                    NamedMonsterBaseExperience,
                    baseExperience);
        }

        internal DungeonParticipantExperienceSnapshot Capture()
            => new DungeonParticipantExperienceSnapshot(
                MonsterBaseExperience,
                MonsterGrowthContractBonusExperience,
                BossBaseExperience,
                ChampionBaseExperience,
                SuperChampionBaseExperience,
                NamedMonsterBaseExperience,
                MonsterChannelBonusExperience);

        // Compatibility setters keep existing fixture/setup APIs usable. New
        // production code records awards only through RecordMonster.
        internal void SetMonsterTotalForCompatibility(uint value)
        {
            MonsterBaseExperience = value;
            MonsterGrowthContractBonusExperience = 0;
        }

        internal void SetBossBaseForCompatibility(uint value) =>
            BossBaseExperience = value;

        internal void SetChampionBaseForCompatibility(uint value) =>
            ChampionBaseExperience = value;

        internal void SetSuperChampionBaseForCompatibility(uint value) =>
            SuperChampionBaseExperience = value;

        internal void SetNamedMonsterBaseForCompatibility(uint value) =>
            NamedMonsterBaseExperience = value;

        internal void SetGrowthContractBonusForCompatibility(uint value) =>
            MonsterGrowthContractBonusExperience = value;

        internal void SetChannelBonusForCompatibility(uint value) =>
            MonsterChannelBonusExperience = value;

        private static uint AddSaturating(uint left, uint right)
        {
            var sum = (ulong)left + right;
            return sum >= uint.MaxValue ? uint.MaxValue : (uint)sum;
        }
    }
}
