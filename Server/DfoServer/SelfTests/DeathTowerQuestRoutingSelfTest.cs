using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class DeathTowerQuestRoutingSelfTest
    {
        private const int CharacterId = 484001;
        private const int AccountId = 484001;
        private const ushort AwakeningQuestId = 157;
        private const int DeathTowerDungeonId = 11000;
        private const int Floor30MapId = 30030;

        public static int Run()
        {
            Console.WriteLine("=== DEATH_TOWER_QUEST_ROUTING selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "death-tower-quest-routing.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var connStr = SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
            SeedAccountAndCharacter(connStr);

            var failures = 0;
            Check("PVF awakening quest targets death tower floor 30",
                GameWorld.QuestData.MatchesClearMapTarget(AwakeningQuestId, dungeonId: 0, mapId: Floor30MapId),
                ref failures);

            var config = DeathTowerData.GetConfig(DeathTowerDungeonId);
            Check("PVF death tower config maps floor 30 to 30030",
                config != null
                && config.StageMapIds != null
                && config.StageMapIds.Count >= 30
                && config.StageMapIds[29] == Floor30MapId,
                ref failures);
            if (config == null || config.StageMapIds == null || config.StageMapIds.Count < 30)
            {
                Console.WriteLine($"FAIL: {failures}");
                return 1;
            }

            SaveAwakeningQuest(connStr);
            using (var fixture = SessionFixture.Create(connStr))
            {
                var tower = new DeathTowerSession(config);
                DungeonRunLifecycle.BeginTowerRun(fixture.Session, DeathTowerDungeonId, tower);
                Check("test advances tower to floor 30",
                    AdvanceToFloor30(tower) && tower.GetCurrentMapId() == Floor30MapId,
                    ref failures);

                tower.SetFighting();
                new DeathTowerCoordinator()
                    .HandleStageCommand(fixture.Session, new GamePacketHeader(), new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();

                Check("tower stage clear syncs clear-map quest trigger",
                    LoadTrigger(connStr, AwakeningQuestId) == 0,
                    ref failures);
                Check("tower stage clear records floor sync marker",
                    fixture.Session.Player.CurrentRun != null
                    && !fixture.Session.Player.CurrentRun.TryMarkClearMapQuestSynced(0, Floor30MapId),
                    ref failures);

                tower.SetFighting();
                new DeathTowerCoordinator()
                    .TryHandleMoveMap(fixture.Session)
                    .GetAwaiter()
                    .GetResult();
                Check("stage clear followed by move-map does not resync the same floor",
                    fixture.Session.Player.CurrentRun != null
                    && !fixture.Session.Player.CurrentRun.TryMarkClearMapQuestSynced(0, Floor30MapId),
                    ref failures);
            }

            SaveAwakeningQuest(connStr);
            using (var fixture = SessionFixture.Create(connStr))
            {
                var tower = new DeathTowerSession(config);
                DungeonRunLifecycle.BeginTowerRun(fixture.Session, DeathTowerDungeonId, tower);
                Check("test advances skip-clear tower to floor 30",
                    AdvanceToFloor30(tower) && tower.GetCurrentMapId() == Floor30MapId,
                    ref failures);

                tower.SetFighting();
                new DeathTowerCoordinator()
                    .TryHandleMoveMap(fixture.Session)
                    .GetAwaiter()
                    .GetResult();

                Check("tower move-map from fighting state syncs skipped stage clear",
                    LoadTrigger(connStr, AwakeningQuestId) == 0,
                    ref failures);
            }

            SaveAwakeningQuest(connStr);
            var sender = new RecordingQuestSender(
                CharacterId,
                AccountId,
                new PlayerContext
                {
                    CharacterId = CharacterId,
                    Level = 50,
                    Job = 4,
                    GrowType = 4,
                    CurrentRun = new Game.Dungeon.DungeonRun((short)DeathTowerDungeonId, 0)
                    {
                        Tower = new DeathTowerSession(config),
                    },
                });
            var manager = new QuestManager(sender, connStr);
            var questSessionId = Guid.NewGuid();
            InventoryContext.Register(
                questSessionId,
                new InventoryService(CharacterId, AccountId));
            try
            {
                manager
                    .HandleSetTriggerAsync(
                        0x0021,
                        BuildWireSetTriggerBody(
                            AwakeningQuestId,
                            triggerType: 0,
                            increment: false),
                        questSessionId)
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                InventoryContext.Unregister(
                    questSessionId,
                    CharacterId);
            }

            Check("client SET_TRIGGER cannot complete a server-owned tower target",
                LoadTrigger(connStr, AwakeningQuestId) == 1,
                ref failures);
            Check("server-owned tower trigger gets a success echo ACK",
                sender.LastAckBody != null && sender.LastAckBody.Length >= 1 && sender.LastAckBody[0] == 1,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool AdvanceToFloor30(DeathTowerSession tower)
        {
            while (tower.CurrentStage < 29)
            {
                tower.SetFighting();
                if (!tower.TryAdvanceStage())
                    return false;
            }

            return true;
        }

        private static void SaveAwakeningQuest(string connStr)
        {
            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = AwakeningQuestId, TriggerValue = 1 },
            });
        }

        private static uint LoadTrigger(string connStr, ushort questId)
        {
            var active = QuestService.LoadActiveQuests(connStr, CharacterId);
            var quest = QuestService.FindByQuestId(active, questId);
            return quest != null ? quest.TriggerValue : uint.MaxValue;
        }

        private static byte[] BuildWireSetTriggerBody(ushort questId, byte triggerType, bool increment)
        {
            var body = new byte[6];
            BitConverter.GetBytes((ushort)0x0021).CopyTo(body, 0);
            BitConverter.GetBytes(questId).CopyTo(body, 2);
            body[4] = triggerType;
            body[5] = increment ? (byte)1 : (byte)0;
            return body;
        }

        private static void SeedAccountAndCharacter(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');
INSERT OR IGNORE INTO characters (character_id, account_id, name, job, grow_type, level)
VALUES (@cid, @aid, @name, 4, 4, 50);";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.Parameters.AddWithValue("@mid", "death-tower-quest-routing");
                    cmd.Parameters.AddWithValue("@name", "death-tower-quest-routing");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class RecordingQuestSender : ISessionPacketSender
        {
            public RecordingQuestSender(int characterId, int accountId, PlayerContext player)
            {
                CharacterId = characterId;
                AccountId = accountId;
                Player = player;
            }

            public int CharacterId { get; }
            public int AccountId { get; }
            public PlayerContext Player { get; }
            public byte[] LastAckBody { get; private set; }

            public Task SendPacketAsync(byte[] rawPacket) => Task.CompletedTask;

            public Task SendNotiAsync(ushort notiType, byte[] body) => Task.CompletedTask;

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                LastAckBody = body;
                return Task.CompletedTask;
            }
        }

        private sealed class SessionFixture : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _client;
            private readonly TcpClient _accepted;

            public EnhancedClientSession Session { get; }

            private SessionFixture(TcpListener listener, TcpClient client, TcpClient accepted, EnhancedClientSession session)
            {
                _listener = listener;
                _client = client;
                _accepted = accepted;
                Session = session;
            }

            public static SessionFixture Create(string connStr)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var client = new TcpClient();
                var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
                var accepted = listener.AcceptTcpClient();
                connectTask.GetAwaiter().GetResult();

                var session = new EnhancedClientSession(accepted, new GamePacketHeader());
                session.Player.CharacterId = CharacterId;
                session.Player.Level = 50;
                session.Player.Job = 4;
                session.Player.GrowType = 4;
                session.Account = new AccountRecord
                {
                    AccountId = AccountId,
                    MId = "death-tower-quest-routing",
                };
                session.GameSession = new Game.Session.GameSession(session, connStr);

                return new SessionFixture(listener, client, accepted, session);
            }

            public void Dispose()
            {
                _accepted.Dispose();
                _client.Dispose();
                _listener.Stop();
            }
        }
    }
}
