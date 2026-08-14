using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using DfoServer.Network;

namespace DfoServer.SelfTests
{
    public static class PartyUdpRelayCoreSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            var portBase = FindFreeUdpRange(4);
            using var relay = new PartyUdpRelay(
                "127.0.0.1",
                portBase,
                4,
                "party-selftest");
            var bindings = new[]
            {
                new PartyUdpRelay.MemberBinding(
                    1001,
                    Guid.NewGuid(),
                    IPAddress.Loopback),
                new PartyUdpRelay.MemberBinding(
                    1002,
                    Guid.NewGuid(),
                    IPAddress.Loopback),
            };

            var created = relay.TrySyncRoom(
                77,
                bindings,
                out var first);
            var firstForward = relay.GetPort(77, 1001, 1002);
            var firstReverse = relay.GetPort(77, 1002, 1001);
            Check(
                "secure relay allocates a complete ordered-pair matrix",
                created &&
                first != null &&
                first.SecureBindings &&
                first.TryGetPort(1001, 1002, out var snapshotForward) &&
                first.TryGetPort(1002, 1001, out var snapshotReverse) &&
                snapshotForward == firstForward &&
                snapshotReverse == firstReverse &&
                firstForward != firstReverse &&
                firstForward >= portBase &&
                firstReverse < portBase + 4,
                ref failures);

            var refreshed = relay.TrySyncRoom(
                77,
                bindings,
                out var second);
            Check(
                "unchanged membership preserves tested relay ports",
                refreshed &&
                second.Generation >= first.Generation &&
                relay.GetPort(77, 1001, 1002) == firstForward &&
                relay.GetPort(77, 1002, 1001) == firstReverse,
                ref failures);
            Check(
                "secure rooms reject legacy endpoint resets",
                !relay.ResetMemberEndpoints(77, 1001),
                ref failures);

            relay.CloseRoom(77);
            Check(
                "closing a room releases its published matrix",
                relay.GetPort(77, 1001, 1002) == 0 &&
                relay.GetPort(77, 1002, 1001) == 0,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "PartyUdpRelayCoreSelfTest OK"
                    : $"PartyUdpRelayCoreSelfTest FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }

        private static int FindFreeUdpRange(int count)
        {
            for (var portBase = 36000; portBase <= 60000 - count; portBase += count)
            {
                var sockets = new List<UdpClient>();
                try
                {
                    for (var offset = 0; offset < count; offset++)
                    {
                        var socket = new UdpClient(AddressFamily.InterNetwork);
                        socket.Client.ExclusiveAddressUse = true;
                        socket.Client.Bind(
                            new IPEndPoint(IPAddress.Loopback, portBase + offset));
                        sockets.Add(socket);
                    }
                    return portBase;
                }
                catch (SocketException)
                {
                }
                finally
                {
                    foreach (var socket in sockets)
                        socket.Dispose();
                }
            }

            throw new InvalidOperationException("No free UDP range for relay self-test.");
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {label}");
            if (!condition)
                failures++;
        }
    }
}
