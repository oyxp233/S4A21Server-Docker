using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DfoServer.Game.Pvp;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Pvp;

namespace DfoServer.SelfTests
{
    public static class PvpRelayLifecycleSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            var connections = new List<ConnectedSession>();
            var roomGate = new SemaphoreSlim(1, 1);
            try
            {
                var portBase = FindFreeUdpRange(8);
                using var relay = new PartyUdpRelay(
                    "127.0.0.1",
                    portBase,
                    8,
                    "pvp-lifecycle-selftest");
                var rooms = new FreeDuelRoomRegistry();
                var pending = new ConcurrentDictionary<int, Guid>();
                var sessions = new List<EnhancedClientSession>();
                using var coordinator = new PvpRelayLifecycleCoordinator(
                    relay,
                    rooms,
                    roomGate,
                    pending,
                    room => ResolveMembers(room, sessions));

                if (!MakePvpRoomRequest.TryParse(
                        new byte[] { 0x06, 0, 0, 0, 0 },
                        out var makeRequest,
                        out _) ||
                    !EnterPvpRoomRequest.TryParse(
                        new byte[] { 0, 0, 0 },
                        out var enterRequest,
                        out _))
                {
                    throw new InvalidOperationException(
                        "PvP room requests did not parse");
                }

                var owner = CreateSession(
                    connections,
                    sessions,
                    6301,
                    6301);
                var member = CreateSession(
                    connections,
                    sessions,
                    6302,
                    6302);
                if (!rooms.TryCreate(
                        GameNetworkConfig.FreeDuelGamePort,
                        owner.Player.CharacterId,
                        owner.SessionId,
                        owner.Player.UserId,
                        makeRequest,
                        out var firstRoom,
                        out _) ||
                    !rooms.TryJoin(
                        GameNetworkConfig.FreeDuelGamePort,
                        member.Player.CharacterId,
                        member.SessionId,
                        member.Player.UserId,
                        enterRequest,
                        out firstRoom,
                        out _,
                        out _))
                {
                    throw new InvalidOperationException(
                        "first PvP room fixture failed");
                }

                var firstMembers = new[] { owner, member };
                var firstSync = coordinator.TrySyncGenerationAsync(
                        firstRoom,
                        firstMembers,
                        firstMembers,
                        "initial")
                    .GetAwaiter()
                    .GetResult();
                var relayRoomId =
                    PvpRelayLifecycleCoordinator.ToRelayRoomId(
                        firstRoom.RoomId);
                Check(
                    "exact room generation publishes one secure relay matrix",
                    firstSync.Success &&
                    firstSync.GenerationCurrent &&
                    PvpRelayLifecycleCoordinator.IsSecureSnapshotForRoom(
                        firstRoom,
                        firstSync.Snapshot) &&
                    relay.GetPort(
                        relayRoomId,
                        owner.Player.UserId,
                        member.Player.UserId) > 0,
                    ref failures);

                var rejected = coordinator.TrySyncGenerationAsync(
                        firstRoom,
                        firstMembers,
                        new[] { owner, owner },
                        "invalid-binding")
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "invalid current-generation bindings fail closed",
                    !rejected.Success &&
                    rejected.GenerationCurrent &&
                    relay.RoomCount == 0,
                    ref failures);

                var restored = coordinator.TrySyncGenerationAsync(
                        firstRoom,
                        firstMembers,
                        firstMembers,
                        "restore")
                    .GetAwaiter()
                    .GetResult();
                if (!restored.Success ||
                    !rooms.TryTakeOwnedRoomForRemoval(
                        owner.Player.CharacterId,
                        owner.SessionId,
                        out var retired) ||
                    !rooms.ReleaseRemovedRoomId(retired))
                {
                    throw new InvalidOperationException(
                        "first room retirement failed");
                }

                var replacementOwner = CreateSession(
                    connections,
                    sessions,
                    6401,
                    6401);
                var replacementMember = CreateSession(
                    connections,
                    sessions,
                    6402,
                    6402);
                if (!rooms.TryCreate(
                        GameNetworkConfig.FreeDuelGamePort,
                        replacementOwner.Player.CharacterId,
                        replacementOwner.SessionId,
                        replacementOwner.Player.UserId,
                        makeRequest,
                        out var replacementRoom,
                        out _) ||
                    replacementRoom.RoomId != firstRoom.RoomId ||
                    !rooms.TryJoin(
                        GameNetworkConfig.FreeDuelGamePort,
                        replacementMember.Player.CharacterId,
                        replacementMember.SessionId,
                        replacementMember.Player.UserId,
                        enterRequest,
                        out replacementRoom,
                        out _,
                        out _))
                {
                    throw new InvalidOperationException(
                        "replacement PvP room fixture failed");
                }

