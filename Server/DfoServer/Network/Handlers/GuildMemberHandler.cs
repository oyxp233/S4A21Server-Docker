using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Network.Builders.Guilds;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class GuildMemberHandler
    {
        private readonly GuildRepository _repository;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly ISessionDirectory _sessions;

        internal GuildMemberHandler(GuildRepository repository, CharacterTransitionCoordinator transitions, ISessionDirectory sessions)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        internal Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!InventoryContext.TryGetOwnedLease(session.SessionId, session.Player.CharacterId, out var lease)) return;
                var command = (CmdPacketTypeA21)header.type;
                bool allMembers = command == CmdPacketTypeA21.GUILD_ALL_MEMBER_LIST;
                // Current client sends 4 zero bytes when switching to all members.
                // Only the initial full-snapshot request is supported, not a guessed cursor.
                bool valid = allMembers ? body?.Length == 4 && BitConverter.ToUInt32(body, 0) == 0
                    : command == CmdPacketTypeA21.GUILD_MEMER_LIST && (body == null || body.Length == 0);
                if (!valid)
                {
                    await session.SendPacketAsync(GuildCreationPacketBuilder.Failure(command));
                    return;
                }
                var roster = _repository.GetRosterForMember(lease.CharacterId);
                if (roster == null)
                {
                    await session.SendPacketAsync(GuildCreationPacketBuilder.Failure(command));
                    return;
                }
                var packet = GuildMemberPacketBuilder.Build(roster, FindOnlineChannel, allMembers);
                // Opening the panel must also repair an actor grade left over
                // from character switching; roster rows do not update that field.
                await session.SendPacketAsync(GuildJoinPacketBuilder.Rank(
                    roster.Members.Single(m => m.CharacterId == lease.CharacterId).Rank));
                await session.SendPacketAsync(packet);
                FileLogger.Log($"[Guild] members cid={lease.CharacterId} guild={roster.Guild.Id} count={roster.Members.Count}");
            });

        private byte? FindOnlineChannel(int characterId)
        {
            if (!_sessions.TryGet(characterId, out var session)
                || !InventoryContext.TryGetOwnedLease(session.SessionId, characterId, out _)
                || !GameNetworkConfig.TryResolveGameChannel(session.ListenerPort, out var channel)) return null;
            return checked((byte)channel.ChannelId);
        }
    }
}
