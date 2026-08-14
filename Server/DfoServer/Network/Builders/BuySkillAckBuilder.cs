using DfoServer.Game.Skills;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    
    
    
    
    
    
    
    public static class BuySkillAckBuilder
    {
        public static byte[] Build(BuySkillResult result)
        {
            var w = new GamePacketWriter();
            if (result == null || !result.Success)
            {
                w.WriteByte(0x00);                                  
                w.WriteByte(result != null ? result.ErrorCode : (byte)1);
                return w.ToArray();
            }

            w.WriteByte(0x01);                                      
            w.WriteByte(result.SkillTree);                         
            w.WriteUInt16(result.RemainSp);                        
            w.WriteUInt16(result.RemainTp);
            w.WriteByte((byte)result.Entries.Count);               
            foreach (var e in result.Entries)
            {
                w.WriteByte(e.Slot);                               
                w.WriteUInt16(e.SkillId);                          
                w.WriteByte(e.Level);                              
                w.WriteByte(e.HasCmd ? (byte)1 : (byte)0);         
            }
            return w.ToArray();
        }
    }
}
