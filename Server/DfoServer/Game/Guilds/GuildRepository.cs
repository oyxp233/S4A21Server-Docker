using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Guilds
{
    internal sealed partial class GuildRepository
    {
        internal IGameDatabase Database { get; }
        internal GuildRepository(IGameDatabase database)
            => Database = database ?? throw new ArgumentNullException(nameof(database));

        internal bool NameExists(string name) => Database.Read(c => NameExists(c, null, name));
        internal bool IsMember(int characterId) => GetForMember(characterId) != null;
        internal GuildRecord GetForMember(int characterId) => Database.Read(c => GetForMember(c, null, characterId));
        internal GuildRecord Get(int guildId) => Database.Read(c => Get(c, null, guildId));

        internal GuildSearchResult FindByName(string name) => Database.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"SELECT g.guild_id,g.name,g.promotion,g.leader_character_id,COUNT(m.character_id)
FROM guilds g JOIN guild_members m ON m.guild_id=g.guild_id WHERE g.name=@name GROUP BY g.guild_id;";
            cmd.Parameters.AddWithValue("@name", name);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new GuildSearchResult(new GuildRecord(reader.GetInt32(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt32(3)), checked((ushort)reader.GetInt32(4))) : null;
        });

        internal IReadOnlyList<GuildApplication> GetApplicationsForLeader(int leaderId) => Database.Read(c =>
        {
            using var cmd = c.CreateCommand();
            // Persist an absolute UTC instant, project elapsed seconds only for the
            // client. Its date renderer uses signed comparisons, so cap at Int32.MaxValue.
            cmd.CommandText = @"SELECT c.character_id,c.name,c.job,c.grow_type,c.level,a.message,
MIN(2147483647,MAX(0,unixepoch('now')-unixepoch(a.applied_at)))
FROM guilds g JOIN guild_applications a ON a.guild_id=g.guild_id
JOIN characters c ON c.character_id=a.character_id AND c.delete_flag=0
WHERE g.leader_character_id=@leader ORDER BY a.applied_at,c.character_id;";
            cmd.Parameters.AddWithValue("@leader", leaderId);
            var result = new List<GuildApplication>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(new GuildApplication(reader.GetInt32(0), reader.GetString(1),
                checked((byte)reader.GetInt32(2)), checked((byte)reader.GetInt32(3)), checked((byte)reader.GetInt32(4)),
                reader.GetString(5), checked((uint)reader.GetInt64(6))));
            return (IReadOnlyList<GuildApplication>)result;
        });

        // Resolve membership and members in one SELECT snapshot. The caller cannot
        // request another guild's roster by supplying an arbitrary guild ID.
        internal GuildRoster GetRosterForMember(int characterId) => Database.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"SELECT g.guild_id,g.name,g.promotion,g.leader_character_id,
leader.name,member.character_id,member.account_id,member.name,member.level,member.job,member.grow_type,
MIN(2147483647,MAX(0,unixepoch('now')-unixepoch(COALESCE(d.last_logout_at,member.updated_at)))),
g.announcement,m.rank,m.memo
FROM guild_members self JOIN guilds g ON g.guild_id=self.guild_id
JOIN characters leader ON leader.character_id=g.leader_character_id
JOIN guild_members m ON m.guild_id=g.guild_id
JOIN characters member ON member.character_id=m.character_id AND member.delete_flag=0
LEFT JOIN account_daily_reset d ON d.account_id=member.account_id
WHERE self.character_id=@character ORDER BY member.character_id;";
            cmd.Parameters.AddWithValue("@character", characterId);
            using var reader = cmd.ExecuteReader();
            GuildRecord guild = null;
            string leaderName = null;
            var members = new List<GuildMemberRecord>();
            while (reader.Read())
            {
                guild ??= new GuildRecord(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(12));
                leaderName ??= reader.GetString(4);
                members.Add(new GuildMemberRecord(reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7),
                    checked((ushort)reader.GetInt32(8)), checked((byte)reader.GetInt32(9)), checked((byte)reader.GetInt32(10)),
                    reader.GetInt32(5) == guild.LeaderId, checked((uint)reader.GetInt64(11)), checked((byte)reader.GetInt32(13)), reader.GetString(14)));
            }
            return guild == null ? null : new GuildRoster(guild, leaderName, members);
        });

        internal static bool NameExists(SqliteConnection c, SqliteTransaction t, string name)
            => Scalar(c, t, "SELECT EXISTS(SELECT 1 FROM guilds WHERE name=@v);", name) != 0;
        internal static bool IsActiveCharacter(SqliteConnection c, SqliteTransaction t, int id)
            => Scalar(c, t, "SELECT EXISTS(SELECT 1 FROM characters WHERE character_id=@v AND delete_flag=0);", id) != 0;
        internal static GuildRecord GetForMember(SqliteConnection c, SqliteTransaction t, int characterId)
            => ReadGuild(c, t, @"SELECT g.guild_id,g.name,g.promotion,g.leader_character_id,g.announcement
FROM guilds g JOIN guild_members m ON m.guild_id=g.guild_id WHERE m.character_id=@v;", characterId);
        internal static GuildRecord Get(SqliteConnection c, SqliteTransaction t, int guildId)
            => ReadGuild(c, t, "SELECT guild_id,name,promotion,leader_character_id,announcement FROM guilds WHERE guild_id=@v;", guildId);

        internal static GuildRecord Insert(SqliteConnection c, SqliteTransaction t, int leaderId, string name, string promotion)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = t;
            cmd.CommandText = @"INSERT INTO guilds(name,promotion,leader_character_id) VALUES(@name,@promotion,@leader);
SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@promotion", promotion);
            cmd.Parameters.AddWithValue("@leader", leaderId);
            int guildId = checked(Convert.ToInt32(cmd.ExecuteScalar()));
            AddMember(c, t, guildId, leaderId);
            return new GuildRecord(guildId, name, promotion, leaderId);
        }

        internal static void AddMember(SqliteConnection c, SqliteTransaction t, int guildId, int characterId)
        {
            using var cmd = PairCommand(c, t, @"INSERT INTO guild_members(guild_id,character_id) VALUES(@guild,@character);
DELETE FROM guild_applications WHERE character_id=@character;", guildId, characterId);
            cmd.ExecuteNonQuery();
        }

        internal GuildResult Apply(int guildId, int characterId, string message = "") => Database.Write((c, t) =>
        {
            if (!GuildCreationRules.IsValidPromotion(message)) return GuildResult.InvalidPromotion;
            if (!IsActiveCharacter(c, t, characterId)) return GuildResult.MissingCharacter;
            if (GetForMember(c, t, characterId) != null) return GuildResult.AlreadyMember;
            if (Get(c, t, guildId) == null) return GuildResult.NotFound;
            using var cmd = PairCommand(c, t, @"INSERT INTO guild_applications(guild_id,character_id,message)
VALUES(@guild,@character,@message) ON CONFLICT(guild_id,character_id) DO NOTHING;", guildId, characterId);
            cmd.Parameters.AddWithValue("@message", message);
            cmd.ExecuteNonQuery();
            return GuildResult.Success;
        });

        internal GuildResult Approve(int guildId, int leaderId, int characterId) => Database.Write((c, t) =>
        {
            var guild = Get(c, t, guildId);
            if (guild == null) return GuildResult.NotFound;
            if (guild.LeaderId != leaderId || !IsActiveCharacter(c, t, leaderId)) return GuildResult.NotLeader;
            if (!IsActiveCharacter(c, t, characterId)) return GuildResult.MissingCharacter;
            if (GetForMember(c, t, characterId) != null) return GuildResult.AlreadyMember;
            using var cmd = PairCommand(c, t, @"SELECT EXISTS(SELECT 1 FROM guild_applications
WHERE guild_id=@guild AND character_id=@character);", guildId, characterId);
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0) return GuildResult.NotApplicant;
            AddMember(c, t, guildId, characterId);
            return GuildResult.Success;
        });

        internal GuildResult RemoveMember(int actorId, int characterId) => Database.Write((c, t) =>
        {
            var guild = GetForMember(c, t, characterId);
            if (guild == null) return GuildResult.NotFound;
            if (!IsActiveCharacter(c, t, actorId)) return GuildResult.MissingCharacter;
            if (actorId != characterId && actorId != guild.LeaderId) return GuildResult.NotLeader;
            if (guild.LeaderId == characterId) return GuildResult.LeaderCannotLeave;
            using var cmd = PairCommand(c, t, "DELETE FROM guild_members WHERE guild_id=@guild AND character_id=@character;", guild.Id, characterId);
            cmd.ExecuteNonQuery();
            return GuildResult.Success;
        });

        internal IReadOnlyList<int> GetMemberIds(int guildId) => Database.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT character_id FROM guild_members WHERE guild_id=@guild ORDER BY character_id;";
            cmd.Parameters.AddWithValue("@guild", guildId);
            var result = new List<int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetInt32(0));
            return (IReadOnlyList<int>)result;
        });

        private static GuildRecord ReadGuild(SqliteConnection c, SqliteTransaction t, string sql, int value)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@v", value);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new GuildRecord(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4)) : null;
        }
        private static int Scalar(SqliteConnection c, SqliteTransaction t, string sql, object value)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@v", value);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        private static SqliteCommand PairCommand(SqliteConnection c, SqliteTransaction t, string sql, int guildId, int characterId)
        {
            var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@guild", guildId); cmd.Parameters.AddWithValue("@character", characterId);
            return cmd;
        }
    }
}
