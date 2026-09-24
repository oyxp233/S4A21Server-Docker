using System;
using System.Collections.Generic;
using System.Linq;
using PvfLib;

namespace DfoServer.Game.Raid
{
    internal sealed class AntonRaidTimerConfiguration
    {
        private const uint MaximumSchedulableSeconds = int.MaxValue / 1000;
        private static readonly uint[] PhaseLimitFallbacks = { 2400, 2400 };
        private static readonly IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> ActiveFallbacks =
            new Dictionary<(uint Phase, uint Dungeon), uint>
            {
                [(0, 211)] = 480,
                [(0, 212)] = 300,
                [(0, 214)] = 300,
                [(0, 216)] = 360,
            };
        private static readonly IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> RecoveryFallbacks =
            new Dictionary<(uint Phase, uint Dungeon), uint>
            {
                [(0, 211)] = 300,
                [(0, 212)] = 150,
                [(0, 214)] = 150,
                [(0, 216)] = 150,
                [(1, 221)] = 240,
                [(1, 222)] = 240,
                [(1, 223)] = 240,
                [(1, 224)] = 240,
            };
        private static readonly IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> PassiveFallbacks =
            new Dictionary<(uint Phase, uint Dungeon), uint>
            {
                [(0, 211)] = 240,
                [(0, 216)] = 120,
            };
        private static readonly IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> ReservedOpenFallbacks =
            new Dictionary<(uint Phase, uint Dungeon), uint>
            {
                [(0, 216)] = 20,
                [(0, 212)] = 150,
                [(0, 214)] = 150,
                [(1, 221)] = 240,
                [(1, 222)] = 240,
                [(1, 223)] = 240,
                [(1, 224)] = 240,
            };
        private static readonly IReadOnlyDictionary<(uint TimerType, uint Dungeon), uint> InitialEffectFallbacks =
            BuildHatcheryEffectFallbacks(120, 120);
        private static readonly IReadOnlyDictionary<(uint TimerType, uint Dungeon), uint> RepeatEffectFallbacks =
            BuildHatcheryEffectFallbacks(45, 50);

        private readonly uint[] _phaseLimits;
        private readonly IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> _activeSeconds;
        private readonly IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> _recoverySeconds;
        private readonly IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> _passiveSeconds;
        private readonly IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> _reservedOpenSeconds;
        private readonly IReadOnlyDictionary<(uint TimerType, uint Dungeon), uint> _initialEffectSeconds;
        private readonly IReadOnlyDictionary<(uint TimerType, uint Dungeon), uint> _repeatEffectSeconds;
        private readonly uint _hatcheryOpenSeconds;

        private AntonRaidTimerConfiguration(
            uint[] phaseLimits,
            IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> activeSeconds,
            IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> recoverySeconds,
            IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> passiveSeconds,
            IReadOnlyDictionary<(uint Phase, uint Dungeon), uint> reservedOpenSeconds,
            IReadOnlyDictionary<(uint TimerType, uint Dungeon), uint> initialEffectSeconds,
            IReadOnlyDictionary<(uint TimerType, uint Dungeon), uint> repeatEffectSeconds,
            uint hatcheryOpenSeconds)
        {
            _phaseLimits = phaseLimits;
            _activeSeconds = activeSeconds;
            _recoverySeconds = recoverySeconds;
            _passiveSeconds = passiveSeconds;
            _reservedOpenSeconds = reservedOpenSeconds;
            _initialEffectSeconds = initialEffectSeconds;
            _repeatEffectSeconds = repeatEffectSeconds;
            _hatcheryOpenSeconds = hatcheryOpenSeconds;
        }

