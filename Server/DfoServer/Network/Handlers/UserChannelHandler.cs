using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Friends;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Friends;

namespace DfoServer.Network.Handlers
{
    internal sealed class UserChannelHandler
    {
        private readonly ISessionDirectory _sessions;
        private readonly CharacterTransitionCoordinator _transitions;

        internal UserChannelHandler(ISessionDirectory sessions, CharacterTransitionCoordinator transitions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
        }

        internal void RegisterHandlers(GameCommandRegistry.GameCommandRegistrationGroup group)
            => group[(ushort)CmdPacketTypeA21.REQUEST_USER_CHANNEL] = Handle;

        private Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (session?.Player == null) return Task.CompletedTask;
            var actorCurrent = UnitedFriendSystem.CaptureSessionIdentityCheck(session, _sessions);
            return _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!actorCurrent()) return;
                if (!UnitedFriendRequest.TryUserChannel(body, out var name))
                {
                    await SendFailure(0);
                    return;
                }

                var target = _sessions.GetAllGameSessions().FirstOrDefault(candidate =>
                    candidate.Player.Name != null
                    && ClientTextEncoding.GetString(candidate.Player.Name) == name
                    && candidate.Player.UserId != 0 && _transitions.IsCurrent(candidate));
                if (target == null)
                {
                    await SendFailure(100);
                    return;
                }

                var targetCurrent = UnitedFriendSystem.CaptureSessionIdentityCheck(target, _sessions);
                var channel = GameNetworkConfig.GetGameChannels().FirstOrDefault(candidate =>
                    candidate.ListenerGamePort == target.ListenerPort);
                if (channel == null)
                {
                    await SendFailure(0);
                    return;
                }

                var writer = new GamePacketWriter();
                writer.WriteByte(1);
                writer.WriteByte(GameNetworkConfig.ChannelServerIndex);
                writer.WriteClientDstr(name);
                // Native follow first filters the catalog by channel server, then resolves the channel.
                writer.WriteByte(GameNetworkConfig.ChannelServerIndex);
                writer.WriteByte(checked((byte)channel.ChannelId));
                bool sent = await session.TrySendPacketAsync(Packet(writer.ToArray()), default,
                    () => actorCurrent() && targetCurrent() && target.Player.Name != null
                        && ClientTextEncoding.GetString(target.Player.Name) == name);
                if (!sent && actorCurrent()) await SendFailure(100);

                Task SendFailure(byte error) => session.TrySendPacketAsync(
                    Packet(new byte[] { 0, error }), default, actorCurrent);
            });
        }

        private static byte[] Packet(byte[] body)
            => GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.REQUEST_USER_CHANNEL, body);
    }
}
