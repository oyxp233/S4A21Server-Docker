using DfoServer.Game.Dungeon;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Network adapter: resolves the frozen entry roster to session-owned
    // inventory facts. Domain calculation consumes only the resulting snapshot.
    internal sealed class DungeonEntryExperienceBonusPlan
    {
        private readonly Dictionary<int, CapturedParticipant> _participants;

        private DungeonEntryExperienceBonusPlan(
            int partyMemberCount,
            bool partyHasEquippedAvatar,
            ChannelExperienceSelection channelExperience,
            Dictionary<int, CapturedParticipant> participants)
        {
            PartyMemberCount = Math.Max(1, Math.Min(4, partyMemberCount));
            PartyHasEquippedAvatar = partyHasEquippedAvatar;
            ChannelExperience = channelExperience
                ?? ChannelExperienceSelection.None;
            _participants = participants
                ?? new Dictionary<int, CapturedParticipant>();
        }

        internal int PartyMemberCount { get; }
        internal bool PartyHasEquippedAvatar { get; }
        internal ChannelExperienceSelection ChannelExperience { get; }

        internal static DungeonEntryExperienceBonusPlan Capture(
            EnhancedClientSession leader,
            Party party,
            ISessionDirectory sessions,
            int partyMemberCount,
            int dungeonId)
        {
            var participants = new Dictionary<int, CapturedParticipant>();
            CaptureParticipant(leader, expectedSessionId: null, participants);

            if (party != null)
            {
                foreach (var member in party.MembersBySlot())
                {
                    EnhancedClientSession candidate = null;
                    if (leader?.Player?.CharacterId == member.CharacterId)
                    {
                        candidate = leader;
                    }
                    else
                    {
                        sessions?.TryGet(member.CharacterId, out candidate);
                    }

                    CaptureParticipant(
                        candidate,
                        member.SessionId == Guid.Empty
                            ? null
                            : member.SessionId,
                        participants);
                }
            }

            var channelExperience = ChannelExperienceSelection.None;
            if (leader != null
                && GameNetworkConfig.TryResolveGameChannel(
                    leader.ListenerPort,
                    out var channel))
            {
                channelExperience = ChannelExperienceDefinitionCatalog.Resolve(
                    channel.ChannelId,
                    dungeonId);
            }

            var partyHasEquippedAvatar = false;
            foreach (var participant in participants.Values)
            {
                if (!participant.Facts.HasEquippedAvatar)
                    continue;
                partyHasEquippedAvatar = true;
                break;
            }

            return new DungeonEntryExperienceBonusPlan(
                partyMemberCount,
                partyHasEquippedAvatar,
                channelExperience,
                participants);
        }

        internal DungeonParticipantExperienceBonusSnapshot ForParticipant(
            EnhancedClientSession session)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || !_participants.TryGetValue(characterId, out var participant)
                || participant.SessionId != session.SessionId)
            {
                return DungeonParticipantExperienceBonusSnapshot.None;
            }

            var hasEquippedCreature = participant.FactsCaptured
                && participant.Facts.HasEquippedCreature;
            return new DungeonParticipantExperienceBonusSnapshot(
                PartyMemberCount,
                PartyHasEquippedAvatar,
                hasEquippedCreature,
                ChannelExperience.ChannelId,
                ChannelExperience.ChannelType,
                ChannelExperience.BonusRate);
        }

        private static void CaptureParticipant(
            EnhancedClientSession session,
            Guid? expectedSessionId,
            IDictionary<int, CapturedParticipant> participants)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || (expectedSessionId.HasValue
                    && session.SessionId != expectedSessionId.Value)
                )
            {
                return;
            }

            var captured = DungeonParticipantExperienceBonusSnapshotCapture
                .TryCaptureOwned(
                    session.SessionId,
                    characterId,
                    out var facts);
            participants[characterId] = new CapturedParticipant(
                session.SessionId,
                captured ? facts : default,
                captured);
        }

        private readonly struct CapturedParticipant
        {
            internal CapturedParticipant(
                Guid sessionId,
                DungeonParticipantEquipmentBonusFacts facts,
                bool factsCaptured)
            {
                SessionId = sessionId;
                Facts = facts;
                FactsCaptured = factsCaptured;
            }

            internal Guid SessionId { get; }
            internal DungeonParticipantEquipmentBonusFacts Facts { get; }
            internal bool FactsCaptured { get; }
        }
    }
}
