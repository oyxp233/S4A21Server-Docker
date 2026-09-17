using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Network.Builders.Guilds;
using DfoServer.Network.Parsers.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Network.Handlers
{
    internal sealed class GuildManagementHandler
    {
        private readonly GuildRepository _repository;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly GuildStatePublisher _publisher;
        internal GuildManagementHandler(GuildRepository repository, CharacterTransitionCoordinator transitions, GuildStatePublisher publisher)
        { _repository = repository; _transitions = transitions; _publisher = publisher; }

        internal async Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var command = (CmdPacketTypeA21)header.type;
            int actor = session.Player.CharacterId;
            if (actor <= 0) return;
            var refresh = new HashSet<int>();
            var gates = await AcquireAffectedAsync(actor);
            if (gates == null) return;
            try
            {
                if (!_transitions.IsCurrent(session)) return;
                if (!InventoryContext.TryGetOwnedLease(session.SessionId, actor, out _)) return;
                if (command == CmdPacketTypeA21.JOIN_GUILD_INFO)
                {
                    if (body == null || body.Length == 0)
                        await session.SendPacketAsync(GuildManagementPacketBuilder.Pending(_repository.GetPendingApplication(actor)));
                    return;
                }
                var roster = _repository.GetRosterForMember(actor);
                var guild = roster?.Guild;
                GuildResult result = GuildResult.NotFound;
                byte[] success = GuildCreationPacketBuilder.Ack(command);
                int target = 0;
                try
                {
                    switch (command)
                    {
                        case CmdPacketTypeA21.CANCEL_JOIN_GUILD:
                            if (TryId(body, out int guildId) && guildId > 0)
                                result = _repository.CancelApplication(actor, guildId);
                            break;
                        case CmdPacketTypeA21.DENY_JOIN_GUILD:
                            if (TryId(body, out target) && target > 0)
                                result = _repository.DenyApplication(actor, target);
                            break;
                        case CmdPacketTypeA21.REQ_GUILD_SECEDE:
                            // A21 captures use one name DSTR. Empty means self;
                            // a nonempty name selects a member of this guild only.
                            if (GuildTextRequest.TryParse(body, 62, out _, out var removedName))
                            {
                                var member = roster?.Members.FirstOrDefault(m => removedName.Length == 0
                                    ? m.CharacterId == actor : m.Name == removedName);
                                if (member != null)
                                {
                                    target = member.CharacterId;
                                    result = _repository.RemoveMember(actor, target);
                                    success = GuildManagementPacketBuilder.Left(member.Name, guild.Name, target != actor);
                                }
                            }
                            break;
                        case CmdPacketTypeA21.SET_SUB_GUILD_MASTER:
                            if (body?.Length >= 5 && body[^1] >= 2 && body[^1] <= 5
                                && GuildTextRequest.TryParse(body[..^1], 62, out _, out var name))
                            {
                                var member = roster?.Members.FirstOrDefault(m => m.Name == name);
                                if (member != null)
                                {
                                    target = member.CharacterId;
                                    result = _repository.ChangeRank(actor, target, body[^1]);
                                    success = GuildManagementPacketBuilder.RankChanged(member.Name, body[^1]);
                                }
                            }
                            break;
                        case CmdPacketTypeA21.NOTIFY_MESSAGE_TO_GUILD:
                        case CmdPacketTypeA21.MODIFY_GUILD_PROMOTE_MSG:
                        case CmdPacketTypeA21.WRITE_GUILD_MEMBER_MEMO:
                            var field = command == CmdPacketTypeA21.NOTIFY_MESSAGE_TO_GUILD ? GuildTextField.Announcement
                                : command == CmdPacketTypeA21.MODIFY_GUILD_PROMOTE_MSG ? GuildTextField.Promotion : GuildTextField.Memo;
                            if (GuildTextRequest.TryParse(body, GuildManagementRules.MaximumBytes(field), out _, out var text))
                                result = _repository.EditText(actor, field, text);
                            break;
                        case CmdPacketTypeA21.BREAK_GUILD:
                            if (body == null || body.Length == 0)
                            {
                                result = _repository.Disband(actor, out var affected);
                                if (result == GuildResult.Success) refresh.UnionWith(affected);
                            }
                            break;
                    }
                }
                catch (SqliteException ex)
                {
                    FileLogger.Log($"[Guild] management persistence failed cid={actor} command={command} code={ex.SqliteErrorCode}");
                    result = GuildResult.PersistenceFailed;
                }
                // A disconnected initiator must not prevent committed state
                // from being published to the other online members/applicants.
                await SessionDirectory.TrySendBestEffortAsync(ct => session.SendPacketAsync(
                    result == GuildResult.Success ? success : GuildCreationPacketBuilder.Ack(command, 0x22), ct),
                    $"guild management ack cid={actor}");
                if (result == GuildResult.Success)
                {
                    refresh.Add(actor);
                    if (target > 0) refresh.Add(target);
                    if (roster != null) foreach (var member in roster.Members) refresh.Add(member.CharacterId);
                }
                FileLogger.Log($"[Guild] management cid={actor} command={command} target={target} result={result}");
            }
            finally { for (int i = gates.Count - 1; i >= 0; i--) gates[i].Dispose(); }
            await _publisher.RefreshAsync(refresh.Where(id => id > 0), command == CmdPacketTypeA21.BREAK_GUILD);
        }

        private async Task<List<IDisposable>> AcquireAffectedAsync(int actor)
        {
            // Joining takes leader+applicant in the same ascending order. If a
            // join committed while we were waiting, release and include that member.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var guild = _repository.GetForMember(actor);
                var ids = new HashSet<int>(guild == null ? Array.Empty<int>() : _repository.GetMemberIds(guild.Id));
                ids.Add(actor);
                var gates = new List<IDisposable>();
                bool retained = false;
                try
                {
                    foreach (int id in ids.OrderBy(x => x)) gates.Add(await _transitions.AcquireAsync(id));
                    var current = _repository.GetForMember(actor);
                    if (current == null || _repository.GetMemberIds(current.Id).All(ids.Contains))
                    { retained = true; return gates; }
                }
                finally { if (!retained) for (int i = gates.Count - 1; i >= 0; i--) gates[i].Dispose(); }
            }
            return null;
        }

        internal static bool TryId(byte[] body, out int id)
        { id = body?.Length == 4 ? BitConverter.ToInt32(body, 0) : 0; return body?.Length == 4; }
    }
}
