using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace DfoServer.SelfTests
{
    public static class SessionGenerationSelfTest
    {
        private const int CharacterId = 4300;

        public static int Run()
        {
            var failures = 0;
            var directory = new SessionDirectory();
            var endings = new List<(int CharacterId, Guid SessionId)>();
            directory.SessionEnding += (characterId, session) =>
            {
                endings.Add((characterId, session.SessionId));
                return Task.CompletedTask;
            };

            var oldSession = CreateSession();
            var newSession = CreateSession();
            try
            {
                directory.Register(CharacterId, oldSession);
                var displaced = directory
                    .RegisterReplacingAsync(CharacterId, newSession)
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "replacement reports and ends only the old generation",
                    ReferenceEquals(displaced, oldSession)
                    && endings.Count == 1
                    && endings[0].CharacterId == CharacterId
                    && endings[0].SessionId == oldSession.SessionId,
                    ref failures);

                var staleRemoved = directory
                    .UnregisterAsync(CharacterId, oldSession)
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "late old disconnect cannot remove the new generation",
                    !staleRemoved
                    && directory.TryGet(CharacterId, out var current)
                    && ReferenceEquals(current, newSession)
                    && endings.Count == 1,
                    ref failures);

                Check(
                    "packet ownership rejects old and accepts current session",
                    !CharacterSessionLifecycleCoordinator
                        .OwnsRegisteredGeneration(
                        directory,
                        oldSession)
                    && CharacterSessionLifecycleCoordinator
                        .OwnsRegisteredGeneration(
                        directory,
                        newSession),
                    ref failures);

                var currentRemoved = directory
                    .UnregisterAsync(CharacterId, newSession)
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "current generation unregisters exactly once",
                    currentRemoved
                    && !directory.TryGet(CharacterId, out _)
                    && endings.Count == 2
                    && endings[1].SessionId == newSession.SessionId,
                    ref failures);

                newSession.Player.TownPresenceReady = true;
                CharacterSessionLifecycleCoordinator
                    .EnterCharacterSelectionState(newSession);
                Check(
                    "selection state clears wire identity and remains dispatchable",
                    newSession.Player.CharacterId == 0
                    && newSession.Player.UserId == 0
                    && CharacterSessionLifecycleCoordinator
                        .OwnsRegisteredGeneration(
                        directory,
                        newSession),
                    ref failures);
            }
            finally
            {
                oldSession.Close();
                newSession.Close();
            }

            Console.WriteLine(
                $"=== SESSION_GENERATION result: failures={failures} ===");
            return failures == 0 ? 0 : 1;
        }

        private static EnhancedClientSession CreateSession()
        {
            var session = new EnhancedClientSession(
                new TcpClient(),
                new GamePacketHeader(),
                GameNetworkConfig.NormalGamePort);
            session.Player.CharacterId = CharacterId;
            session.Player.UserId = (ushort)CharacterId;
            return session;
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine((condition ? "[OK] " : "[FAIL] ") + name);
            if (!condition)
                failures++;
        }
    }
}
