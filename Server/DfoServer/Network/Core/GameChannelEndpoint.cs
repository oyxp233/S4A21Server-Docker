using System;

namespace DfoServer.Network
{
    public sealed class GameChannelEndpoint
    {
        public GameChannelEndpoint(
            int channelId,
            int publicGamePort,
            int listenerGamePort)
        {
            if (channelId < byte.MinValue || channelId > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(channelId));
            if (publicGamePort < 1 || publicGamePort > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(publicGamePort));
            if (listenerGamePort < 1 || listenerGamePort > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(listenerGamePort));

            ChannelId = channelId;
            PublicGamePort = publicGamePort;
            ListenerGamePort = listenerGamePort;
        }

        public int ChannelId { get; }

        public string SelectorName => $"#ch.{ChannelId}";

        public string LoginName => $"ch.{ChannelId}";

        public int PublicGamePort { get; }

        public int ListenerGamePort { get; }
    }
}
