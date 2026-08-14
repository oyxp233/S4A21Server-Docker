namespace DfoServer.Network.Builders
{
    public static class DropItemBuilder
    {
        
        
        public static byte[] BuildDrop(
            ushort dropperActorId,
            ushort positionX,
            ushort positionY,
            Game.Dungeon.DropInfo drop,
            ushort ownerActorId)
        {
            var w = new GamePacketWriter();

            
            w.WriteUInt16(dropperActorId);    
            w.WriteUInt16(positionX);
            w.WriteUInt16(positionY);
            w.WriteUInt16(drop.SceneSlot);
            w.WriteUInt32(drop.TemplateId);
            w.WriteByte(drop.UpgradeLevel);
            w.WriteUInt32(drop.PacketValue);
            w.WriteUInt16(drop.Endurance);

            var core = drop.Core;
            w.WriteUInt32(core != null ? core.SealFlag : 0u);
            w.WriteByte(core != null ? core.GenuineUpgrade : (byte)0);
            w.WriteByte(core != null ? core.TradeRestriction : (byte)0);
            w.WriteUInt16(core != null ? core.AmplifyValue : (ushort)0);
            w.WriteUInt32(core != null ? unchecked((uint)core.Marker16) : 0u);

            
            w.WriteByte(0);

            
            w.WriteUInt16(0);

            
            w.WriteByte(0);

            
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteUInt16(0);                  
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteUInt16(ownerActorId);       

            return w.ToArray();
        }

        public static byte[] BuildDropSuccessAck(byte listType, ushort slotIndex, int count)
        {
            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteByte(listType);
            w.WriteUInt16(slotIndex);
            w.WriteInt32(count);
            return w.ToArray();
        }

        public static byte[] BuildDropFailureAck(byte errorCode, byte listType)
        {
            var w = new GamePacketWriter();
            w.WriteByte(0);
            w.WriteByte(errorCode);
            w.WriteByte(listType);
            return w.ToArray();
        }

        public static byte[] BuildPickupItem(ushort srcSlot, ushort pickerActorId, ushort dstInvSlot, byte moveFlag)
        {
            var w = new GamePacketWriter();

            w.WriteUInt16(srcSlot);
            w.WriteUInt16(pickerActorId);

            for (int i = 0; i < 8; i++)
                w.WriteByte(0);

            w.WriteUInt16(pickerActorId);  
            w.WriteUInt16(dstInvSlot);
            w.WriteByte(moveFlag);

            return w.ToArray();
        }

        
        
        
        public static byte[] BuildPickupGold(ushort srcSlot, ushort pickerActorId, int goldAmount, int extraGold = 0)
        {
            var w = new GamePacketWriter();

            w.WriteUInt16(srcSlot);            
            w.WriteUInt16(pickerActorId);      

            // Valid gold slots carry the pickup effect flag and extra/tax gold fields.
            w.WriteByte(1);                    
            w.WriteUInt32((uint)goldAmount);   
            w.WriteByte(1);
            w.WriteUInt32((uint)extraGold);
            w.WriteUInt32(0);

            for (int i = 1; i < 8; i++)
            {
                w.WriteByte(0);                
                w.WriteUInt32(0);              
            }

            return w.ToArray();
        }
    }
}
