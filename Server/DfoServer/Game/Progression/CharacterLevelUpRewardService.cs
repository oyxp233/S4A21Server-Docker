using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Progression
{
    internal sealed record CharacterLevelUpRewardDelivery(int CharacterId, byte Level, long MessageId,
        int ItemId, int ItemCount);

    // 只消费已经随升级提交的事实。邮件、永久发放账本与事件消费标记在同一事务提交。
    internal sealed class CharacterLevelUpRewardService
    {
        private readonly IGameDatabase _database;
        private readonly MailboxService _mailbox;
        private readonly Func<IReadOnlyDictionary<byte, CharacterLevelUpReward>> _rewards;
        private readonly Func<DateTimeOffset> _utcNow;

        internal CharacterLevelUpRewardService(IGameDatabase database, MailboxService mailbox,
            Func<IReadOnlyDictionary<byte, CharacterLevelUpReward>> rewards = null,
            Func<DateTimeOffset> utcNow = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            _rewards = rewards ?? CharacterLevelUpRewardCatalog.Get;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        internal IReadOnlyList<CharacterLevelUpRewardDelivery> ProcessPending(int batchSize = 32)
        {
            var now = _utcNow().ToUniversalTime();
            var ids = _database.Read(connection =>
                CharacterLevelUpEventRepository.LoadPendingIds(connection, now.ToUnixTimeSeconds(), batchSize));
            var delivered = new List<CharacterLevelUpRewardDelivery>();
            foreach (var id in ids)
            {
                try
                {
                    delivered.AddRange(ProcessEvent(id, now));
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Progression] level-up reward pending event={id}: {ex}");
                    try
                    {
                        _database.Write((connection, transaction) =>
                            CharacterLevelUpEventRepository.Defer(connection, transaction, id, now));
                    }
                    catch (Exception retryError)
                    {
                        FileLogger.Log($"[Progression] level-up reward retry bookkeeping failed event={id}: {retryError}");
                    }
                }
            }
            return delivered;
        }

        private IReadOnlyList<CharacterLevelUpRewardDelivery> ProcessEvent(long eventId, DateTimeOffset now)
        {
            // 配置读取不占写锁。失败不会把事件标记为已消费。
            var rewards = _rewards();
            return _database.Write<IReadOnlyList<CharacterLevelUpRewardDelivery>>((connection, transaction) =>
            {
                var change = CharacterLevelUpEventRepository.LoadPending(connection, transaction,
                    eventId, now.ToUnixTimeSeconds(), out int accountId, out string name, out bool deleted);
                var result = new List<CharacterLevelUpRewardDelivery>();
                if (change == null) return result;
                if (!deleted)
                {
                    for (int level = change.PreviousLevel + 1; level <= change.NewLevel; level++)
                    {
                        if (!rewards.TryGetValue((byte)level, out var reward)
                            || CharacterLevelUpRewardRepository.WasDelivered(connection, transaction,
                                change.CharacterId, (byte)level))
                            continue;

                        var send = _mailbox.SendSystemMails(connection, transaction,
                            new[] { BuildMail(change.CharacterId, accountId, name, reward, now) });
                        if (!send.Success)
                            throw new InvalidOperationException($"level-up mail rejected cid={change.CharacterId} level={level}: {send.Error}");
                        CharacterLevelUpRewardRepository.RecordDelivery(connection, transaction,
                            change.CharacterId, reward, send.MessageId, now);
                        result.Add(new CharacterLevelUpRewardDelivery(change.CharacterId, (byte)level, send.MessageId,
                            reward.ItemId, reward.ItemCount));
                    }
                }
                CharacterLevelUpEventRepository.Complete(connection, transaction, eventId, now);
                return result;
            });
        }

        internal static MailboxSendRequest BuildMail(int characterId, int accountId, string name,
            CharacterLevelUpReward reward, DateTimeOffset deliveredAtUtc)
        {
            string message = $"恭喜你达到{reward.Level}级，获得升级奖励";
            return new MailboxSendRequest
            {
                SenderCharacterId = characterId,
                SenderAccountId = accountId,
                SenderName = string.Empty,
                ReceiverCharacterId = characterId,
                ReceiverAccountId = accountId,
                ReceiverName = name,
                ReceiverLevel = reward.Level,
                Title = message,
                Text = message,
                MailType = 1,
                Unlimited = false,
                ExpireAtUtc = deliveredAtUtc.ToUniversalTime().AddDays(30),
                AuditActor = "DNF管理员GM",
                AuditReason = $"character-level-up:{reward.Level}",
                IdempotencyKey = $"level-up-reward:{characterId}:{reward.Level}",
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest { ItemId = reward.ItemId, ItemCount = reward.ItemCount },
                },
            };
        }
    }
}
