using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace DfoServer.GameWorld
{
    internal enum BloodAltarRewardCandidateKind
    {
        Gold,
        Item,
        Empty,
    }

    internal sealed class BloodAltarUltimateRewardProbability
    {
        internal BloodAltarUltimateRewardProbability(
            int point,
            int item1251Probability,
            int item1252Probability)
        {
            Point = point;
            Item1251Probability = item1251Probability;
            Item1252Probability = item1252Probability;
        }

        internal int Point { get; }
        internal int Item1251Probability { get; }
        internal int Item1252Probability { get; }
    }

    internal sealed class BloodAltarRewardDefinition
    {
        internal const int MaximumRewardProgress = 10;

        private readonly float[] _rewardExperienceWeights;
        private readonly float[] _entryCountExperienceWeights;
        private readonly int[] _ultimateDifficultyPoints;
        private readonly IReadOnlyDictionary<int, BloodAltarUltimateRewardProbability>
            _ultimateRewards;

        internal BloodAltarRewardDefinition(
            int goldCandidateWeight,
            int itemCandidateWeight,
            int rewardWeightPerProgress,
            float goldAmountWeight,
            float[] rewardExperienceWeights,
            float[] entryCountExperienceWeights,
            int[] ultimateDifficultyPoints,
            IDictionary<int, BloodAltarUltimateRewardProbability> ultimateRewards)
        {
            GoldCandidateWeight = goldCandidateWeight;
            ItemCandidateWeight = itemCandidateWeight;
            RewardWeightPerProgress = rewardWeightPerProgress;
            GoldAmountWeight = goldAmountWeight;
            _rewardExperienceWeights = rewardExperienceWeights
                ?? Array.Empty<float>();
            _entryCountExperienceWeights = entryCountExperienceWeights
                ?? Array.Empty<float>();
            _ultimateDifficultyPoints = ultimateDifficultyPoints
                ?? Array.Empty<int>();
            _ultimateRewards = new ReadOnlyDictionary<
                int,
                BloodAltarUltimateRewardProbability>(
                    new Dictionary<int, BloodAltarUltimateRewardProbability>(
                        ultimateRewards
                        ?? new Dictionary<int, BloodAltarUltimateRewardProbability>()));
        }

        internal int GoldCandidateWeight { get; }
        internal int ItemCandidateWeight { get; }
        internal int RewardWeightPerProgress { get; }
        internal int RewardRollScale =>
            RewardWeightPerProgress * MaximumRewardProgress;
        internal float GoldAmountWeight { get; }
        internal int RewardExperienceWeightCount =>
            _rewardExperienceWeights.Length;
        internal int EntryCountExperienceWeightCount =>
            _entryCountExperienceWeights.Length;
        internal int UltimateRewardCount => _ultimateRewards.Count;

        internal bool IsAvailable =>
            GoldCandidateWeight >= 0
            && ItemCandidateWeight >= 0
            && RewardWeightPerProgress > 0
            && GoldCandidateWeight + ItemCandidateWeight
                <= RewardWeightPerProgress
            && GoldAmountWeight > 0f
            && !float.IsNaN(GoldAmountWeight)
            && !float.IsInfinity(GoldAmountWeight)
            && _rewardExperienceWeights.Length > 0
            && _entryCountExperienceWeights.Length > 0
            && _ultimateDifficultyPoints.Length == 2
            && _ultimateRewards.Count > 0;

        internal int GetRewardCardCount(
            int completedRounds,
            int maximumRounds)
        {
            if (maximumRounds <= 0)
                return 0;
            var count = MaximumRewardProgress
                * Math.Max(0, completedRounds)
                / maximumRounds;
            return Math.Min(
                MaximumRewardProgress,
                Math.Max(1, count));
        }

        internal BloodAltarRewardCandidateKind ClassifyCandidate(
            int completedRounds,
            int roll)
        {
            if (!IsAvailable)
                throw new InvalidOperationException(
                    "Blood altar reward definition is unavailable.");
            if (roll < 0 || roll >= RewardRollScale)
                throw new ArgumentOutOfRangeException(nameof(roll));

            var progress = Math.Min(
                MaximumRewardProgress,
                Math.Max(0, completedRounds));
            var goldThreshold = progress * GoldCandidateWeight;
            if (roll < goldThreshold)
                return BloodAltarRewardCandidateKind.Gold;

            var itemThreshold = progress
                * (GoldCandidateWeight + ItemCandidateWeight);
            return roll < itemThreshold
                ? BloodAltarRewardCandidateKind.Item
                : BloodAltarRewardCandidateKind.Empty;
        }

        internal float GetRewardExperienceWeight(int completedRounds)
            => GetIndexedWeight(_rewardExperienceWeights, completedRounds);

        internal float GetEntryCountExperienceWeight(int entryCount)
            => GetIndexedWeight(_entryCountExperienceWeights, entryCount);

        internal int CalculateUltimatePoint(
            IReadOnlyList<byte> completedDifficulties)
        {
            var total = 0L;
            if (completedDifficulties != null)
            {
                foreach (var difficulty in completedDifficulties)
                {
                    if (difficulty < 1
                        || difficulty > _ultimateDifficultyPoints.Length)
                    {
                        continue;
                    }
                    total += _ultimateDifficultyPoints[difficulty - 1];
                }
            }
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        internal bool TryResolveUltimateRewardItem(
            int point,
            int roll,
            out int itemId)
        {
            itemId = -1;
            if (roll < 0 || roll >= 100)
                return false;
            if (!_ultimateRewards.TryGetValue(point, out var reward))
                return false;

            if (reward.Item1252Probability > 0
                && roll < reward.Item1252Probability)
            {
                itemId = 1252;
                return true;
            }

            var item1251Threshold = reward.Item1252Probability
                + reward.Item1251Probability;
            if (reward.Item1251Probability > 0
                && roll < item1251Threshold)
            {
                itemId = 1251;
                return true;
            }
            return false;
        }

        private static float GetIndexedWeight(float[] values, int index)
        {
            if (values == null || values.Length == 0 || index < 0)
                return 0f;
            return values[Math.Min(index, values.Length - 1)];
        }
    }

    internal static class BloodAltarRewardDefinitionCatalog
    {
        private static readonly object SyncRoot = new object();
        private static BloodAltarRewardDefinition _current;

        internal static BloodAltarRewardDefinition Current
        {
            get
            {
                lock (SyncRoot)
                {
                    if (_current != null)
                        return _current;
                    try
                    {
                        _current = Parse(PvfArchiveAccessor.ReadText(
                            "etc/bloodclearreward.etc"));
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[BloodAltar] reward definition load failed: {ex.Message}");
                        _current = CreateUnavailable();
                    }
                    return _current;
                }
            }
        }

        internal static BloodAltarRewardDefinition Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return CreateUnavailable();

            var probabilities = ParseInts(ReadSection(
                text,
                "reward item prob"));
            var goldWeights = ParseFloats(ReadSection(
                text,
                "reward gold weight"));
            var rewardExperienceWeights = ParseFloats(ReadSection(
                text,
                "reward exp weight"));
            var entryCountExperienceWeights = ParseFloats(ReadSection(
                text,
                "reward exp inout count weight"));
            var difficultyPoints = ParseInts(ReadSection(
                text,
                "ultimate difficulty point"));
            var ultimateValues = ParseInts(ReadSection(
                text,
                "ultimate reward prob"));

            if (probabilities.Count != 3
                || goldWeights.Count != 1
                || rewardExperienceWeights.Count == 0
                || entryCountExperienceWeights.Count == 0
                || difficultyPoints.Count != 2
                || ultimateValues.Count == 0
                || ultimateValues.Count % 3 != 0)
            {
                return CreateUnavailable();
            }

            var goldWeight = probabilities[0];
            var itemWeight = probabilities[1];
            var weightPerProgress = probabilities[2];
            if (goldWeight < 0
                || itemWeight < 0
                || weightPerProgress <= 0
                || goldWeight + itemWeight > weightPerProgress
                || goldWeights[0] <= 0f
                || float.IsNaN(goldWeights[0])
                || float.IsInfinity(goldWeights[0])
                || difficultyPoints[0] < 0
                || difficultyPoints[1] < 0)
            {
                return CreateUnavailable();
            }

            var ultimateRewards = new Dictionary<
                int,
                BloodAltarUltimateRewardProbability>();
            for (var index = 0; index < ultimateValues.Count; index += 3)
            {
                var point = ultimateValues[index];
                var item1251Probability = ultimateValues[index + 1];
                var item1252Probability = ultimateValues[index + 2];
                if (point <= 0
                    || item1251Probability < 0
                    || item1252Probability < 0
                    || item1251Probability + item1252Probability > 100
                    || ultimateRewards.ContainsKey(point))
                {
                    return CreateUnavailable();
                }
                ultimateRewards.Add(
                    point,
                    new BloodAltarUltimateRewardProbability(
                        point,
                        item1251Probability,
                        item1252Probability));
            }

            return new BloodAltarRewardDefinition(
                goldWeight,
                itemWeight,
                weightPerProgress,
                goldWeights[0],
                rewardExperienceWeights.ToArray(),
                entryCountExperienceWeights.ToArray(),
                difficultyPoints.ToArray(),
                ultimateRewards);
        }

        private static BloodAltarRewardDefinition CreateUnavailable()
            => new BloodAltarRewardDefinition(
                0,
                0,
                0,
                0f,
                Array.Empty<float>(),
                Array.Empty<float>(),
                Array.Empty<int>(),
                new Dictionary<int, BloodAltarUltimateRewardProbability>());

        private static string ReadSection(string text, string tagName)
        {
            var tag = "[" + tagName + "]";
            var start = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;
            start += tag.Length;

            var closeTag = "[/" + tagName + "]";
            var close = text.IndexOf(
                closeTag,
                start,
                StringComparison.OrdinalIgnoreCase);
            var nextTag = text.IndexOf('[', start);
            var end = close >= 0 && (nextTag < 0 || close <= nextTag)
                ? close
                : nextTag;
            if (end < 0)
                end = text.Length;
            return text.Substring(start, end - start);
        }

        private static List<int> ParseInts(string section)
        {
            var values = new List<int>();
            foreach (var token in SplitTokens(section))
            {
                if (!int.TryParse(
                        token,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    return new List<int>();
                }
                values.Add(value);
            }
            return values;
        }

        private static List<float> ParseFloats(string section)
        {
            var values = new List<float>();
            foreach (var token in SplitTokens(section))
            {
                if (!float.TryParse(
                        token,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value)
                    || float.IsNaN(value)
                    || float.IsInfinity(value))
                {
                    return new List<float>();
                }
                values.Add(value);
            }
            return values;
        }

        private static string[] SplitTokens(string section)
            => (section ?? string.Empty).Split(
                new[] { ' ', '\t', '\r', '\n', '`' },
                StringSplitOptions.RemoveEmptyEntries);
    }
}
