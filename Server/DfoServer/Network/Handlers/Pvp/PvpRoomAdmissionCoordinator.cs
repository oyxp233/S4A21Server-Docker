using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class PvpRoomAdmissionCoordinator : IDisposable
    {
        private readonly ConcurrentDictionary<int, Guid>
            _pendingRoomJoinSessions =
                new ConcurrentDictionary<int, Guid>();
        private readonly ConcurrentDictionary<
            int,
            TaskCompletionSource<bool>> _pendingRoomJoinCompletions =
                new ConcurrentDictionary<
                    int,
                    TaskCompletionSource<bool>>();
        private readonly ConcurrentDictionary<Guid, PendingRoomInvite>
            _pendingRoomInvites =
                new ConcurrentDictionary<Guid, PendingRoomInvite>();
        private readonly object _stateSync = new object();
        private int _disposeStarted;

        // Relay generation validation consumes this exact roomId -> sessionId
        // map. Ownership of its lifecycle remains here.
        internal ConcurrentDictionary<int, Guid>
            PendingRoomJoinSessions => _pendingRoomJoinSessions;

        internal bool TryReservePendingJoin(
            int roomId,
            Guid sessionId)
        {
            lock (_stateSync)
            {
                if (VolatileDisposed() ||
                    !_pendingRoomJoinSessions.TryAdd(
                        roomId,
                        sessionId))
                {
                    return false;
                }

                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (_pendingRoomJoinCompletions.TryAdd(
                        roomId,
                        completion))
                {
                    return true;
                }

                ((ICollection<KeyValuePair<int, Guid>>)
                    _pendingRoomJoinSessions)
                    .Remove(
                        new KeyValuePair<int, Guid>(
                            roomId,
                            sessionId));
                return false;
            }
        }

        internal bool TryGetPendingJoinCompletion(
            int roomId,
            out Task completion)
        {
            if (_pendingRoomJoinCompletions.TryGetValue(
                    roomId,
                    out var source))
            {
                completion = source.Task;
                return true;
            }

            completion = null;
            return false;
        }

        internal bool TryFindPendingJoinForSession(
            Guid sessionId,
            out int roomId,
            out Task completion)
        {
            foreach (var pending in _pendingRoomJoinSessions.ToArray())
            {
                if (pending.Value != sessionId ||
                    !TryGetPendingJoinCompletion(
                        pending.Key,
                        out completion))
                {
                    continue;
                }

                roomId = pending.Key;
                return true;
            }

            roomId = 0;
            completion = null;
            return false;
        }

        internal bool CompletePendingJoin(
            int roomId,
            Guid sessionId)
        {
            lock (_stateSync)
            {
                var expected =
                    new KeyValuePair<int, Guid>(roomId, sessionId);
                if (!((ICollection<KeyValuePair<int, Guid>>)
                        _pendingRoomJoinSessions)
                    .Remove(expected))
                {
                    return false;
                }

                if (_pendingRoomJoinCompletions.TryRemove(
                        roomId,
                        out var completion))
                {
                    completion.TrySetResult(true);
                }
                return true;
            }
        }

        internal IReadOnlyList<KeyValuePair<int, Guid>>
            SnapshotPendingJoins()
        {
            return _pendingRoomJoinSessions.ToArray();
        }

        internal bool TryStorePendingInvite(
            Guid targetSessionId,
            PendingRoomInvite invitation)
        {
            lock (_stateSync)
            {
                if (invitation == null || VolatileDisposed())
                    return false;

                _pendingRoomInvites[targetSessionId] = invitation;
                return true;
            }
        }

        internal bool TryGetPendingInvite(
            Guid targetSessionId,
            out PendingRoomInvite invitation)
        {
            return _pendingRoomInvites.TryGetValue(
                targetSessionId,
                out invitation);
        }

        internal bool TryRemovePendingInvite(
            Guid targetSessionId,
            PendingRoomInvite invitation)
        {
            lock (_stateSync)
            {
                if (invitation == null)
                    return false;

                return ((ICollection<
                    KeyValuePair<Guid, PendingRoomInvite>>)
                    _pendingRoomInvites)
                    .Remove(
                        new KeyValuePair<Guid, PendingRoomInvite>(
                            targetSessionId,
                            invitation));
            }
        }

        internal void RemovePendingInvitesForSession(Guid sessionId)
        {
            lock (_stateSync)
            {
                _pendingRoomInvites.TryRemove(sessionId, out _);
                foreach (var pending in _pendingRoomInvites.ToArray())
                {
                    if (pending.Value.InviterSessionId == sessionId)
                    {
                        ((ICollection<
                            KeyValuePair<Guid, PendingRoomInvite>>)
                            _pendingRoomInvites)
                            .Remove(pending);
                    }
                }
            }
        }

        internal int PendingInviteCount => _pendingRoomInvites.Count;

        internal int PendingJoinCount => _pendingRoomJoinSessions.Count;

        private bool VolatileDisposed()
        {
            return Volatile.Read(ref _disposeStarted) != 0;
        }

        public void Dispose()
        {
            lock (_stateSync)
            {
                if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                    return;

                foreach (var completion in
                         _pendingRoomJoinCompletions.Values)
                {
                    completion.TrySetResult(true);
                }
                _pendingRoomJoinCompletions.Clear();
                _pendingRoomJoinSessions.Clear();
                _pendingRoomInvites.Clear();
            }
        }
    }

    internal sealed class PendingRoomInvite
    {
        internal PendingRoomInvite(
            Guid inviterSessionId,
            ushort roomId,
            Guid ownerSessionId,
            Guid roomGenerationId,
            int listenerPort,
            int peerToken,
            DateTime expiresAtUtc)
        {
            InviterSessionId = inviterSessionId;
            RoomId = roomId;
            OwnerSessionId = ownerSessionId;
            RoomGenerationId = roomGenerationId;
            ListenerPort = listenerPort;
            PeerToken = peerToken;
            ExpiresAtUtc = expiresAtUtc;
        }

        internal Guid InviterSessionId { get; }

        internal ushort RoomId { get; }

        internal Guid OwnerSessionId { get; }

        internal Guid RoomGenerationId { get; }

        internal int ListenerPort { get; }

        internal int PeerToken { get; }

        internal DateTime ExpiresAtUtc { get; }
    }
}
