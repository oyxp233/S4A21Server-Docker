using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DfoServer.Network;

namespace DfoServer.SelfTests
{
    // UDP 哑中继数据面自测: 用真实 loopback UDP 验证"按房间转发"——
    // 同房间成员能收到、不同房间收不到、退房后不再转发。协议无关(payload 任意)。
    public static class UdpRelaySelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== UDP_RELAY selftest ===");
            int pass = 0;
            int fail = 0;
            void Check(string n, bool ok)
            {
                if (ok) { pass++; Console.WriteLine($"  [PASS] {n}"); }
                else { fail++; Console.WriteLine($"  [FAIL] {n}"); }
            }

            const int relayPort = 33321;
            using var relay = new UdpRelayServer(relayPort);
            relay.Start();
            var relayEP = new IPEndPoint(IPAddress.Loopback, relayPort);

            UdpClient MakeClient()
            {
                var cl = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                cl.Client.ReceiveTimeout = 1200;
                return cl;
            }
            using var a = MakeClient();
            using var b = MakeClient();
            using var c = MakeClient();
            var aEP = (IPEndPoint)a.Client.LocalEndPoint;
            var bEP = (IPEndPoint)b.Client.LocalEndPoint;
            var cEP = (IPEndPoint)c.Client.LocalEndPoint;
            var sidA = Guid.NewGuid();
            var sidB = Guid.NewGuid();
            var sidC = Guid.NewGuid();

            // A,B 入房1; C 入房2
            relay.JoinRoom(1, sidA); relay.RegisterEndpoint(sidA, aEP);
            relay.JoinRoom(1, sidB); relay.RegisterEndpoint(sidB, bEP);
            relay.JoinRoom(2, sidC); relay.RegisterEndpoint(sidC, cEP);

            // 纯逻辑
            var tA = relay.ResolveTargets(aEP);
            Check("ResolveTargets(A) == [B]", tA.Count == 1 && tA[0].Equals(bEP));
            Check("ResolveTargets(C) 空(独自在房2)", relay.ResolveTargets(cEP).Count == 0);
            Check("RoomCount == 2", relay.RoomCount == 2);
            Check("来源未登记 -> 空", relay.ResolveTargets(new IPEndPoint(IPAddress.Loopback, 59999)).Count == 0);

            // 真实转发: A 发包 -> B 收到(同房1)
            var payload = Encoding.ASCII.GetBytes("HELLO-DUNGEON");
            a.Send(payload, payload.Length, relayEP);
            bool bGot = false;
            try
            {
                var ep = new IPEndPoint(IPAddress.Any, 0);
                var got = b.Receive(ref ep);
                bGot = Encoding.ASCII.GetString(got) == "HELLO-DUNGEON";
            }
            catch (SocketException) { bGot = false; }
            Check("A发包 -> B收到(同房间转发)", bGot);

            // C 不该收到(不同房间)
            bool cGot = true;
            try { var ep = new IPEndPoint(IPAddress.Any, 0); c.Receive(ref ep); }
            catch (SocketException) { cGot = false; }
            Check("C 未收到(跨房间不转发)", !cGot);

            // 退房: B 离开后 A 无转发目标
            relay.Leave(sidB);
            Check("B退房后 ResolveTargets(A) 空", relay.ResolveTargets(aEP).Count == 0);

            Console.WriteLine($"=== result: {pass} PASS, {fail} FAIL ===");
            return fail == 0 ? 0 : 1;
        }
    }
}
