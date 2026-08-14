using DfoServer.Network;
using System.Linq;

namespace DfoServer.Network.Builders.Party
{
    // PARTY_INFO (Noti type=0x0009) 队伍名册广播。
    // ⚠️ 逐字节照【脱壳客户端 0x09 解析器 sub_D1BD10】的读序列复刻(不是 df, df 是更老版本、包体不同):
    //   读原语: sub_2100490=读u16 / sub_2100420=读u8 / sub_2100500=读u32。
    //   外层: u16 块计数(恒 1, while(i<N) 上界) → 每块 { u16 partyId; u8 type; [信息块]; [名册块]; [子记录] }
    //   信息块(type 0/1, 共 7 字节, 【无队名 dstr】——名字走客户端缓存 getStr(626)):
    //       u8 nameFlag(0→用默认名) + u8 + u8 + u16 + u8 + u8
    //   名册块(type 0/2, 【8 槽 × 5 字节】 + 3 字节尾): 每槽 = u16 uid(空槽 FFFF) + u8 + u8 + u8; 尾 = u8 u8 u8
    //   子记录(type≤2): u8 hasExtra(0=无变长子记录)
    //   旧实现照 df 写了 11B info(含 4B 假 dstr) + 4 槽×3B, 错位导致客户端把名册读成垃圾槽→"满员"、真成员进不去。
    public static class PartyInfoNotiBuilder
    {
        public static byte[] Build(Game.Party.Party party, byte type)
        {
            var w = new GamePacketWriter();
            w.WriteUInt16(1);                       // 块计数(恒 1; 名册在单块内)
            w.WriteUInt16((ushort)party.PartyId);   // partyId
            w.WriteByte(type);

            if (type == 0 || type == 1)             // 信息块【11 字节】(真机钩子实证: 客户端在 info 后 offset+11 读名册)
            {
                // ⚠️ 真机 sub_14A4AA0 钩子证实 info 块=11字节, 不是反编译静态数出的 7 字节(那 4 字节读点未被静态捕捉)。
                //    少写这 4 字节 → 名册整体前移 4B → uid 全错位 → 0 成员。恢复到能对齐的 11 字节结构。
                w.WriteByte(party.TitleIndex);      // titleIdx
                w.WriteRawDstr(party.TitleBytes ?? System.Array.Empty<byte>()); // 队名 dstr(空=4字节), 补齐 info 到 11B
                w.WriteByte(0);                     // IsReturnUserParty
                w.WriteByte(party.UserMax == 0 ? (byte)4 : party.UserMax); // userMax
                w.WriteUInt16(party.DungIndex);     // dungIndex
                w.WriteByte(party.DungDiffi);       // dungDiffi
                w.WriteByte(0);                     // IsEventCharacParty
            }

            if (type == 0 || type == 2)             // 名册块(8 槽 × 5 字节 + 3 字节尾)
            {
                var members = party.MembersBySlot();
                for (byte i = 0; i < 8; i++)
                {
                    var m = members.FirstOrDefault(x => x.SlotIndex == i);
                    w.WriteUInt16(m != null ? m.UserId : (ushort)0xFFFF);   // 槽 uid(空槽 FFFF)
                    w.WriteByte(0);                 // v176 (读后丢弃)
                    w.WriteByte(0);                 // v191[i] (→member+26, 暂 0)
                    w.WriteByte(0);                 // v190[8+i] flag(=1 触发 sub_1C81C30)
                }
                w.WriteByte(0);                     // v179 (→member+82 尾)
                w.WriteByte(0);                     // v185 (→member+72 尾)
                w.WriteByte(0);                     // v182 (队伍级 flag)
            }

            if (type == 5)                          // (本服务端未用 type 5, 占位保持对齐)
                w.WriteByte(0);

            if (type <= 2)                          // 变长子记录标志
                w.WriteByte(0);                     // v181 hasExtra = 0(无子记录)

            return w.ToArray();
        }
    }
}
