using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.Names;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    internal sealed class CharacterRenameHandler
    {
        private readonly IGameDatabase _database;
        private readonly ICharacterRepository _characters;
        private readonly ISessionDirectory _sessions;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly Func<int, Task> _refreshGuild;

        internal CharacterRenameHandler(IGameDatabase database, ICharacterRepository characters,
            ISessionDirectory sessions, CharacterTransitionCoordinator transitions, Func<int, Task> refreshGuild = null)
        { _database = database; _characters = characters; _sessions = sessions; _transitions = transitions; _refreshGuild = refreshGuild; }

        // A21 026DB7D0, caller 023EF4BF: roster index, old DSTR, new DSTR, language byte.
        internal static bool TryParse(byte[] body, out byte index, out byte[] oldName, out byte[] newName)
        {
            index = 0; oldName = newName = null;
            if (body == null || body.Length < 14) return false;
            index = body[0];
            int offset = 1;
            byte[] ReadName()
            {
                if (offset > body.Length - 4) return null;
                int length = BitConverter.ToInt32(body, offset); offset += 4;
                if (length < 2 || length > 18 || length > body.Length - offset) return null;
                var name = body.AsSpan(offset, length).ToArray(); offset += length; return name;
            }
            oldName = ReadName(); newName = ReadName();
            return oldName != null && newName != null && offset == body.Length - 1;
        }

        internal async Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            int account = session?.Account?.AccountId ?? 0;
            bool Selecting() => account > 0 && session.Account?.AccountId == account
                && session.Player.CharacterId == 0 && session.Player.UserId == 0;
            if (!Selecting()) return;
            byte error = 19;
            bool committed = false;
            int characterId = 0;
            byte[] oldName = null, newName = null;
            if (TryParse(body, out byte index, out oldName, out newName))
            {
                if (!NameInputValidator.TryValidateRawName(newName, 2, 18, out _, out _))
                    error = NameInputValidator.InvalidNameErrorCode;
                else
                {
                    var roster = _characters.ListByAccount(account);
                    var character = index < roster.Count ? roster[index] : null;
                    if (character != null && character.AccountId == account && character.Name.AsSpan().SequenceEqual(oldName))
                    {
                        characterId = character.CharacterId;
                        using (await _transitions.AcquireAsync(characterId))
                        {
                            if (!Selecting() || _sessions.TryGet(characterId, out _)
                                || InventoryContext.TryGetLease(characterId, out _)) return;
                            try
                            {
                                using (var connection = _database.OpenConnection())
                                    InventoryContext.Register(session.SessionId,
                                        InventoryService.LoadFromDb(connection, characterId, account, _database));
                                if (InventoryContext.TryGetOwnedLease(session.SessionId, characterId, out var lease))
                                    committed = CharacterRenameCommitService.TryCommit(lease, account, oldName, newName,
                                        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                            }
                            finally { InventoryContext.Unregister(session.SessionId, characterId); }
                        }
                    }
                }
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.CHANGE_CHARAC_NAME,
                committed ? CommonPacketBodyBuilder.BuildSuccessAck() : CommonPacketBodyBuilder.BuildCmdError(error)));
            if (!committed) return;
            FileLogger.Log($"[CharacterRename] committed cid={characterId} aid={account}");
            await UnitedFriendSystem.HandleCharacterRenamedAsync(ClientTextEncoding.GetString(oldName),
                ClientTextEncoding.GetString(newName), _sessions, relationsCommitted: true);
            if (_refreshGuild != null) await _refreshGuild(characterId);
        }
    }
}
