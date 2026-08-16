using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class FreeDuelChannelSelfTest
    {
        public static int Run()
        {
            var failures = 0;

            var normalOnly =
                GameNetworkConfig.BuildGameChannels(
                    includeFreeDuel: false);
            var withFreeDuel =
                GameNetworkConfig.BuildGameChannels(
                    includeFreeDuel: true);
            Check(
                "startup listener gate keeps TCP 10068 fail-closed",
                normalOnly.Count == 3
                && normalOnly[0].ChannelId
                    == GameNetworkConfig.NormalChannelIndex
                && normalOnly[1].ChannelId
                    == GameNetworkConfig.Channel100Index
                && normalOnly[2].ChannelId
                    == GameNetworkConfig.RaidChannelIndex
                && normalOnly[0].PublicGamePort
                    == GameNetworkConfig.NormalGamePort
                && normalOnly.All(
                    channel =>
                        channel.ListenerGamePort
                        != GameNetworkConfig.FreeDuelGamePort),
                ref failures);
            Check(
                "enabled listener set binds distinct CH.68/TCP 10068",
                withFreeDuel.Count == 4
                && withFreeDuel.Any(
                    channel =>
                        channel.ChannelId
                            == GameNetworkConfig.FreeDuelChannelIndex
                        && channel.PublicGamePort
                            == GameNetworkConfig.FreeDuelGamePort
                        && channel.ListenerGamePort
                            == GameNetworkConfig.FreeDuelGamePort),
                ref failures);

            var initial =
                LoginPacketBuilder.BuildInitialLoginNotice(
                    GameNetworkConfig.FreeDuelGamePort);
            Check(
                "CH.68 login notice identifies the free-duel listener",
                initial.Length > 20
                && BitConverter.ToInt32(initial, 1) == 5
                && Encoding.ASCII.GetString(initial, 5, 5) == "ch.68"
                && initial[18]
                    == GameNetworkConfig.ChannelServerIndex
                && initial[19]
                    == GameNetworkConfig.FreeDuelChannelIndex,
                ref failures);

            var success =
                LoginPacketBuilder.BuildLoginSuccess(
                    GameNetworkConfig.FreeDuelGamePort);
            Check(
                "CH.68 enters CHANNEL_INTEGRATED_FREEPVP (0x0D), not selector bootstrap 0x18",
                success.Length > 4
                && success[3]
                    == GameNetworkConfig.FreeDuelChannelEnvironment
                && success[3] == 0x0D
                && success[3] != 0x18,
                ref failures);

            Check(
                "runtime admission gate never blocks the normal listener",
                LoginHandler.IsListenerAdmissionAllowed(
                    GameNetworkConfig.NormalGamePort,
                    freeDuelChannelEnabled: false),
                ref failures);
            Check(
                "runtime admission gate protects CH.68",
                !LoginHandler.IsListenerAdmissionAllowed(
                    GameNetworkConfig.FreeDuelGamePort,
                    freeDuelChannelEnabled: false)
                && LoginHandler.IsListenerAdmissionAllowed(
                    GameNetworkConfig.FreeDuelGamePort,
                    freeDuelChannelEnabled: true),
                ref failures);

            var disabledSelector =
                ChannelProtocolHandler.LoadChannels(
                    "[{\"id\":68,\"name\":\"#ch.68\",\"maxUser\":500}]",
                    includeFreeDuel: false);
            Check(
                "disabled selector never publishes CH.68",
                disabledSelector.Count == 3
                && disabledSelector[0].ChannelId
                    == GameNetworkConfig.NormalChannelIndex
                && disabledSelector[1].ChannelId
                    == GameNetworkConfig.Channel100Index
                && disabledSelector[2].ChannelId
                    == GameNetworkConfig.RaidChannelIndex
                && disabledSelector[0].Port
                    == GameNetworkConfig.NormalGamePort,
                ref failures);

            var enabledSelector =
                ChannelProtocolHandler.LoadChannels(
                    "["
                    + "{\"id\":11,\"name\":\"#ch.11\",\"maxUser\":500},"
                    + "{\"id\":\"68\",\"name\":\"#ch.68\",\"maxUser\":500},"
                    + "{\"id\":68,\"name\":\"duplicate\",\"maxUser\":1}"
                    + "]",
                    includeFreeDuel: true);
            Check(
                "enabled selector publishes one CH.68 on TCP 10068",
                enabledSelector.Count == 4
                && enabledSelector.Count(
                    channel =>
                        channel.ChannelId
                        == GameNetworkConfig.FreeDuelChannelIndex) == 1
                && enabledSelector.Single(
                        channel =>
                            channel.ChannelId
                            == GameNetworkConfig.FreeDuelChannelIndex)
                    .Port
                    == GameNetworkConfig.FreeDuelGamePort,
                ref failures);

            var defaultEnabledSelector =
                ChannelProtocolHandler.LoadChannels(
                    json: null,
                    includeFreeDuel: true);
            var defaultDisabledSelector =
                ChannelProtocolHandler.LoadChannels(
                    json: null,
                    includeFreeDuel: false);
            Check(
                "runtime enable appends CH.68 to the default selector",
                defaultEnabledSelector.Count
                    == defaultDisabledSelector.Count + 1
                && defaultEnabledSelector
                    .Take(defaultDisabledSelector.Count)
                    .Select(channel => channel.ChannelId)
                    .SequenceEqual(
                        defaultDisabledSelector.Select(
                            channel => channel.ChannelId))
                && defaultEnabledSelector[^1].ChannelId
                    == GameNetworkConfig.FreeDuelChannelIndex,
                ref failures);

            var plaintext =
                new ChannelProtocolHandler()
                    .BuildChannelListPlaintext(
                        defaultEnabledSelector);
            const int headerSize = 6;
            const int channelBlockSize = 48;
            const int channelPortOffset = 44;
            var freeDuelIndex = defaultEnabledSelector.FindIndex(
                channel =>
                    channel.ChannelId
                    == GameNetworkConfig.FreeDuelChannelIndex);
            var freeDuelBlockOffset =
                headerSize + channelBlockSize * freeDuelIndex;
            Check(
                "selector wire block carries CH.68 name and TCP 10068",
                plaintext.Length
                    == headerSize
                       + channelBlockSize * defaultEnabledSelector.Count
                && BitConverter.ToInt32(plaintext, 2)
                    == defaultEnabledSelector.Count
                && freeDuelIndex >= 0
                && ReadFixedAscii(
                        plaintext,
                        freeDuelBlockOffset,
                        20)
                    == "#ch.68"
                && BitConverter.ToInt32(
                        plaintext,
                        freeDuelBlockOffset
                        + channelPortOffset)
                    == GameNetworkConfig.FreeDuelGamePort,
                ref failures);

            var channelInfo = File.ReadAllText(
                ServerPaths.ChannelInfoFilePath,
                Encoding.UTF8);
            Check(
                "channel script defines 2014 JP free-duel CH.68 as type 13",
                Regex.IsMatch(
                    channelInfo,
                    @"(?m)^\s*68\s+`<4::channel_info_cname_651>`\s+13\s+"),
                ref failures);
            Check(
                "channel script cache version advances to 66",
                new ChannelProtocolHandler().ScriptVersion == "66",
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "FreeDuelChannelSelfTest OK"
                    : $"FreeDuelChannelSelfTest FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static string ReadFixedAscii(
            byte[] bytes,
            int offset,
            int count)
        {
            return Encoding.ASCII
                .GetString(bytes, offset, count)
                .TrimEnd('\0');
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }

            failures++;
            Console.WriteLine($"[FAIL] {name}");
        }
    }
}
