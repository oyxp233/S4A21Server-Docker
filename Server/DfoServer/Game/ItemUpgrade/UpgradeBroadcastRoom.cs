using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Network;

namespace DfoServer.Game.ItemUpgrade
{
    // Runtime projection only. Equipment remains in the host's owned inventory.
    internal sealed class UpgradeBroadcastRoom
    {
        internal ushort Id;
        internal EnhancedClientSession Host;
        internal int CharacterId;
        internal ushort HostUid;
        internal int Listener;
        internal byte Town, Area;
        internal short X, Y;
        internal byte[] Title;
        internal readonly HashSet<EnhancedClientSession> Viewers = new();
        internal short Slot = -1;
        internal ItemCore Selected;
        internal byte State;
        internal long Revision;
        internal bool Closed;
        internal ItemUpgradeResult LastResult;
        internal readonly HashSet<(ushort Sequence, uint Checksum)> Starts = new();
        internal readonly Queue<(ushort Sequence, uint Checksum)> StartOrder = new();
        internal readonly HashSet<(ushort Sequence, uint Checksum)> Completions = new();
        internal readonly Queue<(ushort Sequence, uint Checksum)> CompletionOrder = new();
        internal readonly Dictionary<int, int> Donations = new();
        // Reserved-but-uncommitted totals. Kept separate so the client never
        // renders a donation before its wallet transaction commits.
        internal readonly Dictionary<int, int> PendingDonations = new();
        internal readonly HashSet<(System.Guid Session, ushort Sequence, uint Checksum)> Gifts = new();
        internal readonly Queue<(System.Guid Session, ushort Sequence, uint Checksum)> GiftOrder = new();
    }
}
