using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Builders.Friends;

namespace DfoServer.Game.Friends
{
    // Published names are client projection state; the repository alone decides blocking.
    internal sealed class BlacklistProjection
    {
        private readonly BlacklistRepository _repository;
        private readonly ISessionDirectory _sessions;
        private readonly ConditionalWeakTable<EnhancedClientSession, ClientProjection> _published = new();

        internal sealed class ClientProjection
        {
            internal int Owner;
            internal ushort UserId;
            internal readonly ConcurrentDictionary<int, string> Names = new();
        }

        internal BlacklistProjection(BlacklistRepository repository, ISessionDirectory sessions)
        { _repository = repository; _sessions = sessions; }

        internal bool IsBlocked(int owner, int target) => _repository.IsBlocked(owner, target);

        internal ClientProjection For(EnhancedClientSession session)
        {
            var state = _published.GetValue(session, _ => new ClientProjection());
            lock (state)
            {
                if (state.Owner != session.Player.CharacterId || state.UserId != session.Player.UserId)
                {
                    state.Names.Clear(); state.Owner = session.Player.CharacterId; state.UserId = session.Player.UserId;
                }
            }
            return state;
        }

        internal async Task PublishAsync(EnhancedClientSession owner)
        {
            var current = UnitedFriendSystem.CaptureSessionIdentityCheck(owner, _sessions);
            if (!current()) return;
            int id = owner.Player.CharacterId;
            var state = For(owner);
            var entries = _repository.List(id);
            bool CanPublish() => current() && entries.SequenceEqual(_repository.List(id));
            // The native list ACK merges; an explicit delete clears an obsolete permanent name.
            foreach (var old in state.Names.ToArray())
            {
                if (entries.Any(e => e.CharacterId == old.Key && e.Name == old.Value)) continue;
                if (!await owner.TrySendPacketAsync(BlacklistPacketBuilder.Ack(CmdPacketTypeA21.DELETE_TO_BLACKLIST, 0, old.Value),
                    default, CanPublish, () => state.Names.TryRemove(old.Key, out _))) return;
            }
            await owner.TrySendPacketAsync(BlacklistPacketBuilder.List(entries), default, CanPublish, () =>
                {
                    state.Names.Clear();
                    foreach (var entry in entries) state.Names[entry.CharacterId] = entry.Name;
                });
        }
    }
}
