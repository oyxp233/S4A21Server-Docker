using DfoServer.Game.Settings;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    public static class A21StartupProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_STARTUP_PROTOCOL selftest ===");
            var failures = 0;

            Check(
                "A21 cmd/noti table size is 1246",
                GameNetworkConfig.CommandPacketCount == 1246
                && GameNetworkConfig.NotificationPacketCount == 1246,
                ref failures);
            Check(
                "A21 loopback advertisement avoids literal 127.0.0.1",
                GameNetworkConfig.AdvertisedGameIp == "127.0.0.2",
                ref failures);

            IPacketHeader header = new GamePacketHeader
            {
                cmd = 1,
                type = 0x04DD,
                length = 14,
                checksum = 0x11223344,
                seq = 7,
                extra = 0x5A
            };
            var headerBytes = header.GetBytes();
            Check(
                "A21 game receive header is 14B",
                header.GetHeaderSize() == 14 && headerBytes.Length == 14,
                ref failures);

            var parsed = new FlexiblePacket(
                new GamePacketHeader
                {
                    cmd = 1,
                    type = 0x04DD,
                    length = 14,
                    checksum = 0x11223344,
                    seq = 7,
                    extra = 0x5A
                }).GetHeader<GamePacketHeader>();
            Check(
                "A21 header is the only game dispatch header",
                parsed.cmd == 1
                && parsed.type == 0x04DD
                && parsed.length == 14
                && parsed.checksum == 0x11223344
                && parsed.seq == 7
                && parsed.extra == 0x5A,
                ref failures);

            var initial = LoginPacketBuilder.BuildInitialLoginNotice();
            Check(
                "A21 initial notice advertises A21 table sizes",
                initial.Length >= 12
                && BitConverter.ToInt32(initial, initial.Length - 12) == 1246
                && BitConverter.ToInt32(initial, initial.Length - 8) == 1246
                && BitConverter.ToInt32(initial, initial.Length - 4) == 0,
                ref failures);
            Check(
                "A21 initial notice advertises 127.0.0.2",
                Encoding.ASCII.GetString(initial).Contains("127.0.0.2"),
                ref failures);

            var loginSuccess = LoginPacketBuilder.BuildLoginSuccess();
            Check(
                "A21 login success second byte is 20",
                loginSuccess.Length > 2 && loginSuccess[1] == 20,
                ref failures);
            Check(
                "A21 login success uses same advertised address",
                Encoding.ASCII.GetString(loginSuccess).Contains("127.0.0.2"),
                ref failures);

            var hiddenAvatar = Copy(AccountSettings.DefaultMainGameOption);
            var fullAvatarOffset = AccountSettings.FullAvatarOptionIndex * 2;
            hiddenAvatar[fullAvatarOffset] = 0;
            hiddenAvatar[fullAvatarOffset + 1] = 0;
            var option = AccountSettingsPacketBuilder.BuildSelectScreenGameOption(
                new AccountSettings { MainGameOption = hiddenAvatar },
                out var persistedMain);
            var mainLength = hiddenAvatar.Length;
            Check(
                "A21 select-screen 00AD keeps three length-prefixed banks",
                option.Length == mainLength + 12
                && BitConverter.ToInt32(option, 0) == mainLength
                && BitConverter.ToInt32(option, 4 + mainLength) == 0
                && BitConverter.ToInt32(option, 8 + mainLength) == 0,
                ref failures);
            Check(
                "A21 select-screen forces FullAvatar visible",
                persistedMain != null
                && persistedMain[fullAvatarOffset] == 1
                && persistedMain[fullAvatarOffset + 1] == 0,
                ref failures);

            var channelHandler = new ChannelProtocolHandler();
            var channelList = channelHandler.BuildChannelListPlaintext(
                new List<ChannelProtocolHandler.ServerInfo>
                {
                    new ChannelProtocolHandler.ServerInfo
                    {
                        ChannelId = 11,
                        ChannelName = "ch.11",
                        MaxUserNum = 500,
                        Port = 10011
                    }
                });
            Check(
                "A21 ASK plaintext starts with group 1 and count 1",
                channelList.Length >= 6
                && BitConverter.ToUInt16(channelList, 0) == 1
                && BitConverter.ToInt32(channelList, 2) == 1,
                ref failures);
            Check(
                "A21 ASK uses the same advertised address",
                Encoding.ASCII.GetString(channelList).Contains("127.0.0.2"),
                ref failures);

            const string etc = @"
