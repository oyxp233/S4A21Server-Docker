using System;
using System.Collections.Generic;

namespace DfoServer.Network
{
    public static class GamePacketEnvelopeBuilder
    {
        public static byte[] Build(byte command, ushort type, byte[] body)
        {
            var buffer = new List<byte>();
            var bodySize = body != null ? body.Length : 0;

            buffer.Add(command);
            buffer.AddRange(BitConverter.GetBytes(type));
            // Server-to-client A21 packets use the 15-byte inbound envelope.
            buffer.AddRange(BitConverter.GetBytes(bodySize + 15));
            buffer.AddRange(new byte[4]);
            buffer.AddRange(new byte[4]);

            if (body != null && body.Length > 0)
                buffer.AddRange(body);

            return buffer.ToArray();
        }
    }
}
