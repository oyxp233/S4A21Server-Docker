using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Network;

namespace DfoServer.Game.Session
{
    public sealed class CharacterTransitionCoordinator
    {
        private readonly ISessionDirectory _sessions;
        private readonly object _gatesLock = new object();
        private readonly Dictionary<int, GateEntry> _gates =
            new Dictionary<int, GateEntry>();

        internal CharacterTransitionCoordinator(
            ISessionDirectory sessions)
        {
            _sessions = sessions ??
                throw new ArgumentNullException(nameof(sessions));
        }

        internal bool IsCurrent(EnhancedClientSession session)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            return characterId > 0 &&
                   _sessions.TryGet(characterId, out var current) &&
                   ReferenceEquals(current, session);
        }

        internal async Task<bool> RunIfCurrentAsync(
            EnhancedClientSession session,
            Func<Task> transition)
        {
            if (transition == null)
                throw new ArgumentNullException(nameof(transition));

            using (var lease = await AcquireIfCurrentAsync(session))
            {
                if (lease == null)
                    return false;

                await transition();
                return true;
            }
        }

        internal async Task<bool> RunIfBothCurrentAsync(
            EnhancedClientSession left,
            EnhancedClientSession right,
            Func<Task> transition)
        {
            if (transition == null)
                throw new ArgumentNullException(nameof(transition));

            var leftCharacterId =
                left?.Player?.CharacterId ?? 0;
            var rightCharacterId =
                right?.Player?.CharacterId ?? 0;
            if (leftCharacterId <= 0 ||
                rightCharacterId <= 0 ||
                leftCharacterId == rightCharacterId)
            {
                return false;
            }

            var first = leftCharacterId < rightCharacterId
                ? left
                : right;
            var second = ReferenceEquals(first, left)
                ? right
                : left;
            using (var firstLease =
                   await AcquireIfCurrentAsync(first))
            {
                if (firstLease == null)
                    return false;
                using (var secondLease =
                       await AcquireIfCurrentAsync(second))
                {
                    if (secondLease == null)
                        return false;
                    await transition();
                    return true;
                }
            }
        }

        internal async Task<IDisposable> AcquireIfCurrentAsync(
            EnhancedClientSession session)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0)
                return null;

            var lease = await AcquireAsync(characterId);
            if (IsCurrent(session))
                return lease;

            lease.Dispose();
            return null;
        }

        internal async Task<IDisposable> AcquireAsync(
            int characterId,
            CancellationToken cancellationToken = default)
        {
            GateEntry entry;
            lock (_gatesLock)
            {
                if (!_gates.TryGetValue(characterId, out entry))
                {
                    entry = new GateEntry();
                    _gates.Add(characterId, entry);
                }
                entry.ReferenceCount++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken);
                return new GateLease(this, characterId, entry);
            }
            catch
            {
                ReleaseReference(characterId, entry);
                throw;
            }
        }

        private void Release(int characterId, GateEntry entry)
        {
            entry.Semaphore.Release();
            ReleaseReference(characterId, entry);
        }

        private void ReleaseReference(
            int characterId,
            GateEntry entry)
        {
            lock (_gatesLock)
            {
                entry.ReferenceCount--;
                if (entry.ReferenceCount == 0 &&
                    _gates.TryGetValue(
                        characterId, out var current) &&
                    ReferenceEquals(current, entry))
                {
                    _gates.Remove(characterId);
                    entry.Semaphore.Dispose();
                }
            }
        }

        private sealed class GateEntry
        {
            internal readonly SemaphoreSlim Semaphore =
                new SemaphoreSlim(1, 1);
            internal int ReferenceCount;
        }

        private sealed class GateLease : IDisposable
        {
            private CharacterTransitionCoordinator _owner;
            private readonly int _characterId;
            private readonly GateEntry _entry;

            internal GateLease(
                CharacterTransitionCoordinator owner,
                int characterId,
                GateEntry entry)
            {
                _owner = owner;
                _characterId = characterId;
                _entry = entry;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.Release(_characterId, _entry);
            }
        }
    }
}
