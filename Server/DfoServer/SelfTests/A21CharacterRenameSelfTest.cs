using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.Names;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using Peer = DfoServer.SelfTests.A21OneToOneChatSelfTest.Peer;

namespace DfoServer.SelfTests
{
    // 角色更名卡自测（临时 SQLite + loopback socket，不连接运行服务）:
    //   - PVF 卡定义: 最低等级、Main 内卡是否启用选角改名入口（USERINFO2 两字节）。
    //   - 请求解析: roster index + 旧名 DSTR + 新名 DSTR + language 尾字节的严格长度。
    //   - 提交: 归属账号/选角状态/无在线租约/最小等级/90 天冷却/重名与旧名保留/卡数量与过期。
    //   - 事务: 名字、双方好友键、历史、卡扣减同批提交; 历史写入失败整体回滚。
    //   - 事务后: 好友与黑名单刷新、公会刷新、租约释放、并发只成功一次。
    // 隔离: 通过反射把 UnitedFriendSystem 的仓储指向临时库(走 item_schema.sql 新库建表)。
    public static class A21CharacterRenameSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string label, bool success)
            {
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (success) passed++; else failed++;
            }

            var originalRepository = GetStatic("_repository");
            var originalLoaded = GetStatic("_loaded");
            var originalCharacters = GetStatic("_characterRepository");
            var friendsRef = (Dictionary<string, HashSet<string>>)GetStatic("Friends");
            var originalSnapshot = friendsRef.ToDictionary(
                kv => kv.Key,
                kv => new HashSet<string>(kv.Value, StringComparer.Ordinal));
            string path = Path.Combine(Path.GetTempPath(), $"character-rename-{Guid.NewGuid():N}.db");
            try
            {
                RunAsync(path, Check).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Check(ex.ToString(), false);
            }
            finally
            {
                friendsRef.Clear();
                foreach (var kv in originalSnapshot)
                    friendsRef[kv.Key] = kv.Value;
                SetStatic("_repository", originalRepository);
                SetStatic("_loaded", originalLoaded);
                SetStatic("_characterRepository", originalCharacters);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix))
                        File.Delete(path + suffix);
            }

            Console.WriteLine($"A21_CHARACTER_RENAME: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static async Task RunAsync(string path, Action<string, bool> check)
        {
            var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
            database.Write((c, t) =>
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = t;
                cmd.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash,created_at) VALUES(100,'rename','',CURRENT_TIMESTAMP);";
                cmd.ExecuteNonQuery();
            });

            SetStatic("_repository", new UnitedFriendRepository(path, ServerPaths.SchemaFilePath));
            SetStatic("_characterRepository", new SqliteCharacterRepository(database));
            SetStatic("_loaded", false);
            ((Dictionary<string, HashSet<string>>)GetStatic("Friends")).Clear();

            var characters = new SqliteCharacterRepository(database);
            int Add(string name, byte level = 85) => characters.Create(new CharacterRecord
            {
                AccountId = 100,
                Name = Bytes(name),
                Level = level,
            });
            int first = Add("renameold");
            int second = Add("occupied");
            int low = Add("lowlevel", 1);
            var sessions = new SessionDirectory();
            var transitions = new CharacterTransitionCoordinator(sessions);
            var repository = new CharacterRenameRepository(database);
            var handler = new CharacterRenameHandler(database, characters, sessions, transitions);
            var friends = new UnitedFriendRepository(path, ServerPaths.SchemaFilePath);
            friends.InsertRelation("renameold", "occupied");
            friends.InsertRelation("occupied", "renameold");
            using var peer = await Peer.Create(sessions, 0, "selecting", port: 10011, register: false);
            peer.Session.Account = new AccountRecord { AccountId = 100 };
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            InventoryService Load(int id)
            {
                using var c = database.OpenConnection();
                return InventoryService.LoadFromDb(c, id, 100, database);
            }
            void Stock(int id, int count, int item = CharacterRenameRepository.CardId, int expiry = 0)
            {
                var lease = InventoryContext.Register(Guid.NewGuid(), Load(id));
                try
                {
                    lock (lease.SyncRoot)
                        lease.Inventory.SetItem(InventoryListType.Main, 65,
                            new ItemCore { ItemKind = ItemCore.KindConsumable, ItemId = item, Count = count, ExpireTime = expiry });
                    if (!InventoryPersistenceService.SaveDirty(lease))
                        throw new Exception("stock failed");
                }
                finally
                {
                    InventoryContext.Unregister(lease.SessionId, id);
                }
            }
            int Count(int id) => Load(id).GetItem(InventoryListType.Main, 65)?.Count ?? 0;
            bool Flags(int id, byte expected)
            {
                var character = characters.GetById(id);
                var body = AccountCharacterListBodyBuilder.Build(
                    new[] { character }, null, out _, accountId: 100, database: database);
                // 头 16 字节 + count:u16 + slot:u16 + 名字 DSTR 之后的两字节即改名入口/CN 卡来源选择器。
                var offset = 24 + character.Name.Length;
                return body[offset] == expected && body[offset + 1] == expected;
            }
            async Task<bool> Request(string oldName, string newName, byte index = 0, byte[] raw = null)
            {
                await handler.Handle(peer.Session,
                    new GamePacketHeader { type = (ushort)CmdPacketTypeA21.CHANGE_CHARAC_NAME },
                    raw ?? Body(index, oldName, newName));
                var packets = peer.Drain();
                if (packets.Count != 1)
                    return false;
                var packet = packets[0];
                return packet[0] == 1
                    && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.CHANGE_CHARAC_NAME
                    && packet[15] == 1;
            }

            check("current PVF identifies CN card and minimum level", CharacterRenameRepository.MinimumLevel == 30);
            check("missing card leaves both native selection flags clear", Flags(first, 0));
            check("missing source cannot rename",
                !await Request("renameold", "renamenew") && characters.GetById(first).Name.SequenceEqual(Bytes("renameold")));

            Stock(first, 2);
            Stock(low, 1);
            check("CN source enables both flags only for an eligible character",
                Flags(first, 1) && Flags(second, 0) && Flags(low, 0));
            check("pending card prevents a repeated charged purchase", !repository.CanPurchase(Load(first)));
            check("low-level card cannot be used", !await Request("lowlevel", "lownew", 2) && Count(low) == 1);
            check("duplicate name preserves card and old name",
                !await Request("renameold", "occupied") && Count(first) == 2);
            check("no-op request cannot consume", !await Request("renameold", "renameold") && Count(first) == 2);
            check("wrong roster index cannot rename another character",
                !await Request("renameold", "renamenew", 1) && Count(first) == 2);
            check("old name spoof rejected", !await Request("wrongold", "renamenew") && Count(first) == 2);
            check("invalid name rejected without spending", !await Request("renameold", "bad name") && Count(first) == 2);

            var valid = Body(0, "renameold", "renamenew");
            check("request parser enforces trailing language byte and exact length",
                CharacterRenameHandler.TryParse(valid, out _, out _, out _)
                && !CharacterRenameHandler.TryParse(valid[..^1], out _, out _, out _)
                && !CharacterRenameHandler.TryParse(valid.Concat(new byte[] { 0 }).ToArray(), out _, out _, out _));
            check("malformed request rejected without spending", !await Request("", "", raw: valid[..^1]) && Count(first) == 2);

            peer.Session.Account.AccountId = 101;
            check("another account cannot rename a foreign character",
                !await Request("renameold", "renamenew") && Count(first) == 2);
            peer.Session.Account.AccountId = 100;

            var online = InventoryContext.Register(Guid.NewGuid(), Load(first));
            await handler.Handle(peer.Session, new GamePacketHeader(), valid);
            check("active inventory lease is never replaced by rename",
                InventoryContext.IsCurrentLease(online, online.SessionId, first) && !peer.Drain().Any());
            InventoryContext.Unregister(online.SessionId, first);

            Stock(first, 1, expiry: (int)now - 1);
            check("expired card cannot enable entry or commit",
                Flags(first, 0) && !await Request("renameold", "renamenew"));
            Stock(first, 1, item: 36);
            check("another cash item cannot pay for rename",
                Flags(first, 0) && !await Request("renameold", "renamenew") && Count(first) == 1);

            Stock(first, 2);
            database.Write((c, t) =>
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = t;
                cmd.CommandText = "CREATE TRIGGER rename_fail BEFORE INSERT ON character_rename_history "
                    + "BEGIN SELECT RAISE(ABORT,'rename rollback test'); END;";
                cmd.ExecuteNonQuery();
            });
            check("history write failure rolls name, friends, card back",
                !await Request("renameold", "renamenew")
                && characters.GetById(first).Name.SequenceEqual(Bytes("renameold")) && Count(first) == 2
                && friends.LoadAll()["occupied"].Contains("renameold")
                && !repository.IsNameReserved(Bytes("renameold"), now)
                && Flags(first, 1));
            database.Write((c, t) =>
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = t;
                cmd.CommandText = "DROP TRIGGER rename_fail;";
                cmd.ExecuteNonQuery();
            });

            check("successful native request durably changes name and spends exactly one",
                await Request("renameold", "renamenew")
                && characters.GetById(first).Name.SequenceEqual(Bytes("renamenew")) && Count(first) == 1);
            check("friend relations preserve both directions",
                friends.LoadAll()["occupied"].Contains("renamenew") && friends.LoadAll()["renamenew"].Contains("occupied"));
            check("scoped selection lease is released after success and failure", !InventoryContext.TryGetLease(first, out _));
            check("relog flags and repeat purchase respect durable 90 day cooldown",
                Flags(first, 0) && !repository.CanPurchase(Load(first))
                && !await Request("renamenew", "renameagain") && Count(first) == 1);
            check("old name reserved for exactly 180 days",
                repository.IsNameReserved(Bytes("renameold"), now + 179 * 86400)
                && !repository.IsNameReserved(Bytes("renameold"), now + 181 * 86400));
            check("remaining owned card becomes eligible after cooldown",
                repository.CanRename(first, 85, now + 91 * 86400));
            check("unrelated character remains unchanged", characters.GetById(second).Name.SequenceEqual(Bytes("occupied")));

            bool reservedRejected = false;
            try { Add("renameold"); }
            catch (InvalidOperationException) { reservedRejected = true; }
            check("character creation cannot reclaim a reserved old name", reservedRejected);

            int race = Add("renamerace");
            Stock(race, 1);
            await Task.WhenAll(
                handler.Handle(peer.Session, new GamePacketHeader(), Body(3, "renamerace", "racewinner")),
                handler.Handle(peer.Session, new GamePacketHeader(), Body(3, "renamerace", "raceloser")));
            var packets = peer.Drain();
            check("concurrent requests consume the final card only once",
                packets.Count == 2 && packets.Count(p => p[15] == 1) == 1 && Count(race) == 0
                && !InventoryContext.TryGetLease(race, out _));
        }

        private static byte[] Bytes(string value) => ClientTextEncoding.GetBytes(value);

        private static byte[] Body(byte index, string oldName, string newName)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(index);
            writer.WriteDstr(Bytes(oldName));
            writer.WriteDstr(Bytes(newName));
            writer.WriteByte(0);
            return writer.ToArray();
        }

        private static object GetStatic(string name)
            => typeof(UnitedFriendSystem)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);

        private static void SetStatic(string name, object value)
            => typeof(UnitedFriendSystem)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, value);
    }
}
