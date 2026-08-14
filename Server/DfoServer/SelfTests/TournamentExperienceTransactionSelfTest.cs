using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class TournamentExperienceTransactionSelfTest
    {
        private const int AccountId = 986400;
        private const int CharacterId = 986401;
        private const uint RawGain = 25;

        public static int Run()
        {
            var failures = 0;
            try
            {
                using var fixture = new Fixture();

                fixture.CreateCharacterProgressFailureTrigger();
                var committed = fixture.TryCommit(
                    out _,
                    out var persistenceFailed);
                Check("tournament character progress failure rejects commit",
                    !committed && persistenceFailed,
                    ref failures);
                Check("character progress failure rolls back account and player state",
                    fixture.HasInitialState() && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                fixture.CreateHonorProgressFailureTrigger();
                committed = fixture.TryCommit(
                    out _,
                    out persistenceFailed);
                Check("tournament honor progress failure rejects commit",
                    !committed && persistenceFailed,
                    ref failures);
                Check("honor progress failure rolls back character and player state",
                    fixture.HasInitialState() && fixture.HasNoDirtyState(),
                    ref failures);

                fixture.DropFailureTriggers();
                committed = fixture.TryCommit(
                    out var result,
                    out persistenceFailed);
                Check("tournament experience retries after persistence recovery",
                    committed && !persistenceFailed
                    && fixture.HasCommittedState(result)
                    && fixture.HasNoDirtyState(),
                    ref failures);
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    "[FAIL] tournament experience transaction selftest threw: "
                    + exception);
                failures++;
            }

            Console.WriteLine(failures == 0
                ? "TournamentExperienceTransactionSelfTest OK"
                : "TournamentExperienceTransactionSelfTest FAIL ("
                    + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine("[" + (condition ? "PASS" : "FAIL") + "] " + label);
            if (!condition)
                failures++;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly byte _initialLevel;
            private readonly uint _initialExp;
            private readonly CharacterExperienceService _experienceService;

            internal Fixture()
            {
                _initialLevel = (byte)(ExpTableProvider.MaxLevel - 1);
                _initialExp = (uint)Math.Max(
                    0,
                    ExpTableProvider.GetLevelThreshold(
                        ExpTableProvider.MaxLevel - 1)) - 10;
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    "tournament-experience-transaction-"
                    + Guid.NewGuid().ToString("N")
                    + ".db");
                Database = new GameDatabase(
                    DatabasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(Database, _initialLevel, _initialExp);

                var inventory = new InventoryService(
                    CharacterId,
                    AccountId,
                    Database);
                Lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory);
                Player = new PlayerContext
                {
                    CharacterId = CharacterId,
                    Level = _initialLevel,
                    Exp = _initialExp,
                };

                var characterRepository = new SqliteCharacterRepository(Database);
                var accountExperience = new AccountExperienceProgressService(
                    characterRepository,
                    Database);
                _experienceService = new CharacterExperienceService(
                    accountExperience,
                    Database);
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }
            internal PlayerContext Player { get; }

            internal bool TryCommit(
                out ExperienceGrantResult result,
                out bool persistenceFailed)
                => CharacterExperienceCommitService.TryCommitTournamentExperience(
                    Lease,
                    Player,
                    AccountId,
                    RawGain,
                    _experienceService,
                    out result,
                    out persistenceFailed);

            internal bool HasInitialState()
            {
                LoadState(
                    out var level,
                    out var exp,
                    out var honorExp,
                    out var growthCapsuleExp);
                return Player.Level == _initialLevel
                    && Player.Exp == _initialExp
                    && level == _initialLevel
                    && exp == _initialExp
                    && honorExp == 0
                    && growthCapsuleExp == 0;
            }

            internal bool HasCommittedState(ExperienceGrantResult result)
            {
                if (result == null)
                    return false;

                LoadState(
                    out var level,
                    out var exp,
                    out var honorExp,
                    out var growthCapsuleExp);
                return result.LeveledUp
                    && result.NormalExpGain == 10
                    && result.HonorExpGain == 15
                    && result.Persisted
                    && result.NewLevel == ExpTableProvider.MaxLevel
                    && Player.Level == result.NewLevel
                    && Player.Exp == result.NewExp
                    && level == result.NewLevel
                    && exp == result.NewExp
                    && honorExp == result.TotalHonorExp
                    && growthCapsuleExp == result.TotalGrowthCapsuleExp
                    && result.Honor != null
                    && result.GrowthCapsule != null;
            }

            internal bool HasNoDirtyState()
                => Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.DirtyMainVirtualCountSlots.Count == 0;

            internal void CreateCharacterProgressFailureTrigger()
                => Execute($@"
CREATE TRIGGER fail_tournament_character_progress
BEFORE UPDATE OF level, exp ON characters
WHEN OLD.character_id={CharacterId}
BEGIN
    SELECT RAISE(ABORT, 'injected tournament character progress failure');
END;");

            internal void CreateHonorProgressFailureTrigger()
                => Execute($@"
CREATE TRIGGER fail_tournament_honor_progress
BEFORE UPDATE OF honor_exp ON accounts
WHEN OLD.account_id={AccountId}
BEGIN
    SELECT RAISE(ABORT, 'injected tournament honor progress failure');
END;");

            internal void DropFailureTriggers()
            {
                if (!File.Exists(DatabasePath))
                    return;
                Execute(@"
DROP TRIGGER IF EXISTS fail_tournament_character_progress;
DROP TRIGGER IF EXISTS fail_tournament_honor_progress;");
            }

            private void LoadState(
                out byte level,
                out uint exp,
                out ulong honorExp,
                out uint growthCapsuleExp)
            {
                using var connection = Database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT c.level, c.exp, a.honor_exp, a.growth_capsule_exp
FROM characters c
JOIN accounts a ON a.account_id=c.account_id
WHERE c.character_id=@cid;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException(
                        "tournament experience fixture character is missing");
                level = (byte)reader.GetInt32(0);
                exp = (uint)reader.GetInt64(1);
                honorExp = (ulong)reader.GetInt64(2);
                growthCapsuleExp = (uint)reader.GetInt64(3);
            }

            private void Execute(string sql)
            {
                using var connection = Database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            public void Dispose()
            {
                try { DropFailureTriggers(); } catch { }
                InventoryContext.Unregister(Lease.SessionId, Lease.CharacterId);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    try
                    {
                        if (File.Exists(DatabasePath + suffix))
                            File.Delete(DatabasePath + suffix);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void SeedCharacter(
            IGameDatabase database,
            byte level,
            uint exp)
        {
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO accounts(account_id,m_id,password_hash)
VALUES(@aid,'tournament-experience-transaction','');
INSERT INTO characters(
    character_id,account_id,name,job,grow_type,level,exp,
    town_id,area_id,direction,area_state)
VALUES(@cid,@aid,@name,0,0,@level,@exp,1,0,5,3);
INSERT INTO character_subtype1_fields(character_id)
VALUES(@cid);";
                command.Parameters.AddWithValue("@aid", AccountId);
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue(
                    "@name",
                    Encoding.UTF8.GetBytes("TournamentExperienceTransaction"));
                command.Parameters.AddWithValue("@level", (int)level);
                command.Parameters.AddWithValue("@exp", (long)exp);
                command.ExecuteNonQuery();
            });
        }
    }
}
