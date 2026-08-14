using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.Game.Mercenary
{
    public sealed class MercenaryRepository
    {
        private readonly string _connectionString;

        public MercenaryRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public MercenaryRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public bool IsAssigned(int characterId)
        {
            if (characterId <= 0)
                return false;

            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT EXISTS(
    SELECT 1 FROM account_mercenary_assignments
    WHERE character_id = @cid AND status = 1
);";
                command.Parameters.AddWithValue("@cid", characterId);
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
            }
        }

        public MercenaryAssignment GetAssignment(int accountId, int characterId)
        {
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = AssignmentSelect + @"
WHERE account_id = @aid AND character_id = @cid AND status = 1;";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? ReadAssignment(reader) : null;
            }
        }

        public IReadOnlyList<MercenaryAssignment> ListAssignments(int accountId)
        {
            var result = new List<MercenaryAssignment>();
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = AssignmentSelect + @"
WHERE account_id = @aid AND status = 1
ORDER BY character_id;";
                command.Parameters.AddWithValue("@aid", accountId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(ReadAssignment(reader));
                }
            }
            return result;
        }

        public bool TryCreateAssignment(MercenaryAssignment assignment)
        {
            if (assignment == null)
                throw new ArgumentNullException(nameof(assignment));

            try
            {
                using (var connection = Open())
                using (var transaction = connection.BeginTransaction())
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO account_mercenary_assignments (
    account_id, character_id, character_level, start_time, finish_time,
    area_index, period_index, avatar_bonus_tier, status, version,
    created_at, updated_at
)
SELECT @aid, c.character_id, @level, @start, @finish,
       @area, @period, @avatar, 1, 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
FROM characters c
WHERE c.character_id = @cid
  AND c.account_id = @aid
  AND c.delete_flag = 0
  AND NOT EXISTS (
      SELECT 1 FROM account_mercenary_assignments a
      WHERE a.character_id = c.character_id
  );";
                    AddAssignmentParameters(command, assignment);
                    if (command.ExecuteNonQuery() != 1)
                    {
                        transaction.Rollback();
                        return false;
                    }

                    command.Parameters.Clear();
                    command.CommandText = "SELECT last_insert_rowid();";
                    assignment.AssignmentId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                    transaction.Commit();
                    return assignment.AssignmentId > 0;
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return false;
            }
        }

        public bool TryReplaceAssignment(
            MercenaryAssignment existing,
            MercenaryReward reward,
            byte returnPurpose,
            MercenaryAssignment replacement,
            out MercenaryRewardOutboxEntry settledReward)
        {
            if (existing == null)
                throw new ArgumentNullException(nameof(existing));
            if (reward == null)
                throw new ArgumentNullException(nameof(reward));
            if (replacement == null)
                throw new ArgumentNullException(nameof(replacement));

            settledReward = null;
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    MercenaryAssignment persisted;
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = AssignmentSelect + @"
WHERE assignment_id = @assignment
  AND account_id = @aid
  AND character_id = @cid
  AND status = 1;";
                        command.Parameters.AddWithValue("@assignment", existing.AssignmentId);
                        command.Parameters.AddWithValue("@aid", existing.AccountId);
                        command.Parameters.AddWithValue("@cid", existing.CharacterId);
                        using (var reader = command.ExecuteReader())
                            persisted = reader.Read() ? ReadAssignment(reader) : null;
                    }

                    if (persisted == null)
                    {
                        transaction.Rollback();
                        return false;
                    }

                    InsertOutbox(connection, transaction, persisted, reward, returnPurpose);
                    DeleteAssignment(connection, transaction, persisted);

                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
INSERT INTO account_mercenary_assignments (
    account_id, character_id, character_level, start_time, finish_time,
    area_index, period_index, avatar_bonus_tier, status, version,
    created_at, updated_at
)
SELECT @aid, c.character_id, @level, @start, @finish,
       @area, @period, @avatar, 1, 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
