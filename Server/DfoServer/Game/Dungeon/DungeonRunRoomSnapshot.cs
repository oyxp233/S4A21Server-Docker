using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonRunRoomSnapshot
    {
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
        }

        internal DungeonRunIdentity RunIdentity { get; }
        internal DungeonRoomIdentity RoomIdentity { get; }
        internal RoomKey RoomKey { get; }
        internal ushort RoomStartSequence { get; }
        internal IReadOnlyList<GameWorld.Dungeon.MonsterSumInfo> Monsters { get; }
        internal RoomState RoomState { get; }
    }
}
