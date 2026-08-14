using DfoServer.Game.SelectCharacter;
using System;

namespace DfoServer.Network.Builders
{
    public sealed class SimpleByteBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType { get; }

        private readonly Func<SelectCharacterInitializationSnapshot, byte> _valueSelector;

        public SimpleByteBodyBuilder(ushort notiType, Func<SelectCharacterInitializationSnapshot, byte> valueSelector)
        {
            NotiType = notiType;
            _valueSelector = valueSelector;
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = new byte[] { _valueSelector(snapshot.InitializationSnapshot) };
            return true;
        }
    }

    public sealed class EmptyBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType { get; }

        public EmptyBodyBuilder(ushort notiType)
        {
            NotiType = notiType;
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = Array.Empty<byte>();
            return true;
        }
    }





    // NOTI 273 (0x0111) 联合服好友信息。客户端有注册 handler(0x00D0DBB0)，
    // 8 字节零是新角色一直在用的空态基线；跨服好友数据对单机服务端无意义，统一发空态。
    public sealed class UnitedServerFriendInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0111;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = new byte[8];
            return true;
        }
    }

    
    
    
    
    
    public sealed class UserPositionBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0016;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var c = snapshot.CharacterRecord;
            if (c == null) { body = null; return false; }
            var w = new GamePacketWriter();
            w.WriteUInt16((ushort)c.CharacterId);
            w.WriteUInt16((ushort)c.PosX);
            w.WriteUInt16((ushort)c.PosY);
            w.WriteByte(c.Direction);
            w.WriteUInt16(100);
            body = w.ToArray();
            return true;
        }
    }

    
    
    
    
    public sealed class CeraBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0035;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var c = snapshot.CharacterRecord;
            if (c == null) { body = null; return false; }

            var init = snapshot.InitializationSnapshot;

            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteInt32(init.AckCera);
            w.WriteInt32(init.AckTokenCera);
            w.WriteInt32(init.AckHappyTokenCera);
            body = w.ToArray();
            return true;
        }
    }

}
