using DfoServer.Network;

namespace DfoServer.Network.Builders.Party
{
    // DUNGEON ROOM P2P (Noti SC cmd 0x0184) — 副本内房间的 P2P 端点表, 让队友在副本里互相直连、填血量/清"连接中"。
    // ⚠️ 与城镇组队 0x0B 是【不同的表/不同的 wire】: 0x0B 写 member+0x3F0(城镇表, 22B/员);
    //    0x184 走 add_peer→[mgr+0x18](副本房间表, 18B/peer)。别混用。
    // 逐字节照【86jp 客户端 sub_D07E10 读序】(workflow leg2/synth 从 _ipinfo_scan.txt 反编译):
    //   HEADER(8B): u8 f1 + u8 f2 + u32 session/room + u8 globalFlag + u8 mode(mode∈{1,2} 才解析 BODY)
    //   BODY: u16 roomId + u8 count1 + count1×14B{u16,u32,u16,u16,u8,u8,u8,u8}(格子位摆放)
    //        + u8 count2 + count2×18B{u8 role/slot, u16 uid, u32 inner_ip, u32 outer_ip, u16 port, u8 nat, u16 extra1, u16 extra2}
    //        + u8 trailerFlag
    // ⚠️⚠️ 待真机验证(最佳猜测): mode 取值(1 vs 2)、header 的 u32 session/room 值(客户端拿它查房间 sub_125C120)、
    //     count1 能否为 0、peer 的 role/slot 与 extra1/extra2 语义、port 字节序(暂沿用城镇 0x0B 的大端)。
    //     若真机仍"连接中/血量空", 优先调这些字段(见晨间交接文档)。
    public static class DungeonRoomP2pBuilder
    {
        public static byte[] Build(Game.Party.Party party)
        {
            var members = party.MembersBySlot();
            var w = new GamePacketWriter();

            // HEADER (8B)
            w.WriteByte(0);                               // f1 (回包会回显; 暂 0)
            w.WriteByte(0);                               // f2
            w.WriteUInt32((uint)party.PartyId);           // session/room id ⚠️猜测=partyId(客户端用它查房间)
            w.WriteByte(0);                               // globalFlag(→byte_3091EBD)
            w.WriteByte(1);                               // mode ⚠️=1 触发 BODY 解析(1 vs 2 待验)

            // BODY
            w.WriteUInt16((ushort)party.PartyId);         // roomId
            w.WriteByte(0);                               // count1 = 0(无格子位摆放列表)⚠️能否为0待验

            w.WriteByte((byte)members.Count);             // count2 = P2P 端点数
            foreach (var m in members)
            {
                w.WriteByte(m.SlotIndex);                 // role/slot ⚠️疑=host/槽位, 暂用槽号
                w.WriteUInt16(m.UserId);                  // uid (LE)
                var ip = (m.IpBytes != null && m.IpBytes.Length == 4) ? m.IpBytes : new byte[] { 127, 0, 0, 1 };
                w.WriteBytes(ip);                         // inner_ip 4B(octets)
                w.WriteBytes(ip);                         // outer_ip 4B(LAN=同 inner)
                w.WriteByte((byte)(m.P2pPort >> 8));      // port 高字节(大端, 沿用 0x0B 约定)
                w.WriteByte((byte)(m.P2pPort & 0xFF));    // port 低字节
                w.WriteByte(0);                           // nat_type = 0(open/LAN)
                w.WriteUInt16(0);                         // extra1 ⚠️
                w.WriteUInt16(0);                         // extra2 ⚠️
            }

            w.WriteByte(0);                               // trailerFlag(→byte_3091FC9; 非0会触发额外 UI)
            return w.ToArray();
        }
    }
}
