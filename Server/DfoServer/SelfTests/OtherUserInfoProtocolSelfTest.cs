using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.TitleBook;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    public static class OtherUserInfoProtocolSelfTest
    {
        private const int AccountId = 77;
        private const int CharacterId = 4202;
        private const ushort UserId = 4202;

        public static int Run()
        {
            var failures = 0;
            VerifyBuilderModes(ref failures);
            VerifyAuthorization(ref failures);
            VerifyHandlerFlows(ref failures);
            VerifySelfTargetFlows(ref failures);

            Console.WriteLine(
                $"=== OTHER_USER_INFO_PROTOCOL result: " +
                $"failures={failures} ===");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyBuilderModes(ref int failures)
        {
            var snapshot = BuildSnapshot();
            var source = new FakeSelectCharacterDataSource(snapshot);
            var repository = new FakeCharacterRepository(
                snapshot.CharacterRecord);
            var target = CreateTarget(GameNetworkConfig.NormalGamePort);

            try
            {
                var ordinary = OtherUserInfoResponseBuilder.Build(
                    source,
                    repository,
                    target,
                    mode: 0,
                    routingByte7: 0x5A,
                    out var ordinaryError);
                Check(
                    "mode 0 remains one target subtype 0 packet",
                    ordinary.Count == 1
                    && IsEnvelope(ordinary[0], 0x0002, 0x5A)
                    && ordinary[0][15] == 0
                    && ordinaryError == null
                    && source.LoadCalls == 0,
                    ref failures);

                source.Reset();
                var mode1 = OtherUserInfoResponseBuilder.Build(
                    source,
                    repository,
                    target,
                    mode: 1,
                    routingByte7: 0x5A,
                    out var mode1Error);
                Check(
                    "mode 1 returns a real target-relative subtype 1",
                    mode1.Count == 1
                    && IsEnvelope(mode1[0], 0x0002, 0x5A)
                    && mode1[0][15] == 1
                    && BitConverter.ToUInt16(mode1[0], 16) == 1
                    && BitConverter.ToUInt16(mode1[0], 18) == UserId
                    && mode1Error == null
                    && source.LoadCalls == 1,
                    ref failures);

                source.Reset();
                var mode3 = OtherUserInfoResponseBuilder.Build(
                    source,
                    repository,
                    target,
                    mode: 3,
                    routingByte7: 0x5A,
                    out var mode3Error);
                var titleBooksValid = mode3.Count == 5;
                for (var category = 0;
                     category < 4 && titleBooksValid;
                     category++)
                {
                    var packet = mode3[category];
                    titleBooksValid =
                        IsEnvelope(packet, 0x0166, 0)
                        && packet[15] == 2
                        && BitConverter.ToUInt16(packet, 16) == UserId
                        && BitConverter.ToInt32(packet, 18) == category;
                }

                Check(
                    "mode 3 sends title-book categories 0..3 before subtype 3",
                    titleBooksValid
                    && BitConverter.ToInt32(mode3[0], 22) == 1
                    && IsEnvelope(mode3[4], 0x0002, 0x5A)
                    && mode3[4][15] == 3
                    && BitConverter.ToUInt16(mode3[4], 16) == 1
                    && BitConverter.ToUInt16(mode3[4], 18) == UserId
                    && mode3Error == null
                    && source.LoadCalls == 1,
                    ref failures);

                source.Reset();
                var unsupported = OtherUserInfoResponseBuilder.Build(
                    source,
                    repository,
                    target,
                    mode: 4,
                    routingByte7: 0x5A,
                    out var unsupportedError);
                Check(
                    "unknown inspect modes fail closed",
                    unsupported.Count == 0
                    && unsupportedError == "unsupported_mode"
                    && source.LoadCalls == 0,
                    ref failures);

                var originalCategory =
                    snapshot.InitializationSnapshot.TitleBookCategories[0];
                Check(
                    "inspect projection does not mutate cached title-book metadata",
                    originalCategory.InfoType == 9
                    && originalCategory.OwnerId16 == 999,
                    ref failures);
            }
            finally
            {
                target.Close();
            }
        }

        private static void VerifyAuthorization(ref int failures)
        {
            var snapshot = BuildSnapshot();
            var source = new FakeSelectCharacterDataSource(snapshot);
            var repository = new FakeCharacterRepository(
                snapshot.CharacterRecord);
            var target = CreateTarget(GameNetworkConfig.NormalGamePort);

            try
            {
                target.Account = null;
                var missingAccount = OtherUserInfoResponseBuilder.Build(
                    source,
                    repository,
                    target,
                    mode: 3,
                    routingByte7: 1,
                    out var missingAccountError);
                Check(
                    "details reject an unauthenticated target before loading",
                    missingAccount.Count == 0
                    && missingAccountError == "target_account_unavailable"
                    && source.LoadCalls == 0,
                    ref failures);

                target.Account = new AccountRecord { AccountId = AccountId };
                source.Reset();
                var wrongOwner = new FakeCharacterRepository(
                    new CharacterRecord
                    {
                        CharacterId = CharacterId,
                        AccountId = AccountId + 1,
                    });
                var unauthorized = OtherUserInfoResponseBuilder.Build(
                    source,
                    wrongOwner,
                    target,
                    mode: 3,
                    routingByte7: 1,
                    out var unauthorizedError);
                Check(
                    "details verify character ownership before writable load",
                    unauthorized.Count == 0
                    && unauthorizedError == "target_identity_mismatch"
                    && source.LoadCalls == 0,
                    ref failures);

                source.Reset();
                var mismatchedSnapshot = BuildSnapshot();
                mismatchedSnapshot.CharacterRecord.AccountId = AccountId + 1;
                source.LoadResult = mismatchedSnapshot;
                var mismatched = OtherUserInfoResponseBuilder.Build(
                    source,
                    repository,
                    target,
                    mode: 3,
                    routingByte7: 1,
                    out var mismatchError);
                Check(
                    "details reject a snapshot for another account",
                    mismatched.Count == 0
                    && mismatchError ==
                        "target_snapshot_identity_mismatch"
                    && source.LoadCalls == 1,
                    ref failures);

                source.Reset();
                source.LoadResult = snapshot;
                source.OnLoad = () =>
                {
                    target.Player.CharacterId = CharacterId + 1;
                    target.Player.UserId =
                        unchecked((ushort)(CharacterId + 1));
                };
                var changedGeneration = OtherUserInfoResponseBuilder.Build(
                    source,
                    repository,
                    target,
                    mode: 3,
                    routingByte7: 1,
                    out var changedGenerationError);
                Check(
                    "snapshot rejects a target that changes character while loading",
                    changedGeneration.Count == 0
                    && changedGenerationError == "target_generation_changed"
                    && source.LoadCalls == 1,
                    ref failures);
                target.Player.CharacterId = CharacterId;
                target.Player.UserId = UserId;

                source.Reset();
                source.LoadResult = snapshot;
                source.ThrowOnLoad = true;
                var failed = OtherUserInfoResponseBuilder.Build(
                    source,
                    repository,
                    target,
                    mode: 3,
                    routingByte7: 1,
                    out var failedError);
                Check(
                    "snapshot failure emits no partial title-book packets",
                    failed.Count == 0
                    && failedError == "target_snapshot_failed"
                    && source.LoadCalls == 1,
                    ref failures);
            }
            finally
            {
                target.Close();
            }
        }

        private static void VerifyHandlerFlows(ref int failures)
        {
            const int RequesterCharacterId = 4201;
            var snapshot = BuildSnapshot();
            var source = new FakeSelectCharacterDataSource(snapshot);
            var repository = new FakeCharacterRepository(
                snapshot.CharacterRecord);
            var directory = new SessionDirectory();
            var target = CreateTarget(GameNetworkConfig.NormalGamePort);
            directory.Register(CharacterId, target);
            var handler = new CharacterSelectHandler(
                source,
                repository,
                null,
                directory);
            var body = new byte[3];
            Buffer.BlockCopy(
                BitConverter.GetBytes(UserId),
                0,
                body,
                0,
                2);
            body[2] = 3;

            try
            {
                using (var unauthenticated = ConnectedSession.Create(
                    GameNetworkConfig.NormalGamePort))
                {
                    unauthenticated.Session.Player.CharacterId =
                        RequesterCharacterId;
                    unauthenticated.Session.Player.UserId =
                        (ushort)RequesterCharacterId;
                    handler.Handle_ENUM_CMDPACKET_GET_USERINFO(
                            unauthenticated.Session,
                            new GamePacketHeader(),
                            Array.Empty<byte>())
                        .GetAwaiter()
                        .GetResult();
                    body[2] = 2;
                    handler.Handle_ENUM_CMDPACKET_GET_USERINFO(
                            unauthenticated.Session,
                            new GamePacketHeader(),
                            body)
                        .GetAwaiter()
                        .GetResult();
                    body[2] = 3;
                    handler.Handle_ENUM_CMDPACKET_GET_USERINFO(
                            unauthenticated.Session,
                            new GamePacketHeader(),
                            body)
                        .GetAwaiter()
                        .GetResult();
                    handler.Handle_ENUM_CMDPACKET_OTHER_USER_TITLE_BOOK_LIST(
                            unauthenticated.Session,
                            new GamePacketHeader(),
                            BitConverter.GetBytes(UserId))
                        .GetAwaiter()
                        .GetResult();
                    Check(
                        "roster and inspect handlers reject an " +
                        "unauthenticated requester",
                        !unauthenticated.HasPendingPacket(100)
                        && source.LoadCalls == 0,
                        ref failures);
                }

                using (var requester = ConnectedSession.Create(
                    GameNetworkConfig.NormalGamePort))
                {
                    requester.Session.Account =
                        new AccountRecord { AccountId = AccountId + 1 };
                    requester.Session.Player.CharacterId =
                        RequesterCharacterId;
                    requester.Session.Player.UserId =
                        (ushort)RequesterCharacterId;
                    directory.Register(
                        RequesterCharacterId,
                        requester.Session);
                    try
                    {
                        handler.Handle_ENUM_CMDPACKET_GET_USERINFO(
                                requester.Session,
                                new GamePacketHeader(),
                                body)
                            .GetAwaiter()
                            .GetResult();

                        var sequenceValid = true;
                        for (var category = 0; category < 4; category++)
                        {
                            var packet = requester.ReadPacket();
                            sequenceValid = sequenceValid
                                && packet.Command == 0
                                && packet.Type == 0x0166
                                && packet.Body[0] == 2
                                && BitConverter.ToUInt16(
                                    packet.Body, 1) == UserId
                                && BitConverter.ToInt32(
                                    packet.Body, 3) == category;
                        }
                        var details = requester.ReadPacket();
                        sequenceValid = sequenceValid
                            && details.Command == 0
                            && details.Type == 0x0002
                            && details.Body[0] == 3;
                        Check(
                            "GET_USERINFO mode 3 handler preserves packet order",
                            sequenceValid
                            && !requester.HasPendingPacket(100),
                            ref failures);

                        handler
                            .Handle_ENUM_CMDPACKET_OTHER_USER_TITLE_BOOK_LIST(
                                requester.Session,
                                new GamePacketHeader(),
                                BitConverter.GetBytes(UserId))
                            .GetAwaiter()
                            .GetResult();

                        var titleCommandValid = true;
                        for (var category = 0; category < 4; category++)
                        {
                            var packet = requester.ReadPacket();
                            titleCommandValid = titleCommandValid
                                && packet.Command == 0
                                && packet.Type == 0x0166
                                && packet.Body[0] == 1
                                && BitConverter.ToUInt16(
                                    packet.Body, 1) == UserId
                                && BitConverter.ToInt32(
                                    packet.Body, 3) == category;
                        }
                        Check(
                            "0x01A8 returns four target title-book categories",
                            titleCommandValid
                            && !requester.HasPendingPacket(100),
                            ref failures);

                        var replacementRequester =
                            new EnhancedClientSession(
                                new TcpClient(),
                                new GamePacketHeader(),
                                GameNetworkConfig.NormalGamePort)
                            {
                                Account = new AccountRecord
                                {
                                    AccountId = AccountId + 1,
                                },
                            };
                        replacementRequester.Player.CharacterId =
                            RequesterCharacterId;
                        replacementRequester.Player.UserId =
                            (ushort)RequesterCharacterId;
                        try
                        {
                            source.Reset();
                            source.OnLoad = () => directory.Register(
                                RequesterCharacterId,
                                replacementRequester);
                            handler.Handle_ENUM_CMDPACKET_GET_USERINFO(
                                    requester.Session,
                                    new GamePacketHeader(),
                                    body)
                                .GetAwaiter()
                                .GetResult();
                            Check(
                                "inspect aborts if requester is replaced " +
                                "during snapshot load",
                                !requester.HasPendingPacket(100)
                                && source.LoadCalls == 1,
                                ref failures);
                        }
                        finally
                        {
                            source.Reset();
                            directory.Register(
                                RequesterCharacterId,
                                requester.Session);
                            replacementRequester.Close();
                        }

                        source.Reset();
                        body[2] = 4;
                        handler.Handle_ENUM_CMDPACKET_GET_USERINFO(
                                requester.Session,
                                new GamePacketHeader(),
                                body)
                            .GetAwaiter()
                            .GetResult();
                        Check(
                            "GET_USERINFO rejects an unknown mode",
                            !requester.HasPendingPacket(100)
                            && source.LoadCalls == 0,
                            ref failures);
                        body[2] = 3;

                        source.Reset();
                        Buffer.BlockCopy(
                            BitConverter.GetBytes(ushort.MaxValue),
                            0,
                            body,
                            0,
                            2);
                        handler.Handle_ENUM_CMDPACKET_GET_USERINFO(
                                requester.Session,
                                new GamePacketHeader(),
                                body)
                            .GetAwaiter()
                            .GetResult();
                        Check(
                            "invalid inspect uid never falls back to roster",
                            !requester.HasPendingPacket(100)
                            && source.LoadCalls == 0,
                            ref failures);
                        Buffer.BlockCopy(
                            BitConverter.GetBytes(UserId),
                            0,
                            body,
                            0,
                            2);

                        var collision = CreateTarget(
                            GameNetworkConfig.NormalGamePort);
                        collision.Account =
                            new AccountRecord { AccountId = AccountId + 2 };
                        collision.Player.CharacterId =
                            CharacterId + 65536;
                        collision.Player.UserId = UserId;
                        directory.Register(
                            collision.Player.CharacterId,
                            collision);
                        try
                        {
                            source.Reset();
                            handler.Handle_ENUM_CMDPACKET_GET_USERINFO(
                                    requester.Session,
                                    new GamePacketHeader(),
                                    body)
                                .GetAwaiter()
                                .GetResult();
                            Check(
                                "16-bit uid collisions fail closed",
                                !requester.HasPendingPacket(100)
                                && source.LoadCalls == 0,
                                ref failures);
                        }
                        finally
                        {
                            directory.UnregisterAsync(
                                    collision.Player.CharacterId,
                                    collision)
                                .GetAwaiter()
                                .GetResult();
                            collision.Close();
                        }
                    }
                    finally
                    {
                        directory.UnregisterAsync(
                                RequesterCharacterId,
                                requester.Session)
                            .GetAwaiter()
                            .GetResult();
                    }
                }

                var crossDirectory = new SessionDirectory();
                var crossTarget = CreateTarget(
                    GameNetworkConfig.NormalGamePort + 1);
                crossDirectory.Register(CharacterId, crossTarget);
                var crossSource = new FakeSelectCharacterDataSource(snapshot);
                var crossHandler = new CharacterSelectHandler(
                    crossSource,
                    repository,
                    null,
                    crossDirectory);
                try
                {
                    using (var requester = ConnectedSession.Create(
                        GameNetworkConfig.NormalGamePort))
                    {
                        requester.Session.Account =
                            new AccountRecord { AccountId = AccountId + 1 };
                        requester.Session.Player.CharacterId =
                            RequesterCharacterId;
                        requester.Session.Player.UserId =
                            (ushort)RequesterCharacterId;
                        crossDirectory.Register(
                            RequesterCharacterId,
                            requester.Session);
                        try
                        {
                            crossHandler
                                .Handle_ENUM_CMDPACKET_OTHER_USER_TITLE_BOOK_LIST(
                                    requester.Session,
                                    new GamePacketHeader(),
                                    BitConverter.GetBytes(UserId))
                                .GetAwaiter()
                                .GetResult();
                            Check(
                                "0x01A8 does not expose a cross-channel target",
                                !requester.HasPendingPacket(100)
                                && crossSource.LoadCalls == 0,
                                ref failures);
                        }
                        finally
                        {
                            crossDirectory.UnregisterAsync(
                                    RequesterCharacterId,
                                    requester.Session)
                                .GetAwaiter()
                                .GetResult();
                        }
                    }
                }
                finally
                {
                    crossTarget.Close();
                }
            }
            finally
            {
                target.Close();
            }
        }

        private static void VerifySelfTargetFlows(ref int failures)
        {
            var snapshot = BuildSnapshot();
            var source = new FakeSelectCharacterDataSource(snapshot);
            var repository = new FakeCharacterRepository(
                snapshot.CharacterRecord);
            var directory = new SessionDirectory();
            var handler = new CharacterSelectHandler(
                source,
                repository,
                null,
                directory);

            using (var requester = ConnectedSession.Create(
                GameNetworkConfig.NormalGamePort))
            {
                requester.Session.Account =
                    new AccountRecord { AccountId = AccountId };
                requester.Session.Player.CharacterId = CharacterId;
                requester.Session.Player.UserId = UserId;
                requester.Session.Player.Level = 50;
                requester.Session.Player.Name =
                    new byte[] { (byte)'s' };
                directory.Register(CharacterId, requester.Session);

                try
                {
                    foreach (var mode in new byte[] { 0, 1, 3 })
                    {
                        var body = new byte[3];
                        Buffer.BlockCopy(
                            BitConverter.GetBytes(UserId),
                            0,
                            body,
                            0,
                            2);
                        body[2] = mode;
                        handler.Handle_ENUM_CMDPACKET_GET_USERINFO(
                                requester.Session,
                                new GamePacketHeader(),
                                body)
                            .GetAwaiter()
                            .GetResult();

                        var prefixValid = true;
                        if (mode == 3)
                        {
                            for (var category = 0;
                                 category < 4;
                                 category++)
                            {
                                var titleBook = requester.ReadPacket();
                                prefixValid = prefixValid
                                    && titleBook.Command == 0
                                    && titleBook.Type == 0x0166
                                    && titleBook.Body[0] == 2
                                    && BitConverter.ToUInt16(
                                        titleBook.Body, 1) == UserId
                                    && BitConverter.ToInt32(
                                        titleBook.Body, 3) == category;
                            }
                        }

                        var userInfo = requester.ReadPacket();
                        Check(
                            $"GET_USERINFO mode {mode} accepts self uid",
                            prefixValid
                            && userInfo.Command == 0
                            && userInfo.Type == 0x0002
                            && userInfo.Body[0] == mode
                            && BitConverter.ToUInt16(
                                userInfo.Body, 3) == UserId
                            && !requester.HasPendingPacket(100),
                            ref failures);
                    }

                    handler
                        .Handle_ENUM_CMDPACKET_OTHER_USER_TITLE_BOOK_LIST(
                            requester.Session,
                            new GamePacketHeader(),
                            BitConverter.GetBytes(UserId))
                        .GetAwaiter()
                        .GetResult();

                    var titleCommandValid = true;
                    for (var category = 0; category < 4; category++)
                    {
                        var packet = requester.ReadPacket();
                        titleCommandValid = titleCommandValid
                            && packet.Command == 0
                            && packet.Type == 0x0166
                            && packet.Body[0] == 1
                            && BitConverter.ToUInt16(
                                packet.Body, 1) == UserId
                            && BitConverter.ToInt32(
                                packet.Body, 3) == category;
                    }
                    Check(
                        "0x01A8 accepts self uid",
                        titleCommandValid
                        && !requester.HasPendingPacket(100),
                        ref failures);
                }
                finally
                {
                    directory
                        .UnregisterAsync(CharacterId, requester.Session)
                        .GetAwaiter()
                        .GetResult();
                }
            }
        }

        private static SelectCharacterDataSnapshot BuildSnapshot()
        {
            var initialization =
                new SelectCharacterInitializationSnapshot
                {
                    UserInfoAddition = new UserInfoAdditionSnapshot
                    {
                        CharacExp = 12345,
                        StatHpMax = 5000,
                        StatMpMax = 3000,
                        StatLevel = 50,
                        SkillTreeIndex = 1,
                        CloneTitleItemId = 0x11223344,
                        NameTagItemId = 0x55667788,
                    },
                    SkillInfo = new SkillInfoSnapshot(),
                };
            var category0 = new TitleBookCategorySnapshot
            {
                InfoType = 9,
                OwnerId16 = 999,
                Category = 0,
            };
            category0.Entries.Add(new TitleBookListEntrySnapshot
            {
                SlotIndex = 3,
                ItemId = 123456,
                Durability = 9,
            });
            initialization.TitleBookCategories.Add(category0);

            // Selection initialization supports a fifth event category, while
            // the native inspect flow consumes only original categories 0..3.
            initialization.TitleBookCategories.Add(
                new TitleBookCategorySnapshot
                {
                    InfoType = 9,
                    OwnerId16 = 999,
                    Category = 4,
                });

            return new SelectCharacterDataSnapshot
            {
                CharacterRecord = new CharacterRecord
                {
                    CharacterId = CharacterId,
                    AccountId = AccountId,
                    Name = new byte[] { (byte)'t' },
                    Level = 50,
                    Subtype0Tail = new UserInfoMinimumTailSnapshot(),
                },
                InitializationSnapshot = initialization,
            };
        }

        private static EnhancedClientSession CreateTarget(int listenerPort)
        {
            var target = new EnhancedClientSession(
                new TcpClient(),
                new GamePacketHeader(),
                listenerPort)
            {
                Account = new AccountRecord { AccountId = AccountId },
            };
            target.Player.CharacterId = CharacterId;
            target.Player.UserId = UserId;
            target.Player.Level = 50;
            target.Player.Name = new byte[] { (byte)'t' };
            return target;
        }

        private static bool IsEnvelope(
            byte[] packet,
            ushort type,
            byte routingByte)
        {
            return packet != null
                && packet.Length >= 16
                && packet[0] == 0
                && BitConverter.ToUInt16(packet, 1) == type
                && BitConverter.ToInt32(packet, 3) == packet.Length
                && packet[7] == routingByte;
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine((condition ? "[OK] " : "[FAIL] ") + name);
            if (!condition)
                failures++;
        }

        private sealed class FakeSelectCharacterDataSource
            : ISelectCharacterDataSource
        {
            internal FakeSelectCharacterDataSource(
                SelectCharacterDataSnapshot loadResult)
            {
                LoadResult = loadResult;
            }

            internal SelectCharacterDataSnapshot LoadResult { get; set; }

            internal int LoadCalls { get; private set; }

            internal bool ThrowOnLoad { get; set; }

            internal Action OnLoad { get; set; }

            public SelectCharacterDataSnapshot Load(
                int characterId,
                int accountId)
            {
                LoadCalls++;
                OnLoad?.Invoke();
                if (ThrowOnLoad)
                    throw new InvalidOperationException("simulated snapshot failure");
                return LoadResult;
            }

            public int GetSeedCharacterId() => CharacterId;

            public void InitializeNewCharacter(
                int characterId,
                int accountId,
                byte job)
            {
            }

            internal void Reset()
            {
                LoadCalls = 0;
                ThrowOnLoad = false;
                OnLoad = null;
            }
        }

        private sealed class FakeCharacterRepository
            : ICharacterRepository
        {
            private readonly CharacterRecord _record;

            internal FakeCharacterRepository(CharacterRecord record)
            {
                _record = record;
            }

            public CharacterRecord GetById(int characterId) =>
                _record != null && _record.CharacterId == characterId
                    ? _record
                    : null;

            public IReadOnlyList<CharacterRecord> ListByAccount(
                int accountId) => Array.Empty<CharacterRecord>();

            public int Create(CharacterRecord record) =>
                throw new NotSupportedException();

            public void UpdatePosition(
                int characterId,
                byte townId,
                byte areaId,
                short posX,
                short posY,
                byte direction,
                byte areaState) => throw new NotSupportedException();

            public void UpdateSeedFields(
                int characterId,
                byte[] name,
                byte job,
                byte growType,
                byte level,
                byte pvpGrade,
                byte pvpRatingGrade,
                byte userState,
                CharacterAppearanceEntry[] appearance,
                DateTime? createdAt = null) =>
                throw new NotSupportedException();

            public void UpdateAppearance(
                int characterId,
                CharacterAppearanceEntry[] appearance) =>
                throw new NotSupportedException();

            public void SoftDelete(int characterId) =>
                throw new NotSupportedException();

            public CharacterRecord GetByName(string name) => null;

            public CharacterRecord GetByNameIncludingDeleted(
                string name) => null;

            public int CountByAccount(int accountId) => 0;

            public void SwapSlotIndexes(
                int accountId,
                byte slotA,
                byte slotB) => throw new NotSupportedException();
        }

        private sealed class ConnectedSession : IDisposable
        {
            private const int HeaderLength = 15;
            private readonly TcpClient _peer;

            private ConnectedSession(
                EnhancedClientSession session,
                TcpClient peer)
            {
                Session = session;
                _peer = peer;
                _peer.ReceiveTimeout = 2000;
            }

            internal EnhancedClientSession Session { get; }

            internal static ConnectedSession Create(int listenerPort)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var sender = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                sender.Connect(endpoint.Address, endpoint.Port);
                var peer = accept.GetAwaiter().GetResult();
                listener.Stop();
                return new ConnectedSession(
                    new EnhancedClientSession(
                        sender,
                        new GamePacketHeader(),
                        listenerPort),
                    peer);
            }

            internal CapturedPacket ReadPacket()
            {
                var header = ReadExact(HeaderLength);
                var packetLength = BitConverter.ToInt32(header, 3);
                if (packetLength < HeaderLength)
                {
                    throw new InvalidOperationException(
                        $"invalid packet length {packetLength}");
                }

                return new CapturedPacket(
                    header[0],
                    BitConverter.ToUInt16(header, 1),
                    ReadExact(packetLength - HeaderLength));
            }

            internal bool HasPendingPacket(int timeoutMilliseconds)
            {
                return _peer.Client.Poll(
                        timeoutMilliseconds * 1000,
                        SelectMode.SelectRead)
                    && _peer.Available > 0;
            }

            public void Dispose()
            {
                Session.Close();
                _peer.Dispose();
            }

            private byte[] ReadExact(int count)
            {
                var result = new byte[count];
                var offset = 0;
                var stream = _peer.GetStream();
                while (offset < count)
                {
                    var read = stream.Read(
                        result,
                        offset,
                        count - offset);
                    if (read <= 0)
                    {
                        throw new InvalidOperationException(
                            "connection closed before packet completed");
                    }
                    offset += read;
                }
                return result;
            }
        }

        private readonly struct CapturedPacket
        {
            internal CapturedPacket(
                byte command,
                ushort type,
                byte[] body)
            {
                Command = command;
                Type = type;
                Body = body;
            }

            internal byte Command { get; }

            internal ushort Type { get; }

            internal byte[] Body { get; }
        }
    }
}
