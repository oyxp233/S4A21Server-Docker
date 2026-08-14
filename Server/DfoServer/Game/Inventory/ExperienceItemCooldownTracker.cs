using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DfoServer.Game.Inventory
{
    internal sealed class ExperienceItemCooldownTracker
    {
        private const int CleanupInterval = 64;
        private const long Pending = long.MaxValue;
        private readonly object _sync = new object();
        private readonly Dictionary<string, long> _expirations = new Dictionary<string, long>();
        private readonly Func<long> _timestampProvider;
        private readonly long _timestampFrequency;
        private int _requestsUntilCleanup = CleanupInterval;

        internal ExperienceItemCooldownTracker()
            : this(Stopwatch.GetTimestamp, Stopwatch.Frequency)
        {
        }

        internal ExperienceItemCooldownTracker(Func<long> timestampProvider, long timestampFrequency)
        {
            _timestampProvider = timestampProvider ?? throw new ArgumentNullException(nameof(timestampProvider));
            if (timestampFrequency <= 0)
                throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
            _timestampFrequency = timestampFrequency;
        }

        internal bool TryReserve(
            int characterId,
            ExperienceItemDefinition definition,
            out ExperienceItemCooldownReservation reservation,
            out int remainingMilliseconds)
        {
            if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            reservation = null;
            remainingMilliseconds = 0;
            if (definition.CooldownMilliseconds <= 0)
                return true;

            var key = BuildKey(characterId, definition);
            lock (_sync)
            {
                var now = _timestampProvider();
                if (--_requestsUntilCleanup <= 0)
                {
                    RemoveExpired(now);
                    _requestsUntilCleanup = CleanupInterval;
                }

                if (_expirations.TryGetValue(key, out var expiration)
                    && (expiration == Pending || expiration > now))
                {
                    var remaining = expiration == Pending
                        ? definition.CooldownMilliseconds
                        : Math.Ceiling((expiration - now) * 1000d / _timestampFrequency);
                    remainingMilliseconds = (int)Math.Max(1d, Math.Min(int.MaxValue, remaining));
                    return false;
                }

                _expirations[key] = Pending;
                reservation = new ExperienceItemCooldownReservation(
                    this, key, definition.CooldownMilliseconds);
                return true;
            }
        }

        internal bool Commit(string key, int cooldownMilliseconds)
        {
            lock (_sync)
            {
                if (!_expirations.TryGetValue(key, out var expiration) || expiration != Pending)
                    return false;
                var duration = (long)Math.Ceiling(
                    cooldownMilliseconds * (double)_timestampFrequency / 1000d);
                _expirations[key] = _timestampProvider() + Math.Max(1L, duration);
                return true;
            }
        }

        internal void Release(string key)
        {
            lock (_sync)
            {
                if (_expirations.TryGetValue(key, out var expiration) && expiration == Pending)
                    _expirations.Remove(key);
            }
        }

        private static string BuildKey(int characterId, ExperienceItemDefinition definition)
        {
            var scope = string.IsNullOrWhiteSpace(definition.CooldownGroup)
                ? "item:" + definition.ItemTemplateId
                : "group:" + definition.CooldownGroup.Trim().ToLowerInvariant();
            return characterId + ":" + scope;
        }

        private void RemoveExpired(long now)
        {
            var expired = new List<string>();
            foreach (var pair in _expirations)
                if (pair.Value != Pending && pair.Value <= now)
                    expired.Add(pair.Key);
            foreach (var key in expired)
                _expirations.Remove(key);
        }
    }

    internal sealed class ExperienceItemCooldownReservation : IDisposable
    {
        private readonly ExperienceItemCooldownTracker _owner;
        private readonly string _key;
        private readonly int _cooldownMilliseconds;
        private int _state;

        internal ExperienceItemCooldownReservation(
            ExperienceItemCooldownTracker owner, string key, int cooldownMilliseconds)
        {
            _owner = owner;
            _key = key;
            _cooldownMilliseconds = cooldownMilliseconds;
        }

        internal void Commit()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return;
            try
            {
                if (!_owner.Commit(_key, _cooldownMilliseconds))
                    throw new InvalidOperationException("cooldown reservation is no longer active");
            }
            catch
            {
                _owner.Release(_key);
                Interlocked.Exchange(ref _state, 2);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, 2) == 0)
                _owner.Release(_key);
        }
    }
}
