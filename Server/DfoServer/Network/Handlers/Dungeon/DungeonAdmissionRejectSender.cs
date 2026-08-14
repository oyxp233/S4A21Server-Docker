using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonAdmissionRejectSender
    {
        internal Task SendAsync(
            EnhancedClientSession session,
            ushort wireType,
            DungeonAdmissionReject rejection)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    wireType,
                    DungeonAdmissionRejectBuilder.Build(rejection)));
        }
    }
}
