using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Quest;

namespace DfoServer.SelfTests
{
    public static class QuestNotifySelectionSelfTest
    {
        private const int CharacterId = 136011;
        private const int AccountId = 136011;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_NOTIFY_SELECTION selftest ===");
            var failures = 0;
            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "quest-notify-selection.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                dbPath,
                ServerPaths.SchemaFilePath);
            SeedCharacter(dbPath);

            var sender = new RecordingSender(CharacterId, AccountId);
            var manager = new QuestManager(sender, connectionString);
            var selected = BuildBody(2066, 2067, 2068);
            manager.HandleSaveQuestNotify(selected);

            var repository = new QuestNotifySelectionRepository(connectionString);
            CheckSequence(
                "selection persists in request order",
                new[] { 2066, 2067, 2068 },
                repository.Load(CharacterId),
                ref failures);
            Check(
                "SAVE_QUEST_NOTIFY has no command ack",
                sender.SentPackets == 0,
                ref failures);

            manager.HandleSaveQuestNotify(BuildBody(2069));
            CheckSequence(
                "new selection atomically replaces old slots",
                new[] { 2069 },
                repository.Load(CharacterId),
                ref failures);

            manager.HandleSaveQuestNotify(new byte[] { 0 });
            Check(
                "zero count clears all selected quest notifications",
                repository.Load(CharacterId).Count == 0,
                ref failures);

            manager.HandleSaveQuestNotify(BuildBody(2066, 2067));
            manager.HandleSaveQuestNotify(new byte[] { 2, 0, 0, 0, 0 });
            manager.HandleSaveQuestNotify(BuildBody(2066, 2066));
            CheckSequence(
                "malformed and duplicate requests preserve the last valid state",
                new[] { 2066, 2067 },
                repository.Load(CharacterId),
                ref failures);

            Check(
                "parser accepts exact count plus int32 quest ids",
                QuestCommandParser.TryParseSaveNotify(
                    BuildBody(2066, 2067, 2068, 2069),
                    out var parsed)
                    && parsed.QuestIds.Count == 4
                    && parsed.QuestIds[3] == 2069,
                ref failures);
            Check(
                "parser rejects more than four A14 projection slots",
                !QuestCommandParser.TryParseSaveNotify(
                    BuildBody(2066, 2067, 2068, 2069, 2070),
                    out _),
                ref failures);

            var snapshot = new SelectCharacterDataSnapshot
            {
                CharacterRecord = new CharacterRecord
                {
                    CharacterId = 0,
                    AccountId = AccountId,
                    Name = Encoding.UTF8.GetBytes("quest-notify-test"),
                    Level = 86,
                    CreatedAt = DateTime.UtcNow,
                },
                InitializationSnapshot = new SelectCharacterInitializationSnapshot(),
            };
            snapshot.InitializationSnapshot.QuestNotifyIds.AddRange(
                repository.Load(CharacterId));
            Check(
                "select-character ack builds with persisted quest projection",
                SelectCharacterAckBodyBuilder.TryBuild(snapshot, out var ackBody),
                ref failures);
            CheckSequence(
                "select-character ack restores four fixed int32 slots",
                new[] { 2066, 2067, 0, 0 },
                ReadNotifySlots(ackBody),
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildBody(params int[] questIds)
        {
            var body = new byte[1 + questIds.Length * sizeof(int)];
            body[0] = (byte)questIds.Length;
            for (var index = 0; index < questIds.Length; index++)
                BitConverter.GetBytes(questIds[index]).CopyTo(body, 1 + index * sizeof(int));
            return body;
        }

        private static IReadOnlyList<int> ReadNotifySlots(byte[] ackBody)
        {
            const int fixedPrefixLength = 18 + sizeof(int);
            const int activeQuestBytes = QuestSlotLayout.ActiveSlotCount * 6;
            var offset = fixedPrefixLength + activeQuestBytes;
            var result = new int[QuestNotifySelectionService.MaxSlots];
            for (var index = 0; index < result.Length; index++)
                result[index] = BitConverter.ToInt32(ackBody, offset + index * sizeof(int));
            return result;
        }

        private static void SeedCharacter(string dbPath)
        {
            var connectionString = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'quest-notify-selection', '');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.ExecuteNonQuery();
                }
            }

            var repository = new SqliteCharacterRepository(
                dbPath,
                ServerPaths.SchemaFilePath);
            repository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-notify-selection"),
                Level = 86,
            });
        }

        private static void CheckSequence(
            string name,
            IReadOnlyList<int> expected,
            IReadOnlyList<int> actual,
            ref int failures)
        {
            var ok = expected != null
                && actual != null
                && expected.Count == actual.Count;
            for (var index = 0; ok && index < expected.Count; index++)
                ok = expected[index] == actual[index];
            Check(name, ok, ref failures);
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class RecordingSender : ISessionPacketSender
        {
            internal RecordingSender(int characterId, int accountId)
            {
                CharacterId = characterId;
                AccountId = accountId;
                Player.CharacterId = characterId;
            }

            internal int SentPackets { get; private set; }
            public PlayerContext Player { get; } = new PlayerContext();
            public int CharacterId { get; }
            public int AccountId { get; }
            public Task SendPacketAsync(byte[] rawPacket)
            {
                SentPackets++;
                return Task.CompletedTask;
            }
            public Task SendCmdAckAsync(ushort type, byte[] body)
            {
                SentPackets++;
                return Task.CompletedTask;
            }
            public Task SendNotiAsync(ushort type, byte[] body)
            {
                SentPackets++;
                return Task.CompletedTask;
            }
        }
    }
}
