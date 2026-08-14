using System;
using System.IO;
using System.Linq;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class PvpSkillIsolationSelfTest
    {
        private const int AccountId = 911380;
        private const int CharacterId = 911381;
        private const byte Job = 2;
        private const byte GrowType = 0x24;
        private const ushort SkillId = 118;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "pvp_skill_isolation_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                SeedCharacter(databasePath);
                var normalRepository = new SqliteCharacterProgressRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var pvpRepository = new SqlitePvpSkillRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);

                var initial = pvpRepository.LoadOrInitialize(
                    CharacterId,
                    Job,
                    85,
                    GrowType);
                Check(
                    "PvP tree initializes independently with unlimited point mirrors",
                    pvpRepository.IsInitialized(CharacterId) &&
                    initial.Pages.Count == 2 &&
                    initial.Pages.All(page => page.HeaderValue == ushort.MaxValue) &&
                    initial.Tail0 == ushort.MaxValue &&
                    initial.Tail1 == ushort.MaxValue,
                    ref failures);

                var result = BuySkillService.ExecutePvp(
                    pvpRepository,
                    CharacterId,
                    AccountId,
                    Job,
                    0,
                    new[]
                    {
                        new BuySkillEntry
                        {
                            SkillIndex = SkillId,
                            Level = 1,
                        },
                    },
                    bonusSp: -100000,
                    level: 85,
                    growType: GrowType);
                var pvp = pvpRepository.Load(CharacterId);
                var normal = normalRepository.LoadSkills(CharacterId);
                Check(
                    "PvP learning bypasses exhausted SP and returns unlimited points",
                    result.Success &&
                    result.RemainSp == ushort.MaxValue &&
                    result.RemainTp == ushort.MaxValue &&
                    result.Entries.Any(entry => entry.SkillId == SkillId),
                    ref failures);
                Check(
                    "PvP persistence does not modify the town skill tree",
                    pvp.Pages[0].Entries.Any(entry => entry.SkillId == SkillId) &&
                    !normal.Pages[0].Entries.Any(entry => entry.SkillId == SkillId),
                    ref failures);

                using var connection = new SqliteConnection(
                    SqliteDatabaseBootstrap.Initialize(
                        databasePath,
                        ServerPaths.SchemaFilePath));
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type='table'
  AND name IN ('character_pvp_skill_state', 'character_pvp_skills');";
                Check(
                    "PvP skill tables are part of the database baseline",
                    Convert.ToInt32(command.ExecuteScalar()) == 2,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] PvP skill isolation threw: " + ex);
                failures++;
            }
            finally
            {
                DeleteDatabaseFiles(databasePath);
            }

            Console.WriteLine(
                failures == 0
                    ? "PvpSkillIsolationSelfTest OK"
                    : $"PvpSkillIsolationSelfTest FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }

        private static void SeedCharacter(string databasePath)
        {
            using var connection = new SqliteConnection(
                SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'pvp-skill-isolation-selftest', '');
INSERT OR IGNORE INTO characters (
    character_id, account_id, name, job, level, grow_type, bonus_sp)
VALUES (
    @characterId, @accountId, 'pvp-skill-isolation-selftest',
    @job, 85, @growType, -100000);";
            command.Parameters.AddWithValue("@accountId", AccountId);
            command.Parameters.AddWithValue("@characterId", CharacterId);
            command.Parameters.AddWithValue("@job", Job);
            command.Parameters.AddWithValue("@growType", GrowType);
            command.ExecuteNonQuery();
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var path = databasePath + suffix;
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {label}");
            if (!condition)
                failures++;
        }
    }
}
