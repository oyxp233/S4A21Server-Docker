using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using DfoServer.Game.Characters;
using DfoServer.Game.Party;
using DfoServer.Network;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class PartyCommandIsolationSelfTest
    {
        public static int Run()
        {
            var manager = new PartyManager();
            using var client = new TcpClient();
            var session = new EnhancedClientSession(
                client,
                new GamePacketHeader(),
                GameNetworkConfig.NormalGamePort);
            session.Player.HydrateIdentityFrom(
                new CharacterRecord
                {
                    CharacterId = 4101,
                    Name = new byte[] { (byte)'a' },
                    Level = 1,
                });

            var ownerSession = session.SessionId;
            var otherSession = Guid.NewGuid();
            var ownerParty = manager.CreateParty(
                new PartyMember
                {
                    UserId = 4101,
                    CharacterId = 4101,
                    SessionId = ownerSession,
                    Name = "owner",
                }).Party;
            var otherParty = manager.CreateParty(
                new PartyMember
                {
                    UserId = 4102,
                    CharacterId = 4102,
                    SessionId = otherSession,
                    Name = "other",
                }).Party;

            // This command intentionally touches no PartyHandler dependencies.
            // Avoid opening the production database merely to test its routing.
            var handler = (PartyHandler)RuntimeHelpers.GetUninitializedObject(
                typeof(PartyHandler));
            handler.Handle_CREATE_GROUP(
                    session,
                    new GamePacketHeader { cmd = 0x01, type = 0x01A3 },
                    new byte[] { 5, 0, 0, 0, (byte)'o', (byte)'t', (byte)'h', (byte)'e', (byte)'r' })
                .GetAwaiter()
                .GetResult();

            var ownerAfter = manager.GetPartyByUser(4101);
            var otherAfter = manager.GetPartyByUser(4102);
            var passed =
                ownerAfter?.PartyId == ownerParty.PartyId &&
                ownerAfter.Count == 1 &&
                otherAfter?.PartyId == otherParty.PartyId &&
                otherAfter.Count == 1;
            Console.WriteLine(
                $"[{(passed ? "PASS" : "FAIL")}] " +
                "0x01A3 chat intent leaves both party generations unchanged");
            return passed ? 0 : 1;
        }
    }
}
