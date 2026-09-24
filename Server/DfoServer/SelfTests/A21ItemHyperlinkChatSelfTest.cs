using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Party;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // 装备超链接聊天（CMD 0x01A9）接收与转发自测。输入夹具取自线上
    // server.log 实机抓包：169B body，mode + targetUid + targetCid +
    // dstr(文本) + 装备 tooltip 数据块（141B，原文转发）。
    public static class A21ItemHyperlinkChatSelfTest
    {
        private const int CapturedBodyBytes = 169;
        // mode=3（当前频道）样本前缀，尾部全 0 补齐到 169B。
        private const string CapturedAreaHex =
            "03050000000000110000007F7F5BD3C0BAE3C1D2D1E6EEF8BCD75D7F01FF00FFFF16F3F605FEC99A3B000026000000";
        // mode=0x34（攻坚队，另一角色/装备）样本前缀，尾部全 0 补齐到 169B。
        private const string CapturedRaidHex =
            "34030000000000110000007F7F5BD3CEC1FAD6AEBBEAD5BDC5DB5D7F01FFB400FF70CBF6056EFF836C000021000000000000000080000000";
        // mode=1（私聊）2026-09-24 packet capture 真实抓包全量 188B：
        // 头部 + 25B 文本 + targetName DSTR("test65") + 1B 私聊标志 +
        // 141B 数据块（01 FF 00 FF FF 开头）。
        private const string CapturedDirectHex =
            "01060000000000190000007F7F5B5BBBC6BDF0C3CE5DC5E5C2B3CBB9B5C4C8D9D3FE5D7F060000007465737436350101FF00FFFF8C75FA05FEC99A3B0000000000000000C9F499" +
            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

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
            Console.WriteLine($"A21_ITEM_HYPERLINK_CHAT: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static async Task RunAsync(Action<string, bool> check)
        {
            var area = Padded(CapturedAreaHex);
            var partyBody = area.ToArray(); partyBody[0] = 2;
            var guildBody = area.ToArray(); guildBody[0] = 6;
            var raid = Padded(CapturedRaidHex);
            var text = area.Skip(11).Take(17).ToArray();
            var blob = area.Skip(28).ToArray();
            var raidText = raid.Skip(11).Take(17).ToArray();
            var raidBlob = raid.Skip(28).ToArray();

            check("captured mode 3 area hyperlink parses with verbatim 141-byte blob",
                ChatHandler.TryParseHyperlinkRequest(area, out var areaRequest, out var areaBlob)
                && areaRequest.Mode == 3 && areaRequest.TargetUniqueId == 5
                && areaRequest.TargetCharacterId == 0
                && areaRequest.MessageBytes.SequenceEqual(text)
                && areaBlob.Length == 141 && areaBlob.SequenceEqual(blob));
            check("captured mode 2 party and mode 6 guild shapes parse identically",
                ChatHandler.TryParseHyperlinkRequest(partyBody, out var partyRequest, out _)
                && partyRequest.Mode == 2
                && ChatHandler.TryParseHyperlinkRequest(guildBody, out var guildRequest, out _)
                && guildRequest.Mode == 6);
            check("captured mode 0x34 raid hyperlink parses with its own blob",
                ChatHandler.TryParseHyperlinkRequest(raid, out var raidRequest, out var raidBlobOut)
                && raidRequest.Mode == 0x34 && raidRequest.TargetUniqueId == 3
                && raidRequest.MessageBytes.SequenceEqual(raidText)
                && raidBlobOut.SequenceEqual(raidBlob));

            var direct = Convert.FromHexString(CapturedDirectHex);
            var directText = direct.Skip(11).Take(25).ToArray();
            var directBlob = direct.Skip(47).ToArray();
            check("captured mode 1 direct hyperlink parses target name and strips conversation flag",
                ChatHandler.TryParseHyperlinkRequest(direct, out var directRequest, out var directLink)
                && directRequest.Mode == 1 && directRequest.TargetUniqueId == 6
                && directRequest.TargetCharacterId == 0
                && directRequest.MessageBytes.SequenceEqual(directText)
                && directRequest.TargetNameBytes.SequenceEqual(ClientTextEncoding.GetBytes("test65"))
                && directLink.Length == 141 && directLink.SequenceEqual(directBlob)
                && directLink[0] == 0x01 && directLink[1] == 0xFF);
            var directMode7 = direct.ToArray(); directMode7[0] = 7;
            check("mode 7 direct hyperlink parses identically to mode 1",
                ChatHandler.TryParseHyperlinkRequest(directMode7, out var mode7Request, out var mode7Link)
                && mode7Request.Mode == 7
                && mode7Request.TargetNameBytes.SequenceEqual(ClientTextEncoding.GetBytes("test65"))
                && mode7Link.SequenceEqual(directBlob));

            var nulText = area.ToArray(); nulText[11] = 0;
            var zeroLength = area.ToArray(); Array.Clear(zeroLength, 7, 4);
            var oversizeText = area.ToArray(); BitConverter.GetBytes(257).CopyTo(oversizeText, 7);
            var overlongText = area.ToArray(); BitConverter.GetBytes(200).CopyTo(overlongText, 7);
            var truncatedText = area[..20];
            var oversizeBody = area.Concat(new byte[1025 - area.Length]).ToArray();
            foreach (var invalid in new[] { Array.Empty<byte>(), truncatedText, nulText, zeroLength, oversizeText, overlongText, oversizeBody })
                check("malformed hyperlink request is rejected", !ChatHandler.TryParseHyperlinkRequest(invalid, out _, out _));

            await RunAreaTests(check, text, blob);
            await RunDirectTests(check, text, blob);
            await RunPartyTests(check, text, blob);
            await RunRaidTests(check, text, blob);
            await RunGuildTests(check, text, blob);
        }

        private static async Task RunAreaTests(Action<string, bool> check, byte[] text, byte[] blob)
        {
            var sessions = new SessionDirectory();
            var transitions = new CharacterTransitionCoordinator(sessions);
            using var a = await Peer.Create(sessions, 101, "甲");
            using var b = await Peer.Create(sessions, 102, "乙");
            using var outsider = await Peer.Create(sessions, 103, "丙", 10011);
            using var chat = new ChatHandler(sessions, new PartyManager(), transitions);
            var registry = new GameCommandRegistry(); registry.RegisterGroup("chat", chat.RegisterHandlers);
            check("composition registers hyperlink command alongside existing chat commands",
                registry.Count == 5
                && registry.TryGetValue((ushort)CmdPacketTypeA21.ITEM_HYPERLINK_MESSAGE, out var registered)
                && registered.Method.Name == nameof(ChatHandler.Handle_ITEM_HYPERLINK_MESSAGE));

            await Dispatch(registry, a, HyperlinkBody(3, 5, 0, text, blob));
            var mine = a.Drain(); var theirs = b.Drain();
            check("mode 3 hyperlink forwards to same-area recipients with sender uid and verbatim blob",
                mine.Count == 1 && theirs.Count == 1 && outsider.Drain().Count == 0
                && IsHyperlinkPacket(theirs[0], 3, 101, text, blob)
                && IsHyperlinkPacket(mine[0], 3, 101, text, blob));

            await Dispatch(registry, a, HyperlinkBody(3, 5, 0, text, Array.Empty<byte>()));
            var emptyTail = b.Drain(); a.Drain();
            check("hyperlink without blob is still routed with empty tail",
                emptyTail.Count == 1 && IsHyperlinkPacket(emptyTail[0], 3, 101, text, Array.Empty<byte>()));
        }

        private static async Task RunDirectTests(Action<string, bool> check, byte[] text, byte[] blob)
        {
            var sessions = new SessionDirectory();
            var transitions = new CharacterTransitionCoordinator(sessions);
            using var a = await Peer.Create(sessions, 101, "甲");
            using var b = await Peer.Create(sessions, 102, "乙");
            using var outsider = await Peer.Create(sessions, 103, "丙", 10011);
            using var chat = new ChatHandler(sessions, new PartyManager(), transitions);
            var registry = new GameCommandRegistry(); registry.RegisterGroup("chat", chat.RegisterHandlers);

            // 真实抓包形态（name DSTR + 标志 + 数据块），按 targetUid 寻址。
            await Dispatch(registry, a, DirectBody(1, 102, 0, ClientTextEncoding.GetBytes("test65"), text, blob, withFlag: true));
            var mine = a.Drain(); var theirs = b.Drain();
            var expected = GamePacketEnvelopeBuilder.Build(0,
                (ushort)NotiPacketTypeA21.MESSAGE_HYPER_LINK,
                ChatHandler.BuildHyperlinkNotificationBody(1, 101, 0, text, blob));
            check("mode 1 hyperlink strips target name and conversation flag before forwarding",
                mine.Count == 1 && theirs.Count == 1 && outsider.Drain().Count == 0
                && theirs[0].SequenceEqual(expected));

            // 名字兜底寻址：targetUid=0 时靠 TargetNameBytes 找到接收方。
            await Dispatch(registry, a, DirectBody(1, 0, 0, ClientTextEncoding.GetBytes("乙"), text, blob, withFlag: true));
            check("direct hyperlink falls back to target name addressing",
                b.Drain().Count == 1 && a.Drain().Count == 1);

            // mode 7 + 无标志形态（旧客户端可能不带私聊标志）。
            await Dispatch(registry, a, DirectBody(7, 102, 0, ClientTextEncoding.GetBytes("test65"), text, blob, withFlag: false));
            var mode7 = b.Drain(); a.Drain();
            check("mode 7 hyperlink without flag forwards pure blob",
                mode7.Count == 1 && IsHyperlinkPacket(mode7[0], 7, 101, text, blob));

            // 回退：私聊不带名字（旧客户端）——名字长度不自洽，文本后全部视为数据块。
            await Dispatch(registry, a, HyperlinkBody(1, 102, 0, text, blob));
            var fallback = b.Drain(); a.Drain();
            check("direct hyperlink without target name keeps legacy whole-tail blob",
                fallback.Count == 1 && IsHyperlinkPacket(fallback[0], 1, 101, text, blob));
        }

        private static byte[] DirectBody(byte mode, ushort targetUid, uint targetCid, byte[] targetName, byte[] text, byte[] blob, bool withFlag)
        {
            var w = new GamePacketWriter();
            w.WriteByte(mode); w.WriteUInt16(targetUid); w.WriteUInt32(targetCid);
            w.WriteDstr(text); w.WriteDstr(targetName);
            if (withFlag) w.WriteByte(1);
            w.WriteBytes(blob);
            return w.ToArray();
        }

        private static async Task RunPartyTests(Action<string, bool> check, byte[] text, byte[] blob)
        {
            var sessions = new SessionDirectory();
            var transitions = new CharacterTransitionCoordinator(sessions);
            using var a = await Peer.Create(sessions, 101, "甲");
            using var b = await Peer.Create(sessions, 102, "乙");
            using var outsider = await Peer.Create(sessions, 103, "丙", 10011);
            var parties = new PartyManager();
            using var chat = new ChatHandler(sessions, parties, transitions);
            var registry = new GameCommandRegistry(); registry.RegisterGroup("chat", chat.RegisterHandlers);
            var created = parties.CreateParty(PartyMemberOf(a));
            var joined = parties.Join(created.Party.PartyId, PartyMemberOf(b));
            check("party fixture has two same-channel members", created.Ok && joined.Ok);

            await Dispatch(registry, a, HyperlinkBody(2, 0, 0, text, blob));
            var mine = a.Drain(); var theirs = b.Drain();
            check("mode 2 hyperlink forwards to both party members only",
                mine.Count == 1 && theirs.Count == 1 && outsider.Drain().Count == 0
                && IsHyperlinkPacket(mine[0], 2, 101, text, blob)
                && IsHyperlinkPacket(theirs[0], 2, 101, text, blob));
        }

        private static async Task RunRaidTests(Action<string, bool> check, byte[] text, byte[] blob)
        {
            var sessions = new SessionDirectory();
            var transitions = new CharacterTransitionCoordinator(sessions);
            using var leader = await Peer.Create(sessions, 201, "甲");
            using var member = await Peer.Create(sessions, 202, "乙");
            using var outsider = await Peer.Create(sessions, 203, "丙");
            var raids = new RaidManager();
            var raid = raids.Create(new byte[] { 65 }, RaidMemberOf(leader, 1), 0);
            raids.TryAddMember(raid.RaidId, RaidMemberOf(member, 2), out raid);
            using var chat = new ChatHandler(sessions, new PartyManager(), transitions, raids);
            var registry = new GameCommandRegistry(); registry.RegisterGroup("chat", chat.RegisterHandlers);

            await Dispatch(registry, member, HyperlinkBody(52, 0, 0, text, blob));
            var leaderPackets = leader.Drain();
            var memberPackets = member.Drain();
            check("mode 52 raid hyperlink sends USERINFO context then hyperlink to other members",
                leaderPackets.Count == 2 && memberPackets.Count == 1
                && BitConverter.ToUInt16(leaderPackets[0], 1) == (ushort)NotiPacketTypeA21.USERINFO
                && IsHyperlinkPacket(leaderPackets[1], 52, 202, text, blob)
                && IsHyperlinkPacket(memberPackets[0], 52, 202, text, blob)
                && outsider.Drain().Count == 0);

            await Dispatch(registry, member, HyperlinkBody(53, 0, 0, text, blob));
            check("mode 53 commander hyperlink from non-leader reaches nobody",
                leader.Drain().Count == 0 && member.Drain().Count == 0);
            await Dispatch(registry, leader, HyperlinkBody(53, 0, 0, text, blob));
            var leaderOwn = leader.Drain(); var memberGot = member.Drain();
            check("mode 53 commander hyperlink from leader uses raid routing",
                leaderOwn.Count == 1 && memberGot.Count == 2
                && IsHyperlinkPacket(leaderOwn[0], 53, 201, text, blob)
                && IsHyperlinkPacket(memberGot[1], 53, 201, text, blob));
        }

        private static async Task RunGuildTests(Action<string, bool> check, byte[] text, byte[] blob)
        {
            string path = Path.Combine(Path.GetTempPath(), $"hyperlink-guild-{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                database.Write((c, t) =>
                {
                    using var cmd = c.CreateCommand(); cmd.Transaction = t;
                    cmd.CommandText = @"INSERT INTO accounts(account_id,m_id,password_hash) VALUES(100,'hyperlink-test','');
INSERT INTO characters(character_id,account_id,name) VALUES(101,100,'hl-a'),(102,100,'hl-b'),(103,100,'hl-c');";
                    cmd.ExecuteNonQuery();
                });
                var repository = new GuildRepository(database);
                var guild = database.Write((c, t) => GuildRepository.Insert(c, t, 101, "hyperlink-guild", ""));
                repository.Apply(guild.Id, 102); repository.Approve(guild.Id, 101, 102);
                var sessions = new SessionDirectory();
                var transitions = new CharacterTransitionCoordinator(sessions);
                using var leader = await Peer.Create(sessions, 101, "甲");
                using var member = await Peer.Create(sessions, 102, "乙");
                using var outsider = await Peer.Create(sessions, 103, "丙");
                InventoryContext.Register(leader.Session.SessionId, new InventoryService(101, 100, database));
                InventoryContext.Register(member.Session.SessionId, new InventoryService(102, 100, database));
                try
                {
                    using var chat = new ChatHandler(sessions, new PartyManager(), transitions);
                    chat.ConfigureGuilds(repository, transitions);
                    await chat.Handle_ITEM_HYPERLINK_MESSAGE(leader.Session, default, HyperlinkBody(6, 0, 0, text, blob));
                    var mine = leader.Drain(); var theirs = member.Drain();
                    check("mode 6 guild hyperlink reaches members cross-channel with sender name and verbatim blob",
                        mine.Count == 1 && theirs.Count == 1 && outsider.Drain().Count == 0
                        && IsGuildHyperlinkPacket(mine[0], ClientTextEncoding.GetBytes("甲"), text, blob)
                        && IsGuildHyperlinkPacket(theirs[0], ClientTextEncoding.GetBytes("甲"), text, blob));
                    await chat.Handle_ITEM_HYPERLINK_MESSAGE(outsider.Session, default, HyperlinkBody(6, 0, 0, text, blob));
                    check("nonmember cannot send guild hyperlink",
                        leader.Drain().Count == 0 && member.Drain().Count == 0 && outsider.Drain().Count == 0);
                }
                finally
                {
                    InventoryContext.Unregister(leader.Session.SessionId);
                    InventoryContext.Unregister(member.Session.SessionId);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
        }

        private static Task Dispatch(
            GameCommandRegistry registry,
            Peer peer,
            byte[] body)
        {
            if (!registry.TryGetValue((ushort)CmdPacketTypeA21.ITEM_HYPERLINK_MESSAGE, out var handler))
                throw new InvalidOperationException("unregistered hyperlink command");
            return handler(peer.Session, new GamePacketHeader { type = (ushort)CmdPacketTypeA21.ITEM_HYPERLINK_MESSAGE }, body);
        }

        private static byte[] Padded(string hexPrefix)
        {
            var prefix = Convert.FromHexString(hexPrefix);
            if (prefix.Length > CapturedBodyBytes) throw new InvalidOperationException("captured prefix exceeds body size");
            var body = new byte[CapturedBodyBytes];
            prefix.CopyTo(body, 0);
            return body;
        }

        private static byte[] HyperlinkBody(byte mode, ushort targetUid, uint targetCid, byte[] text, byte[] blob)
        {
            var w = new GamePacketWriter();
            w.WriteByte(mode); w.WriteUInt16(targetUid); w.WriteUInt32(targetCid);
            w.WriteDstr(text); w.WriteBytes(blob);
            return w.ToArray();
        }

        private static PartyMember PartyMemberOf(Peer peer)
            => new PartyMember
            {
                UserId = peer.Session.Player.UserId,
                CharacterId = peer.Session.Player.CharacterId,
                SessionId = peer.Session.SessionId,
                Name = ClientTextEncoding.GetString(peer.Session.Player.Name),
            };

        private static RaidMember RaidMemberOf(Peer peer, ushort partyIndex)
            => new RaidMember
            {
                UserId = peer.Session.Player.UserId,
                CharacterId = (uint)peer.Session.Player.CharacterId,
                SessionId = peer.Session.SessionId,
                NameBytes = peer.Session.Player.Name,
                PartyIndex = partyIndex,
            };

        // MESSAGE_HYPER_LINK body：mode:u8 + senderUid:u16 + serverGroup:u8 +
        // dstr(文本) + 数据块原文。
        private static bool IsHyperlinkPacket(byte[] packet, byte mode, ushort senderUid, byte[] text, byte[] blob)
        {
            if (packet == null || packet.Length != 15 + 1 + 2 + 1 + 4 + text.Length + blob.Length) return false;
            if (packet[0] != 0 || BitConverter.ToUInt16(packet, 1) != (ushort)NotiPacketTypeA21.MESSAGE_HYPER_LINK) return false;
            if (packet[15] != mode || BitConverter.ToUInt16(packet, 16) != senderUid || packet[18] != 0) return false;
            if (BitConverter.ToInt32(packet, 19) != text.Length) return false;
            if (!packet.AsSpan(23, text.Length).SequenceEqual(text)) return false;
            return packet.AsSpan(23 + text.Length, blob.Length).SequenceEqual(blob);
        }

        // MESSAGE_OTHER_CHANNEL_HYPER_LINK body：mode:u8 + 0x00 + dstr(发送者名) +
        // server 字节 + dstr(文本) + 数据块原文。
        private static bool IsGuildHyperlinkPacket(byte[] packet, byte[] senderName, byte[] text, byte[] blob)
        {
            var bodyBytes = 2 + 4 + senderName.Length + 1 + 4 + text.Length + blob.Length;
            if (packet == null || packet.Length != 15 + bodyBytes) return false;
            if (packet[0] != 0 || BitConverter.ToUInt16(packet, 1) != (ushort)NotiPacketTypeA21.MESSAGE_OTHER_CHANNEL_HYPER_LINK) return false;
            if (packet[15] != 6 || packet[16] != 0) return false;
            var at = 17;
            if (BitConverter.ToInt32(packet, at) != senderName.Length) return false;
            at += 4;
            if (!packet.AsSpan(at, senderName.Length).SequenceEqual(senderName)) return false;
            at += senderName.Length;
            if (packet[at] != GameNetworkConfig.ChannelServerIndex) return false;
            at += 1;
            if (BitConverter.ToInt32(packet, at) != text.Length) return false;
            at += 4;
            if (!packet.AsSpan(at, text.Length).SequenceEqual(text)) return false;
            at += text.Length;
            return packet.AsSpan(at, blob.Length).SequenceEqual(blob);
        }

        private sealed class Peer : IDisposable
        {
            private readonly TcpClient _reader;
            internal EnhancedClientSession Session { get; }
            private Peer(TcpClient reader, EnhancedClientSession session) { _reader = reader; Session = session; }
            internal static async Task<Peer> Create(SessionDirectory sessions, int id, string name, int port = 10010)
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
                sessions.Register(id, session);
                return new Peer(reader, session);
            }
            internal List<byte[]> Drain()
            {
                var packets = new List<byte[]>(); var stream = _reader.GetStream();
                while (_reader.Client.Poll(20000, SelectMode.SelectRead))
                {
                    if (_reader.Available == 0) throw new InvalidOperationException("hyperlink test connection closed");
                    var header = new byte[15]; stream.ReadExactly(header); int length = BitConverter.ToInt32(header, 3);
                    if (length < 15 || length > 65536) throw new InvalidOperationException("invalid hyperlink packet length");
                    var packet = new byte[length]; header.CopyTo(packet, 0); stream.ReadExactly(packet.AsSpan(15)); packets.Add(packet);
                }
                return packets;
            }
            public void Dispose() { Session.Close(); _reader.Dispose(); }
        }
    }
}
