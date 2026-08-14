using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Lottery
{
    internal sealed class LotteryOpenReservation
    {
        internal LotteryOpenReservation(
            short slotIndex,
            int sourceItemTemplateId,
            LotteryOpenPlan openPlan)
        {
            SlotIndex = slotIndex;
            SourceItemTemplateId = sourceItemTemplateId;
            OpenPlan = openPlan ?? LotteryOpenPlan.ConfirmedRegular();
        }

        internal short SlotIndex { get; }

        internal int SourceItemTemplateId { get; }

        internal LotteryOpenPlan OpenPlan { get; }

        internal IReadOnlyList<PvfLib.BoosterRewardEntry> SelectedRewards { get; set; }

        internal int ProgressRewardIndex { get; set; } = -1;

        internal bool? AppliedDoubleReward { get; set; }

        internal bool IsInProgress { get; set; }
    }

    public sealed class PendingLotteryOpen
    {
        internal PendingLotteryOpen(short slotIndex, DateTime createdAtUtc, LotteryOpenPlan openPlan)
        {
            SlotIndex = slotIndex;
            CreatedAtUtc = createdAtUtc;
            OpenPlan = openPlan;
        }

        public short SlotIndex { get; }

        public DateTime CreatedAtUtc { get; }

        public LotteryOpenPlan OpenPlan { get; }

        internal LotteryOpenReservation Reservation { get; set; }
    }

    public sealed class LotteryOpenSessionCoordinator
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

        private readonly object _sync = new object();
        private readonly Dictionary<Guid, PendingLotteryOpen> _pending
            = new Dictionary<Guid, PendingLotteryOpen>();
        private readonly TimeSpan _timeout;
        private readonly Func<DateTime> _utcNow;

        public LotteryOpenSessionCoordinator(TimeSpan? timeout = null, Func<DateTime> utcNow = null)
        {
            _timeout = timeout ?? DefaultTimeout;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public void Set(Guid sessionId, short slotIndex, LotteryOpenPlan openPlan = null)
        {
            lock (_sync)
            {
                var now = _utcNow();
                CleanupExpired(now);
                if (_pending.TryGetValue(sessionId, out var existing)
                    && existing.Reservation != null)
                {
                    return;
                }

                _pending[sessionId] = new PendingLotteryOpen(slotIndex, now, openPlan);
            }
        }

        public bool TryGet(
            Guid sessionId,
            short? expectedSlotIndex,
            out PendingLotteryOpen pending)
        {
            lock (_sync)
            {
                CleanupExpired(_utcNow());
                if (!_pending.TryGetValue(sessionId, out pending))
                    return false;

                return !expectedSlotIndex.HasValue
                    || pending.SlotIndex == expectedSlotIndex.Value;
            }
        }

        internal bool TryReserveOpen(
            Guid sessionId,
            short? expectedSlotIndex,
            Func<PendingLotteryOpen, LotteryOpenReservation> create,
            out LotteryOpenReservation reservation)
        {
            lock (_sync)
            {
                reservation = null;
                CleanupExpired(_utcNow());
                if (!_pending.TryGetValue(sessionId, out var pending)
                    || (expectedSlotIndex.HasValue
                        && pending.SlotIndex != expectedSlotIndex.Value))
                {
                    return false;
                }

                if (pending.Reservation == null)
                    pending.Reservation = create?.Invoke(pending);

                reservation = pending.Reservation;
                if (reservation == null || reservation.IsInProgress)
                {
                    reservation = null;
                    return false;
                }

                reservation.IsInProgress = true;
                return true;
            }
        }

        internal bool ReleaseOpen(
            Guid sessionId,
            LotteryOpenReservation reservation)
        {
            lock (_sync)
            {
                if (reservation == null
                    || !_pending.TryGetValue(sessionId, out var pending)
                    || !ReferenceEquals(pending.Reservation, reservation))
                {
                    return false;
                }

                reservation.IsInProgress = false;
                return true;
            }
        }

        internal bool CompleteOpen(
            Guid sessionId,
            LotteryOpenReservation reservation)
        {
            lock (_sync)
            {
                if (reservation == null
                    || !_pending.TryGetValue(sessionId, out var pending)
                    || !ReferenceEquals(pending.Reservation, reservation))
                {
                    return false;
                }

                _pending.Remove(sessionId);
                return true;
            }
        }

        public bool TryTake(Guid sessionId, short? expectedSlotIndex, out PendingLotteryOpen pending)
        {
            lock (_sync)
            {
                CleanupExpired(_utcNow());
                if (!_pending.TryGetValue(sessionId, out pending))
                    return false;

                if (expectedSlotIndex.HasValue && pending.SlotIndex != expectedSlotIndex.Value)
                    return false;

                _pending.Remove(sessionId);
                return true;
            }
        }

        public void Remove(Guid sessionId)
        {
            lock (_sync)
                _pending.Remove(sessionId);
        }

        private void CleanupExpired(DateTime nowUtc)
        {
            var expired = _pending
                .Where(pair => (pair.Value.Reservation == null
                        || !pair.Value.Reservation.IsInProgress)
                    && nowUtc - pair.Value.CreatedAtUtc > _timeout)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var sessionId in expired)
                _pending.Remove(sessionId);
        }
    }
}
