using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonRunEndCleanupOperation
    {
        internal DungeonRunEndCleanupOperation(
            string kind,
            Func<Task> execute)
        {
            if (string.IsNullOrWhiteSpace(kind))
                throw new ArgumentException("A cleanup operation kind is required.", nameof(kind));

            Kind = kind;
            Execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        internal string Kind { get; }
        internal Func<Task> Execute { get; }
    }

    internal sealed class DungeonRunEndCleanupSummary
    {
        private readonly List<string> _failedOperations = new List<string>();

        internal int CommittedCount { get; set; }
        internal int SkippedCommittedCount { get; set; }
        internal IReadOnlyList<string> FailedOperations => _failedOperations;
        internal bool IsComplete => _failedOperations.Count == 0;

        internal void AddFailure(string kind)
        {
            if (!string.IsNullOrWhiteSpace(kind))
                _failedOperations.Add(kind);
        }
    }

    internal static class DungeonRunEndCleanupExecutor
    {
        private const string EffectKindPrefix = "end-run-cleanup:";

        internal static async Task<DungeonRunEndCleanupSummary> ExecuteAsync(
            DungeonRun run,
            string source,
            IReadOnlyCollection<DungeonRunEndCleanupOperation> operations)
        {
            if (run == null)
                throw new ArgumentNullException(nameof(run));

            var summary = new DungeonRunEndCleanupSummary();
            run.TryBeginEnding();
            try
            {
                if (operations == null)
                    return summary;

                foreach (var operation in operations)
                {
                    if (operation == null)
                        continue;

                    var effectId = CreateEffectId(run, operation.Kind);
                    if (!run.Effects.TryReserve(effectId, out var reservation))
                    {
                        if (run.Effects.GetState(effectId)
                            == DungeonEffectState.Committed)
                        {
                            summary.SkippedCommittedCount++;
                        }
                        else
                        {
                            summary.AddFailure(operation.Kind);
                        }
                        continue;
                    }

                    try
                    {
                        await operation.Execute();
                        if (!run.Effects.TryCommit(reservation))
                        {
                            run.Effects.TryFail(reservation);
                            summary.AddFailure(operation.Kind);
                            continue;
                        }

                        summary.CommittedCount++;
                    }
                    catch (Exception ex)
                    {
                        run.Effects.TryFail(reservation);
                        summary.AddFailure(operation.Kind);
                        FileLogger.Log(
                            $"[DungeonRunEndCleanup] operation failed " +
                            $"source={source ?? string.Empty} " +
                            $"instance={run.PartyDungeonInstanceId} " +
                            $"run={run.RunId}/{run.RunGeneration} " +
                            $"operation={operation.Kind}: {ex.Message}");
                    }
                }
            }
            finally
            {
                run.TryMarkEnded();
            }

            return summary;
        }

        internal static DungeonEffectId CreateEffectId(
            DungeonRun run,
            string operationKind)
        {
            if (run == null)
                throw new ArgumentNullException(nameof(run));
            if (string.IsNullOrWhiteSpace(operationKind))
                throw new ArgumentException(
                    "A cleanup operation kind is required.",
                    nameof(operationKind));

            return new DungeonEffectId(
                run.GetEndSourceEventId(),
                EffectKindPrefix + operationKind,
                DungeonEffectScope.Player,
                run.RunId);
        }
    }
}
