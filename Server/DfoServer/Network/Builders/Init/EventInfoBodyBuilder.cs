using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    // NOTI 0x006C: u16 count, then count * (u16 eventIndex + 12 bytes), then u8 tail.
    public sealed class EventInfoBodyBuilder : IInitPacketBuilder
    {
        internal const ushort RaidChannelEventIndex = 0x00B5;
        internal const int EventDataLength = 12;

        public ushort NotiType => 0x006C;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            const ushort count = 1;
            const int entrySize = sizeof(ushort) + EventDataLength;

            body = new byte[sizeof(ushort) + entrySize + sizeof(byte)];
            Buffer.BlockCopy(BitConverter.GetBytes(count), 0, body, 0, sizeof(ushort));
            Buffer.BlockCopy(
                BitConverter.GetBytes(RaidChannelEventIndex),
                0,
                body,
                sizeof(ushort),
                sizeof(ushort));

            return true;
        }
    }
}
