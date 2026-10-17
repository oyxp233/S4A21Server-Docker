using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Guilds;
using DfoServer.Network.Parsers.Guilds;

namespace DfoServer.Network.Handlers
{
    internal sealed class GuildInviteHandler
    {
        private readonly GuildRepository _repository;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly ISessionDirectory _sessions;
        private readonly GuildStatePublisher _publisher;
        private readonly ConcurrentDictionary<int, PendingInvite> _pending = new();

        internal GuildInviteHandler(GuildRepository repository, CharacterTransitionCoordinator transitions,
            ISessionDirectory sessions, GuildStatePublisher publisher)
        {
            _repository = repository;
            _transitions = transitions;
            _sessions = sessions;
            _publisher = publisher;
        }

        internal async Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var command = (CmdPacketTypeA21)header.type;
            int actor = session.Player.CharacterId;
            if (actor <= 0) return;
            if (command == CmdPacketTypeA21.CALL_GUILD_INVITE)
                await Invite(session, actor, body);
            else if (command == CmdPacketTypeA21.REPLY_GUILD_INVITE)
                await Reply(session, actor, body);
        }

        private async Task Invite(EnhancedClientSession session, int actor, byte[] body)
        {
            GuildResult result = GuildResult.NotFound;
            PendingInvite invite = null;
            EnhancedClientSession targetSession = null;
            if (!GuildTextRequest.TryParse(body, 62, out _, out var targetName) || targetName.Length == 0)
                result = GuildResult.InvalidName;
            else
            {
                var gates = await Acquire(actor);
                if (gates == null) return;
                try
                {
                    if (!_transitions.IsCurrent(session) || !InventoryContext.TryGetOwnedLease(session.SessionId, actor, out _))
                        return;
                    var roster = _repository.GetRosterForMember(actor);
                    int targetId = _repository.FindActiveCharacterId(targetName);
                    if (roster == null || targetId <= 0 || targetId == actor)
                        result = targetId == actor ? GuildResult.InvalidName : GuildResult.NotFound;
                    else if (_repository.IsMember(targetId))
                        result = GuildResult.AlreadyMember;
                    else if (!_sessions.TryGet(targetId, out targetSession) || !_transitions.IsCurrent(targetSession))
                        result = GuildResult.NotFound;
                    else
                    {
                        int online = roster.Members.Count(member =>
                            _sessions.TryGet(member.CharacterId, out var onlineSession)
                            && _transitions.IsCurrent(onlineSession));
                        invite = new PendingInvite(roster.Guild.Id, actor, roster.Guild.Name,
                            roster.Guild.Promotion ?? string.Empty, checked((ushort)online));
                        _pending[targetId] = invite;
                        result = GuildResult.Success;
                    }
                }
                finally { Release(gates); }
            }
            if (result != GuildResult.Success)
                FileLogger.Log($"[Guild] invite rejected cid={actor} result={result} len={body?.Length ?? -1} hex={Hex(body)}");
            await SessionDirectory.TrySendBestEffortAsync(ct => session.SendPacketAsync(
                GuildCreationPacketBuilder.Ack(CmdPacketTypeA21.CALL_GUILD_INVITE, result == GuildResult.Success ? (byte)0 : (byte)0x22), ct),
                $"guild invite ack cid={actor}");
            if (result == GuildResult.Success && targetSession != null)
                await SessionDirectory.TrySendBestEffortAsync(ct => targetSession.SendPacketAsync(
                    GuildInvitePacketBuilder.Invited(invite.GuildName, invite.OnlineCount, invite.Promotion), ct),
                    $"guild invite noti target={targetSession.Player.CharacterId}");
        }

        private async Task Reply(EnhancedClientSession session, int actor, byte[] body)
        {
            if (!TryReply(body, out bool accept))
            {
                FileLogger.Log($"[Guild] invite reply rejected cid={actor} len={body?.Length ?? -1} hex={Hex(body)}");
                await Ack(session, actor, false);
                return;
            }
            if (!_pending.TryGetValue(actor, out var invite))
            {
                await Ack(session, actor, false);
                return;
            }
            var gates = await Acquire(_repository.GetMemberIds(invite.GuildId).Append(actor).ToArray());
            if (gates == null) return;
            GuildResult result = GuildResult.NotFound;
            var refresh = new HashSet<int>();
            try
            {
                if (!_transitions.IsCurrent(session) || !InventoryContext.TryGetOwnedLease(session.SessionId, actor, out _))
                    return;
                if (!_pending.TryGetValue(actor, out invite))
                    result = GuildResult.NotFound;
                else if (!accept)
                {
                    _pending.TryRemove(actor, out _);
                    result = GuildResult.Success;
                }
                else
                {
                    result = _repository.AcceptInvite(invite.GuildId, invite.InviterId, actor);
                    if (result == GuildResult.Success)
                    {
                        _pending.TryRemove(actor, out _);
                        refresh.Add(actor);
                        foreach (var member in _repository.GetMemberIds(invite.GuildId)) refresh.Add(member);
                    }
                }
            }
            finally { Release(gates); }
            await Ack(session, actor, result == GuildResult.Success);
            if (_sessions.TryGet(invite.InviterId, out var caller))
                await SessionDirectory.TrySendBestEffortAsync(ct => caller.SendPacketAsync(
                    GuildInvitePacketBuilder.ReplyToCaller(accept && result == GuildResult.Success), ct),
                    $"guild invite reply caller={invite.InviterId}");
            if (refresh.Count > 0) await _publisher.RefreshAsync(refresh);
            FileLogger.Log($"[Guild] invite reply cid={actor} accept={accept} result={result}");
        }

        private static bool TryReply(byte[] body, out bool accept)
        {
            accept = false;
            if (body == null || body.Length == 0 || body[0] > 1) return false;
            accept = body[0] == 1;
            return body.Length == 1 || GuildTextRequest.TryParse(body.AsSpan(1).ToArray(), 62, out _, out _);
        }

        private async Task<List<IDisposable>> Acquire(params int[] extra)
        {
            var ids = extra.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
            var gates = new List<IDisposable>(ids.Length);
            try
            {
                foreach (int id in ids) gates.Add(await _transitions.AcquireAsync(id));
                return gates;
            }
            catch
            {
                Release(gates);
                return null;
            }
        }

        private static void Release(List<IDisposable> gates)
        {
            if (gates == null) return;
            for (int i = gates.Count - 1; i >= 0; i--) gates[i].Dispose();
        }

        private static Task Ack(EnhancedClientSession session, int actor, bool success)
            => SessionDirectory.TrySendBestEffortAsync(ct => session.SendPacketAsync(
                GuildCreationPacketBuilder.Ack(CmdPacketTypeA21.REPLY_GUILD_INVITE, success ? (byte)0 : (byte)0x22), ct),
                $"guild invite reply ack cid={actor}");

        private static string Hex(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            int length = Math.Min(body.Length, 64);
            return Convert.ToHexString(body.AsSpan(0, length));
        }

        private sealed record PendingInvite(int GuildId, int InviterId, string GuildName, string Promotion, ushort OnlineCount);
    }
}
