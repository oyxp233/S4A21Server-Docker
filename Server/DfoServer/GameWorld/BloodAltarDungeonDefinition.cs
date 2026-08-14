using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace DfoServer.GameWorld
{
    internal enum BloodAltarDungeonKind : byte
    {
        Endless = 1,
        Ultimate = 2,
    }

    internal sealed class BloodAltarMonsterDefinition
    {
        internal BloodAltarMonsterDefinition(
            int monsterCode,
            int templateType,
            int x,
            int y,
            int z,
            int durationMilliseconds,
            int spawnIntervalMilliseconds,
            int baseSpawnCount,
            int spawnCountIncrement,
            int batchCount)
        {
            MonsterCode = monsterCode;
            TemplateType = templateType;
            X = x;
            Y = y;
            Z = z;
            DurationMilliseconds = durationMilliseconds;
            SpawnIntervalMilliseconds = spawnIntervalMilliseconds;
            BaseSpawnCount = baseSpawnCount;
            SpawnCountIncrement = spawnCountIncrement;
            BatchCount = batchCount;
        }

        internal int MonsterCode { get; }
        internal int TemplateType { get; }
        internal int X { get; }
        internal int Y { get; }
        internal int Z { get; }
        internal int DurationMilliseconds { get; }
        internal int SpawnIntervalMilliseconds { get; }
        internal int BaseSpawnCount { get; }
        internal int SpawnCountIncrement { get; }
        internal int BatchCount { get; }
    }

    internal sealed class BloodAltarPhaseDefinition
    {
        internal BloodAltarPhaseDefinition(
            int round,
            int monsterTemplateIndex,
            int delayMilliseconds,
            float scale,
            int flag,
            int concurrentPhaseCount,
            byte difficulty)
        {
            Round = round;
            MonsterTemplateIndex = monsterTemplateIndex;
            DelayMilliseconds = delayMilliseconds;
            Scale = scale;
            Flag = flag;
            ConcurrentPhaseCount = concurrentPhaseCount;
            Difficulty = difficulty;
        }

        internal int Round { get; }
        internal int MonsterTemplateIndex { get; }
        internal int DelayMilliseconds { get; }
        internal float Scale { get; }
        internal int Flag { get; }
        internal int ConcurrentPhaseCount { get; }
        internal byte Difficulty { get; }
    }

    internal sealed class BloodAltarRoundDefinition
    {
        internal BloodAltarRoundDefinition(
            int number,
            IReadOnlyList<BloodAltarPhaseDefinition> phases)
        {
            Number = number;
            Phases = Freeze(phases);
        }

        internal int Number { get; }
        internal IReadOnlyList<BloodAltarPhaseDefinition> Phases { get; }

        private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<T>();
            var copy = new T[source.Count];
            for (var index = 0; index < source.Count; index++)
                copy[index] = source[index];
            return new ReadOnlyCollection<T>(copy);
        }
    }

    internal sealed class BloodAltarMapDefinition
    {
        internal BloodAltarMapDefinition(
            int mapId,
            IReadOnlyList<BloodAltarMonsterDefinition> monsters,
            IReadOnlyList<BloodAltarRoundDefinition> rounds)
        {
            MapId = mapId;
            Monsters = Freeze(monsters);
            Rounds = Freeze(rounds);
        }

        internal int MapId { get; }
        internal IReadOnlyList<BloodAltarMonsterDefinition> Monsters { get; }
        internal IReadOnlyList<BloodAltarRoundDefinition> Rounds { get; }

        private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<T>();
            var copy = new T[source.Count];
            for (var index = 0; index < source.Count; index++)
                copy[index] = source[index];
            return new ReadOnlyCollection<T>(copy);
        }
    }

    internal sealed class BloodAltarDungeonDefinition
    {
        internal BloodAltarDungeonDefinition(
            int dungeonId,
            BloodAltarDungeonKind kind,
            int maxRounds,
            byte basisLevel,
            BloodAltarRewardDefinition rewardDefinition)
        {
            DungeonId = dungeonId;
            Kind = kind;
            MaxRounds = maxRounds;
            BasisLevel = basisLevel;
            Rewards = rewardDefinition
                ?? throw new ArgumentNullException(nameof(rewardDefinition));
        }

        internal int DungeonId { get; }
        internal BloodAltarDungeonKind Kind { get; }
        internal int MaxRounds { get; }
        internal byte BasisLevel { get; }
        internal BloodAltarRewardDefinition Rewards { get; }
    }

    internal static class BloodAltarDungeonDefinitionCatalog
    {
        private const int MonsterFieldCount = 10;
        private const int EndlessPhaseFieldCount = 6;
        private const int UltimatePhaseFieldCount = 7;

        private static readonly ConcurrentDictionary<int, BloodAltarDungeonDefinition>
            Dungeons = new ConcurrentDictionary<int, BloodAltarDungeonDefinition>();
        private static readonly ConcurrentDictionary<long, BloodAltarMapDefinition>
            Maps = new ConcurrentDictionary<long, BloodAltarMapDefinition>();

        internal static bool IsBloodAltarDungeon(int dungeonId)
        {
            try
            {
                var dungeon = DungeonCatalog.GetDungeonFile(dungeonId);
                return dungeon.BloodDungeon
                    && dungeon.BloodDungeonType >= (int)BloodAltarDungeonKind.Endless
                    && dungeon.BloodDungeonType <= (int)BloodAltarDungeonKind.Ultimate;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryResolveDungeon(
            int dungeonId,
            out BloodAltarDungeonDefinition definition,
            out string failureReason)
        {
            definition = null;
            failureReason = string.Empty;
            if (dungeonId <= 0 || dungeonId > ushort.MaxValue)
            {
                failureReason = "blood altar dungeon id is outside the protocol range";
                return false;
            }
            if (Dungeons.TryGetValue(dungeonId, out definition))
                return true;

            try
            {
                var dungeon = DungeonCatalog.GetDungeonFile(dungeonId);
                if (!dungeon.BloodDungeon
                    || dungeon.BloodDungeonType < 1
                    || dungeon.BloodDungeonType > 2)
                {
                    failureReason = "dungeon is not marked as blood altar";
                    return false;
                }
                if (dungeon.BloodMaxRound <= 0
                    || dungeon.BloodMaxRound > byte.MaxValue
                    || dungeon.BasisLevel <= 0
                    || dungeon.BasisLevel > byte.MaxValue)
                {
                    failureReason = "blood altar DGN round or level is invalid";
                    return false;
                }

                var rewards = BloodAltarRewardDefinitionCatalog.Current;
                if (!rewards.IsAvailable)
                {
                    failureReason =
                        "blood altar reward definition is unavailable";
                    return false;
                }

                var projected = new BloodAltarDungeonDefinition(
                    dungeonId,
                    (BloodAltarDungeonKind)dungeon.BloodDungeonType,
                    dungeon.BloodMaxRound,
                    (byte)dungeon.BasisLevel,
                    rewards);
                definition = Dungeons.GetOrAdd(dungeonId, projected);
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        internal static bool TryResolveMap(
            BloodAltarDungeonDefinition dungeon,
            int mapId,
            out BloodAltarMapDefinition definition,
            out string failureReason)
        {
            definition = null;
            failureReason = string.Empty;
            if (dungeon == null || mapId <= 0 || mapId > ushort.MaxValue)
            {
                failureReason = "blood altar dungeon or map is invalid";
                return false;
            }

            var key = ((long)dungeon.DungeonId << 32) | (uint)mapId;
            if (Maps.TryGetValue(key, out definition))
                return true;

            try
            {
                var map = DungeonMapCatalog.GetMapFile(mapId);
                if (map.DungeonId != dungeon.DungeonId)
                {
                    failureReason = "blood altar MAP belongs to another dungeon";
                    return false;
                }

                var monsterText = dungeon.Kind == BloodAltarDungeonKind.Ultimate
                    ? map.UltimateMonster
                    : map.BloodMonster;
                var phaseText = dungeon.Kind == BloodAltarDungeonKind.Ultimate
                    ? map.UltimatePhaseTime
                    : map.BloodPhaseTime;
                if (!TryParseMonsters(
                        monsterText,
                        out var monsters,
                        out failureReason)
                    || !TryParsePhases(
                        dungeon.Kind,
                        phaseText,
                        monsters.Count,
                        out var phases,
                        out failureReason))
                {
                    return false;
                }

                var rounds = phases
                    .GroupBy(phase => phase.Round)
                    .OrderBy(group => group.Key)
                    .Select(group => new BloodAltarRoundDefinition(
                        group.Key,
                        group.ToArray()))
                    .ToArray();
                if (rounds.Length == 0
                    || rounds.Any(round => round.Phases.Count == 0))
                {
                    failureReason = "blood altar MAP has no valid rounds";
                    return false;
                }

                var projected = new BloodAltarMapDefinition(
                    mapId,
                    monsters,
                    rounds);
                definition = Maps.GetOrAdd(key, projected);
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        private static bool TryParseMonsters(
            string value,
            out IReadOnlyList<BloodAltarMonsterDefinition> definitions,
            out string failureReason)
        {
            definitions = Array.Empty<BloodAltarMonsterDefinition>();
            failureReason = string.Empty;
            var tokens = ScriptValueTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count % MonsterFieldCount != 0)
            {
                failureReason =
                    $"blood altar monster rows have {tokens.Count} fields";
                return false;
            }

            var parsed = new List<BloodAltarMonsterDefinition>(
                tokens.Count / MonsterFieldCount);
            for (var offset = 0; offset < tokens.Count; offset += MonsterFieldCount)
            {
                var row = new int[MonsterFieldCount];
                for (var field = 0; field < row.Length; field++)
                {
                    if (!TryParseInt(tokens[offset + field], out row[field]))
                    {
                        failureReason =
                            $"blood altar monster row {offset / MonsterFieldCount} " +
                            $"field {field} is invalid";
                        return false;
                    }
                }

                if (row[0] <= 0
                    || row[2] < 0 || row[2] > ushort.MaxValue
                    || row[3] < 0 || row[3] > ushort.MaxValue
                    || row[4] < 0 || row[4] > ushort.MaxValue
                    || row[5] < 0
                    || row[6] < 0
                    || row[7] < 0
                    || row[9] < 0)
                {
                    failureReason =
                        $"blood altar monster row {offset / MonsterFieldCount} " +
                        "contains an invalid code, coordinate, duration or count";
                    return false;
                }

                parsed.Add(new BloodAltarMonsterDefinition(
                    row[0], row[1], row[2], row[3], row[4],
                    row[5], row[6], row[7], row[8], row[9]));
            }

            definitions = new ReadOnlyCollection<BloodAltarMonsterDefinition>(parsed);
            return true;
        }

        private static bool TryParsePhases(
            BloodAltarDungeonKind kind,
            string value,
            int monsterCount,
            out IReadOnlyList<BloodAltarPhaseDefinition> definitions,
            out string failureReason)
        {
            definitions = Array.Empty<BloodAltarPhaseDefinition>();
            failureReason = string.Empty;
            var width = kind == BloodAltarDungeonKind.Ultimate
                ? UltimatePhaseFieldCount
                : EndlessPhaseFieldCount;
            var tokens = ScriptValueTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count % width != 0)
            {
                failureReason =
                    $"blood altar phase rows have {tokens.Count} fields for width {width}";
                return false;
            }

            var parsed = new List<BloodAltarPhaseDefinition>(tokens.Count / width);
            for (var offset = 0; offset < tokens.Count; offset += width)
            {
                if (!TryParseInt(tokens[offset], out var round)
                    || !TryParseInt(tokens[offset + 1], out var templateIndex)
                    || !TryParseInt(tokens[offset + 2], out var delay)
                    || !float.TryParse(
                        tokens[offset + 3],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var scale)
                    || !TryParseInt(tokens[offset + 4], out var flag)
                    || !TryParseInt(tokens[offset + 5], out var concurrent))
                {
                    failureReason =
                        $"blood altar phase row {offset / width} is malformed";
                    return false;
                }

                var difficulty = (byte)0;
                if (kind == BloodAltarDungeonKind.Ultimate
                    && !TryParseDifficulty(tokens[offset + 6], out difficulty))
                {
                    failureReason =
                        $"blood altar phase row {offset / width} has invalid difficulty";
                    return false;
                }
                if (round < 0
                    || templateIndex < 0
                    || templateIndex >= monsterCount
                    || delay < 0
                    || scale <= 0
                    || float.IsNaN(scale)
                    || float.IsInfinity(scale)
                    || concurrent < 0
                    || concurrent > 10)
                {
                    failureReason =
                        $"blood altar phase row {offset / width} contains invalid values";
                    return false;
                }

                parsed.Add(new BloodAltarPhaseDefinition(
                    round,
                    templateIndex,
                    delay,
                    scale,
                    flag,
                    concurrent,
                    difficulty));
            }

            definitions = new ReadOnlyCollection<BloodAltarPhaseDefinition>(parsed);
            return true;
        }

        private static bool TryParseInt(string value, out int parsed)
            => int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed);

        private static bool TryParseDifficulty(string value, out byte difficulty)
        {
            if (string.Equals(value, "A", StringComparison.OrdinalIgnoreCase))
            {
                difficulty = 1;
                return true;
            }
            if (string.Equals(value, "B", StringComparison.OrdinalIgnoreCase))
            {
                difficulty = 2;
                return true;
            }
            return byte.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out difficulty)
                && (difficulty == 1 || difficulty == 2);
        }
    }
}
