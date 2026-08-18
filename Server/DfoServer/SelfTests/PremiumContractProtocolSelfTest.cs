using DfoServer.Game.Premium;
using DfoServer.Network;
using System;

namespace DfoServer.SelfTests
{
    public static class PremiumContractProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== PREMIUM_CONTRACT_PROTOCOL selftest ===");
            var failures = 0;

            Check(
                "A21 premium query uses CMD 0x036F",
                (ushort)CmdPacketTypeA21.PREMIUM_SERVICE == 0x036F,
                ref failures);
            Check(
                "A21 premium state uses NOTI 0x032F",
                (ushort)NotiPacketTypeA21.PREMIUM_SERVICE == 0x032F,
                ref failures);
            Check(
                "A21 contract activation uses CERA_SPECIALITEM 0x0042",
                (ushort)NotiPacketTypeA21.CERA_SPECIALITEM == 0x0042,
                ref failures);
            Check(
                "Devil contract storage slots remain isolated from PVF premium types",
                DevilContractCatalog.SlotPremiumTypeBase == 580
                && DevilContractCatalog.SlotCount == 8
                && DevilContractCatalog.SlotToPremiumType(6) == 586,
                ref failures);

            var body = PremiumService.BuildPremiumServiceStateBody(
                PremiumService.DefaultServiceType,
                new byte[74]);
            Check(
                "A21 premium state body is status + type + 74-byte data",
                body.Length == 77
                && body[0] == 1
                && BitConverter.ToUInt16(body, 1) == PremiumService.DefaultServiceType,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "PREMIUM_CONTRACT_PROTOCOL selftest passed."
                    : $"PREMIUM_CONTRACT_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
