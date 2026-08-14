using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Dungeon
{
    internal sealed class SeizeMoneyClearRewardPlan
    {
        internal SeizeMoneyClearRewardPlan(
            int hitCount,
            int remainingUnits,
            int count,
            int gauge)
        {
            HitCount = hitCount;
            RemainingUnits = remainingUnits;
            Count = count;
            Gauge = gauge;
        }

        internal int HitCount { get; }
        internal int RemainingUnits { get; }
        internal int Count { get; }
        internal int Gauge { get; }
    }

    internal sealed class SpecialDungeonRuntime
    {
        private readonly HashSet<int> _sealForestBuffMonsterCodes =
            new HashSet<int>();
        private readonly List<int> _sealForestBuffIds = new List<int>();
        private readonly List<int> _seaChaseAppliedBuffIds = new List<int>();
        private readonly List<int> _timeCrackBuffIds = new List<int>();
        private readonly Dictionary<int, int> _gentInfiltrateTowerRequired =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> _gentInfiltrateTowerDestroyed =
            new Dictionary<int, int>();
        private bool _seizeMoneyClearRewardGenerated;
        private int? _seizeMoneyAuthoritativeHitCount;

        internal SpecialDungeonRuntime(SpecialDungeonDefinition definition)
        {
            Definition = definition
                ?? throw new ArgumentNullException(nameof(definition));

            if (Kind == SpecialDungeonKind.SeizeMoney)
                SeizeMoneyGauge = Definition.SeizeMoney.GaugeMax;
            if (Kind == SpecialDungeonKind.GentInfiltrate)
                GentInfiltrateTimerSeconds = Definition.TimerSeconds;
        }

        internal SpecialDungeonDefinition Definition { get; }
        internal int DungeonId => Definition.DungeonId;
        internal SpecialDungeonKind Kind => Definition.Kind;
        internal int SeizeMoneyGauge { get; private set; }
        internal ushort SeizeMoneyBossSeq { get; private set; }
        internal bool? SeaChaseMiniGameSucceeded { get; private set; }
        internal IReadOnlyList<int> SeaChaseAppliedBuffIds =>
            _seaChaseAppliedBuffIds;
        internal int TimeCrackGauge { get; private set; }
        internal IReadOnlyList<int> TimeCrackBuffIds => _timeCrackBuffIds;
        internal IReadOnlyList<int> SealForestBuffIds => _sealForestBuffIds;
        internal IReadOnlyDictionary<int, int> GentInfiltrateTowerRequired =>
            _gentInfiltrateTowerRequired;
        internal int GentInfiltrateTimerSeconds { get; private set; }
        internal bool GentInfiltrateConditionComplete { get; private set; }
        internal bool GentInfiltrateStrongWarlord { get; private set; }
        internal bool GentInfiltrateTimedOut { get; private set; }
        internal string GentInfiltrateCompletionSource { get; private set; }
            = string.Empty;

        internal SpecialDungeonRuntime CloneFresh()
        {
            var clone = new SpecialDungeonRuntime(Definition)
            {
                GentInfiltrateTimerSeconds = GentInfiltrateTimerSeconds,
            };
            foreach (var pair in _gentInfiltrateTowerRequired)
            {
                clone._gentInfiltrateTowerRequired[pair.Key] = pair.Value;
                clone._gentInfiltrateTowerDestroyed[pair.Key] = 0;
            }

            return clone;
        }

        internal void NoteSeizeMoneyBossSeq(ushort bossSeq)
        {
            if (Kind == SpecialDungeonKind.SeizeMoney && bossSeq != 0)
                SeizeMoneyBossSeq = bossSeq;
        }

        internal bool ApplyAuthoritativeSeizeMoneyHits(int hitCount)
        {
            if (Kind != SpecialDungeonKind.SeizeMoney || hitCount <= 0)
                return false;

            var definition = Definition.SeizeMoney;
            var unitValue = Math.Max(1, definition.GaugeSubOnDamage);
            var maxUnits = Math.Max(1, definition.GaugeMax / unitValue);
            var current = _seizeMoneyAuthoritativeHitCount ?? 0;
            var next = Math.Min(maxUnits, current + hitCount);
            _seizeMoneyAuthoritativeHitCount = next;
            SeizeMoneyGauge = Math.Max(
                0,
                definition.GaugeMax - next * unitValue);
            return true;
        }

        internal bool NoteSeaChaseMiniGameResult(bool succeeded)
        {
            if (Kind != SpecialDungeonKind.SeaChase)
                return false;

            SeaChaseMiniGameSucceeded = succeeded;
            return true;
        }

        internal bool NoteSeaChaseBuffsApplied(IReadOnlyList<int> buffIds)
        {
            if (Kind != SpecialDungeonKind.SeaChase
                || buffIds == null
                || buffIds.Count == 0)
            {
                return false;
            }

            for (var index = 0; index < buffIds.Count; index++)
            {
                var buffId = buffIds[index];
                if (buffId > 0 && !_seaChaseAppliedBuffIds.Contains(buffId))
                    _seaChaseAppliedBuffIds.Add(buffId);
            }

            return _seaChaseAppliedBuffIds.Count > 0;
        }

        internal bool TryConsumeSeaChaseAppliedBuffIds(out List<int> buffIds)
        {
            buffIds = new List<int>();
            if (Kind != SpecialDungeonKind.SeaChase
                || _seaChaseAppliedBuffIds.Count == 0)
            {
                return false;
            }

            buffIds.AddRange(_seaChaseAppliedBuffIds);
            _seaChaseAppliedBuffIds.Clear();
            return true;
        }

        internal bool IsTimeCrackInvincibleMonster(int monsterCode)
            => Kind == SpecialDungeonKind.TimeCrack
                && Definition.TimeCrack.InvincibleMonsterCodes.Contains(
                    monsterCode);

        internal bool TryAddTimeCrackGauge(
            int monsterCode,
            bool isChampion,
            out int previous,
            out int current,
            out int delta,
            out bool filled)
        {
            previous = TimeCrackGauge;
            current = TimeCrackGauge;
            delta = 0;
            filled = false;

            if (Kind != SpecialDungeonKind.TimeCrack
                || monsterCode <= 0
                || IsTimeCrackInvincibleMonster(monsterCode))
            {
                return false;
            }

            var definition = Definition.TimeCrack;
            var max = Math.Max(1, definition.SandGaugeMax);
            delta = Math.Max(
                1,
                isChampion
                    ? definition.SandGaugeGainOnChampion
                    : definition.SandGaugeGainOnKill);
            current = Math.Min(max, previous + delta);
            TimeCrackGauge = current;
            filled = current >= max;
            return true;
        }

        internal void ResetTimeCrackGauge()
        {
            if (Kind == SpecialDungeonKind.TimeCrack)
                TimeCrackGauge = 0;
        }

        internal bool NoteTimeCrackBuffApplied(int buffId)
        {
            if (Kind != SpecialDungeonKind.TimeCrack || buffId <= 0)
                return false;

            if (!_timeCrackBuffIds.Contains(buffId))
                _timeCrackBuffIds.Add(buffId);
            return true;
        }

        internal bool TryConsumeTimeCrackBuffIds(out List<int> buffIds)
        {
            buffIds = new List<int>();
            if (Kind != SpecialDungeonKind.TimeCrack
                || _timeCrackBuffIds.Count == 0)
            {
                return false;
            }

            buffIds.AddRange(_timeCrackBuffIds);
            _timeCrackBuffIds.Clear();
            return true;
        }

        internal bool TryReserveAuthoritativeSeizeMoneyClearReward(
            int maxDropCount,
            out SeizeMoneyClearRewardPlan plan,
            out string failureReason)
        {
            plan = null;
            failureReason = string.Empty;
            if (Kind != SpecialDungeonKind.SeizeMoney)
            {
                failureReason = "not_seize_money";
                return false;
            }
            if (_seizeMoneyClearRewardGenerated)
            {
                failureReason = "already_generated";
                return false;
            }
            if (!_seizeMoneyAuthoritativeHitCount.HasValue)
            {
                failureReason = "no_authoritative_hit_fact";
                return false;
            }

            _seizeMoneyClearRewardGenerated = true;
            var definition = Definition.SeizeMoney;
            var unitValue = Math.Max(1, definition.GaugeSubOnDamage);
            var maxUnits = Math.Max(1, definition.GaugeMax / unitValue);
            var hitCount = Math.Max(
                0,
                Math.Min(maxUnits, _seizeMoneyAuthoritativeHitCount.Value));
            var remainingGoldUnits = Math.Max(0, maxUnits - hitCount);

            var gauge = Math.Min(
                definition.GaugeMax,
                remainingGoldUnits * unitValue);
            SeizeMoneyGauge = gauge;

            maxDropCount = Math.Max(0, maxDropCount);
            var count = (int)Math.Floor(
                (remainingGoldUnits * maxDropCount / (double)maxUnits)
                + 0.5d);
            if (count > maxDropCount)
                count = maxDropCount;

            plan = new SeizeMoneyClearRewardPlan(
                hitCount,
                remainingGoldUnits,
                count,
                gauge);
            return true;
        }

        internal bool TryMarkSealForestBuffMonster(
            int monsterCode,
            out SealForestBuffEntry entry)
        {
            entry = null;
            if (Kind != SpecialDungeonKind.SealForest)
                return false;

            if (!Definition.SealForest.BuffsByMonsterCode.TryGetValue(
                    monsterCode,
                    out entry))
            {
                return false;
            }

            if (!_sealForestBuffMonsterCodes.Add(monsterCode))
                return false;

            if (!_sealForestBuffIds.Contains(entry.BuffId))
                _sealForestBuffIds.Add(entry.BuffId);

            return true;
        }

        internal bool TryConsumeSealForestBuffIds(out List<int> buffIds)
        {
            buffIds = new List<int>();
            if (Kind != SpecialDungeonKind.SealForest
                || _sealForestBuffIds.Count == 0)
            {
                return false;
            }

            buffIds.AddRange(_sealForestBuffIds);
            _sealForestBuffIds.Clear();
            _sealForestBuffMonsterCodes.Clear();
            return true;
        }

        internal void ConfigureGentInfiltrateBossEntrance(
            IReadOnlyDictionary<int, int> towerRequirements,
            int timerSeconds)
        {
            if (Kind != SpecialDungeonKind.GentInfiltrate)
                return;

            _gentInfiltrateTowerRequired.Clear();
            _gentInfiltrateTowerDestroyed.Clear();
            GentInfiltrateConditionComplete = false;
            GentInfiltrateStrongWarlord = false;
            GentInfiltrateTimedOut = false;
            GentInfiltrateCompletionSource = string.Empty;
            GentInfiltrateTimerSeconds = timerSeconds > 0
                ? timerSeconds
                : GentInfiltrateTimerSeconds;

            if (towerRequirements == null)
                return;

            foreach (var pair in towerRequirements)
            {
                if (pair.Key <= 0 || pair.Value <= 0)
                    continue;

                _gentInfiltrateTowerRequired[pair.Key] = pair.Value;
                _gentInfiltrateTowerDestroyed[pair.Key] = 0;
            }
        }

        internal bool TryMarkGentInfiltrateTowerDestroyed(
            int monsterCode,
            out int destroyed,
            out int required,
            out int totalDestroyed,
            out int totalRequired,
            out bool completed)
        {
            destroyed = 0;
            required = 0;
            totalDestroyed = 0;
            totalRequired = 0;
            completed = false;

            if (Kind != SpecialDungeonKind.GentInfiltrate
                || !_gentInfiltrateTowerRequired.TryGetValue(
                    monsterCode,
                    out required))
            {
                return false;
            }

            if (!_gentInfiltrateTowerDestroyed.TryGetValue(
                    monsterCode,
                    out destroyed))
            {
                destroyed = 0;
            }

            if (destroyed < required)
            {
                destroyed++;
                _gentInfiltrateTowerDestroyed[monsterCode] = destroyed;
            }

            ComputeGentInfiltrateProgress(
                out totalDestroyed,
                out totalRequired);
            completed = TryCompleteGentInfiltrate(
                "tower",
                strongWarlord: !GentInfiltrateTimedOut);
            return true;
        }

        internal bool TryCompleteGentInfiltrateByTimer(
            out int totalDestroyed,
            out int totalRequired)
        {
            ComputeGentInfiltrateProgress(
                out totalDestroyed,
                out totalRequired);
            if (Kind == SpecialDungeonKind.GentInfiltrate
                && !GentInfiltrateConditionComplete)
            {
                GentInfiltrateTimedOut = true;
            }

            return false;
        }

        private bool TryCompleteGentInfiltrate(
            string source,
            bool strongWarlord)
        {
            if (Kind != SpecialDungeonKind.GentInfiltrate
                || GentInfiltrateConditionComplete)
            {
                return false;
            }

            ComputeGentInfiltrateProgress(
                out var totalDestroyed,
                out var totalRequired);
            if (totalRequired <= 0 || totalDestroyed < totalRequired)
                return false;

            GentInfiltrateConditionComplete = true;
            GentInfiltrateStrongWarlord = strongWarlord;
            GentInfiltrateCompletionSource = source ?? string.Empty;
            return true;
        }

        private void ComputeGentInfiltrateProgress(
            out int totalDestroyed,
            out int totalRequired)
        {
            totalDestroyed = 0;
            totalRequired = 0;
            foreach (var pair in _gentInfiltrateTowerRequired)
            {
                totalRequired += pair.Value;
                _gentInfiltrateTowerDestroyed.TryGetValue(
                    pair.Key,
                    out var value);
                totalDestroyed += Math.Min(value, pair.Value);
            }
        }
    }
}
