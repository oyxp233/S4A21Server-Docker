using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Friends;
using Peer = DfoServer.SelfTests.A21OneToOneChatSelfTest.Peer;

namespace DfoServer.SelfTests
{
    public static class A21UserChannelSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string label, bool success)
            { Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}"); if (success) passed++; else failed++; }
            var catalogField = typeof(GameNetworkConfig).GetField("_channelCatalog", BindingFlags.Static | BindingFlags.NonPublic);
            var proxyProperty = typeof(GameNetworkConfig).GetProperty(nameof(GameNetworkConfig.ProxyMode));
            var duelProperty = typeof(GameNetworkConfig).GetProperty(nameof(GameNetworkConfig.FreeDuelListenerEnabled));
            var catalog = catalogField.GetValue(null);
            bool proxy = GameNetworkConfig.ProxyMode, duel = GameNetworkConfig.FreeDuelListenerEnabled;
            try
            {
                proxyProperty.SetValue(null, false);
                duelProperty.SetValue(null, false);
                GameNetworkConfig.ConfigureChannelCatalog(null);
                RunAsync(Check).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Check(ex.ToString(), false); }
            finally
            {
                catalogField.SetValue(null, catalog);
                proxyProperty.SetValue(null, proxy);
                duelProperty.SetValue(null, duel);
            }
            Console.WriteLine($"A21_USER_CHANNEL: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static async Task RunAsync(Action<string, bool> check)
        {
            var sessions = new SessionDirectory();
            var transitions = new CharacterTransitionCoordinator(sessions);
            var registry = new GameCommandRegistry();
            registry.RegisterGroup("user-channel", new UserChannelHandler(sessions, transitions).RegisterHandlers);
            check("native query is registered as a CMD", registry.Count == 1
                && registry.TryGetValue((ushort)CmdPacketTypeA21.REQUEST_USER_CHANNEL, out _));
            registry.TryGetValue((ushort)CmdPacketTypeA21.REQUEST_USER_CHANNEL, out var handler);
            using var owner = await Peer.Create(sessions, 101, "甲", GameNetworkConfig.NormalGamePort);
            using var target = await Peer.Create(sessions, 102, "尼尔巴斯", GameNetworkConfig.Channel100GamePort);
            Task Dispatch(byte[] body) => handler(owner.Session,
                new GamePacketHeader { type = (ushort)CmdPacketTypeA21.REQUEST_USER_CHANNEL }, body);
            Task Query(string name) => Dispatch(Request(name));
            bool Failure(byte error) => owner.Drain().Single().SequenceEqual(Packet(new byte[] { 0, error }));

            byte[] captured = Convert.FromHexString("0108000000C4E1B6FBB0CDCBB9");
            check("captured A21 query decodes server and GBK DSTR", UnitedFriendRequest.TryUserChannel(captured, out var name) && name == "尼尔巴斯");
            await Dispatch(captured);
            var reply = owner.Drain().Single();
            check("unrelated online target returns native success/server/DSTR/channel-server/channel body",
                reply.SequenceEqual(Packet(Convert.FromHexString("010108000000C4E1B6FBB0CDCBB90164"))));
            check("native catalog group filter and channel lookup both find the destination", CanFindDestination(reply));
            var missingGroup = reply.ToArray(); missingGroup[^2] = 0;
            check("zero channel server produces native no-destination failure", !CanFindDestination(missingGroup));
            check("lookup sends nothing to the target and does not move either player", target.Drain().Count == 0
                && owner.Session.ListenerPort == GameNetworkConfig.NormalGamePort
                && target.Session.ListenerPort == GameNetworkConfig.Channel100GamePort);
            await Query("甲");
            check("same-channel query returns actual channel rather than friend-list zero sentinel",
                owner.Drain().Single().AsSpan(15).SequenceEqual(SuccessBody("甲", GameNetworkConfig.NormalChannelIndex)));
            await Query("离线"); check("offline target terminates pending state with native missing-target error", Failure(100));
            await Query("nierbasi"); check("name lookup is exact", Failure(100));

            foreach (var invalid in new[] { Array.Empty<byte>(), captured[..^1], captured.Concat(new byte[] { 0 }).ToArray(),
                new byte[] { 2 }.Concat(captured.Skip(1)).ToArray(), new byte[] { 1, 255, 255, 255, 127 },
                new byte[] { 1, 1, 0, 0, 0, 0x81 }, Request(""), Request(" "), Request("\0"), Request(new string('中', 50)) })
            {
                await Dispatch(invalid);
                check("malformed/server/encoding/name bounds return generic failure", Failure(0));
            }
            check("native DSTR reader allows 49 GBK bytes and rejects 50",
                UnitedFriendRequest.TryUserChannel(Request(new string('a', 49)), out _)
                && UnitedFriendRequest.TryUserChannel(Request(new string('中', 24)), out _)
                && !UnitedFriendRequest.TryUserChannel(Request(new string('中', 25)), out _)
                && !UnitedFriendRequest.TryUserChannel(Request(new string('a', 50)), out _));

            target.Session.Player.TownPresenceReady = false;
            target.Session.Player.UserState = 5;
            await Query("尼尔巴斯");
            check("online room participants remain discoverable without town presence", owner.Drain().Single().AsSpan(15)
                .SequenceEqual(SuccessBody("尼尔巴斯", GameNetworkConfig.Channel100Index)));
            using var unknown = await Peer.Create(sessions, 103, "未知频道", 19999);
            await Query("未知频道"); check("unknown listener cannot silently fall back to normal channel", Failure(0));
            using var disabled = await Peer.Create(sessions, 104, "未开启频道", GameNetworkConfig.FreeDuelGamePort);
            await Query("未开启频道"); check("disabled channel is not advertised as followable", Failure(0));

            GameNetworkConfig.ConfigureChannelCatalog(new[] { new GameChannelEndpoint(42, 14042, 15042) });
            using var custom = await Peer.Create(sessions, 105, "目录频道", 15042);
            await Query("目录频道");
            check("custom channel uses catalog mapping rather than port arithmetic", owner.Drain().Single().AsSpan(15)
                .SequenceEqual(SuccessBody("目录频道", 42)));
            GameNetworkConfig.ConfigureChannelCatalog(null);
            typeof(GameNetworkConfig).GetProperty(nameof(GameNetworkConfig.ProxyMode)).SetValue(null, true);
            using var proxied = await Peer.Create(sessions, 106, "代理频道", GameNetworkConfig.Channel100ProxyGamePort);
            await Query("代理频道");
            check("proxy listener maps to public channel identity", owner.Drain().Single().AsSpan(15)
                .SequenceEqual(SuccessBody("代理频道", GameNetworkConfig.Channel100Index)));
            typeof(GameNetworkConfig).GetProperty(nameof(GameNetworkConfig.ProxyMode)).SetValue(null, false);

            var sendLock = (SemaphoreSlim)typeof(EnhancedClientSession).GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(owner.Session);
            await sendLock.WaitAsync();
            var pending = Query("尼尔巴斯");
            await sessions.UnregisterAsync(102, target.Session);
            sendLock.Release(); await pending;
            check("target disconnect while ACK is queued returns failure instead of stale success", Failure(100));
            using var replacement = await Peer.Create(sessions, 102, "尼尔巴斯", GameNetworkConfig.NormalGamePort);
            await Query("尼尔巴斯");
            check("reconnection resolves the new session and channel", owner.Drain().Single().AsSpan(15)
                .SequenceEqual(SuccessBody("尼尔巴斯", GameNetworkConfig.NormalChannelIndex)));
            await sendLock.WaitAsync(); pending = Query("尼尔巴斯");
            sessions.Register(102, target.Session);
            sendLock.Release(); await pending;
            check("queued ACK cannot publish displaced session channel", Failure(100));
            sessions.Register(102, replacement.Session);
            await sendLock.WaitAsync(); pending = Query("尼尔巴斯");
            replacement.Session.Player.Name = ClientTextEncoding.GetBytes("改名");
            sendLock.Release(); await pending;
            check("queued ACK rejects renamed target", Failure(100));
            await Query("改名"); owner.Drain();
            await sendLock.WaitAsync(); pending = Query("改名");
            owner.Session.Player.UserId++;
            sendLock.Release(); await pending;
            check("queued response cannot enter another requester identity", owner.Drain().Count == 0);
            using (await transitions.AcquireAsync(101))
            {
                pending = Query("改名");
                owner.Session.Player.CharacterId = 107;
                sessions.Register(107, owner.Session);
            }
            await pending;
            check("request waiting on transition gate cannot follow character switch", owner.Drain().Count == 0);
            owner.Session.Player.CharacterId = 101;
            using var newOwner = await Peer.Create(sessions, 101, "甲", GameNetworkConfig.NormalGamePort);
            await Query("改名");
            check("replaced requester connection receives no response", owner.Drain().Count == 0 && newOwner.Drain().Count == 0);
        }

        private static byte[] Request(string name)
        { var w = new GamePacketWriter(); w.WriteByte(1); w.WriteClientDstr(name); return w.ToArray(); }
        private static byte[] SuccessBody(string name, byte channel)
        { var w = new GamePacketWriter(); w.WriteByte(1); w.WriteByte(1); w.WriteClientDstr(name); w.WriteByte(1); w.WriteByte(channel); return w.ToArray(); }
        // A21 025ACF5B -> 00CF4D60 filters by group before 02592FA0 compares channel IDs.
        private static bool CanFindDestination(byte[] packet)
        {
            int nameLength = BitConverter.ToInt32(packet, 17);
            int groupOffset = 21 + nameLength;
            return packet[15] == 1 && packet.Length == groupOffset + 2
                && packet[groupOffset] == GameNetworkConfig.ChannelServerIndex
                && GameNetworkConfig.GetGameChannels().Any(channel => channel.ChannelId == packet[groupOffset + 1]);
        }
        private static byte[] Packet(byte[] body)
            => GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.REQUEST_USER_CHANNEL, body);
    }
}
