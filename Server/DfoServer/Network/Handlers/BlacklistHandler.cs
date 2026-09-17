using DfoServer.Game.Friends;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Network.Builders.Friends;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class BlacklistHandler
    {
        private readonly BlacklistRepository _repository;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly BlacklistProjection _projection;
        internal BlacklistHandler(BlacklistRepository repository, CharacterTransitionCoordinator transitions,
            BlacklistProjection projection)
        { _repository = repository; _transitions = transitions; _projection = projection; }

        internal void RegisterHandlers(GameCommandRegistry.GameCommandRegistrationGroup group)
        {
            group[(ushort)CmdPacketTypeA21.REGISITER_TO_BLACKLIST] = Handle;
            group[(ushort)CmdPacketTypeA21.DELETE_TO_BLACKLIST] = Handle;
            group[(ushort)CmdPacketTypeA21.REQUEST_BLACKLIST] = Handle;
        }

        internal Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            int actor = session?.Player?.CharacterId ?? 0;
            if (actor <= 0) return Task.CompletedTask;
            ushort userId = session.Player.UserId;
            byte[] actorName = session.Player.Name?.ToArray();
            bool IsCurrent() => session.Player.CharacterId == actor && session.Player.UserId == userId
                && actorName != null && session.Player.Name != null && session.Player.Name.SequenceEqual(actorName)
                && _transitions.IsCurrent(session);
            var command = (CmdPacketTypeA21)header.type;
            return _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!IsCurrent()) return;
                var projection = _projection.For(session);
                byte[] packet;
                Action onSent = null;
                try
                {
                    if (command == CmdPacketTypeA21.REQUEST_BLACKLIST)
                    {
                        if (body == null || body.Length == 0)
                        {
                            await _projection.PublishAsync(session);
                            return;
                        }
                        packet = Failure(command);
                    }
                    else if (!TryParseName(body, out var name)) packet = Failure(command);
                    else if (command == CmdPacketTypeA21.REGISITER_TO_BLACKLIST)
                    {
                        var result = _repository.Add(actor, name);
                        packet = AddAck(result.Status, name);
                        if (result.Status == BlacklistResult.Success)
                        {
                            onSent = () =>
                            {
                                foreach (var previous in projection.Names.Where(e => e.Value == name).ToArray())
                                    projection.Names.TryRemove(previous.Key, out _);
                                projection.Names[result.TargetId] = name;
                            };
                        }
                    }
                    else
                    {
                        int publishedId = projection.Names.FirstOrDefault(e => e.Value == name).Key;
                        bool removed = publishedId != 0 ? _repository.Remove(actor, publishedId) : _repository.Remove(actor, name);
                        packet = Ack(command, removed ? (byte)0 : (byte)75, name);
                        if (removed) onSent = () => projection.Names.TryRemove(publishedId, out _);
                    }
                }
                catch (SqliteException ex)
                {
                    FileLogger.Log($"[Blacklist] persistence failed cid={actor} command={command} code={ex.SqliteErrorCode}");
                    packet = Failure(command);
                }
                await session.TrySendPacketAsync(packet, default, IsCurrent, onSent);
            });
        }

        internal static bool TryParseName(byte[] body, out string name)
        {
            name = null;
            if (body == null || body.Length < 5 || body.Length > 33
                || BitConverter.ToInt32(body, 0) != body.Length - 4) return false;
            return ClientTextEncoding.TryGetStringStrict(body.AsSpan(4).ToArray(), out name)
                && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);
        }

        // A21 common ACK consumes result/error before the blacklist callback reads its DSTR.
        internal static byte[] AddAck(BlacklistResult result, string name)
            => result switch
            {
                BlacklistResult.Success => Ack(CmdPacketTypeA21.REGISITER_TO_BLACKLIST, 0, name),
                BlacklistResult.Duplicate => Ack(CmdPacketTypeA21.REGISITER_TO_BLACKLIST, 74),
                BlacklistResult.NotFound => Ack(CmdPacketTypeA21.REGISITER_TO_BLACKLIST, 76),
                BlacklistResult.Full => Ack(CmdPacketTypeA21.REGISITER_TO_BLACKLIST, 78),
                _ => Failure(CmdPacketTypeA21.REGISITER_TO_BLACKLIST)
            };

        internal static byte[] Failure(CmdPacketTypeA21 command)
            => GamePacketEnvelopeBuilder.Build(1, (ushort)command, new byte[] { 0, 0 });

        internal static byte[] Ack(CmdPacketTypeA21 command, byte error, string name = null)
            => BlacklistPacketBuilder.Ack(command, error, name);

        internal static byte[] BuildList(IReadOnlyList<BlacklistEntry> entries)
            => BlacklistPacketBuilder.List(entries);
    }
}
