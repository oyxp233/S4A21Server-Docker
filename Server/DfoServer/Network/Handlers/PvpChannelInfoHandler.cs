using System;
using System.Threading.Tasks;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// CMD 0x00C3 PVP_CHANNEL_INFO.
    ///
    /// The client sends an empty request before it opens the PvP channel
    /// selector. The legacy game server replies on the same command with:
    ///   u8 success, i32 reserved, u8 connectedServerCount.
    /// The selector itself already receives CH.68 from ChannelProtocol, so the
    /// game-server reply intentionally carries an empty inter-server list.
    /// </summary>
    public sealed class PvpChannelInfoHandler
    {
        internal const ushort CommandType = 0x00C3;

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
            // Legacy SendCmdErrorPacket(type, 0x15):
            // u8 success=0, u8 error=21.
            return new byte[] { 0, 0x15 };
        }

        private static bool IsFreeDuelAvailable()
        {
            return GameNetworkConfig.FreeDuelListenerEnabled;
        }
    }
}
