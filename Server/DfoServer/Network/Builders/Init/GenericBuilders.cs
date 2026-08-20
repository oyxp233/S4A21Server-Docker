using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;

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

    // A21 USERINFO1 后必须发送：count=1 + cid + town state=0。
    public sealed class UserStateInitBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0003;

        public bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            int occurrenceIndex,
            out byte[] body)
        {
            var character = snapshot.CharacterRecord;
            if (character == null)
            {
                body = null;
                return false;
            }

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt16((ushort)character.CharacterId);
            writer.WriteByte(0);
            body = writer.ToArray();
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

    // A21 0x0465: 史诗图鉴碎片数量，mode=0 全量，mode=1 增量。
    public sealed class A21UsableCount0465BodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0465;

        public bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            int occurrenceIndex,
            out byte[] body)
        {
            body = Build(snapshot?.InitializationSnapshot?.EpicPieceItems, 0);
            return true;
        }

        public static byte[] Build(
            IReadOnlyList<ItemValueEntrySnapshot> items,
            byte mode)
        {
            var count = items == null ? 0 : items.Count;
            var writer = new GamePacketWriter();
            writer.WriteByte(mode);
            writer.WriteUInt32((uint)Math.Max(0, count));
            if (items != null)
            {
                for (var index = 0; index < count; index++)
                {
                    var item = items[index];
                    writer.WriteUInt32((uint)Math.Max(0, item.ItemId));
                    writer.WriteUInt32((uint)Math.Max(0, item.Value));
                }
            }

            return writer.ToArray();
        }

        public static byte[] BuildSingle(int itemId, int value)
        {
            return Build(
                new[]
                {
                    new ItemValueEntrySnapshot
                    {
                        ItemId = itemId,
                        Value = Math.Max(0, value),
                    },
                },
                1);
        }
    }

    public sealed class A21UsableCount021EBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x021E;

        public bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            int occurrenceIndex,
            out byte[] body)
        {
            var items = snapshot?.InitializationSnapshot?.UsableCountItems;
            var count = items == null ? 0 : Math.Min(byte.MaxValue, items.Count);
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)count);
            if (items != null)
            {
                for (var index = 0; index < count; index++)
                {
                    writer.WriteInt32(items[index].ItemId);
                    writer.WriteInt32(items[index].Value);
                }
            }
            body = writer.ToArray();
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
