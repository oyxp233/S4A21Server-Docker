using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Guilds;
using DfoServer.Network.Parsers.Guilds;
using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using System.Linq;

namespace DfoServer.Network.Handlers
{
    internal sealed class GuildJoinHandler
    {
        private readonly GuildRepository _repository;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly ISessionDirectory _sessions;
        private readonly InventoryRefreshSender _refresh;
        private readonly GuildStatePublisher _publisher;
        internal GuildJoinHandler(GuildRepository repository, CharacterTransitionCoordinator transitions,
            ISessionDirectory sessions, InventoryRefreshSender refresh, GuildStatePublisher publisher)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        }

        internal Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var command = (CmdPacketTypeA21)header.type;
            if (command == CmdPacketTypeA21.APPROVE_JOIN_GUILD) return Approve(session, body);
            return _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!InventoryContext.TryGetOwnedLease(session.SessionId, session.Player.CharacterId, out var lease)) return;
                switch (command)
                {
                    case CmdPacketTypeA21.REQ_GUILD_SERCH_FOR_JOIN:
                        var result = GuildTextRequest.TryParse(body, GuildCreationRules.MaximumNameBytes, out _, out var name)
                            ? _repository.FindByName(name) : null;
                        await session.SendPacketAsync(GuildJoinPacketBuilder.Search(result));
                        break;
                    case CmdPacketTypeA21.REQUEST_JOIN_GUILD:
                        if (!GuildTextRequest.TryParseApplication(body, out var guildName, out var message))
                        {
                            await session.SendPacketAsync(GuildCreationPacketBuilder.Ack(command, 0x9F));
                            return;
                        }
                        var guild = _repository.FindByName(guildName)?.Guild;
                        GuildResult status;
                        try { status = guild == null ? GuildResult.NotFound : _repository.Apply(guild.Id, lease.CharacterId, message); }
                        catch (SqliteException ex)
                        {
                            FileLogger.Log($"[Guild] application persistence failed cid={lease.CharacterId} code={ex.SqliteErrorCode}");
                            status = GuildResult.PersistenceFailed;
                        }
                        await session.SendPacketAsync(status == GuildResult.Success
                            ? GuildJoinPacketBuilder.Applied(guild, message)
                            : GuildCreationPacketBuilder.Ack(command, 0x9F));
                        FileLogger.Log($"[Guild] apply cid={lease.CharacterId} guild={guild?.Id ?? 0} status={status}");
                        break;
                    case CmdPacketTypeA21.GUILD_JOIN_LIST:
                        if (body != null && body.Length != 0)
                        {
                            await session.SendPacketAsync(GuildCreationPacketBuilder.Failure(command));
                            return;
                        }
                        await session.SendPacketAsync(GuildJoinPacketBuilder.Applications(_repository.GetApplicationsForLeader(lease.CharacterId)));
                        break;
                    case CmdPacketTypeA21.REFRESH_GUILD_INFO:
                        if (body != null && body.Length != 0) return;
                        var roster = _repository.GetRosterForMember(lease.CharacterId);
                        if (roster == null) return;
                        await session.SendPacketAsync(GuildJoinPacketBuilder.Rank(roster.Members.Single(m => m.CharacterId == lease.CharacterId).Rank));
                        await session.SendPacketAsync(GuildInfoPacketBuilder.Build(roster));
                        break;
                }
            });
        }

        private async Task Approve(EnhancedClientSession session, byte[] body)
        {
            int actor = session.Player.CharacterId;
            if (actor <= 0) return;
            int target = body?.Length == 4 ? BitConverter.ToInt32(body, 0) : 0;
            if (target <= 0 || target == actor)
            {
                await _transitions.RunIfCurrentAsync(session, () => session.SendPacketAsync(GuildJoinPacketBuilder.Approved(target, false)));
                return;
            }
            // Match existing coordinator ordering even when the applicant is offline.
            // Login/reconnect cannot race the commit and subsequent identity projection.
            int guildId = 0;
            using (var first = await _transitions.AcquireAsync(Math.Min(actor, target)))
            using (var second = await _transitions.AcquireAsync(Math.Max(actor, target)))
            {
            if (!_transitions.IsCurrent(session)
                || !InventoryContext.TryGetOwnedLease(session.SessionId, actor, out _)) return;
            var guild = _repository.GetForMember(actor);
            GuildResult status;
            try { status = guild == null ? GuildResult.NotLeader : _repository.Approve(guild.Id, actor, target); }
            catch (SqliteException ex)
            {
                FileLogger.Log($"[Guild] approval persistence failed leader={actor} target={target} code={ex.SqliteErrorCode}");
                status = GuildResult.PersistenceFailed;
            }
            await session.SendPacketAsync(GuildJoinPacketBuilder.Approved(target, status == GuildResult.Success));
            FileLogger.Log($"[Guild] approve leader={actor} target={target} status={status}");
            if (status != GuildResult.Success) return;
            guildId = guild.Id;
            if (_sessions.TryGet(target, out var applicant)
                && InventoryContext.TryGetOwnedLease(applicant.SessionId, target, out _))
            {
                applicant.Player.Subtype0Tail ??= new Game.SelectCharacter.UserInfoMinimumTailSnapshot();
                GuildIdentityProjection.Apply(guild, applicant.Player.Subtype0Tail);
                await _refresh.SendNoti2AppearanceUpdate(applicant);
                await applicant.SendPacketAsync(GuildJoinPacketBuilder.Rank(false));
                await applicant.SendPacketAsync(GuildInfoPacketBuilder.Build(_repository.GetRosterForMember(target)));
            }
            }
            await _publisher.RefreshAsync(_repository.GetMemberIds(guildId));
        }
    }
}
