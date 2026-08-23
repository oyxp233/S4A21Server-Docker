using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Characters
{
    internal sealed class GrowupChangeApplicationService
    {
        private const int MinTargetGrowType = 0;
        private const int MaxTargetGrowType = 5;

        internal bool TryChange(
            InventoryLease lease,
            GrowupChangeRequest request,
            out GrowupChangeResult result,
            out bool persistenceFailed)
        {
            result = CreateRejectedResult(
                request,
                GrowupChangeStatus.InvalidRequest,
                "invalid request");
            persistenceFailed = false;

            if (lease == null || lease.Inventory == null || request == null)
                return false;

            GrowupChangeResult committedResult = result;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "growup-change",
                (connection, transaction) =>
                    TryApply(
                        connection,
                        transaction,
                        lease.Inventory,
                        request,
                        out committedResult));

            result = committedResult ?? result;
            if (!committed)
            {
                persistenceFailed = true;
                result.Status = GrowupChangeStatus.PersistenceFailed;
                result.ResultCode = GrowupChangeResult.ResultCodeInvalidState;
                result.Detail = "commit failed";
                return false;
            }

            return result.Success;
        }

        internal static bool TryApply(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            GrowupChangeRequest request,
            out GrowupChangeResult result)
        {
            return TryApply(
                connection,
                transaction,
                inventory,
                request,
                GrowupChangeConfigProvider.Get(),
                out result);
        }

        internal static bool TryApply(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            GrowupChangeRequest request,
            GrowupChangeConfig config,
            out GrowupChangeResult result)
        {
            result = CreateRejectedResult(
                request,
                GrowupChangeStatus.InvalidRequest,
                "invalid request");
            if (connection == null
                || transaction == null
                || inventory == null
                || request == null)
            {
                return true;
            }

            if (request.TargetGrowType < MinTargetGrowType
                || request.TargetGrowType > MaxTargetGrowType)
            {
                result.Detail = "target grow type out of range";
                return true;
            }

            if (config == null || !config.IsValid)
            {
                result.Status = GrowupChangeStatus.ConfigUnavailable;
                result.Detail = "growup change config unavailable";
                return true;
            }

            if (!TryLoadState(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    out var state))
            {
                result.Detail = "character not found";
                return true;
            }

            result.PreviousChangeCount = state.ChangeCount;
            result.NewChangeCount = state.ChangeCount;

            var firstGrow = state.GrowType & 0x0F;
            var secondGrow = (state.GrowType >> 4) & 0x0F;
            if (!config.AllowsLevel(state.Level))
            {
                result.Status = GrowupChangeStatus.InvalidState;
                result.Detail = "character level is outside growup change range";
                return true;
            }

            if (firstGrow <= 0 || secondGrow != 0)
            {
                result.Status = GrowupChangeStatus.InvalidState;
                result.Detail = "character must be transferred and not awakened";
                return true;
            }

            if (request.TargetGrowType == firstGrow)
            {
                result.Status = GrowupChangeStatus.InvalidState;
                result.Detail = "target grow type is unchanged";
                return true;
            }

            var goldCost = config.ResolveGoldCost(state.ChangeCount);
            var currentGold = inventory.GetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
            if (currentGold < goldCost)
            {
                result.Status = GrowupChangeStatus.InsufficientGold;
                result.ResultCode = GrowupChangeResult.ResultCodeInsufficientGold;
                result.GoldCost = goldCost;
                result.UpdatedGold = currentGold;
                result.Detail = "insufficient gold";
                return true;
            }

            if (goldCost > 0
                && !inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    currentGold - goldCost))
            {
                result.Status = GrowupChangeStatus.PersistenceFailed;
                result.Detail = "gold mutation failed";
                return false;
            }

            QuestCompletionApplicationService.UpdateGrowType(
                connection,
                transaction,
                inventory.CharacterId,
                chainType: 1,
                growNumber: request.TargetGrowType);

            var newChangeCount = state.ChangeCount == int.MaxValue
                ? int.MaxValue
                : state.ChangeCount + 1;
            if (!UpdateChangeCount(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    newChangeCount))
            {
                result.Status = GrowupChangeStatus.PersistenceFailed;
                result.Detail = "change count update failed";
                return false;
            }

            var removedQuestCount = DeleteGrowupOrAwakeningActiveQuests(
                connection,
                transaction,
                inventory.CharacterId);

            result.Status = GrowupChangeStatus.Success;
            result.ResultCode = GrowupChangeResult.ResultCodeSuccess;
            result.NewGrowType = request.TargetGrowType;
            result.NewChangeCount = newChangeCount;
            result.GoldCost = goldCost;
            result.UpdatedGold = currentGold - goldCost;
            result.RemovedQuestCount = removedQuestCount;
            result.Detail = "success";
            return true;
        }

        private static GrowupChangeResult CreateRejectedResult(
            GrowupChangeRequest request,
            GrowupChangeStatus status,
            string detail)
        {
            return new GrowupChangeResult
            {
                Status = status,
                Detail = detail,
                ResultCode = GrowupChangeResult.ResultCodeInvalidState,
                TargetGrowType = request?.TargetGrowType ?? (byte)0,
            };
        }

        private static bool TryLoadState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            out GrowupChangeCharacterState state)
        {
            state = null;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT grow_type, level, growup_change_count
FROM characters
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    state = new GrowupChangeCharacterState
                    {
                        GrowType = reader.GetInt32(0),
                        Level = reader.GetInt32(1),
                        ChangeCount = reader.IsDBNull(2)
                            ? 0
                            : Math.Max(0, reader.GetInt32(2)),
                    };
                    return true;
                }
            }
        }

        private static bool UpdateChangeCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int changeCount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE characters
SET growup_change_count = @count,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@count", changeCount);
                command.Parameters.AddWithValue("@cid", characterId);
                return command.ExecuteNonQuery() == 1;
            }
        }

        internal static int DeleteGrowupOrAwakeningActiveQuests(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var removed = 0;
            foreach (var active in QuestRepository.LoadActiveQuests(
                         connection,
                         transaction,
                         characterId))
            {
                var quest = QuestData.GetQuestFile(active.QuestId);
                if (quest == null
                    || quest.JobChangeQuestValue < 1
                    || quest.JobChangeQuestValue > 3)
                {
                    continue;
                }

                removed += QuestRepository.DeleteActiveQuestsByQuestId(
                    connection,
                    transaction,
                    characterId,
                    active.QuestId);
            }

            return removed;
        }

        private sealed class GrowupChangeCharacterState
        {
            public int GrowType { get; set; }

            public int Level { get; set; }

            public int ChangeCount { get; set; }
        }
    }
}
