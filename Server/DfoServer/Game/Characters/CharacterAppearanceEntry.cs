using System;

namespace DfoServer.Game.Characters
{
    
    
    
    
    
    public sealed class CharacterAppearanceEntry
    {
        private byte[] _expansionData;

        public CharacterAppearanceEntry(byte slot, int displayItemId, int expansionLen, byte[] expansionData, byte state, int linkItemId, uint enchantValue, byte flag20)
        {
            Slot = slot;
            DisplayItemId = displayItemId;
            ExpansionLen = expansionLen;
            ExpansionData = expansionData;
            State = state; // 属性状态: invenitem.Attr * 2 + (invenitem.AmplifyType != 0)
            LinkItemId = linkItemId;
            EnchantValue = enchantValue;
            Flag20 = flag20; // 锻造等级: invenitem.EnchantUpgradeCount
        }

        public byte Slot { get; set; }

        
        // NOTI2外观列表中的 itemId 是当前槽位的显示模板ID。
        public int DisplayItemId { get; set; }

        
        public int ExpansionLen { get; set; } = 4;

        
        public byte[] ExpansionData
        {
            get => CopyExpansionData(_expansionData);
            set => _expansionData = CopyExpansionData(value);
        }

        public ushort Color1
        {
            get => BitConverter.ToUInt16(_expansionData, 0);
            set => BitConverter.GetBytes(value).CopyTo(_expansionData, 0);
        }

        public ushort Color2
        {
            get => BitConverter.ToUInt16(_expansionData, 2);
            set => BitConverter.GetBytes(value).CopyTo(_expansionData, 2);
        }

        
        public byte State { get; set; }

        
        // NOTI2外观列表中的关联物品ID：slot 9 是克隆装扮，替换称号动画走 subtype0 tail 首字段。
        public int LinkItemId { get; set; }

        
        public uint EnchantValue { get; set; }

        
        public byte Flag20 { get; set; }

        
        public static CharacterAppearanceEntry FromBytes(byte[] buffer, int offset)
        {
            var slot = buffer[offset];
            var itemId = BitConverter.ToInt32(buffer, offset + 1);
            var expLen = BitConverter.ToInt32(buffer, offset + 5);
            var expData = new byte[4];
            Buffer.BlockCopy(buffer, offset + 9, expData, 0, 4);
            var state = buffer[offset + 13];
            var linkItemId = BitConverter.ToInt32(buffer, offset + 14);
            var enchantValue = BitConverter.ToUInt32(buffer, offset + 18);
            var flag20 = buffer[offset + 22];
            return new CharacterAppearanceEntry(slot, itemId, expLen, expData, state, linkItemId, enchantValue, flag20);
        }

        private static byte[] CopyExpansionData(byte[] data)
        {
            var copy = new byte[4];
            if (data != null && data.Length > 0)
                Buffer.BlockCopy(data, 0, copy, 0, Math.Min(data.Length, copy.Length));
            return copy;
        }
    }
}
