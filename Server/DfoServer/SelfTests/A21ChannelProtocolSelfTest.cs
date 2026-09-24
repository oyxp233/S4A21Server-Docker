using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class A21ChannelProtocolSelfTest
    {
        private const string Key = "20260815000006";
        private const int HeaderSize = 11;

        public static int Run()
        {
            Console.WriteLine("=== A21_CHANNEL_PROTOCOL selftest ===");
            var failures = 0;
            var handler = new ChannelProtocolHandler();
            var channels = new List<ChannelProtocolHandler.ServerInfo>
            {
                new ChannelProtocolHandler.ServerInfo
                {
                    ChannelId = 11,
                    ChannelName = "ch.11",
                    MaxUserNum = 500,
                    Port = 10011
                },
                new ChannelProtocolHandler.ServerInfo
                {
                    ChannelId = 100,
                    ChannelName = "ch.100",
                    MaxUserNum = 900,
                    Port = 10161
                }
            };

            var plaintext = handler.BuildChannelListPlaintext(channels);
            var cursor = 0;
            var prefixReadable = TryReadUInt16(
                plaintext,
                ref cursor,
                out var group);
            var countReadable = TryReadInt32(
                plaintext,
                ref cursor,
                out var channelCount);
            var entries = new List<ParsedChannelEntry>();
            var entriesReadable = prefixReadable
                                  && countReadable
                                  && channelCount >= 0;
            if (entriesReadable)
            {
                for (var i = 0; i < channelCount; i++)
                {
                    if (!TryReadChannelEntry(
                            plaintext,
                            ref cursor,
                            out var entry))
                    {
                        entriesReadable = false;
                        break;
                    }

                    entries.Add(entry);
                }
            }

            Check(
                "A21 channel plaintext has reader prefix",
                prefixReadable
                && countReadable
                && group == 1
                && channelCount == channels.Count
                && cursor >= ChannelProtocolHandler.ChannelListPrefixSize,
                ref failures);
            Check(
                "A21 channel plaintext has fixed 48B entries",
                plaintext.Length
                    == ChannelProtocolHandler.ChannelListPrefixSize
                       + ChannelProtocolHandler.ChannelListEntrySize
                         * channels.Count,
                ref failures);
            Check(
                "A21 channel reader consumes every entry field",
                entriesReadable
                && entries.Count == channels.Count
                && cursor == plaintext.Length,
                ref failures);
            Check(
                "A21 channel reader gets both fixed-width names",
                entries.Count == 2
                && entries[0].Name == "ch.11"
                && entries[1].Name == "ch.100",
                ref failures);
            Check(
                "A21 channel reader gets both field_1 values",
                entries.Count == 2
                && entries[0].Field1 == 500
                && entries[1].Field1 == 900,
                ref failures);
            Check(
                "A21 channel reader gets both field_2 values",
                entries.Count == 2
                && entries[0].Field2 == 0
                && entries[1].Field2 == 0,
                ref failures);
            Check(
                "A21 channel reader gets both address fields",
                entries.Count == 2
                && entries[0].Address == GameNetworkConfig.AdvertisedGameIp
                && entries[1].Address == GameNetworkConfig.AdvertisedGameIp,
                ref failures);
            Check(
                "A21 channel reader gets both tail fields",
                entries.Count == 2
                && entries[0].Tail == 10011
                && entries[1].Tail == 10161,
                ref failures);

            var selectorCatalog = ChannelProtocolHandler.LoadChannels(
                json: null,
                includeFreeDuel: false);
            var selectorCatalogPlaintext = handler.BuildChannelListPlaintext(
                selectorCatalog);
            Check(
                "A21 selector catalog comes from channel_info.etc group 1",
                selectorCatalog.Count == 16
                && selectorCatalog.Any(channel => channel.ChannelId == 1)
                && selectorCatalog.Any(channel => channel.ChannelId == 11)
                && selectorCatalog.Any(channel => channel.ChannelId == 200)
                && !selectorCatalog.Any(channel => channel.ChannelId == 201)
                && !selectorCatalog.Any(channel =>
                    channel.ChannelId == GameNetworkConfig.FreeDuelChannelIndex)
                && !selectorCatalog.Any(channel => channel.ChannelId == 50
                    || channel.ChannelId == 60 || channel.ChannelId == 70)
                && selectorCatalog.Single(
                       channel => channel.ChannelId == 200).MaxUserNum == 250
                && selectorCatalog.All(channel =>
                    channel.ChannelName == $"#ch.{channel.ChannelId}"),
                ref failures);
            Check(
                "A21 disabled PvP catalog keeps 16 valid 48B entries",
                selectorCatalogPlaintext.Length == 774
                && BitConverter.ToInt32(selectorCatalogPlaintext, 2) == 16,
                ref failures);
            CheckRaidChannelRowValidation(ref failures);

            var definitions = ChannelProtocolHandler.ParseScriptChannels(
                File.ReadAllText(ServerPaths.ChannelInfoFilePath));
            GameNetworkConfig.ConfigureChannelCatalog(definitions);
            try
            {
                var pvpCatalog = ChannelProtocolHandler.LoadChannels(null, includeFreeDuel: true);
                var enabledListeners = GameNetworkConfig.BuildGameChannels(includeFreeDuel: true);
                Check(
                    "A21 PvP catalog exposes every configured listener exactly once",
                    pvpCatalog.Count == 28
                    && enabledListeners.Count == 28
                    && enabledListeners.Select(channel => channel.ListenerGamePort).Distinct().Count() == 28
                    && pvpCatalog.All(channel => enabledListeners.Any(
                        endpoint => endpoint.ChannelId == channel.ChannelId)),
                    ref failures);
                foreach (var (channelId, environment) in new[]
                {
                    (50, 24), (54, 24), (60, 8), (64, 8), (68, 13), (70, 13)
                })
                {
                    var port = GameNetworkConfig.PortForChannel(channelId);
                    Check(
                        $"A21 CH.{channelId} uses its PvP environment and restores the town only after leaving PvP",
                        GameNetworkConfig.IsPvpListener(port)
                        && LoginPacketBuilder.BuildLoginSuccess(port)[3] == environment
                        && !LoginHandler.IsListenerAdmissionAllowed(port, false)
                        && LoginHandler.IsListenerAdmissionAllowed(port, true)
                        && !GameChannelSpawnPolicy.ShouldPersistPosition(port),
                        ref failures);
                }
                Check(
                    "A21 disabled PvP removes its listeners without changing normal login",
                    GameNetworkConfig.BuildGameChannels(includeFreeDuel: false).Count == 16
                    && LoginPacketBuilder.BuildLoginSuccess(10011)[3] == GameNetworkConfig.GeneralChannelEnvironment
                    && GameChannelSpawnPolicy.ShouldPersistPosition(10011),
                    ref failures);
                CheckPvpLobbyInitialization(ref failures);
            }
            finally
            {
                GameNetworkConfig.ConfigureChannelCatalog(null);
            }

            var unavailablePvp = PvpChannelInfoHandler.BuildErrorBody();
            Check(
                "A21 unavailable PvP uses ordinary failure instead of the mercenary-only 0x15 branch",
                PvpChannelInfoHandler.CommandType == (ushort)CmdPacketTypeA21.PVP_CHANNEL_INFO
                && unavailablePvp.SequenceEqual(new byte[] { 0, 0 }),
                ref failures);
            var availablePvp = PvpChannelInfoHandler.BuildSuccessBody();
            Check(
                "A21 PvP channel success keeps its 6B context and empty cross-server list",
                availablePvp.Length == 6
                && availablePvp[0] == 1
                && BitConverter.ToInt32(availablePvp, 1) == 0
                && availablePvp[5] == 0,
                ref failures);

            var encrypted = EncryptTool.EncryptData(plaintext, Key);
            var decrypted = EncryptTool.DecryptData(encrypted, Key);
            Check(
                "A21 channel AES/zlib round-trip preserves plaintext",
                encrypted.Length > 2
                && encrypted[0] == 0x78
                && encrypted[1] == 0x9C
                && decrypted.Length >= plaintext.Length
                && decrypted.Take(plaintext.Length).SequenceEqual(plaintext)
                && decrypted.Skip(plaintext.Length).All(value => value == 0),
                ref failures);

            var header = new ChannelPacketHeader
            {
                classification = 0x7C,
                msg_no = 0x12,
                sLength = (uint)(HeaderSize + encrypted.Length),
                check_sum = 0,
                ack = 1
            };
            var wire = new FlexiblePacket(header, encrypted).GetBytes();
            Check(
                "A21 SC_ASK_CHANNEL_INFO_NEW uses an 11B header",
                ((IPacketHeader)header).GetHeaderSize() == HeaderSize
                && wire.Length == HeaderSize + encrypted.Length
                && BitConverter.ToUInt32(wire, 2) == wire.Length
                && wire[0] == 0x7C
                && wire[1] == 0x12
                && wire[10] == 1,
                ref failures);

            var processor = new FlexiblePacketProcessor();
            var clientId = Guid.NewGuid();
            processor.SetClientPacketStructure(clientId, new ChannelPacketHeader());
            var packets = processor.ProcessReceivedData(
                clientId,
                wire,
                wire.Length);
            var parsed = packets.Count == 1 ? packets[0] : null;
            Check(
                "A21 channel wire packet survives TCP framing",
                parsed != null
                && parsed.GetHeader<ChannelPacketHeader>().msg_no == 0x12
                && parsed.BodyData != null
                && parsed.BodyData.SequenceEqual(encrypted),
                ref failures);

            Check(
                "channel-100 town is rejected from normal channels and allowed on ch100",
                !GameChannelSpawnPolicy.CanEnterTown(
                    GameNetworkConfig.NormalGamePort,
                    GameChannelSpawnPolicy.Channel100TownId)
                && GameChannelSpawnPolicy.CanEnterTown(
                    GameNetworkConfig.Channel100GamePort,
                    GameChannelSpawnPolicy.Channel100TownId)
                && !GameChannelSpawnPolicy.CanEnterTown(
                    GameNetworkConfig.Channel100GamePort,
                    1)
                && GameChannelSpawnPolicy.CanEnterTown(
                    GameNetworkConfig.NormalGamePort,
                    1)
                && GameChannelSpawnPolicy.CanEnterTown(
                    GameNetworkConfig.RaidGamePort,
                    GameChannelSpawnPolicy.RaidTownId)
                && !GameChannelSpawnPolicy.CanEnterTown(
                    GameNetworkConfig.RaidGamePort,
                    1),
                ref failures);
            Check(
                "restriction message names ch100 town only for normal-channel attempts",
                ChannelTownRestrictionSender.ResolveRestrictionMessage(
                    GameNetworkConfig.NormalGamePort,
                    GameChannelSpawnPolicy.Channel100TownId)
                    == "当前频道无法前往圣者之鸣号。"
                && ChannelTownRestrictionSender.ResolveRestrictionMessage(
                    GameNetworkConfig.NormalGamePort,
                    1)
                    == "当前频道无法前往其他城镇。"
                && ChannelTownRestrictionSender.ResolveRestrictionMessage(
                    GameNetworkConfig.Channel100GamePort,
                    1)
                    == "当前频道无法前往其他城镇。"
                && ChannelTownRestrictionSender.ResolveRestrictionMessage(
                    GameNetworkConfig.RaidGamePort,
                    GameChannelSpawnPolicy.Channel100TownId)
                    == "当前频道无法前往其他城镇。"
                && ChannelTownRestrictionSender.ResolveRestrictionMessage(
                    GameNetworkConfig.NormalGamePort,
                    null)
                    == "当前频道无法前往其他城镇。",
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_CHANNEL_PROTOCOL selftest passed."
                    : $"A21_CHANNEL_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        // A21 客户端解析 channel_info.etc 时对 `201 [raid] 32` 打印
        // "CHANNEL>> error [CHANNEL LIST] 201 [raid] 32" 并丢弃该行, 客户端因此没有
        // 该频道的名称与类型。服务端必须一致地丢弃类型不符的攻坚行, 否则会为它建监听
        // 并下发条目; 同时攻坚身份必须按目录类型判定, 而不是写死频道 200。
        private static void CheckRaidChannelRowValidation(ref int failures)
        {
            const string script =
                "[server]\n"
                + "1\n"
                + "   11 \t`<4::chn_channel_info_004>` 1 `[sky_catle]` 5 0 0 0 0 0 0 0 0 0 0 0 ``\n"
                + "   200\t`<4::chn_channel_info_016>` 23 `[raid]` 5 0 0 0 0 0 0 0 0 0 0 0 ``\n"
                + "   201\t`<4::chn_channel_info_037>` 32 `[raid]` 5 0 0 0 0 0 0 0 0 0 0 0 ``\n"
                + "   202\t`<4::chn_channel_info_038>` 23 `[raid]` 5 0 0 0 0 0 0 0 0 0 0 0 ``\n"
                + "[/server]\n";
            var definitions = ChannelProtocolHandler.ParseScriptChannels(script);
            Check(
                "A21 drops raid rows whose type is not the raid environment",
                definitions.Count == 3
                && !definitions.Any(channel => channel.ChannelId == 201)
                && definitions.Any(channel => channel.ChannelId == 11)
                && definitions.Single(channel => channel.ChannelId == 200).ChannelType
                    == GameNetworkConfig.RaidChannelEnvironment,
                ref failures);

            GameNetworkConfig.ConfigureChannelCatalog(definitions);
            try
            {
                Check(
                    "A21 raid identity follows the catalog type instead of channel 200",
                    GameNetworkConfig.IsRaidChannel(200)
                    && GameNetworkConfig.IsRaidChannel(202)
                    && !GameNetworkConfig.IsRaidChannel(11)
                    && GameNetworkConfig.IsRaidListener(
                        GameNetworkConfig.PortForChannel(200))
                    && GameNetworkConfig.IsRaidListener(
                        GameNetworkConfig.PortForChannel(202))
                    && !GameNetworkConfig.IsRaidListener(
                        GameNetworkConfig.PortForChannel(201))
                    && !GameNetworkConfig.IsRaidListener(
                        GameNetworkConfig.PortForChannel(11))
                    && !GameNetworkConfig.IsRaidListener(
                        GameNetworkConfig.NormalGamePort)
                    && GameNetworkConfig.ResolveLoginEnvironment(
                        GameNetworkConfig.PortForChannel(202))
                        == GameNetworkConfig.RaidChannelEnvironment
                    && GameNetworkConfig.ResolveLoginEnvironment(
                        GameNetworkConfig.PortForChannel(11))
                        == GameNetworkConfig.GeneralChannelEnvironment,
                    ref failures);
            }
            finally
            {
                GameNetworkConfig.ConfigureChannelCatalog(null);
            }
        }

        private static void CheckPvpLobbyInitialization(ref int failures)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"a21_pvp_lobby_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var database = new GameDatabase(Path.Combine(directory, "inventory.db"),
                    ServerPaths.SchemaFilePath);
                var sessions = new SessionDirectory();
                var sent = new List<byte[]>();
                using var pvp = new PvpRoomHandler(
                    sessions,
                    _ => throw new InvalidOperationException("Lobby must not request full room-entry USERINFO"),
                    new CharacterTransitionCoordinator(sessions),
                    isFreeDuelAvailable: () => true,
                    database: database,
                    sendQueuedPacket: (_, packet, _) =>
                    {
                        sent.Add(packet);
                        return Task.CompletedTask;
                    });
                foreach (var port in new[] { 10011, 10050, 10060, 10068, 10070 })
                {
                    var session = new EnhancedClientSession(null, new GamePacketHeader(), port)
                    {
                        Account = new AccountRecord { AccountId = 1 }
                    };
                    session.Player.CharacterId = 40000 + port;
                    session.Player.UserId = (ushort)session.Player.CharacterId;
                    session.Player.Name = Encoding.ASCII.GetBytes("pvp-lobby");
                    session.Player.Level = 86;
                    session.Player.UserState = 0;
                    session.Player.PvpGrade = 10;
                    session.Player.Subtype0Tail = new UserInfoMinimumTailSnapshot();
                    session.Player.TownPresenceReady = true;
                    session.GameSession = new GameSession(session, database);
                    sessions.Register(session.Player.CharacterId, session);
                    sent.Clear();
                    pvp.HandleLobbyReadyAsync(session).GetAwaiter().GetResult();
                    var isPvp = port != 10011;
                    Check(
                        $"listener {port} publishes the real PvP lobby sequence only in a PvP environment",
                        isPvp
                            ? sent.Count == 2
                              && sent[0][0] == 0
                               && BitConverter.ToUInt16(sent[0], 1) == (ushort)NotiPacketTypeA21.USERINFO
                               && sent[0][15] == 0
                               && BitConverter.ToUInt16(sent[0], 16) == 1
                               && BitConverter.ToUInt16(sent[0], 15 + 41) == session.Player.UserId
                              && sent[1][0] == 0
                              && BitConverter.ToUInt16(sent[1], 1) == (ushort)NotiPacketTypeA21.PVP_ROOM_INFO
                              && sent[1].Length == 17
                              && BitConverter.ToUInt16(sent[1], 15) == 0
                              && pvp.IsLobbyReadyForTest(session.SessionId)
                               && session.Player.TownPresenceReady
                            : sent.Count == 0 && session.Player.TownPresenceReady,
                        ref failures);
                    sent.Clear();
                    pvp.HandleLobbyReadyAsync(session).GetAwaiter().GetResult();
                    Check($"listener {port} does not replay an initialized lobby",
                        sent.Count == 0, ref failures);
                    sessions.UnregisterAsync(session.Player.CharacterId, session)
                        .GetAwaiter().GetResult();
                    Check($"listener {port} releases lobby state on session exit",
                        !pvp.IsLobbyReadyForTest(session.SessionId), ref failures);
                    session.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Check("PvP lobby initialization harness completes", false, ref failures);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        }

        private static string ReadFixedClientText(
            byte[] bytes,
            int offset,
            int count)
        {
            return ClientTextEncoding.GetString(bytes, offset, count);
        }

        private static bool TryReadChannelEntry(
            byte[] bytes,
            ref int cursor,
            out ParsedChannelEntry entry)
        {
            entry = null;
            if (!TryReadFixedClientText(
                    bytes,
                    ref cursor,
                    ChannelProtocolHandler.ChannelListNameSize,
                    out var name)
                || !TryReadInt32(bytes, ref cursor, out var field1)
                || !TryReadInt32(bytes, ref cursor, out var field2)
                || !TryReadFixedClientText(
                    bytes,
                    ref cursor,
                    ChannelProtocolHandler.ChannelListAddressSize,
                    out var address)
                || !TryReadInt32(bytes, ref cursor, out var tail))
            {
                return false;
            }

            entry = new ParsedChannelEntry
            {
                Name = name,
                Field1 = field1,
                Field2 = field2,
                Address = address,
                Tail = tail
            };
            return true;
        }

        private static bool TryReadFixedClientText(
            byte[] bytes,
            ref int cursor,
            int count,
            out string value)
        {
            value = string.Empty;
            if (bytes == null
                || cursor < 0
                || count < 0
                || bytes.Length - cursor < count)
            {
                return false;
            }

            value = ReadFixedClientText(bytes, cursor, count);
            cursor += count;
            return true;
        }

        private static bool TryReadUInt16(
            byte[] bytes,
            ref int cursor,
            out ushort value)
        {
            value = 0;
            if (bytes == null
                || cursor < 0
                || bytes.Length - cursor < sizeof(ushort))
            {
                return false;
            }

            value = BitConverter.ToUInt16(bytes, cursor);
            cursor += sizeof(ushort);
            return true;
        }

        private static bool TryReadInt32(
            byte[] bytes,
            ref int cursor,
            out int value)
        {
            value = 0;
            if (bytes == null
                || cursor < 0
                || bytes.Length - cursor < sizeof(int))
            {
                return false;
            }

            value = BitConverter.ToInt32(bytes, cursor);
            cursor += sizeof(int);
            return true;
        }

        private sealed class ParsedChannelEntry
        {
            public string Name { get; set; }
            public int Field1 { get; set; }
            public int Field2 { get; set; }
            public string Address { get; set; }
            public int Tail { get; set; }
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
