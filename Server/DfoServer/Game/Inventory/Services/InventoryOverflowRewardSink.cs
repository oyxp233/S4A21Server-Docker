using System.Collections.Generic;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryOverflowDeliveryStatus
    {
        None = 0,
        MailUnavailable = 1,
    }

    internal sealed class InventoryOverflowDeliveryResult
    {
        public InventoryOverflowDeliveryStatus Status { get; set; }
    }

    internal interface IInventoryOverflowRewardSink
    {
        bool TryDeliver(
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantRequest> rewards,
            out InventoryOverflowDeliveryResult result);
    }

    internal sealed class RejectingInventoryOverflowRewardSink : IInventoryOverflowRewardSink
    {
        internal static readonly RejectingInventoryOverflowRewardSink Instance =
            new RejectingInventoryOverflowRewardSink();

        private RejectingInventoryOverflowRewardSink()
        {
        }

        public bool TryDeliver(
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantRequest> rewards,
            out InventoryOverflowDeliveryResult result)
        {
            result = new InventoryOverflowDeliveryResult
            {
                Status = InventoryOverflowDeliveryStatus.MailUnavailable,
            };
            return false;
        }
    }

    internal sealed class TransactionBoundInventoryOverflowRewardSink : IInventoryOverflowRewardSink
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly IInventoryOverflowRewardSink _inner;
        private readonly string _title;
        private readonly string _text;

        internal TransactionBoundInventoryOverflowRewardSink(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IInventoryOverflowRewardSink inner,
            string title = null,
            string text = null)
        {
            _connection = connection;
            _transaction = transaction;
            _inner = inner ?? RejectingInventoryOverflowRewardSink.Instance;
            _title = title;
            _text = text;
        }

        internal bool MailboxDeliveryFailed { get; private set; }

        public bool TryDeliver(
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantRequest> rewards,
            out InventoryOverflowDeliveryResult result)
        {
            if (_inner is MailboxInventoryOverflowRewardSink mailbox)
            {
                var delivered = mailbox.TryDeliver(
                    _connection,
                    _transaction,
                    inventory,
                    rewards,
                    _title,
                    _text,
                    out result);
                MailboxDeliveryFailed = !delivered;
                return delivered;
            }

            return _inner.TryDeliver(inventory, rewards, out result);
        }
    }
}
