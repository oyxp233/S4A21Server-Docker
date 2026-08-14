using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.CharacterData
{
    public sealed class SqliteDarkKnightComboSkillRepository
    {
        private const string UpsertPageSql = @"
INSERT INTO character_dark_knight_combo_skill_pages(character_id, page_index, body, updated_at)
VALUES(@cid, @page, @body, CURRENT_TIMESTAMP)
ON CONFLICT(character_id, page_index) DO UPDATE SET
    body = excluded.body,
    updated_at = CURRENT_TIMESTAMP";

        private readonly string _connectionString;

        public SqliteDarkKnightComboSkillRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public SqliteDarkKnightComboSkillRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public List<byte[]> LoadPageBodies(int characterId)
        {
            var bodies = new List<byte[]>();
            if (characterId <= 0)
                return bodies;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT body FROM character_dark_knight_combo_skill_pages WHERE character_id=@cid ORDER BY page_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var body = reader.IsDBNull(0) ? null : (byte[])reader[0];
                            if (body == null || body.Length == 0)
                                continue;

                            bodies.Add(Copy(body));
                        }
                    }
                }
            }

            return bodies;
        }

        public int SavePageBody(int characterId, byte[] body)
        {
            if (characterId <= 0 || body == null || body.Length == 0)
                return 0;

            var page = body[0] == 1 ? 1 : 0;
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(UpsertPageSql, conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@page", page);
                    cmd.Parameters.AddWithValue("@body", body);
                    return cmd.ExecuteNonQuery();
                }
            }
        }

        public void SavePageBodies(int characterId, IEnumerable<byte[]> bodies)
        {
            if (characterId <= 0 || bodies == null)
                return;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var body in bodies)
                    {
                        if (body == null || body.Length == 0)
                            continue;

                        SavePageBody(conn, tx, characterId, body);
                    }

                    tx.Commit();
                }
            }
        }

        private static void SavePageBody(SqliteConnection conn, SqliteTransaction tx, int characterId, byte[] body)
        {
            var page = body[0] == 1 ? 1 : 0;
            using (var cmd = new SqliteCommand(UpsertPageSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@page", page);
                cmd.Parameters.AddWithValue("@body", body);
                cmd.ExecuteNonQuery();
            }
        }

        private static byte[] Copy(byte[] body)
        {
            var copy = new byte[body.Length];
            Buffer.BlockCopy(body, 0, copy, 0, body.Length);
            return copy;
        }
    }
}
