using DfoServer.Game.Inventory;
using System.Collections.Generic;

namespace DfoServer.Game.SelectCharacter
{
    
    
    
    
    
    public static class NewCharacterInitSequence
    {
        public static List<SelectCharacterPacketTemplate> Build()
        {
            var list = new List<SelectCharacterPacketTemplate>();

            void Raw(byte cmd, ushort type, int occ = 0)
                => list.Add(new SelectCharacterPacketTemplate
                {
                    Kind = SelectCharacterPacketTemplateKind.Raw,
                    Command = cmd, Type = type, OccurrenceIndex = occ
                });

            void Item(InventoryListType lt)
                => list.Add(new SelectCharacterPacketTemplate
                {
                    Kind = SelectCharacterPacketTemplateKind.ItemList,
                    Command = 0x00, Type = 0x000D, ItemListType = lt
                });

            
            Raw(0x01, 0x0004);                      
            Item(InventoryListType.Main);           
            Item(InventoryListType.Avatar);         
            Item(InventoryListType.PersonalCargo);  
            Item(InventoryListType.Pet);            
            Item(InventoryListType.AccountCargo);   
            Raw(0x00, 0x0069);                      
            Raw(0x00, 0x0002, 0);                   
            Raw(0x00, 0x0002, 1);                   
            Raw(0x00, 0x0245);
            Raw(0x00, 0x0013);                      
            Raw(0x00, 0x0015);                      
            Raw(0x00, 0x0164);                      
            Raw(0x00, 0x0286);                      
            Raw(0x00, 0x00AD);                      
            Raw(0x00, 0x01C7);                      
            Raw(0x00, 0x006C);                      
            Raw(0x00, 0x0005);                      
            Raw(0x00, 0x0187);                      
            Raw(0x00, 0x01B9);                      
            Raw(0x00, 0x0166, 0);                   
            Raw(0x00, 0x0166, 1);                   
            Raw(0x00, 0x0166, 2);                   
            Raw(0x00, 0x0166, 3);                   
            Raw(0x00, 0x0166, 4);                   
            Raw(0x00, 0x0167);                      
            Raw(0x00, 0x015F);                      
            Raw(0x00, 0x00AC);                      
            Raw(0x00, 0x00AE);                      
            Raw(0x00, 0x02DA);                      
            Raw(0x00, 0x017B);                      
            Raw(0x00, 0x0381, 0);
            Raw(0x00, 0x0381, 1);
            Raw(0x00, 0x021F);                      
            Raw(0x00, 0x0357);                      
            Raw(0x00, 0x00FB);                      
            Raw(0x00, 0x00CD);                      
            Raw(0x00, 0x019F);                      
            Raw(0x00, 0x00B1);                      
            Raw(0x00, 0x0300);                      
            Raw(0x01, 0x0312);                      
            Raw(0x00, 0x03D8);                      
            Raw(0x00, 0x019D);                      
            Raw(0x00, 0x025B);                      
            Raw(0x00, 0x0331);                      
            Raw(0x00, 0x01EB);                      
            Raw(0x00, 0x0061);                      
            Raw(0x00, 0x0158);                      
            Raw(0x00, 0x02D5, 0);                   
            Raw(0x00, 0x02D5, 1);                   
            Raw(0x00, 0x02D5, 2);                   
            Raw(0x00, 0x01A8);                      
            Raw(0x00, 0x007C);                      
            Raw(0x00, 0x0002, 2);
            Raw(0x00, 0x0035);
            Raw(0x00, 0x0111);
            Raw(0x00, 0x0016);
            Raw(0x00, 0x0077);   // 宠物欢迎语; 无宠物或无缓存时 builder 返回 false 跳过

            return list;
        }
    }
}
