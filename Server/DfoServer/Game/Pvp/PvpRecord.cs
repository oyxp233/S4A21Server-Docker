using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.Game.Characters;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.Pvp
{
    // The character row owns both grades. This immutable projection combines
    // those grades with the separately persisted progress and current PVF bounds.
    internal sealed class PvpRecord
    {
        internal byte Grade { get; init; }
        internal byte RatingGrade { get; init; }
        internal int Wins { get; init; }
        internal int Losses { get; init; }
        internal int Experience { get; init; }
        internal int ExperienceFloor { get; init; }
        internal int ExperienceCeiling { get; init; }
        internal int RankPoint { get; init; }
        internal int PeakRankPoint { get; init; }
        internal int RankWarmupGames { get; init; }
        internal int TotalMatchPoint { get; init; }
        internal int TotalMatchWarmupGames { get; init; }

        internal PvpRecord WithProgress(int experience, int wins, int losses, PvpExperienceRules rules)
        {
            var grade = rules.GetGrade(experience);
            var bounds = rules.GetBounds(grade);
            return new PvpRecord
            {
                Grade = grade,
                RatingGrade = RatingGrade,
                Experience = experience,
                ExperienceFloor = bounds.Floor,
                ExperienceCeiling = bounds.Ceiling,
                Wins = wins,
                Losses = losses,
                RankPoint = RankPoint,
                PeakRankPoint = PeakRankPoint,
                RankWarmupGames = RankWarmupGames,
                TotalMatchPoint = TotalMatchPoint,
                TotalMatchWarmupGames = TotalMatchWarmupGames
            };
        }
    }

    internal sealed class PvpExperienceRules
    {
        private readonly int[] _thresholds;
        private readonly int[] _winExperience;
        private readonly int[] _lossExperience;
        private static readonly Lazy<PvpExperienceRules> Rules = new Lazy<PvpExperienceRules>(
            () => Parse(PvfArchiveAccessor.ReadText("etc/pvp_ref.etc")));

        private PvpExperienceRules(int maxGrade, int[] thresholds, int[] wins, int[] losses)
        {
            MaxGrade = maxGrade;
            _thresholds = thresholds;
            _winExperience = wins;
            _lossExperience = losses;
        }

        internal static PvpExperienceRules Current => Rules.Value;
        internal int MaxGrade { get; }
        internal int MaximumExperience => _thresholds[MaxGrade];

        internal (int Floor, int Ceiling) GetBounds(byte grade)
        {
            if (grade > MaxGrade)
                throw new ArgumentOutOfRangeException(nameof(grade));
            // 29FE8B0 stores the threshold at the resource's grade index;
            // entry zero starts at zero. 19E3B30 / 1368FA0 use the first
            // [pvp experience loss] value for MAX (currently grade 20).
            // The final resource row is the positive upper sentinel.
            return (_thresholds[grade], _thresholds[grade + 1]);
        }

        internal byte GetGrade(int experience)
        {
            if (experience < 0)
                throw new ArgumentOutOfRangeException(nameof(experience));
            byte grade = 0;
            while (grade < MaxGrade && experience >= _thresholds[grade + 1])
                grade++;
            return grade;
        }

        internal int GetExperienceChange(byte grade, PvpMatchOutcome outcome)
        {
            if (grade > MaxGrade)
                throw new ArgumentOutOfRangeException(nameof(grade));
            if (grade == MaxGrade || outcome == PvpMatchOutcome.Draw)
                return 0;
            return outcome == PvpMatchOutcome.Win ? _winExperience[grade] : _lossExperience[grade];
        }

        internal static PvpExperienceRules Parse(string text)
        {
            var root = new ScriptParser().Parse(text);
            var maxValues = ReadInts(root.GetChild("pvp experience max grade"), text);
            var pairs = ReadInts(root.GetChild("pvp experience"), text);
            var loss = ReadInts(root.GetChild("pvp experience loss"), text);
            var points = ReadInts(root.GetChild("pvp grade point"), text);
            if (maxValues.Length != 1 || maxValues[0] < 1 || maxValues[0] > byte.MaxValue
                || pairs.Length != maxValues[0] * 2 || loss.Length != 2
                || loss[0] < 1 || loss[0] >= maxValues[0] || loss[1] != 0
                || points.Length != (loss[0] + 1) * 3)
                throw new InvalidDataException("Invalid PvP experience table.");

            var thresholds = new int[maxValues[0] + 1];
            for (var i = 0; i < pairs.Length; i += 2)
            {
                var grade = i / 2 + 1;
                if (pairs[i] != grade || pairs[i + 1] <= thresholds[grade - 1])
                    throw new InvalidDataException("PvP experience thresholds must be continuous and increasing.");
                thresholds[grade] = pairs[i + 1];
            }
            var wins = new int[loss[0] + 1];
            var losses = new int[wins.Length];
            for (var grade = 0; grade < wins.Length; grade++)
            {
                var index = grade * 3;
                // 29FE8B0 keys each resource row by (grade, outcome), with
                // outcome 1 for the positive value and 0 for the loss value.
                if (points[index] != grade || points[index + 1] < 0 || points[index + 2] > 0)
                    throw new InvalidDataException("Invalid PvP outcome experience table.");
                wins[grade] = points[index + 1];
                losses[grade] = points[index + 2];
            }
            return new PvpExperienceRules(loss[0], thresholds, wins, losses);
        }

        private static int[] ReadInts(ScriptNode node, string text)
        {
            if (node?.DataItems == null)
                return Array.Empty<int>();
            return node.DataItems.SelectMany(item => Regex.Matches(
                    item.GetContent(text) ?? string.Empty, @"-?\d+").Cast<Match>())
                .Select(match => int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
        }
    }
}
