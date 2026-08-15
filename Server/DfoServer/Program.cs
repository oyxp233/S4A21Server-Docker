using DfoServer.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer
{
    internal class Program
    {
        // 自测注册表: 新增自测在这里加一行, 单跑参数与 --selftest-all 都会覆盖到。
        private static readonly (string Arg, Func<int> Run)[] SelfTestRegistry =
        {
            ("--selftest-a21-startup-protocol", SelfTests.A21StartupProtocolSelfTest.Run),
            ("--selftest-a21-channel-protocol", SelfTests.A21ChannelProtocolSelfTest.Run),
            ("--selftest-a21-create-character-protocol", SelfTests.A21CreateCharacterProtocolSelfTest.Run),
            ("--selftest-a21-tutorial-protocol", SelfTests.A21TutorialProtocolSelfTest.Run),
            ("--selftest-sqlite-bootstrap", SelfTests.SqliteDatabaseBootstrapSelfTest.Run),
            ("--selftest-game-database", SelfTests.GameDatabaseSelfTest.Run),
            ("--selftest-monster-card-bind", SelfTests.MonsterCardBindSelfTest.Run),
            ("--selftest-monster-card-bead-metadata", SelfTests.MonsterCardBeadMetadataSelfTest.Run),
            ("--selftest-monster-card-upgrade", SelfTests.MonsterCardUpgradeSelfTest.Run),
            ("--selftest-auction-service", SelfTests.AuctionServiceNotificationSelfTest.Run),
            ("--selftest-chronicle-growth", SelfTests.ChronicleGrowthSelfTest.Run),
            ("--selftest-chronicle-refine", SelfTests.ChronicleRefineSelfTest.Run),
            ("--selftest-title-change", SelfTests.TitleChangeSelfTest.Run),
            ("--selftest-item-upgrade", SelfTests.ItemUpgradeSelfTest.Run),
            ("--selftest-equipment-upgrade-regeneration-transaction", SelfTests.EquipmentUpgradeRegenerationTransactionSelfTest.Run),
            ("--selftest-equipment-augmentation-transaction", SelfTests.EquipmentAugmentationTransactionSelfTest.Run),
            ("--selftest-equipment-amplify-option-transaction", SelfTests.EquipmentAmplifyOptionTransactionSelfTest.Run),
            ("--selftest-random-option-transaction", SelfTests.RandomOptionTransactionSelfTest.Run),
            ("--selftest-title-change-transaction", SelfTests.TitleChangeTransactionSelfTest.Run),
            ("--selftest-card-emblem-transaction", SelfTests.CardEmblemTransactionSelfTest.Run),
            ("--selftest-recipe-separate-upgrade-transaction", SelfTests.RecipeSeparateUpgradeTransactionSelfTest.Run),
            ("--selftest-special-consumable-transaction", SelfTests.SpecialConsumableTransactionSelfTest.Run),
            ("--selftest-separate-upgrade", SelfTests.SeparateUpgradeSelfTest.Run),
            ("--selftest-avatar-compound", SelfTests.AvatarCompoundSelfTest.Run),
            ("--selftest-avatar-compound-transaction", SelfTests.AvatarCompoundTransactionSelfTest.Run),
            ("--selftest-cerashop", SelfTests.CeraShopSelfTest.Run),
            ("--selftest-raid-protocol", SelfTests.RaidProtocolSelfTest.Run),
            ("--selftest-pet-consumable", SelfTests.PetConsumableSelfTest.Run),
            ("--selftest-pet-consumable-transaction", SelfTests.PetConsumableTransactionSelfTest.Run),
            ("--selftest-pet-creature-mutation-transaction", SelfTests.PetCreatureMutationTransactionSelfTest.Run),
            ("--selftest-pet-creature-experience-transaction", SelfTests.PetCreatureExperienceTransactionSelfTest.Run),
            ("--selftest-pet-creature-satiety-transaction", SelfTests.PetCreatureSatietyTransactionSelfTest.Run),
            ("--selftest-npc-material-exchange-price", SelfTests.NpcMaterialExchangePriceSelfTest.Run),
            ("--selftest-collectbox-runtime", SelfTests.CollectBoxRuntimeSelfTest.Run),
            ("--selftest-lottery-item", SelfTests.LotteryItemSelfTest.Run),
            ("--selftest-lottery-progress-reset-transaction", SelfTests.LotteryProgressResetTransactionSelfTest.Run),
            ("--selftest-lottery-open-transaction", SelfTests.LotteryOpenTransactionSelfTest.Run),
            ("--selftest-skill-refund-transaction", SelfTests.SkillRefundTransactionSelfTest.Run),
            ("--selftest-inventory-delete-transaction", SelfTests.InventoryDeleteTransactionSelfTest.Run),
            ("--selftest-stackable-use-transaction", SelfTests.StackableUseTransactionSelfTest.Run),
            ("--selftest-inventory-move-transaction", SelfTests.InventoryMoveTransactionSelfTest.Run),
            ("--selftest-inventory-sort-lock-transaction", SelfTests.InventorySortLockTransactionSelfTest.Run),
            ("--selftest-inventory-disjoint-transaction", SelfTests.InventoryDisjointTransactionSelfTest.Run),
            ("--selftest-dungeon-map-fallback", SelfTests.DungeonMapFallbackSelfTest.Run),
            ("--selftest-move-map-request", SelfTests.MoveMapRequestSelfTest.Run),
            ("--selftest-tower-of-despair-progress", SelfTests.TowerOfDespairProgressSelfTest.Run),
            ("--selftest-tower-of-despair-settlement-transaction", SelfTests.TowerOfDespairSettlementTransactionSelfTest.Run),
            ("--selftest-dungeon-room-progress", SelfTests.DungeonRoomProgressSelfTest.Run),
            ("--selftest-dungeon-run", SelfTests.DungeonRunLifecycleSelfTest.Run),
            ("--selftest-dungeon-instance-registry", SelfTests.DungeonInstanceRegistrySelfTest.Run),
            ("--selftest-dungeon-rejoin-protocol", SelfTests.DungeonRejoinProtocolSelfTest.Run),
            ("--selftest-dungeon-encounter-directive", SelfTests.DungeonEncounterDirectiveSelfTest.Run),
            ("--selftest-dungeon-reward-policy", SelfTests.DungeonRewardPolicySelfTest.Run),
            ("--selftest-dungeon-experience", SelfTests.DungeonExperienceSelfTest.Run),
            ("--selftest-impossible-dungeon-drop", SelfTests.ImpossibleDungeonDropSelfTest.Run),
            ("--selftest-dungeon-difficulty-permission", SelfTests.DungeonDifficultyPermissionSelfTest.Run),
            ("--selftest-scripted-fatal-endpoint", SelfTests.ScriptedFatalEndpointSelfTest.Run),
            ("--selftest-special-dungeon", SelfTests.SpecialDungeonSelfTest.Run),
            ("--selftest-special-dungeon-part2", SelfTests.SpecialDungeonPart2SelfTest.Run),
            ("--selftest-special-dungeon-part3", SelfTests.SpecialDungeonPart3SelfTest.Run),
            ("--selftest-tournament-settlement", SelfTests.TournamentSettlementSelfTest.Run),
            ("--selftest-tournament-experience-transaction", SelfTests.TournamentExperienceTransactionSelfTest.Run),
            ("--selftest-card-reward-flow", SelfTests.CardRewardFlowSelfTest.Run),
            ("--selftest-card-reward-persistent-effect", SelfTests.CardRewardPersistentEffectSelfTest.Run),
            ("--selftest-blood-altar-persistent-reward", SelfTests.BloodAltarPersistentRewardSelfTest.Run),
            ("--selftest-dungeon-reward-failure-recovery", SelfTests.DungeonRewardFailureRecoverySelfTest.Run),
            ("--selftest-dungeon-persistent-effects", SelfTests.DungeonPersistentEffectSelfTest.Run),
            ("--selftest-monster-card-drop", SelfTests.MonsterCardDropSelfTest.Run),
            ("--selftest-dungeon-npc-item-drop", SelfTests.DungeonNpcItemDropSelfTest.Run),
            ("--selftest-quest-dungeon-drop", SelfTests.QuestDungeonDropSelfTest.Run),
            ("--selftest-quest-giveup-transaction", SelfTests.QuestGiveupTransactionSelfTest.Run),
            ("--selftest-character-option", SelfTests.CharacterOptionSelfTest.Run),
            ("--selftest-seed-character-protocol", SelfTests.SeedCharacterProtocolSelfTest.Run),
            ("--selftest-expert-contract-skill", SelfTests.ExpertContractSkillSelfTest.Run),
            ("--selftest-expert-job-store", SelfTests.ExpertJobStoreSelfTest.Run),
            ("--selftest-expert-job-giveup", SelfTests.ExpertJobGiveupSelfTest.Run),
            ("--selftest-crystal-contract", SelfTests.CrystalContractSelfTest.Run),
            ("--selftest-slot-expansion-quest", SelfTests.SlotExpansionQuestSelfTest.Run),
            ("--selftest-character-slot-policy", SelfTests.CharacterSlotPolicySelfTest.Run),
            ("--selftest-knight-shield-deck", SelfTests.KnightShieldDeckRepositorySelfTest.Run),
            ("--selftest-clear-map-quest", SelfTests.ClearMapQuestSelfTest.Run),
            ("--selftest-death-tower-map-loader", SelfTests.DeathTowerMapLoaderSelfTest.Run),
            ("--selftest-death-tower-drop", SelfTests.DeathTowerDropSelfTest.Run),
            ("--selftest-death-tower-protocol", SelfTests.DeathTowerProtocolSelfTest.Run),
            ("--selftest-death-tower-inventory-overlay", SelfTests.DeathTowerInventoryOverlaySelfTest.Run),
            ("--selftest-death-tower-quest-routing", SelfTests.DeathTowerQuestRoutingSelfTest.Run),
            ("--selftest-quest-clear", SelfTests.QuestClearSelfTest.Run),
            ("--selftest-quest-trigger-counts", SelfTests.QuestTriggerCountSelfTest.Run),
            ("--selftest-daily-challenge", SelfTests.DailyChallengeSelfTest.Run),
            ("--selftest-quest-chain-availability", SelfTests.QuestChainAvailabilitySelfTest.Run),
            ("--selftest-quest-ack-format", SelfTests.QuestAckFormatSelfTest.Run),
            ("--selftest-quest-notify-selection", SelfTests.QuestNotifySelectionSelfTest.Run),
            ("--selftest-clear-quest-list-packet", SelfTests.ClearQuestListPacketSelfTest.Run),
            ("--selftest-special-reward-quest-source", SelfTests.SpecialRewardQuestSourceSelfTest.Run),
            ("--selftest-question-quest-branch", SelfTests.QuestionQuestBranchSelfTest.Run),
            ("--selftest-striker-skill", SelfTests.StrikerSkillSelfTest.Run),
            ("--selftest-pet-equipment", SelfTests.PetEquipmentSelfTest.Run),
            ("--selftest-pet-hatch", SelfTests.PetHatchSelfTest.Run),
            ("--selftest-gold-limit", SelfTests.GoldLimitSelfTest.Run),
            ("--selftest-cargo-runtime", SelfTests.CargoRuntimeSelfTest.Run),
            ("--selftest-lucky-star-purchase", SelfTests.LuckyStarPurchaseSelfTest.Run),
            ("--selftest-rental-purchase", SelfTests.RentalPurchaseSelfTest.Run),
            ("--selftest-stamina-recovery", SelfTests.StaminaRecoverySelfTest.Run),
            ("--selftest-domain-transaction-boundaries", SelfTests.DomainTransactionBoundarySelfTest.Run),
            ("--selftest-daily-reset", SelfTests.DailyResetSelfTest.Run),
            ("--selftest-daily-refill-item", SelfTests.DailyRefillItemSelfTest.Run),
            ("--selftest-revive-coin", SelfTests.ReviveCoinSelfTest.Run),
            ("--selftest-revive-coin-daily-transaction", SelfTests.ReviveCoinDailyClaimTransactionSelfTest.Run),
            ("--selftest-clock", SelfTests.ClockSelfTest.Run),
            ("--selftest-rental-info", SelfTests.RentalInfoSelfTest.Run),
            ("--selftest-honor-level", SelfTests.HonorLevelSelfTest.Run),
            ("--selftest-character-experience-progression", SelfTests.CharacterExperienceProgressionSelfTest.Run),
            ("--selftest-party", SelfTests.PartySelfTest.Run),
            ("--selftest-party-command-isolation", SelfTests.PartyCommandIsolationSelfTest.Run),
            ("--selftest-chat-broadcast", SelfTests.ChatBroadcastSelfTest.Run),
            ("--selftest-party-udp-relay-core", SelfTests.PartyUdpRelayCoreSelfTest.Run),
            ("--selftest-other-user-info", SelfTests.OtherUserInfoSelfTest.Run),
            ("--selftest-other-user-info-protocol", SelfTests.OtherUserInfoProtocolSelfTest.Run),
            ("--selftest-session-generation", SelfTests.SessionGenerationSelfTest.Run),
            ("--selftest-inventory-lease-lifecycle", SelfTests.InventoryLeaseLifecycleSelfTest.Run),
            ("--selftest-npc-shop-transaction", SelfTests.NpcShopTransactionSelfTest.Run),
            ("--selftest-npc-sell-transaction", SelfTests.NpcSellTransactionSelfTest.Run),
            ("--selftest-equipment-repair-transaction", SelfTests.EquipmentRepairTransactionSelfTest.Run),
            ("--selftest-secret-shop-transaction", SelfTests.SecretShopTransactionSelfTest.Run),
            ("--selftest-teleport-consumable-transaction", SelfTests.TeleportConsumableTransactionSelfTest.Run),
            ("--selftest-crane-start-transaction", SelfTests.CraneStartTransactionSelfTest.Run),
            ("--selftest-crane-pickup-transaction", SelfTests.CranePickupTransactionSelfTest.Run),
            ("--selftest-private-town-area", SelfTests.PrivateTownAreaSelfTest.Run),
            ("--selftest-free-duel-channel", SelfTests.FreeDuelChannelSelfTest.Run),
            ("--selftest-free-duel-room-core", SelfTests.FreeDuelRoomCoreSelfTest.Run),
            ("--selftest-pvp-relay-lifecycle", SelfTests.PvpRelayLifecycleSelfTest.Run),
            ("--selftest-pvp-required-publication", SelfTests.PvpRequiredPublicationSelfTest.Run),
            ("--selftest-pvp-room-admission", SelfTests.PvpRoomAdmissionSelfTest.Run),
            ("--selftest-pvp-room-mutation-recovery", SelfTests.PvpRoomMutationRecoverySelfTest.Run),
            ("--selftest-free-duel-selection-wiring", SelfTests.FreeDuelSelectionWiringSelfTest.Run),
            ("--selftest-pvp-skill-isolation", SelfTests.PvpSkillIsolationSelfTest.Run),
            ("--selftest-dungeon-combat-party", SelfTests.DungeonCombatPartySelfTest.Run),
            ("--selftest-udp-relay", SelfTests.UdpRelaySelfTest.Run),
            ("--selftest-growth-capsule", SelfTests.GrowthCapsuleSelfTest.Run),
            ("--selftest-crane-minigame", SelfTests.CraneMiniGameSelfTest.Run),
            ("--selftest-mailbox", SelfTests.MailboxSelfTest.Run),
            ("--selftest-mercenary", SelfTests.MercenarySelfTest.Run),
            ("--selftest-equipment-regeneration-config", SelfTests.EquipmentRegenerationConfigSelfTest.Run),
        };

        // 顺序跑全部自测, 输出汇总表; 任一失败(或抛异常)退出码为 1。
        private static int RunAllSelfTests()
        {
            var failed = new List<string>();
            foreach (var entry in SelfTestRegistry)
            {
                var name = entry.Arg.Substring("--selftest-".Length);
                Console.WriteLine($"===== [{name}] =====");
                int code;
                try
                {
                    code = entry.Run();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{name}] EXCEPTION: {ex.Message}");
                    code = 1;
                }
                if (code != 0)
                    failed.Add(name);
            }

            Console.WriteLine("===== SELFTEST SUMMARY =====");
            Console.WriteLine($"total={SelfTestRegistry.Length} pass={SelfTestRegistry.Length - failed.Count} fail={failed.Count}");
            foreach (var name in failed)
                Console.WriteLine($"FAIL: {name}");
            return failed.Count == 0 ? 0 : 1;
        }

        private static PartyUdpRelay CreatePartyUdpRelay(string scope)
        {
            var isPvp = string.Equals(
                scope,
                "pvp",
                StringComparison.OrdinalIgnoreCase);
            var enabled = isPvp
                ? GameNetworkConfig.PvpUdpRelayEnabled
                : GameNetworkConfig.UdpRelayEnabled;
            var gateName = isPvp ? "DFO_PVP_UDP_RELAY" : "DFO_UDP_RELAY";
            FileLogger.Log(
                $"[PartyUdpRelay scope={scope}] startup gate " +
                $"{gateName}={(enabled ? 1 : 0)}");
            if (!enabled)
                return null;

            if (GameNetworkConfig.ProxyMode)
            {
                FileLogger.Log(
                    $"[PartyUdpRelay scope={scope}] disabled: " +
                    "proxy mode is not supported");
                return null;
            }

            if (!GameNetworkConfig.UdpRelayPublicIpConfigured ||
                !System.Net.IPAddress.TryParse(
                    GameNetworkConfig.UdpRelayPublicIp,
                    out var publicIp) ||
                publicIp.AddressFamily !=
                    System.Net.Sockets.AddressFamily.InterNetwork ||
                System.Net.IPAddress.IsLoopback(publicIp) ||
                publicIp.Equals(System.Net.IPAddress.Any) ||
                publicIp.Equals(System.Net.IPAddress.Broadcast))
            {
                FileLogger.Log(
                    $"[PartyUdpRelay scope={scope}] disabled: set a " +
                    "non-loopback numeric IPv4 address with " +
                    "DFO_UDP_RELAY_PUBLIC_IP");
                return null;
            }

            var portBase = isPvp
                ? GameNetworkConfig.PvpUdpRelayPortBase
                : GameNetworkConfig.UdpRelayPortBase;
            var portCount = isPvp
                ? GameNetworkConfig.PvpUdpRelayPortCount
                : GameNetworkConfig.UdpRelayPortCount;
            return new PartyUdpRelay(
                publicIp.ToString(),
                portBase,
                portCount,
                scope);
        }

        static void Main(string[] args)
        {
            args ??= Array.Empty<string>();

            if (Array.IndexOf(args, "--selftest-all") >= 0)
            {
                Environment.Exit(RunAllSelfTests());
                return;
            }

            foreach (var entry in SelfTestRegistry)
            {
                if (Array.IndexOf(args, entry.Arg) >= 0)
                {
                    Environment.Exit(entry.Run());
                    return;
                }
            }

            if (Array.IndexOf(args, "--probe-skill-state") >= 0 || Array.IndexOf(args, "--probe-skill-csv") >= 0)
            {
                Environment.Exit(SelfTests.SkillStateProbe.Run(args));
                return;
            }

            GameNetworkConfig.Configure(args);
            GameNetworkConfig.ValidateRelayConfiguration();

            PacketFileLogger.Initialize();
            if (GameNetworkConfig.PacketCaptureEnabled)
                Console.WriteLine("[PacketCapture] ENABLED – all SEND/RECV packets logged to packet_log.txt");

            try
            {
                _ = GameWorld.GameWorldConfig.PvfArchivePath;
            }
            catch (System.IO.FileNotFoundException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: Script.pvf not found.");
                Console.WriteLine("Please place Script.pvf in Data/Pvf/Script.pvf, or set the PVF_ARCHIVE_PATH environment variable.");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            Console.Write("Loading Script.pvf... ");
            try
            {
                GameWorld.PvfArchiveAccessor.ReadText("character/character.lst");
                var itemMetadataWarmupTimer = Stopwatch.StartNew();
                Game.Inventory.ItemMetadataResolver.Warmup();
                itemMetadataWarmupTimer.Stop();
                FileLogger.Log(
                    $"[Startup] ITEM_METADATA_WARMUP totalMs={itemMetadataWarmupTimer.Elapsed.TotalMilliseconds:F3}");
                Game.Dungeon.ClearRewardGenerator.WarmUp();
                GameWorld.DungeonExperienceDefinitionCatalog.WarmUp();
                Game.Dungeon.PassiveObjectDropPlanningService.WarmUp();
                Game.Inventory.EquipmentRegenerationCandidateCatalog.Warmup();
                GameWorld.IndependentDropDefinitionCatalog.WarmUp();
                Game.Inventory.ChronicleRefineMaterialResolver.Warmup();
                Game.Mercenary.StrikerSkillDataProvider.Warmup();
                Game.Mercenary.StrikerDefaultAvatarDataProvider.Warmup();
                Console.WriteLine("OK");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAILED");
                Console.WriteLine($"Error: Failed to load Script.pvf: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            using var runtimeBuilder = Infrastructure.ServerRuntimeBuilder.CreateDefault();
            var database = runtimeBuilder.Database;

            // 启动时一次性按当前等级重算所有角色战斗属性, 修复历史"升级未重算属性"的存量数据。
            // 必须在 PVF 加载后: 属性表来自 Script.pvf。幂等, 重复执行结果一致, 正常时静默, 仅出错时提示。
            try
            {
                new Game.CharacterData.SqliteSubtype1Repository(database)
                    .RecomputeAllCombatStats();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] combat stats recompute skipped: {ex.Message}");
            }

            var server = new MultiStructureTcpServer();
            var sessionDirectory = new Game.Session.SessionDirectory();

            int channelPort = GameNetworkConfig.ProxyMode ? 7002 : 7001;
            var gameChannels = GameNetworkConfig.GetGameChannels();
            var gameListenerPorts = gameChannels
                .Select(channel => channel.ListenerGamePort)
                .Distinct()
                .ToArray();
            var publicGamePorts = gameChannels
                .Select(channel => channel.PublicGamePort)
                .Distinct()
                .ToArray();

            using var udpRelay = CreatePartyUdpRelay("party");
            using var pvpUdpRelay = CreatePartyUdpRelay("pvp");
            using var gameProtocolHandler = runtimeBuilder.BuildGameProtocolHandler(
                sessionDirectory,
                packet => Task.WhenAll(
                    gameListenerPorts.Select(
                        port => server.BroadcastToPortAsync(port, packet))),
                udpRelay,
                pvpUdpRelay);

            var portConfigs = new Dictionary<int, (IProtocolHandler handler, IPacketHeader structure)>
            {
                { channelPort, (new ChannelProtocolHandler(), new ChannelPacketHeader()) }
            };
            Console.WriteLine(
                "A21WireLayout: 14B game receive header; A21 CHANNELINFO/login layout");
            foreach (var channel in gameChannels)
            {
                portConfigs.Add(
                    channel.ListenerGamePort,
                    (gameProtocolHandler, new GamePacketHeader()));
            }

            server.Start(portConfigs);

            Game.Inventory.InventoryPersistenceService.RegisterClock(Infrastructure.ClockService.Instance);
            Infrastructure.ClockService.Instance.Start();

            if (GameNetworkConfig.ProxyMode)
            {
                Console.WriteLine(
                    $"[ProxyMode] Server listening on {channelPort}(channel) / " +
                    $"{string.Join("/", gameListenerPorts)}(game); " +
                    "PvfProxy forwards the public channel/game ports.");
            }

            Console.WriteLine("Multi-structure TCP server started!");
            Console.WriteLine(
                $"Advertised server IP: {GameNetworkConfig.ServerIp} " +
                $"(port 7001 channel, {string.Join("/", publicGamePorts)} game)");
            if (GameNetworkConfig.FreeDuelListenerEnabled)
            {
                Console.WriteLine(
                    $"[FreeDuel] CH.{GameNetworkConfig.FreeDuelChannelIndex} " +
                    $"listener bound on TCP {GameNetworkConfig.FreeDuelGamePort}.");
            }
            var interactiveConsole = Environment.UserInteractive && !Console.IsInputRedirected;
            Console.WriteLine(interactiveConsole
                ? "Press 's' for statistics, 'q' to quit."
                : "Running without interactive console. Stop the service to quit.");

            if (!interactiveConsole)
            {
                var stopped = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    stopped.Set();
                };
                AppDomain.CurrentDomain.ProcessExit += (sender, e) => stopped.Set();
                stopped.Wait();
            }
            else
            {
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);

                    if (key.KeyChar == 's' || key.KeyChar == 'S')
                    {
                        var stats = server.GetStatistics();
                        Console.WriteLine("\n=== Server Statistics ===");
                        Console.WriteLine($"Total Clients: {stats.TotalClients}");
                        foreach (var stat in stats.PortStats)
                        {
                            var config = portConfigs[stat.Key];
                            Console.WriteLine($"Port {stat.Key} ({config.structure.GetType().Name}): {stat.Value} clients");
                        }
                        Console.WriteLine("=========================\n");
                    }
                    else if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                    {
                        break;
                    }
                }
            }

            server.Stop();
            Game.Inventory.InventoryPersistenceService.SaveAllDirty();
            // 服务停止后不再产生常规业务日志，此时完成队列并等待后台写入结束，避免退出时丢失尾部日志。
            FileLogger.Shutdown(TimeSpan.FromSeconds(5));
            Console.WriteLine("Server stopped.");
        }
    }
}
