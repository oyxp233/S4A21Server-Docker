using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonParticipantAttachmentState
    {
        Active = 0,
        Detached = 1,
        Cancelled = 2,
        Expired = 3,
        Terminated = 4,
    }

    internal sealed class DungeonParticipantAttachmentOptions
    {
        internal static DungeonParticipantAttachmentOptions Default { get; } =
            new DungeonParticipantAttachmentOptions(
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(2));

        internal DungeonParticipantAttachmentOptions(
            TimeSpan hardTimeout,
            TimeSpan idleTimeout)
        {
            if (hardTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(hardTimeout));
            if (idleTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(idleTimeout));

            HardTimeout = hardTimeout;
            IdleTimeout = idleTimeout;
        }

        internal TimeSpan HardTimeout { get; }
        internal TimeSpan IdleTimeout { get; }
    }

    internal sealed class DungeonParticipantRegistration
    {
        internal DungeonParticipantRegistration(
            int accountId,
            int characterId,
            ushort participantUserId,
            int partyId,
            Guid sessionId,
            DungeonRun run)
        {
            AccountId = accountId;
            CharacterId = characterId;
            ParticipantUserId = participantUserId;
            PartyId = partyId;
            SessionId = sessionId;
            Run = run;
        }

        internal int AccountId { get; }
        internal int CharacterId { get; }
        internal ushort ParticipantUserId { get; }
        internal int PartyId { get; }
        internal Guid SessionId { get; }
        internal DungeonRun Run { get; }
    }

    internal sealed class DungeonParticipantAttachmentSnapshot
    {
        internal DungeonParticipantAttachmentSnapshot(
            int accountId,
            int characterId,
            ushort participantUserId,
            int partyId,
            long attachmentGeneration,
            DungeonParticipantAttachmentState state,
            DungeonRun run,
            DungeonRunIdentity runIdentity,
            DungeonParticipantRoomIdentity roomIdentity,
            DateTime detachedUtc,
            DateTime hardExpiresUtc,
            DateTime idleExpiresUtc,
            IReadOnlyList<ushort> participantUserIds)
        {
            AccountId = accountId;
            CharacterId = characterId;
            ParticipantUserId = participantUserId;
            PartyId = partyId;
            AttachmentGeneration = attachmentGeneration;
            State = state;
            Run = run;
            RunIdentity = runIdentity;
            RoomIdentity = roomIdentity;
            DetachedUtc = detachedUtc;
            HardExpiresUtc = hardExpiresUtc;
            IdleExpiresUtc = idleExpiresUtc;
            ParticipantUserIds = participantUserIds ?? Array.Empty<ushort>();
        }

        internal int AccountId { get; }
        internal int CharacterId { get; }
        internal ushort ParticipantUserId { get; }
        internal int PartyId { get; }
        internal long AttachmentGeneration { get; }
        internal DungeonParticipantAttachmentState State { get; }
        internal DungeonRun Run { get; }
        internal DungeonRunIdentity RunIdentity { get; }
        internal DungeonParticipantRoomIdentity RoomIdentity { get; }
        internal DateTime DetachedUtc { get; }
        internal DateTime HardExpiresUtc { get; }
        internal DateTime IdleExpiresUtc { get; }
        internal IReadOnlyList<ushort> ParticipantUserIds { get; }
        internal DateTime ExpiresUtc =>
            HardExpiresUtc <= IdleExpiresUtc ? HardExpiresUtc : IdleExpiresUtc;
    }

    internal enum DungeonAttachmentOperationStatus
    {
        Success = 0,
        NotFound = 1,
        IdentityMismatch = 2,
        InvalidState = 3,
        StaleGeneration = 4,
        Expired = 5,
        TargetParticipantMissing = 6,
        PartyUnavailable = 7,
    }
}
