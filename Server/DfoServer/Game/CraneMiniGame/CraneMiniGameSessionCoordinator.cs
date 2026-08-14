using System;
using System.Collections.Generic;

namespace DfoServer.Game.CraneMiniGame
{
    internal sealed class CraneMiniGamePickupReservation
    {
        internal CraneMiniGamePickupReservation(
            CraneMiniGameItem item,
            bool won)
        {
            Item = item;
            Won = won;
        }

        internal CraneMiniGameItem Item { get; }

        internal bool Won { get; }

        internal bool IsInProgress { get; set; }
    }

    internal sealed class CraneMiniGameSessionCoordinator
    {
        private sealed class PendingSession
        {
            internal CraneMiniGameStartResult State { get; set; }

            internal CraneMiniGamePickupReservation Reservation { get; set; }
        }

        private readonly object _syncRoot = new object();
        private readonly Dictionary<Guid, PendingSession> _pending = new();

        internal void Set(Guid sessionId, CraneMiniGameStartResult state)
        {
            if (sessionId == Guid.Empty || state == null)
                return;
            lock (_syncRoot)
            {
                _pending[sessionId] = new PendingSession
                {
                    State = state,
                };
            }
        }

        internal bool TryGet(Guid sessionId, out CraneMiniGameStartResult state)
        {
            lock (_syncRoot)
            {
                if (_pending.TryGetValue(sessionId, out var pending))
                {
                    state = pending.State;
                    return true;
                }

                state = null;
                return false;
            }
        }

        internal bool TryTake(Guid sessionId, out CraneMiniGameStartResult state)
        {
            lock (_syncRoot)
            {
                if (!_pending.TryGetValue(sessionId, out var pending))
                {
                    state = null;
                    return false;
                }

                state = pending.State;
                _pending.Remove(sessionId);
                return true;
            }
        }

        internal bool TryReservePickup(
            Guid sessionId,
            ushort displaySlot,
            int itemId,
            out CraneMiniGamePickupReservation reservation,
            Func<CraneMiniGameItem, bool> rollSuccess = null)
        {
            lock (_syncRoot)
            {
                reservation = null;
                if (!_pending.TryGetValue(sessionId, out var pending))
                    return false;

                if (pending.Reservation == null)
                {
                    if (!CraneMiniGamePickupService.TryResolveSelection(
                            pending.State,
                            displaySlot,
                            itemId,
                            out var selected))
                    {
                        return false;
                    }

                    var item = CopyItem(selected);
                    var won = rollSuccess != null
                        ? rollSuccess(item)
                        : CraneMiniGamePickupService.RollSuccess(item);
                    pending.Reservation = new CraneMiniGamePickupReservation(
                        item,
                        won);
                    if (!won)
                    {
                        reservation = pending.Reservation;
                        _pending.Remove(sessionId);
                        return true;
                    }
                }

                reservation = pending.Reservation;
                if (reservation == null
                    || reservation.Item.CatalogIndex != displaySlot
                    || reservation.Item.ItemId != itemId
                    || reservation.IsInProgress)
                {
                    reservation = null;
                    return false;
                }

                reservation.IsInProgress = true;
                return true;
            }
        }

        internal bool ReleasePickup(
            Guid sessionId,
            CraneMiniGamePickupReservation reservation)
        {
            lock (_syncRoot)
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

        internal bool CompletePickup(
            Guid sessionId,
            CraneMiniGamePickupReservation reservation)
        {
            lock (_syncRoot)
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

        internal void Clear(Guid sessionId)
        {
            lock (_syncRoot)
                _pending.Remove(sessionId);
        }

        private static CraneMiniGameItem CopyItem(CraneMiniGameItem item)
        {
            return new CraneMiniGameItem
            {
                CatalogIndex = item.CatalogIndex,
                ItemId = item.ItemId,
                Count = item.Count,
                ViewWeight = item.ViewWeight,
                PickChance = item.PickChance,
            };
        }
    }
}
