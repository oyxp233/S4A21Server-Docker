using DfoServer.Game.Premium;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class PremiumServiceInitBodyBuilder : IInitCmdPacketBuilder
    {
        public ushort CmdType => (ushort)CmdPacketTypeA21.PREMIUM_SERVICE;

        public bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            out byte[] body)
        {
            var init = snapshot?.InitializationSnapshot;
            if (init?.PremiumServiceData == null)
            {
                body = null;
                return false;
            }

            body = PremiumService.BuildPremiumServiceStateBody(
                init.PremiumServiceType,
                init.PremiumServiceData);
            return true;
        }
    }
}
