using DfoServer.Infrastructure;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Guilds
{
    internal sealed class GuildDungeonRepository
    {
        private readonly IGameDatabase _database;
        private readonly MailboxService _mailbox;
        internal GuildDungeonRepository(IGameDatabase database)
        {
            _database = database;
            _mailbox = new MailboxService(new MailboxRepository(database));
        }

        internal GuildDungeonSnapshot Read(int characterId, GuildDungeonCatalog catalog, GuildDungeonPeriod period)
            => _database.Read(c =>
            {
                var guild = GuildRepository.GetForMember(c, null, characterId);
                return guild == null ? null : new GuildDungeonSnapshot(guild.Id, catalog.StyleId, period,
                    ReadCounts(c, null, guild.Id, period.StartUnix, catalog.StyleId));
            });

        private static Dictionary<int, int> ReadCounts(SqliteConnection c, SqliteTransaction t, int guildId, long period, int style)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "SELECT dungeon_id,clear_count FROM guild_dungeon_progress WHERE guild_id=@g AND period_start=@p AND style_id=@s;";
            cmd.Parameters.AddWithValue("@g", guildId);
            cmd.Parameters.AddWithValue("@p", period);
            cmd.Parameters.AddWithValue("@s", style);
            var counts = new Dictionary<int, int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) counts.Add(reader.GetInt32(0), reader.GetInt32(1));
            return counts;
        }

        internal GuildDungeonClearResult RecordClear(int characterId, int dungeonId, Guid eventId,
            GuildDungeonAdmission admission, GuildDungeonCatalog catalog, DateTimeOffset now)
        {
            if (eventId == Guid.Empty || admission == null || !catalog.Dungeons.TryGetValue(dungeonId, out var definition))
                throw new InvalidOperationException("Guild clear needs a frozen admission and authoritative event.");
            // The whole guild transaction (idempotency lookup, contribution, progress
            // and the reward mail) runs inside one immediate SQLite write
            // transaction, which owns the database write lock and therefore
            // serializes concurrent clears of the same guild.
            return _database.Write((c, t) =>
            {
                using var prior = c.CreateCommand();
                prior.Transaction = t;
                prior.CommandText = "SELECT mail_id,clear_count FROM guild_dungeon_clears WHERE event_id=@e AND character_id=@c;";
                prior.Parameters.AddWithValue("@e", eventId.ToString("N"));
                prior.Parameters.AddWithValue("@c", characterId);
                using (var reader = prior.ExecuteReader())
                    if (reader.Read()) return new GuildDungeonClearResult(false, reader.GetInt64(0), reader.GetInt32(1));
                var guild = GuildRepository.GetForMember(c, t, characterId);
                // An old-period run cannot advance the new period; a departed member
                // cannot donate progress or rewards to either the old or new guild.
                if (guild?.Id != admission.GuildId || GuildDungeonPeriod.At(now) != admission.Period)
                    return new GuildDungeonClearResult(false, 0, 0);
                var counts = ReadCounts(c, t, guild.Id, admission.Period.StartUnix, admission.StyleId);
                int previous = counts.GetValueOrDefault(dungeonId);
                int count = checked(previous + 1);
                var reward = definition.Rewards.First(r => definition.Ratio(previous) <= r.MaximumRatio);
                int accountId; byte level; string name;
                using (var character = c.CreateCommand())
                {
                    character.Transaction = t;
                    character.CommandText = "SELECT account_id,level,name FROM characters WHERE character_id=@c AND delete_flag=0;";
                    character.Parameters.AddWithValue("@c", characterId);
                    using var reader = character.ExecuteReader();
                    if (!reader.Read()) return new GuildDungeonClearResult(false, 0, 0);
                    accountId = reader.GetInt32(0); level = checked((byte)reader.GetInt32(1));
                    name = reader.GetValue(2) is byte[] raw ? ClientTextEncoding.GetString(raw) : reader.GetString(2);
                }
                var send = _mailbox.SendSystemMails(c, t, new[] { new MailboxSendRequest
                {
                    SenderCharacterId = characterId, SenderAccountId = accountId, SenderName = "",
                    ReceiverCharacterId = characterId, ReceiverAccountId = accountId, ReceiverName = name, ReceiverLevel = level,
                    Title = "公会地下城通关奖励", Text = "公会地下城通关奖励，请查收附件。",
                    MailType = 1, ExpireAtUtc = now.AddDays(30), AuditActor = "GuildDungeon",
                    AuditReason = "guild-dungeon-clear", IdempotencyKey = $"guild-dungeon:{eventId:N}:{characterId}",
                    Attachments = new[] { new MailboxSendAttachmentRequest { ItemId = reward.ItemId, ItemCount = reward.Count } }
                }});
                if (!send.Success) throw new InvalidOperationException($"Guild reward mail failed: {send.Error}");
                if (reward.Contribution > 0 && !GuildRepository.TryAddContribution(c, t, guild.Id, characterId, reward.Contribution))
                    throw new InvalidOperationException("Guild clear contribution failed.");
                using var update = c.CreateCommand();
                update.Transaction = t;
                update.CommandText = previous == 0 && !counts.ContainsKey(dungeonId)
                    ? "INSERT INTO guild_dungeon_progress(guild_id,period_start,style_id,dungeon_id,clear_count) VALUES(@g,@p,@s,@d,@n);"
                    : "UPDATE guild_dungeon_progress SET clear_count=@n WHERE guild_id=@g AND period_start=@p AND style_id=@s AND dungeon_id=@d;";
                update.Parameters.AddWithValue("@g", guild.Id);
                update.Parameters.AddWithValue("@p", admission.Period.StartUnix);
                update.Parameters.AddWithValue("@s", admission.StyleId);
                update.Parameters.AddWithValue("@d", dungeonId);
                update.Parameters.AddWithValue("@n", count);
                update.ExecuteNonQuery();
                using var ledger = c.CreateCommand();
                ledger.Transaction = t;
                ledger.CommandText = "INSERT INTO guild_dungeon_clears(event_id,character_id,guild_id,period_start,dungeon_id,mail_id,clear_count) VALUES(@e,@c,@g,@p,@d,@m,@n);";
                ledger.Parameters.AddWithValue("@e", eventId.ToString("N"));
                ledger.Parameters.AddWithValue("@c", characterId);
                ledger.Parameters.AddWithValue("@g", guild.Id);
                ledger.Parameters.AddWithValue("@p", admission.Period.StartUnix);
                ledger.Parameters.AddWithValue("@d", dungeonId);
                ledger.Parameters.AddWithValue("@m", send.MessageId);
                ledger.Parameters.AddWithValue("@n", count);
                ledger.ExecuteNonQuery();
                return new GuildDungeonClearResult(true, send.MessageId, count);
            });
        }
    }
}
