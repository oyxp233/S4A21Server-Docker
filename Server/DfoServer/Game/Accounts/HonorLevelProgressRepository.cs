using DfoServer.Game.Characters;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Accounts
{
    public sealed class HonorLevelProgressRepository
    {
        private readonly string _connectionString;
        private readonly ICharacterRepository _characterRepository;

        public HonorLevelProgressRepository(string databasePath, string schemaFilePath, ICharacterRepository characterRepository = null)
            : this(new GameDatabase(databasePath, schemaFilePath), characterRepository)
        {
        }

        public HonorLevelProgressRepository(
            IGameDatabase database,
            ICharacterRepository characterRepository = null)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
            _characterRepository = characterRepository;
        }

        public HonorLevelSummary LoadSummary(int accountId)
        {
            var characters = _characterRepository?.ListByAccount(accountId);
            return LoadSummary(accountId, characters);
        }

        public HonorLevelSummary LoadSummary(int accountId, IEnumerable<CharacterRecord> characters)
        {
            var totalExp = LoadAccountHonorExp(accountId);
            return HonorLevelDataProvider.CalculateFromHonorExp(totalExp, characters);
        }

        public HonorLevelSummary AddHonorExp(int accountId, uint delta, IEnumerable<CharacterRecord> characters = null)
        {
            if (accountId <= 0)
                return HonorLevelDataProvider.CalculateFromHonorExp(0UL, Array.Empty<CharacterRecord>());

            characters = characters ?? _characterRepository?.ListByAccount(accountId);
            var totalExp = delta > 0
                ? AddAccountHonorExp(accountId, delta)
                : LoadAccountHonorExp(accountId);
            return HonorLevelDataProvider.CalculateFromHonorExp(totalExp, characters);
        }

        private ulong AddAccountHonorExp(int accountId, uint delta)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var next = AddHonorExpInTransaction(conn, tx, accountId, delta);
                    tx.Commit();
                    return next;
                }
            }
        }

        internal static ulong AddHonorExpInTransaction(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId,
            uint delta)
        {
            if (conn == null)
                throw new ArgumentNullException(nameof(conn));
            if (accountId <= 0)
                return 0;

            var current = LoadAccountHonorExp(conn, tx, accountId);
            var max = HonorLevelDataProvider.MaxTotalHonorExp;
            var next = (ulong)delta >= max - current ? max : current + delta;
            UpdateAccountHonorExp(conn, tx, accountId, next);
            return next;
        }

        private ulong LoadAccountHonorExp(int accountId)
        {
            if (accountId <= 0)
                return 0;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                return LoadAccountHonorExp(conn, null, accountId);
            }
        }

        private static ulong LoadAccountHonorExp(SqliteConnection conn, SqliteTransaction tx, int accountId)
        {
            if (accountId <= 0)
                return 0;

            return TryLoadAccountHonorExp(conn, tx, accountId).GetValueOrDefault();
        }

        private static ulong? TryLoadAccountHonorExp(SqliteConnection conn, SqliteTransaction tx, int accountId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT honor_exp FROM accounts WHERE account_id=@aid;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return null;

                var raw = Convert.ToInt64(value);
                if (raw <= 0)
                    return 0;
                return Math.Min((ulong)raw, HonorLevelDataProvider.MaxTotalHonorExp);
            }
        }

        private static void UpdateAccountHonorExp(SqliteConnection conn, SqliteTransaction tx, int accountId, ulong totalExp)
        {
            var capped = Math.Min(totalExp, HonorLevelDataProvider.MaxTotalHonorExp);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE accounts
SET honor_exp = @exp
WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.Parameters.AddWithValue("@exp", capped > long.MaxValue ? long.MaxValue : (long)capped);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
