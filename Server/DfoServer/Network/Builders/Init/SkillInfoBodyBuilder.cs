using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class SkillInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0013;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = BuildFrom(snapshot.InitializationSnapshot.SkillInfo);
            return true;
        }

        public static byte[] BuildFrom(SkillInfoSnapshot skill)
        {
            var writer = new GamePacketWriter();
            foreach (var page in skill.Pages)
            {
                writer.WriteUInt16(page.HeaderValue);
                writer.WriteByte((byte)page.Entries.Count);
                foreach (var entry in page.Entries)
                {
                    writer.WriteByte(entry.Slot);
                    writer.WriteUInt16(entry.SkillId);
                    writer.WriteByte(entry.Level);
                    writer.WriteByte((byte)entry.ExtraValues.Count);
                    foreach (var extraValue in entry.ExtraValues)
                        writer.WriteByte(extraValue);
                }
            }
            writer.WriteUInt16(skill.Tail0);
            writer.WriteUInt16(skill.Tail1);
            return writer.ToArray();
        }
    }
}