                var replacementMembers =
                    new[] { replacementOwner, replacementMember };
                var replacementSync = coordinator.TrySyncGenerationAsync(
                        replacementRoom,
                        replacementMembers,
                        replacementMembers,
                        "replacement")
                    .GetAwaiter()
                    .GetResult();
                var replacementPort = relay.GetPort(
                    relayRoomId,
                    replacementOwner.Player.UserId,
                    replacementMember.Player.UserId);
                var staleSync = coordinator.TrySyncGenerationAsync(
                        firstRoom,
                        firstMembers,
                        firstMembers,
                        "stale-old-generation")
                    .GetAwaiter()
                    .GetResult();
                coordinator.ReconcileGenerationAsync(
                        firstRoom,
                        "stale-old-reconcile")
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "stale generation cannot close a recycled relay room",
                    replacementSync.Success &&
                    replacementPort > 0 &&
                    !staleSync.Success &&
                    !staleSync.GenerationCurrent &&
                    relay.GetPort(
                        relayRoomId,
                        replacementOwner.Player.UserId,
                        replacementMember.Player.UserId) == replacementPort,
                    ref failures);

                var staleClose = coordinator.CloseRoomAsync(
                        firstRoom,
                        "stale-retirement")
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "stale retirement close cannot close recycled relay",
                    !staleClose &&
                    relay.GetPort(
                        relayRoomId,
                        replacementOwner.Player.UserId,
                        replacementMember.Player.UserId) ==
                        replacementPort,
                    ref failures);

                var closed = coordinator.CloseRoomAsync(
                        replacementRoom,
                        "retirement")
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "retirement close barrier releases the relay matrix",
                    closed && relay.RoomCount == 0,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] PvP relay lifecycle self-test threw: " + ex);
                failures++;
            }
            finally
            {
                roomGate.Dispose();
                foreach (var connection in connections)
                    connection.Dispose();
            }

            Console.WriteLine(
                failures == 0
                    ? "PvpRelayLifecycleSelfTest OK"
                    : $"PvpRelayLifecycleSelfTest FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }

        private static EnhancedClientSession CreateSession(
            ICollection<ConnectedSession> connections,
            ICollection<EnhancedClientSession> sessions,
            int characterId,
            ushort userId)
        {
            var connection = ConnectedSession.Create();
            connection.Session.Player.CharacterId = characterId;
            connection.Session.Player.UserId = userId;
            connections.Add(connection);
            sessions.Add(connection.Session);
            return connection.Session;
        }

        private static IReadOnlyList<EnhancedClientSession> ResolveMembers(
            FreeDuelRoom room,
            IReadOnlyCollection<EnhancedClientSession> sessions)
        {
            var result = new List<EnhancedClientSession>();
            for (var seat = 0; seat < FreeDuelRoom.SeatCount; seat++)
            {
                if (!room.IsOccupiedSeat(seat))
                    continue;

                var member = sessions.SingleOrDefault(
                    candidate =>
                        candidate.SessionId ==
                            room.GetSeatSessionId(seat) &&
                        candidate.Player.CharacterId ==
                            room.GetSeatCharacterId(seat) &&
                        candidate.Player.UserId ==
                            room.GetSeatUserId(seat) &&
                        candidate.ListenerPort == room.ListenerPort);
                if (member == null)
                {
                    throw new InvalidOperationException(
                        $"room {room.RoomId} seat {seat} is not live");
                }
                result.Add(member);
            }
            return result;
        }

        private static int FindFreeUdpRange(int count)
        {
            for (var portBase = 36000;
                 portBase <= 60000 - count;
                 portBase += count)
            {
                var sockets = new List<UdpClient>();
                try
                {
                    for (var offset = 0; offset < count; offset++)
                    {
                        var socket =
                            new UdpClient(AddressFamily.InterNetwork);
                        socket.Client.ExclusiveAddressUse = true;
                        socket.Client.Bind(
                            new IPEndPoint(
                                IPAddress.Loopback,
                                portBase + offset));
                        sockets.Add(socket);
                    }
                    return portBase;
                }
                catch (SocketException)
                {
                }
                finally
                {
                    foreach (var socket in sockets)
                        socket.Dispose();
                }
            }

            throw new InvalidOperationException(
                "No free UDP range for PvP relay self-test.");
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

        private sealed class ConnectedSession : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _peer;
            private readonly TcpClient _server;

            private ConnectedSession(
                TcpListener listener,
                TcpClient peer,
                TcpClient server,
                EnhancedClientSession session)
            {
                _listener = listener;
                _peer = peer;
                _server = server;
                Session = session;
            }

            internal EnhancedClientSession Session { get; }

            internal static ConnectedSession Create()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port =
                    ((IPEndPoint)listener.LocalEndpoint).Port;
                var peer = new TcpClient();
                var connect = peer.ConnectAsync(
                    IPAddress.Loopback,
                    port);
                var server = listener.AcceptTcpClient();
                connect.GetAwaiter().GetResult();
                return new ConnectedSession(
                    listener,
                    peer,
                    server,
                    new EnhancedClientSession(
                        server,
                        new GamePacketHeader(),
                        GameNetworkConfig.FreeDuelGamePort));
            }

            public void Dispose()
            {
                _peer.Dispose();
                _server.Dispose();
                _listener.Stop();
            }
        }
    }
}
