using System;

namespace DfoServer.Network.Builders.Auction;

internal static class AuctionMutationReplyBuilder
{
    // Both native search senders feed the same ordinary result cache. The
    // complete auction ACK registration maps result BA to0111F970, not BB.
    internal static CmdPacketTypeA21 ReplyCommand(CmdPacketTypeA21 request)
        => request == CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY
            ? CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY : request;

    internal static byte[] Success(CmdPacketTypeA21 command)
        => command switch {
            CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE => new byte[] { 1 },
            CmdPacketTypeA21.AUCTION_REGIST_ITEM or CmdPacketTypeA21.AUCTION_REGIST_CANCEL
                or CmdPacketTypeA21.AUCTION_BIDDING => new byte[] { 1, 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

    // Generic153/busy158 avoid assigning a command-specific error the wrong
    // meaning. PIECE has no ordinary selector in either result branch.
    internal static byte[] Failure(CmdPacketTypeA21 command, bool busy = false)
        => command == CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE
            ? new byte[] { 0, busy ? (byte)158 : (byte)153 }
            : new byte[] { 0, busy ? (byte)158 : (byte)153, 0 };
}
