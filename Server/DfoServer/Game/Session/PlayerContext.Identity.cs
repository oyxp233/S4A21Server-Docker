using System;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        public byte[] Name { get; set; } = Array.Empty<byte>();
        public byte Job { get; set; }
        public byte GrowType { get; set; }
        public byte Level { get; set; } = 1;
        public uint Exp { get; set; }
        public ushort UserId { get; set; }
        public byte UserState { get; set; } = 0x01;
        public CharacterAppearanceEntry[] AppearanceEntries { get; set; } = Array.Empty<CharacterAppearanceEntry>();
        public SelectCharacter.UserInfoMinimumTailSnapshot Subtype0Tail { get; set; }

        /// <summary>
        /// </summary>
        public int CharacterId { get; set; }
    }
}
