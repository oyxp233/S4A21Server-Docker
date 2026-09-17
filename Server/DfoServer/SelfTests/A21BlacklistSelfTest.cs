using DfoServer.Game.Friends;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Peer = DfoServer.SelfTests.A21OneToOneChatSelfTest.Peer;

namespace DfoServer.SelfTests
{
    public static class A21BlacklistSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string label, bool success)
            { Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}"); if (success) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), $"blacklist-{Guid.NewGuid():N}.db");
            try { RunAsync(path, Check).GetAwaiter().GetResult(); }
            catch (Exception ex) { Check(ex.ToString(), false); }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"A21_BLACKLIST: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static async Task RunAsync(string path, Action<string, bool> check)
        {
            var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
            void Sql(string sql) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
            long Scalar(string sql) => db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); });
            void StoreName(int id, string name) => db.Write((c, t) =>
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = t;
                cmd.CommandText = "UPDATE characters SET name=@name WHERE character_id=@id;";
                cmd.Parameters.AddWithValue("@name", ClientTextEncoding.GetBytes(name));
                cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery();
            });
            Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(101,'blacklist-test','');" +
                "INSERT INTO characters(character_id,account_id,name) VALUES(101,101,'甲'),(102,101,'乙'),(103,101,'丙');");
            var repo = new BlacklistRepository(db);
            check("TEXT character names remain compatible", repo.Add(101, "乙").Status == BlacklistResult.Success
                && repo.List(101).Single().Name == "乙" && repo.Remove(101, "乙"));
            StoreName(101, "甲"); StoreName(102, "乙"); StoreName(103, "GUMA");
            check("character fixtures use production GBK BLOB storage", Scalar("SELECT COUNT(*) FROM characters WHERE typeof(name)='blob'") == 3);
            check("ASCII BLOB add/list/delete round trips", repo.Add(101, "GUMA").Status == BlacklistResult.Success
                && repo.List(101).Single().Name == "GUMA" && repo.Remove(101, "GUMA"));
            check("new database has empty blacklist at current schema", repo.List(101).Count == 0 && Scalar("PRAGMA user_version") == Sqlite.SqliteMigrations.CurrentVersion);
            Sql("DROP TABLE character_blacklist; PRAGMA user_version=31; UPDATE schema_metadata SET schema_version=31;");
            Sql("CREATE TRIGGER reject_blacklist_migration BEFORE UPDATE OF schema_version ON schema_metadata BEGIN SELECT RAISE(ABORT,'test'); END;");
            bool migrationFailed = false;
            using (var c = db.OpenConnection())
                try { Sqlite.SqliteMigrations.Apply(c); } catch (SqliteException) { migrationFailed = true; }
            check("failed migration rolls back table and versions", migrationFailed && Scalar("PRAGMA user_version") == 31
                && Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='character_blacklist'") == 0);
            Sql("DROP TRIGGER reject_blacklist_migration;");
            using (var c = db.OpenConnection()) { Sqlite.SqliteMigrations.Apply(c); Sqlite.SqliteMigrations.Apply(c); }
            check("v31 migration preserves characters and advances both versions", Scalar("SELECT COUNT(*) FROM characters") == 3
                && Scalar("PRAGMA user_version") == 32 && Scalar("SELECT schema_version FROM schema_metadata") == 32);
            check("captured permanent request decodes strict GBK", BlacklistHandler.TryParseName(Convert.FromHexString("06000000BAA3C9AADEB1"), out var captured) && captured.Length == 3);
            foreach (var bytes in new[] { Array.Empty<byte>(), Name(""), Name("甲").Concat(new byte[] { 0 }).ToArray(),
                new byte[] { 255,255,255,127 }, new byte[] { 1,0,0,0,0x81 }, Name("\0"), Name(new string('中', 15)) })
                check("invalid blacklist name rejected", !BlacklistHandler.TryParseName(bytes, out _));
            check("self and nonexistent targets rejected", repo.Add(101, "甲").Status == BlacklistResult.Self && repo.Add(101, "不存在").Status == BlacklistResult.NotFound);
            check("offline GBK BLOB add creates only directed relation", repo.Add(101, "乙").Status == BlacklistResult.Success && repo.IsBlocked(101, 102) && !repo.IsBlocked(102, 101));
            check("duplicate does not duplicate durable row", repo.Add(101, "乙").Status == BlacklistResult.Duplicate && repo.List(101).Count == 1);
            check("native localized duplicate and missing-name errors are distinct",
                BlacklistHandler.AddAck(BlacklistResult.Duplicate, "乙").AsSpan(15).SequenceEqual(new byte[] { 0, 74 })
                && BlacklistHandler.AddAck(BlacklistResult.NotFound, "无").AsSpan(15).SequenceEqual(new byte[] { 0, 76 }));
            check("self rejection does not claim target is GM",
                BlacklistHandler.AddAck(BlacklistResult.Self, "甲").AsSpan(15).SequenceEqual(new byte[] { 0, 0 }));
            check("reopened repository restores blacklist", new BlacklistRepository(new GameDatabase(path, ServerPaths.SchemaFilePath)).IsBlocked(101, 102));
            StoreName(102, "改名");
            check("rename keeps ID relation and projects current name", repo.IsBlocked(101, 102) && repo.List(101).Single().Name == "改名");
            Sql("UPDATE characters SET delete_flag=1 WHERE character_id=102;");
            check("deleted characters do not block or occupy visible list", !repo.IsBlocked(101, 102) && repo.List(101).Count == 0);
            StoreName(102, "乙"); Sql("UPDATE characters SET delete_flag=0 WHERE character_id=102;");
            check("delete and duplicate delete have stable outcomes", repo.Remove(101, "乙") && !repo.Remove(101, "乙"));
            Sql("CREATE TRIGGER blacklist_fail BEFORE INSERT ON character_blacklist BEGIN SELECT RAISE(ABORT,'test'); END;");
            bool failedWrite = false;
            try { repo.Add(101, "乙"); } catch (SqliteException) { failedWrite = true; }
            check("failed write publishes no relation", failedWrite && !repo.IsBlocked(101, 102));
            Sql("DROP TRIGGER blacklist_fail;");
            for (int i = 200; i < 265; i++) Sql($"INSERT INTO characters(character_id,account_id,name) VALUES({i},101,'目标{i}');");
            await Task.WhenAll(Enumerable.Range(200, 65).Select(i => Task.Run(() => repo.Add(101, $"目标{i}"))));
            check("concurrent additions cannot exceed native 64-entry capacity", repo.List(101).Count == 64 && Scalar("SELECT COUNT(*) FROM character_blacklist") == 64);
            Sql("DELETE FROM character_blacklist;");
            var inventory = new Game.Inventory.InventoryService(101, 101, db);
            inventory.SetMainVirtualCount(0, 0, 10000);
            var lease = Game.Inventory.InventoryContext.Register(Guid.NewGuid(), inventory);
            try
            {
                check("mail test inventory persisted", Game.Inventory.InventoryPersistenceService.SaveDirty(lease));
                var mail = new Game.Mailbox.MailboxRepository(db);
                var request = new Game.Mailbox.MailboxSendRequest { SenderCharacterId = 101, SenderAccountId = 101,
                    SenderName = "甲", ReceiverCharacterId = 102, ReceiverAccountId = 101, ReceiverName = "乙",
                    Text = "测试", IdempotencyKey = "blacklist-test" };
                repo.Add(102, "甲");
                var blocked = mail.SendMail(request, lease);
                check("blocked mail has no charge or durable delivery", !blocked.Success
                    && blocked.Error == Game.Mailbox.MailboxSendError.Blacklisted
                    && inventory.GetMainVirtualCount(0).Count == 10000 && Scalar("SELECT COUNT(*) FROM mailbox_messages") == 0);
                repo.Remove(102, "甲");
                var sent = mail.SendMail(request, lease);
                check("unblocked mail resumes normal transaction", sent.Success && inventory.GetMainVirtualCount(0).Count < 10000);
                repo.Add(102, "甲");
                var replay = mail.SendMail(request, lease);
                check("existing committed mail retry stays idempotent after blocking", replay.Success && replay.MessageId == sent.MessageId
                    && inventory.GetMainVirtualCount(0).Count == sent.UpdatedGold);
                repo.Remove(102, "甲");
            }
            finally { Game.Inventory.InventoryContext.Unregister(lease.SessionId); }
            var date = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Local);
            var list = BlacklistHandler.BuildList(new[] { new BlacklistEntry(102, "乙", date.ToUniversalTime()) });
            var expected = new GamePacketWriter(); expected.WriteByte(1); expected.WriteByte(1); expected.WriteClientDstr("乙");
            expected.WriteByte(126); expected.WriteByte(8); expected.WriteByte(17);
            check("list matches native count/DSTR/tm_year/tm_mon/tm_mday reader", list.AsSpan(15).SequenceEqual(expected.ToArray()));

            var sessions = new SessionDirectory();
            using var a = await Peer.Create(sessions, 101, "甲", GameNetworkConfig.NormalGamePort);
            using var b = await Peer.Create(sessions, 102, "乙", GameNetworkConfig.NormalGamePort);
            var transitions = new CharacterTransitionCoordinator(sessions);
            var projection = new BlacklistProjection(repo, sessions);
            var blacklist = new BlacklistHandler(repo, transitions, projection);
            var registry = new GameCommandRegistry(); registry.RegisterGroup("blacklist", blacklist.RegisterHandlers);
            check("all three native commands registered", registry.Count == 3);
            Task Dispatch(Peer peer, CmdPacketTypeA21 command, byte[] body)
            { registry.TryGetValue((ushort)command, out var handler); return handler(peer.Session, new GamePacketHeader { type = (ushort)command }, body); }
            using var chat = new ChatHandler(sessions, new PartyManager(), transitions); chat.ConfigureBlacklist(repo);
            await Dispatch(b, CmdPacketTypeA21.REGISITER_TO_BLACKLIST, Name("甲"));
            check("add ACK contains success followed by exact target name", b.Drain().Single().SequenceEqual(BlacklistHandler.Ack(CmdPacketTypeA21.REGISITER_TO_BLACKLIST, 0, "甲")));
            await Dispatch(b, CmdPacketTypeA21.REQUEST_BLACKLIST, Array.Empty<byte>());
            check("list request returns durable entries", b.Drain().Single().SequenceEqual(BlacklistHandler.BuildList(repo.List(102))));
            registry.RegisterGroup("user-channel", new UserChannelHandler(sessions, transitions).RegisterHandlers);
            var query = new GamePacketWriter(); query.WriteByte(1); query.WriteClientDstr("甲");
            await Dispatch(b, CmdPacketTypeA21.REQUEST_USER_CHANNEL, query.ToArray());
            var queryAck = new GamePacketWriter(); queryAck.WriteByte(1); queryAck.WriteByte(1); queryAck.WriteClientDstr("甲");
            queryAck.WriteByte(1); queryAck.WriteByte(GameNetworkConfig.NormalChannelIndex);
            check("permanent blacklist can query channel without removing its blocking relation",
                b.Drain().Single().SequenceEqual(GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.REQUEST_USER_CHANNEL, queryAck.ToArray()))
                && a.Drain().Count == 0 && repo.IsBlocked(102, 101));
            await chat.Handle_CREATE_GROUP(a.Session, default, Name("乙"));
            check("blacklisted sender cannot open recipient window", b.Drain().Count == 0 && a.Drain().Single()[15] == 77);
            await chat.Handle_SEND_MESSAGE(a.Session, default, Message(1, 0, 102));
            check("direct message blocked at server while sender echo remains", b.Drain().Count == 0 && a.Drain().Count == 1);
            var parties = new PartyManager();
            using var party = new PartyHandler(parties, new Game.Characters.SqliteCharacterRepository(db), sessions,
                characterTransitions: transitions, database: db);
            for (byte type = 0; type <= 2; type++)
            {
                var invite = new GamePacketWriter(); invite.WriteUInt16(102); invite.WriteByte(type); invite.WriteInt32(0);
                await party.Handle_REQUEST_PEER(a.Session, default, invite.ToArray());
                check("blocked party/trade/PvP request emits no recipient notification", b.Drain().Count == 0);
            }
            await Dispatch(b, CmdPacketTypeA21.DELETE_TO_BLACKLIST, Name("甲"));
            check("delete ACK matches native name consumer", b.Drain().Single().SequenceEqual(BlacklistHandler.Ack(CmdPacketTypeA21.DELETE_TO_BLACKLIST, 0, "甲")));
            check("pending invitation recorded before blocking", parties.RecordInvite(102, b.Session.SessionId, 101, a.Session.SessionId, out _));
            repo.Add(102, "甲");
            var accept = new GamePacketWriter(); accept.WriteUInt16(101); accept.WriteByte(0); accept.WriteInt32(0);
            await party.Handle_RES_PEER(b.Session, default, accept.ToArray());
            check("blocking revokes previously pending party acceptance", parties.PartyCount == 0
                && !parties.CancelInvite(102, b.Session.SessionId, 101, a.Session.SessionId));
            repo.Remove(102, "甲"); a.Drain(); b.Drain();
            var sendLock = (System.Threading.SemaphoreSlim)typeof(EnhancedClientSession)
                .GetField("_sendLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(b.Session);
            await sendLock.WaitAsync();
            var queuedMessage = chat.Handle_SEND_MESSAGE(a.Session, default, Message(1, 0, 102));
            repo.Add(102, "甲");
            sendLock.Release(); await queuedMessage;
            check("queued direct message rechecks blacklist at socket write", b.Drain().Count == 0); a.Drain();
            repo.Remove(102, "甲");
            byte[] RaidMessage(byte mode)
            {
                var writer = new GamePacketWriter(); writer.WriteByte(mode); writer.WriteUInt16(0);
                writer.WriteUInt32(0); writer.WriteClientDstr("测试"); return writer.ToArray();
            }
            var raids = new Game.Raid.RaidManager();
            Game.Raid.RaidMember RaidMember(Peer peer) => new Game.Raid.RaidMember
            {
                UserId = peer.Session.Player.UserId, CharacterId = (uint)peer.Session.Player.CharacterId,
                SessionId = peer.Session.SessionId, NameBytes = peer.Session.Player.Name
            };
            var raid = raids.Create(Name("团本"), RaidMember(a), 1);
            check("raid chat fixture joins current recipient", raids.TryAddMember(raid.RaidId, RaidMember(b), out _));
            using var raidChat = new ChatHandler(sessions, new PartyManager(), transitions, raids);
            raidChat.ConfigureBlacklist(repo);
            foreach (byte mode in new byte[] { 52, 53 })
            {
                await raidChat.Handle_SEND_MESSAGE(a.Session, default, RaidMessage(mode));
                check("raid and commander chat reach current member", b.Drain().Any(packet => IsPacket(packet, NotiPacketTypeA21.MESSAGE)));
                a.Drain();
                await sendLock.WaitAsync();
                var queuedRaid = raidChat.Handle_SEND_MESSAGE(a.Session, default, RaidMessage(mode));
                repo.Add(102, "甲");
                sendLock.Release(); await queuedRaid;
                check("queued raid chat rechecks blacklist before context and message", b.Drain().Count == 0);
                a.Drain(); repo.Remove(102, "甲");
            }
            await sendLock.WaitAsync();
            var staleRaid = raidChat.Handle_SEND_MESSAGE(a.Session, default, RaidMessage(52));
            b.Session.Player.CharacterId = 103;
            sendLock.Release(); await staleRaid;
            check("queued raid chat rejects changed recipient identity", b.Drain().Count == 0);
            b.Session.Player.CharacterId = 102; a.Drain();
            Sql("CREATE TRIGGER blacklist_fail BEFORE INSERT ON character_blacklist BEGIN SELECT RAISE(ABORT,'test'); END;");
            await Dispatch(b, CmdPacketTypeA21.REGISITER_TO_BLACKLIST, Name("甲"));
            check("failed persistence emits failure ACK without phantom block", b.Drain().Single().SequenceEqual(
                BlacklistHandler.Failure(CmdPacketTypeA21.REGISITER_TO_BLACKLIST)) && !repo.IsBlocked(102, 101));
            Sql("DROP TRIGGER blacklist_fail;");
            await chat.Handle_CREATE_GROUP(a.Session, default, Name("乙"));
            uint group = BitConverter.ToUInt32(a.Drain().Single(), 16); b.Drain();
            await chat.Handle_SEND_MESSAGE(a.Session, default, Message(43, group, 102));
            check("unblocking restores conversation delivery", b.Drain().Count == 1);
            await Dispatch(b, CmdPacketTypeA21.REGISITER_TO_BLACKLIST, Name("甲")); b.Drain();
            await chat.Handle_SEND_MESSAGE(a.Session, default, Message(43, group, 102));
            check("blocking also suppresses already-open conversation", b.Drain().Count == 0);
            await Dispatch(b, CmdPacketTypeA21.DELETE_TO_BLACKLIST, Name("甲")); b.Drain();
            using (var gate = await transitions.AcquireAsync(102))
            {
                var pending = Dispatch(b, CmdPacketTypeA21.REGISITER_TO_BLACKLIST, Name("甲"));
                b.Session.Player.CharacterId = 103;
                gate.Dispose(); await pending;
                check("waiting request cannot mutate after character switch", !repo.IsBlocked(102, 101) && !repo.IsBlocked(103, 101) && b.Drain().Count == 0);
                b.Session.Player.CharacterId = 102;
            }
            using var replacement = await Peer.Create(sessions, 102, "乙");
            await Dispatch(b, CmdPacketTypeA21.REGISITER_TO_BLACKLIST, Name("甲"));
            check("replaced session cannot modify blacklist", !repo.IsBlocked(102, 101) && b.Drain().Count == 0);

            Sql("INSERT INTO characters(character_id,account_id,name) VALUES(301,101,'屏蔽者'),(302,101,'旧名'),(303,101,'其他人');");
            StoreName(301, "屏蔽者"); StoreName(302, "旧名"); StoreName(303, "其他人");
            var renameSessions = new SessionDirectory();
            using var owner = await Peer.Create(renameSessions, 301, "屏蔽者");
            using var target = await Peer.Create(renameSessions, 302, "旧名");
            var renameTransitions = new CharacterTransitionCoordinator(renameSessions);
            var renameHandler = new BlacklistHandler(repo, renameTransitions, new BlacklistProjection(repo, renameSessions));
            Task RenameCommand(CmdPacketTypeA21 command, byte[] body) => renameHandler.Handle(owner.Session,
                new GamePacketHeader { type = (ushort)command }, body);
            using var renameChat = new ChatHandler(renameSessions, new PartyManager(), renameTransitions);
            renameChat.ConfigureBlacklist(repo);
            await RenameCommand(CmdPacketTypeA21.REGISITER_TO_BLACKLIST, Name("旧名")); owner.Drain();
            StoreName(302, "新名"); target.Session.Player.Name = ClientTextEncoding.GetBytes("新名");
            await renameChat.Handle_CREATE_GROUP(target.Session, default, Name("屏蔽者"));
            check("renamed target keeps same ID and cannot reopen chat", repo.IsBlocked(301, 302)
                && owner.Drain().Count == 0 && target.Drain().Single()[15] == 77);
            await RenameCommand(CmdPacketTypeA21.REQUEST_BLACKLIST, Array.Empty<byte>());
            var renamedPackets = owner.Drain();
            check("native merge-list refresh explicitly removes old name before publishing renamed entry",
                renamedPackets.Count == 2
                && renamedPackets[0].SequenceEqual(BlacklistHandler.Ack(CmdPacketTypeA21.DELETE_TO_BLACKLIST, 0, "旧名"))
                && renamedPackets[1].SequenceEqual(BlacklistHandler.BuildList(repo.List(301)))
                && repo.List(301).Single().Name == "新名");
            StoreName(302, "再次改名"); StoreName(303, "新名");
            repo.Add(301, "新名");
            await RenameCommand(CmdPacketTypeA21.DELETE_TO_BLACKLIST, Name("新名")); owner.Drain();
            check("deleting visible stale name removes its original ID, not the new name holder",
                !repo.IsBlocked(301, 302) && repo.IsBlocked(301, 303));
            await RenameCommand(CmdPacketTypeA21.REQUEST_BLACKLIST, Array.Empty<byte>()); owner.Drain();
            Sql("UPDATE characters SET delete_flag=1 WHERE character_id=303;");
            await RenameCommand(CmdPacketTypeA21.REQUEST_BLACKLIST, Array.Empty<byte>());
            var deletedPackets = owner.Drain();
            check("deleted target is explicitly removed from native cached list",
                deletedPackets.Count == 2 && deletedPackets[0].SequenceEqual(BlacklistHandler.Ack(CmdPacketTypeA21.DELETE_TO_BLACKLIST, 0, "新名"))
                && deletedPackets[1].AsSpan(15).SequenceEqual(new byte[] { 1, 0 }));
            await RunPresenceTests(path, db, repo, check);
        }

        private static async Task RunPresenceTests(string path, GameDatabase db, BlacklistRepository repo, Action<string, bool> check)
        {
            db.Write((c, t) =>
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = t;
                cmd.CommandText = "INSERT INTO characters(character_id,account_id,name) VALUES"
                    + "(401,101,'观察者'),(402,101,'好友黑名单'),(403,101,'仅黑名单');";
                cmd.ExecuteNonQuery();
            });
            var sessions = new SessionDirectory();
            using var owner = await Peer.Create(sessions, 401, "观察者", 10011);
            using var target = await Peer.Create(sessions, 402, "好友黑名单", 10011);
            using var blackOnly = await Peer.Create(sessions, 403, "仅黑名单", 10100);
            repo.Add(401, "好友黑名单"); repo.Add(401, "仅黑名单");
            var projection = new BlacklistProjection(repo, sessions);
            UnitedFriendSystem.ConfigureBlacklist(sessions, projection);

            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(UnitedFriendSystem);
            var repositoryField = type.GetField("_repository", flags);
            var loadedField = type.GetField("_loaded", flags);
            var charactersField = type.GetField("_characterRepository", flags);
            var oldRepository = repositoryField.GetValue(null); var oldLoaded = loadedField.GetValue(null);
            var oldCharacters = charactersField.GetValue(null);
            var friends = (System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>)type.GetField("Friends", flags).GetValue(null);
            var oldFriends = friends.ToArray();
            try
            {
                friends.Clear(); loadedField.SetValue(null, false);
                repositoryField.SetValue(null, new UnitedFriendRepository(path, ServerPaths.SchemaFilePath));
                charactersField.SetValue(null, new Game.Characters.SqliteCharacterRepository(db));
                UnitedFriendSystem.RecordFriendship("观察者", "好友黑名单");
                await projection.PublishAsync(owner.Session);
                check("blacklist snapshot contains native names/dates without invented online fields or entities",
                    owner.Drain().Single().SequenceEqual(BlacklistHandler.BuildList(repo.List(401)))
                    && target.Drain().Count == 0 && blackOnly.Drain().Count == 0);

                await UnitedFriendSystem.SendFriendListAsync(owner.Session, sessions, UnitedFriendSystem.GetFriends("观察者"));
                var reset = owner.Drain();
                check("ordinary full reset is followed by permanent blacklist restoration",
                    reset.Count == 2 && IsPacket(reset[0], NotiPacketTypeA21.UNITED_SERVER_FRIEND_INFO)
                    && BitConverter.ToInt32(reset[0], 15) == 0
                    && reset[1].SequenceEqual(BlacklistHandler.BuildList(repo.List(401))));
                check("blacklist restoration does not add ordinary or reciprocal friends",
                    UnitedFriendSystem.GetFriends("观察者").SequenceEqual(new[] { "好友黑名单" })
                    && UnitedFriendSystem.GetFriends("仅黑名单").Count == 0);
                UnitedFriendSystem.RemoveFriendship("观察者", "好友黑名单");
                await UnitedFriendSystem.SendFriendListAsync(owner.Session, sessions, Array.Empty<string>());
                var emptyFriends = owner.Drain();
                check("empty ordinary friend reset still restores permanent blacklist", emptyFriends.Count == 2
                    && emptyFriends[0].AsSpan(15).SequenceEqual(new byte[8])
                    && emptyFriends[1].SequenceEqual(BlacklistHandler.BuildList(repo.List(401))));
                UnitedFriendSystem.RecordFriendship("观察者", "好友黑名单");

                await UnitedFriendSystem.NotifyPlayerEnteredGame(target.Session, sessions); target.Drain();
                var entered = owner.Drain();
                check("ordinary friend entering retains recipient permanent blacklist flag",
                    entered.Count == 4 && IsPacket(entered[0], NotiPacketTypeA21.INOUT_UNITED_SERVER_FRIEND)
                    && entered[0][^1] == 1
                    && entered[2].SequenceEqual(BlacklistHandler.BuildList(repo.List(401))));
                await UnitedFriendSystem.NotifyPlayerDisconnected(target.Session, sessions);
                var left = owner.Drain();
                check("ordinary friend leaving retains blacklist flag and removes friend entity", left.Count == 2
                    && IsPacket(left[0], NotiPacketTypeA21.INOUT_UNITED_SERVER_FRIEND) && left[0][^1] == 1
                    && IsPacket(left[1], NotiPacketTypeA21.USER_LEAVE));

                var sendLock = (System.Threading.SemaphoreSlim)typeof(EnhancedClientSession)
                    .GetField("_sendLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(owner.Session);
                await sendLock.WaitAsync();
                var pending = UnitedFriendSystem.NotifyPlayerDisconnected(target.Session, sessions);
                repo.Remove(401, 402);
                sendLock.Release(); await pending;
                check("queued presence cannot restore a blacklist flag after unblocking", owner.Drain().Count == 0);
                await UnitedFriendSystem.NotifyPlayerDisconnected(target.Session, sessions);
                var unblocked = owner.Drain();
                check("unblocked ordinary friend notice carries zero blacklist flag",
                    unblocked.Count == 2 && unblocked[0][^1] == 0);

                await sendLock.WaitAsync();
                pending = UnitedFriendSystem.NotifyPlayerDisconnected(target.Session, sessions);
                repo.Add(401, "好友黑名单");
                sendLock.Release(); await pending;
                check("queued ordinary notice cannot clear a newly added blacklist flag", owner.Drain().Count == 0);

                await sendLock.WaitAsync();
                pending = projection.PublishAsync(owner.Session);
                repo.Remove(401, 403);
                sendLock.Release(); await pending;
                check("queued blacklist restoration cannot resurrect removed relation", owner.Drain().Count == 0);

                await sendLock.WaitAsync();
                pending = projection.PublishAsync(owner.Session);
                owner.Session.Player.UserId++;
                sendLock.Release(); await pending;
                check("queued restoration cannot overwrite a changed recipient identity", owner.Drain().Count == 0);
                owner.Session.Player.UserId--;

                using var reconnect = await Peer.Create(sessions, 401, "观察者", 10011);
                await UnitedFriendSystem.NotifyPlayerEnteredGame(reconnect.Session, sessions);
                var restored = reconnect.Drain();
                check("owner login restores permanent list after friend initialization",
                    restored.Count == 2 && restored[0].SequenceEqual(BlacklistHandler.BuildList(repo.List(401)))
                    && IsPacket(restored[1], NotiPacketTypeA21.USERINFO));
            }
            finally
            {
                friends.Clear(); foreach (var item in oldFriends) friends[item.Key] = item.Value;
                repositoryField.SetValue(null, oldRepository); loadedField.SetValue(null, oldLoaded);
                charactersField.SetValue(null, oldCharacters);
            }
        }

        private static bool IsPacket(byte[] packet, NotiPacketTypeA21 type)
            => packet[0] == 0 && BitConverter.ToUInt16(packet, 1) == (ushort)type;

        private static byte[] Name(string text) { var w = new GamePacketWriter(); w.WriteClientDstr(text); return w.ToArray(); }
        private static byte[] Message(byte mode, uint group, ushort target)
        {
            var w = new GamePacketWriter(); w.WriteByte(mode); w.WriteUInt16(target); w.WriteUInt32(group);
            w.WriteClientDstr("测试"); w.WriteClientDstr("乙"); w.WriteByte(0); return w.ToArray();
        }
    }
}
