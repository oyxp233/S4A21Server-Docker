using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon.Tournament
{
    internal enum TournamentRewardDeliveryState : byte
    {
        NotSelected = 0,
        Reserved = 1,
        Delivered = 2,
    }

    internal enum TournamentExperienceDeliveryState : byte
    {
        NotReserved = 0,
        Reserved = 1,
        Delivered = 2,
    }

    internal sealed class TournamentParticipantRewardState
    {
        internal const int CardTypeCount = 2;
        internal const int CardsPerType = 2;
        internal const int PartySlotCount = 4;
        internal const byte Unselected = byte.MaxValue;

        private readonly object _syncRoot = new object();
        private readonly ClearRewardGenerator.CardReward[,] _rewards =
            new ClearRewardGenerator.CardReward[CardTypeCount, CardsPerType];
        private readonly byte[] _selectedIndexes =
            { Unselected, Unselected };
        private readonly byte[] _cardCounts = new byte[CardTypeCount];
        private readonly TournamentRewardDeliveryState[] _deliveryStates =
            new TournamentRewardDeliveryState[CardTypeCount];
        private TournamentExperienceDeliveryState _experienceState;

        internal TournamentParticipantRewardState(
            IReadOnlyList<ClearRewardGenerator.CardReward> rewards,
            int partyCount,
            int localPartySlot,
            uint rewardExperience,
            byte completedRounds,
            bool completedAllRounds)
        {
            if (rewards == null
                || rewards.Count != CardTypeCount * CardsPerType)
            {
                throw new ArgumentException(
                    "Tournament reward state requires four cards.",
                    nameof(rewards));
            }
            if (partyCount < 1 || partyCount > PartySlotCount)
                throw new ArgumentOutOfRangeException(nameof(partyCount));
            if (localPartySlot < 0 || localPartySlot >= partyCount)
                throw new ArgumentOutOfRangeException(nameof(localPartySlot));

            PartyCount = (byte)partyCount;
            LocalPartySlot = (byte)localPartySlot;
            RewardExperience = rewardExperience;
            CompletedRounds = (byte)Math.Max(0, Math.Min(4, (int)completedRounds));
            CompletedAllRounds = completedAllRounds;
            _cardCounts[0] = (byte)CardsPerType;
            _cardCounts[1] = completedAllRounds
                ? (byte)CardsPerType
                : (byte)0;
            for (var type = 0; type < CardTypeCount; type++)
            {
                for (var index = 0; index < CardsPerType; index++)
                    _rewards[type, index] = rewards[type * CardsPerType + index];
            }
        }

        internal byte PartyCount { get; }
        internal byte LocalPartySlot { get; }
        internal uint RewardExperience { get; }
        internal byte CompletedRounds { get; }
        internal bool CompletedAllRounds { get; }
        internal bool ExperienceDelivered
        {
            get
            {
                lock (_syncRoot)
                    return _experienceState
                        == TournamentExperienceDeliveryState.Delivered;
            }
        }
        internal bool IsSelectionComplete
        {
            get
            {
                lock (_syncRoot)
                {
                    for (var type = 0; type < CardTypeCount; type++)
                    {
                        if (_cardCounts[type] > 0
                            && _deliveryStates[type]
                                != TournamentRewardDeliveryState.Delivered)
                            return false;
                    }
                    return true;
                }
            }
        }

        internal bool IsPartySlotPresent(int partySlot) =>
            partySlot >= 0 && partySlot < PartyCount;

        internal byte GetCardCount(int cardType)
        {
            ValidateCardType(cardType);
            return _cardCounts[cardType];
        }

        internal bool IsCardTypeEnabled(int cardType)
            => GetCardCount(cardType) > 0;

        internal bool IsCardTypeSelectionComplete(int cardType)
        {
            ValidateCardType(cardType);
            lock (_syncRoot)
            {
                return _cardCounts[cardType] == 0
                    || _deliveryStates[cardType]
                        == TournamentRewardDeliveryState.Delivered;
            }
        }

        internal ClearRewardGenerator.CardReward GetReward(
            int cardType,
            int cardIndex)
        {
            ValidateCard(cardType, cardIndex);
            lock (_syncRoot)
                return _rewards[cardType, cardIndex];
        }

        internal byte GetSelection(int cardType, int cardIndex)
        {
            ValidateCard(cardType, cardIndex);
            lock (_syncRoot)
            {
                return _selectedIndexes[cardType] == cardIndex
                    ? LocalPartySlot
                    : Unselected;
            }
        }

        internal bool TryReserveSelection(
            byte cardType,
            byte cardIndex,
            out ClearRewardGenerator.CardReward reward)
        {
            reward = default;
            if (cardType >= CardTypeCount
                || cardIndex >= _cardCounts[cardType])
                return false;

            lock (_syncRoot)
            {
                if (_deliveryStates[cardType]
                        != TournamentRewardDeliveryState.NotSelected)
                {
                    return false;
                }

                _selectedIndexes[cardType] = cardIndex;
                _deliveryStates[cardType] =
                    TournamentRewardDeliveryState.Reserved;
                reward = _rewards[cardType, cardIndex];
                return true;
            }
        }

        internal bool TryMarkDelivered(byte cardType, byte cardIndex)
        {
            if (cardType >= CardTypeCount
                || cardIndex >= _cardCounts[cardType])
                return false;

            lock (_syncRoot)
            {
                if (_selectedIndexes[cardType] != cardIndex
                    || _deliveryStates[cardType]
                        != TournamentRewardDeliveryState.Reserved)
                {
                    return false;
                }

                _deliveryStates[cardType] =
                    TournamentRewardDeliveryState.Delivered;
                return true;
            }
        }

        internal bool TryRollbackSelection(byte cardType, byte cardIndex)
        {
            if (cardType >= CardTypeCount
                || cardIndex >= _cardCounts[cardType])
                return false;

            lock (_syncRoot)
            {
                if (_selectedIndexes[cardType] != cardIndex
                    || _deliveryStates[cardType]
                        != TournamentRewardDeliveryState.Reserved)
                {
                    return false;
                }

                _selectedIndexes[cardType] = Unselected;
                _deliveryStates[cardType] =
                    TournamentRewardDeliveryState.NotSelected;
                return true;
            }
        }

        internal bool TryReserveExperience()
        {
            lock (_syncRoot)
            {
                if (_experienceState
                    != TournamentExperienceDeliveryState.NotReserved)
                    return false;
                _experienceState =
                    TournamentExperienceDeliveryState.Reserved;
                return true;
            }
        }

        internal bool TryMarkExperienceDelivered()
        {
            lock (_syncRoot)
            {
                if (_experienceState
                    != TournamentExperienceDeliveryState.Reserved)
                    return false;
                _experienceState =
                    TournamentExperienceDeliveryState.Delivered;
                return true;
            }
        }

        internal bool TryRollbackExperience()
        {
            lock (_syncRoot)
            {
                if (_experienceState
                    != TournamentExperienceDeliveryState.Reserved)
                    return false;
                _experienceState =
                    TournamentExperienceDeliveryState.NotReserved;
                return true;
            }
        }

        private void ValidateCard(int cardType, int cardIndex)
        {
            ValidateCardType(cardType);
            if (cardIndex < 0 || cardIndex >= _cardCounts[cardType])
                throw new ArgumentOutOfRangeException(nameof(cardIndex));
        }

        private static void ValidateCardType(int cardType)
        {
            if (cardType < 0 || cardType >= CardTypeCount)
                throw new ArgumentOutOfRangeException(nameof(cardType));
        }
    }
}
