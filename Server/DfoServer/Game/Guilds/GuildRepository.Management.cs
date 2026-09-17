using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Guilds
{
    internal sealed partial class GuildRepository
    {
        internal GuildPendingApplication GetPendingApplication(int characterId) => Database.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"SELECT g.guild_id,g.name,g.promotion,g.leader_character_id,g.announcement,a.message
FROM guild_applications a JOIN guilds g ON g.guild_id=a.guild_id
WHERE a.character_id=@v AND NOT EXISTS(SELECT 1 FROM guild_members WHERE character_id=@v)
ORDER BY a.applied_at,g.guild_id LIMIT 1;";
            cmd.Parameters.AddWithValue("@v", characterId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new GuildPendingApplication(new GuildRecord(r.GetInt32(0), r.GetString(1),
                r.GetString(2), r.GetInt32(3), r.GetString(4)), r.GetString(5)) : null;
        });

        internal GuildResult CancelApplication(int characterId, int guildId) => Database.Write((c, t) =>
        {
            if (!IsActiveCharacter(c, t, characterId)) return GuildResult.MissingCharacter;
            using var cmd = PairCommand(c, t, "DELETE FROM guild_applications WHERE guild_id=@guild AND character_id=@character;", guildId, characterId);
            return cmd.ExecuteNonQuery() == 1 ? GuildResult.Success : GuildResult.NotApplicant;
        });

        internal GuildResult DenyApplication(int actorId, int targetId) => Database.Write((c, t) =>
        {
            var guild = GetForMember(c, t, actorId);
            if (guild == null || guild.LeaderId != actorId || !IsActiveCharacter(c, t, actorId)) return GuildResult.NotLeader;
            using var cmd = PairCommand(c, t, "DELETE FROM guild_applications WHERE guild_id=@guild AND character_id=@character;", guild.Id, targetId);
            return cmd.ExecuteNonQuery() == 1 ? GuildResult.Success : GuildResult.NotApplicant;
        });

        // Member rank 1 is projected ONLY from guilds.leader_character_id. It is
        // never stored twice. Transfer and demotion commit in the same transaction.
        internal GuildResult ChangeRank(int actorId, int targetId, byte rank) => Database.Write((c, t) =>
        {
            if (rank < 1 || rank > 5) return GuildResult.InvalidRank;
            var guild = GetForMember(c, t, actorId);
            if (guild == null || guild.LeaderId != actorId || !IsActiveCharacter(c, t, actorId)) return GuildResult.NotLeader;
            if (targetId == actorId) return GuildResult.LeaderCannotLeave;
            if (!IsActiveCharacter(c, t, targetId) || GetForMember(c, t, targetId)?.Id != guild.Id) return GuildResult.NotFound;
            using var cmd = PairCommand(c, t, rank == 1
                ? @"UPDATE guild_members SET rank=4 WHERE guild_id=@guild AND character_id IN (@character,@actor);
UPDATE guilds SET leader_character_id=@character WHERE guild_id=@guild AND leader_character_id=@actor;"
                : "UPDATE guild_members SET rank=@rank WHERE guild_id=@guild AND character_id=@character;", guild.Id, targetId);
            cmd.Parameters.AddWithValue("@actor", actorId);
            cmd.Parameters.AddWithValue("@rank", rank);
            cmd.ExecuteNonQuery();
            return GuildResult.Success;
        });

        internal GuildResult EditText(int actorId, GuildTextField field, string text) => Database.Write((c, t) =>
        {
            if (!GuildManagementRules.IsValidText(field, text)) return GuildResult.InvalidPromotion;
            var guild = GetForMember(c, t, actorId);
            if (guild == null || !IsActiveCharacter(c, t, actorId)) return GuildResult.NotFound;
            if (field != GuildTextField.Memo && guild.LeaderId != actorId) return GuildResult.NotLeader;
            string sql = field switch
            {
                GuildTextField.Announcement => "UPDATE guilds SET announcement=@text WHERE guild_id=@guild;",
                GuildTextField.Promotion => "UPDATE guilds SET promotion=@text WHERE guild_id=@guild;",
                GuildTextField.Memo => "UPDATE guild_members SET memo=@text WHERE guild_id=@guild AND character_id=@character;",
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
            using var cmd = PairCommand(c, t, sql, guild.Id, actorId);
            cmd.Parameters.AddWithValue("@text", text);
            cmd.ExecuteNonQuery();
            return GuildResult.Success;
        });

        internal GuildResult Disband(int actorId) => Disband(actorId, out _);

        internal GuildResult Disband(int actorId, out IReadOnlyList<int> affectedCharacters)
        {
            var affected = new List<int>();
            var result = Database.Write((c, t) =>
            {
            var guild = GetForMember(c, t, actorId);
            if (guild == null || guild.LeaderId != actorId || !IsActiveCharacter(c, t, actorId)) return GuildResult.NotLeader;
            // Capture applicants in the same transaction as the cascade. An
            // application committed just before disband must also lose its UI.
            using (var read = PairCommand(c, t, @"SELECT character_id FROM guild_members WHERE guild_id=@guild
UNION SELECT character_id FROM guild_applications WHERE guild_id=@guild;", guild.Id, actorId))
            using (var reader = read.ExecuteReader())
                while (reader.Read()) affected.Add(reader.GetInt32(0));
            using var cmd = PairCommand(c, t, "DELETE FROM guilds WHERE guild_id=@guild AND leader_character_id=@character;", guild.Id, actorId);
            cmd.ExecuteNonQuery(); // Foreign keys cascade members and pending applications.
            return GuildResult.Success;
            });
            affectedCharacters = affected;
            return result;
        }
    }
}
