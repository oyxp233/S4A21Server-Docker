using DfoServer.Network;

namespace DfoServer.Network.Builders.Party
{
    // REQUEST_MEMBER_ENTER_TO_RESPONSER (Noti 0x0049): 服务端转给"被邀请者(徒弟)"的【师徒】邀请弹窗。
    // df @0x084CCF02: put_header(0,0x49) put_short(X) put_int(nameLen) put_str(A_name)。X = df data+0x12,
    //   在 df 由 Monitor 填(不透明); 最合理推断 = 发起者 UID(唯一可用的应答句柄)。
    // ⚠️ short X 的确切值待真机验证(见 compound #432)。0x4C/0x4D 是【师徒】不是组队(真机实测弹师徒框)。
    public static class MemberEnterInviteBuilder
    {
        public static byte[] Build(ushort inviterUserId, byte[] inviterName)
        {
            var w = new GamePacketWriter();
            w.WriteUInt16(inviterUserId);                          // short X(推断=发起者 UserId)
            w.WriteRawDstr(inviterName ?? System.Array.Empty<byte>());   // 发起者角色名(int32 len + bytes)
            return w.ToArray();
        }
    }
}
