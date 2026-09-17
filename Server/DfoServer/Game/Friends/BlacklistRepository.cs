using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.Game.Friends
{
    // Character IDs own the relation; names are projected from current active characters.
    internal sealed class BlacklistRepository
    {
        internal const int MaximumEntries = 64;
        private readonly IGameDatabase _database;
        internal BlacklistRepository(IGameDatabase database)
            => _database = database ?? throw new ArgumentNullException(nameof(database));

        internal bool IsBlocked(int recipient, int sender)
            => recipient != sender && _database.Read(c => IsBlocked(c, null, recipient, sender));

        internal static bool IsBlocked(SqliteConnection c, SqliteTransaction t, int recipient, int sender)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = t;
            cmd.CommandText = @"SELECT EXISTS(SELECT 1 FROM character_blacklist b
JOIN characters o ON o.character_id=b.owner_character_id AND o.delete_flag=0
JOIN characters p ON p.character_id=b.target_character_id AND p.delete_flag=0
WHERE b.owner_character_id=@owner AND b.target_character_id=@target);";
            cmd.Parameters.AddWithValue("@owner", recipient); cmd.Parameters.AddWithValue("@target", sender);
            return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
        }

        // Target lookup, capacity check and insertion share one immediate transaction.
        internal BlacklistAddResult Add(int owner, string name) => _database.Write((c, t) =>
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = t;
            cmd.CommandText = "SELECT character_id FROM characters WHERE (name=@name OR name=@gbkBytes) AND delete_flag=0;";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@gbkBytes", ClientTextEncoding.GetBytes(name));
            var value = cmd.ExecuteScalar();
            if (value == null) return new BlacklistAddResult(BlacklistResult.NotFound, 0);
            int target = Convert.ToInt32(value);
            if (target == owner) return new BlacklistAddResult(BlacklistResult.Self, target);
            if (IsBlocked(c, t, owner, target)) return new BlacklistAddResult(BlacklistResult.Duplicate, target);
            cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("@owner", owner);
            cmd.CommandText = @"DELETE FROM character_blacklist WHERE owner_character_id=@owner
AND target_character_id IN (SELECT character_id FROM characters WHERE delete_flag<>0);
SELECT COUNT(*) FROM character_blacklist WHERE owner_character_id=@owner;";
            if (Convert.ToInt32(cmd.ExecuteScalar()) >= MaximumEntries) return new BlacklistAddResult(BlacklistResult.Full, target);
            cmd.Parameters.AddWithValue("@target", target);
            cmd.CommandText = @"INSERT INTO character_blacklist(owner_character_id,target_character_id)
SELECT @owner,@target WHERE EXISTS(SELECT 1 FROM characters WHERE character_id=@owner AND delete_flag=0);";
            return new BlacklistAddResult(cmd.ExecuteNonQuery() == 1 ? BlacklistResult.Success : BlacklistResult.InvalidOwner, target);
        });

        internal bool Remove(int owner, int target) => _database.Write((c, t) =>
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = t;
            cmd.CommandText = "DELETE FROM character_blacklist WHERE owner_character_id=@owner AND target_character_id=@target;";
            cmd.Parameters.AddWithValue("@owner", owner); cmd.Parameters.AddWithValue("@target", target);
            return cmd.ExecuteNonQuery() != 0;
        });

        internal bool Remove(int owner, string name) => _database.Write((c, t) =>
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = t;
            cmd.CommandText = @"DELETE FROM character_blacklist WHERE owner_character_id=@owner
AND target_character_id IN (SELECT character_id FROM characters WHERE (name=@name OR name=@gbkBytes) AND delete_flag=0);";
            cmd.Parameters.AddWithValue("@owner", owner); cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@gbkBytes", ClientTextEncoding.GetBytes(name));
            return cmd.ExecuteNonQuery() != 0;
        });

        internal IReadOnlyList<BlacklistEntry> List(int owner) => _database.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"SELECT p.name,b.created_at,p.character_id FROM character_blacklist b
JOIN characters o ON o.character_id=b.owner_character_id AND o.delete_flag=0
JOIN characters p ON p.character_id=b.target_character_id AND p.delete_flag=0
WHERE b.owner_character_id=@owner ORDER BY b.created_at,b.target_character_id;";
            cmd.Parameters.AddWithValue("@owner", owner);
            var result = new List<BlacklistEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(new BlacklistEntry(reader.GetInt32(2), reader.GetValue(0) is byte[] bytes
                ? ClientTextEncoding.GetString(bytes) : reader.GetString(0),
                DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)));
            return (IReadOnlyList<BlacklistEntry>)result;
        });
    }

    internal sealed record BlacklistEntry(int CharacterId, string Name, DateTime CreatedAt);
    internal sealed record BlacklistAddResult(BlacklistResult Status, int TargetId);
    internal enum BlacklistResult { Success, NotFound, Self, Duplicate, Full, InvalidOwner }
}
