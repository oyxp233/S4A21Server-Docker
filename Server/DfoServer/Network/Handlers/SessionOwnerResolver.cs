using DfoServer.Network;

namespace DfoServer.Network.Handlers
{
    public static class SessionOwnerResolver
    {
        public static (int characterId, int accountId) Resolve(EnhancedClientSession session)
        {
            var cid = session.Player != null && session.Player.CharacterId > 0 ? session.Player.CharacterId : 0;
            var aid = session.Account?.AccountId ?? 1;
            return (cid, aid);
        }
    }
}
