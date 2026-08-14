using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    
    
    public sealed class SkillPointSlotBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x015F;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var entries = snapshot.InitializationSnapshot.SkillPointSlots;
            var count = entries.Count;
            body = new byte[1 + count * 3];
            body[0] = (byte)count;
            for (var i = 0; i < count; i++)
            {
                var off = 1 + i * 3;
                body[off] = entries[i].SkillType;
                body[off + 1] = (byte)(entries[i].Points & 0xFF);
                body[off + 2] = (byte)(entries[i].Points >> 8);
            }
            return true;
        }
    }
}
