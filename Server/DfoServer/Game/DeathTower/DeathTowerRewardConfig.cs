using System;
using System.Collections.Generic;
using System.Globalization;
using DfoServer.GameWorld;

namespace DfoServer.Game.DeathTower
{
    public enum DeathTowerRewardProfile
    {
        Standard = 0,
        Illusion = 1,
    }

    public sealed class DeathTowerRewardConfig
    {
        internal const int MaximumRewardProgress = 10;

        private static readonly object Sync = new object();
        private static DeathTowerRewardConfig _cached;

        private readonly float[] _standardExpWeights;
        private readonly int[] _standardRewardCardCounts;
        private readonly float[] _illusionExpWeights;
        private readonly int[] _illusionRewardCardCounts;

        private DeathTowerRewardConfig(
            int goldCandidateWeight,
            int itemCandidateWeight,
            int rewardWeightPerProgress,
            float goldAmountWeight,
            float[] standardExpWeights,
            int[] standardRewardCardCounts,
            float[] illusionExpWeights,
            int[] illusionRewardCardCounts)
        {
            GoldCandidateWeight = goldCandidateWeight;
            ItemCandidateWeight = itemCandidateWeight;
            RewardWeightPerProgress = rewardWeightPerProgress;
            GoldAmountWeight = goldAmountWeight;
            _standardExpWeights = standardExpWeights ?? Array.Empty<float>();
            _standardRewardCardCounts = standardRewardCardCounts
                ?? Array.Empty<int>();
            _illusionExpWeights = illusionExpWeights ?? Array.Empty<float>();
            _illusionRewardCardCounts = illusionRewardCardCounts
                ?? Array.Empty<int>();
        }

        public int GoldCandidateWeight { get; }
        public int ItemCandidateWeight { get; }
        public int RewardWeightPerProgress { get; }
        public int RewardRollScale =>
            RewardWeightPerProgress * MaximumRewardProgress;
        public float GoldAmountWeight { get; }
        public bool IsAvailable =>
            GoldCandidateWeight >= 0
            && ItemCandidateWeight >= 0
            && RewardWeightPerProgress > 0
            && GoldCandidateWeight + ItemCandidateWeight
                <= RewardWeightPerProgress
            && GoldAmountWeight > 0
            && _standardExpWeights.Length > 0
            && _standardRewardCardCounts.Length > 0
            && _illusionExpWeights.Length > 0
            && _illusionRewardCardCounts.Length > 0;

        internal int StandardExpWeightCount => _standardExpWeights.Length;
        internal int StandardRewardCardCount =>
            _standardRewardCardCounts.Length;
        internal int IllusionExpWeightCount => _illusionExpWeights.Length;
        internal int IllusionRewardCardCount =>
            _illusionRewardCardCounts.Length;

        public static DeathTowerRewardConfig Load()
        {
            lock (Sync)
            {
                if (_cached != null)
                    return _cached;

                try
                {
                    var text = PvfArchiveAccessor.ReadText("etc/deathtower.etc");
                    _cached = Parse(text);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[DeathTower] reward config load failed: {ex.Message}");
                    _cached = CreateUnavailable();
                }

                return _cached;
            }
        }

        internal static DeathTowerRewardConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return CreateUnavailable();

            var probabilities = ParseInts(ReadSection(text, "reward item prob"));
            var goldWeights = ParseFloats(ReadSection(text, "reward gold weight"));
            var standardExpWeights = ParseFloats(
                ReadSection(text, "reward exp weight"));
            var standardRewardCardCounts = ParseInts(
                ReadSection(text, "reward card num"));
            var illusionExpWeights = ParseFloats(
                ReadSection(text, "illusion reward exp weight"));
            var illusionRewardCardCounts = ParseInts(
                ReadSection(text, "illusion reward card num"));

            if (probabilities.Count < 3
                || goldWeights.Count == 0
                || standardExpWeights.Count == 0
                || standardRewardCardCounts.Count == 0
                || illusionExpWeights.Count == 0
                || illusionRewardCardCounts.Count == 0)
            {
                return CreateUnavailable();
            }

