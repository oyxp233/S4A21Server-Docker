namespace DfoServer.Network.Builders
{
    internal static class UserInfoType2RosterTailBuilder
    {
        public static void Write(GamePacketWriter writer, uint cloneTitleItemId)
        {
            writer.WriteUInt32(cloneTitleItemId); // clone_title_item_id，0x0239 的持久化克隆称号动画

            writer.WriteByte(0x00); // tail_byte4
            writer.WriteByte(0x00); // tail_byte5
            writer.WriteByte(0x00); // tail_byte6
            writer.WriteByte(0x00); // tail_byte7

            writer.WriteUInt32(0); // name_tag_slot28_item_id，名称装饰卡 slot28
            writer.WriteUInt32(0); // name_tag_slot28_expire_time

            writer.WriteByte(0x00); // stamina
            writer.WriteUInt32(0); // fatigue_penalty
            writer.WriteByte(0x00); // tail_flag21
            writer.WriteByte(0x00); // tail_flag22
            writer.WriteByte(0x00); // tail_byte23

            writer.WriteByte(0x03); // display_state_bits，客户端按 bit0/bit1/bit3 消费
            writer.WriteByte(0x00); // tail_flag25
            writer.WriteByte(0x00); // tail_byte26
            writer.WriteByte(0x04); // tail_byte27，保留样本中的非零值
            writer.WriteByte(0x00); // tail_flag28
            writer.WriteByte(0x00); // tail_flag29
            writer.WriteByte(0x00); // tail_flag30
            writer.WriteByte(0x00); // tail_flag31
        }
    }
}
