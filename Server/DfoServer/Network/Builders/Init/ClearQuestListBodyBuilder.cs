using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public sealed class ClearQuestListBodyBuilder : IInitPacketBuilder
    {
        internal const int PayloadLength = 30000;

        public ushort NotiType => 0x0164;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            body = BuildBody(init.CharacInvisibleFalgs);
            return true;
        }

        internal static byte[] BuildBody(
            IEnumerable<CharacInvisibleFalgEntrySnapshot> flags)
        {
            var body = new byte[4 + PayloadLength];
            Buffer.BlockCopy(BitConverter.GetBytes(PayloadLength), 0, body, 0, 4);
            if (flags == null)
                return body;

            foreach (var entry in flags)
            {
                if (entry != null && entry.SlotIndex < PayloadLength)
                    body[4 + entry.SlotIndex] = entry.FlagValue;
            }
            return body;
        }

        internal static byte[] BuildBody(IReadOnlyDictionary<int, int> flags)
        {
            var body = new byte[4 + PayloadLength];
            Buffer.BlockCopy(BitConverter.GetBytes(PayloadLength), 0, body, 0, 4);
            if (flags == null)
                return body;

            foreach (var pair in flags)
            {
                if (pair.Key >= 0 && pair.Key < PayloadLength)
                    body[4 + pair.Key] = (byte)Math.Max(0, Math.Min(byte.MaxValue, pair.Value));
            }
            return body;
        }
    }
}
