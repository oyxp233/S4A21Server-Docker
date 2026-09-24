using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class A21OneToOneChatSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string label, bool success)
            {
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (success) passed++; else failed++;
            }
            try { RunAsync(Check).GetAwaiter().GetResult(); }
            catch (Exception ex) { Check(ex.ToString(), false); }
            Console.WriteLine($"A21_ONE_TO_ONE_CHAT: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static async Task RunAsync(Action<string, bool> check)
        {
            byte[] captured = Convert.FromHexString("2BF60301000000050000003233323373040000007465737400");
            check("A21 conversation request consumes mode 43, group id and name tail",
                ChatHandler.TryParseRequest(captured, out var request)
                && request.ConversationId == 1 && request.MessageBytes.SequenceEqual(ClientTextEncoding.GetBytes("2323s")));
            check("conversation keeps the existing 256-byte message limit",
                ChatHandler.TryParseRequest(Message(1, new string('中', 128), "甲"), out _)
                && !ChatHandler.TryParseRequest(Message(1, new string('中', 129), "甲"), out _));
            var wrongMode = captured.ToArray(); wrongMode[0] = 45;
            var badGbk = captured.ToArray(); badGbk[15] = 0x81;
            var overflow = captured.ToArray(); BitConverter.GetBytes(int.MaxValue).CopyTo(overflow, 7);
            var nullText = captured.ToArray(); nullText[11] = 0;
            foreach (var invalid in new[] { Array.Empty<byte>(), captured[..^1], captured.Concat(new byte[] { 0 }).ToArray(),
                wrongMode, badGbk, overflow, nullText, Message(0, "a", "甲"), Message(uint.MaxValue, "a", "甲"), Message(1, "", "甲") })
                check("malformed conversation request is rejected", !ChatHandler.TryParseRequest(invalid, out _));
            var sessions = new SessionDirectory();
            using var a = await Peer.Create(sessions, 101, "甲");
            using var b = await Peer.Create(sessions, 102, "乙", 10011);
            using var outsider = await Peer.Create(sessions, 103, "旁观者");
            var transitions = new CharacterTransitionCoordinator(sessions);
            using var chat = new ChatHandler(sessions, new PartyManager(), transitions);
            var registry = new GameCommandRegistry(); registry.RegisterGroup("chat", chat.RegisterHandlers);
            check("composition registers A21 create, leave, send, hyperlink and legacy state independently", registry.Count == 5
                && registry.TryGetValue((ushort)CmdPacketTypeA21.ITEM_HYPERLINK_MESSAGE, out var registeredHyperlink)
                && registeredHyperlink.Method.Name == nameof(ChatHandler.Handle_ITEM_HYPERLINK_MESSAGE)
                && registry.TryGetValue((ushort)CmdPacketTypeA21.LEAVE_FROM_GROUP, out var registeredLeave)
                && registeredLeave.Method.Name == nameof(ChatHandler.Handle_LEAVE_FROM_GROUP));
            Task Dispatch(Peer peer, CmdPacketTypeA21 command, byte[] body)
            {
                if (!registry.TryGetValue((ushort)command, out var handler)) throw new InvalidOperationException("unregistered chat command");
                return handler(peer.Session, new GamePacketHeader { type = (ushort)command }, body);
            }
            Task Open(Peer peer, string name) => Dispatch(peer, CmdPacketTypeA21.CREATE_GROUP, Name(name));
            Task Send(Peer peer, uint group, string text) => Dispatch(peer, CmdPacketTypeA21.SEND_MESSAGE, Message(group, text, "甲"));
            Task Leave(Peer peer, uint group) => Dispatch(peer, CmdPacketTypeA21.LEAVE_FROM_GROUP, BitConverter.GetBytes(group));
            foreach (var invalid in new[] { Array.Empty<byte>(), Name(""), Name("乙").Concat(new byte[] { 0 }).ToArray(),
                Name("不存在"), Name("甲"), new byte[] { 1, 0, 0, 0, 0x81 }, Name("\0") })
                await Dispatch(a, CmdPacketTypeA21.CREATE_GROUP, invalid);
            check("invalid, offline and self targets do not create windows", a.Drain().Count == 0 && b.Drain().Count == 0);
            await Open(a, "乙");
            var createdA = a.Drain(); var createdB = b.Drain();
            check("create publishes both members across channels without notifying outsiders",
                createdA.Count == 1 && createdB.Count == 1 && outsider.Drain().Count == 0);
            uint id = BitConverter.ToUInt32(createdA.Single(), 16);
            check("create uses A21 result, group, count and recipient-first members",
                createdA.Single().SequenceEqual(CreatePacket(id, "甲", "乙"))
                && createdB.Single().SequenceEqual(CreatePacket(id, "乙", "甲")));
            await chat.Handle_SEND_MESSAGE(a.Session, default, Message(id, "你好", "旁观者", 103));
            var received = b.Drain();
            check("mode 43 routes by membership and server sender name, not supplied name or UID",
                received.Count == 1 && received[0].SequenceEqual(ChatPacket(id, "甲", "你好"))
                && outsider.Drain().Count == 0 && a.Drain().Count == 0);
            await chat.Handle_SEND_MESSAGE(b.Session, default, Message(id, "回复", "乙"));
            check("reverse message arrives once without server self echo", a.Drain().Count == 1 && b.Drain().Count == 0);
            await chat.Handle_SEND_MESSAGE(outsider.Session, default, Message(id, "伪造", "甲"));
            check("nonmember cannot inject a conversation message", a.Drain().Count == 0 && b.Drain().Count == 0 && outsider.Drain().Count == 0);
            await Task.WhenAll(Open(a, "乙"), Open(b, "甲"));
            check("concurrent duplicate opens do not create duplicate native windows", a.Drain().Count == 0 && b.Drain().Count == 0);
            await Leave(outsider, id);
            await Dispatch(a, CmdPacketTypeA21.LEAVE_FROM_GROUP, BitConverter.GetBytes(id).Concat(new byte[] { 0 }).ToArray());
            await Dispatch(a, CmdPacketTypeA21.ONE_TO_ONE_CHAT_STATE, BitConverter.GetBytes(id));
            await Send(a, id, "仍在线");
            check("outsider, malformed leave and legacy state cannot close a group", b.Drain().Count == 1 && a.Drain().Count == 0);
            await Leave(a, id);
            var left = b.Drain();
            check("leaving removes routing and notifies only the peer with id and actor name", a.Drain().Count == 0
                && left.Count == 1 && left[0].SequenceEqual(LeavePacket(id, "甲")));
            await Send(b, id, "过期"); await Leave(a, id);
            check("closed group messages and repeated leave are inert", a.Drain().Count == 0 && b.Drain().Count == 0);
            await Open(a, "乙");
            var reopened = a.Drain(); var reopenedPeer = b.Drain();
            uint next = BitConverter.ToUInt32(reopened.Single(), 16);
            check("close then reopen creates a fresh usable conversation", reopened.Count == 1 && reopenedPeer.Count == 1 && next != id);
            await Leave(a, id); await Send(a, id, "旧窗口"); await Send(a, next, "重开后的消息");
            var afterReopen = b.Drain();
            check("old window traffic cannot remove or impersonate reopened group", afterReopen.Count == 1
                && afterReopen[0].SequenceEqual(ChatPacket(next, "甲", "重开后的消息")));
            await Leave(b, next); a.Drain();
            await Open(b, "甲");
            var reverseOpen = b.Drain(); a.Drain();
            uint reverseId = BitConverter.ToUInt32(reverseOpen.Single(), 16);
            await Send(b, reverseId, "反向重开");
            check("either participant can leave and reopen", a.Drain().Count == 1 && b.Drain().Count == 0 && reverseId != next);
            await sessions.UnregisterAsync(102, b.Session);
            check("disconnect removes group and publishes peer departure", a.Drain().Single().SequenceEqual(LeavePacket(reverseId, "乙")));
            using var reconnect = await Peer.Create(sessions, 102, "乙", 10011);
            await Send(reconnect, reverseId, "不能继承旧编号");
            check("reconnected session cannot inherit old conversation", a.Drain().Count == 0 && reconnect.Drain().Count == 0);
            await Open(reconnect, "甲"); var reconnectPackets = reconnect.Drain(); a.Drain();
            uint reconnectId = BitConverter.ToUInt32(reconnectPackets.Single(), 16);
            await Send(reconnect, reconnectId, "重新建立");
            check("reconnected participants can establish and use a new conversation", a.Drain().Count == 1 && reconnectId != reverseId);
            await RunIdentityTests(check);
            await RunPublicationTests(check);
        }

        private static async Task RunIdentityTests(Action<string, bool> check)
        {
            var sessions = new SessionDirectory();
            var transitions = new CharacterTransitionCoordinator(sessions);
            using var a = await Peer.Create(sessions, 201, "甲");
            using var b = await Peer.Create(sessions, 202, "乙");
            using var chat = new ChatHandler(sessions, new PartyManager(), transitions);
            Task Open() => chat.Handle_CREATE_GROUP(a.Session, default, Name("乙"));
            await Open(); uint id = BitConverter.ToUInt32(a.Drain().Single(), 16); b.Drain();
            var sendLock = (SemaphoreSlim)typeof(EnhancedClientSession).GetField("_sendLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(b.Session);
            await sendLock.WaitAsync();
            var queued = chat.Handle_SEND_MESSAGE(a.Session, default, Message(id, "旧UID", "甲"));
            b.Session.Player.UserId++;
            sendLock.Release(); await queued;
            check("queued message rechecks receiver UID under send lock", b.Drain().Count == 0);
            await Open(); uint updatedId = BitConverter.ToUInt32(a.Drain().Single(), 16); b.Drain();
            check("changed identity requires a fresh group id", updatedId != id);
            await sendLock.WaitAsync();
            queued = chat.Handle_SEND_MESSAGE(a.Session, default, Message(updatedId, "旧发送者", "甲"));
            a.Session.Player.UserId++;
            sendLock.Release(); await queued;
            check("queued message rechecks sender UID under receiver send lock", b.Drain().Count == 0);

            using (var gate = await transitions.AcquireAsync(201))
            {
                queued = Open();
                a.Session.Player.CharacterId = 204; sessions.Register(204, a.Session);
            }
            await queued;
            check("queued create cannot follow the same socket into another character", a.Drain().Count == 0 && b.Drain().Count == 0);
            a.Session.Player.CharacterId = 201;
            await Open(); id = BitConverter.ToUInt32(a.Drain().Single(), 16); b.Drain();
            using (var gate = await transitions.AcquireAsync(201))
            {
                queued = chat.Handle_LEAVE_FROM_GROUP(a.Session, default, BitConverter.GetBytes(id));
                a.Session.Player.UserId++;
            }
            await queued;
            check("queued leave cannot use a changed actor identity", b.Drain().Count == 0);
            await Open(); id = BitConverter.ToUInt32(a.Drain().Single(), 16); b.Drain();
            await sendLock.WaitAsync();
            queued = chat.Handle_SEND_MESSAGE(a.Session, default, Message(id, "离线竞争", "甲"));
            var ending = sessions.UnregisterAsync(202, b.Session);
            sendLock.Release(); await queued; await ending;
            check("queued message cannot reach a disconnected directory generation", b.Drain().Count == 0
                && a.Drain().Single().SequenceEqual(LeavePacket(id, "乙")));
            await RunReplacementTests(check);
        }

        private static async Task RunReplacementTests(Action<string, bool> check)
        {
            var sessions = new SessionDirectory();
            var transitions = new CharacterTransitionCoordinator(sessions);
            var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sessions.SessionEnding += async (_, _) => { arrived.TrySetResult(true); await release.Task; };
            using var old = await Peer.Create(sessions, 301, "甲");
            using var peer = await Peer.Create(sessions, 302, "乙");
            using var chat = new ChatHandler(sessions, new PartyManager(), transitions);
            await chat.Handle_CREATE_GROUP(old.Session, default, Name("乙")); old.Drain(); peer.Drain();
            using var replacement = await Peer.Create(sessions, 301, "甲", register: false);
            var replacing = sessions.RegisterReplacingAsync(301, replacement.Session);
            await arrived.Task;
            await chat.Handle_CREATE_GROUP(replacement.Session, default, Name("乙"));
            uint id = BitConverter.ToUInt32(replacement.Drain().Single(), 16); peer.Drain();
            release.SetResult(true); await replacing;
            await chat.Handle_SEND_MESSAGE(replacement.Session, default, Message(id, "新会话", "甲"));
            check("late old-session cleanup cannot remove replacement conversation", peer.Drain().Single().SequenceEqual(ChatPacket(id, "甲", "新会话")));
            await chat.Handle_CREATE_GROUP(old.Session, default, Name("乙"));
            await chat.Handle_SEND_MESSAGE(old.Session, default, Message(id, "旧会话", "甲"));
            await chat.Handle_LEAVE_FROM_GROUP(old.Session, default, BitConverter.GetBytes(id));
            check("displaced session cannot open, send or leave replacement group", old.Drain().Count == 0 && peer.Drain().Count == 0);
        }

        private static async Task RunPublicationTests(Action<string, bool> check)
        {
            var sessions = new SessionDirectory();
            using var a = await Peer.Create(sessions, 401, "甲");
            using var b = await Peer.Create(sessions, 402, "乙");
            using var outsider = await Peer.Create(sessions, 403, "丙", 10011);
            using var chat = new ChatHandler(sessions, new PartyManager(), new CharacterTransitionCoordinator(sessions));
            var sendLock = (SemaphoreSlim)typeof(EnhancedClientSession).GetField("_sendLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(b.Session);
            await sendLock.WaitAsync();
            try { await chat.Handle_CREATE_GROUP(a.Session, default, Name("乙")); }
            finally { sendLock.Release(); }
            var first = a.Drain(); uint id = BitConverter.ToUInt32(first.Single(), 16);
            check("create timeout retains only successful publication", b.Drain().Count == 0);
            await chat.Handle_SEND_MESSAGE(a.Session, default, Message(id, "未建窗", "甲"));
            check("messages cannot precede the peer's successful create notification", b.Drain().Count == 0);
            await chat.Handle_CREATE_GROUP(a.Session, default, Name("乙"));
            check("retry repairs missing publication without duplicating the existing window", a.Drain().Count == 0
                && b.Drain().Single().SequenceEqual(CreatePacket(id, "乙", "甲")));
            await chat.Handle_SEND_MESSAGE(a.Session, default, Message(id, "重试成功", "甲"));
            check("repaired group can receive messages", b.Drain().Single().SequenceEqual(ChatPacket(id, "甲", "重试成功")));

            foreach (var mode in new byte[] { 1, 2, 3, 7 })
            {
                var w = new GamePacketWriter(); w.WriteByte(mode); w.WriteUInt16(402); w.WriteUInt32(402); w.WriteClientDstr("普通聊天");
                await chat.Handle_SEND_MESSAGE(a.Session, default, w.ToArray());
                var mine = a.Drain(); var theirs = b.Drain();
                check($"existing chat mode {mode} retains MESSAGE routing and excludes other channels", mine.Count == 1
                    && mine[0][0] == 0 && BitConverter.ToUInt16(mine[0], 1) == (ushort)NotiPacketTypeA21.MESSAGE
                    && theirs.Count == (mode == 2 ? 0 : 1) && outsider.Drain().Count == 0);
            }
            b.Session.Stream.Dispose();
            await chat.Handle_LEAVE_FROM_GROUP(a.Session, default, BitConverter.GetBytes(id));
            await chat.Handle_SEND_MESSAGE(a.Session, default, Message(id, "已关闭", "甲"));
            check("peer send failure does not prevent local group cleanup", a.Drain().Count == 0);
        }

        private static byte[] Name(string value)
        { var w = new GamePacketWriter(); w.WriteClientDstr(value); return w.ToArray(); }

        private static byte[] Message(uint id, string value, string name, ushort uid = 0)
        {
            var w = new GamePacketWriter(); w.WriteByte(43); w.WriteUInt16(uid); w.WriteUInt32(id);
            w.WriteClientDstr(value); w.WriteClientDstr(name); w.WriteByte(0); return w.ToArray();
        }

        private static byte[] CreatePacket(uint id, string recipient, string peer)
        {
            var w = new GamePacketWriter(); w.WriteByte(0); w.WriteUInt32(id); w.WriteByte(2);
            w.WriteClientDstr(recipient); w.WriteClientDstr(peer);
            return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.CREATE_GROUP, w.ToArray());
        }

        private static byte[] ChatPacket(uint id, string sender, string value)
        {
            var w = new GamePacketWriter(); w.WriteUInt32(id); w.WriteClientDstr(sender); w.WriteClientDstr(value);
            return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.MESSAGE_GROUP_CHAT, w.ToArray());
        }

        private static byte[] LeavePacket(uint id, string actor)
        {
            var w = new GamePacketWriter(); w.WriteUInt32(id); w.WriteClientDstr(actor);
            return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.LEAVE_USER_FROM_GROUP, w.ToArray());
        }

        internal sealed class Peer : IDisposable
        {
            private readonly TcpClient _reader;
            internal EnhancedClientSession Session { get; }
            private Peer(TcpClient reader, EnhancedClientSession session) { _reader = reader; Session = session; }
            internal static async Task<Peer> Create(SessionDirectory sessions, int id, string name, int port = 10010, bool register = true)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                var reader = new TcpClient { ReceiveTimeout = 3000 }; TcpClient writer;
                try
                {
                    var connecting = reader.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    writer = await listener.AcceptTcpClientAsync(); await connecting;
                }
                finally { listener.Stop(); }
                var session = new EnhancedClientSession(writer, new GamePacketHeader(), port);
                session.Player.CharacterId = id; session.Player.UserId = checked((ushort)id);
                session.Player.CurTownId = 1; session.Player.CurAreaId = 1;
                session.Player.TownPresenceReady = true;
                session.Player.UserState = 0;
                session.Player.Name = ClientTextEncoding.GetBytes(name);
                if (register) sessions.Register(id, session);
                return new Peer(reader, session);
            }
            internal List<byte[]> Drain()
            {
                var packets = new List<byte[]>(); var stream = _reader.GetStream();
                while (_reader.Client.Poll(20000, SelectMode.SelectRead))
                {
                    if (_reader.Available == 0) throw new InvalidOperationException("chat test connection closed");
                    var header = new byte[15]; stream.ReadExactly(header); int length = BitConverter.ToInt32(header, 3);
                    if (length < 15 || length > 65536) throw new InvalidOperationException("invalid chat packet length");
                    var packet = new byte[length]; header.CopyTo(packet, 0); stream.ReadExactly(packet.AsSpan(15)); packets.Add(packet);
                }
                return packets;
            }
            public void Dispose() { Session.Close(); _reader.Dispose(); }
        }
    }
}
