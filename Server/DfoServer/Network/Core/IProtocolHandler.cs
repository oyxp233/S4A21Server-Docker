using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.Network
{
    
    public interface IProtocolHandler
    {
        Task OnClientConnected(EnhancedClientSession session);
        Task OnClientDisconnected(EnhancedClientSession session);
        Task OnPacketReceived(EnhancedClientSession session, FlexiblePacket packet);
        string ProtocolName { get; }
    }

    
    public abstract class BaseProtocolHandler : IProtocolHandler
    {
        public abstract string ProtocolName { get; }

        public abstract Task OnClientConnected(EnhancedClientSession session);
        public abstract Task OnClientDisconnected(EnhancedClientSession session);
        public abstract Task OnPacketReceived(EnhancedClientSession session, FlexiblePacket packet);
    }
}