[dungeon]
`[granfloris]` `格兰之森` 3 4
[/dungeon]
[server]
1 11 `格兰之森` 0 `[granfloris]` 5 0 0 0 0 0 0 0 0 0 0
[/server]
[server]
2 12 `格兰之森` 0 `[granfloris]` 5 0 0 0 0 0 0 0 0 0 0
[/server]";
            var script = Encoding.UTF8.GetString(
                ChannelProtocolHandler.BuildGetScriptPlaintext(etc));
            Check(
                "A21 channel script keeps only server group 1",
                script.Contains("[server]\n1\n11 ")
                && !script.Contains("[server]\n2\n"),
                ref failures);

            var userInfo0 = UserInfoSubtype0Builder.BuildNotificationBody(
                new CharacterRecord
                {
                    CharacterId = 7,
                    Name = new byte[] { (byte)'a' },
                });
            var userInfo0PrefixValid = userInfo0.Length >= 41;
            if (userInfo0PrefixValid)
            {
                for (var i = 3; i < 41; i++)
                {
                    if (userInfo0[i] != 0)
                    {
                        userInfo0PrefixValid = false;
                        break;
                    }
                }
            }
            Check(
                "A21 USERINFO0 reserves the fixed 38-byte header",
                userInfo0PrefixValid
                && BitConverter.ToUInt16(userInfo0, 41) == 7,
                ref failures);

            var userInfo1 = UserInfoSubtype1Builder.BuildFromSnapshot(
                new UserInfoAdditionSnapshot(),
                null);
            Check(
                "A21 USERINFO1 uses the 88-byte stat block and fixed dimension tail",
                userInfo1.Length == 301
                && BitConverter.ToInt32(userInfo1, 4) == 88,
                ref failures);

            var roster = AccountCharacterListBodyBuilder.Build(
                new[]
                {
                    new CharacterRecord
                    {
                        CharacterId = 0,
                        SlotIndex = 0,
                        Name = new byte[] { (byte)'a' },
                        Job = 1,
                        GrowType = 2,
                        Level = 1,
                    },
                },
                new GetUserInfoTemplate
                {
                    GateOrCount1 = 32,
                    GateOrCount2 = 32,
                },
                out _,
                accountId: 0);
            Check(
                "A21 type=2 roster uses a zero-based slot and explicit count",
                roster.Length >= 20
                && roster[0] == 2
                && BitConverter.ToUInt16(roster, 16) == 1
                && BitConverter.ToUInt16(roster, 18) == 0,
                ref failures);

            var singleRecordLength = roster.Length - 18;
            var twoCharacterRoster = AccountCharacterListBodyBuilder.Build(
                new[]
                {
                    new CharacterRecord
                    {
                        CharacterId = 0,
                        SlotIndex = 0,
                        Name = new byte[] { (byte)'a' },
                        Job = 1,
                        GrowType = 2,
                        Level = 1,
                    },
                    new CharacterRecord
                    {
                        CharacterId = 0,
                        SlotIndex = 1,
                        Name = new byte[] { (byte)'b' },
                        Job = 2,
                        GrowType = 3,
                        Level = 1,
                    },
                },
                new GetUserInfoTemplate
                {
                    GateOrCount1 = 32,
                    GateOrCount2 = 32,
                },
                out _,
                accountId: 0);
            Check(
                "A21 type=2 keeps adjacent zero-based roster slots distinct",
                BitConverter.ToUInt16(twoCharacterRoster, 16) == 2
                && BitConverter.ToUInt16(twoCharacterRoster, 18) == 0
                && BitConverter.ToUInt16(twoCharacterRoster, 18 + singleRecordLength) == 1,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_STARTUP_PROTOCOL selftest passed."
                    : $"A21_STARTUP_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] Copy(byte[] source)
        {
            var result = new byte[source.Length];
            Buffer.BlockCopy(source, 0, result, 0, source.Length);
            return result;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
