using System;
using DfoServer.Game.ExpertJob;

namespace DfoServer.Network.Parsers.ExpertJob
{
    internal static class CreateExpertJobStoreRequest
    {
        private const int FixedBodyLength = 15;
        private const int MaxNameLength = 255;

        internal static bool TryParse(byte[] body, out ExpertJobStoreCreateCommand command)
        {
            command = null;
            if (body == null || body.Length < FixedBodyLength)
                return false;

            var nameLength = BitConverter.ToInt32(body, 1);
            if (nameLength < 0 || nameLength > MaxNameLength || body.Length != FixedBodyLength + nameLength)
                return false;

            var offset = 5;
            var nameBytes = new byte[nameLength];
            if (nameLength > 0)
                Buffer.BlockCopy(body, offset, nameBytes, 0, nameLength);
            offset += nameLength;

            command = new ExpertJobStoreCreateCommand
            {
                Kind = (ExpertJobStoreKind)body[0],
                NameBytes = nameBytes,
                Cost = BitConverter.ToInt32(body, offset),
                PositionX = BitConverter.ToInt16(body, offset + 4),
                PositionY = BitConverter.ToInt16(body, offset + 6),
                Direction = BitConverter.ToInt16(body, offset + 8),
            };
            return true;
        }
    }
}
