using System;
using System.Threading;

namespace DfoServer.Game.Dungeon
{
    internal static class DungeonIdentityGenerator
    {
        private static long _instanceId;
        private static long _runId;
        private static long _roomId;

        internal static long NextInstanceId() => Next(ref _instanceId);
        internal static long NextRunId() => Next(ref _runId);
        internal static long NextRoomId() => Next(ref _roomId);

        private static long Next(ref long value)
        {
            var next = Interlocked.Increment(ref value);
            if (next > 0)
                return next;

            throw new InvalidOperationException("Dungeon identity sequence exhausted.");
        }
    }

    public readonly struct DungeonInstanceIdentity : IEquatable<DungeonInstanceIdentity>
    {
        public DungeonInstanceIdentity(long partyDungeonInstanceId)
        {
            PartyDungeonInstanceId = partyDungeonInstanceId;
        }

        public long PartyDungeonInstanceId { get; }
        public bool IsValid => PartyDungeonInstanceId > 0;

        public bool Equals(DungeonInstanceIdentity other) =>
            PartyDungeonInstanceId == other.PartyDungeonInstanceId;

        public override bool Equals(object obj) =>
            obj is DungeonInstanceIdentity other && Equals(other);

        public override int GetHashCode() => PartyDungeonInstanceId.GetHashCode();
    }

    public readonly struct DungeonParticipantRunIdentity
        : IEquatable<DungeonParticipantRunIdentity>
    {
        public DungeonParticipantRunIdentity(
            DungeonInstanceIdentity instance,
            long runId,
            long runGeneration)
        {
            Instance = instance;
            RunId = runId;
            RunGeneration = runGeneration;
        }

        public DungeonInstanceIdentity Instance { get; }
        public long RunId { get; }
        public long RunGeneration { get; }
        public bool IsValid => Instance.IsValid && RunId > 0 && RunGeneration > 0;

        public bool Equals(DungeonParticipantRunIdentity other) =>
            Instance.Equals(other.Instance)
            && RunId == other.RunId
            && RunGeneration == other.RunGeneration;

        public override bool Equals(object obj) =>
            obj is DungeonParticipantRunIdentity other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Instance, RunId, RunGeneration);
    }

    public readonly struct DungeonRunIdentity : IEquatable<DungeonRunIdentity>
    {
        public DungeonRunIdentity(
            long partyDungeonInstanceId,
            long runId,
            long runGeneration)
        {
            PartyDungeonInstanceId = partyDungeonInstanceId;
            RunId = runId;
            RunGeneration = runGeneration;
        }

        public long PartyDungeonInstanceId { get; }
        public long RunId { get; }
        public long RunGeneration { get; }
        public DungeonInstanceIdentity InstanceIdentity =>
            new DungeonInstanceIdentity(PartyDungeonInstanceId);
        public DungeonParticipantRunIdentity ParticipantIdentity =>
            new DungeonParticipantRunIdentity(
                InstanceIdentity,
                RunId,
                RunGeneration);
        public bool IsValid => PartyDungeonInstanceId > 0 && RunId > 0 && RunGeneration > 0;

        public bool Equals(DungeonRunIdentity other) =>
            PartyDungeonInstanceId == other.PartyDungeonInstanceId
            && RunId == other.RunId
            && RunGeneration == other.RunGeneration;

        public override bool Equals(object obj) =>
            obj is DungeonRunIdentity other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(PartyDungeonInstanceId, RunId, RunGeneration);
    }

    public readonly struct DungeonRoomIdentity : IEquatable<DungeonRoomIdentity>
    {
        public DungeonRoomIdentity(
            DungeonInstanceIdentity instance,
            long roomInstanceId)
        {
            Instance = instance;
            RoomInstanceId = roomInstanceId;
        }

        public DungeonInstanceIdentity Instance { get; }
        public long RoomInstanceId { get; }
        public bool IsValid => Instance.IsValid && RoomInstanceId > 0;

        public bool Equals(DungeonRoomIdentity other) =>
            Instance.Equals(other.Instance)
            && RoomInstanceId == other.RoomInstanceId;

        public override bool Equals(object obj) =>
            obj is DungeonRoomIdentity other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Instance, RoomInstanceId);
    }

    public readonly struct DungeonParticipantRoomIdentity
        : IEquatable<DungeonParticipantRoomIdentity>
    {
        public DungeonParticipantRoomIdentity(
            DungeonRunIdentity run,
            DungeonRoomIdentity room)
        {
            Run = run;
            Room = room;
        }

        public DungeonRunIdentity Run { get; }
        public DungeonRoomIdentity Room { get; }
        public long RoomInstanceId => Room.RoomInstanceId;
        public bool IsValid => Run.IsValid
            && Room.IsValid
            && Run.InstanceIdentity.Equals(Room.Instance);

        public bool Equals(DungeonParticipantRoomIdentity other) =>
            Run.Equals(other.Run) && Room.Equals(other.Room);

        public override bool Equals(object obj) =>
            obj is DungeonParticipantRoomIdentity other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Run, Room);
    }
}
