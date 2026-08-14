using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    
    
    
    
    
    
    
    
    
    
    
    
    public static class UserInfoSubtype0Builder
    {
        public static byte[] BuildNotificationBody(CharacterRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteUInt16(1);
            writer.WriteUInt16((ushort)record.CharacterId);
            writer.WriteDstr(record.Name);
            writer.WriteBytes(BuildRemainingBytes(record));
            return writer.ToArray();
        }

        
        
        
        
        public static byte[] BuildRemainingBytes(CharacterRecord record)
        {
            var writer = new GamePacketWriter();

            
            writer.WriteByte(record.Job);           
            writer.WriteByte(record.GrowType);      
            writer.WriteByte(record.Level);              
            writer.WriteByte(record.PvpGrade);           
            writer.WriteByte(record.PvpRatingGrade);     
            writer.WriteByte(record.UserState);          

            
            var appearances = GetAppearanceEntries(record);
            writer.WriteByte((byte)appearances.Count);   
            foreach (var e in appearances)
                WriteAppearanceEntry(writer, e);

            
            WriteTail(writer, record);

            return writer.ToArray();
        }

        
        
        
        
        
        private static void WriteTail(GamePacketWriter writer, CharacterRecord record)
        {
            var t = record.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
            ApplyOnlineInventoryTailFields(record.CharacterId, t);

            writer.WriteUInt32(t.CloneTitleItemId);
            writer.WriteByte(t.Forging); // 锻造
            writer.WriteByte(t.CreatureField2);             
            writer.WriteByte(t.CreatureField3);             
            writer.WriteByte(t.CreatureField4);             
            writer.WriteUInt32(t.NameTagItemId); // 名称装饰卡ID
            writer.WriteUInt32(t.NameTagExpireTime); // 名称装饰卡到期时间戳
            writer.WriteByte(t.Stamina);                    
            writer.WriteUInt32(t.FatiguePenalty);           
            writer.WriteByte(t.IsEventCharacter);           
            writer.WriteUInt32(t.EquippedCreatureItemId); // 宠物ID
            writer.WriteDstr(t.EquippedCreatureNameBytes); // 宠物名称
            writer.WriteByte(t.EquippedCreatureAliveState); // 宠物存活状态
            writer.WriteByte(t.IsPremiumPcRoom);            
            writer.WriteByte(t.ServerGroupId);              
            writer.WriteUInt32(t.BlackCount);               
            writer.WriteByte(t.GuildLevel);
            writer.WriteDstr(t.GuildNameBytes); // 工会名称
            writer.WriteUInt32(t.ChaosPoint);               
            writer.WriteByte(1);                            
            writer.WriteByte(t.DisguiseKind);               
            writer.WriteByte(t.IsDisguised);                
            writer.WriteByte(t.ExpertJobType);              
            writer.WriteUInt32(t.ExpertJobExp);             
            writer.WriteByte(0);                            
            writer.WriteUInt32(0);                          
            writer.WriteUInt16(0);                          
            writer.WriteByte(t.IsHardcoreMode);             
            writer.WriteByte(t.IsHardcoreDead);             
            writer.WriteUInt16(t.HardcoreDeathCount);       
            writer.WriteUInt32(t.ProgressA);                
            writer.WriteUInt32(t.ProgressB);                
            writer.WriteByte(t.UserStateBits);              
            writer.WriteUInt32(t.ChatBanEndTime);           
            writer.WriteByte(100);                          
            writer.WriteUInt16(t.FatigueUpdate);            
            writer.WriteByte(t.ReturnUserFlag);             
            writer.WriteUInt16(t.ChannelDisplayMode);       
            writer.WriteByte(t.ChannelType);                
            writer.WriteUInt16(t.MoodValue);
            writer.WriteByte(t.SkillTreeIndex);             
            writer.WriteByte(t.IsReturnUser);               
            writer.WriteByte(t.LinkSlotEnabled);            
            writer.WriteByte(t.LinkTypeA);                  
            writer.WriteByte(t.LinkTypeB);                  
            writer.WriteUInt16(t.EmotionIndex);             
            writer.WriteByte(t.ActionByte);                 
            writer.WriteUInt16(t.FatigueDisplayUpdate);     
            writer.WriteByte(t.CostumeFlag);                
            writer.WriteByte(t.AuraFlag);                   
            writer.WriteByte(t.PetDisplayFlag);             
            writer.WriteByte(t.TitleDisplayFlag);           
            writer.WriteUInt32(t.PvpStatA);                 
            writer.WriteByte(t.PvpWinStreak);               
            writer.WriteByte(t.PvpLoseStreak);              
            writer.WriteUInt32(t.PvpRankPoint);             
            writer.WriteByte(t.TrailingByte);               
        }

        private static void ApplyOnlineInventoryTailFields(int characterId, UserInfoMinimumTailSnapshot tail)
        {
            if (characterId <= 0 || tail == null || !InventoryContext.TryGetLease(characterId, out var lease))
                return;

            lock (lease.SyncRoot)
                new Noti2InventoryProjectionBuilder().ApplySubtype0TailDynamicFields(lease.Inventory, tail);
        }

        
        
        

        private static List<CharacterAppearanceEntry> GetAppearanceEntries(CharacterRecord record)
        {
            var result = new List<CharacterAppearanceEntry>();
            if (record.Appearance == null)
                return result;

            foreach (var e in record.Appearance)
            {
                if (e != null)
                    result.Add(e);
            }
            return result;
        }

        
        
        
        
        
        
        
        internal static void WriteAppearanceEntry(GamePacketWriter writer, CharacterAppearanceEntry e)
        {
            writer.WriteByte(e.Slot);
            writer.WriteInt32(e.DisplayItemId);
            writer.WriteInt32(e.ExpansionLen);
            writer.WriteBytes(e.ExpansionData != null && e.ExpansionData.Length == 4
                ? e.ExpansionData : new byte[4]);
            writer.WriteByte(e.State);
            writer.WriteInt32(e.LinkItemId);
            writer.WriteUInt32(e.EnchantValue);
            writer.WriteByte(e.Flag20);
        }
    }
}
