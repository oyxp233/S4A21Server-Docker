using DfoServer.GameWorld;

namespace DfoServer.Game.Dungeon
{
    internal sealed class ScriptedFatalEndpointRuntime
    {
        internal ScriptedFatalEndpointRuntime(
            ScriptedFatalEndpointDefinition definition)
        {
            Definition = definition;
        }

        internal ScriptedFatalEndpointDefinition Definition { get; }
        internal bool Armed { get; private set; }
        internal bool ClearIssued { get; private set; }

        internal bool TryArmForPassiveObject(int objectCode)
            => TryArm(Definition?.MatchesTriggerPassiveObject(objectCode) == true);

        internal bool TryArmForFixtureMonster(int monsterCode)
            => TryArm(Definition?.MatchesFixtureMonster(monsterCode) == true);

        internal bool TryHandleCharacterDeath(out bool shouldClear)
        {
            shouldClear = false;
            if (!Armed)
                return false;

            if (!ClearIssued)
            {
                ClearIssued = true;
                shouldClear = true;
            }

            return true;
        }

        internal ScriptedFatalEndpointRuntime CloneFresh()
            => Definition == null
                ? null
                : new ScriptedFatalEndpointRuntime(Definition);

        private bool TryArm(bool matches)
        {
            if (!matches || Armed)
                return false;

            Armed = true;
            return true;
        }
    }
}