FROM characters c
WHERE c.character_id = @cid
  AND c.account_id = @aid
  AND c.delete_flag = 0
  AND NOT EXISTS (
      SELECT 1 FROM account_mercenary_assignments a
      WHERE a.character_id = c.character_id
  );";
                        AddAssignmentParameters(command, replacement);
                        if (command.ExecuteNonQuery() != 1)
                            throw new InvalidOperationException("replacement mercenary assignment insert failed");

                        command.Parameters.Clear();
                        command.CommandText = "SELECT last_insert_rowid();";
                        replacement.AssignmentId = Convert.ToInt64(
                            command.ExecuteScalar(),
                            CultureInfo.InvariantCulture);
                    }

                    settledReward = LoadOutbox(connection, transaction, persisted.AssignmentId)
                        ?? throw new InvalidOperationException("replacement mercenary reward outbox insert failed");
                    transaction.Commit();
                    return replacement.AssignmentId > 0;
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        public MercenaryRewardOutboxEntry Settle(
            MercenaryAssignment assignment,
            MercenaryReward reward,
            byte returnPurpose)
        {
            if (assignment == null)
                throw new ArgumentNullException(nameof(assignment));
            if (reward == null)
                throw new ArgumentNullException(nameof(reward));

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    MercenaryAssignment persisted;
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = AssignmentSelect + @"
WHERE assignment_id = @assignment
  AND account_id = @aid
  AND character_id = @cid
  AND status = 1;";
                        command.Parameters.AddWithValue("@assignment", assignment.AssignmentId);
                        command.Parameters.AddWithValue("@aid", assignment.AccountId);
                        command.Parameters.AddWithValue("@cid", assignment.CharacterId);
                        using (var reader = command.ExecuteReader())
                            persisted = reader.Read() ? ReadAssignment(reader) : null;
                    }

                    if (persisted == null)
                    {
                        var existing = LoadOutbox(connection, transaction, assignment.AssignmentId);
                        transaction.Commit();
                        return existing;
                    }

                    InsertOutbox(connection, transaction, persisted, reward, returnPurpose);
                    DeleteAssignment(connection, transaction, persisted);

                    var outbox = LoadOutbox(connection, transaction, persisted.AssignmentId)
                        ?? throw new InvalidOperationException("mercenary reward outbox insert failed");
                    transaction.Commit();
                    return outbox;
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        public MercenaryRewardOutboxEntry GetOutboxByAssignment(long assignmentId)
        {
            using (var connection = Open())
                return LoadOutbox(connection, null, assignmentId);
        }

        public IReadOnlyList<MercenaryRewardOutboxEntry> ListPendingOutbox(int limit = 100)
            => ListPendingOutbox(null, limit);

        public IReadOnlyList<MercenaryRewardOutboxEntry> ListPendingOutboxForAccount(
            int accountId,
            int limit = 100)
            => accountId > 0
                ? ListPendingOutbox(accountId, limit)
                : Array.Empty<MercenaryRewardOutboxEntry>();

        private IReadOnlyList<MercenaryRewardOutboxEntry> ListPendingOutbox(
            int? accountId,
            int limit)
        {
            var result = new List<MercenaryRewardOutboxEntry>();
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = OutboxSelect + @"
WHERE delivery_status = 'pending'
  AND (@aid IS NULL OR account_id = @aid)
ORDER BY outbox_id
LIMIT @limit;";
                command.Parameters.AddWithValue("@aid", accountId.HasValue
                    ? (object)accountId.Value
                    : DBNull.Value);
                command.Parameters.AddWithValue("@limit", Math.Max(1, limit));
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(ReadOutbox(reader));
                }

                for (var i = 0; i < result.Count; i++)
                    LoadOutboxItems(connection, null, result[i]);
            }
            return result;
        }

        public void MarkDelivered(long outboxId, long mailboxMessageId)
        {
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE mercenary_reward_outbox
SET delivery_status = 'delivered',
    delivery_attempts = delivery_attempts + 1,
    last_delivery_error = NULL,
    mailbox_message_id = CASE WHEN @message > 0 THEN @message ELSE NULL END,
    delivered_at = CURRENT_TIMESTAMP
WHERE outbox_id = @id AND delivery_status <> 'delivered';";
                command.Parameters.AddWithValue("@id", outboxId);
                command.Parameters.AddWithValue("@message", mailboxMessageId);
                command.ExecuteNonQuery();
            }
        }

        public void MarkDeliveryFailed(long outboxId, string error)
        {
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE mercenary_reward_outbox
SET delivery_attempts = delivery_attempts + 1,
    last_delivery_error = @error
WHERE outbox_id = @id AND delivery_status = 'pending';";
                command.Parameters.AddWithValue("@id", outboxId);
                command.Parameters.AddWithValue("@error", (object)error ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        private static void AddAssignmentParameters(SqliteCommand command, MercenaryAssignment assignment)
        {
            command.Parameters.AddWithValue("@aid", assignment.AccountId);
            command.Parameters.AddWithValue("@cid", assignment.CharacterId);
            command.Parameters.AddWithValue("@level", assignment.CharacterLevel);
            command.Parameters.AddWithValue("@start", assignment.StartTime);
            command.Parameters.AddWithValue("@finish", assignment.FinishTime);
            command.Parameters.AddWithValue("@area", (int)assignment.AreaIndex);
            command.Parameters.AddWithValue("@period", (int)assignment.PeriodIndex);
            command.Parameters.AddWithValue("@avatar", assignment.AvatarBonusTier);
        }

        private static void InsertOutbox(
            SqliteConnection connection,
            SqliteTransaction transaction,
            MercenaryAssignment assignment,
            MercenaryReward reward,
            byte returnPurpose)
        {
            var items = new List<MercenaryRewardItem>(reward.Items);
            if (items.Count == 0 && reward.ItemTemplateId > 0 && reward.ItemCount > 0)
            {
                items.Add(new MercenaryRewardItem
                {
                    ItemTemplateId = reward.ItemTemplateId,
                    ItemCount = reward.ItemCount,
                });
            }
            var primaryItem = items.Count > 0 ? items[0] : null;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR IGNORE INTO mercenary_reward_outbox (
    assignment_id, account_id, character_id, area_index, period_index,
    completed_hours, is_early_return, return_purpose,
    base_gold, bonus_gold, item_template_id, item_count,
    mail_title_key, mail_message_key, critical_multiplier_milli,
    delivery_status, delivery_attempts, created_at
) VALUES (
    @assignment, @aid, @cid, @area, @period,
    @hours, @early, @purpose,
    @baseGold, @bonusGold, @item, @itemCount,
    @title, @message, @critical,
    'pending', 0, CURRENT_TIMESTAMP
);";
                command.Parameters.AddWithValue("@assignment", assignment.AssignmentId);
                command.Parameters.AddWithValue("@aid", assignment.AccountId);
                command.Parameters.AddWithValue("@cid", assignment.CharacterId);
                command.Parameters.AddWithValue("@area", (int)assignment.AreaIndex);
                command.Parameters.AddWithValue("@period", (int)assignment.PeriodIndex);
                command.Parameters.AddWithValue("@hours", reward.CompletedHours);
                command.Parameters.AddWithValue("@early", reward.IsEarlyReturn ? 1 : 0);
                command.Parameters.AddWithValue("@purpose", (int)returnPurpose);
                command.Parameters.AddWithValue("@baseGold", reward.BaseGold);
                command.Parameters.AddWithValue("@bonusGold", reward.BonusGold);
                command.Parameters.AddWithValue("@item", primaryItem?.ItemTemplateId ?? 0);
                command.Parameters.AddWithValue("@itemCount", primaryItem?.ItemCount ?? 0);
                command.Parameters.AddWithValue("@title", reward.MailTitleKey ?? string.Empty);
                command.Parameters.AddWithValue("@message", reward.MailMessageKey ?? string.Empty);
                command.Parameters.AddWithValue(
                    "@critical",
                    (int)Math.Round(reward.CriticalMultiplier * 1000.0, MidpointRounding.AwayFromZero));
                command.ExecuteNonQuery();
            }

            for (var ordinal = 0; ordinal < items.Count; ordinal++)
            {
                var item = items[ordinal];
                if (item.ItemTemplateId <= 0 || item.ItemCount <= 0)
                    throw new InvalidOperationException("mercenary reward item is invalid");

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO mercenary_reward_items
    (outbox_id, ordinal, item_template_id, item_count)
SELECT outbox_id, @ordinal, @item, @count
FROM mercenary_reward_outbox
WHERE assignment_id = @assignment
ON CONFLICT(outbox_id, ordinal) DO UPDATE SET
    item_template_id = excluded.item_template_id,
    item_count = excluded.item_count;";
                    command.Parameters.AddWithValue("@assignment", assignment.AssignmentId);
                    command.Parameters.AddWithValue("@ordinal", ordinal);
                    command.Parameters.AddWithValue("@item", item.ItemTemplateId);
                    command.Parameters.AddWithValue("@count", item.ItemCount);
                    if (command.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("mercenary reward item insert failed");
                }
            }
        }

        private static void DeleteAssignment(
            SqliteConnection connection,
            SqliteTransaction transaction,
            MercenaryAssignment assignment)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM account_mercenary_assignments
WHERE assignment_id = @assignment AND account_id = @aid AND character_id = @cid;";
                command.Parameters.AddWithValue("@assignment", assignment.AssignmentId);
                command.Parameters.AddWithValue("@aid", assignment.AccountId);
                command.Parameters.AddWithValue("@cid", assignment.CharacterId);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("mercenary assignment changed during settlement");
            }
        }

        private static MercenaryRewardOutboxEntry LoadOutbox(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long assignmentId)
        {
            MercenaryRewardOutboxEntry result;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = OutboxSelect + " WHERE assignment_id = @assignment;";
                command.Parameters.AddWithValue("@assignment", assignmentId);
                using (var reader = command.ExecuteReader())
                    result = reader.Read() ? ReadOutbox(reader) : null;
            }
            if (result != null)
                LoadOutboxItems(connection, transaction, result);
            return result;
        }

        private static void LoadOutboxItems(
            SqliteConnection connection,
            SqliteTransaction transaction,
            MercenaryRewardOutboxEntry entry)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_template_id, item_count
