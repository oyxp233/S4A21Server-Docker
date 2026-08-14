using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class DungeonDifficultyPermissionSelfTest
    {
        private const int PartyImpossibleDungeonId = 62;
        private const int SoloImpossibleDungeonId = 183;
        private const int StandardDungeonId = 70;
        private const int SecondStandardDungeonId = 71;
        private const int AntonDungeonId = 225;
        private const int TaskExclusiveDungeonId = 522;

        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_DIFFICULTY_PERMISSION selftest ===");
            var failures = 0;

            Check(
                "ordinary, party-impossible and solo-impossible permissions are account scoped",
                DungeonPermissionScopePolicy.IsAccountDifficulty(
                    StandardDungeonId)
                    && DungeonPermissionScopePolicy.IsAccountDifficulty(
                        PartyImpossibleDungeonId)
                    && DungeonPermissionScopePolicy.IsAccountDifficulty(
                        SoloImpossibleDungeonId),
                ref failures);
            Check(
                "Anton conquest remains character scoped",
                DungeonPermissionScopePolicy.Resolve(AntonDungeonId)
                    == DungeonPermissionPersistenceScope.CharacterMechanism,
                ref failures);
            Check(
                "task-exclusive dungeon permission is not persistent",
                DungeonPermissionScopePolicy.Resolve(TaskExclusiveDungeonId)
                    == DungeonPermissionPersistenceScope.None,
                ref failures);

            var projected = DungeonPermissionProjector.ProjectForClient(
                BuildPermissions(
                    (PartyImpossibleDungeonId, 2),
                    (SoloImpossibleDungeonId, 1)));
            Check(
                "party and solo impossible dungeons remain separate physical permissions",
                Format(projected) == "62:2,183:1",
                ref failures);

            var physicalPlan = DungeonPermissionProjector.BuildProgressionPlan(
                projected,
                PartyImpossibleDungeonId,
                requestedClearState: 3);
            Check(
                "physical progression changes only the cleared dungeon id",
                physicalPlan.RequiresPersistence
                    && Format(physicalPlan.Entries) == "62:3",
                ref failures);

            var body = DungeonPermissionBodyBuilder.BuildEntries(projected);
            Check(
                "0x0005 preserves separate party and solo dungeon entries",
                BytesEqual(
                    body,
                    0x02, 0x00,
                    0x3E, 0x00, 0x02,
                    0xB7, 0x00, 0x01),
                ref failures);

            TestAccountPersistence(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "DUNGEON_DIFFICULTY_PERMISSION selftest passed."
                    : $"DUNGEON_DIFFICULTY_PERMISSION selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void TestAccountPersistence(ref int failures)
        {
            const int firstAccountId = 978041;
            const int secondAccountId = 978042;
            const int firstCharacterId = 978141;
            const int secondCharacterId = 978142;
            const int newCharacterId = 978143;
            const int otherAccountCharacterId = 978241;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"dungeon-difficulty-permission-{Guid.NewGuid():N}.db");

            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@firstAccountId, 'difficulty-account-a', '');
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@secondAccountId, 'difficulty-account-b', '');
INSERT INTO characters (character_id, account_id, name, level, slot_index)
VALUES (@firstCharacterId, @firstAccountId, 'DifficultyA', 86, 0);
INSERT INTO characters (character_id, account_id, name, level, slot_index)
VALUES (@secondCharacterId, @firstAccountId, 'DifficultyB', 86, 1);
INSERT INTO characters (character_id, account_id, name, level, slot_index)
VALUES (@newCharacterId, @firstAccountId, 'DifficultyNew', 1, 2);
INSERT INTO characters (character_id, account_id, name, level, slot_index)
VALUES (@otherCharacterId, @secondAccountId, 'DifficultyOther', 86, 0);";
                        command.Parameters.AddWithValue(
                            "@firstAccountId",
                            firstAccountId);
                        command.Parameters.AddWithValue(
                            "@secondAccountId",
                            secondAccountId);
                        command.Parameters.AddWithValue(
                            "@firstCharacterId",
                            firstCharacterId);
                        command.Parameters.AddWithValue(
                            "@secondCharacterId",
                            secondCharacterId);
                        command.Parameters.AddWithValue(
                            "@newCharacterId",
                            newCharacterId);
                        command.Parameters.AddWithValue(
                            "@otherCharacterId",
                            otherAccountCharacterId);
                        command.ExecuteNonQuery();
                    }
                }

                var characterRepository = new SqliteCharacterStateRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                characterRepository.ApplyDungeonPermissionBatch(
                    firstCharacterId,
                    BuildPermissions(
                        (PartyImpossibleDungeonId, 2),
                        (SoloImpossibleDungeonId, 1),
                        (StandardDungeonId, 3),
                        (AntonDungeonId, 2),
                        (TaskExclusiveDungeonId, 3)),
                    out _);
                characterRepository.ApplyDungeonPermissionBatch(
                    secondCharacterId,
                    BuildPermissions((SecondStandardDungeonId, 2)),
                    out _);
                characterRepository.ApplyDungeonPermissionBatch(
                    otherAccountCharacterId,
                    BuildPermissions((StandardDungeonId, 1)),
                    out _);

                var service = new DungeonDifficultyPermissionService(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var firstLogin = service.BuildLoginPermissions(
                    firstAccountId,
                    characterRepository.LoadDungeonPermissions(
                        firstCharacterId));
                Check(
                    "legacy rows merge by account while current Anton state stays character local",
                    Format(firstLogin) == "62:2,70:3,71:2,183:1,225:2",
                    ref failures);

                var secondLogin = service.BuildLoginPermissions(
                    firstAccountId,
                    characterRepository.LoadDungeonPermissions(
                        secondCharacterId));
                Check(
                    "second character inherits all account dungeon difficulties",
                    Format(secondLogin) == "62:2,70:3,71:2,183:1",
                    ref failures);

                var newCharacterLogin = service.BuildLoginPermissions(
                    firstAccountId,
                    characterRepository.LoadDungeonPermissions(newCharacterId));
                Check(
                    "new character inherits existing account dungeon difficulties",
                    Format(newCharacterLogin) == "62:2,70:3,71:2,183:1",
                    ref failures);

                var otherAccountLogin = service.BuildLoginPermissions(
                    secondAccountId,
                    characterRepository.LoadDungeonPermissions(
                        otherAccountCharacterId));
                Check(
                    "different accounts remain isolated",
                    Format(otherAccountLogin) == "70:1",
                    ref failures);

                var plan = service.BuildProgressionPlan(
                    firstAccountId,
                    PartyImpossibleDungeonId,
                    requestedClearState: 3);
                var snapshot = service.ApplyBatch(
                    firstAccountId,
                    plan.Entries,
                    out var changes);
                Check(
                    "account progression atomically raises only the cleared physical dungeon",
                    plan.RequiresPersistence
                        && Format(changes) == "62:3"
                        && Format(snapshot) == "62:3,70:3,71:2,183:1"
                        && DungeonPermissionProjector.IsApplied(
                            snapshot,
                            plan.Entries),
                    ref failures);

                var replay = service.BuildProgressionPlan(
                    firstAccountId,
                    PartyImpossibleDungeonId,
                    requestedClearState: 1);
                Check(
                    "lower difficulty replay cannot reduce account progress",
                    !replay.RequiresPersistence
                        && Format(replay.Entries) == "62:3",
                    ref failures);

                newCharacterLogin = service.BuildLoginPermissions(
                    firstAccountId,
                    characterRepository.LoadDungeonPermissions(newCharacterId));
                Check(
                    "account increase is visible to another character on next login",
                    Format(newCharacterLogin) == "62:3,70:3,71:2,183:1",
                    ref failures);

                otherAccountLogin = service.BuildLoginPermissions(
                    secondAccountId,
                    characterRepository.LoadDungeonPermissions(
                        otherAccountCharacterId));
                Check(
                    "account increase does not leak to another account",
                    Format(otherAccountLogin) == "70:1",
                    ref failures);

                var accountRows = ReadAccountRows(
                    connectionString,
                    firstAccountId);
                Check(
                    "account ledger excludes task-exclusive and character mechanism rows",
                    Format(accountRows) == "62:3,70:3,71:2,183:1",
                    ref failures);

                Check(
                    "standard account pipeline rejects Anton and task-exclusive updates",
                    !service.BuildProgressionPlan(
                        firstAccountId,
                        AntonDungeonId,
                        requestedClearState: 3).RequiresPersistence
                    && !service.BuildProgressionPlan(
                        firstAccountId,
                        TaskExclusiveDungeonId,
                        requestedClearState: 3).RequiresPersistence,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] account persistence: {ex}");
                failures++;
            }
            finally
            {
                DeleteDatabaseFiles(databasePath);
            }
        }

        private static List<DungeonPermissionEntrySnapshot> ReadAccountRows(
            string connectionString,
            int accountId)
        {
            var result = new List<DungeonPermissionEntrySnapshot>();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT dungeon_id, clear_state
FROM account_dungeon_permissions
WHERE account_id = @accountId
ORDER BY dungeon_id;";
                    command.Parameters.AddWithValue("@accountId", accountId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(new DungeonPermissionEntrySnapshot
                            {
                                DungeonId = (ushort)reader.GetInt32(0),
                                ClearState = (byte)reader.GetInt32(1),
                            });
                        }
                    }
                }
            }

            return result;
        }

        private static List<DungeonPermissionEntrySnapshot> BuildPermissions(
            params (int DungeonId, byte ClearState)[] values)
            => values.Select(value => new DungeonPermissionEntrySnapshot
            {
                DungeonId = checked((ushort)value.DungeonId),
                ClearState = value.ClearState,
            }).ToList();

        private static string Format(
            IEnumerable<DungeonPermissionEntrySnapshot> entries)
            => string.Join(
                ",",
                (entries ?? Array.Empty<DungeonPermissionEntrySnapshot>())
                    .OrderBy(entry => entry.DungeonId)
                    .Select(entry => $"{entry.DungeonId}:{entry.ClearState}"));

        private static bool BytesEqual(byte[] actual, params byte[] expected)
            => actual != null && actual.SequenceEqual(expected);

        private static void DeleteDatabaseFiles(string databasePath)
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = databasePath + suffix;
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                    // Best-effort cleanup for transient SQLite handles.
                }
            }
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
