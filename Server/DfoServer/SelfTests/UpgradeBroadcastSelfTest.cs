using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class UpgradeBroadcastSelfTest
    {
        public static int Run()
        {
            int checks = 0, failures = 0;
            void Check(string name, bool ok) { checks++; Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (!ok) failures++; }
            try { RunAsync(Check).GetAwaiter().GetResult(); } catch (Exception e) { Check(e.ToString(), false); }
            Console.WriteLine($"UPGRADE_BROADCAST checks={checks} failures={failures}"); return failures == 0 ? 0 : 1;
        }
        private static async Task RunAsync(Action<string, bool> check)
        {
            using var db = SelfTestDatabase.Create("upgrade-broadcast");
            foreach (int id in new[] { 701, 702, 703, 704, 705 }) SeedOwner(db.Database, 100 + id, id, "live" + id);
            var sessions = new SessionDirectory(); var transitions = new CharacterTransitionCoordinator(sessions);
            using var host = await Peer.Create(db.Database, sessions, 701, 10011);
            using var viewer = await Peer.Create(db.Database, sessions, 702, 10011);
            using var other = await Peer.Create(db.Database, sessions, 703, 10100);
            using var onlooker = await Peer.Create(db.Database, sessions, 704, 10011);
            using var gifter = await Peer.Create(db.Database, sessions, 705, 10011);
            var characters = new SqliteCharacterRepository(db.Database);
            var refresh = new InventoryRefreshSender(new SqliteSelectCharacterDataSource(db.Database, characters), characters, db.Database);
            refresh.BindSessions(sessions);
            using var handler = new UpgradeBroadcastHandler(sessions, transitions, refresh, null);
            ushort requestSequence = 0;
            GamePacketHeader lastHeader = default;
            async Task Request(Peer peer, CmdPacketTypeA21 cmd, byte[] body = null)
            {
                lastHeader = new GamePacketHeader { type = (ushort)cmd, seq = ++requestSequence, checksum = 1234 };
                await handler.Handle(peer.Session, lastHeader, body ?? Array.Empty<byte>());
            }
            InventoryLease Lease() { InventoryContext.TryGetOwnedLease(host.Session.SessionId, 701, out var l); return l; }
            int Gold() => Lease().Inventory.GetMainVirtualCount(0).Count;
            int Level() => Lease().Inventory.GetItem(InventoryListType.Main, 10)?.Upgrade ?? -1;
            void Stock(byte level = 0)
            {
                var l = Lease(); var metadata = ItemMetadataResolver.Resolve(31044);
                lock (l.SyncRoot)
                {
                    l.Inventory.SetItem(InventoryListType.Main, 10, new ItemCore { ItemKind = ItemCore.KindEquipment,
                        ItemId = 31044, Value = 1, Durability = (ushort)metadata.Durability, Upgrade = level });
                    l.Inventory.SetMainVirtualCount(0, 100000000);
                    var row = ItemUpgradeTableProvider.GetTable(ItemUpgradeTableKind.Normal).GetRowByTargetLevel(level + 1);
                    if (InventoryService.TryResolveMainVirtualSlotByItemId(row.MaterialItemId, out var slot, out _))
                        l.Inventory.SetMainVirtualCount(slot, 100000);
                    else l.Inventory.SetItem(InventoryListType.Main, 20, new ItemCore { ItemKind = ItemCore.KindConsumable, ItemId = row.MaterialItemId, Count = 100000 });
                }
                if (!InventoryPersistenceService.SaveDirty(l)) throw new Exception("stock failed");
            }
            void DrainAll() { host.Drain(); viewer.Drain(); other.Drain(); onlooker.Drain(); gifter.Drain(); }
            void Sql(string sql) => db.Database.Write((c, t) =>
            {
                using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql; command.ExecuteNonQuery();
            });
            byte[] UpgradeBody()
            {
                var row = ItemUpgradeTableProvider.GetTable(ItemUpgradeTableKind.Normal).GetRowByTargetLevel(Level() + 1);
                short material = InventoryService.TryResolveMainVirtualSlotByItemId(row.MaterialItemId, out var virtualSlot, out _) ? virtualSlot : (short)20;
                var w = new GamePacketWriter(); w.WriteUInt16((ushort)ItemUpgradeMethod.Reinforce); w.WriteInt16(10); w.WriteInt32(31044);
                w.WriteInt16(material); w.WriteInt16(-1); w.WriteDstr(Array.Empty<byte>()); return w.ToArray();
            }
            async Task<GamePacketHeader> Complete()
            {
                var completion = new GamePacketHeader { type = (ushort)CmdPacketTypeA21.UPGRADE_ITEM, seq = ++requestSequence, checksum = 1234 };
                await handler.TryHandleUpgradeCompletion(host.Session, completion, UpgradeBody()); return completion;
            }
            Stock();
            await Request(host, CmdPacketTypeA21.CREATE_UPGRADE_ROOM, new byte[] { 0 });
            check("malformed create cannot open a room", !host.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_INFO)));
            await Request(host, CmdPacketTypeA21.CREATE_UPGRADE_ROOM, Pair(123, 234));
            var opened = host.Drain(); var nearby = viewer.Drain();
            ushort room = BitConverter.ToUInt16(opened.Single(Is(NotiPacketTypeA21.UPGRADE_ROOM_INFO)), 15);
            check("create opens native room with actor UID and real room id", room > 0
                && BitConverter.ToUInt16(opened.Single(Is(NotiPacketTypeA21.UPGRADE_ROOM_INFO)), 18) == 701);
            check("create acknowledges native host permission before opening the live window",
                opened.FindIndex(p => IsAck(CmdPacketTypeA21.CREATE_UPGRADE_ROOM)(p)) >= 0
                && opened.FindIndex(p => IsAck(CmdPacketTypeA21.CREATE_UPGRADE_ROOM)(p)) < opened.FindIndex(p => Is(NotiPacketTypeA21.UPGRADE_ROOM_INFO)(p)));
            check("empty room supplies both fixed native donation entries", NativeGiftTotals(opened.Single(Is(NotiPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO)), 0, 0));
            var giftRoom = new UpgradeBroadcastRoom { Id = room, State = 3 };
            giftRoom.Donations[0] = 3;
            check("gold-only donation preserves the second cube entry required by the native renderer",
                NativeGiftTotals(UpgradeBroadcastPacketBuilder.Donations(giftRoom), 3, 0));
            giftRoom.Donations.Clear(); giftRoom.Donations[3037] = 3;
            check("cube-only donation preserves gold first and cubes second",
                NativeGiftTotals(UpgradeBroadcastPacketBuilder.Donations(giftRoom), 0, 3));
            check("selected room discovery uses native idle phase rather than server selection state",
                UpgradeBroadcastPacketBuilder.List(new[] { giftRoom })[27] == 0);
            var quota = new UpgradeBroadcastRoom { Id = room, State = 3 };
            quota.Donations[0] = int.MaxValue - 5;
            var fiveGold = new Dictionary<int, int> { [0] = 5 };
            check("donation reserves room capacity before the wallet transaction",
                UpgradeBroadcastHandler.TryReserveDonations(quota, fiveGold) && quota.PendingDonations[0] == 5);
            check("in-flight reservation counts against later quota checks without changing visible totals",
                !UpgradeBroadcastHandler.TryReserveDonations(quota, new Dictionary<int, int> { [0] = 1 })
                && quota.Donations[0] == int.MaxValue - 5);
            UpgradeBroadcastHandler.ReleaseDonations(quota, fiveGold);
            check("released reservation can be reserved again for the same amount",
                !quota.PendingDonations.ContainsKey(0) && UpgradeBroadcastHandler.TryReserveDonations(quota, fiveGold));
            UpgradeBroadcastHandler.SettleDonations(quota, fiveGold);
            check("settlement moves the reservation into committed totals exactly once",
                quota.Donations[0] == int.MaxValue && quota.PendingDonations.Count == 0);
            var saturated = new UpgradeBroadcastRoom { Id = room, State = 3 };
            saturated.Donations[3037] = int.MaxValue - 2; saturated.PendingDonations[3037] = 1;
            check("overflowing reservation is rejected and leaves the pending ledger untouched",
                !UpgradeBroadcastHandler.TryReserveDonations(saturated, new Dictionary<int, int> { [3037] = 2 })
                && saturated.PendingDonations[3037] == 1 && !saturated.Donations.ContainsKey(0));
            var marker = opened.Single(Is(NotiPacketTypeA21.CREATE_UPGRADE_ROOM_INFO));
            check("native machine marker uses the real town/area and requested machine position",
                marker[19] == 1 && marker[20] == 0 && BitConverter.ToInt16(marker, 21) == 123 && BitConverter.ToInt16(marker, 23) == 234);
            check("room marker reaches same-area peers but not another channel", nearby.Any(Is(NotiPacketTypeA21.CREATE_UPGRADE_ROOM_INFO)) && other.Drain().Count == 0);
            onlooker.Drain();
            await Request(host, CmdPacketTypeA21.CREATE_UPGRADE_ROOM, Pair(123, 234));
            check("duplicate create reopens existing room without another marker", host.Drain().Count(Is(NotiPacketTypeA21.UPGRADE_ROOM_INFO)) == 1 && viewer.Drain().Count == 0);
            await Request(other, CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM, Pair(room, 701));
            check("foreign channel cannot join the room", !other.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_INFO)));
            await Request(viewer, CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM, Pair(room, 999));
            check("host identity spoof is rejected", !viewer.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_INFO)));
            await Request(viewer, CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM, Pair(room, 701));
            check("viewer joins native room and host receives the join", viewer.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_INFO)) && host.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_JOIN_SPECTATOR_USER_INFO)));
            await Request(viewer, CmdPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM, Put(31044, 10));
            check("viewer cannot control host equipment", !host.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM)) && Level() == 0); viewer.Drain();
            await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM, Put(31044, 10));
            var putPackets = host.Drain(); var viewerPut = viewer.Drain();
            check("put broadcasts selected equipment slot and level", putPackets.Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM)) && viewerPut.Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM)));
            var townPut = onlooker.Drain();
            check("town onlooker receives the selected marker without a live-window inventory projection",
                townPut.Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM)) && !townPut.Any(Is(NotiPacketTypeA21.UPDATE_ITEM_LIST)));
            var preview = putPackets.Single(Is(NotiPacketTypeA21.UPDATE_ITEM_LIST));
            check("put also hydrates the native subtype 30 equipment preview for host and viewer",
                preview.Length == 119 && preview[15] == 30 && BitConverter.ToUInt16(preview, 16) == 1
                && BitConverter.ToInt16(preview, 18) == 10 && BitConverter.ToInt32(preview, 20) == 31044
                && viewerPut.Single(Is(NotiPacketTypeA21.UPDATE_ITEM_LIST)).SequenceEqual(preview) && Level() == 0);
            await Request(viewer, CmdPacketTypeA21.LEAVE_SPECTATOR_UPGRADE_ROOM); DrainAll();
            await Request(viewer, CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM, Pair(room, 701));
            check("late viewer receives the full selected equipment preview", viewer.Drain().Single(Is(NotiPacketTypeA21.UPDATE_ITEM_LIST)).SequenceEqual(preview)); host.Drain();
            int before = Gold(); await Complete();
            check("completion without an accepted animation cannot spend", Gold() == before && Level() == 0
                && !host.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE))); DrainAll();
            Stock(1); before = Gold();
            await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_START);
            check("changed source fails the transaction without spending", Level() == 1 && Gold() == before && !host.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE))); viewer.Drain();
            Stock(); await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM, Put(31044, 10)); DrainAll();
            await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_START); DrainAll(); Stock(1); before = Gold(); await Complete();
            check("equipment changed during animation cannot be charged or strengthened", Level() == 1 && Gold() == before
                && !host.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE))); DrainAll();
            Stock(); await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM, Put(31044, 10)); DrainAll();
            before = Gold(); await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_START);
            var startHeader = lastHeader; var animation = host.Drain(); var viewerAnimation = viewer.Drain();
            check("start plays native animation without rolling, charging or publishing a result", Level() == 0 && Gold() == before
                && animation.Count(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_START)) == 1
                && !animation.Concat(viewerAnimation).Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE)));
            var completionHeader = await Complete();
            var result = host.Drain(); var seen = viewer.Drain();
            check("animation completion uses the existing durable upgrade transaction", Level() == 1 && Gold() < before);
            check("host and viewer receive one result after animation completion", viewerAnimation.Count(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_START)) == 1
                && result.Count(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE)) == 1
                && seen.Count(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE)) == 1 && other.Drain().Count == 0);
            check("completion returns the existing native equipment-upgrade ACK", result.Count(p => p[0] == 1
                && BitConverter.ToUInt16(p, 1) == (ushort)CmdPacketTypeA21.UPGRADE_ITEM && p.Length > 16 && p[15] == 1) == 1);
            var townResult = onlooker.Drain();
            check("town onlooker receives the final result without a current-room animation command",
                townResult.Count(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE)) == 1 && !townResult.Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_START)));
            check("start has no payload and result carries native room/item/level", animation.Single(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_START)).Length == 15
                && BitConverter.ToInt32(seen.Single(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE)), 18) == 31044
                && seen.Single(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE))[22] == 1);
            before = Gold(); await handler.Handle(host.Session, startHeader, Array.Empty<byte>());
            check("replayed start cannot roll or spend again", Gold() == before && Level() == 1 && !host.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE)));
            await handler.TryHandleUpgradeCompletion(host.Session, completionHeader, UpgradeBody());
            check("replayed completion cannot roll or spend again", Gold() == before && Level() == 1
                && !host.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE)));
            await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_START);
            await handler.TryHandleUpgradeCompletion(host.Session, completionHeader, UpgradeBody());
            check("old completion cannot settle a later animation", Level() == 1 && Gold() == before);
            await Complete();
            check("a fresh animation completion continues strengthening the selected item", Level() == 2 && Gold() < before); DrainAll(); before = Gold();
            await Request(viewer, CmdPacketTypeA21.LEAVE_SPECTATOR_UPGRADE_ROOM); DrainAll();
            await Request(viewer, CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM, Pair(room, 701));
            var lateView = viewer.Drain();
            check("late viewer catches up to the completed reinforcement result",
                lateView.Last(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE))[22] == 2
                && lateView.FindLastIndex(p => Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE)(p))
                    > lateView.FindIndex(p => Is(NotiPacketTypeA21.UPGRADE_ROOM_INFO)(p))); DrainAll();
            onlooker.Session.Player.CurAreaId = 2; await handler.Reconcile(onlooker.Session); onlooker.Drain();
            onlooker.Session.Player.CurAreaId = 0; await handler.Reconcile(onlooker.Session);
            var rediscovered = onlooker.Drain();
            check("entering the area replays native result after room discovery",
                rediscovered.Single(Is(NotiPacketTypeA21.UPGRADE_ROOM_LIST_INFO))[27] == 2
                && rediscovered.Single(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE))[22] == 2);
            await handler.Reconcile(onlooker.Session);
            check("unchanged discovery key does not resend the room list", onlooker.Drain().Count == 0);
            onlooker.Session.Player.UserState = 3; await handler.Reconcile(onlooker.Session);
            check("ineligible session receives no discovery packet", onlooker.Drain().Count == 0);
            onlooker.Session.Player.UserState = 0; await handler.Reconcile(onlooker.Session);
            check("regaining eligibility rediscovers the current room list",
                onlooker.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_LIST_INFO)));
            check("ordinary upgrade cannot independently double-spend in a live room", await handler.TryHandleUpgradeCompletion(host.Session,
                new GamePacketHeader { type = (ushort)CmdPacketTypeA21.UPGRADE_ITEM, seq = ++requestSequence }, UpgradeBody()) && Gold() == before); host.Drain();
            using (var c = db.OpenConnection()) check("successful result survives fresh inventory load", InventoryService.LoadFromDb(c, 701, 801, db.Database).GetItem(InventoryListType.Main, 10).Upgrade == 2);
            Stock(); await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM, Put(31044, 10)); DrainAll();
            Sql("CREATE TRIGGER live_fail BEFORE UPDATE ON character_inventory_items WHEN NEW.character_id = 701 BEGIN SELECT RAISE(ABORT, 'live rollback'); END;");
            before = Gold(); await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_START);
            DrainAll(); await Complete();
            check("failed save restores item and gold and publishes no success", Level() == 0 && Gold() == before
                && !host.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE)) && !viewer.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE)));
            Sql("DROP TRIGGER live_fail;");
            Stock(); await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM, Put(31044, 10));
            before = Gold(); await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_START); DrainAll();
            await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_CANCEL); await Complete();
            check("cancel prevents the later animation completion from consuming", Level() == 0 && Gold() == before); DrainAll();
            var title = new GamePacketWriter(); title.WriteDstr(ClientTextEncoding.GetBytes("强化直播测试"));
            await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_MASTER_MESSAGE, title.ToArray());
            check("host title updates both native windows", host.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_MASTER_MESSAGE)) && viewer.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_MASTER_MESSAGE)));
            InventoryLease Owned(Peer peer) { InventoryContext.TryGetOwnedLease(peer.Session.SessionId, peer.Session.Player.CharacterId, out var l); return l; }
            int Balance(Peer peer, short slot) => Owned(peer).Inventory.GetMainVirtualCount(slot)?.Count ?? 0;
            InventoryService.TryResolveMainVirtualSlotByItemId(3037, out var clearSlot, out _);
            Owned(host).Inventory.SetMainVirtualCount(0, 1000); Owned(host).Inventory.SetMainVirtualCount(clearSlot, 0);
            Owned(viewer).Inventory.SetMainVirtualCount(0, 500); Owned(viewer).Inventory.SetMainVirtualCount(clearSlot, 20);
            InventoryPersistenceService.SaveDirty(Owned(host)); InventoryPersistenceService.SaveDirty(Owned(viewer)); DrainAll();
            await Request(viewer, CmdPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO, Gifts(100, 3));
            var giftHeader = lastHeader; var received = host.Drain(); var donorReceived = viewer.Drain();
            check("donation atomically transfers actual gold and account clear-cube wallets",
                Balance(viewer, 0) == 400 && Balance(host, 0) == 1100 && Balance(viewer, clearSlot) == 17 && Balance(host, clearSlot) == 3);
            check("donation publishes the real donor and room totals", received.Count(Is(NotiPacketTypeA21.GIVE_USER_INFO)) == 2
                && received.Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO)));
            check("native donor event begins with the room id rather than the donor UID",
                received.Where(Is(NotiPacketTypeA21.GIVE_USER_INFO)).All(p => BitConverter.ToUInt16(p, 15) == room)
                && room != viewer.Session.Player.UserId);
            check("donation acknowledges the active common native callback once",
                donorReceived.Count(IsAck(CmdPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO)) == 1);
            var hostWallet = Wallet(received.Single(Is(NotiPacketTypeA21.UPDATE_ITEM_LIST)));
            var donorWallet = Wallet(donorReceived.Single(Is(NotiPacketTypeA21.UPDATE_ITEM_LIST)));
            check("donation patches only real wallet slots and never rebuilds selected equipment",
                !received.Concat(donorReceived).Any(Is(NotiPacketTypeA21.ITEM_LIST))
                && hostWallet.Count == 2 && donorWallet.Count == 2 && hostWallet[0] == 1100 && hostWallet[clearSlot] == 3
                && donorWallet[0] == 400 && donorWallet[clearSlot] == 17);
            await handler.Handle(viewer.Session, giftHeader, Gifts(100, 3));
            check("donation packet replay never transfers a second time", Balance(viewer, 0) == 400 && Balance(host, 0) == 1100);
            await Request(viewer, CmdPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO, Gifts(10000, 0)); DrainAll();
            check("insufficient donation cannot partially debit either currency", Balance(viewer, 0) == 400
                && Balance(host, 0) == 1100 && Balance(viewer, clearSlot) == 17 && Balance(host, clearSlot) == 3);
            await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO, Gifts(10, 0));
            check("host cannot donate to itself", Balance(host, 0) == 1100);
            var invalidGift = new GamePacketWriter(); invalidGift.WriteByte(1); invalidGift.WriteInt32(36); invalidGift.WriteInt32(1);
            await Request(viewer, CmdPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO, invalidGift.ToArray());
            check("unrecognized gift item is rejected without a transfer", Balance(host, 0) == 1100 && Balance(viewer, 0) == 400);
            Sql("CREATE TRIGGER donation_fail BEFORE UPDATE ON character_inventory_items WHEN NEW.character_id = 701 BEGIN SELECT RAISE(ABORT, 'recipient donation rollback'); END;");
            await Request(viewer, CmdPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO, Gifts(10, 1));
            check("recipient save failure restores both donor and host balances", Balance(host, 0) == 1100 && Balance(viewer, 0) == 400
                && Balance(host, clearSlot) == 3 && Balance(viewer, clearSlot) == 17 && !host.Drain().Any(Is(NotiPacketTypeA21.GIVE_USER_INFO)));
            Sql("DROP TRIGGER donation_fail;");
            await Request(gifter, CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM, Pair(room, 701)); DrainAll();
            Owned(gifter).Inventory.SetMainVirtualCount(0, 300); Owned(gifter).Inventory.SetMainVirtualCount(clearSlot, 10);
            InventoryPersistenceService.SaveDirty(Owned(gifter)); DrainAll();
            var viewerGift = new GamePacketHeader { type = (ushort)CmdPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO, seq = ++requestSequence, checksum = 5678 };
            var gifterGift = new GamePacketHeader { type = (ushort)CmdPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO, seq = ++requestSequence, checksum = 5678 };
            await Task.WhenAll(handler.Handle(viewer.Session, viewerGift, Gifts(50, 0)),
                handler.Handle(gifter.Session, gifterGift, Gifts(70, 2)));
            var concurrentTotals = host.Drain().Where(Is(NotiPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO)).ToList();
            check("concurrent viewers reserve, transfer and settle independent donations",
                Balance(viewer, 0) == 350 && Balance(gifter, 0) == 230 && Balance(host, 0) == 1220
                && Balance(viewer, clearSlot) == 17 && Balance(gifter, clearSlot) == 8 && Balance(host, clearSlot) == 5);
            check("concurrent donations publish the combined committed totals",
                concurrentTotals.Count == 2 && NativeGiftTotals(concurrentTotals.Last(), 220, 5));
            using (var c = db.OpenConnection()) check("gift balances survive a fresh reload", InventoryService.LoadFromDb(c, 701, 801, db.Database).GetMainVirtualCount(0).Count == 1220
                && InventoryService.LoadFromDb(c, 702, 802, db.Database).GetMainVirtualCount(clearSlot).Count == 17);
            DrainAll();
            await Request(host, CmdPacketTypeA21.DESTROY_UPGRADE_ROOM);
            check("closing clears viewer state and town marker", viewer.Drain().Any(Is(NotiPacketTypeA21.LEAVE_SPECTATOR_USER_INFO)) && host.Drain().Any(Is(NotiPacketTypeA21.DESTROY_UPGRADE_ROOM_INFO)));
            await Request(viewer, CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM, Pair(room, 701));
            check("closed room cannot be rejoined", !viewer.Drain().Any(Is(NotiPacketTypeA21.UPGRADE_ROOM_INFO)));
            await Request(host, CmdPacketTypeA21.CREATE_UPGRADE_ROOM, Pair(123, 234)); DrainAll();
            host.Session.Player.CurAreaId = 2; await handler.Reconcile(host.Session);
            check("area change removes old room and prevents ordinary-upgrade interception", !await handler.TryHandleUpgradeCompletion(host.Session, default, Array.Empty<byte>())); DrainAll();
            host.Session.Player.CurAreaId = 0;
            await Request(host, CmdPacketTypeA21.CREATE_UPGRADE_ROOM, Pair(123, 234));
            ushort secondRoom = BitConverter.ToUInt16(host.Drain().Single(Is(NotiPacketTypeA21.UPGRADE_ROOM_INFO)), 15); DrainAll();
            await Request(viewer, CmdPacketTypeA21.JOIN_SPECTATOR_UPGRADE_ROOM, Pair(secondRoom, 701)); DrainAll();
            using var replacement = await Peer.Create(db.Database, sessions, 701, 10011);
            await handler.Reconcile(replacement.Session);
            await handler.Reconcile(viewer.Session);
            check("reconcile clears membership in a room that is no longer live",
                viewer.Drain().Any(Is(NotiPacketTypeA21.LEAVE_SPECTATOR_USER_INFO)));
            await handler.Reconcile(viewer.Session);
            check("cleared membership is not cleaned twice", viewer.Drain().Count == 0);
            await Request(host, CmdPacketTypeA21.UPGRADE_ROOM_UPGRADE_START);
            check("displaced host cannot retain a room or spend", !await handler.TryHandleUpgradeCompletion(replacement.Session, default, Array.Empty<byte>()));
        }
        private static Func<byte[], bool> Is(NotiPacketTypeA21 type) => p => p[0] == 0 && BitConverter.ToUInt16(p, 1) == (ushort)type;
        private static Func<byte[], bool> IsAck(CmdPacketTypeA21 type) => p => p[0] == 1 && BitConverter.ToUInt16(p, 1) == (ushort)type
            && p.Length == 16 && p[15] == 1;
        private static bool NativeGiftTotals(byte[] packet, int gold, int cubes) => packet.Length == 34 && packet[17] == 2
            && BitConverter.ToInt32(packet, 18) == 0 && BitConverter.ToInt32(packet, 22) == gold
            && BitConverter.ToInt32(packet, 26) == 3037 && BitConverter.ToInt32(packet, 30) == cubes;
        private static Dictionary<short, int> Wallet(byte[] packet)
        {
            if (packet[15] != 0 || packet.Length != 18 + 101 * BitConverter.ToUInt16(packet, 16)) throw new Exception("wallet projection layout");
            return Enumerable.Range(0, BitConverter.ToUInt16(packet, 16)).ToDictionary(
                n => BitConverter.ToInt16(packet, 18 + 101 * n), n => BitConverter.ToInt32(packet, 24 + 101 * n));
        }
        private static byte[] Pair(ushort a, ushort b) { var w = new GamePacketWriter(); w.WriteUInt16(a); w.WriteUInt16(b); return w.ToArray(); }
        private static byte[] Put(int item, short slot) { var w = new GamePacketWriter(); w.WriteInt32(item); w.WriteInt16(slot); return w.ToArray(); }
        private static byte[] Gifts(int gold, int cubes)
        { var w = new GamePacketWriter(); w.WriteByte((byte)((gold > 0 ? 1 : 0) + (cubes > 0 ? 1 : 0)));
            if (gold > 0) { w.WriteInt32(0); w.WriteInt32(gold); } if (cubes > 0) { w.WriteInt32(3037); w.WriteInt32(cubes); } return w.ToArray(); }
        private sealed class Peer : IDisposable
        {
            private TcpClient _reader; private SessionDirectory _sessions;
            internal EnhancedClientSession Session;
            internal static async Task<Peer> Create(GameDatabase db, SessionDirectory sessions, int id, int port)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var reader = new TcpClient { ReceiveTimeout = 3000 };
                try { var connecting = reader.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    var writer = await listener.AcceptTcpClientAsync(); await connecting;
                    var session = new EnhancedClientSession(writer, new GamePacketHeader(), port);
                    session.Account = new AccountRecord { AccountId = id + 100 }; session.Player.CharacterId = id; session.Player.UserId = (ushort)id;
                    session.Player.Name = ClientTextEncoding.GetBytes("live" + id); session.Player.CurTownId = 1; session.Player.CurAreaId = 0;
                    session.Player.UserState = 0;
                    LoadAndRegister(db, session.SessionId, id + 100, id); sessions.Register(id, session);                    return new Peer { _reader = reader, Session = session, _sessions = sessions }; }
                finally { listener.Stop(); }
            }
            internal List<byte[]> Drain()
            {
                var packets = new List<byte[]>(); var stream = _reader.GetStream();
                while (_reader.Client.Poll(20000, SelectMode.SelectRead))
                { if (_reader.Available == 0) break; var h = new byte[15]; stream.ReadExactly(h); var p = new byte[BitConverter.ToInt32(h, 3)]; h.CopyTo(p, 0); stream.ReadExactly(p.AsSpan(15)); packets.Add(p); }
                return packets;
            }
            public void Dispose() { _sessions.UnregisterAsync(Session.Player.CharacterId, Session).GetAwaiter().GetResult(); InventoryContext.Unregister(Session.SessionId); Session.Close(); _reader.Dispose(); }
        }
        private static void SeedOwner(GameDatabase database, int accountId, int characterId, string name)
        {
            database.Write((c, t) =>
            {
                using var command = c.CreateCommand(); command.Transaction = t;
                command.CommandText = "INSERT INTO accounts (account_id, m_id, password_hash) VALUES (@aid, @name, '');"
                    + "INSERT INTO characters (character_id, account_id, name) VALUES (@cid, @aid, @name);";
                command.Parameters.AddWithValue("@aid", accountId); command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@name", name); command.ExecuteNonQuery();
            });
        }
        private static InventoryLease LoadAndRegister(GameDatabase database, Guid sessionId, int accountId, int characterId)
        {
            using var connection = database.OpenConnection();
            return InventoryContext.Register(sessionId, InventoryService.LoadFromDb(connection, characterId, accountId, database));
        }
        // master 基线是 SQLite：临时文件库，Dispose 时连同 WAL/SHM 一起清理。
        private sealed class SelfTestDatabase : IDisposable
        {
            private readonly string _path;
            private SelfTestDatabase(string path)
            { _path = path; Database = new GameDatabase(path, ServerPaths.SchemaFilePath); }
            internal GameDatabase Database { get; }
            internal Microsoft.Data.Sqlite.SqliteConnection OpenConnection() => Database.OpenConnection();
            internal static SelfTestDatabase Create(string hint) => new(
                Path.Combine(Path.GetTempPath(), $"dfo_st_{hint}_{Guid.NewGuid():N}.db"));
            public void Dispose()
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
                    if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
