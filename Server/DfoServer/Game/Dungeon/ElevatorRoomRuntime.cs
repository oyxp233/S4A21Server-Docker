using System;
using System.Threading;

namespace DfoServer.Game.Dungeon
{
    internal enum ElevatorStopKind : byte
    {
        Running = 0,
        Normal = 1,
        Crash = 2,
    }

    internal readonly struct ElevatorRoomSnapshot
    {
        internal ElevatorRoomSnapshot(byte stage, ElevatorStopKind stop)
        {
            Stage = stage;
            Stop = stop;
        }

        internal byte Stage { get; }
        internal ElevatorStopKind Stop { get; }
    }

    // One physical room owns the clock and outcome for every participant.
    // Original server: check_allmember_loading 0x085B1C18 starts a 15s timer;
    // check_elevator_timer 0x0830D902 advances stages 1..4 every 15s;
    // kill_monster 0x0830D16E selects normal below stage 4, crash at stage 4.
    internal sealed class ElevatorRoomRuntime
    {
        internal const int StageSeconds = 15;
        internal const byte LastStage = 4;
        private readonly object _syncRoot = new object();
        private bool _started;
        private bool _closed;
        private DateTime _startedUtc;
        private long _startedTick;
        private ElevatorRoomSnapshot _state;

        internal RunTimerRegistry Timers { get; } = new RunTimerRegistry();
        internal SemaphoreSlim ProjectionGate { get; } = new SemaphoreSlim(1, 1);

        internal bool TryStart(DateTime utcNow, long tickNow,
            out RunTimerTicket ticket, out DateTime deadlineUtc)
        {
            lock (_syncRoot)
            {
                ticket = default;
                deadlineUtc = default;
                if (_started || _closed)
                    return false;
                _started = true;
                _startedUtc = utcNow;
                _startedTick = tickNow;
                BeginNextStage(out ticket, out deadlineUtc);
                return true;
            }
        }

        internal bool TryAdvance(RunTimerTicket ticket, DateTime utcNow,
            out ElevatorRoomSnapshot state,
            out RunTimerTicket nextTicket, out DateTime nextDeadlineUtc)
        {
            lock (_syncRoot)
            {
                state = default;
                nextTicket = default;
                nextDeadlineUtc = default;
                if (_closed || !Timers.IsCurrent(ticket)
                    || _state.Stop != ElevatorStopKind.Running)
                    return false;

                var stage = StageAt(utcNow);
                if (stage <= _state.Stage)
                    return false;
                _state = new ElevatorRoomSnapshot(stage, ElevatorStopKind.Running);
                state = _state;
                Timers.TryComplete(ticket);
                if (stage < LastStage)
                    BeginNextStage(out nextTicket, out nextDeadlineUtc);
                return true;
            }
        }

        internal void Complete(long clearedTick)
        {
            lock (_syncRoot)
            {
                if (!_started || _closed || _state.Stop != ElevatorStopKind.Running)
                    return;

                // Use the canonical death time, including when participant
                // rewards or a reconnect delay the clear projection.
                var stage = StageAt(_startedUtc.AddMilliseconds(clearedTick - _startedTick));
                _state = new ElevatorRoomSnapshot(stage,
                    stage < LastStage ? ElevatorStopKind.Normal : ElevatorStopKind.Crash);
                Timers.CancelAll();
            }
        }

        internal ElevatorRoomSnapshot? Capture(DateTime utcNow)
        {
            lock (_syncRoot)
            {
                if (!_started || _closed)
                    return null;
                return _state.Stop == ElevatorStopKind.Running
                    ? new ElevatorRoomSnapshot(StageAt(utcNow), ElevatorStopKind.Running)
                    : _state;
            }
        }

        internal bool AllowsExit(RoomKey room, int nextX, int nextY)
        {
            lock (_syncRoot)
            {
                if (_closed || _state.Stop == ElevatorStopKind.Running)
                    return false;
                var direction = _state.Stop == ElevatorStopKind.Normal ? 1 : -1;
                return nextX == room.X + direction && nextY == room.Y;
            }
        }

        internal void Close()
        {
            lock (_syncRoot)
            {
                _closed = true;
                Timers.CancelAll();
            }
        }

        private byte StageAt(DateTime utcNow)
            => (byte)Math.Clamp((int)((utcNow - _startedUtc).TotalSeconds / StageSeconds),
                0, LastStage);

        private void BeginNextStage(out RunTimerTicket ticket, out DateTime deadlineUtc)
        {
            deadlineUtc = _startedUtc.AddSeconds((_state.Stage + 1) * StageSeconds);
            ticket = Timers.Begin(DungeonRunTimerKeys.ElevatorStage, deadlineUtc,
                RunTimerDetachPolicy.Cancel);
        }
    }
}
