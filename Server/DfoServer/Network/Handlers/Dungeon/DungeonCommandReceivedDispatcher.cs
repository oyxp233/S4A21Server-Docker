using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Dungeon;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal static class DungeonCommandReceivedDispatcher
    {
        internal static Task DispatchAsync(
            EnhancedClientSession session,
            DungeonCommand command,
            DropService drops,
            TournamentDungeonCoordinator tournaments,
            BloodAltarDungeonCoordinator bloodAltars,
            IGameDatabase database)
        {
            if (session == null || command == null)
                return Task.CompletedTask;

            var run = session.Player?.CurrentRun;
            var sourceEvent = run != null
                ? DungeonEventEnvelope.Create(
                    run,
                    session.Player.CharacterId,
                    $"dungeon command 0x{command.WireType:X4}")
                : null;

            switch (command)
            {
                case SummonMonsterDungeonCommand summon:
                    return SpecialDungeonNotifier.HandleBossSummonRequestAsync(
                        session,
                        summon,
                        sourceEvent);

                case TimerModifyInfoDungeonCommand timer:
                    return SpecialDungeonNotifier.HandleGentInfiltrateTimerModifyInfoAsync(
                        session,
                        timer,
                        sourceEvent);

                case SeaChaseResultDungeonCommand result:
                    return SpecialDungeonNotifier.HandleSeaChaseMiniGameResultAsync(
                        session,
                        result,
                        sourceEvent);

                case SeaChaseObservedDungeonCommand observed:
                    return SpecialDungeonNotifier.ObserveSeaChasePacketAsync(
                        session,
                        observed,
                        sourceEvent);

                case NpcItemDropDungeonCommand npcDrop:
                    return DungeonNpcItemDropCoordinator.HandleCommandAsync(
                        session,
                        npcDrop,
                        drops,
                        sourceEvent,
                        database);

                case BreakTrapResultDungeonCommand breakTrap:
                    return TimeSpiralDungeonCoordinator.HandleBreakTrapResultAsync(
                        session,
                        breakTrap,
                        sourceEvent);

                case TournamentRewardSelectStateDungeonCommand tournamentState:
                    return tournaments?.HandleRewardSelectStateAsync(
                        session,
                        tournamentState,
                        sourceEvent) ?? Task.CompletedTask;

                case TournamentRewardSelectDungeonCommand tournamentSelect:
                    return tournaments?.HandleRewardSelectAsync(
                        session,
                        tournamentSelect,
                        sourceEvent) ?? Task.CompletedTask;

                case BloodAltarPrepareFinishedDungeonCommand altarPrepare:
                    return bloodAltars?.HandlePrepareFinishedAsync(
                        session,
                        altarPrepare,
                        sourceEvent) ?? Task.CompletedTask;

                case BloodAltarMonsterDeathsDungeonCommand altarDeaths:
                    return bloodAltars?.HandleMonsterDeathsAsync(
                        session,
                        altarDeaths,
                        sourceEvent) ?? Task.CompletedTask;

                case BloodAltarSelectDifficultyDungeonCommand altarDifficulty:
                    return bloodAltars?.HandleSelectDifficultyAsync(
                        session,
                        altarDifficulty,
                        sourceEvent) ?? Task.CompletedTask;

                default:
                    FileLogger.Log(
                        $"[DungeonCommand] no dispatcher for type=0x{command.WireType:X4} " +
                        $"command={command.GetType().Name}");
                    return Task.CompletedTask;
            }
        }
    }
}
