using System;

namespace DfoServer.Game.SelectCharacter
{
    
    
    
    
    
    
    
    
    
    
    
    public sealed class UserInfoMinimumTailSnapshot
    {
        public uint CloneTitleItemId { get; set; }
        public byte Forging { get; set; }
        public byte CreatureField1 { get; set; }             
        public byte CreatureField2 { get; set; }             
        public byte CreatureField3 { get; set; }             
        public byte CreatureField4 { get; set; }             
        public byte[] CreatureBuffer { get; set; }           
        public uint NameTagItemId { get; set; }
        public uint NameTagExpireTime { get; set; }
        public byte Stamina { get; set; }                    
        public uint FatiguePenalty { get; set; }             
        public byte IsEventCharacter { get; set; }           
        public uint EquippedCreatureItemId { get; set; }
        public byte[] EquippedCreatureNameBytes { get; set; } = new byte[0];
        public byte EquippedCreatureAliveState { get; set; }
        public uint PcRoomId { get; set; } = 0x00010001;     
        public byte IsPrivateStore { get; set; }             
        public byte IsPremiumPcRoom { get; set; }            
        public byte ServerGroupId { get; set; }              
        public uint BlackCount { get; set; }                 
        public byte GuildLevel { get; set; }                 
        public byte[] GuildNameBytes { get; set; } = new byte[0];
        public uint ChaosPoint { get; set; }                 
        public byte DisguiseKind { get; set; }               
        public byte IsDisguised { get; set; }                
        public byte ExpertJobType { get; set; }              
        public uint ExpertJobExp { get; set; }               
        public byte IsHardcoreMode { get; set; }             
        public byte IsHardcoreDead { get; set; }             
        public ushort HardcoreDeathCount { get; set; }       
        public uint ProgressA { get; set; }                  
        public uint ProgressB { get; set; }                  
        public byte UserStateBits { get; set; } = 3;         
        public uint ChatBanEndTime { get; set; }             
        public ushort FatigueUpdate { get; set; }            
        public byte ReturnUserFlag { get; set; } = 1;        
        public ushort ChannelDisplayMode { get; set; }       
        public byte ChannelType { get; set; }                
        public ushort ChannelId { get; set; } = 2;           
        public ushort MoodValue { get; set; }
        public byte SkillTreeIndex { get; set; } = Skills.SkillTreeExpansionState.LockedWireValue;
        public byte IsReturnUser { get; set; }               
        public byte LinkSlotEnabled { get; set; }            
        public byte LinkTypeA { get; set; }                  
        public byte LinkTypeB { get; set; }                  
        public ushort EmotionIndex { get; set; }             
        public byte ActionByte { get; set; }                 
        public ushort FatigueDisplayUpdate { get; set; }     
        public byte CostumeFlag { get; set; }                
        public byte AuraFlag { get; set; }                   
        public byte PetDisplayFlag { get; set; }             
        public byte TitleDisplayFlag { get; set; }           
        public uint PvpStatA { get; set; }                   
        public byte PvpWinStreak { get; set; }               
        public byte PvpLoseStreak { get; set; }              
        public uint PvpRankPoint { get; set; }               
        public byte TrailingByte { get; set; }               

        public const int TailLength = 104;

        
        public static UserInfoMinimumTailSnapshot FromBytes(byte[] t)
        {
            if (t == null || t.Length < TailLength)
                throw new ArgumentException($"subtype0 tail 必须 {TailLength}B, got {t?.Length ?? 0}");

            var buf8 = new byte[8];
            Buffer.BlockCopy(t, 8, buf8, 0, 8);

            return new UserInfoMinimumTailSnapshot
            {
                CloneTitleItemId = BitConverter.ToUInt32(t, 0),
                Forging = t[4],
                CreatureField1 = t[4],
                CreatureField2 = t[5],
                CreatureField3 = t[6],
                CreatureField4 = t[7],
                CreatureBuffer = buf8,
                NameTagItemId = BitConverter.ToUInt32(buf8, 0),
                NameTagExpireTime = BitConverter.ToUInt32(buf8, 4),
                Stamina = t[16],
                FatiguePenalty = BitConverter.ToUInt32(t, 17),
                IsEventCharacter = t[21],
                PcRoomId = BitConverter.ToUInt32(t, 22),
                IsPrivateStore = t[26],
                IsPremiumPcRoom = t[27],
                ServerGroupId = t[28],
                BlackCount = BitConverter.ToUInt32(t, 29),
                GuildLevel = t[33],
                ChaosPoint = BitConverter.ToUInt32(t, 34),
                
                DisguiseKind = t[39],
                IsDisguised = t[40],
                ExpertJobType = t[41],
                ExpertJobExp = BitConverter.ToUInt32(t, 42),
                
                IsHardcoreMode = t[53],
                IsHardcoreDead = t[54],
                HardcoreDeathCount = BitConverter.ToUInt16(t, 55),
                ProgressA = BitConverter.ToUInt32(t, 57),
                ProgressB = BitConverter.ToUInt32(t, 61),
                UserStateBits = t[65],
                ChatBanEndTime = BitConverter.ToUInt32(t, 66),
                
                FatigueUpdate = BitConverter.ToUInt16(t, 71),
                ReturnUserFlag = t[73],
                ChannelDisplayMode = BitConverter.ToUInt16(t, 74),
                ChannelType = t[76],
                MoodValue = BitConverter.ToUInt16(t, 77),
                SkillTreeIndex = t[79],
                IsReturnUser = t[80],
                LinkSlotEnabled = t[81],
                LinkTypeA = t[82],
                LinkTypeB = t[83],
                EmotionIndex = BitConverter.ToUInt16(t, 84),
                ActionByte = t[86],
                FatigueDisplayUpdate = BitConverter.ToUInt16(t, 87),
                CostumeFlag = t[89],
                AuraFlag = t[90],
                PetDisplayFlag = t[91],
                TitleDisplayFlag = t[92],
                PvpStatA = BitConverter.ToUInt32(t, 93),
                PvpWinStreak = t[97],
                PvpLoseStreak = t[98],
                PvpRankPoint = BitConverter.ToUInt32(t, 99),
                TrailingByte = t[103],
            };
        }
    }
}
