using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Network;

namespace DfoServer.Game.Session
{
    public interface ISessionDirectory
    {
        void Register(int characterId, EnhancedClientSession session);
        Task<EnhancedClientSession> RegisterReplacingAsync(
            int characterId, EnhancedClientSession session);
        Task UnregisterAsync(int characterId);
        Task<bool> UnregisterAsync(
            int characterId, EnhancedClientSession expectedSession);
        bool TryGet(int characterId, out EnhancedClientSession session);
        IReadOnlyList<EnhancedClientSession> GetAllGameSessions();

        Task SendToAsync(int characterId, byte[] packet);
        Task BroadcastToAsync(IEnumerable<int> characterIds, byte[] packet);

        /// <summary>
        /// 同一频道监听端口、同一 (townId, areaId) 且不在副本中的其它在线会话
        /// (排除 excludeCharacterId 自己)。listenerPort=0 仅供无监听端口的旧自测使用。
        /// </summary>
        IReadOnlyList<EnhancedClientSession> GetSessionsInArea(
            byte townId,
            byte areaId,
            int excludeCharacterId,
            int listenerPort = 0);

        /// <summary>
        /// 向同一频道监听端口、同一 (townId, areaId) 的其它会话广播封包
        /// (排除 excludeCharacterId 自己)。
        /// </summary>
        Task BroadcastToAreaAsync(
            byte townId,
            byte areaId,
            int excludeCharacterId,
            byte[] packet,
            int listenerPort = 0);

        event Func<int, EnhancedClientSession, Task> SessionEnding;
    }
}
