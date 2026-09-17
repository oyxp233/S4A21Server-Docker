using System;
using System.Collections.Generic;
using DfoServer.Game.DailyReset;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Dungeon
{
    /// <summary>
    /// Serializes generation and durable cap-one accounting for each configured
    /// sequential group/monster pair. DailyResetService remains the persistent
    /// owner of the Beijing 06:00 game-day boundary.
    /// </summary>
    internal sealed class SequentialDungeonDailyLootGuard
    {
        private const int GateCount = 64;
        private readonly DailyResetService _dailyReset;
        private readonly Func<DateTime> _utcNow;
        private readonly object[] _gates = CreateGates();

        internal SequentialDungeonDailyLootGuard(
            DailyResetService dailyReset,
            Func<DateTime> utcNow = null)
        {
            _dailyReset = dailyReset
                ?? throw new ArgumentNullException(nameof(dailyReset));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal MonsterDropResult GenerateAndMark(
            int characterId,
            SequentialDungeonCapabilityResolution definitionResolution,
            SequentialDungeonDefinition definition,
            int monsterId,
            Func<MonsterDropResult> generate,
            Action<IReadOnlyList<DropInfo>> rollback,
            int dungeonId = 0,
            DungeonRunIdentity runIdentity = default,
            Guid sourceEventId = default)
        {
            if (generate == null)
                throw new ArgumentNullException(nameof(generate));
            if (rollback == null)
                throw new ArgumentNullException(nameof(rollback));

            if (definitionResolution
                    == SequentialDungeonCapabilityResolution.Absent
                && definition == null)
            {
                return generate();
            }

            if (definitionResolution
                    != SequentialDungeonCapabilityResolution.Resolved
                || definition == null)
            {
                LogFailure(
                    characterId,
                    definition?.GroupKey ?? 0,
                    monsterId,
                    "resolve-definition",
                    definitionResolution.ToString(),
                    dungeonId,
                    runIdentity,
                    sourceEventId);
                return EmptyResult();
            }

            if (!definition.ContainsMonster(monsterId))
                return generate();

            if (characterId <= 0)
            {
                LogFailure(
                    characterId,
                    definition.GroupKey,
                    monsterId,
                    "validate",
                    "invalid character id",
                    dungeonId,
                    runIdentity,
                    sourceEventId);
                return EmptyResult();
            }

            var utcNow = _utcNow();
            var counterKey = BuildCounterKey(
                definition.GroupKey,
                monsterId);
            lock (_gates[ResolveGateIndex(
                characterId,
                definition.GroupKey,
                monsterId)])
            {
                try
                {
                    if (!_dailyReset.TryGetCounter(
                            characterId,
                            counterKey,
                            DailyResetService.PeriodDay,
                            utcNow,
                            out var currentCount))
                    {
                        LogFailure(
                            characterId,
                            definition.GroupKey,
                            monsterId,
                            "read-counter",
                            "stale time anchor",
                            dungeonId,
                            runIdentity,
                            sourceEventId);
                        return EmptyResult();
                    }

                    if (currentCount > 0)
                    {
                        return EmptyResult();
                    }
                }
                catch (Exception ex)
                {
                    LogFailure(
                        characterId,
                        definition.GroupKey,
                        monsterId,
                        "read-counter",
                        ex.Message,
                        dungeonId,
                        runIdentity,
                        sourceEventId);
                    return EmptyResult();
                }

                MonsterDropResult generated;
                try
                {
                    generated = generate();
                }
                catch (Exception ex)
                {
                    LogFailure(
                        characterId,
                        definition.GroupKey,
                        monsterId,
                        "generate",
                        ex.Message,
                        dungeonId,
                        runIdentity,
                        sourceEventId);
                    throw;
                }

                var generatedDrops = (IReadOnlyList<DropInfo>)generated.Drops
                    ?? Array.Empty<DropInfo>();
                if (generatedDrops.Count == 0 && generated.GoldAmount <= 0)
                    return generated;

                try
                {
                    if (_dailyReset.TryIncrementCounter(
                            characterId,
                            counterKey,
                            cap: 1,
                            period: DailyResetService.PeriodDay,
                            utcNow: utcNow))
                    {
                        return generated;
                    }

                    RollbackGenerated(
                        rollback,
                        generatedDrops,
                        characterId,
                        definition.GroupKey,
                        monsterId,
                        "claim-rejected",
                        dungeonId,
                        runIdentity,
                        sourceEventId);
                    return EmptyResult();
                }
                catch (Exception ex)
                {
                    RollbackGenerated(
                        rollback,
                        generatedDrops,
                        characterId,
                        definition.GroupKey,
                        monsterId,
                        "claim-exception",
                        dungeonId,
                        runIdentity,
                        sourceEventId);
                    LogFailure(
                        characterId,
                        definition.GroupKey,
                        monsterId,
                        "claim-counter",
                        ex.Message,
                        dungeonId,
                        runIdentity,
                        sourceEventId);
                    return EmptyResult();
                }
            }
        }

        internal static string BuildCounterKey(int groupKey, int monsterId) =>
            "sequential_monster_drop_v1:" + groupKey + ":" + monsterId;

        private static object[] CreateGates()
        {
            var gates = new object[GateCount];
            for (var index = 0; index < gates.Length; index++)
                gates[index] = new object();
            return gates;
        }

        private static int ResolveGateIndex(
            int characterId,
            int groupKey,
            int monsterId)
        {
            unchecked
            {
                var hash = characterId;
                hash = (hash * 397) ^ groupKey;
                hash = (hash * 397) ^ monsterId;
                return (int)((uint)hash % GateCount);
            }
        }

        private static void RollbackGenerated(
            Action<IReadOnlyList<DropInfo>> rollback,
            IReadOnlyList<DropInfo> drops,
            int characterId,
            int groupKey,
            int monsterId,
            string stage,
            int dungeonId,
            DungeonRunIdentity runIdentity,
            Guid sourceEventId)
        {
            try
            {
                rollback(drops);
            }
            catch (Exception ex)
            {
                LogFailure(
                    characterId,
                    groupKey,
                    monsterId,
                    stage + "-rollback",
                    ex.Message,
                    dungeonId,
                    runIdentity,
                    sourceEventId);
            }
        }

        private static MonsterDropResult EmptyResult() =>
            new MonsterDropResult
            {
                Drops = new List<DropInfo>(),
            };

        private static void LogFailure(
            int characterId,
            int groupKey,
            int monsterId,
            string stage,
            string error,
            int dungeonId = 0,
            DungeonRunIdentity runIdentity = default,
            Guid sourceEventId = default)
        {
            FileLogger.Log(
                "[SequentialDungeonDailyLoot] "
                + $"character={characterId} group={groupKey} "
                + $"dungeon={dungeonId} monster={monsterId} "
                + $"instance={runIdentity.PartyDungeonInstanceId} "
                + $"run={runIdentity.RunId} "
                + $"generation={runIdentity.RunGeneration} "
                + $"event={sourceEventId:N} stage={Sanitize(stage)} "
                + $"error={Sanitize(error)}");
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";
            return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
