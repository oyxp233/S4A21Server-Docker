using System;
using System.IO;
using System.Linq;
using System.Text;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class SpecialRewardQuestSourceSelfTest
    {
        private const int CharacterId = 315002;
        private const int AccountId = 315002;
        private const int OrdinaryQuestId = 13501;
        private const int SpecialRewardQuestId = 13502;

        public static int Run()
        {
            Console.WriteLine("=== SPECIAL_REWARD_QUEST_SOURCE selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "special-reward-quest-source.db");
            DeleteDatabase(dbPath);

            var schemaPath = ServerPaths.SchemaFilePath;
            var characterRepository = new SqliteCharacterRepository(dbPath, schemaPath);
            SeedAccount(dbPath);
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("special-reward-source"),
                Job = 0,
                GrowType = 0,
                Level = 20,
            });
            SeedSubtype1Row(dbPath);

            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            var failures = 0;

            MarkQuestCleared(connStr, OrdinaryQuestId);
            MarkQuestCleared(connStr, SpecialRewardQuestId);

            Check("13502 has special reward status in PVF",
                GameWorld.QuestData.HasSpecialRewardStatus(SpecialRewardQuestId),
                ref failures);
            Check("13501 is not a special reward source",
                !GameWorld.QuestData.HasSpecialRewardStatus(OrdinaryQuestId),
                ref failures);

            var relogSnapshot = new SqliteSubtype1Repository(dbPath, schemaPath).Load(CharacterId);
            Check("relog subtype1 snapshot loads", relogSnapshot != null, ref failures);
            Check("relog source list contains only 13502",
                relogSnapshot != null
                && relogSnapshot.SpecialRewardQuestIds.SequenceEqual(new[] { (uint)SpecialRewardQuestId }),
                ref failures);

            if (relogSnapshot != null)
            {
                var subtype1WithSource = UserInfoSubtype1Builder.BuildFromSnapshot(
                    relogSnapshot,
                    new SkillInfoSnapshot());
                relogSnapshot.SpecialRewardQuestIds.Clear();
                var subtype1WithoutSource = UserInfoSubtype1Builder.BuildFromSnapshot(
                    relogSnapshot,
                    new SkillInfoSnapshot());
                Check("A21 subtype1 omits the legacy variable special-reward source list",
                    subtype1WithSource.SequenceEqual(subtype1WithoutSource),
                    ref failures);

                relogSnapshot.SpecialRewardQuestIds.Add(SpecialRewardQuestId);
                var subtype3 = UserInfoSubtype3Builder.BuildNotificationBody(
                    1,
                    relogSnapshot,
                    new SkillInfoSnapshot(),
                    new CharacterRecord());
                Check("subtype3 wire carries one 13502 source",
                    ReadSubtype3SpecialRewardQuestIds(
                            subtype3,
                            relogSnapshot.SpecialRewardQuestIds.Count)
                        .SequenceEqual(new[] { (uint)SpecialRewardQuestId }),
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static uint[] ReadSubtype3SpecialRewardQuestIds(
            byte[] body,
            int expectedCount)
        {
            const int fixedTailLength = 2 + 1;
            int countOffset = body.Length - fixedTailLength - 4 - expectedCount * 4;
            if (countOffset < 0)
                return Array.Empty<uint>();

            uint wireCount = BitConverter.ToUInt32(body, countOffset);
            if (wireCount != expectedCount)
                return Array.Empty<uint>();

            var result = new uint[checked((int)wireCount)];
            for (int i = 0; i < result.Length; i++)
                result[i] = BitConverter.ToUInt32(body, countOffset + 4 + i * 4);
            return result;
        }

        private static void SeedAccount(string dbPath)
        {
            var connStr = SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash) VALUES (@aid, @mid, '');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@mid", "special-reward-source");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void SeedSubtype1Row(string dbPath)
        {
            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "INSERT OR IGNORE INTO character_subtype1_fields(character_id) VALUES(@cid);";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void MarkQuestCleared(string connStr, int questId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO character_quest_completions (character_id, quest_id, completion_value)
VALUES (@cid, @slot, 1)
ON CONFLICT(character_id, quest_id) DO UPDATE SET completion_value = 1;";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.Parameters.AddWithValue("@slot", questId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteDatabase(string databasePath)
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
