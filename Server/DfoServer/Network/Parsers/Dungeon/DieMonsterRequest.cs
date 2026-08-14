using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public readonly struct DieMonsterRequest
    {
        public ushort LocalIndex { get; }
        public ushort UserId { get; }
        public bool IsCapture { get; }
        public bool IsPassiveObject { get; }

        public DieMonsterRequest(
            ushort localIndex,
            ushort userId,
            bool isCapture,
            bool isPassiveObject)
        {
            LocalIndex = localIndex;
            UserId = userId;
            IsCapture = isCapture;
            IsPassiveObject = isPassiveObject;
        }

        public static DieMonsterRequest Parse(byte[] body)
        {
            if (body == null || body.Length < 2)
                throw new ArgumentException("DIE_MONSTER body must be at least 2 bytes.", nameof(body));

            ushort localIndex = BitConverter.ToUInt16(body, 0);
            ushort userId = body.Length >= 4 ? BitConverter.ToUInt16(body, 2) : (ushort)0;

            
            bool isCapture = false;
            bool isPassive = false;
            if (body.Length > 20)
            {
                int atkCount = body[20];
                int flagOffset = 21 + atkCount * 10 + 6;
                if (flagOffset - 1 < body.Length)
                    isCapture = body[flagOffset - 1] != 0;
                if (flagOffset < body.Length)
                    isPassive = body[flagOffset] == 1;
            }
            return new DieMonsterRequest(
                localIndex,
                userId,
                isCapture,
                isPassive);
        }
    }
}