            var goldCandidateWeight = probabilities[0];
            var itemCandidateWeight = probabilities[1];
            var rewardWeightPerProgress = probabilities[2];
            if (goldCandidateWeight < 0
                || itemCandidateWeight < 0
                || rewardWeightPerProgress <= 0
                || goldCandidateWeight + itemCandidateWeight
                    > rewardWeightPerProgress
                || goldWeights[0] <= 0)
            {
                return CreateUnavailable();
            }

            return new DeathTowerRewardConfig(
                goldCandidateWeight,
                itemCandidateWeight,
                rewardWeightPerProgress,
                goldWeights[0],
                standardExpWeights.ToArray(),
                standardRewardCardCounts.ToArray(),
                illusionExpWeights.ToArray(),
                illusionRewardCardCounts.ToArray());
        }

        internal float GetExpWeight(
            DeathTowerRewardProfile profile,
            int clearedFloorCount)
        {
            var values = GetProfile(
                profile,
                _standardExpWeights,
                _illusionExpWeights);
            return GetFloorValue(values, clearedFloorCount, 0f);
        }

        internal int GetRewardCardCount(
            DeathTowerRewardProfile profile,
            int clearedFloorCount)
        {
            var values = GetProfile(
                profile,
                _standardRewardCardCounts,
                _illusionRewardCardCounts);
            return Math.Max(0, GetFloorValue(values, clearedFloorCount, 0));
        }

        internal DeathTowerRewardCandidateKind ClassifyCandidate(
            int clearedFloorCount,
            int roll)
        {
            if (!IsAvailable)
                throw new InvalidOperationException(
                    "Death tower reward configuration is unavailable.");
            if (roll < 0 || roll >= RewardRollScale)
                throw new ArgumentOutOfRangeException(nameof(roll));

            var progress = Math.Min(
                MaximumRewardProgress,
                Math.Max(0, clearedFloorCount));
            var goldThreshold = progress * GoldCandidateWeight;
            if (roll < goldThreshold)
                return DeathTowerRewardCandidateKind.Gold;

            var itemThreshold = progress
                * (GoldCandidateWeight + ItemCandidateWeight);
            return roll < itemThreshold
                ? DeathTowerRewardCandidateKind.Item
                : DeathTowerRewardCandidateKind.Empty;
        }

        private static DeathTowerRewardConfig CreateUnavailable()
        {
            return new DeathTowerRewardConfig(
                0,
                0,
                0,
                0f,
                Array.Empty<float>(),
                Array.Empty<int>(),
                Array.Empty<float>(),
                Array.Empty<int>());
        }

        private static T[] GetProfile<T>(
            DeathTowerRewardProfile profile,
            T[] standard,
            T[] illusion)
        {
            switch (profile)
            {
                case DeathTowerRewardProfile.Standard:
                    return standard ?? Array.Empty<T>();
                case DeathTowerRewardProfile.Illusion:
                    return illusion ?? Array.Empty<T>();
                default:
                    return Array.Empty<T>();
            }
        }

        private static T GetFloorValue<T>(
            T[] values,
            int clearedFloorCount,
            T unavailable)
        {
            if (values == null
                || values.Length == 0
                || clearedFloorCount <= 0)
            {
                return unavailable;
            }

            var index = Math.Min(clearedFloorCount - 1, values.Length - 1);
            return values[index];
        }

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
                if (int.TryParse(
                        token,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    values.Add(value);
                }
            }
            return values;
        }

        private static List<float> ParseFloats(string section)
        {
            var values = new List<float>();
            foreach (var token in SplitTokens(section))
            {
                if (float.TryParse(
                        token,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    values.Add(value);
                }
            }
            return values;
        }

        private static string[] SplitTokens(string section)
        {
            return (section ?? string.Empty).Split(
                new[] { ' ', '\t', '\r', '\n', '`' },
                StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
