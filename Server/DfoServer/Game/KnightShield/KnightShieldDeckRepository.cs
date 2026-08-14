using System;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.KnightShield
{
    public sealed class KnightShieldDeckRepository
    {
        private const string InsertSlotSql = @"
INSERT INTO character_knight_shield_deck(character_id, slot_index, shield_item_id)
VALUES(@cid, @slot, @item);";

        private readonly string _connectionString;

        public KnightShieldDeckRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public KnightShieldDeckRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        private KnightShieldDeckRepository(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public static KnightShieldDeckRepository FromConnectionString(string connectionString)
        {
            return new KnightShieldDeckRepository(connectionString);
        }

        public KnightShieldDeckSnapshot Load(int characterId)
        {
            ValidateCharacterId(characterId);
            var snapshot = new KnightShieldDeckSnapshot();

            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT slot_index, shield_item_id
FROM character_knight_shield_deck
WHERE character_id = @cid
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        snapshot.SetLoadedSlot(reader.GetInt32(0), reader.GetInt32(1));
                }
            }

            return snapshot;
        }

        public void Save(int characterId, KnightShieldDeckSnapshot snapshot)
        {
            ValidateCharacterId(characterId);
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM character_knight_shield_deck WHERE character_id = @cid;";
                    delete.Parameters.AddWithValue("@cid", characterId);
                    delete.ExecuteNonQuery();
                }

                for (var slotIndex = 0; slotIndex < KnightShieldDeckSnapshot.SlotCount; slotIndex++)
                {
                    var shieldItemId = snapshot.GetShieldItemId(slotIndex);
                    if (shieldItemId > 0)
                        InsertSlot(connection, transaction, characterId, slotIndex, shieldItemId);
                }

                transaction.Commit();
            }
        }

        private static void InsertSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int slotIndex,
            int shieldItemId)
        {
            using (var command = new SqliteCommand(InsertSlotSql, connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@slot", slotIndex);
                command.Parameters.AddWithValue("@item", shieldItemId);
                command.ExecuteNonQuery();
            }
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private static void ValidateCharacterId(int characterId)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId), "角色 ID 必须大于 0");
        }
    }
}
