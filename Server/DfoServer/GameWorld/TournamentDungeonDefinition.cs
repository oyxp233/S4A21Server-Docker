using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.GameWorld
{
    internal enum TournamentActorKind : byte
    {
        Monster = 0,
        AiCharacter = 1,
    }

    internal sealed class TournamentActorDefinition
    {
        internal TournamentActorDefinition(
            int partyCount,
            TournamentActorKind kind,
            int code,
            int strength,
            string name,
            byte level,
            byte actorType)
        {
            PartyCount = partyCount;
            Kind = kind;
            Code = code;
            Strength = strength;
            Name = name ?? string.Empty;
            Level = level;
            ActorType = actorType;
        }

        internal int PartyCount { get; }
        internal TournamentActorKind Kind { get; }
        internal int Code { get; }
        internal int Strength { get; }
        internal string Name { get; }
        internal byte Level { get; }
        internal byte ActorType { get; }
    }

    internal readonly struct TournamentStartAreaDefinition
    {
        internal TournamentStartAreaDefinition(
            int partyCount,
            int x,
            int y,
            int direction)
        {
            PartyCount = partyCount;
            X = x;
            Y = y;
            Direction = direction;
        }

        internal int PartyCount { get; }
        internal int X { get; }
        internal int Y { get; }
        internal int Direction { get; }
    }

    internal readonly struct TournamentEntryItemDefinition
    {
        internal TournamentEntryItemDefinition(
            int itemId,
            int count,
            bool consumeOnEntry)
        {
            ItemId = itemId;
            Count = count;
            ConsumeOnEntry = consumeOnEntry;
        }

        internal int ItemId { get; }
        internal int Count { get; }
        internal bool ConsumeOnEntry { get; }
    }

    internal readonly struct TournamentResultCardDefinition
    {
        internal TournamentResultCardDefinition(
            int resultKey,
            int goldWeight,
            int itemWeight,
            int emptyWeight)
        {
            ResultKey = resultKey;
            GoldWeight = goldWeight;
            ItemWeight = itemWeight;
            EmptyWeight = emptyWeight;
        }

        internal int ResultKey { get; }
        internal int GoldWeight { get; }
        internal int ItemWeight { get; }
        internal int EmptyWeight { get; }
        internal long TotalWeight => (long)GoldWeight + ItemWeight + EmptyWeight;
    }

    internal readonly struct TournamentRewardItemRateDefinition
    {
        internal TournamentRewardItemRateDefinition(
            int itemId,
            int weight,
            int count)
        {
            ItemId = itemId;
            Weight = weight;
            Count = count;
        }

        internal int ItemId { get; }
        internal int Weight { get; }
        internal int Count { get; }
    }

    internal sealed class TournamentDungeonDefinition
    {
        private readonly IReadOnlyDictionary<int, uint> _experienceByRound;
        private readonly IReadOnlyDictionary<int, TournamentResultCardDefinition>
            _resultCards;

        internal TournamentDungeonDefinition(
            int dungeonId,
            int mapId,
            byte basicLevel,
            int partyLimit,
            int coinLimit,
            int roundFatigue,
            float clearRewardGoldRate,
            IReadOnlyDictionary<int, uint> experienceByRound,
            IReadOnlyDictionary<int, TournamentResultCardDefinition> resultCards,
            IReadOnlyList<TournamentRewardItemRateDefinition> rewardItemRates,
            IReadOnlyList<TournamentActorDefinition> candidates,
            IReadOnlyList<TournamentStartAreaDefinition> startAreas,
            IReadOnlyList<TournamentEntryItemDefinition> entryItems)
        {
            DungeonId = dungeonId;
            MapId = mapId;
            BasicLevel = basicLevel;
            PartyLimit = partyLimit;
            CoinLimit = coinLimit;
            RoundFatigue = roundFatigue;
            ClearRewardGoldRate = clearRewardGoldRate;
            _experienceByRound = experienceByRound
                ?? new ReadOnlyDictionary<int, uint>(
                    new Dictionary<int, uint>());
            _resultCards = resultCards
                ?? new ReadOnlyDictionary<int, TournamentResultCardDefinition>(
                    new Dictionary<int, TournamentResultCardDefinition>());
            RewardItemRates = Freeze(rewardItemRates);
            Candidates = Freeze(candidates);
            StartAreas = Freeze(startAreas);
            EntryItems = Freeze(entryItems);
        }

        internal int DungeonId { get; }
        internal int MapId { get; }
        internal byte BasicLevel { get; }
        internal int PartyLimit { get; }
        internal int CoinLimit { get; }
        internal int RoundFatigue { get; }
        internal float ClearRewardGoldRate { get; }
        internal IReadOnlyList<TournamentRewardItemRateDefinition> RewardItemRates { get; }
        internal IReadOnlyList<TournamentActorDefinition> Candidates { get; }
        internal IReadOnlyList<TournamentStartAreaDefinition> StartAreas { get; }
        internal IReadOnlyList<TournamentEntryItemDefinition> EntryItems { get; }

        internal uint GetClearRewardExperience(int completedRounds)
            => _experienceByRound.TryGetValue(completedRounds, out var value)
                ? value
                : 0;

        internal bool TryGetResultCard(
            int resultKey,
            out TournamentResultCardDefinition resultCard)
            => _resultCards.TryGetValue(resultKey, out resultCard);

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

    internal static class TournamentDungeonDefinitionCatalog
    {
        private static readonly ConcurrentDictionary<long, TournamentDungeonDefinition>
            Definitions = new ConcurrentDictionary<long, TournamentDungeonDefinition>();

        internal static bool IsTournamentDungeon(int dungeonId)
        {
            try
            {
                return dungeonId > 0
                    && DungeonCatalog.GetDungeonFile(dungeonId).TournamentDungeon;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryResolve(
            int dungeonId,
            int mapId,
            out TournamentDungeonDefinition definition,
            out string failureReason)
        {
            definition = null;
            failureReason = string.Empty;
            if (dungeonId <= 0 || dungeonId > ushort.MaxValue || mapId <= 0)
            {
                failureReason = "dungeon or map id is outside the protocol range";
                return false;
            }

            var key = ((long)dungeonId << 32) | (uint)mapId;
            if (Definitions.TryGetValue(key, out definition))
                return true;

            try
            {
                if (!TryProject(
                        dungeonId,
                        mapId,
                        out var projected,
                        out failureReason))
                {
                    return false;
                }

                definition = Definitions.GetOrAdd(key, projected);
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        private static bool TryProject(
            int dungeonId,
            int mapId,
            out TournamentDungeonDefinition definition,
            out string failureReason)
        {
            definition = null;
            failureReason = string.Empty;
            var dungeon = DungeonCatalog.GetDungeonFile(dungeonId);
            if (!dungeon.TournamentDungeon)
            {
                failureReason = "dungeon is not marked as tournament";
                return false;
            }
            if (dungeon.BasisLevel <= 0 || dungeon.BasisLevel > byte.MaxValue)
            {
                failureReason = "tournament basic level is invalid";
                return false;
            }
            if (dungeon.LimitPartyCount < 1 || dungeon.LimitPartyCount > 2)
            {
                failureReason = "tournament party limit is unsupported";
                return false;
            }
            if (dungeon.CoinLimit < 0
                || dungeon.TournamentRoundFatigue < 0
                || dungeon.TournamentClearRewardGoldRate <= 0
                || float.IsNaN(dungeon.TournamentClearRewardGoldRate)
                || float.IsInfinity(dungeon.TournamentClearRewardGoldRate))
            {
                failureReason = "tournament DGN reward or round fields are incomplete";
                return false;
            }
            if (dungeon.TournamentRewardExperienceMalformed
                || dungeon.TournamentResultCardMalformed
                || dungeon.TournamentRewardItemRateMalformed)
            {
                failureReason = "tournament reward definition is malformed";
                return false;
            }

            var rewardExperience = new Dictionary<int, uint>();
            foreach (var reward in dungeon.TournamentClearRewardExperiences)
            {
                if (reward == null
                    || reward.CompletedRounds < 0
                    || reward.CompletedRounds > 4
                    || reward.Experience < 0
                    || reward.Experience > uint.MaxValue
                    || rewardExperience.ContainsKey(reward.CompletedRounds))
                {
                    failureReason = "tournament reward experience contains an invalid row";
                    return false;
                }

                rewardExperience.Add(
                    reward.CompletedRounds,
                    (uint)reward.Experience);
            }
            for (var round = 0; round <= 4; round++)
            {
                if (!rewardExperience.ContainsKey(round))
                {
                    failureReason = $"tournament reward experience misses round {round}";
                    return false;
                }
            }

            var resultCards = new Dictionary<int, TournamentResultCardDefinition>();
            foreach (var card in dungeon.TournamentResultCards)
            {
                if (card == null
                    || card.ResultKey < 0
                    || card.ResultKey > 5
                    || card.GoldWeight < 0
                    || card.ItemWeight < 0
                    || card.EmptyWeight < 0)
                {
                    failureReason = "tournament result card contains an invalid row";
                    return false;
                }

                var definitionCard = new TournamentResultCardDefinition(
                    card.ResultKey,
                    card.GoldWeight,
                    card.ItemWeight,
                    card.EmptyWeight);
                if (definitionCard.TotalWeight <= 0
                    || definitionCard.TotalWeight > int.MaxValue
                    || resultCards.ContainsKey(card.ResultKey))
                {
                    failureReason = "tournament result card weights are invalid";
                    return false;
                }
                resultCards.Add(card.ResultKey, definitionCard);
            }
            for (var resultKey = 0; resultKey <= 5; resultKey++)
            {
                if (!resultCards.ContainsKey(resultKey))
                {
                    failureReason =
                        $"tournament result card misses key {resultKey}";
                    return false;
                }
            }

            var rewardItemRates = new List<TournamentRewardItemRateDefinition>();
            long rewardItemWeightTotal = 0;
            foreach (var rate in dungeon.TournamentRewardItemRates)
            {
                if (rate == null
                    || rate.ItemId <= 0
                    || rate.Weight <= 0
                    || rate.Count <= 0)
                {
                    failureReason = "tournament reward item rate contains an invalid row";
                    return false;
                }

                rewardItemWeightTotal += rate.Weight;
                if (rewardItemWeightTotal > int.MaxValue)
                {
                    failureReason = "tournament reward item rate total exceeds protocol range";
                    return false;
                }
                rewardItemRates.Add(new TournamentRewardItemRateDefinition(
                    rate.ItemId,
                    rate.Weight,
                    rate.Count));
            }
            if (rewardItemRates.Count == 0 || rewardItemWeightTotal <= 0)
            {
                failureReason = "tournament reward item rate is missing";
                return false;
            }

            var map = DungeonMapCatalog.GetMapFile(mapId);
            if (map.DungeonId != dungeonId)
            {
                failureReason = "tournament MAP belongs to another dungeon";
                return false;
            }
            if (map.TournamentDefinitionMalformed)
            {
                failureReason = "tournament MAP definition is malformed";
                return false;
            }

            var candidates = new List<TournamentActorDefinition>(
                map.TournamentEnemyCandidates.Count);
            foreach (var candidate in map.TournamentEnemyCandidates)
            {
                if (candidate == null
                    || candidate.PartyCount < 1
                    || candidate.PartyCount > 2
                    || candidate.Code <= 0
                    || candidate.Strength < 0
                    || string.IsNullOrWhiteSpace(candidate.Name))
                {
                    failureReason = "tournament candidate contains an invalid field";
                    return false;
                }

                var level = (byte)dungeon.BasisLevel;
                var actorType = (byte)0;
                var kind = TournamentActorKind.Monster;
                if (candidate.IsApc)
                {
                    if (!DungeonActorTemplateProjector.TryGetAiCharacterLevel(
                            candidate.Code,
                            out level))
                    {
                        failureReason =
                            $"tournament APC definition is missing code={candidate.Code}";
                        return false;
                    }
                    kind = TournamentActorKind.AiCharacter;
                    actorType = 5;
                }

                candidates.Add(new TournamentActorDefinition(
                    candidate.PartyCount,
                    kind,
                    candidate.Code,
                    candidate.Strength,
                    candidate.Name,
                    level,
                    actorType));
            }

            var startAreas = new List<TournamentStartAreaDefinition>(
                map.TournamentStartAreas.Count);
            foreach (var area in map.TournamentStartAreas)
            {
                if (area == null
                    || area.PartyCount < 1
                    || area.PartyCount > 2
                    || area.X < 0
                    || area.X > ushort.MaxValue
                    || area.Y < 0
                    || area.Y > ushort.MaxValue
                    || area.Direction < 0
                    || area.Direction > byte.MaxValue)
                {
                    failureReason = "tournament start area contains an invalid field";
                    return false;
                }

                startAreas.Add(new TournamentStartAreaDefinition(
                    area.PartyCount,
                    area.X,
                    area.Y,
                    area.Direction));
            }

            var partyLimit = dungeon.LimitPartyCount;
            if (CountCandidates(candidates, partyLimit) < 15 * partyLimit)
            {
                failureReason =
                    $"tournament candidate pool is too small for party count {partyLimit}";
                return false;
            }
            if (CountStartAreas(startAreas, partyLimit) < 2)
            {
                failureReason =
                    $"tournament start areas are incomplete for party count {partyLimit}";
                return false;
            }

            var entryItems = new List<TournamentEntryItemDefinition>();
            foreach (var item in dungeon.RequiredItems)
            {
                if (item == null || item.ItemId <= 0 || item.Count <= 0)
                {
                    failureReason = "tournament entry item contains an invalid field";
                    return false;
                }

                entryItems.Add(new TournamentEntryItemDefinition(
                    item.ItemId,
                    item.Count,
                    item.ConsumeOnEntry));
            }

            definition = new TournamentDungeonDefinition(
                dungeonId,
                mapId,
                (byte)dungeon.BasisLevel,
                partyLimit,
                dungeon.CoinLimit,
                dungeon.TournamentRoundFatigue,
                dungeon.TournamentClearRewardGoldRate,
                new ReadOnlyDictionary<int, uint>(rewardExperience),
                new ReadOnlyDictionary<int, TournamentResultCardDefinition>(
                    resultCards),
                rewardItemRates,
                candidates,
                startAreas,
                entryItems);
            return true;
        }

        private static int CountCandidates(
            IReadOnlyList<TournamentActorDefinition> candidates,
            int partyCount)
        {
            var count = 0;
            foreach (var candidate in candidates)
            {
                if (candidate.PartyCount == partyCount)
                    count++;
            }
            return count;
        }

        private static int CountStartAreas(
            IReadOnlyList<TournamentStartAreaDefinition> areas,
            int partyCount)
        {
            var count = 0;
            foreach (var area in areas)
            {
                if (area.PartyCount == partyCount)
                    count++;
            }
            return count;
        }
    }
}
