using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Names;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Pvp
{
    internal sealed class PvpTotalMatchTeam
    {
        internal PvpTotalMatchTeam(string name, int[] characterIds, byte[] slots)
        {
            Name = name;
            CharacterIds = Array.AsReadOnly((int[])characterIds.Clone());
            Slots = Array.AsReadOnly((byte[])slots.Clone());
        }

        internal string Name { get; }
        internal IReadOnlyList<int> CharacterIds { get; }
        internal IReadOnlyList<byte> Slots { get; }
    }

    internal sealed class SqlitePvpTotalMatchTeamRepository
    {
        private readonly IGameDatabase _database;

        internal SqlitePvpTotalMatchTeamRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal static bool IsValidName(byte[] bytes, out string name)
        {
            // teamnamepopup.xui strMaxLenth=12; GBK uses at most two bytes per character.
            return NameInputValidator.TryValidateRawName(bytes, 1, 24, out name, out _)
                && name.Length <= 12;
        }

        internal bool IsNameAvailable(int accountId, string name)
        {
            using var connection = _database.OpenConnection();
            return IsNameAvailable(connection, null, accountId, name);
        }

        internal PvpTotalMatchTeam Load(int accountId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: true);
            var stored = ReadStored(connection, transaction, accountId);
            if (stored == null)
                return null;
            var roster = ReadRoster(connection, transaction, accountId);
            var slots = new byte[3];
            for (var i = 0; i < slots.Length; i++)
            {
                var index = roster.FindIndex(character => character.Id == stored.Value.Ids[i]);
                if (index < 0 || index >= byte.MaxValue)
                    return null; // deleted/moved characters must never bind to a new slot occupant
                slots[i] = (byte)index;
            }
            transaction.Commit();
            return new PvpTotalMatchTeam(stored.Value.Name, stored.Value.Ids, slots);
        }

        internal bool TrySet(int accountId, int currentCharacterId, byte[] nameBytes,
            byte[] selectedSlots, out PvpTotalMatchTeam team, out byte error)
        {
            team = null;
            error = NameInputValidator.InvalidNameErrorCode;
            if (!IsValidName(nameBytes, out var name))
                return false;
            error = 2; // A21: invalid team composition
            if (selectedSlots?.Length != 3 || selectedSlots.Distinct().Count() != 3)
                return false;

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            var roster = ReadRoster(connection, transaction, accountId);
            if (!roster.Any(character => character.Id == currentCharacterId)
                || selectedSlots.Any(slot => slot == byte.MaxValue || slot >= roster.Count))
                return false;

            var members = selectedSlots.Select(slot => roster[slot]).ToArray();
            // 13E8600 splits roster grow_type into low-nibble profession and
            // awakening; 545800 compares the (job, profession) pairs for duplicates.
            if (members.Select(character => (character.Job, character.GrowType & 0xF)).Distinct().Count() != 3)
                return false;
            var stored = ReadStored(connection, transaction, accountId);
            if (stored != null && !string.Equals(stored.Value.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                // Paid rename has its own command and inventory transaction.
                // SET_TEAM may change members but cannot bypass that transaction.
                error = NameInputValidator.InvalidNameErrorCode;
                return false;
            }
            if (!IsNameAvailable(connection, transaction, accountId, name))
            {
                error = 1; // A21 SET_PVP_TOTAL_MATCH_TEAM: duplicate team name
                return false;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO account_pvp_total_match_teams(account_id, name, character_id_0, character_id_1, character_id_2)
VALUES(@aid, @name, @c0, @c1, @c2)
ON CONFLICT(account_id) DO UPDATE SET
    character_id_0=excluded.character_id_0, character_id_1=excluded.character_id_1,
    character_id_2=excluded.character_id_2, updated_at=CURRENT_TIMESTAMP;";
            command.Parameters.AddWithValue("@aid", accountId);
            command.Parameters.AddWithValue("@name", name);
            for (var i = 0; i < 3; i++)
                command.Parameters.AddWithValue("@c" + i, members[i].Id);
            command.ExecuteNonQuery();
            transaction.Commit();
            team = new PvpTotalMatchTeam(stored?.Name ?? name,
                members.Select(member => member.Id).ToArray(), selectedSlots);
            error = 0;
            return true;
        }

        private static bool IsNameAvailable(SqliteConnection connection, SqliteTransaction transaction,
            int accountId, string name)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT 1 FROM account_pvp_total_match_teams WHERE name=@name COLLATE NOCASE AND account_id<>@aid;";
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@aid", accountId);
            return command.ExecuteScalar() == null;
        }

        private static (string Name, int[] Ids)? ReadStored(SqliteConnection connection,
            SqliteTransaction transaction, int accountId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT name, character_id_0, character_id_1, character_id_2
FROM account_pvp_total_match_teams WHERE account_id=@aid;";
            command.Parameters.AddWithValue("@aid", accountId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? (reader.GetString(0), new[] { reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3) })
                : null;
        }

        private static List<(int Id, int Job, int GrowType)> ReadRoster(SqliteConnection connection,
            SqliteTransaction transaction, int accountId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Same ordering as ICharacterRepository.ListByAccount and the native slot request.
            command.CommandText = @"
SELECT character_id, job, grow_type FROM characters
WHERE account_id=@aid AND delete_flag=0 ORDER BY slot_index, character_id;";
            command.Parameters.AddWithValue("@aid", accountId);
            using var reader = command.ExecuteReader();
            var result = new List<(int Id, int Job, int GrowType)>();
            while (reader.Read())
                result.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
            return result;
        }
    }
}
