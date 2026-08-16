using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DfoServer.Infrastructure;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Network
{
    public class ChannelProtocolHandler : BaseProtocolHandler
    {
        public override string ProtocolName => "ChannelProtocol";

        public string ScriptVersion => "66";

        public string AesEncryptionKey => DateTime.Now.ToString("yyyyMMdd") + "000006";

        public string EtcFilePath => ServerPaths.ChannelInfoFilePath;

        public string TestServerIP => GameNetworkConfig.AdvertisedGameIp;

        public int TestServerPort => 10011;

        // A21 SC_ASK_CHANNEL_INFO_NEW 明文 reader 的固定布局：
        // 2B 首部字段、4B 条目数，以及每条 20B + 4B + 4B + 16B + 4B。
        // 中间两个 4B 和尾部 4B 的业务语义仍以客户端后续消费为准。
        internal const int ChannelListPrefixSize = 6;
        internal const int ChannelListNameSize = 20;
        internal const int ChannelListAddressSize = 16;
        internal const int ChannelListEntrySize = 48;

        private readonly object _responseCacheLock = new object();
        private CachedScriptResponse _cachedScriptResponse;
        private CachedChannelResponse _cachedChannelResponse;

        private sealed class CachedScriptResponse
        {
            public string Key { get; set; }
            public byte[] Data { get; set; }
            public int PlainLength { get; set; }
            public int SourceLength { get; set; }
            public string Preview { get; set; }
        }

        private sealed class CachedChannelResponse
        {
            public string Key { get; set; }
            public bool IncludeFreeDuel { get; set; }
            public byte[] Data { get; set; }
            public int ChannelCount { get; set; }
            public int PlainLength { get; set; }
            public string Names { get; set; }
            public string Head { get; set; }
        }


        private enum PACKETS : int
        {
            CS_ASK_CHANNEL_INFO = 0x1,
            CS_UPDATE_CHANNEL_INFO = 0x2,
            SC_ASK_CHANNEL_INFO = 0x3,
            CS_NOTICE_CHANNEL_SERVER = 0x4,
            CS_CHECK_SCRIPT_VERSION = 0x5,
            SC_CHECK_SCRIPT_VERSION = 0x6,
            CS_ASK_CHANNEL_SCRIPT = 0x7,
            SC_ASK_CHANNEL_SCRIPT = 0x8,
            CS_GET_SCRIPT = 0x9,
            SC_GET_SCRIPT = 0xA,
            CS_CONNECT = 0xB,
            SC_CONNECT = 0xC,
            CS_GET_GC_INFO = 0xD,
            SC_GET_GC_INFO = 0xE,
            CB_GET_CHANNEL_INFO = 0xF,
            BC_GET_CHANNEL_INFO = 0x10,
            CS_ASK_CHANNEL_INFO_NEW = 0x11,
            SC_ASK_CHANNEL_INFO_NEW = 0x12,
        }

        internal sealed class ServerInfo
        {
            public int ChannelId { get; set; }
            public string ChannelName { get; set; }
            public int MaxUserNum { get; set; }
            public int Port { get; set; }
        }

        private class ServerGroupInfo
        {
            public string ServerGroupName { get; set; }
            public int ServerCount { get; set; }
            public List<ServerInfo> Servers { get; set; }
        }

        private class ServerGroup
        {
            public int ServerGroupCount { get; set; }
            public List<ServerGroupInfo> Groups { get; set; }
        }

        public override async Task OnClientConnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Client connected: {session.SessionId}");
            FileLogger.Log(AesEncryptionKey);
            await Task.CompletedTask;
        }

        public override Task OnClientDisconnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Client disconnected: {session.SessionId}");
            return Task.CompletedTask;
        }

        public override async Task OnPacketReceived(EnhancedClientSession session, FlexiblePacket packet)
        {
            var header = packet.GetHeader<ChannelPacketHeader>();
            var msgType = (PACKETS)header.msg_no;
            var body = packet.BodyData;

            
            FileLogger.Log($"[{ProtocolName}] Packet received from {session.SessionId}:, Type={msgType}, Length={packet.TotalLength}");

            
            if (packet.BodyData != null && packet.BodyData.Length > 0)
                FileLogger.Log($"[{ProtocolName}] Packet body (hex): {BitConverter.ToString(packet.BodyData).Replace("-", " ")}");
            else
                FileLogger.Log($"[{ProtocolName}] Packet body is empty.");


            switch (msgType)
            {
                case PACKETS.CS_ASK_CHANNEL_INFO_NEW:
                    await HandleCS_ASK_CHANNEL_INFO_NEW(session, body);
                    break;
                case PACKETS.CS_CHECK_SCRIPT_VERSION:
                    await HandleCS_CHECK_SCRIPT_VERSION(session, body);
                    break;
                case PACKETS.CS_GET_SCRIPT:
                    await HandleCS_GET_SCRIPT(session, body);
                    break;
                case PACKETS.CS_CONNECT:
                    await HandleCS_CONNECT(session, body);
                    break;
                default:
                    FileLogger.Log($"[{ProtocolName}] Unknown message type: {msgType}");
                    break;
            }
        }

        
        private async Task SendResponsePacket(EnhancedClientSession session, PACKETS msgType, byte[] data)
        {
            var header = new ChannelPacketHeader()
            {
                classification = 0x7C,
                msg_no = (byte)msgType,
                sLength = (uint)(Marshal.SizeOf<ChannelPacketHeader>() + data.Length),
                check_sum = 0,
                ack = 1
            };
            var responsePacket = new FlexiblePacket(header, data);
            var responseBytes = responsePacket.GetBytes();
            await session.SendPacketAsync(responseBytes);
        }

        private async Task HandleCS_CONNECT(EnhancedClientSession session, byte[] packet)
        {
            var list = new List<byte>();
            list.AddRange(new byte[] { 0, 0, 0, 0 }); 
            list.AddRange(Encoding.ASCII.GetBytes(AesEncryptionKey));
            list.AddRange(new byte[32 - AesEncryptionKey.Length]);
            var data = list.ToArray();

            await SendResponsePacket(session, PACKETS.SC_CONNECT, data);
        }

        private async Task HandleCS_GET_SCRIPT(EnhancedClientSession session, byte[] packet)
        {
            // A21 组号之后按名字解析频道 id。
            // 中文和带反引号的 `1` 都会解析成 0，每组只能插入一条。
            // 名字发裸整数 id；地牢/extra 仍按脚本引用解析。
            var cached = GetScriptResponse();
            FileLogger.Log(
                $"[{ProtocolName}] SC_GET_SCRIPT etc=a21-ch-id-bare " +
                $"plain={cached.PlainLength} cipher={cached.Data.Length} " +
                $"src={cached.SourceLength} preview={cached.Preview}");
            await SendResponsePacket(session, PACKETS.SC_GET_SCRIPT, cached.Data);
        }

        private CachedScriptResponse GetScriptResponse()
        {
            var key = AesEncryptionKey;
            lock (_responseCacheLock)
            {
                if (_cachedScriptResponse != null &&
                    string.Equals(_cachedScriptResponse.Key, key, StringComparison.Ordinal))
                {
                    return _cachedScriptResponse;
                }

                var text = PvfArchiveAccessor.ReadChannelInfoEtc();
                return _cachedScriptResponse = BuildScriptResponse(text, key);
            }
        }

        internal byte[] BuildGetScriptResponseForSelfTest(string text, string key)
        {
            lock (_responseCacheLock)
            {
                if (_cachedScriptResponse != null &&
                    string.Equals(_cachedScriptResponse.Key, key, StringComparison.Ordinal))
                {
                    return _cachedScriptResponse.Data;
                }

                return (_cachedScriptResponse = BuildScriptResponse(text, key)).Data;
            }
        }

        private static CachedScriptResponse BuildScriptResponse(string text, string key)
        {
            var raw = BuildGetScriptPlaintext(text);
            var data = EncryptTool.EncryptData(raw, key);
            var preview = Encoding.UTF8.GetString(raw);
            if (preview.Length > 200)
                preview = preview.Substring(0, 200);

            return new CachedScriptResponse
            {
                Key = key,
                Data = data,
                PlainLength = raw.Length,
                SourceLength = text?.Length ?? 0,
                Preview = preview.Replace("\n", "\\n")
            };
        }

        internal static byte[] BuildGetScriptPlaintext(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<byte>();

            var tokens = TokenizeGetScript(text);
            var sb = new StringBuilder(text.Length + 2048);
            // 当前 channel_info.etc 的 dungeon 投影：名称、客户端引用和结束标记按 A21 顺序输出。
            sb.Append("[dungeon]\n`[none]`\n`<4::channel_info_dname_0>`\n[/dungeon]\n");

            var i = 0;
            while (i < tokens.Count)
            {
                if (tokens[i] == "[dungeon]")
                {
                    i = AppendDungeonSection(sb, tokens, i);
                    continue;
                }

                if (tokens[i] == "[server]")
                {
                    i = AppendServerSection(sb, tokens, i);
                    continue;
                }

                sb.Append(tokens[i]).Append('\n');
                i++;
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        internal static List<string> TokenizeGetScript(string text)
        {
            var tokens = new List<string>();
            var i = 0;
            while (i < text.Length)
            {
                var ch = text[i];
                if (char.IsWhiteSpace(ch))
                {
                    i++;
                    continue;
                }

                if (ch == '[')
                {
                    var end = text.IndexOf(']', i + 1);
                    if (end > i)
                    {
                        tokens.Add(text.Substring(i, end - i + 1));
                        i = end + 1;
                        continue;
                    }
                }

                if (ch == '`')
                {
                    var end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        tokens.Add(text.Substring(i, end - i + 1));
                        i = end + 1;
                        continue;
                    }
                }

                var start = i;
                while (i < text.Length
                       && !char.IsWhiteSpace(text[i])
                       && text[i] != '`'
                       && text[i] != '[')
                {
                    i++;
                }

                tokens.Add(text.Substring(start, i - start));
            }

            return tokens;
        }

        private static int AppendDungeonSection(
            StringBuilder sb,
            List<string> tokens,
            int i)
        {
            sb.Append("[dungeon]\n");
            i++;
            var bodyIndex = 0;
            var dungeonTag = string.Empty;
            while (i < tokens.Count
                   && tokens[i] != "[/dungeon]"
                   && tokens[i] != "[dungeon]"
                   && tokens[i] != "[server]")
            {
                var token = tokens[i];
                if (bodyIndex == 0)
                {
                    dungeonTag = UnwrapTick(token);
                    sb.Append(WrapTick(token));
                }
                else if (bodyIndex == 1)
                {
                    // A21 外层要反引号；去壳后要 `<4::key>`。
                    // 资源中的 name2 可能已经去壳；统一投影为 A21 客户端需要的引用形式。
                    sb.Append(WrapTick(DungeonDisplayRef(dungeonTag, token)));
                }
                else
                {
                    sb.Append(token);
                }

                sb.Append('\n');
                bodyIndex++;
                i++;
            }

            if (i < tokens.Count && tokens[i] == "[/dungeon]")
                i++;
            sb.Append("[/dungeon]\n");
            return i;
        }

        private static int AppendServerSection(
            StringBuilder sb,
            List<string> tokens,
            int i)
        {
            i++;
            if (i >= tokens.Count)
                return i;

            // ASK 头是组 1。A21 PVF 还有 2–9/98/99，全发会把客户端组表盖成最后一组。
            if (!IsScriptInt(tokens[i])
                || !int.TryParse(
                    tokens[i],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var group)
                || group != 1)
            {
                return SkipServerSection(tokens, i);
            }

            sb.Append("[server]\n");
            sb.Append(tokens[i]).Append('\n');
            i++;
            while (i < tokens.Count
                   && tokens[i] != "[/server]"
                   && tokens[i] != "[server]")
            {
                if (!LooksLikeChannelStart(tokens, i))
                {
                    i++;
                    continue;
                }

                var dungeonTag = UnwrapTick(tokens[i + 3]);
                var name = tokens[i];
                var type = tokens[i + 2];
                var dungeonDisp = WrapTick(DungeonDisplayRef(dungeonTag, tokens[i + 3]));
                var dungeonKey = WrapTick(dungeonTag);
                i += 4;
                var nums = new List<string>();
                while (i < tokens.Count && IsScriptNumber(tokens[i]))
                {
                    if (LooksLikeChannelStart(tokens, i))
                        break;
                    nums.Add(tokens[i]);
                    i++;
                }

                while (nums.Count < 11)
                    nums.Add("0");

                // A21 频道：地牢字段去壳后要 `<4::>`；
                // extra 不去壳，带反引号，内层 `[tag]` 用来查频道表。
                sb.Append(name).Append(' ');
                sb.Append(dungeonDisp).Append(' ');
                sb.Append(type).Append(' ');
                sb.Append(dungeonKey).Append(' ');
                sb.Append(string.Join(" ", nums.Take(11)));
                sb.Append(' ').Append(dungeonKey).Append('\n');
            }

            if (i < tokens.Count && tokens[i] == "[/server]")
                i++;
            sb.Append("[/server]\n");
            return i;
        }

        private static int SkipServerSection(List<string> tokens, int i)
        {
            while (i < tokens.Count
                   && tokens[i] != "[/server]"
                   && tokens[i] != "[server]")
            {
                i++;
            }

            if (i < tokens.Count && tokens[i] == "[/server]")
                i++;
            return i;
        }

        private static bool LooksLikeChannelStart(List<string> tokens, int i)
        {
            return i + 3 < tokens.Count
                   && IsScriptInt(tokens[i])
                   && !IsScriptNumber(tokens[i + 1])
                   && IsScriptInt(tokens[i + 2]);
        }

        private static bool IsScriptInt(string token)
        {
            return int.TryParse(
                token,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _);
        }

        private static bool IsScriptNumber(string token)
        {
            return double.TryParse(
                token,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out _);
        }

        private static readonly Dictionary<string, string> DungeonDisplayKeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["[none]"] = "<4::channel_info_dname_0>",
                ["[elven_guard]"] = "<4::channel_info_dname_0>",
                ["[granfloris]"] = "<4::channel_info_dname_1>",
                ["[sky_catle]"] = "<4::channel_info_dname_2>",
                ["[behemoth]"] = "<4::channel_info_dname_3>",
                ["[Alfhlyra]"] = "<4::channel_info_dname_4>",
                ["[north_myre]"] = "<4::channel_info_dname_5>",
                ["[stormpass]"] = "<4::channel_info_dname_6>",
                ["[deathtower]"] = "<4::channel_info_dname_7>",
                ["[Fortress]"] = "<4::channel_info_dname_9>",
                ["[Hall]"] = "<4::channel_info_dname_10>",
                ["[Antwer]"] = "<4::channel_info_dname_11>",
                ["[impossible]"] = "<4::channel_info_dname_12>",
                ["[seatrain]"] = "<4::channel_info_dname_13>",
                ["[dragonroad]"] = "<4::channel_info_dname_13>",
                ["[timedoor]"] = "<4::channel_info_dname_15>",
                ["[sainthorn]"] = "<4::channel_info_dname_15>",
                ["[noblsky]"] = "<4::channel_info_dname_15>",
                ["[CastleofDead]"] = "<4::channel_info_dname_15>",
                ["[powerstation]"] = "<4::channel_info_dname_16>",
                ["[attackzone]"] = "<4::channel_info_dname_17>",
                ["[zombie]"] = "<4::channel_info_dname_18>",
                ["[tournament]"] = "<4::channel_info_dname_19>",
                ["[goldenroad]"] = "<4::channel_info_dname_20>",
            };

        private static string DungeonDisplayRef(string dungeonTag, string token)
        {
            var inner = UnwrapTick(token);
            if (inner.StartsWith("<4::", StringComparison.Ordinal))
                return inner;
            if (DungeonDisplayKeys.TryGetValue(dungeonTag, out var key))
                return key;
            return "<4::channel_info_dname_0>";
        }

        private static string UnwrapTick(string token)
        {
            if (string.IsNullOrEmpty(token))
                return string.Empty;
            if (token.Length >= 2 && token[0] == '`' && token[token.Length - 1] == '`')
                return token.Substring(1, token.Length - 2);
            return token;
        }

        private static string WrapTick(string token)
        {
            if (string.IsNullOrEmpty(token))
                return "`_`";
            if (token.Length >= 2 && token[0] == '`' && token[token.Length - 1] == '`')
                return token;
            return "`" + token + "`";
        }

        private async Task HandleCS_ASK_CHANNEL_INFO_NEW(EnhancedClientSession session, byte[] packet)
        {
            var cached = GetChannelResponse(GameNetworkConfig.FreeDuelListenerEnabled);
            FileLogger.Log(
                $"[{ProtocolName}] SC_ASK_CHANNEL_INFO_NEW channels={cached.ChannelCount} " +
                $"plain={cached.PlainLength} cipher={cached.Data.Length} zlib=on " +
                $"names={cached.Names} head={cached.Head}");
            await SendResponsePacket(session, PACKETS.SC_ASK_CHANNEL_INFO_NEW, cached.Data);
        }

        private CachedChannelResponse GetChannelResponse(bool includeFreeDuel)
        {
            var key = AesEncryptionKey;
            lock (_responseCacheLock)
            {
                if (_cachedChannelResponse != null &&
                    string.Equals(_cachedChannelResponse.Key, key, StringComparison.Ordinal) &&
                    _cachedChannelResponse.IncludeFreeDuel == includeFreeDuel)
                {
                    return _cachedChannelResponse;
                }

                var channels = LoadChannels(
                    json: null,
                    includeFreeDuel: includeFreeDuel);
                var plaintext = BuildChannelListPlaintext(channels);
                var data = EncryptTool.EncryptData(plaintext, key);
                return _cachedChannelResponse = new CachedChannelResponse
                {
                    Key = key,
                    IncludeFreeDuel = includeFreeDuel,
                    Data = data,
                    ChannelCount = channels.Count,
                    PlainLength = plaintext.Length,
                    Names = string.Join(",", channels.Select(c => c.ChannelName)),
                    Head = BitConverter.ToString(data, 0, Math.Min(16, data.Length))
                };
            }
        }

        internal byte[] BuildChannelListPlaintext(
            IReadOnlyList<ServerInfo> channels)
        {
            if (channels == null)
                throw new ArgumentNullException(nameof(channels));

            var expectedLength = checked(
                ChannelListPrefixSize
                + ChannelListEntrySize * channels.Count);
            var list = new List<byte>(expectedLength);

            // A21 当前 reader 只证明该字段存在；现有 channel_info.etc
            // 路径使用组 1，保留原值直到获得真实 7001 原始响应。
            WriteUInt16(list, 1);
            WriteInt32(list, channels.Count);
            foreach (var channel in channels)
            {
                WriteFixedField(list, channel.ChannelName, ChannelListNameSize);
                WriteInt32(list, channel.MaxUserNum);
                WriteInt32(list, 0);
                WriteFixedField(list, TestServerIP, ChannelListAddressSize);
                WriteInt32(list, channel.Port);
            }

            if (list.Count != expectedLength)
                throw new InvalidOperationException(
                    $"A21 channel list layout mismatch: "
                    + $"actual={list.Count}, expected={expectedLength}");

            return list.ToArray();
        }

        internal static List<ServerInfo> LoadChannels(
            string json,
            bool includeFreeDuel)
        {
            var result = new List<ServerInfo>();
            var channelIds = new HashSet<int>();
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in document.RootElement.EnumerateArray())
                        {
                            if (element.ValueKind != JsonValueKind.Object)
                                continue;

                            var channelId = ReadChannelId(element);
                            if (channelId < byte.MinValue ||
                                channelId > byte.MaxValue ||
                                (!includeFreeDuel &&
                                 GameNetworkConfig.IsFreeDuelChannel(channelId)) ||
                                !channelIds.Add(channelId))
                            {
                                continue;
                            }

                            var name = element.TryGetProperty("name", out var nameValue) &&
                                       nameValue.ValueKind == JsonValueKind.String &&
                                       !string.IsNullOrWhiteSpace(nameValue.GetString())
                                ? nameValue.GetString()
                                : $"ch.{channelId}";
                            var maxUser = ReadMaxUser(element);
                            result.Add(
                                new ServerInfo
                                {
                                    ChannelId = channelId,
                                    ChannelName = name,
                                    MaxUserNum = maxUser,
                                    Port = ResolveSelectorPort(channelId)
                                });
                        }
                    }
                }
                catch (JsonException ex)
                {
                    FileLogger.Log(
                        $"[{nameof(ChannelProtocolHandler)}] invalid channel list: " +
                        ex.Message);
                    result.Clear();
                    channelIds.Clear();
                }

                foreach (var channel in
                         GameNetworkConfig.BuildGameChannels(includeFreeDuel))
                {
                    if (channelIds.Add(channel.ChannelId))
                        result.Add(CreateDefaultChannel(channel.ChannelId));
                }

                return result;
            }

            foreach (var channel in LoadChannelsFromEtc(serverGroup: 1))
            {
                if (channelIds.Add(channel.ChannelId))
                    result.Add(channel);
            }

            if (includeFreeDuel
                && channelIds.Add(GameNetworkConfig.FreeDuelChannelIndex))
            {
                result.Add(
                    CreateDefaultChannel(
                        GameNetworkConfig.FreeDuelChannelIndex));
            }

            return result;
        }

        private static int ReadChannelId(JsonElement element)
        {
            if (element.TryGetProperty("id", out var id))
            {
                if (id.ValueKind == JsonValueKind.Number &&
                    id.TryGetInt32(out var numericId))
                    return numericId;
                if (id.ValueKind == JsonValueKind.String &&
                    int.TryParse(id.GetString(), out var stringId))
                    return stringId;
            }

            return GameNetworkConfig.NormalChannelIndex;
        }

        private static int ReadMaxUser(JsonElement element)
        {
            if (!element.TryGetProperty("maxUser", out var maxUser))
                return 500;
            if (maxUser.ValueKind == JsonValueKind.Number &&
                maxUser.TryGetInt32(out var numeric))
                return Math.Max(0, numeric);
            if (maxUser.ValueKind == JsonValueKind.String &&
                int.TryParse(maxUser.GetString(), out var text))
                return Math.Max(0, text);
            return 500;
        }

        private static ServerInfo CreateDefaultChannel(int channelId)
            => new ServerInfo
            {
                ChannelId = channelId,
                ChannelName = GameNetworkConfig.FindGameChannel(channelId)?.SelectorName
                              ?? $"#ch.{channelId}",
                MaxUserNum = 500,
                Port = ResolveSelectorPort(channelId)
            };

        // A21 [server]：组号后同一行重复 id / 名称 / type / dungeon / 数值。
        internal static List<ServerInfo> LoadChannelsFromEtc(int serverGroup)
        {
            var text = PvfArchiveAccessor.ReadChannelInfoEtc();
            var root = new ScriptParser().Parse(text);
            var result = new List<ServerInfo>();
            var seen = new HashSet<int>();
            foreach (var server in root.GetChildren("server"))
            {
                var line = string.Join(
                    " ",
                    server.DataItems
                        .Select(item => item.GetContent(text).Trim())
                        .Where(part => !string.IsNullOrWhiteSpace(part)));
                var tokens = ScriptValueTokenizer.Tokenize(line);
                if (tokens.Count < 5)
                    continue;
                if (!int.TryParse(
                        tokens[0],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var group)
                    || group != serverGroup)
                {
                    continue;
                }

                var i = 1;
                while (i + 3 < tokens.Count)
                {
                    if (!int.TryParse(
                            tokens[i],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var channelId)
                        || channelId < byte.MinValue
                        || channelId > byte.MaxValue)
                    {
                        break;
                    }

                    _ = tokens[i + 1];
                    if (!int.TryParse(
                            tokens[i + 2],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out _))
                    {
                        break;
                    }

                    i += 4;
                    while (i < tokens.Count)
                    {
                        var isNumber = double.TryParse(
                            tokens[i],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out _);
                        var nextIsName = i + 1 < tokens.Count
                            && !double.TryParse(
                                tokens[i + 1],
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out _);
                        if (isNumber && nextIsName)
                            break;
                        if (!isNumber)
                            break;
                        i++;
                    }

                    if (!seen.Add(channelId))
                        continue;

                    result.Add(
                        new ServerInfo
                        {
                            ChannelId = channelId,
                            ChannelName = $"ch.{channelId}",
                            MaxUserNum = 500,
                            Port = ResolveSelectorPort(channelId)
                        });
                }
            }

            return result;
        }

        private static int ResolveSelectorPort(int channelId)
        {
            var channel = GameNetworkConfig.FindGameChannel(channelId)
                          ?? GameNetworkConfig.FindGameChannel(
                              GameNetworkConfig.NormalChannelIndex);
            return channel.PublicGamePort;
        }

        private static void WriteFixedField(
            List<byte> target,
            string value,
            int size)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            target.AddRange(bytes.Take(size));
            if (bytes.Length < size)
                target.AddRange(new byte[size - bytes.Length]);
        }

        private static void WriteUInt16(List<byte> target, ushort value)
        {
            target.AddRange(BitConverter.GetBytes(value));
        }

        private static void WriteInt32(List<byte> target, int value)
        {
            target.AddRange(BitConverter.GetBytes(value));
        }

        private async Task HandleCS_CHECK_SCRIPT_VERSION(EnhancedClientSession session, byte[] packet)
        {
            var list = new List<byte>();
            list.AddRange(new byte[] { 0, 0, 0, 0 }); 
            list.AddRange(Encoding.ASCII.GetBytes(ScriptVersion));
            list.AddRange(new byte[16 - ScriptVersion.Length]);
            var data = EncryptTool.EncryptData(list.ToArray(), AesEncryptionKey, false);
            await SendResponsePacket(session, PACKETS.SC_CHECK_SCRIPT_VERSION, data);
        }
    }
}
