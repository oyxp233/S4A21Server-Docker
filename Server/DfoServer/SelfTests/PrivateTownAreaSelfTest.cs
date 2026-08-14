using DfoServer.Game.Session;
using DfoServer.Network;
using System;
using System.Net;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    internal static class PrivateTownAreaSelfTest
    {
        internal static int Run()
        {
            var failures = 0;
            const byte TownId = 1;
            const byte SharedAreaId = 2;
            const byte PrivateAreaId = 3;
            var directory = new SessionDirectory(
                (townId, areaId) => areaId != PrivateAreaId);

            using var first = ConnectedSession.Create(5101);
            using var second = ConnectedSession.Create(5102);
            PrepareTownPresence(first.Session, TownId, SharedAreaId);
            PrepareTownPresence(second.Session, TownId, SharedAreaId);
            directory.Register(5101, first.Session);
            directory.Register(5102, second.Session);

            Check(
                "ordinary town areas still share their roster",
                directory.GetSessionsInArea(
                    TownId,
                    SharedAreaId,
                    5101,
                    GameNetworkConfig.NormalGamePort).Count == 1,
                ref failures);

            PrepareTownPresence(first.Session, TownId, PrivateAreaId);
            PrepareTownPresence(second.Session, TownId, PrivateAreaId);
            Check(
                "Cera room never exposes another character",
                directory.GetSessionsInArea(
                    TownId,
                    PrivateAreaId,
                    5101,
                    GameNetworkConfig.NormalGamePort).Count == 0,
                ref failures);

            directory.BroadcastToAreaAsync(
                    TownId,
                    PrivateAreaId,
                    5101,
                    new byte[] { 0x5A },
                    GameNetworkConfig.NormalGamePort)
                .GetAwaiter()
                .GetResult();
            Check(
                "Cera room suppresses area broadcasts",
                second.Peer.Available == 0,
                ref failures);

            Console.WriteLine(
                $"=== PRIVATE_TOWN_AREA result: failures={failures} ===");
            return failures == 0 ? 0 : 1;
        }

        private static void PrepareTownPresence(
            EnhancedClientSession session,
            byte townId,
            byte areaId)
        {
            session.Player.CurTownId = townId;
            session.Player.CurAreaId = areaId;
            session.Player.UserState = 0;
            session.Player.TownPresenceReady = true;
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine((condition ? "[PASS] " : "[FAIL] ") + name);
            if (!condition)
                failures++;
        }

        private sealed class ConnectedSession : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _server;

            private ConnectedSession(
                TcpListener listener,
                TcpClient server,
                TcpClient peer,
                EnhancedClientSession session)
            {
                _listener = listener;
                _server = server;
                Peer = peer;
                Session = session;
            }

            internal TcpClient Peer { get; }
            internal EnhancedClientSession Session { get; }

            internal static ConnectedSession Create(int characterId)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var peer = new TcpClient();
                var connect = peer.ConnectAsync(
                    IPAddress.Loopback,
                    endpoint.Port);
                var server = listener.AcceptTcpClient();
                connect.GetAwaiter().GetResult();
                var session = new EnhancedClientSession(
                    server,
                    new GamePacketHeader(),
                    GameNetworkConfig.NormalGamePort);
                session.Player.CharacterId = characterId;
                session.Player.UserId = checked((ushort)characterId);
                return new ConnectedSession(
                    listener,
                    server,
                    peer,
                    session);
            }

            public void Dispose()
            {
                Session.Close();
                _server.Dispose();
                Peer.Dispose();
                _listener.Stop();
            }
        }
    }
}
