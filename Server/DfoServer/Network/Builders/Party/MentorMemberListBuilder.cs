using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders.Party
{
    // 师徒成员列表 SC 0x004F(CALL_MEMER_LIST 应答)。客户端脱壳定论(routing RE):
    //   渲染器 = sub_CD1F70, 只注册在【lobby/channel 域】→ 收包信封 byte[0] 必须 = 0x01(与好友 0x112@byte0=0 相反)。
    //   该包 reset 后重建师徒窗名册数组 dword_3091F6C。body(sub_CD1F70 的 read_* 序列):
    //     [u8 prefix≠0]                         gate: 为 0 则整个 handler 不执行
    //     entry0: [u8 role][u8 b][dstr name][u8 c][u8 d][u32 e][u32 f]
    //     [u8 count]
    //     count × { [u8 role][u8 b][dstr name][u8 c][u8 d][u32 e][u32 f] }
    //   ⚠️ 6-agent 对抗验证定论: 师傅/徒弟【方向由位置决定, 不是 role 字节】!
    //     客户端迭代器 sub_14A6E30 恒 begin+344*(a2+1)、计数 sub_14A6DD0 = 总数-1 → 【index0 被跳过】。
    //     故: entry0 = "我的师傅"槽(单个); 循环项 index1.. = "我的徒弟"列表(DNF 一师多徒)。
    //     role→元素+52 只是徒弟列表内的次级/显示标志(可见性等), 与师徒方向无关。之前"+52 符号=方向"是误判。
    //   b/c/d/e/f 为显示字段(等级/职业/id 等), 非方向关键, 暂填 0, 语义留真机细化。
    public static class MentorMemberListBuilder
    {
        public readonly struct Entry
        {
            public readonly byte Role;       // 1=师傅 2=徒弟 0xFF=空
            public readonly byte[] Name;     // GBK 原始名
            public Entry(byte role, byte[] name) { Role = role; Name = name ?? System.Array.Empty<byte>(); }
        }

        // entries 至少 1 个(entry0 强制)。多个 = entry0 + (Count-1) 个后续项。
        public static byte[] Build(IReadOnlyList<Entry> entries)
        {
            var w = new GamePacketWriter();
            w.WriteByte(0x01);                                   // prefix(gate, 非0)
            WriteEntry(w, entries[0]);                           // entry0(强制)
            int count = entries.Count - 1;
            w.WriteByte((byte)(count < 0 ? 0 : count));          // count
            for (int i = 1; i < entries.Count; i++)
                WriteEntry(w, entries[i]);
            return w.ToArray();
        }

        private static void WriteEntry(GamePacketWriter w, Entry e)
        {
            w.WriteByte(e.Role);                                 // role → 元素+52
            w.WriteByte(0);                                      // b
            w.WriteRawDstr(e.Name);                              // dstr name
            w.WriteByte(0);                                      // c
            w.WriteByte(0);                                      // d
            w.WriteUInt32(0);                                    // e
            w.WriteUInt32(0);                                    // f
        }
    }
}
