using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Pvp;
using DfoServer.Network.Parsers.Pvp;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class PvpRoomHandler
    {
        internal Task HandleCheckTotalMatchTeamName(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => HandleTotalMatchTeamAsync(session, body, false);

        internal Task HandleSetTotalMatchTeam(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => HandleTotalMatchTeamAsync(session, body, true);

        private async Task HandleTotalMatchTeamAsync(EnhancedClientSession session, byte[] body, bool setMembers)
        {
            if (_database == null)
                return;
            await _characterTransitions.RunIfCurrentAsync(session, async () =>
            {
                var accountId = session.Account?.AccountId ?? 0;
                var characterId = session.Player.CharacterId;
                var command = setMembers ? CmdPacketTypeA21.SET_PVP_TOTAL_MATCH_TEAM
                    : CmdPacketTypeA21.CHECK_PVP_TOTAL_MATCH_TEAM_NAME;
                byte[] response;
                PvpTotalMatchTeam team = null;
                // Room configuration is available from a waiting PvP room as
                // well as town. Serialize validation/commit with room start.
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (!CanUseTotalMatchTeam(session, characterId, accountId))
                    {
                        FileLogger.Log($"[GameProtocol] {command} ignored: cid={characterId} state={session.Player.UserState} townReady={session.Player.TownPresenceReady}");
                        return;
                    }
                    var repository = new SqlitePvpTotalMatchTeamRepository(_database);
                    if (!PvpTotalMatchTeamRequest.TryParse(body, setMembers, out var nameBytes, out var slots)
                        || !SqlitePvpTotalMatchTeamRepository.IsValidName(nameBytes, out var name))
                    {
                        response = new byte[] { 0, Game.Names.NameInputValidator.InvalidNameErrorCode };
                    }
                    else if (setMembers)
                    {
                        response = repository.TrySet(accountId, characterId, nameBytes, slots, out team, out var error)
                            ? PvpTotalMatchTeamBodyBuilder.BuildSuccessBody(team)
                            : new byte[] { 0, error };
                    }
                    else
                    {
                        // 110D6A0: 01 enables confirm, 00 03 displays duplicate name.
                        response = repository.IsNameAvailable(accountId, name)
                            ? new byte[] { 1 } : new byte[] { 0, 3 };
                    }
                }
                finally
                {
                    _roomPublicationGate.Release();
                }

                bool CanSend() => CanUseTotalMatchTeam(session, characterId, accountId);
                if (team != null && !await session.TrySendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.PVP_TOTAL_MATCH_TEAM_INFO,
                            PvpTotalMatchTeamBodyBuilder.BuildBody(team)), CancellationToken.None, CanSend))
                    return;
                await session.TrySendPacketAsync(GamePacketEnvelopeBuilder.Build(1, (ushort)command, response),
                    CancellationToken.None, CanSend);
                FileLogger.Log($"[GameProtocol] {command}: cid={characterId} success={response[0]} bytes={response.Length}");
            });
        }

        private bool CanUseTotalMatchTeam(EnhancedClientSession session, int characterId, int accountId)
        {
            if (session?.Account?.AccountId != accountId || accountId <= 0
                || session.Player.CharacterId != characterId || session.GameSession == null
                || session.Player.CurrentRun != null || !_characterTransitions.IsCurrent(session))
                return false;
            if (_rooms.TryGetRoomForMember(characterId, session.SessionId, out var room, out _))
            {
                if (session.Player.UserState != 2 || room.ListenerPort != session.ListenerPort
                    || room.RoomState != FreeDuelRoom.WaitingRoomState
                    || room.SettlementPhase != FreeDuelRoom.WaitingSettlementPhase)
                    return false;
            }
            else if (!session.Player.TownPresenceReady || session.Player.UserState != 0)
                return false;
            var character = new SqliteCharacterRepository(_database).GetById(characterId);
            return character != null && !character.Deleted && character.AccountId == accountId;
        }
    }
}
