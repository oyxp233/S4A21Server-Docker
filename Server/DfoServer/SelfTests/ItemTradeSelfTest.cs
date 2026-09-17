using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Inventory;
using Microsoft.Data.Sqlite;
using Peer = DfoServer.SelfTests.A21OneToOneChatSelfTest.Peer;

namespace DfoServer.SelfTests
{
    public static class ItemTradeSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool value)
            { Console.WriteLine($"[{(value ? "PASS" : "FAIL")}] {name}"); if (value) passed++; else failed++; }
            var path = Path.Combine(Path.GetTempPath(), $"a21-trade-{Guid.NewGuid():N}.db");
            try { RunAsync(path, Check).GetAwaiter().GetResult(); }
            catch (Exception ex) { Check(ex.ToString(), false); }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"ITEM_TRADE: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static async Task RunAsync(string path, Action<string, bool> check)
        {
            var liveItem = Convert.FromHexString("000A00A0280000010000000400000000000000000000FFFFFFFF0000");
            var liveGold = Convert.FromHexString("00000000000000140000000400000000000000000000FFFFFFFF0000");
            check("live 28-byte item request parses", ItemTradeRequest.TryMove(liveItem, out var liveMove)
                && liveMove.SourceSlotIndex == 10 && liveMove.SourceInstanceValue == 10400 && liveMove.MoveCount == 1);
            check("live 28-byte gold request parses", ItemTradeRequest.TryMove(liveGold, out liveMove)
                && liveMove.SourceSlotIndex == 0 && liveMove.SourceInstanceValue == 0 && liveMove.MoveCount == 20);
            var padded = new byte[32]; liveItem.CopyTo(padded, 0);
            check("reference zero-padded request remains supported", ItemTradeRequest.TryMove(padded, out _));
            padded[31] = 1;
            check("invalid trade lengths and nonzero padding rejected", !ItemTradeRequest.TryMove(liveItem[..27], out _)
                && !ItemTradeRequest.TryMove(new byte[29], out _) && !ItemTradeRequest.TryMove(padded, out _));
            check("captured padded trade request", ItemTradeRequest.TryPeer(
                Convert.FromHexString("480301948A0000000000000000000000"), false, out var uid, out var peer, out _)
                && uid == 840 && peer == 35476);
            check("captured acceptance and refusal are distinct", ItemTradeRequest.TryPeer(
                Convert.FromHexString("49030114A0000000"), true, out _, out _, out var refused) && !refused
                && ItemTradeRequest.TryPeer(Convert.FromHexString("49030114A00000850000000000000000"), true, out _, out _, out refused) && refused);
            check("state padding and malformed tails", ItemTradeRequest.TryState(new byte[] { 5, 0, 0, 0 }, out var state) && state == 5
                && ItemTradeRequest.TryState(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0 }, out state) && state == 3
                && !ItemTradeRequest.TryState(new byte[] { 3, 1, 0, 0 }, out _)
                && !ItemTradeRequest.TryState(new byte[] { 3, 0 }, out _));
            var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
            void Sql(string sql) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
            Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(62001,'trade-a',''),(62002,'trade-b','');"
                + "INSERT INTO characters(character_id,account_id,name,level) VALUES(62001,62001,'trade-a',85),(62002,62002,'trade-b',85);");
            var directory = new SessionDirectory();
            using var a = await Peer.Create(directory, 62001, "trade-a");
            using var b = await Peer.Create(directory, 62002, "trade-b");
            InventoryLease Load(Peer p)
            {
                using var c = db.OpenConnection();
                return InventoryContext.Register(p.Session.SessionId,
                    InventoryService.LoadFromDb(c, p.Session.Player.CharacterId, p.Session.Player.CharacterId, db));
            }
            InventoryLease left = Load(a), right = Load(b);
            try
            {
                check("current PVF material fixtures", InventoryRewardGrantService.TryCreateAndInsert(left.Inventory, 3030, ItemCreateReason.Unknown, 20, out var ga)
                    && InventoryRewardGrantService.TryCreateAndInsert(right.Inventory, 3242, ItemCreateReason.Unknown, 10, out _));
                left.Inventory.SetMainVirtualCount(0, 10000); right.Inventory.SetMainVirtualCount(0, 10000);
                check("baseline persisted", InventoryPersistenceService.SaveDirty(left) && InventoryPersistenceService.SaveDirty(right));
                short Source(InventoryLease l, int id) => l.Inventory.GetItems(InventoryListType.Main).First(x => x.Value.ItemId == id).Key;
                int Count(InventoryLease l, int id) => l.Inventory.CountMainItem(id);
                var ls = Source(left, 3030); var rs = Source(right, 3242);
                check("transfer rule rejects locked/bound/detail items", !InventoryExchangeCommitService.CanOffer(new ItemCore { ItemId = 3030, EquipmentLockId = 1 }, 1, 62001, 62002)
                    && !InventoryExchangeCommitService.CanOffer(new ItemCore { ItemId = 3030, TradeRestriction = 1 }, 1, 62001, 62002)
                    && !InventoryExchangeCommitService.CanOffer(new ItemCore { ItemId = 3030, ItemKind = ItemCore.KindAvatar }, 1, 62001, 62002));
                var packet = ItemTradePacketBuilder.Item(3, left.Inventory.GetItem(InventoryListType.Main, ls));
                check("A21 trade entry reads 101 bytes, not captured 96", packet.Length == 116
                    && BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.CHANGE_ITEMTRADE_ITEM
                    && BitConverter.ToUInt16(packet, 15) == 3 && BitConverter.ToInt32(packet, 17) == 3030);
                check("peer and close packet lengths match readers", ItemTradePacketBuilder.Accepted(840, 1, true).Length == 20
                    && ItemTradePacketBuilder.Accepted(840, 1, false).Length == 27
                    && ItemTradePacketBuilder.Invite(840, 1).Length == 26
                    && ItemTradePacketBuilder.RegistrationFinished().Length == 15
                    && ItemTradePacketBuilder.Closed(false).Length == 17);

                var trade = new ItemTradeSession(left, right, 1);
                check("only invited lease may accept once", !trade.Accept(left) && trade.Accept(right) && !trade.Accept(right));
                check("early final confirmation rejected", trade.Advance(0, 3) == ItemTradeAdvance.Rejected);
                check("offer is a snapshot; no early debit", trade.Offer(0, ls, 3030, 7, out var slot, out _) && slot == 3 && Count(left, 3030) == 20);
                check("duplicate source and excessive count rejected", !trade.Offer(0, ls, 3030, 7, out _, out _)
                    && !trade.Offer(1, rs, 3242, 11, out _, out _));
                check("opposite offer and gold", trade.Offer(1, rs, 3242, 3, out _, out _)
                    && trade.Offer(0, 0, 0, 100, out _, out _) && !trade.Offer(0, 0, 0, int.MaxValue, out _, out _));
                check("registration locks own offer", trade.Advance(0, 5) == ItemTradeAdvance.Changed
                    && !trade.Offer(0, ls, 3030, 1, out _, out _) && trade.Advance(0, 1) == ItemTradeAdvance.Rejected
                    && trade.Advance(1, 5) == ItemTradeAdvance.RegistrationFinished);
                check("two ready states precede confirmations", trade.Advance(0, 1) == ItemTradeAdvance.Changed
                    && trade.Advance(0, 3) == ItemTradeAdvance.Rejected && trade.Advance(1, 1) == ItemTradeAdvance.Changed);
                check("first confirmation does not commit", trade.Advance(0, 3) == ItemTradeAdvance.Changed && Count(left, 3030) == 20);
                check("second confirmation atomically exchanges assets", trade.Advance(1, 3) == ItemTradeAdvance.Committed
                    && Count(left, 3030) == 13 && Count(right, 3030) == 7 && Count(left, 3242) == 3 && Count(right, 3242) == 7
                    && left.Inventory.GetMainVirtualCount(0).Count == 9900 && right.Inventory.GetMainVirtualCount(0).Count == 10100);
                check("replayed final confirmation cannot transfer twice", trade.Advance(1, 3) == ItemTradeAdvance.Rejected && Count(right, 3030) == 7);
                using (var c = db.OpenConnection())
                {
                    var la = InventoryService.LoadFromDb(c, 62001, 62001, db);
                    var lb = InventoryService.LoadFromDb(c, 62002, 62002, db);
                    check("relogin sees both committed inventories", la.CountMainItem(3030) == 13 && lb.CountMainItem(3030) == 7
                        && la.GetMainVirtualCount(0).Count == 9900 && lb.GetMainVirtualCount(0).Count == 10100);
                }
                var stale = new InventoryExchangeOffer(ls, 1, left.Inventory.GetItem(InventoryListType.Main, ls).Copy());
                var changed = stale.Snapshot.Copy(); changed.Count--;
                left.Inventory.SetItem(InventoryListType.Main, ls, changed);
                check("changed source invalidates stale offer without losing dirty changes", !InventoryExchangeCommitService.TryCommit(left, new[] { stale }, 0, right, Array.Empty<InventoryExchangeOffer>(), 0)
                    && Count(left, 3030) == 12 && Count(right, 3030) == 7);
                stale = new(ls, 1, changed.Copy());
                Sql("CREATE TRIGGER reject_trade BEFORE UPDATE ON character_inventory_items WHEN NEW.character_id=62002 BEGIN SELECT RAISE(ABORT,'trade-test'); END;");
                check("second participant database failure rolls back both", !InventoryExchangeCommitService.TryCommit(left, new[] { stale }, 10, right, Array.Empty<InventoryExchangeOffer>(), 0));
                Sql("DROP TRIGGER reject_trade;");
                InventoryContext.TryGetLease(62001, out left); InventoryContext.TryGetLease(62002, out right);
                check("rollback preserves earlier dirty mutation and both balances", Count(left, 3030) == 12 && Count(right, 3030) == 7
                    && left.Inventory.GetMainVirtualCount(0).Count == 9900 && right.Inventory.GetMainVirtualCount(0).Count == 10100);
                var abandoned = new ItemTradeSession(left, right, 0); abandoned.Accept(right); abandoned.Cancel();
                check("canceled negotiation cannot mutate", !abandoned.Offer(0, ls, 3030, 1, out _, out _) && abandoned.Advance(0, 5) == ItemTradeAdvance.Rejected);
                var old = left; left = Load(a);
                check("replaced lease rejected", !InventoryExchangeCommitService.TryCommit(old, Array.Empty<InventoryExchangeOffer>(), 1, right, Array.Empty<InventoryExchangeOffer>(), 0));
                await VerifyWire(db, directory, a, b, left, right, check);
                InventoryContext.TryGetLease(62002, out right);
                await VerifyTimedItems(db, left, right, check);
                using (var c = db.OpenConnection())
                {
                    int cap = DfoServer.Game.Currency.CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(c, null, 62002);
                    right.Inventory.SetMainVirtualCount(0, cap);
                    check("recipient gold cap rejects entire exchange", !InventoryExchangeCommitService.TryCommit(left,
                        Array.Empty<InventoryExchangeOffer>(), 1, right, Array.Empty<InventoryExchangeOffer>(), 0)
                        && left.Inventory.GetMainVirtualCount(0).Count == 9900 && right.Inventory.GetMainVirtualCount(0).Count == cap);
                    right.Inventory.SetMainVirtualCount(0, 10100);
                }
                var receiverSlot = Source(right, 3030);
                var receiverCore = right.Inventory.GetItem(InventoryListType.Main, receiverSlot).Copy();
                var incompatible = receiverCore.Copy(); incompatible.StackTradeCount = 1;
                right.Inventory.SetItem(InventoryListType.Main, receiverSlot, incompatible);
                var offer = new InventoryExchangeOffer(ls, 1, left.Inventory.GetItem(InventoryListType.Main, ls).Copy());
                check("stack merge cannot erase instance trade state", !InventoryExchangeCommitService.TryCommit(left,
                    new[] { offer }, 0, right, Array.Empty<InventoryExchangeOffer>(), 0)
                    && Count(left, 3030) == 11 && Count(right, 3030) == 8);
                right.Inventory.SetItem(InventoryListType.Main, receiverSlot, receiverCore);
                var filler = right.Inventory.GetItem(InventoryListType.Main, Source(right, 3242)).Copy();
                for (short slotIndex = 3; slotIndex < 352; slotIndex++)
                    right.Inventory.SetItem(InventoryListType.Main, slotIndex, filler.Copy());
                check("full recipient inventory rolls back sender debit and gold", !InventoryExchangeCommitService.TryCommit(left,
                    new[] { offer }, 1, right, Array.Empty<InventoryExchangeOffer>(), 0)
                    && Count(left, 3030) == 11 && Count(right, 3030) == 0
                    && left.Inventory.GetMainVirtualCount(0).Count == 9900 && right.Inventory.GetMainVirtualCount(0).Count == 10100);
            }
            finally
            {
                InventoryContext.Unregister(a.Session.SessionId); InventoryContext.Unregister(b.Session.SessionId);
                await directory.UnregisterAsync(62001, a.Session); await directory.UnregisterAsync(62002, b.Session);
            }
        }

        private static async Task VerifyTimedItems(GameDatabase db, InventoryLease left, InventoryLease right, Action<string, bool> check)
        {
            const int itemId = 3030; // Known free stack fixture for split/merge/rollback coverage.
            var owners = new[] { left, right };
            var originals = owners.Select(l => l.Inventory.GetItems(InventoryListType.Main)
                .Where(x => x.Value.ItemId == itemId).ToDictionary(x => x.Key, x => x.Value.Copy())).ToArray();
            foreach (var l in owners)
                foreach (var entry in l.Inventory.GetItems(InventoryListType.Main).Where(x => x.Value.ItemId == itemId).ToArray())
                    l.Inventory.RemoveItem(InventoryListType.Main, entry.Key);
            try
            {
                int expiration = checked((int)DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
                check("current live timed consumable fixture", InventoryRewardGrantService.TryCreateAndInsert(left.Inventory,
                    10008088, ItemCreateReason.Unknown, 1, new InventoryCreateOptions { ExpireTime = expiration }, out var liveGrant));
                var liveCore = left.Inventory.GetItem(InventoryListType.Main, liveGrant.SlotIndex);
                Console.WriteLine($"Live timed fixture attach={ItemMetadataResolver.Resolve(10008088)?.AttachType}");
                check("reported timed consumable can enter trade", InventoryExchangeCommitService.CanOffer(liveCore, 1, left.AccountId, right.AccountId));
                var liveOffer = new InventoryExchangeOffer(liveGrant.SlotIndex, 1, liveCore.Copy());
                bool liveCommitted = InventoryExchangeCommitService.TryCommit(left,
                    new[] { liveOffer }, 0, right, Array.Empty<InventoryExchangeOffer>(), 0);
                var liveReceived = right.Inventory.GetItems(InventoryListType.Main).FirstOrDefault(x => x.Value.ItemId == 10008088).Value;
                check("reported timed consumable transfers with exact core", liveCommitted && liveReceived != null
                    && liveReceived.ToBytes().SequenceEqual(liveOffer.Snapshot.ToBytes()));
                left.Inventory.RemoveItem(InventoryListType.Main, liveGrant.SlotIndex);
                foreach (var entry in right.Inventory.GetItems(InventoryListType.Main).Where(x => x.Value.ItemId == 10008088).ToArray())
                    right.Inventory.RemoveItem(InventoryListType.Main, entry.Key);
                check("current PVF timed stack fixture", InventoryRewardGrantService.TryCreateAndInsert(left.Inventory,
                    itemId, ItemCreateReason.Unknown, 2, new InventoryCreateOptions { ExpireTime = expiration }, out var grant));
                var slot = grant.SlotIndex;
                var core = left.Inventory.GetItem(InventoryListType.Main, slot).Copy();
                Console.WriteLine($"Timed fixture attach={ItemMetadataResolver.Resolve(itemId)?.AttachType} kind={core.ItemKind}");
                bool allowed = InventoryExchangeCommitService.CanOffer(core, 1, left.AccountId, right.AccountId);
                check("unexpired tradable item can enter direct trade", allowed);
                var mail = new MailboxSendRequest { SenderAccountId = left.AccountId, ReceiverAccountId = right.AccountId };
                var bound = core.Copy(); bound.ItemId = 3330;
                check("deadline does not bypass nontradable PVF attach type", !InventoryExchangeCommitService.CanOffer(bound, 1, left.AccountId, right.AccountId));
                check("mail still rejects unexpired timed attachment", MailboxSendPolicy.ValidateAttachment(mail, core) == MailboxSendError.LimitedPeriodItem);
                var expired = core.Copy(); expired.ExpireTime = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                check("already expired item rejected", !InventoryExchangeCommitService.CanOffer(expired, 1, left.AccountId, right.AccountId));
                check("mail retains expired-item error", MailboxSendPolicy.ValidateAttachment(mail, expired) == MailboxSendError.ExpiredItem);
                var permanent = core.Copy(); permanent.ExpireTime = 0;
                check("mail and trade share unchanged permanent transfer policy", MailboxSendPolicy.ValidateAttachment(mail, permanent) == MailboxSendError.None
                    && InventoryExchangeCommitService.CanOffer(permanent, 1, left.AccountId, right.AccountId));
                var restricted = core.Copy(); restricted.TradeRestriction = 1;
                var locked = core.Copy(); locked.EquipmentLockId = 1;
                check("deadline does not bypass instance restrictions", !InventoryExchangeCommitService.CanOffer(restricted, 1, left.AccountId, right.AccountId)
                    && !InventoryExchangeCommitService.CanOffer(locked, 1, left.AccountId, right.AccountId));
                if (!allowed) return;
                var trade = new ItemTradeSession(left, right, 0); trade.Accept(right);
                check("timed quote preserves expiry on wire", trade.Offer(0, slot, itemId, 1, out var tradeSlot, out var projection)
                    && projection.ExpireTime == expiration
                    && BitConverter.ToInt32(ItemTradePacketBuilder.Item(tradeSlot, projection), 15 + 43) == expiration);
                foreach (byte state in new byte[] { 5, 1 }) { trade.Advance(0, state); trade.Advance(1, state); }
                trade.Advance(0, 3);
                check("timed transfer commits without resetting deadline", trade.Advance(1, 3) == ItemTradeAdvance.Committed
                    && left.Inventory.CountMainItem(itemId) == 1 && right.Inventory.CountMainItem(itemId) == 1
                    && left.Inventory.GetItem(InventoryListType.Main, slot).ExpireTime == expiration);
                var receiverSlot = right.Inventory.GetItems(InventoryListType.Main).Single(x => x.Value.ItemId == itemId).Key;
                var received = right.Inventory.GetItem(InventoryListType.Main, receiverSlot).Copy();
                var expected = core.Copy(); expected.Count = 1;
                check("received timed instance retains full core", received.ToBytes().SequenceEqual(expected.ToBytes()));
                using (var c = db.OpenConnection())
                {
                    var reload = InventoryService.LoadFromDb(c, right.CharacterId, right.AccountId, db);
                    check("relogin preserves exact timed deadline", reload.GetItem(InventoryListType.Main, receiverSlot).ToBytes().SequenceEqual(expected.ToBytes()));
                }
                // A different expiry must never be merged away by the shared stack insertion path.
                var different = received.Copy(); different.ExpireTime++;
                right.Inventory.SetItem(InventoryListType.Main, receiverSlot, different);
                var offer = new InventoryExchangeOffer(slot, 1, left.Inventory.GetItem(InventoryListType.Main, slot).Copy());
                check("different timed deadlines cannot merge or debit", !InventoryExchangeCommitService.TryCommit(left,
                    new[] { offer }, 1, right, Array.Empty<InventoryExchangeOffer>(), 0)
                    && left.Inventory.CountMainItem(itemId) == 1 && right.Inventory.CountMainItem(itemId) == 1
                    && right.Inventory.GetItem(InventoryListType.Main, receiverSlot).ExpireTime == expiration + 1);
                right.Inventory.SetItem(InventoryListType.Main, receiverSlot, received);
                InventoryPersistenceService.SaveDirty(right);
                db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                    cmd.CommandText = "CREATE TRIGGER reject_timed_trade BEFORE UPDATE ON character_inventory_items WHEN NEW.character_id=62002 BEGIN SELECT RAISE(ABORT,'timed-trade-test'); END;"; cmd.ExecuteNonQuery(); });
                bool failed = !InventoryExchangeCommitService.TryCommit(left, new[] { offer }, 1, right, Array.Empty<InventoryExchangeOffer>(), 0);
                db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "DROP TRIGGER reject_timed_trade;"; cmd.ExecuteNonQuery(); });
                check("timed transfer rollback restores both exact deadlines", failed
                    && left.Inventory.GetItem(InventoryListType.Main, slot).ToBytes().SequenceEqual(expected.ToBytes())
                    && right.Inventory.GetItem(InventoryListType.Main, receiverSlot).ToBytes().SequenceEqual(expected.ToBytes()));
                var soon = expected.Copy(); soon.ExpireTime = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + 2;
                left.Inventory.SetItem(InventoryListType.Main, slot, soon);
                var expiring = new ItemTradeSession(left, right, 0); expiring.Accept(right);
                check("active timed item may be quoted before expiry", expiring.Offer(0, slot, itemId, 1, out _, out _)
                    && expiring.Offer(0, 0, 0, 1, out _, out _));
                foreach (byte state in new byte[] { 5, 1 }) { expiring.Advance(0, state); expiring.Advance(1, state); }
                expiring.Advance(0, 3);
                await Task.Delay(TimeSpan.FromSeconds(3));
                check("expiry between quote and final confirm cancels all assets", expiring.Advance(1, 3) == ItemTradeAdvance.Canceled
                    && right.Inventory.CountMainItem(itemId) == 1 && left.Inventory.GetMainVirtualCount(0).Count == 9900
                    && right.Inventory.GetMainVirtualCount(0).Count == 10100);
                left.Inventory.SetItem(InventoryListType.Main, slot, expected);
            }
            finally
            {
                for (int i = 0; i < owners.Length; i++)
                {
                    var inventory = owners[i].Inventory;
                    foreach (var entry in inventory.GetItems(InventoryListType.Main).Where(x => x.Value.ItemId == itemId).ToArray())
                        inventory.RemoveItem(InventoryListType.Main, entry.Key);
                    foreach (var entry in originals[i]) inventory.SetItem(InventoryListType.Main, entry.Key, entry.Value);
                    InventoryPersistenceService.SaveDirty(owners[i]);
                }
            }
        }

        private static async Task VerifyWire(GameDatabase db, SessionDirectory sessions, Peer a, Peer b,
            InventoryLease left, InventoryLease right, Action<string, bool> check)
        {
            using var runtime = new ServerRuntimeBuilder(db);
            var protocol = runtime.BuildGameProtocolHandler(sessions);
            var social = (GameProtocolSocialHandlers)typeof(ServerRuntimeBuilder)
                .GetField("_gameProtocolSocialHandlers", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(runtime);
            var handler = social.Trade;
            byte[] PeerBody(ushort uid, bool refuse = false)
            {
                var w = new GamePacketWriter(); w.WriteUInt16(uid); w.WriteByte(1); w.WriteInt32(123);
                if (refuse) w.WriteUInt16(0x85); return w.ToArray();
            }
            await handler.Respond(b.Session, default, PeerBody(a.Session.Player.UserId));
            check("unsolicited acceptance emits nothing", a.Drain().Count == 0 && b.Drain().Count == 0);
            await protocol.OnPacketReceived_86JP(a.Session, new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.REQUEST_PEER }, PeerBody(b.Session.Player.UserId));
            check("real dispatch emits request ACK and invite separately", a.Drain().Single().SequenceEqual(ItemTradePacketBuilder.PeerAck(CmdPacketTypeA21.REQUEST_PEER))
                && b.Drain().Single().SequenceEqual(ItemTradePacketBuilder.Invite(a.Session.Player.UserId, 123)));
            await handler.Respond(b.Session, default, PeerBody(a.Session.Player.UserId, true));
            check("refusal reaches inviter only", a.Drain().Single().SequenceEqual(ItemTradePacketBuilder.PeerAck(CmdPacketTypeA21.RESPONSE_PEER, 0x85)) && b.Drain().Count == 0);
            await handler.Request(a.Session, default, PeerBody(b.Session.Player.UserId)); a.Drain(); b.Drain();
            await protocol.OnPacketReceived_86JP(b.Session, new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.RESPONSE_PEER }, PeerBody(a.Session.Player.UserId));
            check("accept opens both trade windows with distinct layouts", a.Drain().Single().SequenceEqual(ItemTradePacketBuilder.Accepted(b.Session.Player.UserId, 123, false))
                && b.Drain().Single().SequenceEqual(ItemTradePacketBuilder.Accepted(a.Session.Player.UserId, 0, true)));
            var source = left.Inventory.GetItems(InventoryListType.Main).First(x => x.Value.ItemId == 3030).Key;
            var move = Convert.FromHexString("000A00A0280000010000000400000000000000000000FFFFFFFF0000");
            BitConverter.GetBytes(source).CopyTo(move, 1); BitConverter.GetBytes(3030).CopyTo(move, 3); BitConverter.GetBytes(1).CopyTo(move, 7);
            await protocol.OnPacketReceived_86JP(a.Session, new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.MOVE_ITEMSPACE }, move);
            check("move ACK and corrected opponent item projection", a.Drain().Single().Length == 26 && b.Drain().Single().Length == 116);
            var withdraw = (byte[])move.Clone(); withdraw[0] = 4; withdraw[11] = 0;
            BitConverter.GetBytes((short)3).CopyTo(withdraw, 1);
            await handler.TryMove(a.Session, default, withdraw);
            check("withdraw clears opponent projection without changing assets", a.Drain().Single().Length == 26
                && BitConverter.ToInt32(b.Drain().Single(), 17) == -1 && left.Inventory.CountMainItem(3030) == 12);
            await handler.TryMove(a.Session, default, move); a.Drain(); b.Drain();
            var gold = Convert.FromHexString("00000000000000140000000400000000000000000000FFFFFFFF0000");
            await protocol.OnPacketReceived_86JP(a.Session, new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.MOVE_ITEMSPACE }, gold);
            var goldAck = a.Drain().Single(); var goldNoti = b.Drain().Single();
            check("live gold request ACK and opponent amount without early debit", goldAck.Length == 26 && goldAck[15] == 1
                && BitConverter.ToInt32(goldAck, 19) == 20 && goldNoti.Length == 116
                && BitConverter.ToUInt16(goldNoti, 15) == 0 && BitConverter.ToInt32(goldNoti, 21) == 20
                && left.Inventory.GetMainVirtualCount(0).Count == 9900);
            await handler.TryMove(a.Session, default, gold[..27]);
            var rejected = a.Drain().Single();
            check("malformed trade request reports invalid operation, not cargo full", rejected.Length == 19
                && rejected[15] == 0 && rejected[16] == MoveItemSpaceAckBuilder.InvalidOperationErrorCode
                && b.Drain().Count == 0 && left.Inventory.GetMainVirtualCount(0).Count == 9900);
            gold[0] = 4; gold[11] = 0;
            await handler.TryMove(a.Session, default, gold);
            check("28-byte gold withdrawal restores quote to zero", a.Drain().Single()[15] == 1
                && BitConverter.ToInt32(b.Drain().Single(), 21) == 0 && left.Inventory.GetMainVirtualCount(0).Count == 9900);
            foreach (byte state in new byte[] { 5, 1, 3 })
            {
                await protocol.OnPacketReceived_86JP(a.Session, new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.SET_ITEMTRADE_STATE }, new[] { state });
                await protocol.OnPacketReceived_86JP(b.Session, new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.SET_ITEMTRADE_STATE }, new[] { state });
            }
            var ap = a.Drain(); var bp = b.Drain();
            check("wire settlement ends with FINISH then authoritative ITEM_LIST", ap.Count == 7 && bp.Count == 7
                && BitConverter.ToUInt16(ap[^2], 1) == (ushort)NotiPacketTypeA21.FINISH_ITEMTRADE
                && BitConverter.ToUInt16(ap[^1], 1) == (ushort)NotiPacketTypeA21.ITEM_LIST
                && left.Inventory.CountMainItem(3030) == 11 && right.Inventory.CountMainItem(3030) == 8);
            await handler.State(b.Session, default, new byte[] { 3 });
            check("wire duplicate confirmation is silent", a.Drain().Count == 0 && b.Drain().Count == 0);
            b.Session.Player.CurAreaId = 2;
            await handler.Request(a.Session, default, PeerBody(b.Session.Player.UserId));
            check("different areas cannot trade", a.Drain().Single()[15] == 0 && b.Drain().Count == 0);
            b.Session.Player.CurAreaId = 1;
            await handler.Request(a.Session, default, PeerBody(b.Session.Player.UserId)); a.Drain(); b.Drain();
            await handler.Respond(b.Session, default, PeerBody(a.Session.Player.UserId)); a.Drain(); b.Drain();
            await handler.CancelBeforeTransition(a.Session, (ushort)CmdPacketTypeA21.ENTER_SELECT_DUNGEON);
            check("dungeon transition cancels both before leaving town", a.Drain().First().SequenceEqual(ItemTradePacketBuilder.Closed(false))
                && b.Drain().First().SequenceEqual(ItemTradePacketBuilder.Closed(false)));

            var sendLock = (SemaphoreSlim)typeof(EnhancedClientSession).GetField("_sendLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(b.Session);
            await sendLock.WaitAsync();
            var pending = handler.Request(a.Session, default, PeerBody(b.Session.Player.UserId));
            // Request publishes the inviter ACK before waiting for the invitee send lock.
            // Replacing the lease must invalidate that already-built invitation.
            check("race fixture reaches inviter publication", a.Drain().Single()[15] == 1);
            using (var c = db.OpenConnection())
                InventoryContext.Register(b.Session.SessionId, InventoryService.LoadFromDb(c, 62002, 62002, db));
            sendLock.Release();
            await pending;
            var canceled = b.Drain(); a.Drain();
            check("lease replacement while sending suppresses stale invitation", canceled.All(p => BitConverter.ToUInt16(p, 1) != (ushort)NotiPacketTypeA21.REQUEST_PEER));
            // Restore this test's original lease as the current generation for subsequent checks.
            InventoryContext.Register(b.Session.SessionId, right.Inventory);
            await handler.Request(a.Session, default, PeerBody(b.Session.Player.UserId)); a.Drain(); b.Drain();
            await handler.Respond(b.Session, default, PeerBody(a.Session.Player.UserId)); a.Drain(); b.Drain();
            await sessions.UnregisterAsync(62002, b.Session);
            check("disconnect cancels surviving client and refreshes inventory", a.Drain().First().SequenceEqual(ItemTradePacketBuilder.Closed(false)) && b.Drain().Count == 0);
            sessions.Register(62002, b.Session);
        }
    }
}
