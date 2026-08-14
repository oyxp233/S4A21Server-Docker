using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders.Party
{
    // PARTY IP INFO (Noti SC cmd 0x000B) — P2P 端点交换, 让队友互相 UDP 直连(清"连接中")。
    // ⚠️ 逐字节照【86jp 客户端 sub_D31160 读序】(= df CParty::send_party_ipinfo @0x0859CEA2 镜像;
    //    工作流 IDA 实证: 每成员 22 字节, 尾部只有 1 个 char_attr, 不是 df 老版 23 字节)。
    // body = [u8 memberCount] + 每成员 22B:
    //   u16 uid(LE) + u32 inner_ip(4B octets a.b.c.d) + u32 outer_ip(4B octets)
    //   + u16 port(大端/网络序, 客户端过 htons 转换器) + u32 acc_id(LE) + u8 nat_type + u32 mtu(LE) + u8 char_attr
    // 存进客户端队伍成员对象 m+0x3F0(inner)/+0x3F4(outer)/+0x3F8(port)/+0x3FA(nat)。
    // df 次序: RES_PEER 接受成功 → 0x99 → 0x08(给A) → 【0x0B】 → 0x09。一个包广播给全队。
    public static class PartyIpInfoBuilder
    {
        public static byte[] Build(Game.Party.Party party)
        {
            return Build(party.MembersBySlot());
        }

        public static byte[] Build(
            IReadOnlyList<Game.Party.PartyMember> members)
        {
            var w = new GamePacketWriter();
            w.WriteByte((byte)members.Count);                 // memberCount (u8)
            foreach (var m in members)
            {
                w.WriteUInt16(m.UserId);                      // uid (LE)
                var ip = (m.IpBytes != null && m.IpBytes.Length == 4) ? m.IpBytes : new byte[] { 127, 0, 0, 1 };
                w.WriteBytes(ip);                             // inner_ip 4B(octets)
                w.WriteBytes(ip);                             // outer_ip 4B(LAN=同 inner)
                w.WriteByte((byte)(m.P2pPort >> 8));          // port 高字节(大端/网络序)
                w.WriteByte((byte)(m.P2pPort & 0xFF));        // port 低字节
                w.WriteUInt32(m.AccId);                       // acc_id (LE)
                w.WriteByte(0);                               // nat_type = 0(open/LAN)
                w.WriteUInt32(1500);                          // mtu
                w.WriteByte(0);                               // char_attr
            }
            return w.ToArray();
        }

        /// <summary>
        /// Builds the per-recipient endpoint roster used by the proven
        /// PartyUdpRelay path. The recipient keeps its own reported endpoint;
        /// every peer points at the relay's public IP and the ordered
        /// (recipient, peer) relay port. A missing relay leg is an invariant
        /// violation: combining the relay IP with a client's direct port would
        /// advertise a black-hole endpoint.
        /// </summary>
        public static byte[] BuildForRelay(
            IReadOnlyList<Game.Party.PartyMember> members,
            ushort recipientUid,
            byte[] relayIpBytes,
            System.Func<ushort, int> peerPortLookup)
        {
            if (members == null)
                throw new System.ArgumentNullException(nameof(members));
            if (relayIpBytes == null || relayIpBytes.Length != 4)
                throw new System.ArgumentException(
                    "relay IP must contain four IPv4 octets",
                    nameof(relayIpBytes));

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)members.Count);
            foreach (var member in members)
            {
                writer.WriteUInt16(member.UserId);

                byte[] ipBytes;
                int port;
                if (member.UserId == recipientUid)
                {
                    ipBytes =
                        member.IpBytes != null && member.IpBytes.Length == 4
                            ? member.IpBytes
                            : new byte[] { 127, 0, 0, 1 };
                    port = member.P2pPort;
                }
                else
                {
                    var relayPort =
                        peerPortLookup?.Invoke(member.UserId) ?? 0;
                    if (relayPort <= 0 ||
                        relayPort > ushort.MaxValue)
                    {
                        throw new System.InvalidOperationException(
                            $"relay matrix is incomplete for " +
                            $"{recipientUid}->{member.UserId}");
                    }

                    ipBytes = relayIpBytes;
                    port = relayPort;
                }

                writer.WriteBytes(ipBytes);
                writer.WriteBytes(ipBytes);
                writer.WriteByte((byte)((port >> 8) & 0xFF));
                writer.WriteByte((byte)(port & 0xFF));
                writer.WriteUInt32(member.AccId);
                writer.WriteByte(0);
                writer.WriteUInt32(1500);
                writer.WriteByte(0);
            }

            return writer.ToArray();
        }

    }
}
