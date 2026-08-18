using System;
using System.Collections.Generic;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class DungeonPermissionBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => (ushort)NotiPacketTypeA21.DUNGEON_PERMISSION;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var permissions = snapshot.InitializationSnapshot.DungeonPermissions;
            body = BuildEntries(permissions);
            return true;
        }

        internal static byte[] BuildEntries(
            IReadOnlyList<DungeonPermissionEntrySnapshot> permissions)
        {
            var persistent = DungeonPermissionProjector.ProjectForClient(
                permissions);

            var count = persistent.Count;
            var body = new byte[2 + count * 5];
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)count), 0, body, 0, 2);
            for (var i = 0; i < count; i++)
            {
                var off = 2 + i * 5;
                Buffer.BlockCopy(BitConverter.GetBytes((int)persistent[i].DungeonId), 0, body, off, 4);
                body[off + 4] = persistent[i].ClearState;
            }
            return body;
        }
    }
}
