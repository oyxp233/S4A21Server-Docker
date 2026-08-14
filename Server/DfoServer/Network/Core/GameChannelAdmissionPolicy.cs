namespace DfoServer.Network
{
    public sealed class GameChannelAdmissionRejection
    {
        public GameChannelAdmissionRejection(
            byte commandErrorCode,
            string message)
        {
            CommandErrorCode = commandErrorCode;
            Message = message ?? string.Empty;
        }

        public byte CommandErrorCode { get; }

        public string Message { get; }
    }

    public static class GameChannelAdmissionPolicy
    {
        public const byte Channel100MinimumCharacterLevel = 70;

        private static readonly GameChannelAdmissionRejection
            Channel100LevelRejection = new GameChannelAdmissionRejection(
                commandErrorCode: 0xFD,
                message: "\u5F53\u524D\u9891\u9053\u4EC5\u965070\u7EA7\u53CA\u4EE5\u4E0A\u89D2\u8272\u8FDB\u5165\u3002");

        public static bool TryGetCharacterEntryRejection(
            int listenerGamePort,
            int characterLevel,
            out GameChannelAdmissionRejection rejection)
        {
            if (GameNetworkConfig.IsChannel100Listener(listenerGamePort)
                && characterLevel < Channel100MinimumCharacterLevel)
            {
                rejection = Channel100LevelRejection;
                return true;
            }

            rejection = null;
            return false;
        }
    }
}
