using System;
using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Session;

namespace DfoServer.Network.Parsers.Party
{
    public enum SetUdpEndpointParseFailure
    {
        None = 0,
        NullBody,
        ShortBody,
        InnerIpv4Class,
        OuterIpv4Class,
        ZeroPort,
        MtuRange,
    }

    /// <summary>
    /// Parses the fixed SET_UDP_IP_PORT (0x0002) endpoint prefix. The client
    /// layout is nat(1), inner IPv4(4), outer IPv4(4), port LE(2), MTU LE(4),
    /// followed by optional identity bytes that are deliberately ignored.
    /// </summary>
    public readonly struct SetUdpEndpointRequest
    {
        public const int MinimumBodyLength = 15;
        public const int NatTypeOffset = 0;
        public const int InnerIpv4Offset = 1;
        public const int OuterIpv4Offset = 5;
        public const int PortOffset = 9;
        public const int MtuOffset = 11;

        private SetUdpEndpointRequest(
            byte natType,
            IPAddress innerIpv4,
            IPAddress outerIpv4,
            ushort port,
            uint mtu)
        {
            NatType = natType;
            InnerIpv4 = innerIpv4;
            OuterIpv4 = outerIpv4;
            Port = port;
            Mtu = mtu;
        }

        public byte NatType { get; }
        public IPAddress InnerIpv4 { get; }
        public IPAddress OuterIpv4 { get; }
        public ushort Port { get; }
        public uint Mtu { get; }

        public static bool TryParse(byte[] body, out SetUdpEndpointRequest request)
        {
            return TryParse(body, out request, out _);
        }

        public static bool TryParse(
            byte[] body,
            out SetUdpEndpointRequest request,
            out SetUdpEndpointParseFailure failure)
        {
            request = default;
            failure = SetUdpEndpointParseFailure.None;
            if (body == null)
            {
                failure = SetUdpEndpointParseFailure.NullBody;
                return false;
            }
            if (body.Length < MinimumBodyLength)
            {
                failure = SetUdpEndpointParseFailure.ShortBody;
                return false;
            }

            var innerIpv4 = new IPAddress(
                body.AsSpan(InnerIpv4Offset, 4));
            var outerIpv4 = new IPAddress(
                body.AsSpan(OuterIpv4Offset, 4));
            if (!ReportedUdpEndpointState.IsUsableIpv4(innerIpv4))
            {
                failure = SetUdpEndpointParseFailure.InnerIpv4Class;
                return false;
            }
            if (!ReportedUdpEndpointState.IsUsableIpv4(outerIpv4))
            {
                failure = SetUdpEndpointParseFailure.OuterIpv4Class;
                return false;
            }

            var port = BinaryPrimitives.ReadUInt16LittleEndian(
                body.AsSpan(PortOffset, sizeof(ushort)));
            if (port == 0)
            {
                failure = SetUdpEndpointParseFailure.ZeroPort;
                return false;
            }

            var mtu = BinaryPrimitives.ReadUInt32LittleEndian(
                body.AsSpan(MtuOffset, sizeof(uint)));
            if (mtu < ReportedUdpEndpointState.MinimumMtu
                || mtu > ReportedUdpEndpointState.MaximumMtu)
            {
                failure = SetUdpEndpointParseFailure.MtuRange;
                return false;
            }

            request = new SetUdpEndpointRequest(
                body[NatTypeOffset],
                innerIpv4,
                outerIpv4,
                port,
                mtu);
            return true;
        }
    }
}
