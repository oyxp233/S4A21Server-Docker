using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Skills
{
    /// <summary>
    /// The fair-PvP channel owns a skill tree that is independent from the
    /// character's town/dungeon skill tree.  The state row is deliberately
    /// separate from the entries so even an empty initial tree is initialized
    /// exactly once.
    /// </summary>
    public sealed class SqlitePvpSkillRepository
    {
        private readonly string _connectionString;

        public SqlitePvpSkillRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public SqlitePvpSkillRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        internal string ConnectionString => _connectionString;

        public SkillInfoSnapshot LoadOrInitialize(
            int characterId,
            byte job,
            byte level,
            byte growType)
        {
            Characters.CharacterStatComputer.DecodeGrowType(
                growType,
                out var firstGrow,
                out var secondGrow);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    if (!IsInitialized(connection, transaction, characterId))
                    {
                        var initial = CharacterSkillProfile.BuildSnapshot(
                            job,
                            firstGrow,
                            secondGrow,
                            level);
                        SaveCore(connection, transaction, characterId, initial);
                        MarkInitialized(connection, transaction, characterId);
                        transaction.Commit();
                        ApplyUnlimitedPointMirrors(initial);
                        return initial;
                    }

                    var loaded = LoadCore(connection, transaction, characterId);
                    transaction.Commit();
                    ApplyUnlimitedPointMirrors(loaded);
                    return loaded;
                }
            }
        }

        public SkillInfoSnapshot Load(int characterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                var snapshot = LoadCore(connection, null, characterId);
                ApplyUnlimitedPointMirrors(snapshot);
                return snapshot;
            }
        }

        public bool IsInitialized(int characterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return IsInitialized(connection, null, characterId);
            }
        }

        public void Save(int characterId, SkillInfoSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    SaveCore(connection, transaction, characterId, snapshot);
                    MarkInitialized(connection, transaction, characterId);
                    transaction.Commit();
                }
            }
        }

        public int ClearAllSkillCommands(int characterId)
        {
            return ExecuteUpdate(
                "UPDATE character_pvp_skills SET extra_values=NULL WHERE character_id=@cid AND extra_values IS NOT NULL",
                characterId);
        }

        public int UpdateSkillCommand(int characterId, ushort skillId, byte[] commandBytes)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE character_pvp_skills
SET extra_values=@extra
WHERE character_id=@cid AND skill_id=@sid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@sid", (int)skillId);
                    command.Parameters.AddWithValue(
                        "@extra",
                        commandBytes != null && commandBytes.Length > 0
                            ? (object)commandBytes
                            : DBNull.Value);
                    return command.ExecuteNonQuery();
                }
            }
        }

        public void SwapSkillSlot(int characterId, int page, int slot1, int slot2)
        {
            if (slot1 == slot2)
                return;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    MoveSkillSlot(connection, transaction, characterId, page, slot1, -1);
                    MoveSkillSlot(connection, transaction, characterId, page, slot2, slot1);
                    MoveSkillSlot(connection, transaction, characterId, page, -1, slot2);
                    transaction.Commit();
                }
            }
        }

        internal static void ApplyUnlimitedPointMirrors(SkillInfoSnapshot snapshot)
        {
            if (snapshot == null)
                return;
            while (snapshot.Pages.Count < 2)
                snapshot.Pages.Add(new SkillInfoPageSnapshot());

            snapshot.Pages[0].HeaderValue = ushort.MaxValue;
            snapshot.Pages[1].HeaderValue = ushort.MaxValue;
            snapshot.Tail0 = ushort.MaxValue;
            snapshot.Tail1 = ushort.MaxValue;
            snapshot.HasTailValues = true;
        }

        private int ExecuteUpdate(string sql, int characterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@cid", characterId);
                    return command.ExecuteNonQuery();
                }
            }
        }

        private static bool IsInitialized(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 1
FROM character_pvp_skill_state
WHERE character_id=@cid
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                return command.ExecuteScalar() != null;
            }
        }

        private static void MarkInitialized(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_pvp_skill_state(character_id, initialized_at, updated_at)
VALUES(@cid, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT(character_id) DO UPDATE SET updated_at=CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        private static SkillInfoSnapshot LoadCore(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var snapshot = new SkillInfoSnapshot();
            var pages = new Dictionary<int, SkillInfoPageSnapshot>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT page_index, slot, skill_id, level, extra_values
FROM character_pvp_skills
WHERE character_id=@cid
ORDER BY page_index, slot;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var pageIndex = reader.GetInt32(0);
                        if (pageIndex < 0 || pageIndex > 1)
                            continue;
                        if (!pages.TryGetValue(pageIndex, out var page))
                        {
                            page = new SkillInfoPageSnapshot();
                            pages[pageIndex] = page;
                        }

                        var entry = new SkillInfoEntrySnapshot
                        {
                            Slot = (byte)reader.GetInt32(1),
                            SkillId = (ushort)reader.GetInt32(2),
                            Level = (byte)reader.GetInt32(3),
                        };
                        if (!reader.IsDBNull(4))
                        {
                            foreach (var value in (byte[])reader[4])
                                entry.ExtraValues.Add(value);
                        }
                        page.Entries.Add(entry);
                    }
                }
            }

            for (var pageIndex = 0; pageIndex < 2; pageIndex++)
            {
                snapshot.Pages.Add(
                    pages.TryGetValue(pageIndex, out var page)
                        ? page
                        : new SkillInfoPageSnapshot());
            }
            return snapshot;
        }

        private static void SaveCore(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            SkillInfoSnapshot snapshot)
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM character_pvp_skills WHERE character_id=@cid";
                delete.Parameters.AddWithValue("@cid", characterId);
                delete.ExecuteNonQuery();
            }

            for (var pageIndex = 0;
                 pageIndex < Math.Min(2, snapshot.Pages.Count);
                 pageIndex++)
            {
                foreach (var entry in snapshot.Pages[pageIndex].Entries)
                {
                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = @"
INSERT INTO character_pvp_skills
    (character_id, page_index, slot, skill_id, level, extra_values)
VALUES
    (@cid, @page, @slot, @sid, @level, @extra);";
                        insert.Parameters.AddWithValue("@cid", characterId);
                        insert.Parameters.AddWithValue("@page", pageIndex);
                        insert.Parameters.AddWithValue("@slot", (int)entry.Slot);
                        insert.Parameters.AddWithValue("@sid", (int)entry.SkillId);
                        insert.Parameters.AddWithValue("@level", (int)entry.Level);
                        insert.Parameters.AddWithValue(
                            "@extra",
                            entry.ExtraValues.Count > 0
                                ? (object)entry.ExtraValues.ToArray()
                                : DBNull.Value);
                        insert.ExecuteNonQuery();
                    }
                }
            }
        }

        private static void MoveSkillSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int page,
            int fromSlot,
            int toSlot)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_pvp_skills
SET slot=@to
WHERE character_id=@cid AND page_index=@page AND slot=@from;";
                command.Parameters.AddWithValue("@to", toSlot);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@page", page);
                command.Parameters.AddWithValue("@from", fromSlot);
                command.ExecuteNonQuery();
            }
        }
    }
}
