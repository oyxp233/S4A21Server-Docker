using DfoServer.Network;

namespace DfoServer.Network.Builders.Party
{
    // MEMBER_ENTER_OK (SC 0x004A): 师徒关系建立成功通知, 发给【师傅】和【徒弟】双方(各自收到对方的名字)。
    // df @0x084CD218: put_byte(a) put_byte(b) put_byte(c) put_int(nameLen) put_str(partnerName)。
    // ⚠️ a/b/c 三个字节值在 df 由 Monitor 服务器提供, 这里按分支逻辑推断默认 a=1,b=1,c=0(建立-带链接 分支);
    //    确切能让客户端渲染"师徒已建立"的三元组必须真机验证(见 compound #432)。prefix=0。
    public static class MemberEnterOkBuilder
    {
        public static byte[] Build(byte[] partnerName, byte a = 1, byte b = 1, byte c = 0)
        {
            var w = new GamePacketWriter();
            w.WriteByte(a);                                        // df data+0x0A mode(非2/3/4=建立)
            w.WriteByte(b);                                        // df data+0x0B flag(b==1&&+0x0C==1 才应用链接)
            w.WriteByte(c);                                        // df data+0x15(用途未解, 默认0)
            w.WriteRawDstr(partnerName ?? System.Array.Empty<byte>()); // int32 len + 对方角色名
            return w.ToArray();
        }
    }
}
