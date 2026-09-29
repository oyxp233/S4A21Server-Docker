using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.NpcFavor
{
    internal sealed class NpcFavorState
    {
        internal int CharacterId { get; set; }
        internal int NpcId { get; set; }
        internal int FavorPoint { get; set; }
        internal int GiftDayId { get; set; }
        internal int GiftActionCount { get; set; }
        internal int GiftItemCount { get; set; }
    }

    internal enum NpcFavorGiftApplyError
    {
        None = 0,
        InvalidRequest = 1,
        NpcDailyLimit = 2,
    }

    internal sealed class NpcFavorGiftApplyResult
    {
        internal bool Success { get; set; }
        internal NpcFavorGiftApplyError Error { get; set; }
        internal NpcFavorState State { get; set; }
        internal int AppliedFavorPointDelta { get; set; }
    }

    /// <summary>
    /// NPC 好感度持久化。建表只由 item_schema.sql / SqliteMigrations 负责；
    /// 赠礼更新提供现成事务变体，以便和在线背包扣除在同一事务提交。
    /// </summary>
    internal sealed class NpcFavorRepository
    {
        private readonly IGameDatabase _database;

        internal NpcFavorRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal NpcFavorState GetState(
            int characterId,
            int npcId,
            int defaultFavorPoint,
            int gameDayId)
        {
            using var connection = _database.OpenConnection();
            return ReadState(
                connection,
                transaction: null,
                characterId,
                npcId,
                defaultFavorPoint,
                gameDayId);
        }

        internal NpcFavorGiftApplyResult TryApplyGift(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int npcId,
            int defaultFavorPoint,
            int gameDayId,
            int itemCount,
            int favorPointDelta,
            int npcDailyGiftLimit,
            int maximumFavorPoint = NpcFavorProgressionPolicy.MaximumFavorPoint,
            int trustedFavorPoint = NpcFavorProgressionPolicy.TrustedFavorPoint)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (characterId <= 0
                || npcId < 0
                || defaultFavorPoint < 0
                || gameDayId <= 0
                || itemCount <= 0
                || npcDailyGiftLimit <= 0
                || maximumFavorPoint <= 0
                || trustedFavorPoint <= 0
                || trustedFavorPoint > maximumFavorPoint)
            {
                return Fail(NpcFavorGiftApplyError.InvalidRequest);
            }

            var current = ReadState(
                connection,
                transaction,
                characterId,
                npcId,
                defaultFavorPoint,
                gameDayId);
            if (current.GiftActionCount >= npcDailyGiftLimit)
                return Fail(NpcFavorGiftApplyError.NpcDailyLimit, current);

            var nextFavorPoint = Math.Clamp(
                (long)current.FavorPoint + favorPointDelta,
                0L,
                maximumFavorPoint);
            if (current.FavorPoint < trustedFavorPoint
                && nextFavorPoint >= trustedFavorPoint
                && ReadTrustedNpcCount(
                    connection,
                    transaction,
                    characterId,
                    npcId,
                    trustedFavorPoint) >= NpcFavorProgressionPolicy.MaximumTrustedNpcCount)
            {
                nextFavorPoint = trustedFavorPoint - 1;
            }
            var next = new NpcFavorState
            {
                CharacterId = characterId,
                NpcId = npcId,
                FavorPoint = (int)nextFavorPoint,
                GiftDayId = gameDayId,
                GiftActionCount = current.GiftActionCount + 1,
                GiftItemCount = checked(current.GiftItemCount + itemCount),
            };

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_npc_favor (
    character_id, npc_id, favor_point, gift_day_id,
    gift_action_count, gift_item_count, last_gift_at, updated_at
) VALUES (
    @characterId, @npcId, @favorPoint, @giftDayId,
    @giftActionCount, @giftItemCount, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
)
ON CONFLICT(character_id, npc_id) DO UPDATE SET
    favor_point = excluded.favor_point,
    gift_day_id = excluded.gift_day_id,
    gift_action_count = excluded.gift_action_count,
    gift_item_count = excluded.gift_item_count,
    last_gift_at = excluded.last_gift_at,
    updated_at = CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@npcId", npcId);
                command.Parameters.AddWithValue("@favorPoint", next.FavorPoint);
                command.Parameters.AddWithValue("@giftDayId", gameDayId);
                command.Parameters.AddWithValue("@giftActionCount", next.GiftActionCount);
                command.Parameters.AddWithValue("@giftItemCount", next.GiftItemCount);
                command.ExecuteNonQuery();
            }

            return new NpcFavorGiftApplyResult
            {
                Success = true,
                Error = NpcFavorGiftApplyError.None,
                State = next,
                AppliedFavorPointDelta = next.FavorPoint - current.FavorPoint,
            };
        }

        private static NpcFavorState ReadState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int npcId,
            int defaultFavorPoint,
            int gameDayId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT favor_point, gift_day_id, gift_action_count, gift_item_count
FROM character_npc_favor
WHERE character_id=@characterId AND npc_id=@npcId;";
            command.Parameters.AddWithValue("@characterId", characterId);
            command.Parameters.AddWithValue("@npcId", npcId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return new NpcFavorState
                {
                    CharacterId = characterId,
                    NpcId = npcId,
                    FavorPoint = Math.Max(0, defaultFavorPoint),
                    GiftDayId = gameDayId,
                };
            }

            var storedDayId = reader.GetInt32(1);
            return new NpcFavorState
            {
                CharacterId = characterId,
                NpcId = npcId,
                FavorPoint = Math.Max(0, reader.GetInt32(0)),
                GiftDayId = gameDayId,
                GiftActionCount = storedDayId == gameDayId ? reader.GetInt32(2) : 0,
                GiftItemCount = storedDayId == gameDayId ? reader.GetInt32(3) : 0,
            };
        }

        private static NpcFavorGiftApplyResult Fail(
            NpcFavorGiftApplyError error,
            NpcFavorState state = null)
            => new NpcFavorGiftApplyResult
            {
                Success = false,
                Error = error,
                State = state,
            };

        private static int ReadTrustedNpcCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int excludedNpcId,
            int trustedFavorPoint)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT COUNT(*)
FROM character_npc_favor
WHERE character_id=@characterId
  AND npc_id<>@excludedNpcId
  AND favor_point>=@trustedFavorPoint;";
            command.Parameters.AddWithValue("@characterId", characterId);
            command.Parameters.AddWithValue("@excludedNpcId", excludedNpcId);
            command.Parameters.AddWithValue(
                "@trustedFavorPoint",
                trustedFavorPoint);
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }
}
