using DfoServer.Game.SelectCharacter;
using System;

namespace DfoServer.Network.Builders
{
    public sealed class ClearQuestListBodyBuilder : IInitPacketBuilder
    {
        internal const int PayloadLength = 30000;

        public ushort NotiType => 0x0164;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            body = new byte[4 + PayloadLength];
            Buffer.BlockCopy(BitConverter.GetBytes(PayloadLength), 0, body, 0, 4);
            foreach (var entry in init.CharacInvisibleFalgs)
            {
                if (entry.SlotIndex < PayloadLength)
                    body[4 + entry.SlotIndex] = entry.FlagValue;
            }
            return true;
        }
    }
}
