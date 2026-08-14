using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    
    
    
    
    
    
    
    
    
    
    public sealed class UserInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0002;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var c = snapshot.CharacterRecord;
            if (c == null) { body = null; return false; }

            if (occurrenceIndex == 1)
            {
                var addition = snapshot.InitializationSnapshot.UserInfoAddition;
                if (addition == null)
                {
                    DfoServer.FileLogger.Log("[UserInfoBodyBuilder] ERROR: occ1 UserInfoAddition is null — 结构化表未迁移。不兜底 blob。");
                    body = null; return false;
                }
                var w = new GamePacketWriter();
                w.WriteByte(1); w.WriteUInt16(1);
                w.WriteUInt16((ushort)c.CharacterId);
                w.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(
                    addition, snapshot.InitializationSnapshot.SkillInfo));
                body = w.ToArray(); return true;
            }

            if (occurrenceIndex == 0 || occurrenceIndex == 2)
            {
                body = UserInfoSubtype0Builder.BuildNotificationBody(c);
                return true;
            }

            
            DfoServer.FileLogger.Log($"[UserInfoBodyBuilder] ERROR: 不支持的 occurrence {occurrenceIndex} — init 流只有 occ0/1/2。");
            body = null;
            return false;
        }
    }
}
