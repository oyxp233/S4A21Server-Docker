using DfoServer.Game.Dungeon.Tournament;
using DfoServer.Game.Dungeon.BloodAltar;
using System;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonInstanceMechanismRuntimeSet
    {
        private readonly object _syncRoot = new object();
        private TournamentDungeonRuntime _tournament;
        private BloodAltarDungeonRuntime _bloodAltar;
        private DungeonBossRouteRuntime _bossRoute;

        internal DungeonDynamicActorRegistry DynamicActors { get; } =
            new DungeonDynamicActorRegistry();

        internal TournamentDungeonRuntime Tournament
        {
            get
            {
                lock (_syncRoot)
                    return _tournament;
            }
        }

        internal bool TryAttachTournament(TournamentDungeonRuntime runtime)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            lock (_syncRoot)
            {
                if (_tournament == null)
                {
                    _tournament = runtime;
                    return true;
                }
                return ReferenceEquals(_tournament, runtime);
            }
        }

        internal BloodAltarDungeonRuntime BloodAltar
        {
            get
            {
                lock (_syncRoot)
                    return _bloodAltar;
            }
        }

        internal bool TryAttachBloodAltar(BloodAltarDungeonRuntime runtime)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            lock (_syncRoot)
            {
                if (_bloodAltar == null)
                {
                    _bloodAltar = runtime;
                    return true;
                }
                return ReferenceEquals(_bloodAltar, runtime);
            }
        }

        internal DungeonBossRouteRuntime BossRoute
        {
            get
            {
                lock (_syncRoot)
                    return _bossRoute;
            }
        }

        internal bool TryAttachBossRoute(DungeonBossRouteRuntime runtime)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            lock (_syncRoot)
            {
                if (_bossRoute == null)
                {
                    _bossRoute = runtime;
                    return true;
                }
                return ReferenceEquals(_bossRoute, runtime);
            }
        }

        internal void OnInstanceEnding()
        {
            BloodAltarDungeonRuntime bloodAltar;
            lock (_syncRoot)
                bloodAltar = _bloodAltar;

            bloodAltar?.Timers.CancelAll();
        }
    }
}
