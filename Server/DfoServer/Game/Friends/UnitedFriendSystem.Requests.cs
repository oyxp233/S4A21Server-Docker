using System.Threading.Tasks;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Friends;
using DfoServer.Network.Parsers.Friends;

namespace DfoServer.Game.Friends
{
    public static partial class UnitedFriendSystem
    {
        // The ordinary friend panel holds at most 64 entries.
        internal const int MaximumFriends = 64;

        internal static void RegisterHandlers(GameCommandRegistry.GameCommandRegistrationGroup group,
            ISessionDirectory sessions, CharacterTransitionCoordinator transitions)
        {
            group[(ushort)CmdPacketTypeA21.ADD_UNITED_SERVER_FRIEND] =
                (s, h, b) => HandleAddUnitedServerFriend(s, h, b, sessions, transitions);
            group[(ushort)CmdPacketTypeA21.DELETE_UNITED_SERVER_FRIEND] =
                (s, h, b) => HandleDeleteUnitedServerFriend(s, h, b, sessions, transitions);
        }

        private static Task<bool> SendFriendAck(EnhancedClientSession session, CmdPacketTypeA21 command, byte error = 0)
            => SessionDirectory.TrySendBestEffortAsync(token => session.SendPacketAsync(
                UnitedFriendPacketBuilder.Ack(command, error), token), $"friend ack {command}");

        internal static async Task HandleAddUnitedServerFriend(EnhancedClientSession session,
            GamePacketHeader header, byte[] body, ISessionDirectory sessions, CharacterTransitionCoordinator transitions)
        {
            const CmdPacketTypeA21 command = CmdPacketTypeA21.ADD_UNITED_SERVER_FRIEND;
            var actorId = session.Player.CharacterId;
            var actorUid = session.Player.UserId;
            var actorName = GetPlayerName(session);
            await transitions.RunIfCurrentAsync(session, async () =>
            {
                // The same transport can select a different character while waiting for its gate.
                if (session.Player.CharacterId != actorId || session.Player.UserId != actorUid
                    || GetPlayerName(session) != actorName) return;
                byte error = 0;
                string targetName = null;
                lock (Sync)
                {
                    try
                    {
                        if (!UnitedFriendRequest.TryAdd(body, out targetName)) error = 0x15;
                        else if (string.IsNullOrEmpty(actorName) || actorUid == 0 || actorName == targetName) error = 0x01;
                        // Request UID is a hint, not an identity. Existing offline characters are valid targets.
                        else if (CharacterRepository.GetByName(targetName) == null) error = 0x15;
                        else if (!IsFriend(actorName, targetName) && GetFriends(actorName).Count >= MaximumFriends) error = 0x04;
                        else RecordFriendship(actorName, targetName);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex)
                    {
                        FileLogger.Log($"[UnitedFriend] add persistence failed: {ex}");
                        error = 0x01;
                    }
                }
                await SendFriendAck(session, command, error);
                // Only the owner changed. Ordinary friends never use the PVP buddy invitation flow.
                if (error == 0) await PublishFriendList(session, sessions, targetName);
            });
        }

        internal static async Task HandleDeleteUnitedServerFriend(EnhancedClientSession session,
            GamePacketHeader header, byte[] body, ISessionDirectory sessions, CharacterTransitionCoordinator transitions)
        {
            const CmdPacketTypeA21 command = CmdPacketTypeA21.DELETE_UNITED_SERVER_FRIEND;
            var actorId = session.Player.CharacterId;
            var actorUid = session.Player.UserId;
            var actorName = GetPlayerName(session);
            await transitions.RunIfCurrentAsync(session, async () =>
            {
                if (session.Player.CharacterId != actorId || session.Player.UserId != actorUid
                    || GetPlayerName(session) != actorName) return;
                byte error = 0;
                string targetName = null;
                lock (Sync)
                {
                    try
                    {
                        if (!UnitedFriendRequest.TryDelete(body, out targetName)
                            || !RemoveFriendship(actorName, targetName)) error = 0x05;
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex)
                    {
                        FileLogger.Log($"[UnitedFriend] delete persistence failed: {ex}");
                        error = 0x05;
                    }
                }
                await SendFriendAck(session, command, error);
                if (error == 0) await PublishFriendList(session, sessions, removedName: targetName);
            });
        }

        private static async Task PublishFriendList(EnhancedClientSession session, ISessionDirectory sessions,
            string addedName = null, string removedName = null)
        {
            await SessionDirectory.TrySendBestEffortAsync(async token =>
            {
                // Clear the entity friend flag before rebuilding the list.
                if (removedName != null) await SendFriendDeletedAsync(session, removedName, sessions, token);
                // Native full snapshot rebuild first, then the same-channel identity projection.
                await SendFriendListAsync(session, sessions, GetFriends(GetPlayerName(session)), token);
                if (addedName != null) await NotifyFriendAddedAsync(session, addedName, sessions, token);
            }, $"friend list cid={session.Player.CharacterId}");
        }
    }
}
