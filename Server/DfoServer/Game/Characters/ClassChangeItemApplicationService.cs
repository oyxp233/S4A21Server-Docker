using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Characters
{
    internal sealed class ClassChangeItemApplicationService
    {
        private const int MinTargetGrowType = 0;
        private const int MaxTargetGrowType = 5;
        private const int BeginnerIconIndex = 788;
        private const int AdvancedIconIndex = 789;
        private const string ClassChangeActionType = "class change";

        internal bool TryUse(
            InventoryLease lease,
            ClassChangeItemRequest request,
            out ClassChangeItemResult result,
            out bool persistenceFailed)
        {
            result = CreateRejectedResult(
                request,
                ClassChangeItemStatus.InvalidRequest,
                "invalid request");
            persistenceFailed = false;

            if (lease == null || lease.Inventory == null || request == null)
                return false;

            ClassChangeItemResult committedResult = result;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "class-change-item",
                (connection, transaction) =>
                    TryApply(
                        connection,
                        transaction,
                        lease.Inventory,
                        request,
                        StackableItemProvider.Load,
                        out committedResult));

            result = committedResult ?? result;
            if (!committed)
            {
                persistenceFailed = true;
                result.Status = ClassChangeItemStatus.PersistenceFailed;
                result.Detail = "commit failed";
                return false;
            }

            return result.Success;
        }

        internal static bool TryApply(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            ClassChangeItemRequest request,
            Func<int, StackableItemFile> stackableLoader,
            out ClassChangeItemResult result)
        {
            result = CreateRejectedResult(
                request,
                ClassChangeItemStatus.InvalidRequest,
                "invalid request");
            if (connection == null
                || transaction == null
                || inventory == null
                || request == null)
            {
                return true;
            }

            if (request.ItemSlotIndex < 0
                || request.TargetGrowType < MinTargetGrowType
                || request.TargetGrowType > MaxTargetGrowType)
            {
                result.Detail = "request value out of range";
                return true;
            }

            if (!InventoryDeleteService.CanUseStackableForClient(
                    inventory,
                    InventoryListType.Main,
                    request.ItemSlotIndex,
                    0,
                    out var itemTemplateId))
            {
                return Fail(
                    result,
                    ClassChangeItemStatus.SourceMissing,
                    "source item is unavailable",
                    false);
            }

            result.ItemTemplateId = itemTemplateId;
            var now = InventoryItemLifecycleService.UtcNowUnixSeconds();
            if (InventoryItemLifecycleService.TryRemoveExpiredSource(
                    inventory,
                    InventoryListType.Main,
                    request.ItemSlotIndex,
                    itemTemplateId,
                    now,
                    out var expiredMutation))
            {
                result.Status = ClassChangeItemStatus.SourceExpired;
                result.Detail = "source item has expired";
                result.SourceMutation = expiredMutation;
                AddUnique(result.MainRefreshSlots, request.ItemSlotIndex);
                return true;
            }

            var stackable = (stackableLoader ?? StackableItemProvider.Load)
                .Invoke(itemTemplateId);
            if (!TryResolveDefinition(stackable, result, out var mode))
                return true;
            result.Mode = mode;

            var lifecyclePlan = InventoryItemLifecycleService
                .PrepareUseWithDefinition(
                    inventory,
                    InventoryListType.Main,
                    request.ItemSlotIndex,
                    itemTemplateId,
                    now,
                    1,
                    stackable,
                    checkEffectMaintenance: false,
                    checkCooltimeMaintenance: true);
            if (!lifecyclePlan.Success)
            {
                ApplyLifecycleFailure(result, lifecyclePlan);
                return true;
            }

            if (!TryLoadState(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    out var state))
            {
                return Fail(
                    result,
                    ClassChangeItemStatus.InvalidState,
                    "character not found",
                    false);
            }

            result.PreviousGrowType = state.GrowType;
            if (!AllowsLevel(stackable, state.Level))
            {
                return Fail(
                    result,
                    ClassChangeItemStatus.LevelRejected,
                    "character level is outside item range",
                    false);
            }

            var firstGrow = state.GrowType & 0x0F;
            var secondGrow = (state.GrowType >> 4) & 0x0F;
            if (request.TargetGrowType == firstGrow)
            {
                return Fail(
                    result,
                    ClassChangeItemStatus.TargetUnchanged,
                    "target grow type is unchanged",
                    false);
            }

            if (!ValidateCurrentGrowth(mode, firstGrow, secondGrow, result))
                return true;

            if (!ConsumeItemAndRecordUse(
                    connection,
                    transaction,
                    inventory,
                    request,
                    itemTemplateId,
                    result,
                    out var usableCountState))
            {
                return result.Status != ClassChangeItemStatus.MutationFailed;
            }

            InventoryItemLifecycleService.ApplyUseSuccess(inventory, lifecyclePlan);

            if (mode == ClassChangeItemMode.Advanced)
            {
                QuestCompletionApplicationService.UpdateGrowTypeExact(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    request.TargetGrowType,
                    secondGrow);
                result.NewGrowType =
                    ((secondGrow & 0x0F) << 4)
                    | (request.TargetGrowType & 0x0F);
                result.MarkedAwakeningQuestCount =
                    MarkPreservedAwakeningQuestsCleared(
                        connection,
                        transaction,
                        inventory.CharacterId,
                        request.TargetGrowType,
                        secondGrow);
            }
            else
            {
                QuestCompletionApplicationService.UpdateGrowType(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    chainType: 1,
                    growNumber: request.TargetGrowType);
                result.NewGrowType = request.TargetGrowType & 0x0F;
            }

            result.UsableCountState = usableCountState;
            result.RemovedQuestCount =
                GrowupChangeApplicationService
                    .DeleteGrowupOrAwakeningActiveQuests(
                        connection,
                        transaction,
                        inventory.CharacterId);
            result.Status = ClassChangeItemStatus.Success;
            result.Detail = "success";
            return true;
        }

        private static bool TryResolveDefinition(
            StackableItemFile stackable,
            ClassChangeItemResult result,
            out ClassChangeItemMode mode)
        {
            mode = ClassChangeItemMode.Unknown;
            if (stackable == null)
                return Fail(
                    result,
                    ClassChangeItemStatus.InvalidItem,
                    "stackable definition is missing",
                    false);

            var action = NormalizeActionType(stackable.ActionTypeName);
            if (!string.Equals(
                    action,
                    ClassChangeActionType,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    result,
                    ClassChangeItemStatus.InvalidItem,
                    "action type is not class change",
                    false);
            }

            var iconIndex = ResolveIconIndex(stackable.Icon);
            if (iconIndex == BeginnerIconIndex)
            {
                mode = ClassChangeItemMode.Beginner;
                return true;
            }

            if (iconIndex == AdvancedIconIndex)
            {
                mode = ClassChangeItemMode.Advanced;
                return true;
            }

            return Fail(
                result,
                ClassChangeItemStatus.InvalidItem,
                "class change item icon is unsupported",
                false);
        }

        private static bool ValidateCurrentGrowth(
            ClassChangeItemMode mode,
            int firstGrow,
            int secondGrow,
            ClassChangeItemResult result)
        {
            if (firstGrow <= 0)
            {
                return Fail(
                    result,
                    ClassChangeItemStatus.InvalidState,
                    "character must be transferred",
                    false);
            }

            if (mode == ClassChangeItemMode.Beginner)
            {
                if (secondGrow == 0)
                    return true;

                return Fail(
                    result,
                    ClassChangeItemStatus.InvalidState,
                    "beginner item requires not awakened",
                    false);
            }

            if (mode == ClassChangeItemMode.Advanced && secondGrow >= 1)
                return true;

            return Fail(
                result,
                ClassChangeItemStatus.InvalidState,
                "advanced item requires first or second awakening",
                false);
        }

        private static int MarkPreservedAwakeningQuestsCleared(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int targetFirstGrow,
            int preservedSecondGrow)
        {
            if (connection == null
                || transaction == null
                || characterId <= 0
                || preservedSecondGrow <= 0)
            {
                return 0;
            }

            var marked = 0;
            foreach (var questId in QuestCatalog.OrderedIds)
            {
                if (questId <= 0 || questId > ushort.MaxValue)
                    continue;

                var quest = QuestData.GetQuestFile(questId);
                if (quest == null
                    || quest.GrowType != targetFirstGrow
                    || !ShouldPreserveAwakeningQuestClear(
                        quest.JobChangeQuestValue,
                        preservedSecondGrow))
                {
                    continue;
                }

                QuestRepository.MarkQuestCleared(
                    connection,
                    transaction,
                    characterId,
                    (ushort)questId);
                marked++;
            }

            return marked;
        }

        private static bool ShouldPreserveAwakeningQuestClear(
            int jobChangeQuestValue,
            int preservedSecondGrow)
        {
            if (jobChangeQuestValue == 2)
                return preservedSecondGrow >= 1;
            if (jobChangeQuestValue == 3)
                return preservedSecondGrow >= 2;

            return false;
        }

        private static bool ConsumeItemAndRecordUse(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            ClassChangeItemRequest request,
            int itemTemplateId,
            ClassChangeItemResult result,
            out UsableCountLimitState usableCountState)
        {
            usableCountState = null;
            if (!UsableCountLimitService.TryRecordUseIfLimited(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    itemTemplateId,
                    1,
                    out usableCountState))
            {
                return Fail(
                    result,
                    ClassChangeItemStatus.UsableCountLimitExceeded,
                    "usable count limit exceeded",
                    false);
            }

            if (!InventoryDeleteService.TryUseStackableForClient(
                    inventory,
                    InventoryListType.Main,
                    request.ItemSlotIndex,
                    itemTemplateId,
                    out var mutation))
            {
                result.Status = ClassChangeItemStatus.MutationFailed;
                result.Detail = "source item consume failed";
                return false;
            }

            mutation.UsableCountState = usableCountState;
            result.SourceMutation = mutation;
            AddUnique(result.MainRefreshSlots, request.ItemSlotIndex);
            return true;
        }

        private static void ApplyLifecycleFailure(
            ClassChangeItemResult result,
            InventoryItemLifecycleUsePlan lifecyclePlan)
        {
            if (lifecyclePlan == null)
            {
                Fail(
                    result,
                    ClassChangeItemStatus.InvalidLifecycle,
                    "lifecycle plan is missing",
                    false);
                return;
            }

            if (lifecyclePlan.SourceExpiredDeleted)
            {
                result.Status = ClassChangeItemStatus.SourceExpired;
                result.Detail = lifecyclePlan.Detail;
                result.SourceMutation = lifecyclePlan.SourceMutation;
                AddUnique(result.MainRefreshSlots, lifecyclePlan.SlotIndex);
                return;
            }

            switch (lifecyclePlan.Status)
            {
                case InventoryItemLifecycleStatus.CooltimeActive:
                    Fail(
                        result,
                        ClassChangeItemStatus.CooltimeActive,
                        lifecyclePlan.Detail,
                        false);
                    break;
                case InventoryItemLifecycleStatus.SourceChanged:
                    Fail(
                        result,
                        ClassChangeItemStatus.SourceChanged,
                        lifecyclePlan.Detail,
                        false);
                    break;
                case InventoryItemLifecycleStatus.SourceEmpty:
                    Fail(
                        result,
                        ClassChangeItemStatus.SourceEmpty,
                        lifecyclePlan.Detail,
                        false);
                    break;
                case InventoryItemLifecycleStatus.SourceMissing:
                    Fail(
                        result,
                        ClassChangeItemStatus.SourceMissing,
                        lifecyclePlan.Detail,
                        false);
                    break;
                case InventoryItemLifecycleStatus.SourceExpired:
                    Fail(
                        result,
                        ClassChangeItemStatus.SourceExpired,
                        lifecyclePlan.Detail,
                        false);
                    break;
                default:
                    Fail(
                        result,
                        ClassChangeItemStatus.InvalidLifecycle,
                        lifecyclePlan.Detail,
                        false);
                    break;
            }
        }

        private static bool TryLoadState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            out CharacterGrowthState state)
        {
            state = null;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT grow_type, level
FROM characters
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    state = new CharacterGrowthState
                    {
                        GrowType = reader.GetInt32(0),
                        Level = reader.GetInt32(1),
                    };
                    return true;
                }
            }
        }

        private static bool AllowsLevel(
            StackableItemFile stackable,
            int level)
        {
            if (stackable == null)
                return false;
            if (stackable.MinimumLevel >= 0 && level < stackable.MinimumLevel)
                return false;
            if (stackable.MaximumLevel >= 0 && level > stackable.MaximumLevel)
                return false;

            return true;
        }

        private static string NormalizeActionType(string raw)
        {
            var text = StackableItemProvider.NormalizeType(raw);
            if (text.Length >= 2
                && text[0] == '['
                && text[text.Length - 1] == ']')
            {
                return text.Substring(1, text.Length - 2).Trim();
            }

            return text.Trim();
        }

        private static int ResolveIconIndex(string icon)
        {
            if (string.IsNullOrWhiteSpace(icon))
                return -1;

            var matches = Regex.Matches(icon, @"(?<!\d)\d+(?!\d)");
            if (matches.Count == 0)
                return -1;

            return int.TryParse(
                matches[matches.Count - 1].Value,
                out var value)
                ? value
                : -1;
        }

        private static ClassChangeItemResult CreateRejectedResult(
            ClassChangeItemRequest request,
            ClassChangeItemStatus status,
            string detail)
        {
            return new ClassChangeItemResult
            {
                Request = request ?? new ClassChangeItemRequest(),
                Status = status,
                Detail = detail,
            };
        }

        private static bool Fail(
            ClassChangeItemResult result,
            ClassChangeItemStatus status,
            string detail,
            bool returnValue)
        {
            result.Status = status;
            result.Detail = detail;
            return returnValue;
        }

        private static void AddUnique(
            System.Collections.Generic.ICollection<short> slots,
            short slotIndex)
        {
            if (slots != null && !slots.Contains(slotIndex))
                slots.Add(slotIndex);
        }

        private sealed class CharacterGrowthState
        {
            public int GrowType { get; set; }

            public int Level { get; set; }
        }
    }
}
