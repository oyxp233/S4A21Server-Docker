using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Network;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class PvpRoomAdmissionSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            try
            {
                CheckSingleReservation(ref failures);
                CheckConcurrentReservation(ref failures);
                CheckInviteIdentity(ref failures);
                CheckSessionCleanup(ref failures);
                CheckDisposeCompletesWaiters(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] PvP room admission self-test threw: " +
                    ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "PvpRoomAdmissionSelfTest OK"
                    : $"PvpRoomAdmissionSelfTest FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckSingleReservation(ref int failures)
        {
            using var coordinator = new PvpRoomAdmissionCoordinator();
            var session = Guid.NewGuid();
            var reserved = coordinator.TryReservePendingJoin(501, session);
            var duplicate = coordinator.TryReservePendingJoin(
                501,
                Guid.NewGuid());
            var found = coordinator.TryFindPendingJoinForSession(
                session,
                out var roomId,
                out var completion);
            var wrongCompletion = coordinator.CompletePendingJoin(
                roomId,
                Guid.NewGuid());
            var completed = coordinator.CompletePendingJoin(
                roomId,
                session);
            completion.WaitAsync(TimeSpan.FromSeconds(1))
                .GetAwaiter()
                .GetResult();

            Check(
                "one room has one exact pending join reservation",
                reserved &&
                !duplicate &&
                found &&
                roomId == 501 &&
                !wrongCompletion &&
                completed &&
                coordinator.PendingJoinCount == 0,
                ref failures);
        }

        private static void CheckConcurrentReservation(ref int failures)
        {
            using var coordinator = new PvpRoomAdmissionCoordinator();
            var results =
                Task.WhenAll(
                        Enumerable.Range(0, 32)
                            .Select(
                                _ => Task.Run(
                                    () => coordinator
                                        .TryReservePendingJoin(
                                            502,
                                            Guid.NewGuid()))))
                    .GetAwaiter()
                    .GetResult();
            var winner = coordinator
                .PendingRoomJoinSessions
                .Single();
            coordinator.CompletePendingJoin(
                winner.Key,
                winner.Value);

            Check(
                "concurrent join preparation elects one room owner",
                results.Count(result => result) == 1 &&
                coordinator.PendingJoinCount == 0,
                ref failures);
        }

        private static void CheckInviteIdentity(ref int failures)
        {
            using var coordinator = new PvpRoomAdmissionCoordinator();
            var target = Guid.NewGuid();
            var inviter = Guid.NewGuid();
            var first = CreateInvite(inviter, 601, 1);
            var replacement = CreateInvite(inviter, 601, 2);
            coordinator.TryStorePendingInvite(target, first);
            coordinator.TryStorePendingInvite(target, replacement);
            var current = coordinator.TryGetPendingInvite(
                target,
                out var stored);
            var staleRemoved = coordinator.TryRemovePendingInvite(
                target,
                first);
            var currentRemoved = coordinator.TryRemovePendingInvite(
                target,
                replacement);

            Check(
                "invite replacement removes only the exact current snapshot",
                current &&
                ReferenceEquals(stored, replacement) &&
                !staleRemoved &&
                currentRemoved &&
                coordinator.PendingInviteCount == 0,
                ref failures);
        }

        private static void CheckSessionCleanup(ref int failures)
        {
            using var coordinator = new PvpRoomAdmissionCoordinator();
            var ending = Guid.NewGuid();
            var other = Guid.NewGuid();
            coordinator.TryStorePendingInvite(
                Guid.NewGuid(),
                CreateInvite(ending, 701, 1));
            coordinator.TryStorePendingInvite(
                ending,
                CreateInvite(other, 701, 2));
            coordinator.TryStorePendingInvite(
                Guid.NewGuid(),
                CreateInvite(other, 701, 3));
            coordinator.RemovePendingInvitesForSession(ending);

            Check(
                "session cleanup removes target and inviter invite snapshots",
                coordinator.PendingInviteCount == 1,
                ref failures);
        }

        private static void CheckDisposeCompletesWaiters(ref int failures)
        {
            var coordinator = new PvpRoomAdmissionCoordinator();
            var session = Guid.NewGuid();
            coordinator.TryReservePendingJoin(801, session);
            coordinator.TryStorePendingInvite(
                Guid.NewGuid(),
                CreateInvite(session, 801, 1));
            coordinator.TryGetPendingJoinCompletion(
                801,
                out var completion);
            coordinator.Dispose();
            completion.WaitAsync(TimeSpan.FromSeconds(1))
                .GetAwaiter()
                .GetResult();
            coordinator.Dispose();

            Check(
                "dispose completes pending join and clears admission state",
                completion.IsCompleted &&
                coordinator.PendingJoinCount == 0 &&
                coordinator.PendingInviteCount == 0,
                ref failures);
        }

        private static PendingRoomInvite CreateInvite(
            Guid inviterSessionId,
            ushort roomId,
            int peerToken)
        {
            return new PendingRoomInvite(
                inviterSessionId,
                roomId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                GameNetworkConfig.FreeDuelGamePort,
                peerToken,
                DateTime.UtcNow.AddMinutes(1));
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                $"[{(condition ? "PASS" : "FAIL")}] {label}");
            if (!condition)
                failures++;
        }
    }
}
