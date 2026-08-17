using DfoServer.Game.Mailbox;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using System;

namespace DfoServer.Network.Builders
{
    public sealed class MailboxBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0061;
        private const int MailboxPageSize = 20;
        private static readonly bool A21MailboxFullListEnabled = false;
        private readonly MailboxRepository _repository;

        public MailboxBodyBuilder()
            : this(GameDatabase.CreateDefault())
        {
        }

        public MailboxBodyBuilder(IGameDatabase database)
        {
            _repository = new MailboxRepository(
                database ?? throw new ArgumentNullException(nameof(database)));
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var characterId = snapshot.CharacterRecord?.CharacterId ?? 0;
            if (characterId <= 0)
            {
                body = new byte[6];
                return true;
            }

            try
            {
                if (!A21MailboxFullListEnabled)
                {
                    // A21 邮箱 0x0061 完整列表结构尚未逆完；进城阶段先发 6B 空 seed，避免带附件邮件导致客户端崩溃。
                    body = new byte[6];
                    FileLogger.Log($"[MailboxInit] a21 empty 6B cid={characterId}");
                    return true;
                }

                // Full 0x0061 state is needed during enter-town init. The old 6-byte seed only
                // updated the mailbox container count and did not make the town mailbox object
                // show its floating envelope until the player opened and closed the mailbox UI.
                var page = _repository.LoadInboxPage(characterId, MailboxPageSize);
                var notLoaded = ClampUInt16(page.NotLoadedCount);
                body = MailboxHandler.BuildMailboxListNotification(page.Entries, isFirstLoad: false, notLoadedCount: notLoaded);
                FileLogger.Log($"[MailboxInit] cid={characterId} entries={page.Entries.Count} notLoaded={page.NotLoadedCount}");
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[MailboxInit] full build failed cid={characterId}: {ex.Message}");
                body = new byte[6];
                return true;
            }
        }

        private static ushort ClampUInt16(int value)
        {
            if (value <= ushort.MinValue)
                return ushort.MinValue;
            if (value >= ushort.MaxValue)
                return ushort.MaxValue;
            return (ushort)value;
        }
    }
}
