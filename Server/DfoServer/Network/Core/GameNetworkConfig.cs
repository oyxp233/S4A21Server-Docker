using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Network
{
    public static class GameNetworkConfig
    {
        public const int NormalChannelIndex = 11;
        public const int Channel100Index = 100;
        public const int FreeDuelChannelIndex = 68;
        public const int RaidChannelIndex = 200;
        public const string ChannelName = "ch.11";
        public const int ChannelServerIndex = 1;
        public const int ChannelIndex = NormalChannelIndex;
        public const int NormalGamePort = 10011;
        public const int NormalProxyGamePort = 10012;
        public const int Channel100GamePort = 10161;
        public const int Channel100ProxyGamePort = 10162;
        public const int FreeDuelGamePort = 10068;
        public const int RaidGamePort = 10200;
        public const string FreeDuelListenerEnvironmentVariable =
            "DFO_FREE_DUEL_CHANNEL_LISTENER";
        public const byte GeneralChannelEnvironment = 0x01;
        public const byte FreeDuelChannelEnvironment = 0x0D;
        public const byte RaidChannelEnvironment = 0x17;
        public const int InitialUdpPort1 = 12311;
        public const int InitialUdpPort2 = 12312;
        public const int LoginChannelPort = 10128;
        public const int LoginUnknownPort = 17200;
        public const int CommandPacketCount = 1086;
        public const int NotificationPacketCount = 1036;

        public static string ServerIp { get; private set; } = "127.0.0.1";
        public static bool PacketCaptureEnabled { get; private set; }
        public static string PacketCaptureDir { get; private set; }
        public static bool ProxyMode { get; private set; }
        public static bool FreeDuelListenerEnabled { get; private set; }
        public static bool UdpRelayEnabled { get; private set; }
        public static bool PvpUdpRelayEnabled { get; private set; }
        public static string UdpRelayPublicIp { get; private set; }
        public static bool UdpRelayPublicIpConfigured { get; private set; }
        public static int UdpRelayPortBase { get; private set; } = 30000;
        public static int UdpRelayPortCount { get; private set; } = 256;
        public static int PvpUdpRelayPortBase { get; private set; } = 30256;
        public static int PvpUdpRelayPortCount { get; private set; } = 256;

        public static bool RelayPortRangesOverlap =>
            PortRangesOverlap(
                UdpRelayPortBase,
                UdpRelayPortCount,
                PvpUdpRelayPortBase,
                PvpUdpRelayPortCount);

        public static IReadOnlyList<GameChannelEndpoint> GetGameChannels()
            => BuildGameChannels(FreeDuelListenerEnabled);

        internal static IReadOnlyList<GameChannelEndpoint> BuildGameChannels(
            bool includeFreeDuel)
            => BuildGameChannels(includeFreeDuel, ProxyMode);

        internal static IReadOnlyList<GameChannelEndpoint> BuildGameChannels(
            bool includeFreeDuel,
            bool proxyMode)
        {
            var normalListenerPort =
                proxyMode ? NormalProxyGamePort : NormalGamePort;
            var channel100ListenerPort =
                proxyMode ? Channel100ProxyGamePort : Channel100GamePort;
            var channels = new List<GameChannelEndpoint>
            {
                new GameChannelEndpoint(
                    NormalChannelIndex,
                    NormalGamePort,
                    normalListenerPort),
                new GameChannelEndpoint(
                    Channel100Index,
                    Channel100GamePort,
                    channel100ListenerPort),
                new GameChannelEndpoint(
                    RaidChannelIndex,
                    RaidGamePort,
                    RaidGamePort)
            };

            if (includeFreeDuel)
            {
                channels.Add(
                    new GameChannelEndpoint(
                        FreeDuelChannelIndex,
                        FreeDuelGamePort,
                        FreeDuelGamePort));
            }

            return channels;
        }

        public static GameChannelEndpoint ResolveGameChannel(
            int listenerGamePort)
            => BuildGameChannels(includeFreeDuel: true).FirstOrDefault(
                   channel =>
                       channel.ListenerGamePort == listenerGamePort)
               ?? BuildGameChannels(includeFreeDuel: false)[0];

        public static bool TryResolveGameChannel(
            int listenerGamePort,
            out GameChannelEndpoint channel)
        {
            channel = BuildGameChannels(includeFreeDuel: true)
                .FirstOrDefault(candidate =>
                    candidate.ListenerGamePort == listenerGamePort);
            return channel != null;
        }

        public static GameChannelEndpoint FindGameChannel(int channelId)
            => BuildGameChannels(includeFreeDuel: true).FirstOrDefault(
                channel => channel.ChannelId == channelId);

        public static bool IsFreeDuelChannel(int channelId)
            => channelId == FreeDuelChannelIndex;

        public static bool IsFreeDuelListener(int listenerGamePort)
            => listenerGamePort == FreeDuelGamePort;

        public static bool IsChannel100Listener(int listenerGamePort)
            => listenerGamePort == Channel100GamePort
               || listenerGamePort == Channel100ProxyGamePort;

        public static bool IsRaidChannel(int channelId)
            => channelId == RaidChannelIndex;

        public static bool IsRaidListener(int listenerGamePort)
            => listenerGamePort == RaidGamePort;

        public static byte ResolveLoginEnvironment(int listenerGamePort)
        {
            if (IsRaidListener(listenerGamePort))
                return RaidChannelEnvironment;

            return IsFreeDuelListener(listenerGamePort)
                ? FreeDuelChannelEnvironment
                : GeneralChannelEnvironment;
        }

        public static void Configure(string[] args)
        {
            string serverIp = null;
            string udpRelayPublicIp = null;
            var freeDuelListenerRequested = false;

            UdpRelayPublicIp = null;
            UdpRelayPublicIpConfigured = false;
            FreeDuelListenerEnabled = false;

            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (string.Equals(
                            args[i], "--server-ip",
                            StringComparison.OrdinalIgnoreCase)
                        && i + 1 < args.Length)
                    {
                        serverIp = args[++i];
                    }
                    else if (string.Equals(
                                 args[i], "--udp-relay-public-ip",
                                 StringComparison.OrdinalIgnoreCase)
                             && i + 1 < args.Length)
                    {
                        udpRelayPublicIp = args[++i];
                    }
                    else if (string.Equals(
                                 args[i], "--packet-capture",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        PacketCaptureEnabled = true;
                        if (i + 1 < args.Length
                            && !args[i + 1].StartsWith("-"))
                        {
                            PacketCaptureDir = args[++i];
                        }
                    }
                    else if (string.Equals(
                                 args[i], "--proxy",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        ProxyMode = true;
                    }
                    else if (string.Equals(
                                 args[i], "--free-duel-channel-listener",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        freeDuelListenerRequested = true;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(serverIp))
                serverIp = Environment.GetEnvironmentVariable("SERVER_IP");
            if (string.IsNullOrWhiteSpace(serverIp))
            {
                serverIp = Environment.GetEnvironmentVariable(
                    "DFO_PUBLIC_SERVER_IP");
            }
            if (!string.IsNullOrWhiteSpace(serverIp))
                ServerIp = serverIp.Trim();

            FreeDuelListenerEnabled =
                freeDuelListenerRequested
                || ReadBoolEnvironmentVariable(
                    FreeDuelListenerEnvironmentVariable,
                    false);
            UdpRelayEnabled = ReadBoolEnvironmentVariable(
                "DFO_UDP_RELAY", false);
            PvpUdpRelayEnabled = ReadBoolEnvironmentVariable(
                "DFO_PVP_UDP_RELAY", false);

            if (string.IsNullOrWhiteSpace(udpRelayPublicIp))
            {
                udpRelayPublicIp = Environment.GetEnvironmentVariable(
                    "DFO_UDP_RELAY_PUBLIC_IP");
            }
            if (!string.IsNullOrWhiteSpace(udpRelayPublicIp))
            {
                UdpRelayPublicIp = udpRelayPublicIp.Trim();
                UdpRelayPublicIpConfigured = true;
            }

            UdpRelayPortBase = ReadIntEnvironmentVariable(
                "DFO_UDP_RELAY_PORT_BASE", 30000);
            UdpRelayPortCount = ReadIntEnvironmentVariable(
                "DFO_UDP_RELAY_PORT_COUNT", 256);
            PvpUdpRelayPortBase = ReadIntEnvironmentVariable(
                "DFO_PVP_UDP_RELAY_PORT_BASE", 30256);
            PvpUdpRelayPortCount = ReadIntEnvironmentVariable(
                "DFO_PVP_UDP_RELAY_PORT_COUNT", 256);
        }

        internal static void ValidateRelayConfiguration()
        {
            if (UdpRelayEnabled
                && !IsValidRelayPortRange(
                    UdpRelayPortBase, UdpRelayPortCount))
            {
                throw new InvalidOperationException(
                    "Invalid party UDP relay port range: " +
                    $"{UdpRelayPortBase}/{UdpRelayPortCount}.");
            }

            if (PvpUdpRelayEnabled
                && !IsValidRelayPortRange(
                    PvpUdpRelayPortBase, PvpUdpRelayPortCount))
            {
                throw new InvalidOperationException(
                    "Invalid PvP UDP relay port range: " +
                    $"{PvpUdpRelayPortBase}/{PvpUdpRelayPortCount}.");
            }

            if (UdpRelayEnabled
                && PvpUdpRelayEnabled
                && RelayPortRangesOverlap)
            {
                throw new InvalidOperationException(
                    "Party and PvP UDP relay port ranges must not overlap.");
            }
        }

        internal static bool PortRangesOverlap(
            int leftBase,
            int leftCount,
            int rightBase,
            int rightCount)
        {
            if (leftBase <= 0 || leftCount <= 0
                || rightBase <= 0 || rightCount <= 0)
            {
                return false;
            }

            var leftLast = (long)leftBase + leftCount - 1;
            var rightLast = (long)rightBase + rightCount - 1;
            return leftBase <= rightLast && rightBase <= leftLast;
        }

        private static bool IsValidRelayPortRange(
            int portBase,
            int portCount)
            => portBase > 0
               && portCount >= 2
               && (long)portBase + portCount - 1 <= ushort.MaxValue;

        private static bool ReadBoolEnvironmentVariable(
            string name,
            bool fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    return true;
                case "0":
                case "false":
                case "no":
                case "off":
                    return false;
                default:
                    return fallback;
            }
        }

        private static int ReadIntEnvironmentVariable(
            string name,
            int fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, out var parsed)
                ? parsed
                : fallback;
        }
    }
}
