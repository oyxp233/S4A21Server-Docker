using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    // ★台服官方服务端 DisPatcher_RepairEquip::send (ELF 0x81c61ea) 反编译确认的回包结构:
    //   成功: put_byte(1); put_int(money); put_byte(inven_type); put_short(slot); put_short(x)
    //         = [01][剩余金币:4 LE][inven_type:1][slot:2 LE][x:2 LE]
    //   失败: put_byte(0); put_byte(errcode)  = [00][errcode:1]
    //   ★body[0] 是"成功标志": 1=成功, 0=失败。之前发 00 被客户端判失败(ErrCode 17=body[1])。
    //   money 是修理后剩余金币绝对值(CInventory::get_money, 客户端直接覆盖显示)。
    public static class RepairEquipmentAckBuilder
    {
        // updatedGold = 修理后剩余金币(绝对值), invenType/slot 原样回传请求
        public static byte[] Build(byte invenType, short slot, int updatedGold)
        {
            var w = new GamePacketWriter();
            w.WriteByte(0x01);          // 成功标志 = 1
            w.WriteInt32(updatedGold);  // 剩余金币
            w.WriteByte(invenType);     // inven_type
            w.WriteInt16(slot);         // slot
            w.WriteInt16(0);            // 台服最后一个 put_short, 实测补0
            return w.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
        {
            var w = new GamePacketWriter();
            w.WriteByte(0x00);          // 失败标志 = 0
            w.WriteByte(errorCode);
            return w.ToArray();
        }
    }
}