        internal static AntonRaidTimerConfiguration Create(RaidEtcFile source, Action<string> warn)
        {
            warn ??= _ => { };
            var warningKeys = new HashSet<string>(StringComparer.Ordinal);

            void WarnOnce(string key, string message)
            {
                if (warningKeys.Add(key))
                    warn(message);
            }

            Resolution ResolveTimer(
                uint phase,
                uint timerType,
                uint dungeon,
                RaidEtcTriggerKind? triggerKind,
                uint fallback,
                string semantic)
            {
                IEnumerable<RaidTimerDirective> candidates = source?
                    .GetPhase(checked((int)phase))?
                    .TimerDirectives
                    .Where(entry => entry.TimerType == timerType && entry.DungeonId == dungeon)
                    ?? Enumerable.Empty<RaidTimerDirective>();
                if (triggerKind.HasValue)
                    candidates = candidates.Where(entry => entry.Trigger.Kind == triggerKind.Value);

                int[] seconds = candidates.Select(entry => entry.Seconds).ToArray();
                string key = $"{semantic}:phase={phase}:type={timerType}:dungeon={dungeon}";
                if (seconds.Length == 0)
                {
                    WarnOnce(key, $"{semantic} phase={phase} type={timerType} dungeon={dungeon} is missing; using fallback {fallback}s.");
                    return new Resolution(fallback, false);
                }

                if (seconds.Any(value => value <= 0 || value > MaximumSchedulableSeconds))
                {
                    WarnOnce(key, $"{semantic} phase={phase} type={timerType} dungeon={dungeon} contains an unschedulable duration; using fallback {fallback}s.");
                    return new Resolution(fallback, false);
                }

                int[] distinct = seconds.Distinct().ToArray();
                if (distinct.Length != 1)
                {
                    WarnOnce(key, $"{semantic} phase={phase} type={timerType} dungeon={dungeon} conflicts ({string.Join(",", distinct)}); using fallback {fallback}s.");
                    return new Resolution(fallback, false);
                }

                return new Resolution(checked((uint)distinct[0]), true);
            }

            Resolution ResolveReserve(uint phase, uint dungeon, uint fallback)
            {
                int[] seconds = source?
                    .GetPhase(checked((int)phase))?
                    .ReservedDungeonStates
                    .Where(entry => entry.DungeonId == dungeon
                        && string.Equals(entry.State, "open", StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Seconds)
                    .ToArray()
                    ?? Array.Empty<int>();
                string key = $"reserve-open:phase={phase}:dungeon={dungeon}";
                if (seconds.Length == 0)
                {
                    WarnOnce(key, $"reserve-open phase={phase} dungeon={dungeon} is missing; using fallback {fallback}s.");
                    return new Resolution(fallback, false);
                }

                if (seconds.Any(value => value <= 0 || value > MaximumSchedulableSeconds))
                {
                    WarnOnce(key, $"reserve-open phase={phase} dungeon={dungeon} contains an unschedulable duration; using fallback {fallback}s.");
                    return new Resolution(fallback, false);
                }

                int[] distinct = seconds.Distinct().ToArray();
                if (distinct.Length != 1)
                {
                    WarnOnce(key, $"reserve-open phase={phase} dungeon={dungeon} conflicts ({string.Join(",", distinct)}); using fallback {fallback}s.");
                    return new Resolution(fallback, false);
                }

                return new Resolution(checked((uint)distinct[0]), true);
            }

            var phaseLimits = new uint[PhaseLimitFallbacks.Length];
            for (var index = 0; index < phaseLimits.Length; index++)
            {
                int configured = source != null && source.PhaseTimeOverSeconds.Count > index
                    ? source.PhaseTimeOverSeconds[index]
                    : 0;
                if (configured > 0 && configured <= MaximumSchedulableSeconds)
                {
                    phaseLimits[index] = checked((uint)configured);
                    continue;
                }

                phaseLimits[index] = PhaseLimitFallbacks[index];
                WarnOnce(
                    $"phase-limit:{index}",
                    $"phase-limit phase={index} is missing or unschedulable; using fallback {phaseLimits[index]}s.");
            }

            var active = ActiveFallbacks.ToDictionary(
                entry => entry.Key,
                entry => ResolveTimer(entry.Key.Phase, 1, entry.Key.Dungeon, null, entry.Value, "active").Value);
            var passive = PassiveFallbacks.ToDictionary(
                entry => entry.Key,
                entry => ResolveTimer(entry.Key.Phase, 3, entry.Key.Dungeon, null, entry.Value, "passive").Value);

            var recoveryResults = RecoveryFallbacks.ToDictionary(
                entry => entry.Key,
                entry => ResolveTimer(entry.Key.Phase, 2, entry.Key.Dungeon, null, entry.Value, "recovery"));
            var reserveResults = ReservedOpenFallbacks.ToDictionary(
                entry => entry.Key,
                entry => ResolveReserve(entry.Key.Phase, entry.Key.Dungeon, entry.Value));

            foreach (var pair in new[]
            {
                (Phase: 0u, Dungeon: 212u),
                (Phase: 0u, Dungeon: 214u),
                (Phase: 1u, Dungeon: 221u),
                (Phase: 1u, Dungeon: 222u),
                (Phase: 1u, Dungeon: 223u),
                (Phase: 1u, Dungeon: 224u),
            })
            {
                Resolution recovery = recoveryResults[pair];
                Resolution reserve = reserveResults[pair];
                if (recovery.IsConfigured && reserve.IsConfigured && recovery.Value == reserve.Value)
                    continue;

                uint fallback = RecoveryFallbacks[pair];
                recoveryResults[pair] = new Resolution(fallback, false);
                reserveResults[pair] = new Resolution(ReservedOpenFallbacks[pair], false);
                if (recovery.IsConfigured && reserve.IsConfigured)
                {
                    WarnOnce(
                        $"recovery-reserve:phase={pair.Phase}:dungeon={pair.Dungeon}",
                        $"phase={pair.Phase} dungeon={pair.Dungeon} recovery/reserve mismatch ({recovery.Value}/{reserve.Value}); using fallbacks.");
                }
            }

            var initialEffects = InitialEffectFallbacks.ToDictionary(
                entry => entry.Key,
                entry => ResolveTimer(
                    1,
                    entry.Key.TimerType,
                    entry.Key.Dungeon,
                    RaidEtcTriggerKind.DungeonOpened,
                    entry.Value,
                    "hatchery-initial").Value);
            var repeatEffects = RepeatEffectFallbacks.ToDictionary(
                entry => entry.Key,
                entry => ResolveTimer(
                    1,
                    entry.Key.TimerType,
                    entry.Key.Dungeon,
                    RaidEtcTriggerKind.TimerEnded,
                    entry.Value,
                    "hatchery-repeat").Value);
            uint hatcheryOpen = ResolveTimer(
                1,
                3,
                219,
                RaidEtcTriggerKind.PhaseInitialization,
                180,
                "hatchery-open").Value;

            return new AntonRaidTimerConfiguration(
                phaseLimits,
                active,
                recoveryResults.ToDictionary(entry => entry.Key, entry => entry.Value.Value),
                passive,
                reserveResults.ToDictionary(entry => entry.Key, entry => entry.Value.Value),
                initialEffects,
                repeatEffects,
                hatcheryOpen);
        }

        internal uint GetPhaseLimitSeconds(uint phaseIndex)
        {
            return phaseIndex < _phaseLimits.Length
                ? _phaseLimits[phaseIndex]
                : PhaseLimitFallbacks[^1];
        }

        internal uint GetDungeonActiveSeconds(uint phaseIndex, uint dungeonId)
        {
            return _activeSeconds.TryGetValue((phaseIndex, dungeonId), out uint seconds) ? seconds : 0;
        }

        internal uint GetDungeonRecoverySeconds(uint phaseIndex, uint dungeonId)
        {
            return _recoverySeconds.TryGetValue((phaseIndex, dungeonId), out uint seconds) ? seconds : 0;
        }

        internal uint GetDungeonPassiveSeconds(uint phaseIndex, uint dungeonId)
        {
            return _passiveSeconds.TryGetValue((phaseIndex, dungeonId), out uint seconds) ? seconds : 0;
        }

        internal uint GetHatcheryOpenSeconds()
        {
            return _hatcheryOpenSeconds;
        }

        internal uint GetHatcheryEffectInitialSeconds(uint timerType, uint dungeonId)
        {
            return _initialEffectSeconds.TryGetValue((timerType, dungeonId), out uint seconds) ? seconds : 0;
        }

        internal uint GetHatcheryEffectRepeatSeconds(uint timerType, uint dungeonId)
        {
            return _repeatEffectSeconds.TryGetValue((timerType, dungeonId), out uint seconds) ? seconds : 0;
        }

        internal uint GetReservedOpenSeconds(uint phaseIndex, uint dungeonId)
        {
            return _reservedOpenSeconds.TryGetValue((phaseIndex, dungeonId), out uint seconds) ? seconds : 0;
        }

        private static IReadOnlyDictionary<(uint TimerType, uint Dungeon), uint> BuildHatcheryEffectFallbacks(
            uint typeOneSeconds,
            uint typeThreeSeconds)
        {
            var result = new Dictionary<(uint TimerType, uint Dungeon), uint>();
            for (uint dungeon = 221; dungeon <= 224; dungeon++)
            {
                result[(1, dungeon)] = typeOneSeconds;
                result[(3, dungeon)] = typeThreeSeconds;
            }

            return result;
        }

        private readonly record struct Resolution(uint Value, bool IsConfigured);
    }
}
