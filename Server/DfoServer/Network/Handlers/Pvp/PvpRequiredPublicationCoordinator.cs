using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers
{
    internal sealed class PvpRequiredPublicationCoordinator : IDisposable
    {
        private readonly SemaphoreSlim _roomPublicationGate;
        private readonly Func<
            EnhancedClientSession,
            byte[],
            CancellationToken,
            Task> _sendPacket;
        private readonly Action<EnhancedClientSession> _closeSession;
        private readonly TimeSpan _queuedPublicationTimeout;
        private readonly object _tailSync = new object();
        private readonly Dictionary<Guid, Task> _publicationTails =
            new Dictionary<Guid, Task>();
        private readonly Dictionary<
            Guid,
            TaskCompletionSource<bool>> _directHandshakeBarriers =
                new Dictionary<Guid, TaskCompletionSource<bool>>();
        private bool _disposed;

        internal PvpRequiredPublicationCoordinator(
            SemaphoreSlim roomPublicationGate,
            Func<
                EnhancedClientSession,
                byte[],
                CancellationToken,
                Task> sendPacket,
            TimeSpan queuedPublicationTimeout,
            TimeSpan directHandshakeTimeout,
            Action<EnhancedClientSession> closeSession = null)
        {
            _roomPublicationGate =
                roomPublicationGate
                ?? throw new ArgumentNullException(
                    nameof(roomPublicationGate));
            _sendPacket =
                sendPacket
                ?? throw new ArgumentNullException(nameof(sendPacket));
            if (queuedPublicationTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(queuedPublicationTimeout));
            }
            if (directHandshakeTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(directHandshakeTimeout));
            }

            _queuedPublicationTimeout = queuedPublicationTimeout;
            DirectHandshakeTimeout = directHandshakeTimeout;
            _closeSession = closeSession ?? (session => session.Close());
        }

        internal TimeSpan DirectHandshakeTimeout { get; }

        internal int ActiveTailCount
        {
            get
            {
                lock (_tailSync)
                    return _publicationTails.Count;
            }
        }

        internal int DirectHandshakeCount
        {
            get
            {
                lock (_tailSync)
                    return _directHandshakeBarriers.Count;
            }
        }

        internal Task QueueRequired(
            IReadOnlyList<EnhancedClientSession> targets,
            int listenerPort,
            params byte[][] packets)
        {
            if (targets == null)
                throw new ArgumentNullException(nameof(targets));
            if (targets.Count == 0 ||
                packets == null ||
                packets.Length == 0)
            {
                return Task.CompletedTask;
            }

            var queued = new List<Task>(targets.Count);
            var tailsToClean = new List<KeyValuePair<Guid, Task>>(
                targets.Count);
            lock (_tailSync)
            {
                if (_disposed)
                    return Task.CompletedTask;

                foreach (var target in targets)
                {
                    if (target == null)
                        continue;

                    var sessionId = target.SessionId;
                    var previous =
                        _publicationTails.TryGetValue(
                            sessionId,
                            out var currentTail)
                            ? currentTail
                            : Task.CompletedTask;
                    var next = SendQueuedRequiredAsync(
                        previous,
                        target,
                        listenerPort,
                        packets);
                    _publicationTails[sessionId] = next;
                    if (!_directHandshakeBarriers.ContainsKey(sessionId))
                        queued.Add(next);
                    tailsToClean.Add(
                        new KeyValuePair<Guid, Task>(sessionId, next));
                }
            }

            foreach (var tail in tailsToClean)
            {
                _ = RemovePublicationTailAsync(
                    tail.Key,
                    tail.Value);
            }

            return queued.Count == 0
                ? Task.CompletedTask
                : Task.WhenAll(queued);
        }

        internal async Task SendRequiredSequenceAsync(
            EnhancedClientSession session,
            IEnumerable<byte[]> packets,
            CancellationToken cancellationToken)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            foreach (var packet in packets ?? Enumerable.Empty<byte[]>())
            {
                if (packet == null || packet.Length == 0)
                {
                    throw new InvalidOperationException(
                        "required PvP packet is empty");
                }

                await _sendPacket(
                        session,
                        packet,
                        cancellationToken)
                    .WaitAsync(cancellationToken);
            }
        }

        // The caller owns the room publication gate. The coordinator owns
        // the per-session tail lock so gate-external lifecycle publications
        // cannot race this barrier or each other.
        internal void ReserveDirectHandshakeUnderGate(
            EnhancedClientSession session,
            out Task precedingPublication,
            out TaskCompletionSource<bool> barrier)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            KeyValuePair<Guid, Task> tailToClean;
            lock (_tailSync)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(
                        nameof(PvpRequiredPublicationCoordinator));
                }

                var sessionId = session.SessionId;
                if (_directHandshakeBarriers.ContainsKey(sessionId))
                {
                    throw new InvalidOperationException(
                        "PvP direct handshake is already reserved");
                }

                precedingPublication =
                    _publicationTails.TryGetValue(
                        sessionId,
                        out var currentTail)
                        ? currentTail
                        : Task.CompletedTask;
                barrier = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _directHandshakeBarriers.Add(sessionId, barrier);
                _publicationTails[sessionId] = barrier.Task;
                tailToClean =
                    new KeyValuePair<Guid, Task>(
                        sessionId,
                        barrier.Task);
            }

            _ = RemovePublicationTailAsync(
                tailToClean.Key,
                tailToClean.Value);
        }

        internal void CompleteDirectHandshake(
            EnhancedClientSession session,
            TaskCompletionSource<bool> barrier)
        {
            if (barrier == null)
                return;

            lock (_tailSync)
            {
                if (session != null &&
                    _directHandshakeBarriers.TryGetValue(
                        session.SessionId,
                        out var currentBarrier) &&
                    ReferenceEquals(currentBarrier, barrier))
                {
                    _directHandshakeBarriers.Remove(session.SessionId);
                }
                barrier.TrySetResult(true);
            }
        }

        private async Task SendQueuedRequiredAsync(
            Task previous,
            EnhancedClientSession target,
            int listenerPort,
            IReadOnlyList<byte[]> packets)
        {
            using var publicationTimeout =
                new CancellationTokenSource(_queuedPublicationTimeout);
            try
            {
                await previous.WaitAsync(publicationTimeout.Token);
                await _roomPublicationGate.WaitAsync(
                    publicationTimeout.Token);
                _roomPublicationGate.Release();
                await SendRequiredSequenceAsync(
                    target,
                    packets,
                    publicationTimeout.Token);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[GameProtocol] PvP required publication failed: " +
                    $"listener={listenerPort} " +
                    $"cid={target.Player?.CharacterId ?? 0} " +
                    $"error={ex.GetType().Name}: {ex.Message}");
                try
                {
                    _closeSession(target);
                }
                catch (Exception closeError)
                {
                    FileLogger.Log(
                        "[GameProtocol] PvP required publication close " +
                        $"failed: listener={listenerPort} " +
                        $"cid={target.Player?.CharacterId ?? 0} " +
                        $"error={closeError.GetType().Name}: " +
                        closeError.Message);
                }
            }
        }

        private async Task RemovePublicationTailAsync(
            Guid sessionId,
            Task completedTail)
        {
            try
            {
                await completedTail;
            }
            finally
            {
                lock (_tailSync)
                {
                    if (_publicationTails.TryGetValue(
                            sessionId,
                            out var currentTail) &&
                        ReferenceEquals(currentTail, completedTail))
                    {
                        _publicationTails.Remove(sessionId);
                    }
                }
            }
        }

        public void Dispose()
        {
            TaskCompletionSource<bool>[] barriers;
            lock (_tailSync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                barriers = _directHandshakeBarriers.Values.ToArray();
                _directHandshakeBarriers.Clear();
            }

            foreach (var barrier in barriers)
                barrier.TrySetResult(true);
        }
    }
}
