using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class CharacterOptionBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0187;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var saved = snapshot.InitializationSnapshot.CharacterOptionBlob;
            if (saved != null)
            {
                body = new byte[saved.Length];
                Buffer.BlockCopy(saved, 0, body, 0, saved.Length);
                return true;
            }

            // 未保存过角色选项时发空位图(u32 len=0)
            var writer = new GamePacketWriter();
            writer.WriteInt32(0);
            body = writer.ToArray();
            return true;
        }
    }
}
