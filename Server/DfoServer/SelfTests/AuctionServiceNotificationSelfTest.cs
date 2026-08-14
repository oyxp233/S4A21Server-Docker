using DfoServer.Network.Builders.Auction;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class AuctionServiceNotificationSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== AUCTION_SERVICE_NOTIFICATION selftest ===");
            var failures = 0;

            var packets = AuctionServiceNotificationBuilder.BuildOpenPackets();
            var hasTwoPackets = packets != null && packets.Length == 2;

            Check("two auction service packets", hasTwoPackets, ref failures);
            Check("service type 0 is open",
                hasTwoPackets && IsAuctionOpenPacket(packets[0], 0x00),
                ref failures);
            Check("service type 1 is open",
                hasTwoPackets && IsAuctionOpenPacket(packets[1], 0x01),
                ref failures);

            Console.WriteLine($"=== AUCTION_SERVICE_NOTIFICATION selftest result: fail={failures} ===");
            return failures == 0 ? 0 : 1;
        }

        private static bool IsAuctionOpenPacket(byte[] packet, byte serviceType)
        {
            return packet != null
                && packet.SequenceEqual(new byte[]
                {
                    0x00,
                    0xB7, 0x00,
                    0x11, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00,
                    serviceType, 0x01
                });
        }

        private static void Check(string label, bool passed, ref int failures)
        {
            Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {label}");
            if (!passed)
                failures++;
        }
    }
}
