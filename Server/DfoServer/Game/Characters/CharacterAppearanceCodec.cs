using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Characters
{
    
    
    
    
    internal static class CharacterAppearanceCodec
    {
        public static byte[] Encode(CharacterAppearanceEntry[] entries)
        {
            if (entries == null) entries = Array.Empty<CharacterAppearanceEntry>();
            if (entries.Length > byte.MaxValue) throw new ArgumentException("too many appearance entries", nameof(entries));

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)entries.Length);
                foreach (var e in entries)
                {
                    bw.Write(e.Slot);
                    bw.Write(e.DisplayItemId);
                    bw.Write(e.ExpansionLen);
                    var exp = e.ExpansionData ?? new byte[4];
                    bw.Write(exp.Length == 4 ? exp : new byte[4]);
                    bw.Write(e.State);
                    bw.Write(e.LinkItemId);
                    bw.Write(e.EnchantValue);
                    bw.Write(e.Flag20);
                }
                return ms.ToArray();
            }
        }

        public static CharacterAppearanceEntry[] Decode(byte[] blob)
        {
            if (blob == null || blob.Length == 0)
                return Array.Empty<CharacterAppearanceEntry>();

            using (var ms = new MemoryStream(blob))
            using (var br = new BinaryReader(ms))
            {
                var count = br.ReadByte();
                var list = new List<CharacterAppearanceEntry>(count);
                for (var i = 0; i < count; i++)
                {
                    var slot = br.ReadByte();
                    var itemId = br.ReadInt32();
                    var expLen = br.ReadInt32();
                    var expData = br.ReadBytes(4);
                    var state = br.ReadByte();
                    var linkItemId = br.ReadInt32();
                    var enchantValue = br.ReadUInt32();
                    var flag20 = br.ReadByte();
                    list.Add(new CharacterAppearanceEntry(slot, itemId, expLen, expData, state, linkItemId, enchantValue, flag20));
                }
                return list.ToArray();
            }
        }
    }
}
