using System;
using System.Threading.Tasks;
using DfoServer.Game.Pvp;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Pvp;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class PvpRoomHandler
    {
        internal async Task HandleSetMapIndex(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await _characterTransitions.RunIfCurrentAsync(session, async () =>
            {
                // 13AD8E0 sends only an i16 index. The room-state notification
                // carries the accepted map to both occupants and lobby viewers.
                if (!CanMutateOwnedRoom(session) || body?.Length != 2)
                {
                    FileLogger.Log("[GameProtocol] SET_PVP_MAP_INDEX rejected: invalid state or request length");
                    return;
                }
                Task publication = Task.CompletedTask;
                byte error = 0;
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (!CanMutateOwnedRoom(session)
                        || !_rooms.TryGetRoomForMember(session.Player.CharacterId, session.SessionId,
                            out var current, out _)
                        || _pendingRoomJoinSessions.ContainsKey(current.RoomId))
                        error = 19;
                    else if (_rooms.TrySetMapIndex(session.Player.CharacterId, session.SessionId,
                                 BitConverter.ToInt16(body, 0), out var room, out error))
                        publication = QueueRequiredToReadyListener(room.ListenerPort, null,
                            GamePacketEnvelopeBuilder.Build(0, RoomStateNotificationType,
                                PvpRoomNotificationBuilder.BuildRoomStateBody(room)));
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
                if (error != 0)
                    // 1144AF0/163C190 register no command reply for this request.
                    // Only the accepted ROOM_STATE notification has a consumer.
                    FileLogger.Log($"[GameProtocol] SET_PVP_MAP_INDEX rejected: cid={session.Player.CharacterId} reason={error}");
                else
                    await publication;
            });
        }
    }
}
