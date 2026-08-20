using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonRunRoomSnapshot
    {
        private readonly HashSet<int> _mapOwnedPassiveObjectCodes;

        internal DungeonRunRoomSnapshot(
            DungeonRunIdentity runIdentity,
            DungeonRoomIdentity roomIdentity,
            RoomKey roomKey,
            ushort roomStartSequence,
            IReadOnlyList<GameWorld.Dungeon.MonsterSumInfo> monsters,
            RoomState roomState)
        {
            RunIdentity = runIdentity;
            RoomIdentity = roomIdentity;
            RoomKey = roomKey;
            RoomStartSequence = roomStartSequence;
            Monsters = monsters ?? Array.Empty<GameWorld.Dungeon.MonsterSumInfo>();
            RoomState = roomState;
            _mapOwnedPassiveObjectCodes = SnapshotMapOwnedPassiveObjectCodes(
                roomState?.Maze);
        }

        internal DungeonRunIdentity RunIdentity { get; }
        internal DungeonRoomIdentity RoomIdentity { get; }
        internal RoomKey RoomKey { get; }
        internal ushort RoomStartSequence { get; }
        internal IReadOnlyList<GameWorld.Dungeon.MonsterSumInfo> Monsters { get; }
        internal RoomState RoomState { get; }

        internal bool ContainsStaticActorSequence(ushort sequenceId)
        {
            var localIndex = sequenceId - RoomStartSequence;
            return localIndex >= 0 && localIndex < Monsters.Count;
        }

        internal bool ContainsMapOwnedPassiveObjectCode(int objectCode)
            => objectCode > 0
                && _mapOwnedPassiveObjectCodes.Contains(objectCode);

        private static HashSet<int> SnapshotMapOwnedPassiveObjectCodes(
            GameWorld.Dungeon.MazeSumInfo? maze)
        {
            var result = new HashSet<int>();
            if (!maze.HasValue)
                return result;

            var value = maze.Value;
            if (value.PassiveObjectCodes != null)
            {
                foreach (var objectCode in value.PassiveObjectCodes)
                {
                    if (objectCode > 0)
                        result.Add(objectCode);
                }
            }

            if (value.SpecialPassiveObjects != null)
            {
                foreach (var passiveObject in value.SpecialPassiveObjects)
                {
                    if (passiveObject?.ObjectCode > 0)
                        result.Add(passiveObject.ObjectCode);
                }
            }
            return result;
        }
    }
}
