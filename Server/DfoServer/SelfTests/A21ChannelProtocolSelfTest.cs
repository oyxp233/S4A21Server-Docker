using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DfoServer.Network;

namespace DfoServer.SelfTests
{
    public static class A21ChannelProtocolSelfTest
    {
        private const string Key = "20260815000006";
        private const int HeaderSize = 11;
        private const int ChannelBlockSize = 48;

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
            Check(
                "A21 channel plaintext has 6B prefix and 48B blocks",
                plaintext.Length == 6 + ChannelBlockSize * channels.Count
                && BitConverter.ToUInt16(plaintext, 0) == 1
                && BitConverter.ToInt32(plaintext, 2) == channels.Count,
                ref failures);
            Check(
                "A21 channel block fields keep name/max/ip/port offsets",
                ReadFixedUtf8(plaintext, 6, 20) == "ch.11"
                && BitConverter.ToInt32(plaintext, 26) == 500
                && BitConverter.ToInt32(plaintext, 30) == 0
                && ReadFixedUtf8(plaintext, 34, 16)
                    == GameNetworkConfig.AdvertisedGameIp
                && BitConverter.ToInt32(plaintext, 50) == 10011
                && ReadFixedUtf8(plaintext, 6 + ChannelBlockSize, 20)
                    == "ch.100"
                && BitConverter.ToInt32(
                    plaintext,
                    6 + ChannelBlockSize + 44)
                    == 10161,
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

            Console.WriteLine(
                failures == 0
                    ? "A21_CHANNEL_PROTOCOL selftest passed."
                    : $"A21_CHANNEL_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static string ReadFixedUtf8(
            byte[] bytes,
            int offset,
            int count)
        {
            return Encoding.UTF8
                .GetString(bytes, offset, count)
                .TrimEnd('\0');
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
