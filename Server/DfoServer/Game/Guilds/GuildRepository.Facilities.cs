using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Guilds
{
    internal sealed partial class GuildRepository
    {
        internal static int ReadMemberContribution(SqliteConnection c, SqliteTransaction t, int characterId)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "SELECT contribution FROM guild_members WHERE character_id=@character;";
            cmd.Parameters.AddWithValue("@character", characterId);
            var value = cmd.ExecuteScalar();
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        internal static bool TryAddContribution(SqliteConnection c, SqliteTransaction t, int guildId, int characterId, int points)
        {
            if (points <= 0) return false;
            using var guild = c.CreateCommand();
            guild.Transaction = t;
            guild.CommandText = @"UPDATE guilds SET contribution=contribution+@points
WHERE guild_id=@guild AND contribution+@points<=2147483647;";
            guild.Parameters.AddWithValue("@points", points);
            guild.Parameters.AddWithValue("@guild", guildId);
            if (guild.ExecuteNonQuery() != 1) return false;
            using var member = c.CreateCommand();
            member.Transaction = t;
            member.CommandText = @"UPDATE guild_members SET contribution=contribution+@points
WHERE guild_id=@guild AND character_id=@character AND contribution+@points<=2147483647;";
            member.Parameters.AddWithValue("@points", points);
            member.Parameters.AddWithValue("@guild", guildId);
            member.Parameters.AddWithValue("@character", characterId);
            return member.ExecuteNonQuery() == 1;
        }

        internal static bool TryAdvanceAgit(SqliteConnection c, SqliteTransaction t, int guildId, int leaderId, int expectedLevel, int nextLevel, int requiredContribution)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = @"UPDATE guilds SET agit_level=@next
WHERE guild_id=@guild AND leader_character_id=@leader AND agit_level=@expected AND contribution>=@need;";
            cmd.Parameters.AddWithValue("@next", nextLevel);
            cmd.Parameters.AddWithValue("@guild", guildId);
            cmd.Parameters.AddWithValue("@leader", leaderId);
            cmd.Parameters.AddWithValue("@expected", expectedLevel);
            cmd.Parameters.AddWithValue("@need", requiredContribution);
            return cmd.ExecuteNonQuery() == 1;
        }

        internal static bool TryAdvanceCargo(SqliteConnection c, SqliteTransaction t, int guildId, int leaderId, int expectedSlots, int nextSlots, int requiredContribution, int slotLimit)
        {
            if (nextSlots <= expectedSlots || nextSlots > slotLimit) return false;
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = @"UPDATE guilds SET cargo_slot_count=@next
WHERE guild_id=@guild AND leader_character_id=@leader AND cargo_slot_count=@expected AND contribution>=@need;";
            cmd.Parameters.AddWithValue("@next", nextSlots);
            cmd.Parameters.AddWithValue("@guild", guildId);
            cmd.Parameters.AddWithValue("@leader", leaderId);
            cmd.Parameters.AddWithValue("@expected", expectedSlots);
            cmd.Parameters.AddWithValue("@need", requiredContribution);
            return cmd.ExecuteNonQuery() == 1;
        }

        internal static (int Contribution, int AgitLevel, int CargoSlots) ReadFacility(SqliteConnection c, SqliteTransaction t, int guildId)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "SELECT contribution,agit_level,cargo_slot_count FROM guilds WHERE guild_id=@guild;";
            cmd.Parameters.AddWithValue("@guild", guildId);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))
                : (-1, -1, -1);
        }
    }
}
