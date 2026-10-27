using System;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Auction.Storage
{
    // The deletion mutation itself runs under the auction gate. A separate
    // check-then-delete would race a new bid, listing or funded mail delivery.
    internal sealed class AuctionCharacterLiabilityGuard
    {
        private readonly AuctionOrderStore store;
        private readonly IGameDatabase publicDatabase;
        private readonly object marketGate;
        internal AuctionCharacterLiabilityGuard(AuctionOrderStore store, IGameDatabase publicDatabase, object sharedMarketGate)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.publicDatabase = publicDatabase ?? throw new ArgumentNullException(nameof(publicDatabase));
            marketGate = sharedMarketGate ?? throw new ArgumentNullException(nameof(sharedMarketGate));
        }

        internal bool TryExecuteWhenClear(int characterId, Action deleteMutation)
        {
            if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
            ArgumentNullException.ThrowIfNull(deleteMutation);
            lock (marketGate)
            {
                if (store.HasHeldAssetsForCharacter(characterId)) return false;
                // Completed delivery history alone never blocks deletion.
                // Query the actual public claim state, including a held claim=2.
                var messages = store.DeliveryMessageIds(characterId);
                bool unclaimed = publicDatabase.Read(c => {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = @"SELECT EXISTS(SELECT 1 FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id=r.message_id
WHERE r.character_id=@cid AND r.message_id=@message AND r.folder=0
AND ((m.gold>0 AND r.received_gold_flag<>1) OR EXISTS(
 SELECT 1 FROM mailbox_attachments a WHERE a.message_id=m.message_id AND a.claimed_flag<>1)));";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    var message = cmd.Parameters.AddWithValue("@message", 0L);
                    foreach (long id in messages)
                    { message.Value = id; if (Convert.ToInt32(cmd.ExecuteScalar()) != 0) return true; }
                    return false;
                });
                if (unclaimed) return false;
                deleteMutation();
                return true;
            }
        }
    }
}
