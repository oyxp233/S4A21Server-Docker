using System.Threading.Tasks;

namespace DfoServer.Game.Session
{
    public interface ISessionPacketSender
    {
        Task SendPacketAsync(byte[] rawPacket);
        Task SendNotiAsync(ushort notiType, byte[] body);
        Task SendCmdAckAsync(ushort cmdType, byte[] body);
        PlayerContext Player { get; }
        int CharacterId { get; }
        int AccountId { get; }
    }
}
