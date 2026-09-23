using System;
using System.Reflection;

namespace DfoServer.SelfTests
{
    internal static class A21MouseRegistrationSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== A21_MOUSE_REGISTRATION selftest ===");
            var builderType = typeof(Program).Assembly.GetType(
                "DfoServer.Network.Builders.MouseRegistrationDiscardPacketBuilder");
            var build = builderType?.GetMethod(
                "BuildPacket",
                BindingFlags.Static | BindingFlags.NonPublic);
            var packet = build?.Invoke(null, null) as byte[];
            var passed = packet != null
                && packet.Length == 15
                && packet[0] == 0x00
                && BitConverter.ToUInt16(packet, 1) == 0x00AB
                && BitConverter.ToInt32(packet, 3) == 15;
            Console.WriteLine(passed
                ? "PASS: dungeon mouse registration discard uses an empty A21 notification"
                : "FAIL: dungeon mouse registration discard packet is missing or malformed");
            return passed ? 0 : 1;
        }
    }
}