FROM mercenary_reward_items
WHERE outbox_id = @outbox
ORDER BY ordinal;";
                command.Parameters.AddWithValue("@outbox", entry.OutboxId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        entry.Items.Add(new MercenaryRewardItem
                        {
                            ItemTemplateId = reader.GetInt32(0),
                            ItemCount = reader.GetInt32(1),
                        });
                    }
                }
            }

            if (entry.Items.Count == 0 && entry.ItemTemplateId > 0 && entry.ItemCount > 0)
            {
                entry.Items.Add(new MercenaryRewardItem
                {
                    ItemTemplateId = entry.ItemTemplateId,
                    ItemCount = entry.ItemCount,
                });
            }
        }

        private static MercenaryAssignment ReadAssignment(SqliteDataReader reader)
        {
            return new MercenaryAssignment
            {
                AssignmentId = reader.GetInt64(0),
                AccountId = reader.GetInt32(1),
                CharacterId = reader.GetInt32(2),
                CharacterLevel = reader.GetInt32(3),
                StartTime = reader.GetInt32(4),
                FinishTime = reader.GetInt32(5),
                AreaIndex = (byte)reader.GetInt32(6),
                PeriodIndex = (byte)reader.GetInt32(7),
                AvatarBonusTier = reader.GetInt32(8),
                Status = reader.GetInt32(9),
                Version = reader.GetInt32(10),
            };
        }

        private static MercenaryRewardOutboxEntry ReadOutbox(SqliteDataReader reader)
        {
            return new MercenaryRewardOutboxEntry
            {
                OutboxId = reader.GetInt64(0),
                AssignmentId = reader.GetInt64(1),
                MailboxMessageId = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                AccountId = reader.GetInt32(3),
                CharacterId = reader.GetInt32(4),
                AreaIndex = (byte)reader.GetInt32(5),
                PeriodIndex = (byte)reader.GetInt32(6),
                CompletedHours = reader.GetInt32(7),
                IsEarlyReturn = reader.GetInt32(8) != 0,
                ReturnPurpose = (byte)reader.GetInt32(9),
                BaseGold = reader.GetInt32(10),
                BonusGold = reader.GetInt32(11),
                ItemTemplateId = reader.GetInt32(12),
                ItemCount = reader.GetInt32(13),
                MailTitleKey = reader.GetString(14),
                MailMessageKey = reader.GetString(15),
                CriticalMultiplier = reader.GetInt32(16) / 1000.0,
                DeliveryStatus = reader.GetString(17),
                DeliveryAttempts = reader.GetInt32(18),
            };
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private const string AssignmentSelect = @"
SELECT assignment_id, account_id, character_id, character_level,
       start_time, finish_time, area_index, period_index,
       avatar_bonus_tier, status, version
FROM account_mercenary_assignments
";

        private const string OutboxSelect = @"
SELECT outbox_id, assignment_id, mailbox_message_id, account_id, character_id,
       area_index, period_index, completed_hours, is_early_return,
       return_purpose, base_gold, bonus_gold, item_template_id, item_count,
       mail_title_key, mail_message_key, critical_multiplier_milli,
       delivery_status, delivery_attempts
FROM mercenary_reward_outbox
";
    }
}
