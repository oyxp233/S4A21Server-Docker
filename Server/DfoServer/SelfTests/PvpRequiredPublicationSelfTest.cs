using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Network;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class PvpRequiredPublicationSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            try
            {
                CheckConcurrentQueue(ref failures);
                CheckDirectHandshakeBarrier(ref failures);
                CheckStalledSend(ref failures);
                CheckDisposeReleasesBarrier(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] PvP required publication self-test threw: " +
                    ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "PvpRequiredPublicationSelfTest OK"
                    : $"PvpRequiredPublicationSelfTest FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckConcurrentQueue(ref int failures)
        {
            const int publicationCount = 32;
            using var fixture = SessionFixture.Create(6501);
            using var roomGate = new SemaphoreSlim(0, 1);
            using var enqueued = new CountdownEvent(publicationCount);
            var delivered = new ConcurrentBag<byte>();
            var activeSends = 0;
            var maximumActiveSends = 0;
            using var coordinator =
                new PvpRequiredPublicationCoordinator(
                    roomGate,
                    async (_, packet, cancellationToken) =>
                    {
                        var active = Interlocked.Increment(
                            ref activeSends);
                        UpdateMaximum(
                            ref maximumActiveSends,
                            active);
                        try
                        {
                            await Task.Delay(
                                TimeSpan.FromMilliseconds(3),
                                cancellationToken);
                            delivered.Add(packet[0]);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref activeSends);
                        }
                    },
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(1),
                    _ => { });

            var publications = Enumerable.Range(0, publicationCount)
                .Select(
                    index => Task.Run(
                        async () =>
                        {
                            var publication = coordinator.QueueRequired(
                                new[] { fixture.Session },
                                GameNetworkConfig.FreeDuelGamePort,
                                new[] { (byte)index });
                            enqueued.Signal();
                            await publication;
                        }))
                .ToArray();
            if (!enqueued.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException(
                    "concurrent publications were not queued");
            }

            roomGate.Release();
            Task.WhenAll(publications)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            var deliveredValues = delivered.OrderBy(value => value).ToArray();
            Check(
                "concurrent same-session publications stay strictly serial",
                maximumActiveSends == 1,
                ref failures);
            Check(
                "concurrent same-session publications are delivered once",
                deliveredValues.SequenceEqual(
                    Enumerable.Range(0, publicationCount)
                        .Select(value => (byte)value)),
                ref failures);
        }

        private static void CheckDirectHandshakeBarrier(ref int failures)
        {
            using var fixture = SessionFixture.Create(6502);
            using var roomGate = new SemaphoreSlim(1, 1);
            var sent = 0;
            using var coordinator =
                new PvpRequiredPublicationCoordinator(
                    roomGate,
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref sent);
                        return Task.CompletedTask;
                    },
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(250),
                    _ => { });

            roomGate.Wait();
            coordinator.ReserveDirectHandshakeUnderGate(
                fixture.Session,
                out var preceding,
                out var barrier);
            var deferred = coordinator.QueueRequired(
                new[] { fixture.Session },
                GameNetworkConfig.FreeDuelGamePort,
                new byte[] { 0x41 });
            roomGate.Release();

            var unrelatedMutation = Task.Run(
                async () =>
                {
                    await roomGate.WaitAsync();
                    roomGate.Release();
                });
            unrelatedMutation
                .WaitAsync(TimeSpan.FromSeconds(1))
                .GetAwaiter()
                .GetResult();
            Check(
                "direct handshake barrier defers only its session wire",
                preceding.IsCompletedSuccessfully &&
                deferred.IsCompletedSuccessfully &&
                Volatile.Read(ref sent) == 0 &&
                coordinator.DirectHandshakeCount == 1,
                ref failures);

            coordinator.CompleteDirectHandshake(
                fixture.Session,
                barrier);
            var resumed = WaitUntilAsync(
                    () =>
                        Volatile.Read(ref sent) == 1 &&
                        coordinator.ActiveTailCount == 0,
                    TimeSpan.FromSeconds(1))
                .GetAwaiter()
                .GetResult();
            Check(
                "barrier completion resumes queued wire and cleans its tail",
                resumed && coordinator.DirectHandshakeCount == 0,
                ref failures);
        }

        private static void CheckStalledSend(ref int failures)
        {
            using var fixture = SessionFixture.Create(6503);
            using var roomGate = new SemaphoreSlim(1, 1);
            var closed = 0;
            var stalled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var coordinator =
                new PvpRequiredPublicationCoordinator(
                    roomGate,
                    (_, _, _) => stalled.Task,
                    TimeSpan.FromMilliseconds(75),
                    TimeSpan.FromSeconds(1),
                    _ => Interlocked.Increment(ref closed));

            coordinator.QueueRequired(
                    new[] { fixture.Session },
                    GameNetworkConfig.FreeDuelGamePort,
                    new byte[] { 0x51 })
                .WaitAsync(TimeSpan.FromSeconds(2))
                .GetAwaiter()
                .GetResult();
            var cleaned = WaitUntilAsync(
                    () => coordinator.ActiveTailCount == 0,
                    TimeSpan.FromSeconds(1))
                .GetAwaiter()
                .GetResult();
            Check(
                "stalled required send closes the target and removes tail",
                Volatile.Read(ref closed) == 1 && cleaned,
                ref failures);
        }

        private static void CheckDisposeReleasesBarrier(ref int failures)
        {
            using var fixture = SessionFixture.Create(6504);
            using var roomGate = new SemaphoreSlim(1, 1);
            var sent = 0;
            var coordinator = new PvpRequiredPublicationCoordinator(
                roomGate,
                (_, _, _) =>
                {
                    Interlocked.Increment(ref sent);
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                _ => { });

            roomGate.Wait();
            coordinator.ReserveDirectHandshakeUnderGate(
                fixture.Session,
                out _,
                out _);
            coordinator.QueueRequired(
                new[] { fixture.Session },
                GameNetworkConfig.FreeDuelGamePort,
                new byte[] { 0x61 });
            roomGate.Release();
            coordinator.Dispose();

            var cleaned = WaitUntilAsync(
                    () =>
                        Volatile.Read(ref sent) == 1 &&
                        coordinator.ActiveTailCount == 0,
                    TimeSpan.FromSeconds(1))
                .GetAwaiter()
                .GetResult();
            Check(
                "dispose releases direct barrier and clears coordinator state",
                cleaned && coordinator.DirectHandshakeCount == 0,
                ref failures);
        }

        private static async Task<bool> WaitUntilAsync(
            Func<bool> predicate,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                    return true;
                await Task.Delay(10);
            }
            return predicate();
        }

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximum);
                if (candidate <= current)
                    return;
                if (Interlocked.CompareExchange(
                        ref maximum,
                        candidate,
                        current) == current)
                {
                    return;
                }
            }
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

        private sealed class SessionFixture : IDisposable
        {
            private readonly TcpClient _client;

            private SessionFixture(
                TcpClient client,
                EnhancedClientSession session)
            {
                _client = client;
                Session = session;
            }

            internal EnhancedClientSession Session { get; }

            internal static SessionFixture Create(int characterId)
            {
                var client = new TcpClient();
                var session = new EnhancedClientSession(
                    client,
                    new GamePacketHeader(),
                    GameNetworkConfig.FreeDuelGamePort);
                session.Player.CharacterId = characterId;
                session.Player.UserId = (ushort)characterId;
                return new SessionFixture(client, session);
            }

            public void Dispose()
            {
                _client.Dispose();
            }
        }
    }
}
