using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Progression
{
    internal static class CharacterLevelUpRewardRepository
    {
        internal const string BaseRewardKey = "base-level-up";

        internal static bool WasDelivered(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, byte level)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT 1 FROM character_level_up_rewards WHERE character_id = @cid AND reward_key = @key AND level = @level;";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@key", BaseRewardKey);
            command.Parameters.AddWithValue("@level", level);
            return command.ExecuteScalar() != null;
        }

        internal static void RecordDelivery(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, CharacterLevelUpReward reward, long messageId, DateTimeOffset now)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO character_level_up_rewards
    (character_id, reward_key, level, item_id, item_count, message_id, delivered_at_utc)
VALUES (@cid, @key, @level, @item, @count, @message, @now);";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@key", BaseRewardKey);
            command.Parameters.AddWithValue("@level", reward.Level);
            command.Parameters.AddWithValue("@item", reward.ItemId);
            command.Parameters.AddWithValue("@count", reward.ItemCount);
            command.Parameters.AddWithValue("@message", messageId);
            command.Parameters.AddWithValue("@now", now.ToString("O"));
            command.ExecuteNonQuery();
        }
    }
}
