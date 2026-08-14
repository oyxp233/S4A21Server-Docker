using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.GameWorld
{
    internal enum DungeonExperienceDefinitionKind : byte
    {
        Unavailable = 0,
        Standard = 1,
        Risk = 2,
        DeathTower = 3,
        BloodAltar = 4,
        Tournament = 5,
    }

    // Immutable configuration frozen onto DungeonInstance. Runtime code must
    // not reread DGN/ETC while applying a kill or preparing settlement.
    internal sealed class DungeonExperienceDefinition
    {
        private readonly double[] _difficultyRates;
        private readonly double[] _partyMemberRates;
        private readonly double[] _monsterKindRates;

        internal DungeonExperienceDefinition(
            int dungeonId,
            DungeonExperienceDefinitionKind kind,
            int standardLevel,
            double experienceWeight,
            IReadOnlyList<double> difficultyRates,
            IReadOnlyList<double> partyMemberRates,
            IReadOnlyList<double> monsterKindRates,
            double legacyMonsterOverallRate,
            bool isAvailable = true,
            DungeonClearExperienceBonusDefinition clearBonusDefinition = null)
        {
            DungeonId = dungeonId;
            Kind = kind;
            StandardLevel = standardLevel;
            ExperienceWeight = experienceWeight;
            _difficultyRates = Copy(difficultyRates);
            _partyMemberRates = Copy(partyMemberRates);
            _monsterKindRates = Copy(monsterKindRates);
            LegacyMonsterOverallRate = legacyMonsterOverallRate;
            IsAvailable = isAvailable;
            ClearBonusDefinition = clearBonusDefinition
                ?? DungeonClearExperienceBonusDefinition.A14;
        }

        internal int DungeonId { get; }
        internal DungeonExperienceDefinitionKind Kind { get; }
        internal int StandardLevel { get; }
        internal double ExperienceWeight { get; }
        internal double LegacyMonsterOverallRate { get; }
        internal bool IsAvailable { get; }
        internal DungeonClearExperienceBonusDefinition ClearBonusDefinition
        {
            get;
        }
        internal bool UsesStandardFormula =>
            IsAvailable && Kind == DungeonExperienceDefinitionKind.Standard;

        internal double GetDifficultyRate(int difficulty)
        {
            if (_difficultyRates.Length == 0
                || difficulty < 0
                || difficulty >= _difficultyRates.Length)
            {
                return 1.0;
            }

            return _difficultyRates[difficulty];
        }

        internal double GetPartyMemberRate(int partyMemberCount)
        {
            if (_partyMemberRates.Length == 0)
                return 1.0;

            var index = Math.Min(
                _partyMemberRates.Length - 1,
                Math.Max(0, partyMemberCount - 1));
            return _partyMemberRates[index];
        }

        internal double GetMonsterKindRate(int monsterKind)
        {
            if (_monsterKindRates.Length == 0)
                return 1.0;
            if (monsterKind < 0 || monsterKind >= _monsterKindRates.Length)
                return _monsterKindRates[0];
            return _monsterKindRates[monsterKind];
        }

        internal static DungeonExperienceDefinition CreateUnavailable(
            int dungeonId)
            => new DungeonExperienceDefinition(
                dungeonId,
                DungeonExperienceDefinitionKind.Unavailable,
                standardLevel: 0,
                experienceWeight: 0.0,
                difficultyRates: Array.Empty<double>(),
                partyMemberRates: Array.Empty<double>(),
                monsterKindRates: Array.Empty<double>(),
                legacyMonsterOverallRate: 0.0,
                isAvailable: false);

        private static double[] Copy(IReadOnlyList<double> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<double>();

            var copy = new double[source.Count];
            for (var index = 0; index < source.Count; index++)
                copy[index] = source[index];
            return copy;
        }
    }

    internal static class DungeonExperienceDefinitionCatalog
    {
        private const string ServerParameterPath = "Etc/ServerParameter.etc";
        private static readonly ConcurrentDictionary<int, DungeonExperienceDefinition>
            Definitions = new ConcurrentDictionary<int, DungeonExperienceDefinition>();
        private static readonly Lazy<ServerExperienceParameters> ServerParameters =
            new Lazy<ServerExperienceParameters>(LoadServerParameters);

        internal static DungeonExperienceDefinition Resolve(int dungeonId)
            => Definitions.GetOrAdd(dungeonId, LoadDungeonDefinition);

        internal static void WarmUp()
        {
            _ = ServerParameters.Value;
        }

        private static DungeonExperienceDefinition LoadDungeonDefinition(
            int dungeonId)
        {
            try
            {
                var parameters = ServerParameters.Value;
                if (!parameters.IsAvailable)
                    throw new InvalidOperationException(parameters.Error);

                var dungeon = DungeonCatalog.GetDungeonFile(dungeonId);
                var experienceWeight = dungeon != null
                    && dungeon.ExperienceIncreasingPoint >= 0
                    ? dungeon.ExperienceIncreasingPoint
                    : 1.0f;
                if (dungeon == null
                    || dungeon.BasisLevel <= 0
                    || experienceWeight < 0
                    || float.IsNaN(experienceWeight)
                    || float.IsInfinity(experienceWeight))
                {
                    throw new InvalidOperationException(
                        $"Dungeon {dungeonId} has invalid experience metadata.");
                }

                var definition = new DungeonExperienceDefinition(
                    dungeonId,
                    ResolveKind(dungeon),
                    dungeon.BasisLevel,
                    experienceWeight,
                    parameters.DifficultyRates,
                    parameters.PartyMemberRates,
                    parameters.MonsterKindRates,
                    parameters.LegacyMonsterOverallRate);
                FileLogger.Log(
                    $"[DungeonExperienceDefinition] loaded dungeon={dungeonId} " +
                    $"kind={definition.Kind} standardLevel={definition.StandardLevel} " +
                    $"weight={definition.ExperienceWeight:R}");
                return definition;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonExperienceDefinition] unavailable dungeon={dungeonId}: " +
                    ex.Message);
                return DungeonExperienceDefinition.CreateUnavailable(dungeonId);
            }
        }

        private static DungeonExperienceDefinitionKind ResolveKind(
            DungeonFile dungeon)
        {
            if (dungeon.TournamentDungeon)
                return DungeonExperienceDefinitionKind.Tournament;
            if (dungeon.BloodDungeon)
                return DungeonExperienceDefinitionKind.BloodAltar;
            if (dungeon.TowerOfDespair > 0
                || dungeon.DeathTowerStages?.Count > 0
                || !string.IsNullOrWhiteSpace(dungeon.DeathTowerMapIndexes))
            {
                return DungeonExperienceDefinitionKind.DeathTower;
            }
            if (dungeon.RiskDungeon)
                return DungeonExperienceDefinitionKind.Risk;
            return DungeonExperienceDefinitionKind.Standard;
        }

        private static ServerExperienceParameters LoadServerParameters()
        {
            try
            {
                var text = PvfArchiveAccessor.ReadText(ServerParameterPath);
                var root = new ScriptParser().Parse(text ?? string.Empty);
                var difficultyRates = ParseRates(
                    root.GetChild("dungeon difficulty exp bonusrate"),
                    text);
                var partyMemberRates = ParseRates(
                    root.GetChild("party user number exp bonusrate"),
                    text);
                var monsterKindRates = ParseRates(
                    root.GetChild("drop bonusrate of monster kind"),
                    text);
                var legacyMonsterRates = ParseRates(
                    root.GetChild("monster exp bonusrate"),
                    text);

                RequireRates(
                    difficultyRates,
                    minimumCount: 5,
                    "dungeon difficulty exp bonusrate");
                RequireRates(
                    partyMemberRates,
                    minimumCount: 4,
                    "party user number exp bonusrate");
                RequireRates(
                    monsterKindRates,
                    minimumCount: 4,
                    "drop bonusrate of monster kind");
                RequireRates(
                    legacyMonsterRates,
                    minimumCount: 1,
                    "monster exp bonusrate");

                FileLogger.Log(
                    "[DungeonExperienceDefinition] server parameters loaded: " +
                    $"difficulty={string.Join(',', difficultyRates)} " +
                    $"party={string.Join(',', partyMemberRates)} " +
                    $"monsterKind={string.Join(',', monsterKindRates)}");
                return new ServerExperienceParameters(
                    difficultyRates,
                    partyMemberRates,
                    monsterKindRates,
                    legacyMonsterRates[0]);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonExperienceDefinition] failed to load " +
                    $"{ServerParameterPath}: {ex.Message}");
                return ServerExperienceParameters.Unavailable(ex.Message);
            }
        }

        private static double[] ParseRates(ScriptNode node, string text)
        {
            var values = new List<double>();
            foreach (var token in ScriptValueTokenizer.Tokenize(
                         node?.GetFirstDataContent(text ?? string.Empty)))
            {
                if (double.TryParse(
                        token,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    values.Add(value);
                }
            }
            return values.ToArray();
        }

        private static void RequireRates(
            IReadOnlyList<double> rates,
            int minimumCount,
            string tag)
        {
            if (rates == null || rates.Count < minimumCount)
                throw new InvalidOperationException($"[{tag}] is incomplete.");

            for (var index = 0; index < rates.Count; index++)
            {
                var rate = rates[index];
                if (rate <= 0.0 || double.IsNaN(rate) || double.IsInfinity(rate))
                {
                    throw new InvalidOperationException(
                        $"[{tag}] contains invalid rate {rate:R} at {index}.");
                }
            }
        }

        private sealed class ServerExperienceParameters
        {
            internal ServerExperienceParameters(
                double[] difficultyRates,
                double[] partyMemberRates,
                double[] monsterKindRates,
                double legacyMonsterOverallRate,
                bool isAvailable = true,
                string error = null)
            {
                DifficultyRates = difficultyRates ?? Array.Empty<double>();
                PartyMemberRates = partyMemberRates ?? Array.Empty<double>();
                MonsterKindRates = monsterKindRates ?? Array.Empty<double>();
                LegacyMonsterOverallRate = legacyMonsterOverallRate;
                IsAvailable = isAvailable;
                Error = error ?? string.Empty;
            }

            internal double[] DifficultyRates { get; }
            internal double[] PartyMemberRates { get; }
            internal double[] MonsterKindRates { get; }
            internal double LegacyMonsterOverallRate { get; }
            internal bool IsAvailable { get; }
            internal string Error { get; }

            internal static ServerExperienceParameters Unavailable(string error)
                => new ServerExperienceParameters(
                    Array.Empty<double>(),
                    Array.Empty<double>(),
                    Array.Empty<double>(),
                    legacyMonsterOverallRate: 0.0,
                    isAvailable: false,
                    error: error);
        }
    }
}
