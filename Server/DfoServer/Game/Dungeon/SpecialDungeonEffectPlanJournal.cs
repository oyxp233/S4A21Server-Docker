using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class SpecialDungeonEffectPlanItem
    {
        internal SpecialDungeonEffectPlanItem(
            int index,
            DungeonEffectId effectId,
            SpecialDungeonEffectIntent intent)
        {
            Index = index;
            EffectId = effectId;
            Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        }

        internal int Index { get; }
        internal DungeonEffectId EffectId { get; }
        internal SpecialDungeonEffectIntent Intent { get; }
    }

    internal sealed class SpecialDungeonEffectPlan
    {
        internal SpecialDungeonEffectPlan(
            string operation,
            DungeonEventEnvelope source,
            IReadOnlyList<SpecialDungeonEffectPlanItem> items)
        {
            Operation = operation
                ?? throw new ArgumentNullException(nameof(operation));
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Items = items ?? throw new ArgumentNullException(nameof(items));
        }

        internal string Operation { get; }
        internal DungeonEventEnvelope Source { get; }
        internal DungeonRunIdentity RunIdentity => Source.RunIdentity;
        internal IReadOnlyList<SpecialDungeonEffectPlanItem> Items { get; }

        internal bool IsComplete(DungeonEffectLedger ledger)
        {
            if (ledger == null)
                return false;

            foreach (var item in Items)
            {
                if (ledger.GetState(item.EffectId)
                    != DungeonEffectState.Committed)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal readonly struct SpecialDungeonEffectPlanRegistration
    {
        internal SpecialDungeonEffectPlanRegistration(
            SpecialDungeonEffectPlan plan,
            bool created,
            bool wasComplete)
        {
            Plan = plan;
            Created = created;
            WasComplete = wasComplete;
        }

        internal SpecialDungeonEffectPlan Plan { get; }
        internal bool Created { get; }
        internal bool WasComplete { get; }
        internal bool HasPlan => Plan != null;
    }

    // Per-run, in-process journal for ordered mechanism effects. The existing
    // DungeonEffectLedger remains the only effect state machine; this journal
    // only freezes the plan and preserves its deterministic order.
    internal sealed class SpecialDungeonEffectPlanJournal
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, SpecialDungeonEffectPlan> _plans =
            new Dictionary<string, SpecialDungeonEffectPlan>(
                StringComparer.Ordinal);
        private readonly List<SpecialDungeonEffectPlan> _orderedPlans =
            new List<SpecialDungeonEffectPlan>();

        internal bool TryGet(
            string operation,
            out SpecialDungeonEffectPlan plan)
        {
            if (string.IsNullOrWhiteSpace(operation))
            {
                plan = null;
                return false;
            }

            lock (_syncRoot)
                return _plans.TryGetValue(operation, out plan);
        }

        // The caller owns run.SyncRoot so the mechanism transition and this
        // registration form one short synchronous critical section.
        internal SpecialDungeonEffectPlanRegistration Register(
            DungeonRun run,
            DungeonEventEnvelope source,
            string operation,
            IReadOnlyList<SpecialDungeonEffectIntent> intents)
        {
            if (run == null)
                throw new ArgumentNullException(nameof(run));
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (!run.Matches(source.RunIdentity))
                return default;
            if (string.IsNullOrWhiteSpace(operation)
                || intents == null
                || intents.Count == 0)
            {
                return default;
            }

            lock (_syncRoot)
            {
                if (_plans.TryGetValue(operation, out var existing))
                {
                    return new SpecialDungeonEffectPlanRegistration(
                        existing,
                        created: false,
                        wasComplete: existing.IsComplete(run.Effects));
                }

                var scopeTarget = source.AffectedPlayerId
                    ?? source.SourcePlayerId;
                var scope = scopeTarget > 0
                    ? DungeonEffectScope.Player
                    : DungeonEffectScope.Instance;
                var target = scopeTarget > 0
                    ? scopeTarget
                    : run.RunId;
                var items = new List<SpecialDungeonEffectPlanItem>(
                    intents.Count);
                for (var index = 0; index < intents.Count; index++)
                {
                    var intent = intents[index];
                    if (intent == null)
                        continue;

                    var frozen = intent.Freeze();
                    var effectId = new DungeonEffectId(
                        source.SourceEventId,
                        $"special-dungeon/{operation}/{index:D2}/" +
                            frozen.Kind,
                        scope,
                        target);
                    items.Add(new SpecialDungeonEffectPlanItem(
                        index,
                        effectId,
                        frozen));
                }

                if (items.Count == 0)
                    return default;

                var plan = new SpecialDungeonEffectPlan(
                    operation,
                    source,
                    items.AsReadOnly());
                _plans.Add(operation, plan);
                _orderedPlans.Add(plan);
                return new SpecialDungeonEffectPlanRegistration(
                    plan,
                    created: true,
                    wasComplete: false);
            }
        }

        internal IReadOnlyList<SpecialDungeonEffectPlan> GetRecoverable(
            DungeonEffectLedger ledger)
        {
            if (ledger == null)
                return Array.Empty<SpecialDungeonEffectPlan>();

            var result = new List<SpecialDungeonEffectPlan>();
            lock (_syncRoot)
            {
                foreach (var plan in _orderedPlans)
                {
                    if (!plan.IsComplete(ledger))
                        result.Add(plan);
                }
            }

            return result.AsReadOnly();
        }
    }
}
