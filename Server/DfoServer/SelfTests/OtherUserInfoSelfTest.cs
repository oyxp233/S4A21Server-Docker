using System;
using System.Net.Sockets;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class OtherUserInfoSelfTest
    {
        public static int Run()
        {
            var directory = new SessionDirectory();
            using var requesterClient = new TcpClient();
            using var targetClient = new TcpClient();
            var requester = CreateSession(requesterClient, 4201);
            var target = CreateSession(targetClient, 4202);
            directory.Register(4201, requester);
            directory.Register(4202, target);

            var found = CharacterSelectHandler.FindOnlineByUserId(
                directory,
                target.Player.UserId);
            var missing = CharacterSelectHandler.FindOnlineByUserId(
                directory,
                4999);
            var passed =
                ReferenceEquals(found, target) &&
                found.Player.CharacterId == 4202 &&
                missing == null;
            Console.WriteLine(
                $"[{(passed ? "PASS" : "FAIL")}] " +
                "GET_USERINFO resolves the requested online character only");
            return passed ? 0 : 1;
        }

        private static EnhancedClientSession CreateSession(
            TcpClient client,
            int characterId)
        {
            var session = new EnhancedClientSession(
                client,
                new GamePacketHeader(),
                GameNetworkConfig.NormalGamePort);
            session.Player.HydrateIdentityFrom(
                new CharacterRecord
                {
                    CharacterId = characterId,
                    Name = BitConverter.GetBytes(characterId),
                    Level = 1,
                });
            return session;
        }
    }
}
