using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal readonly struct EnterSelectDungeonRequest
    {
        internal const int MinimumBodyLength = 4;

        private EnterSelectDungeonRequest(
            int dungeonId,
            int bodyLength,
            bool hasNonZeroTrailingBytes)
        {
            DungeonId = dungeonId;
            BodyLength = bodyLength;
            HasNonZeroTrailingBytes = hasNonZeroTrailingBytes;
        }

        internal int DungeonId { get; }
        internal int BodyLength { get; }
        internal int TrailingLength => BodyLength - MinimumBodyLength;
        internal bool HasNonZeroTrailingBytes { get; }

        internal static bool TryParse(
            byte[] body,
            out EnterSelectDungeonRequest request)
        {
            request = default;
            if (body == null || body.Length < MinimumBodyLength)
                return false;

            var wireDungeonId = BitConverter.ToUInt32(body, 0);
            if (wireDungeonId > int.MaxValue)
                return false;

            var hasNonZeroTrailingBytes = false;
            for (var i = MinimumBodyLength; i < body.Length; i++)
            {
                if (body[i] == 0)
                    continue;

                hasNonZeroTrailingBytes = true;
                break;
            }

            request = new EnterSelectDungeonRequest(
                (int)wireDungeonId,
                body.Length,
                hasNonZeroTrailingBytes);
            return true;
        }
    }
}
