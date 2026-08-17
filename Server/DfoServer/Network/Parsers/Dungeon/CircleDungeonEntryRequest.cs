using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal readonly struct CircleDungeonEntryRequest
    {
        internal const int BodySize = 8;

        internal CircleDungeonEntryRequest(
            uint dungeonId,
            uint circleQuestId)
        {
            DungeonId = dungeonId;
            CircleQuestId = circleQuestId;
        }

        internal uint DungeonId { get; }
        internal uint CircleQuestId { get; }

        internal static bool TryParse(
            byte[] body,
            out CircleDungeonEntryRequest request)
        {
            request = default;
            if (body == null || body.Length != BodySize)
                return false;

            request = new CircleDungeonEntryRequest(
                BitConverter.ToUInt32(body, 0),
                BitConverter.ToUInt32(body, 4));
            return true;
        }
    }
}
