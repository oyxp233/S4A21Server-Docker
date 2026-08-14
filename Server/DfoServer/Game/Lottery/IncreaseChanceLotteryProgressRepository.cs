using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Lottery
{
    public sealed class IncreaseChanceLotteryProgressRepository
    {
        private readonly string _connectionString;

        public IncreaseChanceLotteryProgressRepository(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public HashSet<int> Load(int accountId, int itemTemplateId)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return Load(connection, null, accountId, itemTemplateId);
        }

        internal HashSet<int> Load(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int itemTemplateId)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            var result = new HashSet<int>();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"SELECT reward_index FROM account_increase_chance_lottery_progress
WHERE account_id=@accountId AND item_template_id=@itemTemplateId;";
            command.Parameters.AddWithValue("@accountId", accountId);
            command.Parameters.AddWithValue("@itemTemplateId", itemTemplateId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetInt32(0));
            return result;
        }

        public IReadOnlyList<LotteryProgressSnapshot> LoadAll(int accountId)
        {
            var byItem = new Dictionary<int, LotteryProgressSnapshot>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT item_template_id,reward_index
FROM account_increase_chance_lottery_progress
WHERE account_id=@accountId ORDER BY item_template_id,reward_index;";
            command.Parameters.AddWithValue("@accountId", accountId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var itemTemplateId = reader.GetInt32(0);
                if (!byItem.TryGetValue(itemTemplateId, out var snapshot))
                {
                    snapshot = new LotteryProgressSnapshot { ItemTemplateId = itemTemplateId };
                    byItem[itemTemplateId] = snapshot;
                }
                snapshot.ClaimedRewardIndexes.Add(reader.GetInt32(1));
            }
            return new List<LotteryProgressSnapshot>(byItem.Values);
        }

        public void SaveClaim(int accountId, int itemTemplateId, int rewardIndex, bool resetRound)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            SaveClaim(
                connection,
                transaction,
                accountId,
                itemTemplateId,
                rewardIndex,
                resetRound);
            transaction.Commit();
        }

        internal void SaveClaim(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int itemTemplateId,
            int rewardIndex,
            bool resetRound)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            if (resetRound)
            {
                Delete(
                    connection,
                    transaction,
                    accountId,
                    itemTemplateId);
                return;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT OR IGNORE INTO account_increase_chance_lottery_progress
(account_id,item_template_id,reward_index) VALUES (@accountId,@itemTemplateId,@rewardIndex);";
            command.Parameters.AddWithValue("@accountId", accountId);
            command.Parameters.AddWithValue("@itemTemplateId", itemTemplateId);
            command.Parameters.AddWithValue("@rewardIndex", rewardIndex);
            command.ExecuteNonQuery();
        }

        public void Reset(int accountId, int itemTemplateId)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            Reset(connection, transaction, accountId, itemTemplateId);
            transaction.Commit();
        }

        internal void Reset(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int itemTemplateId)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            Delete(
                connection,
                transaction,
                accountId,
                itemTemplateId);
        }

        private static void Delete(SqliteConnection connection, SqliteTransaction transaction, int accountId, int itemTemplateId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"DELETE FROM account_increase_chance_lottery_progress
WHERE account_id=@accountId AND item_template_id=@itemTemplateId;";
            command.Parameters.AddWithValue("@accountId", accountId);
            command.Parameters.AddWithValue("@itemTemplateId", itemTemplateId);
            command.ExecuteNonQuery();
        }
    }
}
