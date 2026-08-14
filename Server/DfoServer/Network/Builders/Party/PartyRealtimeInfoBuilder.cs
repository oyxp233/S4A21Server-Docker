using DfoServer.Network;

namespace DfoServer.Network.Builders.Party
{
    // PARTY_MEMBER_REALTIME_INFO (Noti type=0x0099), 逐字节照 df_game_r CParty::get_party_realtime_info @0x0859CBAC。
    // body = [memberCount:byte] + 每有效成员 { uid(u16) + hpPercent(byte) + isHelpAbuseParty(byte) + slotIndex(byte) }。
    // 该字节 86jp 客户端渲染为 **HP 百分比**(0~100); 满血=100。MP 在本包无字段(客户端 HP 取到后再自行取 MP)。
    public static class PartyRealtimeInfoBuilder
    {
        public static byte[] Build(Game.Party.Party party)
        {
            var w = new GamePacketWriter();
            var members = party.MembersBySlot();
            w.WriteByte((byte)members.Count);           // memberCount
            foreach (var m in members)
            {
                w.WriteUInt16(m.UserId);                // +0 uid
                w.WriteByte(100);                       // +2 HP 百分比(满血=100)
                w.WriteByte(0);                         // +3 isHelpAbuseParty
                w.WriteByte(m.SlotIndex);               // +4 slot
            }
            return w.ToArray();
        }
    }
}
