using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Owns timers that belong to optional dungeon mechanisms. Generic run
    // lifecycle code only starts or cancels this coordinator.
    internal static class DungeonMechanismTimerCoordinator
    {
        private const int GentInfiltrateClientTimerSyncGraceSeconds = 4;

        internal static void Start(EnhancedClientSession session, string source)
        {
            var run = session?.Player?.CurrentRun;
            var special = run?.SpecialDungeon;
            if (run == null
                || special == null
                || special.Kind != SpecialDungeonKind.GentInfiltrate)
            {
                return;
            }

            var seconds = special.GentInfiltrateTimerSeconds;
            if (seconds <= 0)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] GENT_INFILTRATE timer skipped " +
                    $"source={source} cid={session.Player.CharacterId} " +
                    $"dungeon={special.DungeonId} reason=no_timer");
                return;
            }

            var scheduledSeconds =
                seconds + GentInfiltrateClientTimerSyncGraceSeconds;
            var identity = run.CaptureIdentity();
            var deadlineUtc = DateTime.UtcNow.AddSeconds(scheduledSeconds);
            var ticket = run.Timers.Begin(
                DungeonRunTimerKeys.GentInfiltrateTimeout,
                deadlineUtc,
                RunTimerDetachPolicy.SuspendUntilResume);
            Schedule(
                session,
                run,
                identity,
                ticket,
                deadlineUtc);

            FileLogger.Log(
                $"[SpecialDungeonModule] GENT_INFILTRATE timer scheduled " +
                $"source={source} cid={session.Player.CharacterId} " +
                $"dungeon={special.DungeonId} configSeconds={seconds} " +
                $"scheduledSeconds={scheduledSeconds} " +
                $"clientSyncGrace={GentInfiltrateClientTimerSyncGraceSeconds} " +
                $"deadline={deadlineUtc:O} generation={ticket.Generation}");
        }

        internal static bool Recover(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            var special = run?.SpecialDungeon;
            if (run == null
                || special?.Kind != SpecialDungeonKind.GentInfiltrate)
            {
                return false;
            }
            if (special.GentInfiltrateConditionComplete
                || special.GentInfiltrateTimedOut)
            {
                run.Timers.Cancel(
                    DungeonRunTimerKeys.GentInfiltrateTimeout);
                return false;
            }
            if (!run.Timers.TryResume(
                    DungeonRunTimerKeys.GentInfiltrateTimeout,
                    out var ticket,
                    out var deadlineUtc))
            {
                return false;
            }

            Schedule(
                session,
                run,
                run.CaptureIdentity(),
                ticket,
                deadlineUtc);
            FileLogger.Log(
                $"[SpecialDungeonModule] GENT_INFILTRATE timer resumed " +
                $"cid={session.Player.CharacterId} dungeon={special.DungeonId} " +
                $"deadline={deadlineUtc:O} generation={ticket.Generation}");
            return true;
        }

        private static void Schedule(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket,
            DateTime deadlineUtc)
        {
            var timerName =
                $"special-dungeon:gent-infiltrate:{session.Player.CharacterId}:" +
                $"{run.RunId}:{ticket.Generation}";
            var handle = ClockService.Instance.ScheduleOneShotAsync(
                timerName,
                deadlineUtc,
                async _ => await OnTimeoutElapsedAsync(
                    session,
                    run,
                    identity,
                    ticket));

            run.Timers.Attach(ticket, handle);
        }

        internal static void Cancel(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            Cancel(run);
        }

        internal static void Cancel(DungeonRun run)
        {
            if (run == null)
                return;

            run.Timers.Cancel(DungeonRunTimerKeys.GentInfiltrateTimeout);
        }

        private static async System.Threading.Tasks.Task OnTimeoutElapsedAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket)
        {
            if (session?.Player == null
                || !session.Player.IsCurrentDungeonRun(identity)
                || !run.Matches(identity)
                || !run.Timers.IsCurrent(ticket))
                return;

            try
            {
                await SpecialDungeonNotifier.MarkGentInfiltrateTimeoutAsync(
                    session,
                    run,
                    "timer");
            }
            finally
            {
                run.Timers.TryComplete(ticket);
            }
        }
    }
}
