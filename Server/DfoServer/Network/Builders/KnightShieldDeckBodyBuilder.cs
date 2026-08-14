using DfoServer.Game.KnightShield;

namespace DfoServer.Network.Builders
{
    public static class KnightShieldDeckBodyBuilder
    {
        public const ushort DeckNotificationType = (ushort)NotiPacketType.SEND_DECK_INFO;
        public const ushort ChangeDeckCommandType = (ushort)CmdPacketType.CHANGE_DECK_INFO;
        public const int DeckBodyLength = KnightShieldDeckSnapshot.SlotCount * sizeof(int);
        public const int ChangeDeckAckLength = (2 * sizeof(byte)) + DeckBodyLength;
        public const int ChangeDeckAckStatusOffset = 0x00;
        public const int ChangeDeckAckReservedOffset = 0x01;
        public const int ChangeDeckAckSlotsOffset = 0x02;

        public static byte[] BuildDeck(KnightShieldDeckSnapshot snapshot)
        {
            snapshot ??= new KnightShieldDeckSnapshot();
            var writer = new GamePacketWriter();

            // 86JP NOTI 0x0245 handler 0x00D33400 固定循环 5 次读取 u32。
            // 每项偏移为 slotIndex*4，依次为主盾和四个备用盾；没有数量或状态前缀。
            for (var slotIndex = 0; slotIndex < KnightShieldDeckSnapshot.SlotCount; slotIndex++)
                writer.WriteInt32(snapshot.GetShieldItemId(slotIndex));
            return writer.ToArray();
        }

        public static byte[] BuildChangeDeckAck(KnightShieldDeckSnapshot snapshot)
        {
            snapshot ??= new KnightShieldDeckSnapshot();
            var writer = new GamePacketWriter();

            // CMD ACK dispatcher 先消费 +0x00 的通用成功标志，并以 arg_C 传给
            // 86JP 0x00CC6B70。handler 随后从包游标读取 +0x01 的一个未使用 u8，
            // 再从 +0x02 固定读取 5 个 u32 并更新 player/UI deck，总消费长度 22B。
            writer.WriteByte(1); // +0x00 [u8] 通用 CMD ACK 成功标志
            writer.WriteByte(0); // +0x01 [u8] handler 读取但不使用的保留字段
            for (var slotIndex = 0; slotIndex < KnightShieldDeckSnapshot.SlotCount; slotIndex++)
                writer.WriteInt32(snapshot.GetShieldItemId(slotIndex)); // +0x02+slotIndex*4 [i32]
            return writer.ToArray();
        }
    }
}
