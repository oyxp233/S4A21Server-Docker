using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Guilds
{
    internal sealed partial class GuildRepository
    {
        internal IReadOnlyList<GuildContent> GetContents(int characterId, long? now = null)
            => Database.Read(c => ReadContents(c, null, characterId, now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        internal static IReadOnlyList<GuildContent> ReadContents(SqliteConnection c, SqliteTransaction t, int characterId, long now)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = @"SELECT x.content_type,x.content_id,x.content_value,x.expires_at
FROM guild_contents x JOIN guild_members m ON m.guild_id=x.guild_id
WHERE m.character_id=@character AND (x.expires_at=0 OR x.expires_at>@now) ORDER BY x.content_type;";
            cmd.Parameters.AddWithValue("@character", characterId);
            cmd.Parameters.AddWithValue("@now", now);
            using var reader = cmd.ExecuteReader();
            var rows = new List<GuildContent>();
            while (reader.Read())
                rows.Add(new GuildContent(checked((byte)reader.GetInt32(0)), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3)));
            return rows;
        }

        // Caller holds the current character transition. IGameDatabase.Write opens an
        // immediate write transaction, so the guild row, its funds and the content
        // ownership of this guild are serialized without an explicit row lock.
        internal GuildResult PurchaseContent(int actor, GuildContentDefinition definition, long now)
        {
            if (!GuildContentRules.Supported(definition)) return GuildResult.UnsupportedContent;
            return Database.Write((c, t) =>
            {
                var guild = GetForMember(c, t, actor);
                if (guild == null) return GuildResult.NotFound;
                using var command = c.CreateCommand();
                command.Transaction = t;
                command.CommandText = "SELECT leader_character_id FROM guilds WHERE guild_id=@guild;";
                command.Parameters.AddWithValue("@guild", guild.Id);
                if (Convert.ToInt32(command.ExecuteScalar()) != actor || !IsActiveCharacter(c, t, actor)) return GuildResult.NotLeader;
                if (ReadContents(c, t, actor, now).Any(x => x.Type == definition.Type)) return GuildResult.ContentAlreadyActive;
                long expiry = definition.Days < 0 ? 0 : checked(now + definition.Days * 86400L);
                if (expiry >= int.MaxValue) return GuildResult.InvalidAmount;
                command.CommandText = "UPDATE guilds SET contribution=contribution-@price WHERE guild_id=@guild AND contribution>=@price;";
                command.Parameters.AddWithValue("@price", definition.Price);
                if (command.ExecuteNonQuery() != 1) return GuildResult.InsufficientContribution;
                command.Parameters.Clear();
                command.CommandText = @"INSERT INTO guild_contents(guild_id,content_type,content_id,content_value,expires_at)
VALUES(@guild,@type,@id,@value,@expiry)
ON CONFLICT(guild_id,content_type) DO UPDATE SET
content_id=excluded.content_id,content_value=excluded.content_value,expires_at=excluded.expires_at;";
                command.Parameters.AddWithValue("@guild", guild.Id);
                command.Parameters.AddWithValue("@type", definition.Type);
                command.Parameters.AddWithValue("@id", definition.Id);
                command.Parameters.AddWithValue("@value", definition.Value);
                command.Parameters.AddWithValue("@expiry", expiry);
                command.ExecuteNonQuery();
                return GuildResult.Success;
            });
        }
    }
}
