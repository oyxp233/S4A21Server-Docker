using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Session;

namespace DfoServer.Network.Builders.Pvp
{
    /// <summary>
    /// PvP waiting-room peer endpoints (NOTI 0x000B).
    ///
    /// body = u8 count + count * 22-byte records:
    /// u16 uid | IPv4 inner | IPv4 outer | u16 port(network byte order) |
    /// u32 account id | u8 NAT | u32 MTU | u8 character attribute.
    /// </summary>
    internal static class PvpPeerInfoBuilder
    {
        internal static byte[] Build(
            IReadOnlyList<EnhancedClientSession> members)
        {
            return BuildCore(
                members,
                recipient: null,
                relayIpBytes: null,
                peerPortLookup: null);
        }

        /// <summary>
        /// Builds a recipient-specific relay roster. The recipient's own
        /// record preserves the endpoint reported by CMD 0x0002. Every peer
        /// record points at that recipient-to-peer ordered relay leg.
        /// </summary>
        internal static byte[] BuildForRelay(
            IReadOnlyList<EnhancedClientSession> members,
            EnhancedClientSession recipient,
            byte[] relayIpBytes,
            Func<ushort, int> peerPortLookup)
        {
            if (recipient?.Player == null ||
                recipient.Player.UserId == 0)
            {
                throw new ArgumentException(
                    "PvP relay recipient has no active character",
                    nameof(recipient));
            }
            if (relayIpBytes == null ||
                relayIpBytes.Length != 4)
            {
                throw new ArgumentException(
                    "relay IP must contain four IPv4 octets",
                    nameof(relayIpBytes));
            }
            if (peerPortLookup == null)
                throw new ArgumentNullException(nameof(peerPortLookup));

            return BuildCore(
                members,
                recipient,
                relayIpBytes,
                peerPortLookup);
        }

        private static byte[] BuildCore(
            IReadOnlyList<EnhancedClientSession> members,
            EnhancedClientSession recipient,
            byte[] relayIpBytes,
            Func<ushort, int> peerPortLookup)
        {
            var count = members?.Count ?? 0;
            if (count > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(members));

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)count);
            for (var index = 0; index < count; index++)
            {
                var member = members[index]
                             ?? throw new ArgumentException(
                                 "PvP peer member is null",
                                 nameof(members));
                if (member.Player == null ||
                    member.Player.UserId == 0)
                {
                    throw new ArgumentException(
                        "PvP peer member has no active character",
                        nameof(members));
                }

                var endpoint = member.Player.ReportedUdpEndpoint;

                writer.WriteUInt16(member.Player.UserId);
                byte[] innerIp;
                byte[] outerIp;
                int port;
                byte natType;
                if (recipient != null &&
                    member.SessionId != recipient.SessionId)
                {
                    innerIp = relayIpBytes;
                    outerIp = relayIpBytes;
                    port = peerPortLookup(
                        member.Player.UserId);
                    if (port <= 0 ||
                        port > ushort.MaxValue)
                    {
                        throw new InvalidOperationException(
                            "PvP relay matrix is incomplete for " +
                            $"{recipient.Player.UserId}->" +
                            $"{member.Player.UserId}");
                    }
                    // The published endpoint itself is the open server relay.
                    // NAT discovery remains preserved on the recipient's own
                    // record and in PlayerContext for diagnostics.
                    natType = 0;
                }
                else
                {
                    // The original 2014 room join does not reject a missing
                    // network report. Preserve that honest "not reported"
                    // state as zeroes; never manufacture the historical
                    // fixed port 10000.
                    innerIp = endpoint?.InnerIpv4.GetAddressBytes()
                              ?? new byte[4];
                    if (endpoint != null &&
                        !GameNetworkConfig.ProxyMode &&
                        TryGetAuthenticatedRemoteIpv4(
                            member,
                            out var authenticatedOuter))
                    {
                        outerIp =
                            authenticatedOuter.GetAddressBytes();
                        port = endpoint.Port;
                    }
                    else
                    {
                        // Never reflect UDP toward a client-supplied third
                        // party. A proxy/unknown TCP peer is fail-closed.
                        outerIp = new byte[4];
                        port = 0;
                    }
                    natType = endpoint?.NatType ?? 0;
                }

                writer.WriteBytes(innerIp);
                writer.WriteBytes(outerIp);
                writer.WriteByte((byte)(port >> 8));
                writer.WriteByte((byte)(port & 0xFF));

                var accountId =
                    member.Account?.AccountId > 0
                        ? member.Account.AccountId
                        : member.Player.CharacterId;
                writer.WriteUInt32((uint)accountId);
                writer.WriteByte(natType);
                writer.WriteUInt32(endpoint?.Mtu ?? 0);
                writer.WriteByte(0); // character attribute
            }

            return writer.ToArray();
        }

        private static bool TryGetAuthenticatedRemoteIpv4(
            EnhancedClientSession session,
            out IPAddress address)
        {
            address = null;
            try
            {
                if (session?.TcpClient?.Client?.RemoteEndPoint
                        is not IPEndPoint remote)
                {
                    return false;
                }

                address = remote.Address.IsIPv4MappedToIPv6
                    ? remote.Address.MapToIPv4()
                    : remote.Address;
                if (address.AddressFamily !=
                    AddressFamily.InterNetwork)
                {
                    address = null;
                    return false;
                }
                var octets = address.GetAddressBytes();
                if (octets.Length != 4 ||
                    octets[0] == 0 ||
                    octets[0] >= 224)
                {
                    address = null;
                    return false;
                }
                return true;
            }
            catch (ObjectDisposedException)
            {
                address = null;
                return false;
            }
            catch (SocketException)
            {
                address = null;
                return false;
            }
        }
    }
}
