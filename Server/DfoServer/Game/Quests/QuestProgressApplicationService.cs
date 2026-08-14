using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestProgressApplicationService
    {
        private const int MaxCasAttempts = 4;
        private readonly string _connectionString;

        internal QuestProgressApplicationService(string connectionString)
        {
            _connectionString = connectionString
                ?? throw new ArgumentNullException(nameof(connectionString));
        }

        internal QuestProgressApplicationResult Apply(
            QuestProgressApplicationRequest request,
            Func<ushort, int, int, bool> clearMapMatcher = null)
        {
            var validation = Validate(request);
            if (validation != null)
                return validation;

            for (var attempt = 0; attempt < MaxCasAttempts; attempt++)
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        var result = ApplyInTransactionCore(
                            connection,
                            transaction,
                            request,
                            clearMapMatcher);
                        if (result.RetryRequired)
                        {
                            transaction.Rollback();
                            continue;
                        }
                        if (!result.Success)
                        {
                            transaction.Rollback();
                            return result;
                        }

                        transaction.Commit();
                        return result;
                    }
                }
            }

            return Failed("quest progress CAS retry exhausted");
        }

        internal QuestProgressApplicationResult ApplyInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            QuestProgressApplicationRequest request,
            Func<ushort, int, int, bool> clearMapMatcher = null)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            var validation = Validate(request);
            return validation ?? ApplyInTransactionCore(
                connection,
                transaction,
                request,
                clearMapMatcher);
        }

        private static QuestProgressApplicationResult ApplyInTransactionCore(
            SqliteConnection connection,
            SqliteTransaction transaction,
            QuestProgressApplicationRequest request,
            Func<ushort, int, int, bool> clearMapMatcher)
        {
            var active = QuestRepository.LoadActiveQuests(
                connection,
                transaction,
                request.CharacterId);
            var result = new QuestProgressApplicationResult();
            var eligible = request.EligibleQuestIds == null
                ? null
                : new HashSet<ushort>(request.EligibleQuestIds);
            var eligibleActivations = request.EligibleQuestActivations;
            var foundClientQuest = false;
            var clientActivationChanged = false;

            foreach (var quest in active)
            {
                if (quest == null)
                    continue;
                if (eligibleActivations != null)
                {
                    if (!eligibleActivations.TryGetValue(
                            quest.QuestId,
                            out var expectedActivation)
                        || !expectedActivation.Equals(quest.ActivationId))
                    {
                        if (request.Operation == QuestProgressOperation.ClientTrigger
                            && quest.QuestId == request.QuestId)
                        {
                            clientActivationChanged = true;
                        }
                        continue;
                    }
                }
                else if (eligible != null && !eligible.Contains(quest.QuestId))
                {
                    continue;
                }
                if (request.Operation == QuestProgressOperation.ClientTrigger
                    && quest.QuestId != request.QuestId)
                {
                    continue;
                }

                if (request.Operation == QuestProgressOperation.ClientTrigger)
                    foundClientQuest = true;

                if (request.SourceEventId != Guid.Empty
                    && !quest.ActivationId.IsValid)
                {
                    return Failed(
                        $"quest={quest.QuestId} has invalid activation identity");
                }

                QuestProgressEvaluation evaluation;
                try
                {
                    evaluation = QuestObjectiveEvaluator.Evaluate(
                        quest,
                        request,
                        clearMapMatcher);
                }
                catch (Exception ex)
                {
                    return Failed(
                        $"objective evaluation failed quest={quest.QuestId}: {ex.Message}");
                }

                if (!evaluation.Matched)
                    continue;
                result.MatchedObjective = true;
                if (request.SourceEventId != Guid.Empty
                    && !QuestRepository.TryInsertProgressEvent(
                        connection,
                        transaction,
                        request.CharacterId,
                        quest.ActivationId,
                        request.SourceEventId,
                        request.EventKind))
                {
                    result.DuplicateEvent = true;
                    continue;
                }
                if (evaluation.Trigger.PackedValue == quest.TriggerValue)
                {
                    result.AddChanges(evaluation.Changes);
                    continue;
                }

                if (!QuestRepository.TryUpdateTriggerValueCas(
                        connection,
                        transaction,
                        request.CharacterId,
                        quest.QuestId,
                        quest.ActivationId,
                        quest.Version,
                        quest.TriggerValue,
                        evaluation.Trigger.PackedValue))
                {
                    return Retry(
                        $"quest progress CAS conflict quest={quest.QuestId}");
                }

                result.AddChanges(evaluation.Changes);
            }

            if (request.Operation == QuestProgressOperation.ClientTrigger
                && !foundClientQuest)
            {
                result.QuestNotActive = true;
                result.ActivationChanged = clientActivationChanged;
            }
            if (request.Operation == QuestProgressOperation.ClientTrigger
                && (!request.CommandOwner.HasValue
                    || !request.CommandOwner.Value.IsCurrentInventoryOwner()))
            {
                return Failed(
                    "client quest trigger session owner changed before commit");
            }

            result.Success = true;
            return result;
        }

        private static QuestProgressApplicationResult Validate(
            QuestProgressApplicationRequest request)
        {
            if (request == null || request.CharacterId <= 0)
                return Failed("invalid quest progress request");
            if (request.Operation == QuestProgressOperation.ClientTrigger
                && (!request.CommandOwner.HasValue
                    || !request.CommandOwner.Value.IsCurrentInventoryOwner()))
            {
                return Failed("client quest trigger has no current session owner");
            }
            return null;
        }

        private static QuestProgressApplicationResult Retry(string error)
            => new QuestProgressApplicationResult
            {
                Success = false,
                RetryRequired = true,
                Error = error ?? string.Empty,
            };

        private static QuestProgressApplicationResult Failed(string error)
            => new QuestProgressApplicationResult
            {
                Success = false,
                Error = error ?? string.Empty,
            };
    }

}
