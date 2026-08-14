using PvfLib;

namespace DfoServer.Game.Dungeon
{
    internal static class DungeonActorRewardEligibility
    {
        internal static bool AllowsParticipantCombatRewards(
            GameWorld.Dungeon.MonsterSumInfo actor)
        {
            var isApc = actor.Type >= (byte)ApcAIType.Normal
                && actor.Type <= (byte)ApcAIType.Boss;
            if (!isApc || !actor.Faction.HasValue)
                return true;

            return actor.Faction.Value == ApcFaction.Monster;
        }
    }
}
