using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers
{
    internal readonly struct PvpRelaySyncResult
    {
        internal PvpRelaySyncResult(
            bool success,
            bool generationCurrent,
            PartyUdpRelay.RoomSnapshot snapshot)
        {
            Success = success;
            GenerationCurrent = generationCurrent;
            Snapshot = snapshot;
        }

        internal bool Success { get; }

        internal bool GenerationCurrent { get; }

        internal PartyUdpRelay.RoomSnapshot Snapshot { get; }
    }

    internal sealed class PvpRelayLifecycleCoordinator : IDisposable
    {
        private readonly PartyUdpRelay _relay;
        private readonly FreeDuelRoomRegistry _rooms;
        private readonly SemaphoreSlim _roomPublicationGate;
        private readonly ConcurrentDictionary<int, Guid>
            _pendingRoomJoinSessions;
        private readonly Func<
            FreeDuelRoom,
            IReadOnlyList<EnhancedClientSession>> _getLiveRoomMembers;
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _roomGates =
            new ConcurrentDictionary<int, SemaphoreSlim>();
        private volatile bool _disposed;
        private int _disposeStarted;

        internal PvpRelayLifecycleCoordinator(
            PartyUdpRelay relay,
            FreeDuelRoomRegistry rooms,
            SemaphoreSlim roomPublicationGate,
            ConcurrentDictionary<int, Guid> pendingRoomJoinSessions,
            Func<FreeDuelRoom, IReadOnlyList<EnhancedClientSession>>
                getLiveRoomMembers)
        {
            _relay = relay;
            _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
            _roomPublicationGate = roomPublicationGate ??
                throw new ArgumentNullException(nameof(roomPublicationGate));
            _pendingRoomJoinSessions = pendingRoomJoinSessions ??
                throw new ArgumentNullException(
                    nameof(pendingRoomJoinSessions));
            _getLiveRoomMembers = getLiveRoomMembers ??
                throw new ArgumentNullException(nameof(getLiveRoomMembers));
        }

        internal Func<string, Task> AfterRoomGateAcquiredForTest
        {
            get;
            set;
        }

        internal async Task<PvpRelaySyncResult> TrySyncGenerationAsync(
            FreeDuelRoom expectedRoom,
            IReadOnlyList<EnhancedClientSession> expectedRegistryMembers,
            IReadOnlyList<EnhancedClientSession> desiredRelayMembers,
            string phase,
            ushort resetOwnerUserId = 0,
            bool requireExactRevision = true,
            long? expectedRegistryRevision = null,
            Guid pendingJoinSessionId = default,
            bool closeOnFailure = true)
        {
            if (_relay == null)
                return new PvpRelaySyncResult(true, true, null);
            if (expectedRoom == null || _disposed)
                return new PvpRelaySyncResult(false, false, null);

            var relayRoomId = ToRelayRoomId(expectedRoom.RoomId);
            var roomGate = _roomGates.GetOrAdd(
                relayRoomId,
                _ => new SemaphoreSlim(1, 1));
            await roomGate.WaitAsync();

            try
            {
                if (AfterRoomGateAcquiredForTest != null)
                    await AfterRoomGateAcquiredForTest(phase);
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (_disposed)
                        return new PvpRelaySyncResult(false, false, null);
                    var currentRoom = FindCurrentRoom(expectedRoom);
                    if (currentRoom == null ||
                        currentRoom.OwnerSessionId !=
                            expectedRoom.OwnerSessionId ||
                        currentRoom.GenerationId !=
                            expectedRoom.GenerationId ||
                        requireExactRevision &&
                        currentRoom.Revision !=
                            (expectedRegistryRevision ??
                             expectedRoom.Revision))
                    {
                        FileLogger.Log(
                            "[GameProtocol] PvP generation relay sync " +
                            $"skipped: room={expectedRoom.RoomId} " +
                            $"phase={phase}");
                        return new PvpRelaySyncResult(false, false, null);
                    }

                    if (pendingJoinSessionId != Guid.Empty &&
                        (!_pendingRoomJoinSessions.TryGetValue(
                             expectedRoom.RoomId,
                             out var pendingSessionId) ||
                         pendingSessionId != pendingJoinSessionId))
                    {
                        FileLogger.Log(
                            "[GameProtocol] PvP predicted relay sync " +
                            $"skipped: room={expectedRoom.RoomId} " +
                            $"phase={phase} reason=pending-generation");
                        return new PvpRelaySyncResult(false, false, null);
                    }

                    var sameRegistryGeneration =
                        pendingJoinSessionId != Guid.Empty
                            ? SameRegistryMemberGeneration(
                                currentRoom,
                                expectedRegistryMembers)
                            : TryGetLiveRegistryGeneration(
                                currentRoom,
                                expectedRegistryMembers);
                    if (!sameRegistryGeneration)
                    {
                        FileLogger.Log(
                            "[GameProtocol] PvP generation relay sync " +
                            $"skipped: room={expectedRoom.RoomId} " +
                            $"phase={phase} reason=member-generation");
                        return new PvpRelaySyncResult(false, false, null);
                    }

                    var desired = desiredRelayMembers ??
                        Array.Empty<EnhancedClientSession>();
                    if (desired.Count < 2)
                    {
                        _relay.CloseRoom(relayRoomId);
                        return new PvpRelaySyncResult(true, true, null);
                    }
                    if (!TryBuildSecureBindings(desired, out var bindings))
                    {
                        if (closeOnFailure)
                            _relay.CloseRoom(relayRoomId);
                        FileLogger.Log(
                            "[GameProtocol] PvP secure relay binding " +
                            $"rejected: room={expectedRoom.RoomId} " +
                            $"phase={phase}");
                        return new PvpRelaySyncResult(false, true, null);
                    }

                    if (resetOwnerUserId != 0)
                    {
                        var resetOwner = desired.FirstOrDefault(
                            member =>
                                member?.Player?.UserId == resetOwnerUserId);
                        if (resetOwner == null)
                        {
                            if (closeOnFailure)
                                _relay.CloseRoom(relayRoomId);
                            return new PvpRelaySyncResult(false, true, null);
                        }
                        _relay.ResetMemberEndpoints(
                            relayRoomId,
                            resetOwnerUserId,
                            resetOwner.SessionId);
                    }

                    var success = _relay.TrySyncRoom(
                        relayRoomId,
                        bindings,
                        out var snapshot);
                    if (!success && closeOnFailure)
                        _relay.CloseRoom(relayRoomId);
                    return new PvpRelaySyncResult(success, true, snapshot);
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
            }
            catch (Exception ex)
            {
                var exceptionalClose =
                    (Closed: false, GenerationCurrent: false);
                if (closeOnFailure)
                {
                    exceptionalClose =
                        await CloseForExpectedGenerationUnderRoomGateAsync(
                            expectedRoom,
                            relayRoomId,
                            phase);
                }
                FileLogger.Log(
                    "[GameProtocol] PvP generation relay sync failed: " +
                    $"room={expectedRoom.RoomId} phase={phase} " +
                    $"error={ex.GetType().Name}: {ex.Message}");
                return new PvpRelaySyncResult(
                    false,
                    exceptionalClose.GenerationCurrent,
                    null);
            }
            finally
            {
                roomGate.Release();
            }
        }

        internal async Task ReconcileGenerationAsync(
            FreeDuelRoom expectedRoom,
            string phase)
        {
            if (_relay == null || expectedRoom == null || _disposed)
                return;

            var relayRoomId = ToRelayRoomId(expectedRoom.RoomId);
            var roomGate = _roomGates.GetOrAdd(
                relayRoomId,
                _ => new SemaphoreSlim(1, 1));
            await roomGate.WaitAsync();

            try
            {
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (_disposed)
                        return;
                    var currentRoom = FindCurrentRoom(expectedRoom);
                    if (currentRoom != null &&
                        (currentRoom.OwnerSessionId !=
                             expectedRoom.OwnerSessionId ||
                         currentRoom.GenerationId !=
                             expectedRoom.GenerationId))
                    {
                        FileLogger.Log(
                            "[GameProtocol] PvP generation reconcile " +
                            $"skipped recycled room={expectedRoom.RoomId} " +
                            $"phase={phase}");
                        return;
                    }

                    if (currentRoom == null)
                    {
                        _relay.CloseRoom(relayRoomId);
                        return;
                    }

                    IReadOnlyList<EnhancedClientSession> currentMembers;
                    try
                    {
                        currentMembers = _getLiveRoomMembers(currentRoom);
                    }
                    catch
                    {
                        _relay.CloseRoom(relayRoomId);
                        FileLogger.Log(
                            "[GameProtocol] PvP generation reconcile " +
                            $"closed incomplete room={expectedRoom.RoomId} " +
                            $"phase={phase}");
                        return;
                    }
                    if (currentMembers.Count < 2)
                    {
                        _relay.CloseRoom(relayRoomId);
                        return;
                    }

                    if (!TryBuildSecureBindings(
                            currentMembers,
                            out var bindings) ||
                        !_relay.TrySyncRoom(
                            relayRoomId,
                            bindings,
                            out _))
                    {
                        _relay.CloseRoom(relayRoomId);
                        FileLogger.Log(
                            "[GameProtocol] PvP generation reconcile " +
                            $"failed closed: room={expectedRoom.RoomId} " +
                            $"phase={phase}");
                    }
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
            }
            catch (Exception ex)
            {
                await CloseForExpectedGenerationUnderRoomGateAsync(
                    expectedRoom,
                    relayRoomId,
                    phase);
                FileLogger.Log(
                    "[GameProtocol] PvP generation reconcile failed " +
                    $"closed: room={expectedRoom.RoomId} phase={phase} " +
                    $"error={ex.GetType().Name}");
            }
            finally
            {
                roomGate.Release();
            }
        }

        internal async Task<bool> CloseRoomAsync(
            FreeDuelRoom expectedRoom,
            string phase)
        {
            if (_relay == null)
                return true;
            if (expectedRoom == null || _disposed)
                return false;

            var relayRoomId = ToRelayRoomId(expectedRoom.RoomId);
            var roomGate = _roomGates.GetOrAdd(
                relayRoomId,
                _ => new SemaphoreSlim(1, 1));
            var gateHeld = false;
            try
            {
                await roomGate.WaitAsync();
                gateHeld = true;
                await _roomPublicationGate.WaitAsync();
                try
                {
                    var currentRoom = FindCurrentRoom(expectedRoom);
                    if (currentRoom != null &&
                        (currentRoom.OwnerSessionId !=
                             expectedRoom.OwnerSessionId ||
                         currentRoom.GenerationId !=
                             expectedRoom.GenerationId))
                    {
                        FileLogger.Log(
                            "[GameProtocol] PvP relay close skipped " +
                            "recycled generation: " +
                            $"room={expectedRoom.RoomId} phase={phase}");
                        return false;
                    }

                    _relay.CloseRoom(relayRoomId);
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[GameProtocol] PvP relay close failed; room id " +
                    $"remains retired: room={expectedRoom.RoomId} " +
                    $"phase={phase} " +
                    $"error={ex.GetType().Name}");
                return false;
            }
            finally
            {
                if (gateHeld)
                    roomGate.Release();
            }
        }

        internal static int ToRelayRoomId(int roomId)
        {
            if (roomId < 0 ||
                roomId >= FreeDuelRoomRegistry.MaximumRooms)
            {
                throw new ArgumentOutOfRangeException(nameof(roomId));
            }
            return checked(roomId + 1);
        }

        internal static bool IsSecureSnapshotForRoom(
            FreeDuelRoom room,
            PartyUdpRelay.RoomSnapshot relaySnapshot)
        {
            return room != null &&
                   relaySnapshot != null &&
                   relaySnapshot.SecureBindings &&
                   relaySnapshot.RoomId == ToRelayRoomId(room.RoomId);
        }

        internal static bool SameRegistryMemberGeneration(
            FreeDuelRoom room,
            IReadOnlyList<EnhancedClientSession> expected)
        {
            if (room == null)
                return false;

            var expectedMembers = expected ??
                Array.Empty<EnhancedClientSession>();
            var expectedBySession =
                new Dictionary<Guid, EnhancedClientSession>();
            foreach (var member in expectedMembers)
            {
                if (member?.Player == null ||
                    member.SessionId == Guid.Empty ||
                    !expectedBySession.TryAdd(member.SessionId, member))
                {
                    return false;
                }
            }

            var occupiedCount = 0;
            for (var seat = 0; seat < FreeDuelRoom.SeatCount; seat++)
            {
                if (!room.IsOccupiedSeat(seat))
                    continue;

                occupiedCount++;
                var sessionId = room.GetSeatSessionId(seat);
                if (!expectedBySession.TryGetValue(
                        sessionId,
                        out var member) ||
                    member.ListenerPort != room.ListenerPort ||
                    member.Player.CharacterId !=
                        room.GetSeatCharacterId(seat) ||
                    member.Player.UserId != room.GetSeatUserId(seat))
                {
                    return false;
                }
            }

            return occupiedCount == expectedBySession.Count;
        }

        private bool TryGetLiveRegistryGeneration(
            FreeDuelRoom room,
            IReadOnlyList<EnhancedClientSession> expectedMembers)
        {
            try
            {
                return SameSessionGeneration(
                    _getLiveRoomMembers(room),
                    expectedMembers);
            }
            catch
            {
                return false;
            }
        }

        private async Task<(
            bool Closed,
            bool GenerationCurrent)>
            CloseForExpectedGenerationUnderRoomGateAsync(
                FreeDuelRoom expectedRoom,
                int relayRoomId,
                string phase)
        {
            await _roomPublicationGate.WaitAsync();
            try
            {
                FreeDuelRoom currentRoom;
                try
                {
                    currentRoom = FindCurrentRoom(expectedRoom);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        "[GameProtocol] PvP relay exceptional close " +
                        "could not validate generation: " +
                        $"room={expectedRoom.RoomId} phase={phase} " +
                        $"error={ex.GetType().Name}");
                    return (false, false);
                }

                if (currentRoom != null &&
                    (currentRoom.OwnerSessionId !=
                         expectedRoom.OwnerSessionId ||
                     currentRoom.GenerationId !=
                         expectedRoom.GenerationId))
                {
                    FileLogger.Log(
                        "[GameProtocol] PvP relay exceptional close " +
                        $"skipped recycled room={expectedRoom.RoomId} " +
                        $"phase={phase}");
                    return (false, false);
                }

                _relay.CloseRoom(relayRoomId);
                return (true, currentRoom != null);
            }
            finally
            {
                _roomPublicationGate.Release();
            }
        }

        private FreeDuelRoom FindCurrentRoom(FreeDuelRoom expectedRoom)
        {
            return _rooms.SnapshotForListener(expectedRoom.ListenerPort)
                .FirstOrDefault(
                    candidate => candidate.RoomId == expectedRoom.RoomId);
        }

        private static bool TryBuildSecureBindings(
            IReadOnlyList<EnhancedClientSession> members,
            out IReadOnlyList<PartyUdpRelay.MemberBinding> bindings)
        {
            bindings = null;
            var source = members ?? Array.Empty<EnhancedClientSession>();
            if (source.Count < 2 || source.Count > 8)
                return false;

            var result = new List<PartyUdpRelay.MemberBinding>(source.Count);
            var userIds = new HashSet<ushort>();
            var sessionIds = new HashSet<Guid>();
            foreach (var member in source)
            {
                if (member?.Player == null ||
                    member.Player.UserId == 0 ||
                    member.SessionId == Guid.Empty ||
                    !userIds.Add(member.Player.UserId) ||
                    !sessionIds.Add(member.SessionId))
                {
                    return false;
                }

                IPEndPoint remote;
                try
                {
                    remote = member.TcpClient?.Client?.RemoteEndPoint
                        as IPEndPoint;
                }
                catch
                {
                    return false;
                }
                if (remote == null)
                    return false;

                var address = remote.Address;
                if (address.IsIPv4MappedToIPv6)
                    address = address.MapToIPv4();
                if (address.AddressFamily != AddressFamily.InterNetwork)
                    return false;

                result.Add(
                    new PartyUdpRelay.MemberBinding(
                        member.Player.UserId,
                        member.SessionId,
                        address));
            }

            bindings = result
                .OrderBy(binding => binding.MemberKey)
                .ToArray();
            return true;
        }

        private static bool SameSessionGeneration(
            IReadOnlyList<EnhancedClientSession> current,
            IReadOnlyList<EnhancedClientSession> expected)
        {
            var currentIds =
                (current ?? Array.Empty<EnhancedClientSession>())
                    .Where(member => member != null)
                    .Select(member => member.SessionId)
                    .OrderBy(id => id)
                    .ToArray();
            var expectedIds =
                (expected ?? Array.Empty<EnhancedClientSession>())
                    .Where(member => member != null)
                    .Select(member => member.SessionId)
                    .OrderBy(id => id)
                    .ToArray();
            return currentIds.SequenceEqual(expectedIds);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                return;

            _disposed = true;
            if (_relay == null)
                return;

            foreach (var gateEntry in _roomGates.ToArray())
            {
                gateEntry.Value.Wait();
                try
                {
                    _relay.CloseRoom(gateEntry.Key);
                }
                finally
                {
                    gateEntry.Value.Release();
                }
            }
        }
    }
}
