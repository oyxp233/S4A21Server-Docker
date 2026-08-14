using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // 翻牌奖励幂等自测:
    // 自动翻、手动翻、EPLP/再次挑战会竞争同一局 CardRewards, 每一段奖励只能发一次。
    public static class CardRewardFlowSelfTest
    {
        private const int AccountId = 970016;
        private const int CharacterId = 970116;
        private const int PaidRewardItemId = 1004;

        public static int Run()
        {
            Console.WriteLine("=== CARD_REWARD_FLOW selftest ===");
            var failures = 0;

            var tempDb = Path.Combine(Path.GetTempPath(), "card-reward-flow.db");
            DeleteTempDatabase(tempDb);
            var previousDatabasePath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");
            Environment.SetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH",
                tempDb);
            try
            {
            var connStr = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
            var database = GameDatabase.AttachInitialized(connStr);
            Seed(connStr);
            VerifyClearRewardEtc(ref failures);
            VerifyConfiguredRewardGeneration(ref failures);
            VerifyPaidCardProtocolFields(ref failures);
            VerifySharedKillStatistics(ref failures);
            VerifyLayoutPacketSequence(ref failures);
            VerifyProjectionSerialization(ref failures);
            var service = new CardRewardCoordinator();

            using (var peer = ConnectedSession.Create())
            {
                var session = peer.Session;
                session.Player.CharacterId = CharacterId;
                session.Account = new AccountRecord { AccountId = AccountId };
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        CharacterId,
                        AccountId,
                        database);
                }
                InventoryContext.Register(session.SessionId, CharacterId, inventory);

                try
                {
                    var run = BuildRun(freeGold: 10, paidCardCost: 20);
                    session.Player.CurrentRun = run;

                    Check("paid-card commission is independent from reward entries",
                        run.PaidCardCost == 20
                        && run.CardRewards[4].IsGold
                        && run.CardRewards[4].GoldAmount == 0
                        && run.CardRewards[5].ItemId == PaidRewardItemId,
                        ref failures);

                    service.HandleSelectCard(session, new byte[] { 0, 0 }).GetAwaiter().GetResult();
                    CheckGold("free card grants once", inventory, 10, ref failures);
                    CheckGoldUpdatePacket("free card sends gold refresh packet", peer, 10, ref failures);
                    Check("free flag set, paid still pending",
                        run.FreeCardRewardDelivered && !run.PaidCardRewardDelivered && run.CardRewards != null,
                        ref failures);
                    Check("free card authority is the committed settlement effect",
                        run.Effects.GetState(CardEffectId(run, "card-reward-free"))
                            == DungeonEffectState.Committed
                        && run.Effects.GetState(CardEffectId(run, "card-reward-paid"))
                            == DungeonEffectState.Absent,
                        ref failures);

                    var shouldReturn = service.HandleEplpCommand(session, new byte[] { 1, 0 }).GetAwaiter().GetResult();
                    Check("EPLP requests return", shouldReturn, ref failures);
                    Check("EPLP does not auto-grant paid card or duplicate free card",
                        LoadGold(inventory) == 10 && !run.PaidCardRewardDelivered,
                        ref failures);

                    service.HandleSelectCard(session, new byte[] { 0, 0 }).GetAwaiter().GetResult();
                    CheckGold("duplicate free-card click does not grant again", inventory, 10, ref failures);

                    service.HandleSelectCard(session, new byte[] { 1, 0 }).GetAwaiter().GetResult();
                    Check("insufficient gold rejects paid card before slot selection",
                        LoadGold(inventory) == 10
                        && run.PaidCardSlots[0] == 0xFF
                        && !run.PaidCardRewardDelivered,
                        ref failures);

                    var run2 = BuildRun(freeGold: 100, paidCardCost: 20);
                    session.Player.CurrentRun = run2;
                    service.HandleSelectCard(session, new byte[] { 0, 0 }).GetAwaiter().GetResult();
                    CheckGoldUpdatePacket("second free card sends gold refresh packet", peer, 110, ref failures);
                    service.HandleSelectCard(session, new byte[] { 1, 0 }).GetAwaiter().GetResult();
                    CheckGoldUpdatePacket("paid card sends gold refresh packet", peer, 90, ref failures);
                    CheckGold("explicit free card grant and paid card cost apply once", inventory, 90, ref failures);
                    Check("paid card grants the reward item without granting commission as gold",
                        inventory.CountMainItem(PaidRewardItemId) == 1,
                        ref failures);
                    Check("card rewards clear after both sides delivered",
                        run2.FreeCardRewardDelivered && run2.PaidCardRewardDelivered && run2.CardRewards == null,
                        ref failures);
                    Check("both card effects commit before settlement completes",
                        run2.Effects.GetState(CardEffectId(run2, "card-reward-free"))
                            == DungeonEffectState.Committed
                        && run2.Effects.GetState(CardEffectId(run2, "card-reward-paid"))
                            == DungeonEffectState.Committed
                        && run2.SettlementState == DungeonSettlementState.Completed,
                        ref failures);

                    service.HandleSelectCard(session, new byte[] { 1, 0 }).GetAwaiter().GetResult();
                    CheckGold("duplicate paid-card click does not spend again", inventory, 90, ref failures);

                    var run3 = BuildRun(freeGold: 5, paidCardCost: 7);
                    session.Player.CurrentRun = run3;
                    shouldReturn = service.HandleEplpCommand(session, new byte[] { 1, 0 }).GetAwaiter().GetResult();
                    Check("EPLP before any card reward does not grant", shouldReturn && LoadGold(inventory) == 90, ref failures);
                }
                finally
                {
                    inventory.ClearDirtyState();
                    InventoryContext.Unregister(session.SessionId, CharacterId);
                }
            }

            using (var peer = ConnectedSession.Create())
            {
                var session = peer.Session;
                session.Player.CharacterId = CharacterId;
                session.Account = new AccountRecord { AccountId = AccountId };
                session.Player.CurrentRun = new DungeonRun(11008, 0)
                {
                    Phase = DungeonRunPhase.ResultShown,
                    CardRewards = null,
                };

                var shouldReturn = service
                    .HandleEplpCommand(session, new byte[] { 1, 1 })
                    .GetAwaiter()
                    .GetResult();
                var sentExitAck = peer.TryReadPacket(out var packet)
                    && packet.Command == 0x01
                    && packet.Type == 0x0048;
                Check(
                    "EPLP exits immediately when settlement intentionally has no card flow",
                    shouldReturn && sentExitAck,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);
                SqliteConnection.ClearAllPools();
                DeleteTempDatabase(tempDb);
            }
        }

        private static DungeonRun BuildRun(int freeGold, int paidCardCost)
        {
            return new DungeonRun(11008, 0)
            {
                Phase = DungeonRunPhase.CardsRevealed,
                PaidCardCost = paidCardCost,
                CardRewards = new List<ClearRewardGenerator.CardReward>
                {
                    new ClearRewardGenerator.CardReward { IsGold = true, GoldAmount = freeGold },
                    default,
                    default,
                    default,
                    new ClearRewardGenerator.CardReward { IsGold = true, GoldAmount = 0 },
                    new ClearRewardGenerator.CardReward
                    {
                        ItemId = PaidRewardItemId,
                        StackCount = 1,
                    },
                    default,
                    default,
                },
                FreeCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
                PaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
            };
        }

        private static void VerifyProjectionSerialization(ref int failures)
        {
            var blockingSender = new RecordingCardNotificationSender(
                blockFirstLayout: true);
            var coordinator = new CardRewardCoordinator(
                sender: blockingSender);
            var session = new EnhancedClientSession(new TcpClient(), null);
            session.Player.CharacterId = CharacterId;
            var run = BuildRun(freeGold: 1, paidCardCost: 1);
            run.Phase = DungeonRunPhase.ResultShown;
            session.Player.CurrentRun = run;

            var first = coordinator.HandleCardStartRequest(session);
            var layoutEntered = blockingSender.WaitForLayoutStart();
            var competing = coordinator.HandleSelectCard(
                session,
                new byte[] { 0, 0 });
            blockingSender.ReleaseLayout();
            Task.WhenAll(first, competing).GetAwaiter().GetResult();
            Check(
                "concurrent layout requests project exactly one card layout",
                layoutEntered
                    && blockingSender.LayoutCount == 1
                    && blockingSender.CardInfoCount == 0
                    && run.Phase == DungeonRunPhase.CardsRevealed,
                ref failures);
            DungeonRunLifecycle.CancelAutoFlip(session);
            session.Close();

            var lateSender = new RecordingCardNotificationSender(
                blockFirstLayout: false);
            coordinator = new CardRewardCoordinator(sender: lateSender);
            session = new EnhancedClientSession(new TcpClient(), null);
            session.Player.CharacterId = CharacterId;
            run = BuildRun(freeGold: 1, paidCardCost: 1);
            run.Phase = DungeonRunPhase.ResultShown;
            session.Player.CurrentRun = run;

            run.Settlement.CardProjectionGate.Wait();
            try
            {
                coordinator.ScheduleAutoFlow(
                    session,
                    layoutDelayMs: 0,
                    autoFlipDelayMs: 4000);
                Thread.Sleep(50);
                run.TryMarkCardsRevealed();
            }
            finally
            {
                run.Settlement.CardProjectionGate.Release();
            }

            coordinator.HandleSelectCard(session, new byte[] { 0, 1 })
                .GetAwaiter()
                .GetResult();
            Thread.Sleep(100);
            Check(
                "late layout timer cannot project after card info",
                lateSender.LayoutCount == 0
                    && lateSender.CardInfoCount == 1,
                ref failures);
            DungeonRunLifecycle.CancelAutoFlip(session);
            session.Close();

            var duplicateSender = new RecordingCardNotificationSender(
                blockFirstLayout: false);
            coordinator = new CardRewardCoordinator(sender: duplicateSender);
            session = new EnhancedClientSession(new TcpClient(), null);
            session.Player.CharacterId = CharacterId;
            run = BuildRun(freeGold: 1, paidCardCost: 1);
            session.Player.CurrentRun = run;

            var duplicateSelections = new Task[16];
            for (var index = 0; index < duplicateSelections.Length; index++)
            {
                duplicateSelections[index] = coordinator.HandleSelectCard(
                    session,
                    new byte[] { 0, 1 });
            }
            Task.WhenAll(duplicateSelections).GetAwaiter().GetResult();
            Check(
                "duplicate selection projects the selected slot exactly once",
                duplicateSender.LayoutCount == 0
                    && duplicateSender.CardInfoCount == 1
                    && run.CardFlipCount == 1
                    && run.FreeCardSlots[1] == 0,
                ref failures);
            DungeonRunLifecycle.CancelAutoFlip(session);
            session.Close();

            var completedSender = new RecordingCardNotificationSender(
                blockFirstLayout: false);
            coordinator = new CardRewardCoordinator(sender: completedSender);
            session = new EnhancedClientSession(new TcpClient(), null);
            session.Player.CharacterId = CharacterId;
            run = BuildRun(freeGold: 1, paidCardCost: 1);
            run.CardRewards = null;
            var completed = run.TryCompleteSettlement();
            session.Player.CurrentRun = run;

            coordinator.HandleSelectCard(session, new byte[] { 0, 0 })
                .GetAwaiter()
                .GetResult();
            coordinator.HandleCardStartRequest(session)
                .GetAwaiter()
                .GetResult();
            var shouldReturn = coordinator.HandleEplpCommand(
                    session,
                    new byte[] { 1, 0 })
                .GetAwaiter()
                .GetResult();
            Check(
                "completed settlement cannot reopen or reproject card UI",
                completed
                    && run.SettlementState == DungeonSettlementState.Completed
                    && run.Phase == DungeonRunPhase.ResultShown
                    && completedSender.LayoutCount == 0
                    && completedSender.CardInfoCount == 0
                    && completedSender.ExitCount == 1
                    && shouldReturn,
                ref failures);
            session.Close();
        }

        private static void VerifyLayoutPacketSequence(ref int failures)
        {
            using (var peer = ConnectedSession.Create())
            {
                var sender = new CardRewardNotificationSender();
                sender.SendLayoutAsync(peer.Session).GetAwaiter().GetResult();
                var firstRead = peer.TryReadPacket(out var first);
                var secondRead = peer.TryReadPacket(out var second);
                Check(
                    "card layout projects CARD_START before CARD_LAYOUT",
                    firstRead
                        && secondRead
                        && first.Command == 0x01
                        && first.Type == 0x0045
                        && first.Body.Length == 1
                        && first.Body[0] == 0x01
                        && second.Command == 0x01
                        && second.Type == 0x0046
                        && second.Body.Length == 17
                        && second.Body[0] == 0x01,
                    ref failures);
            }
        }

        private static void VerifyClearRewardEtc(ref int failures)
        {
            var definition = ClearRewardDefinitionCatalog.Current;
            var range = definition.GetGradeRange(86);
            Check("clear-reward ETC level 86 paid-card cost is 10300",
                definition.GetPaidCardCost(86) == 10300,
                ref failures);
            Check("clear-reward ETC item type weights are 0/9500/0/500",
                definition.GetItemTypeWeight(1) == 0
                && definition.GetItemTypeWeight(2) == 9500
                && definition.GetItemTypeWeight(3) == 0
                && definition.GetItemTypeWeight(4) == 500,
                ref failures);
            Check("clear-reward ETC level 86 grade range is down 7/up 3",
                range.Down == 7 && range.Up == 3,
                ref failures);
        }

        private static void VerifyPaidCardProtocolFields(ref int failures)
        {
            const int commission = 10300;
            var clearRewardBody = DungeonNotificationBuilder.BuildClearDungeonReward(
                clearBaseExp: 0,
                paidCardCost: commission);
            Check("CLEAR_DUNGEON_REWARD projects paid-card commission",
                TryReadPaidCardCommission(clearRewardBody, out var projected)
                && projected == commission,
                ref failures);

            var run = BuildRun(freeGold: 0, paidCardCost: commission);
            var selected = CardRewardRules.TrySelectCardSlot(run, 1, 0);
            var cardInfoBody = CardRewardNotificationSender.BuildCardInfoAck(run);
            Check("CARD_INFO keeps paid gold reward placeholder at zero",
                selected
                && cardInfoBody.Length >= 12
                && cardInfoBody[3] == 2
                && BitConverter.ToInt32(cardInfoBody, 8) == 0,
                ref failures);
        }

        private static void VerifyConfiguredRewardGeneration(ref int failures)
        {
            var zeroKillContext = new ClearRewardGenerationContext(
                dungeonLevel: 86,
                difficulty: 1,
                partyMemberCount: 1,
                rankBonusRate: 0.05f,
                normalKillCount: 0,
                championKillCount: 0,
                bossKillCount: 0,
                visitedRoomCount: 5,
                totalRoomCount: 5);
            var rewardContext = new ClearRewardGenerationContext(
                dungeonLevel: 86,
                difficulty: 1,
                partyMemberCount: 1,
                rankBonusRate: 0.05f,
                normalKillCount: 20,
                championKillCount: 2,
                bossKillCount: 1,
                visitedRoomCount: 5,
                totalRoomCount: 5);
            var zeroKillGold = ClearRewardGenerator.GenerateFreeGoldCard(
                zeroKillContext,
                new DnfLcg(1));
            var populatedGold = ClearRewardGenerator.GenerateFreeGoldCard(
                rewardContext,
                new DnfLcg(1));
            Check("configured free gold requires recorded kill facts",
                zeroKillGold.IsGold
                && zeroKillGold.GoldAmount == 0
                && populatedGold.IsGold
                && populatedGold.GoldAmount > 0,
                ref failures);

            var freeItem = FindGeneratedItem(
                seed => ClearRewardGenerator.GenerateFreeItemCard(
                    rewardContext,
                    new DnfLcg(seed)));
            var paidItem = FindGeneratedItem(
                seed => ClearRewardGenerator.GeneratePaidItemCard(
                    rewardContext,
                    new DnfLcg(seed)));
            Check("configured free-card equipment pool produces a real item",
                freeItem.ItemId > 0
                && freeItem.StackCount == 1
                && string.Equals(
                    ItemMetadataResolver.Resolve(freeItem.ItemId).ItemKind,
                    "equipment",
                    StringComparison.Ordinal),
                ref failures);
            Check("configured paid-card equipment pool produces a real item",
                paidItem.ItemId > 0
                && paidItem.StackCount == 1
                && string.Equals(
                    ItemMetadataResolver.Resolve(paidItem.ItemId).ItemKind,
                    "equipment",
                    StringComparison.Ordinal),
                ref failures);
        }

        private static ClearRewardGenerator.CardReward FindGeneratedItem(
            Func<uint, ClearRewardGenerator.CardReward> generate)
        {
            for (uint seed = 1; seed <= 256; seed++)
            {
                var reward = generate(seed);
                if (!reward.IsGold && reward.ItemId > 0)
                    return reward;
            }
            return default;
        }

        private static void VerifySharedKillStatistics(ref int failures)
        {
            var instance = new DungeonInstance(11008, 0);
            var room = new RoomKey(1, 1, 0);
            var first = instance.TryRecordMonsterKill(1, room, 100, 0);
            var duplicate = instance.TryRecordMonsterKill(1, room, 100, 0);
            instance.TryRecordMonsterKill(1, room, 101, 1);
            instance.TryRecordMonsterKill(1, room, 102, 3);
            instance.TryRecordMonsterKill(1, room, 103, 2);
            var statistics = instance.KillStatistics;
            Check("shared kill statistics ignore party-relay duplicate actors",
                first
                && !duplicate
                && statistics.NormalKillCount == 2
                && statistics.ChampionKillCount == 1
                && statistics.BossKillCount == 1,
                ref failures);
        }

        private static bool TryReadPaidCardCommission(
            byte[] body,
            out int commission)
        {
            commission = 0;
            const int rewardBlocksOffset = 172;
            var offset = rewardBlocksOffset;
            if (body == null || body.Length <= offset)
                return false;

            for (var member = 0; member < 8; member++)
            {
                if (offset >= body.Length)
                    return false;
                var count = body[offset++];
                var entryBytes = count * 8;
                if (offset > body.Length - entryBytes)
                    return false;
                offset += entryBytes;
            }

            if (offset > body.Length - sizeof(int))
                return false;
            commission = BitConverter.ToInt32(body, offset);
            return true;
        }

        private static int LoadGold(InventoryService inventory)
        {
            return inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
        }

        private static DungeonEffectId CardEffectId(
            DungeonRun run,
            string effectKind)
        {
            return new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                effectKind,
                DungeonEffectScope.Player,
                run.RunId);
        }

        private static void Seed(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'card-reward-flow', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@cid, @aid, 'card-reward-flow');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private static void CheckGold(string name, InventoryService inventory, int expected, ref int failures)
        {
            var actual = LoadGold(inventory);
            Check($"{name} expected={expected} actual={actual}", actual == expected, ref failures);
        }

        private static void CheckGoldUpdatePacket(string name, ConnectedSession peer, int expectedGold, ref int failures)
        {
            var ok = false;
            while (peer.TryReadPacket(out var packet))
            {
                if (packet.Command != 0x00 || packet.Type != 0x000E)
                    continue;

                if (ContainsGoldUpdate(packet.Body, expectedGold))
                {
                    ok = true;
                    break;
                }
            }

            Check($"{name} expectedGold={expectedGold}", ok, ref failures);
        }

        private static bool ContainsGoldUpdate(byte[] body, int expectedGold)
        {
            if (body == null || body.Length < 3 || body[0] != 0)
                return false;

            var count = BitConverter.ToUInt16(body, 1);
            var offset = 3;
            for (var i = 0; i < count; i++, offset += 84)
            {
                if (body.Length < offset + 10)
                    return false;

                var slot = BitConverter.ToInt16(body, offset);
                var itemId = BitConverter.ToInt32(body, offset + 2);
                var gold = BitConverter.ToInt32(body, offset + 6);
                if (slot == 0 && itemId == 0 && gold == expectedGold)
                    return true;
            }

            return false;
        }

        private static void DeleteTempDatabase(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
            var wal = path + "-wal";
            if (File.Exists(wal))
                File.Delete(wal);
            var shm = path + "-shm";
            if (File.Exists(shm))
                File.Delete(shm);
        }

        private sealed class RecordingCardNotificationSender
            : ICardRewardNotificationSender
        {
            private readonly bool _blockFirstLayout;
            private readonly ManualResetEventSlim _layoutEntered =
                new ManualResetEventSlim(false);
            private readonly TaskCompletionSource<bool> _layoutRelease =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private int _layoutCount;
            private int _cardInfoCount;
            private int _exitCount;

            internal RecordingCardNotificationSender(bool blockFirstLayout)
            {
                _blockFirstLayout = blockFirstLayout;
                if (!blockFirstLayout)
                    _layoutRelease.TrySetResult(true);
            }

            internal int LayoutCount => Volatile.Read(ref _layoutCount);
            internal int CardInfoCount => Volatile.Read(ref _cardInfoCount);
            internal int ExitCount => Volatile.Read(ref _exitCount);

            internal bool WaitForLayoutStart()
                => _layoutEntered.Wait(TimeSpan.FromSeconds(2));

            internal void ReleaseLayout()
                => _layoutRelease.TrySetResult(true);

            public async Task SendLayoutAsync(EnhancedClientSession session)
            {
                var count = Interlocked.Increment(ref _layoutCount);
                _layoutEntered.Set();
                if (_blockFirstLayout && count == 1)
                    await _layoutRelease.Task;
            }

            public Task SendCardInfoAsync(
                EnhancedClientSession session,
                DungeonRun run)
            {
                Interlocked.Increment(ref _cardInfoCount);
                return Task.CompletedTask;
            }

            public Task SendExitAsync(
                EnhancedClientSession session,
                byte state,
                byte option)
            {
                Interlocked.Increment(ref _exitCount);
                return Task.CompletedTask;
            }

            public Task SendItemUpdatesAsync(
                EnhancedClientSession session,
                IReadOnlyList<InventorySlotMutation> changes)
                => Task.CompletedTask;
        }

        private sealed class ConnectedSession : IDisposable
        {
            private const int GameEnvelopeHeaderSize = 15;
            private readonly TcpClient _serverSide;

            private ConnectedSession(EnhancedClientSession session, TcpClient serverSide)
            {
                Session = session;
                _serverSide = serverSide;
                _serverSide.ReceiveTimeout = 1000;
            }

            public EnhancedClientSession Session { get; }

            public static ConnectedSession Create()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var endpoint = (IPEndPoint)listener.LocalEndpoint;

                var client = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                client.Connect(endpoint.Address, endpoint.Port);
                var server = accept.GetAwaiter().GetResult();
                listener.Stop();

                return new ConnectedSession(
                    new EnhancedClientSession(client, new GamePacketHeader()),
                    server);
            }

            public void Dispose()
            {
                Session.Close();
                    _serverSide.Close();
            }

            public bool TryReadPacket(out SentPacket packet)
            {
                packet = default;
                try
                {
                    var stream = _serverSide.GetStream();
                    var header = new byte[GameEnvelopeHeaderSize];
                    if (!ReadExact(stream, header, 0, header.Length))
                        return false;

                    var length = (int)BitConverter.ToUInt32(header, 3);
                    if (length < GameEnvelopeHeaderSize)
                        return false;

                    var bodyLength = length - GameEnvelopeHeaderSize;
                    var body = new byte[bodyLength];
                    if (bodyLength > 0 && !ReadExact(stream, body, 0, bodyLength))
                        return false;

                    packet = new SentPacket(header[0], BitConverter.ToUInt16(header, 1), body);
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (SocketException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }

            private static bool ReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
            {
                while (count > 0)
                {
                    var read = stream.Read(buffer, offset, count);
                    if (read <= 0)
                        return false;

                    offset += read;
                    count -= read;
                }

                return true;
            }

            public struct SentPacket
            {
                public SentPacket(byte command, ushort type, byte[] body)
                {
                    Command = command;
                    Type = type;
                    Body = body;
                }

                public byte Command { get; }
                public ushort Type { get; }
                public byte[] Body { get; }
            }
        }
    }
}
