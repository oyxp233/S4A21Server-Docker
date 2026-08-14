using DfoServer.Infrastructure;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Session;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class TowerOfDespairProgressSelfTest
    {
        private const int AccountId = 940020;
        private const int CharacterId = 940120;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "tower-of-despair-progress-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(connectionString);

                var repository = new TowerOfDespairProgressRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var service = new TowerOfDespairProgressService(repository);

                Check("fresh character starts on floor 1",
                    service.ResolveEntryDungeonId(CharacterId, 11008) == 11008,
                    ref failures);
                Check("non tower dungeon is unchanged",
                    service.ResolveEntryDungeonId(CharacterId, 144) == 144,
                    ref failures);
                Check("tower progress table is part of the database baseline",
                    TableExists(connectionString, "character_tower_of_despair_progress"),
                    ref failures);

                Check("floor 1 clear is recorded",
                    service.TryRecordClear(CharacterId, 11008, out _, out _),
                    ref failures);
                Check("clearing floor 1 redirects the base request to floor 2",
                    service.ResolveEntryDungeonId(CharacterId, 11008) == 11009,
                    ref failures);

                var reopenedRepository = new TowerOfDespairProgressRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var reopenedService = new TowerOfDespairProgressService(reopenedRepository);
                Check("floor progress survives repository recreation",
                    reopenedService.ResolveEntryDungeonId(CharacterId, 11008) == 11009,
                    ref failures);

                CheckEnterSelectDungeonFloorLayout(ref failures);

                Check("replayed floor 1 clear is accepted idempotently",
                    reopenedService.TryRecordClear(CharacterId, 11008, out _, out _),
                    ref failures);
                Check("replaying an older clear does not skip a floor",
                    reopenedService.ResolveEntryDungeonId(CharacterId, 11008) == 11009,
                    ref failures);

                Check("floor 100 clear is recorded",
                    reopenedService.TryRecordClear(CharacterId, 11107, out _, out _),
                    ref failures);
                Check("floor progress is capped at floor 100",
                    reopenedService.ResolveEntryDungeonId(CharacterId, 11008) == 11107,
                    ref failures);

                DropTowerProgressTable(connectionString);
                Check("missing progress table returns a safe floor fallback",
                    !reopenedService.TryGetNextFloor(
                        CharacterId,
                        out var fallbackFloor,
                        out var readError)
                    && fallbackFloor == 1
                    && readError != null,
                    ref failures);
                Check("missing progress table keeps the requested entry dungeon",
                    reopenedService.ResolveEntryDungeonId(CharacterId, 11008) == 11008,
                    ref failures);
                Check("missing progress table rejects a clear before rewards are exposed",
                    !reopenedService.TryRecordClear(
                        CharacterId,
                        11008,
                        out _,
                        out var writeError)
                    && writeError != null,
                    ref failures);

            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] tower progress selftest exception: " + ex);
                failures++;
            }
            finally
            {
                DeleteDatabase(databasePath);
            }

            return Finish(failures);
        }

        private static void CheckEnterSelectDungeonFloorLayout(ref int failures)
        {
            var player = new PlayerContext { UserId = 1002 };
            var body = Network.Builders.EnterSelectDungeonStateBuilder
                .BuildEnterSelectDungeon(player, 8);
            Check("enter-select-dungeon body keeps the proven 19-byte layout",
                body != null && body.Length == 19,
                ref failures);
            Check("enter-select-dungeon body writes the user id at offset 7",
                body != null
                    && body.Length >= 9
                    && BitConverter.ToUInt16(body, 7) == player.UserId,
                ref failures);
            Check("enter-select-dungeon body writes the despair floor at offset 14",
                body != null
                    && body.Length >= 16
                    && BitConverter.ToUInt16(body, 14) == 8,
                ref failures);
        }

        private static void DropTowerProgressTable(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DROP TABLE character_tower_of_despair_progress;";
                    command.ExecuteNonQuery();
                }
            }
        }

        private static bool TableExists(string connectionString, string tableName)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type='table' AND name=@tableName;";
                    command.Parameters.AddWithValue("@tableName", tableName);
                    return Convert.ToInt32(command.ExecuteScalar()) == 1;
                }
            }
        }

        private static void SeedCharacter(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, 'tower-of-despair-selftest', '');
INSERT OR IGNORE INTO characters(character_id, account_id, name, level)
VALUES(@characterId, @accountId, 'tower-of-despair-selftest', 86);";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteDatabase(string databasePath)
        {
            TryDelete(databasePath);
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
        }

        private static void TryDelete(string path)
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

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private static int Finish(int failures)
        {
            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }
    }
}
