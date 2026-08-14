using Microsoft.Data.Sqlite;
using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Mailbox
{
    public sealed class MailboxService
    {
        private readonly MailboxRepository _repository;

        public MailboxService(MailboxRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public MailboxSendResult SendMail(MailboxSendRequest request)
        {
            return SendMail(request, null);
        }

        internal MailboxSendResult SendMail(MailboxSendRequest request, InventoryLease lease)
        {
            try
            {
                return lease == null
                    ? _repository.SendMail(request)
                    : _repository.SendMail(request, lease);
            }
            catch (SqliteException ex) when (IsDatabaseBusy(ex))
            {
                FileLogger.Log($"[Mailbox] SEND database busy sqlite={ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}");
                return MailboxSendResult.Fail(MailboxSendError.ServerBusy);
            }
        }

        public MailboxSendResult SendSystemMail(MailboxSendRequest request)
        {
            return _repository.SendSystemMail(request);
        }

        public MailboxSendResult SendSystemMails(IReadOnlyList<MailboxSendRequest> requests)
        {
            try
            {
                return _repository.SendSystemMails(requests);
            }
            catch (SqliteException ex) when (IsDatabaseBusy(ex))
            {
                FileLogger.Log($"[Mailbox] SYSTEM_SEND database busy sqlite={ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}");
                return MailboxSendResult.Fail(MailboxSendError.ServerBusy);
            }
        }

        internal MailboxSendResult SendSystemMails(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyList<MailboxSendRequest> requests)
        {
            try
            {
                return _repository.SendSystemMails(connection, transaction, requests);
            }
            catch (SqliteException ex) when (IsDatabaseBusy(ex))
            {
                FileLogger.Log($"[Mailbox] SYSTEM_SEND database busy sqlite={ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}");
                return MailboxSendResult.Fail(MailboxSendError.ServerBusy);
            }
        }

        public MailboxCampaignBatchResult ProcessSystemMailCampaignBatch(
            string campaignId,
            MailboxSendRequest template,
            int batchSize = 500)
        {
            return _repository.ProcessSystemMailCampaignBatch(campaignId, template, batchSize);
        }

        public MailboxExpirationBatchResult MaintainExpiredMail(int expireBatchSize = 200, int purgeBatchSize = 100)
        {
            try
            {
                return _repository.MaintainExpiredMail(expireBatchSize, purgeBatchSize);
            }
            catch (SqliteException ex) when (IsDatabaseBusy(ex))
            {
                FileLogger.Log($"[Mailbox] MAINTENANCE database busy sqlite={ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}");
                return new MailboxExpirationBatchResult();
            }
        }

        public IReadOnlyList<MailboxListEntry> LoadInbox(int characterId, int limit)
        {
            return _repository.LoadInbox(characterId, limit);
        }

        public MailboxInboxPage LoadInboxPage(int characterId, int limit)
        {
            return _repository.LoadInboxPage(characterId, limit);
        }

        public MailboxClaimResult ClaimMail(int characterId, long messageId)
        {
            return ClaimMail(characterId, messageId, null);
        }

        internal MailboxClaimResult ClaimMail(int characterId, long messageId, InventoryLease lease)
        {
            try
            {
                return lease == null
                    ? _repository.ClaimMail(characterId, messageId)
                    : _repository.ClaimMail(characterId, messageId, lease);
            }
            catch (SqliteException ex) when (IsDatabaseBusy(ex))
            {
                FileLogger.Log($"[Mailbox] CLAIM database busy cid={characterId} objectId={messageId} sqlite={ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}");
                return MailboxClaimResult.Fail(MailboxSendError.ServerBusy);
            }
        }

        public MailboxDeleteResult DeleteMail(int characterId, long messageId)
        {
            return _repository.DeleteMail(characterId, messageId);
        }

        public MailboxDeleteResult MarkMailRead(int characterId, long messageId)
        {
            return _repository.MarkMailRead(characterId, messageId);
        }

        public MailboxDeleteResult SaveMail(int characterId, long messageId)
        {
            return _repository.SaveMail(characterId, messageId);
        }

        private static bool IsDatabaseBusy(SqliteException exception)
        {
            return exception != null
                && (exception.SqliteErrorCode == 5 || exception.SqliteErrorCode == 6);
        }
    }
}
