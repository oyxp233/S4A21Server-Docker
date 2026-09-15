using System;
using System.Threading.Tasks;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// A21 PVP_CHANNEL_INFO.
    ///
    /// The client sends an empty request before it opens the PvP channel
    /// selector. A21 reads the successful reply on the same command as:
    ///   u8 success, i32 context, u8 connectedServerCount.
    /// ChannelProtocol advertises configured PvP channels when enabled; the
    /// game-server reply intentionally carries an empty inter-server list.
    /// </summary>
    public sealed class PvpChannelInfoHandler
    {
        internal const ushort CommandType = (ushort)CmdPacketTypeA21.PVP_CHANNEL_INFO;

        private readonly Func<bool> _isFreeDuelAvailable;

        public PvpChannelInfoHandler()
            : this(IsFreeDuelAvailable)
        {
        }

        internal PvpChannelInfoHandler(
            Func<bool> isFreeDuelAvailable)
        {
            _isFreeDuelAvailable =
                isFreeDuelAvailable
                ?? throw new ArgumentNullException(
                    nameof(isFreeDuelAvailable));
        }

        public Task HandlePvpChannelInfo(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session?.Account == null)
            {
                FileLogger.Log(
                    "[GameProtocol] PVP_CHANNEL_INFO rejected: " +
                    "session is not authenticated");
                return Task.CompletedTask;
            }

            var requestIsValid =
                body == null || body.Length == 0;
            var success =
                requestIsValid && _isFreeDuelAvailable();
            if (!requestIsValid)
            {
                FileLogger.Log(
                    "[GameProtocol] PVP_CHANNEL_INFO rejected: " +
                    $"expected empty body, received {body.Length} bytes");
            }

            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    CommandType,
                    success
                        ? BuildSuccessBody()
                        : BuildErrorBody()));
        }

        internal static byte[] BuildSuccessBody()
        {
            return new byte[]
            {
                1,
                0, 0, 0, 0,
                0
            };
        }

        internal static byte[] BuildErrorBody()
        {
            // A21 1119FB0 sends failure 0x15 to the mercenary notice (1585060).
            // Zero follows 26BAC60's ordinary selector fallback. If no eligible
            // PvP channel exists, 26BAB90 shows the client's localized 70077.
            // That text resource ID is not a wire error code or server notice.
            return new byte[] { 0, 0 };
        }

        private static bool IsFreeDuelAvailable()
        {
            return GameNetworkConfig.FreeDuelListenerEnabled;
        }
    }
}
