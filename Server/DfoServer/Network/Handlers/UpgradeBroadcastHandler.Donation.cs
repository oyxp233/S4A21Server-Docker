using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class UpgradeBroadcastHandler
    {
        // Native 01204EC0 constructs the two possible gifts: gold and clear cubes.
        private async Task Donate(EnhancedClientSession donor, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 9 || body[0] is < 1 or > 2 || body.Length != 1 + 8 * body[0]) return;
            var gifts = new Dictionary<int, int>();
            for (int i = 0; i < body[0]; i++)
            {
                int item = BitConverter.ToInt32(body, 1 + i * 8), count = BitConverter.ToInt32(body, 5 + i * 8);
                if ((item != 0 && item != 3037) || count <= 0 || !gifts.TryAdd(item, count)) return;
            }
            var room = Find(donor);
            if (room == null || ReferenceEquals(room.Host, donor) || !SameArea(room, donor)) return;
            await _transitions.RunIfBothCurrentAsync(donor, room.Host, async () =>
            {
                if (!Live(room) || !SameArea(room, donor) || !ReferenceEquals(Find(donor), room)
                    || !InventoryContext.TryGetOwnedLease(donor.SessionId, donor.Player.CharacterId, out var from)
                    || !InventoryContext.TryGetOwnedLease(room.Host.SessionId, room.CharacterId, out var to)) return;
                var key = (donor.SessionId, header.seq, header.checksum);
                lock (_sync)
                {
                    // Replay detection and the quota reservation are one step so
                    // concurrent viewers cannot both pass the same limit.
                    if (room.Gifts.Contains(key) || !TryReserveDonations(room, gifts)) return;
                }
                bool settled = false;
                try
                {
                    var first = from.CharacterId < to.CharacterId ? from : to;
                    var second = ReferenceEquals(first, from) ? to : from;
                    bool committed;
                    lock (first.SyncRoot)
                    lock (second.SyncRoot)
                    {
                        var offers = new List<InventoryExchangeOffer>();
                        if (gifts.TryGetValue(3037, out int cubes))
                        {
                            if (!InventoryService.TryResolveMainVirtualSlotByItemId(3037, out var slot, out _)) return;
                            var source = InventoryExchangeCommitService.ReadSource(from.Inventory, slot);
                            if (source == null) return;
                            offers.Add(new InventoryExchangeOffer(slot, cubes, source.Copy()));
                        }
                        committed = InventoryExchangeCommitService.TryCommit(from, offers, gifts.GetValueOrDefault(0),
                            to, Array.Empty<InventoryExchangeOffer>(), 0);
                    }
                    if (!committed)
                    { await Failure(donor, "捐赠未完成，未扣除金币或无色小晶块。请检查余额、对方金币上限及账号。"); return; }
                    byte[] total;
                    lock (_sync)
                    {
                        SettleDonations(room, gifts);
                        settled = true;
                        room.Gifts.Add(key); room.GiftOrder.Enqueue(key);
                        if (room.GiftOrder.Count > 64) room.Gifts.Remove(room.GiftOrder.Dequeue());
                        total = UpgradeBroadcastPacketBuilder.Donations(room);
                    }
                    await Accept(donor, CmdPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO);
                    // A full Main list rebuild invalidates the native live window's
                    // selected equipment pointer. Refresh only the changed wallets.
                    var changedSlots = new List<short>();
                    if (gifts.ContainsKey(0)) changedSlots.Add(InventoryService.MainVirtualCurrencySlotStart);
                    if (gifts.ContainsKey(3037) && InventoryService.TryResolveMainVirtualSlotByItemId(3037, out var clearSlot, out _))
                        changedSlots.Add(clearSlot);
                    await _refresh.SendUpdateItemList(donor, InventoryListType.Main, changedSlots);
                    await _refresh.SendUpdateItemList(room.Host, InventoryListType.Main, changedSlots);
                    foreach (var gift in gifts)
                        await Members(room, UpgradeBroadcastPacketBuilder.Donor(room.Id, donor.Player.Name, gift.Key, gift.Value));
                    await Members(room, total);
                    FileLogger.Log($"[UpgradeBroadcast] donation room={room.Id} from={from.CharacterId} to={to.CharacterId} gold={gifts.GetValueOrDefault(0)} cubes={gifts.GetValueOrDefault(3037)}");
                }
                finally
                {
                    // Every early return, failure or exception must drop the
                    // reservation; a settled donation released nothing.
                    if (!settled) lock (_sync) ReleaseDonations(room, gifts);
                }
            });
        }

        // Reservation helpers for the room donation quota. Callers hold _sync.
        // Committed totals are client-visible; pending totals are only capacity
        // that an in-flight wallet transaction is about to consume.
        internal static bool TryReserveDonations(UpgradeBroadcastRoom room, IReadOnlyDictionary<int, int> gifts)
        {
            foreach (var gift in gifts)
                if ((long)room.Donations.GetValueOrDefault(gift.Key) + room.PendingDonations.GetValueOrDefault(gift.Key)
                    + gift.Value > int.MaxValue) return false;
            foreach (var gift in gifts)
                room.PendingDonations[gift.Key] = room.PendingDonations.GetValueOrDefault(gift.Key) + gift.Value;
            return true;
        }

        // Released and settled exactly once per reservation by the Donate flow.
        internal static void ReleaseDonations(UpgradeBroadcastRoom room, IReadOnlyDictionary<int, int> gifts)
        {
            foreach (var gift in gifts)
            {
                int remaining = room.PendingDonations.GetValueOrDefault(gift.Key) - gift.Value;
                if (remaining > 0) room.PendingDonations[gift.Key] = remaining;
                else room.PendingDonations.Remove(gift.Key);
            }
        }

        internal static void SettleDonations(UpgradeBroadcastRoom room, IReadOnlyDictionary<int, int> gifts)
        {
            ReleaseDonations(room, gifts);
            foreach (var gift in gifts)
                room.Donations[gift.Key] = room.Donations.GetValueOrDefault(gift.Key) + gift.Value;
        }
    }
}
