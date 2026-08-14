using System;
using System.IO;
using System.Linq;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.ExpertJob;
using DfoServer.Network.Parsers.ExpertJob;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class ExpertJobStoreSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== EXPERT_JOB_STORE selftest ===");
            var failures = 0;
            var capturedBody = new byte[]
            {
                0x00,
                0x01, 0x00, 0x00, 0x00, 0x30,
                0x01, 0x00, 0x00, 0x00,
                0xA5, 0x03,
                0x0A, 0x01,
                0xFF, 0xFF,
            };

            Check("captured create request parses", CreateExpertJobStoreRequest.TryParse(capturedBody, out var command), ref failures);
            Check("captured create request fields", command != null
                && command.Kind == ExpertJobStoreKind.DisjointMachine
                && command.NameBytes.SequenceEqual(new byte[] { 0x30 })
                && command.Cost == 1
                && command.PositionX == 933
                && command.PositionY == 266
                && command.Direction == -1, ref failures);
            Check("truncated request rejects", !CreateExpertJobStoreRequest.TryParse(capturedBody.Take(15).ToArray(), out _), ref failures);
            Check("trailing request bytes reject", !CreateExpertJobStoreRequest.TryParse(capturedBody.Concat(new byte[] { 0 }).ToArray(), out _), ref failures);
            var placementMap = MapFile.Parse(@"
[virtual movable area]
0 0 250 500
251 0 249 500
[/virtual movable area]
[NPC]
7 `[left]` 100 100 0
8 `[right]` 400 100 0
9 `[left]` 100 400 0
10 `[right]` 400 400 0
[/NPC]");
            Check("town map NPC placement fields parse",
                placementMap.NpcCount == 4
                && placementMap.Npcs.Count == 4
                && placementMap.Npcs[0].NpcId == 7
                && placementMap.Npcs[0].Direction == "[left]"
                && placementMap.Npcs[0].X == 100
                && placementMap.Npcs[0].Y == 100
                && placementMap.Npcs[3].NpcId == 10
                && placementMap.Npcs[3].Direction == "[right]",
                ref failures);
            Check("store placement rejects point outside movable area",
                ExpertJobStorePlacementValidator.Validate(
                    placementMap.VirtualMovableArea,
                    placementMap.Npcs,
                    501,
                    100) == ExpertJobStorePlacementValidator.ErrorUnavailablePoint,
                ref failures);
            Check("store placement ignores malformed negative movable area",
                ExpertJobStorePlacementValidator.Validate(
                    new[] { 100, 100, -1, 0 },
                    Array.Empty<MapNpcInfo>(),
                    100,
                    100) == ExpertJobStorePlacementValidator.ErrorUnavailablePoint,
                ref failures);
            Check("store placement rejects official NPC commercial zone",
                ExpertJobStorePlacementValidator.Validate(
                    placementMap.VirtualMovableArea,
                    placementMap.Npcs,
                    179,
                    249) == ExpertJobStorePlacementValidator.ErrorRestrictedCommercialZone,
                ref failures);
            Check("store placement keeps official commercial-zone boundary available",
                ExpertJobStorePlacementValidator.Validate(
                    placementMap.VirtualMovableArea,
                    placementMap.Npcs,
                    180,
                    250) == 0,
                ref failures);
            Check("captured enter request parses",
                EnterExpertJobStoreRequest.TryParse(new byte[] { 0xF1, 0x03 }, out var enterRequest)
                && enterRequest.OwnerUserId == 1009, ref failures);
            Check("short enter request rejects",
                !EnterExpertJobStoreRequest.TryParse(new byte[] { 0xF1 }, out _), ref failures);
            Check("trailing enter request rejects",
                !EnterExpertJobStoreRequest.TryParse(new byte[] { 0xF1, 0x03, 0x00 }, out _), ref failures);
            Check("captured empty close request accepts null body",
                CloseExpertJobStoreRequest.IsValid(null), ref failures);
            Check("empty close request accepts empty body",
                CloseExpertJobStoreRequest.IsValid(Array.Empty<byte>()), ref failures);
            Check("close request rejects trailing bytes",
                !CloseExpertJobStoreRequest.IsValid(new byte[] { 0x00 }), ref failures);
            Check("captured empty repair request accepts null body",
                RepairExpertJobStoreRequest.IsValid(null), ref failures);
            Check("empty repair request accepts empty body",
                RepairExpertJobStoreRequest.IsValid(Array.Empty<byte>()), ref failures);
            Check("repair request rejects trailing bytes",
                !RepairExpertJobStoreRequest.IsValid(new byte[] { 0x00 }), ref failures);
            Check("captured empty upgrade request accepts null body",
                UpgradeDisjointMachineRequest.IsValid(null), ref failures);
            Check("empty upgrade request accepts empty body",
                UpgradeDisjointMachineRequest.IsValid(Array.Empty<byte>()), ref failures);
            Check("upgrade request rejects trailing bytes",
                !UpgradeDisjointMachineRequest.IsValid(new byte[] { 0x00 }), ref failures);
            Check("captured machine disjoint request parses",
                DisjointMachineRequest.TryParse(
                    new byte[] { 0xF1, 0x03, 0x25, 0x00, 0x00 },
                    out var disjointRequest)
                && disjointRequest.OwnerUserId == 1009
                && disjointRequest.TargetSlotIndex == 37
                && disjointRequest.ItemSpace == InventoryListType.Main,
                ref failures);
            Check("machine disjoint request rejects trailing bytes",
                !DisjointMachineRequest.TryParse(
                    new byte[] { 0xF1, 0x03, 0x25, 0x00, 0x00, 0x00 },
                    out _),
                ref failures);
            Check("captured enchanter extraction request parses",
                ExpertJobExtractionRequest.TryParse(
                    new byte[] { 0x01, 0x03, 0x00, 0x00, 0x0C, 0x00 },
                    out var extractionRequest)
                && extractionRequest.ExtractorType == ExpertJobStateCodec.EnchanterType
                && extractionRequest.ExtractorSlotIndex == 3
                && extractionRequest.TargetListType == InventoryListType.Main
                && extractionRequest.TargetSlotIndex == 12,
                ref failures);
            Check("enchanter extraction request rejects trailing bytes",
                !ExpertJobExtractionRequest.TryParse(
                    new byte[] { 0x01, 0x03, 0x00, 0x00, 0x0C, 0x00, 0x00 },
                    out _),
                ref failures);
            Check("captured alchemist extraction request uses shared layout",
                ExpertJobExtractionRequest.TryParse(
                    new byte[] { 0x02, 0x03, 0x00, 0x00, 0x09, 0x00 },
                    out var alchemistExtractionRequest)
                && alchemistExtractionRequest.ExtractorType == ExpertJobStateCodec.AlchemistType
                && alchemistExtractionRequest.ExtractorSlotIndex == 3
                && alchemistExtractionRequest.TargetListType == InventoryListType.Main
                && alchemistExtractionRequest.TargetSlotIndex == 9,
                ref failures);
            var capturedEnchantRequest = new byte[]
            {
                0xEE, 0x03, 0x99, 0xD1, 0x98, 0x00, 0x02,
                0x00, 0x0A, 0x00, 0x00, 0xEB, 0x00,
            };
            Check("captured enchanter store use request parses",
                EnchanterStoreUseRequest.TryParse(capturedEnchantRequest, out var enchantRequest)
                && enchantRequest.OwnerUserId == 1006
                && enchantRequest.RecipeItemId == 10015129
                && enchantRequest.Mode == 2
                && enchantRequest.TargetListType == InventoryListType.Main
                && enchantRequest.TargetSlotIndex == 10
                && enchantRequest.CardListType == InventoryListType.Main
                && enchantRequest.CardSlotIndex == 235,
                ref failures);
            Check("enchanter store use request rejects trailing bytes",
                !EnchanterStoreUseRequest.TryParse(
                    capturedEnchantRequest.Concat(new byte[] { 0 }).ToArray(), out _),
                ref failures);
            var capturedBeadCraftRequest = new byte[]
            {
                0x99, 0xD1, 0x98, 0x00, 0x01, 0x00, 0xEF, 0x00,
            };
            Check("captured enchanter bead craft request parses",
                ExpertJobCompoundRequest.TryParse(
                    capturedBeadCraftRequest,
                    out var beadCraftRequest)
                && beadCraftRequest.RecipeItemId == 10015129
                && beadCraftRequest.RequestedCount == 1
                && beadCraftRequest.CardSlotIndex == 239,
                ref failures);
            var capturedProductCraftRequest = new byte[]
            {
                0xCD, 0xAC, 0x27, 0x00, 0x0A, 0x00, 0xFF, 0xFF,
            };
            Check("captured expert-job product craft request parses",
                ExpertJobCompoundRequest.TryParse(
                    capturedProductCraftRequest,
                    out var productCraftRequest)
                && productCraftRequest.RecipeItemId == 2600141
                && productCraftRequest.RequestedCount == 10
                && productCraftRequest.IsProductCraft,
                ref failures);
            Check("doll-controller extraction request uses shared layout",
                ExpertJobExtractionRequest.TryParse(
                    new byte[] { 0x04, 0x03, 0x00, 0x00, 0x0C, 0x00 },
                    out var dollExtractionRequest)
                && dollExtractionRequest.ExtractorType == ExpertJobStateCodec.DollControllerType
                && dollExtractionRequest.ExtractorSlotIndex == 3
                && dollExtractionRequest.TargetListType == InventoryListType.Main
                && dollExtractionRequest.TargetSlotIndex == 12,
                ref failures);
            Check("enchanter bead craft request rejects trailing bytes",
                !ExpertJobCompoundRequest.TryParse(
                    capturedBeadCraftRequest.Concat(new byte[] { 0 }).ToArray(),
                    out _),
                ref failures);

            var runtime = new ExpertJobStoreRuntimeService();
            var sessionId = Guid.NewGuid();
            var state = new DisjointMachineState
            {
                MachineGrade = 6,
                Endurance = 163,
            };
            Check("disjointer creates store", runtime.TryCreate(
                    sessionId,
                    990486,
                    321,
                    3,
                    1,
                    2,
                    false,
                    false,
                    command,
                    state,
                    out var store,
                    out var errorCode)
                && errorCode == 0
                && runtime.Count == 1, ref failures);
            Check("duplicate owner rejects", !runtime.TryCreate(
                    sessionId,
                    990486,
                    321,
                    3,
                    1,
                    2,
                    false,
                    false,
                    command,
                    state,
                    out _,
                    out errorCode)
                && errorCode == ExpertJobStoreRuntimeService.ErrorStoreBusy, ref failures);
            Check("owner lookup reports deployed store",
                runtime.HasStore(990486)
                && runtime.TryGetOwnedStore(sessionId, 990486, out var ownedStore)
                && ReferenceEquals(store, ownedStore), ref failures);

            var createBody = ExpertJobStorePacketBuilder.BuildCreateExpertJobNotification(store);
            var disjointStoreTail = 7 + store.NameBytes.Length;
            Check("disjoint store create notification field order",
                createBody.Length == disjointStoreTail + 11
                && createBody[0] == (byte)ExpertJobStoreKind.DisjointMachine
                && BitConverter.ToUInt16(createBody, 1) == 321
                && createBody[disjointStoreTail] == 1
                && createBody[disjointStoreTail + 1] == 2
                && BitConverter.ToInt16(createBody, disjointStoreTail + 2) == 933
                && BitConverter.ToInt16(createBody, disjointStoreTail + 4) == 266
                && BitConverter.ToInt32(createBody, disjointStoreTail + 6) == 1
                && createBody[disjointStoreTail + 10] == 1,
                ref failures);
            Check("success ack shape", CommonPacketBodyBuilder.BuildSuccessAck().SequenceEqual(new byte[] { 1 }), ref failures);
            Check("same-area owner uid resolves",
                runtime.TryGetStoreInArea(1, 2, 321, out var entered)
                && ReferenceEquals(store, entered), ref failures);
            Check("cross-area owner uid rejects",
                !runtime.TryGetStoreInArea(1, 3, 321, out _), ref failures);
            Check("unknown owner uid rejects",
                !runtime.TryGetStoreInArea(1, 2, 322, out _), ref failures);
            var visitorSessionId = Guid.NewGuid();
            Check("enter binds visitor session",
                runtime.TryEnter(visitorSessionId, 990487, 1, 2, 321, out var visitorStore)
                && ReferenceEquals(store, visitorStore)
                && runtime.TryGetEnteredStore(visitorSessionId, 990487, out visitorStore)
                && ReferenceEquals(store, visitorStore), ref failures);
            Check("unbound session cannot route machine disjoint",
                !runtime.TryGetEnteredStore(Guid.NewGuid(), 990487, out _), ref failures);
            var leavingVisitorSessionId = Guid.NewGuid();
            Check("visitor leave clears machine routing",
                runtime.TryEnter(leavingVisitorSessionId, 990488, 1, 2, 321, out _)
                && runtime.Leave(leavingVisitorSessionId)
                && !runtime.TryGetEnteredStore(leavingVisitorSessionId, 990488, out _),
                ref failures);
            Check("enter ack field order",
                ExpertJobStorePacketBuilder.BuildEnterSuccess(store).SequenceEqual(new byte[]
                {
                    0x01,
                    0x00,
                    0x06,
                    0x01, 0x00, 0x00, 0x00,
                    0xA3, 0x00, 0x00, 0x00,
                }), ref failures);
            Check("area projection includes store", runtime.GetStoresInArea(1, 2).Count == 1
                && runtime.GetStoresInArea(1, 3).Count == 0, ref failures);
            Check("wrong session cannot close", !runtime.TryClose(Guid.NewGuid(), 990486, out _)
                && runtime.Count == 1, ref failures);
            Check("session close removes store", runtime.TryCloseSession(sessionId, out var closed)
                && ReferenceEquals(store, closed)
                && ExpertJobStorePacketBuilder.BuildCloseNotification(closed.OwnerUserId)
                    .SequenceEqual(BitConverter.GetBytes((ushort)321))
                && runtime.Count == 0, ref failures);
            Check("closing owner clears visitor bindings",
                !runtime.TryGetEnteredStore(visitorSessionId, 990487, out _), ref failures);

            var otherProfession = new ExpertJobStoreCreateCommand
            {
                Kind = ExpertJobStoreKind.EnchantShop,
                NameBytes = new byte[] { 0x31 },
                Cost = 1,
            };
            Check("legacy store overload cannot create enchanter store", !runtime.TryCreate(
                    Guid.NewGuid(), 990487, 322, 1, 1, 2, false, false,
                    otherProfession, state, out _, out errorCode)
                && errorCode == ExpertJobStoreRuntimeService.ErrorInvalidState, ref failures);

            var enchanterState = new EnchanterStoreState
            {
                Endurance = 300,
                CardQualificationLevels = new byte[] { 0 },
            };
            Check("enchanter creates typed store", runtime.TryCreate(
                    Guid.NewGuid(), 990487, 322, ExpertJobStateCodec.EnchanterType,
                    1, 2, false, false, otherProfession, null, enchanterState,
                    out var enchanterStore, out errorCode)
                && errorCode == 0
                && enchanterStore.Kind == ExpertJobStoreKind.EnchantShop,
                ref failures);
            var enchanterCreateBody = ExpertJobStorePacketBuilder.BuildCreateExpertJobNotification(enchanterStore);
            var enchanterNameLength = enchanterStore.NameBytes.Length;
            var enchanterStoreTail = 7 + enchanterNameLength;
            Check("enchanter create notification field order",
                enchanterCreateBody.Length == 20 + enchanterNameLength
                && enchanterCreateBody[0] == (byte)ExpertJobStoreKind.EnchantShop
                && BitConverter.ToUInt16(enchanterCreateBody, 1) == 322
                && enchanterCreateBody[enchanterStoreTail] == enchanterStore.TownId
                && enchanterCreateBody[enchanterStoreTail + 1] == enchanterStore.AreaId
                && BitConverter.ToInt16(enchanterCreateBody, enchanterStoreTail + 2) == enchanterStore.PositionX
                && BitConverter.ToInt16(enchanterCreateBody, enchanterStoreTail + 4) == enchanterStore.PositionY
                && BitConverter.ToInt32(enchanterCreateBody, enchanterStoreTail + 6) == 1
                && enchanterCreateBody[enchanterStoreTail + 10] == 1
                && enchanterCreateBody[enchanterStoreTail + 11] == 1
                && enchanterCreateBody[enchanterStoreTail + 12] == 0,
                ref failures);
            Check("enchanter enter ack field order",
                ExpertJobStorePacketBuilder.BuildEnterSuccess(enchanterStore)
                    .SequenceEqual(new byte[] { 1, 3, 0x42, 0x01, 0x2C, 0x01, 0, 0 }),
                ref failures);
            runtime.TryCloseSession(enchanterStore.OwnerSessionId, out _);

            var extractionAckResult = new ExpertJobExtractionResult
            {
                TargetListType = InventoryListType.Main,
                TargetSlotIndex = 13,
            };
            extractionAckResult.Materials.Add(new ExpertJobExtractionMaterial
            {
                SlotIndex = 42,
                ItemTemplateId = 3038,
                Count = 2,
            });
            Check("enchanter extraction success ACK uses current-client result layout",
                ExpertJobExtractionPacketBuilder.BuildSuccess(extractionAckResult).SequenceEqual(new byte[]
                {
                    1, 0, 13, 0, 1,
                    42, 0, 0xDE, 0x0B, 0, 0, 2, 0, 0, 0,
                }),
                ref failures);

            Check("legacy blob decoder rejects truncated state",
                !ExpertJobStateCodec.TryDecodeLegacyBlob(
                    new byte[] { 0x00, 0x03, 0x07 },
                    out _,
                    out _),
                ref failures);
            RunPersistenceChecks(ref failures);
            Check("PVF disjointer initial endurance",
                DisjointMachineConfigProvider.InitialEndurance == 300, ref failures);
            var config = DisjointMachineConfigProvider.Config;
            Check("PVF disjointer machine limits",
                config.MaximumStoreCharge == 10000
                && config.BaseConst == 150
                && config.EnduranceReduceMin == 1
                && config.EnduranceReduceMax == 3
                && config.GainExpMin == 0
                && config.GainExpMax == 1
                && config.RepairRules.Count == 11
                && config.GetRepairRule(1)?.FullRepairCost == 10000
                && config.GetRepairRule(1)?.MaximumEndurance == 300
                && config.GetUpgradeCost(2) == 30000
                && config.GetMinimumCharacterLevel(2) == 20,
                ref failures);
            var enchanterConfig = EnchanterConfigProvider.Config;
            Check("PVF enchanter progression and extractor mappings",
                enchanterConfig.MaximumStoreCharge == 10000000
                && enchanterConfig.ExtractionBaseConst == 500
                && enchanterConfig.GetLevel(19) == 1
                && enchanterConfig.GetLevel(20) == 2
                && enchanterConfig.Extractors[2600482].RequiredExpertJobLevel == 1
                && enchanterConfig.Extractors[2600482].ExtractionIndex == 0
                && enchanterConfig.Extractors[2600485].RequiredExpertJobLevel == 7
                && enchanterConfig.Extractors[2600485].ExtractionIndex == 3
                && enchanterConfig.GetAutoLearnRecipeIds(0).SequenceEqual(new[] { 10015129 })
                && enchanterConfig.GetNewAutoLearnRecipeIds(0, 246).SequenceEqual(
                    new[] { 10015130 })
                && enchanterConfig.GetStoreSkillIds(0).SequenceEqual(new byte[] { 191 })
                && enchanterConfig.GetCardQualificationLevels(0).SequenceEqual(new byte[] { 0 })
                && enchanterConfig.GetCardQualificationLevels(246).SequenceEqual(new byte[] { 0, 1 })
                && enchanterConfig.GetCardQualificationLevels(585).SequenceEqual(new byte[] { 0, 1, 2 })
                && enchanterConfig.GetCardQualificationLevels(939).SequenceEqual(new byte[] { 0, 1, 2, 3 })
                && enchanterConfig.GetCardQualificationLevels(1067).SequenceEqual(new byte[] { 0, 1, 2, 3, 4 })
                && enchanterConfig.CardRecipesByItemId[10015129].Qualification == 0
                && enchanterConfig.CardRecipesByItemId[10015129].RequiredLevel == 1
                && enchanterConfig.CardRecipesByItemId[10015129].Materials.Count == 2
                && enchanterConfig.CardRecipesByItemId[10015129].Materials.Any(
                    material => material.ItemTemplateId == 3227 && material.Count == 5)
                && enchanterConfig.CardRecipesByItemId[10015129].Materials.Any(
                    material => material.ItemTemplateId == 3166 && material.Count == 20)
                && enchanterConfig.CardsByItemId[3619].Qualification == 0
                && enchanterConfig.BeadItemIdByCardItemId[3619] == 2600313
                && enchanterConfig.CardsByItemId[10015144].Qualification == 4
                && enchanterConfig.BeadItemIdByCardItemId[10015144] == 10015170
                && enchanterConfig.CardExperienceRulesByLevel[1].SuccessRates[0] == 100
                && enchanterConfig.CardExperienceRulesByLevel[1].MinimumExperienceGain == 3
                && enchanterConfig.CardExperienceRulesByLevel[1].MaximumExperienceGain == 6
                && enchanterConfig.RecipesByItemId[2600141].ProductItemId == 2610034
                && enchanterConfig.RecipesByItemId[2600141].RequiredLevel == 1
                && enchanterConfig.RecipesByItemId[2600141].MinimumExperienceGain == 0
                && enchanterConfig.RecipesByItemId[2600141].MaximumExperienceGain == 1,
                ref failures);
            var alchemistConfig = AlchemistConfigProvider.Config;
            Check("PVF alchemist recipes and extractor mappings",
                alchemistConfig.RecipeConfig.GetLevel(19) == 1
                && alchemistConfig.RecipeConfig.GetLevel(20) == 2
                && alchemistConfig.RecipeConfig.GetAutoLearnRecipeIds(0).SequenceEqual(
                    new[] { 2600149 })
                && alchemistConfig.RecipeConfig.RecipesByItemId[2600150].ProductItemId == 1116
                && alchemistConfig.RecipeConfig.RecipesByItemId[2600492].ProductItemId == 2600463
                && alchemistConfig.Extractors[2600463].RequiredExpertJobLevel == 1
                && alchemistConfig.Extractors[2600463].ExtractionIndex == 0
                && alchemistConfig.Extractors[2600547].RequiredExpertJobLevel == 11
                && alchemistConfig.Extractors[2600547].ExtractionIndex == 5
                && alchemistConfig.ExtractionRules.ContainsKey((2600463, 0, 0))
                && alchemistConfig.ExtractionRules.ContainsKey((2600547, 6, 1)),
                ref failures);
            var dollControllerConfig = DollControllerConfigProvider.Config;
            Check("PVF doll-controller recipes and extractor mappings",
                dollControllerConfig.RecipeConfig.GetLevel(19) == 1
                && dollControllerConfig.RecipeConfig.GetLevel(20) == 2
                && dollControllerConfig.RecipeConfig.GetAutoLearnRecipeIds(0).SequenceEqual(
                    new[] { 2600083 })
                && dollControllerConfig.RecipeConfig.RecipesByItemId[2600083].ProductItemId
                    == 2600029
                && dollControllerConfig.RecipeConfig.RecipesByItemId[2600502].ProductItemId
                    == 2600474
                && dollControllerConfig.Extractors[2600474].RequiredExpertJobLevel == 1
                && dollControllerConfig.Extractors[2600474].ExtractionIndex == 0
                && dollControllerConfig.Extractors[2600549].RequiredExpertJobLevel == 11
                && dollControllerConfig.Extractors[2600549].ExtractionIndex == 5
                && dollControllerConfig.ExtractionRules.ContainsKey((2600474, 0, 0))
                && dollControllerConfig.ExtractionRules.ContainsKey((2600549, 6, 1)),
                ref failures);
            Check("PVF disjointer experience thresholds map to one-based level",
                config.GetExpertJobLevel(19) == 1
                && config.GetExpertJobLevel(20) == 2,
                ref failures);
            Check("PVF disjointer grade-zero rarity rule",
                config.GetResult(0, 0, 0) is DisjointMachineResultRule rule
                && rule.ItemId == 3037
                && Math.Abs(rule.Multiplier - 0.72d) < 0.0001d, ref failures);
            Check("PVF disjointer preserves all equipment-state result rows",
                config.GetResult(3, 3, 0) != null
                && config.GetResult(3, 3, 1) != null
                && config.GetResult(3, 3, 2) != null,
                ref failures);
            Check("player disjoint additional table uses equipment grade boundaries",
                ExpertJobSelectionRuleSelector.Select(
                    new[]
                    {
                        new DisjointMachineSelectionRule
                        {
                            MinimumLevel = 0,
                            MaximumLevel = 66,
                            ItemId = 1,
                            Weight = 10000,
                        },
                        new DisjointMachineSelectionRule
                        {
                            MinimumLevel = 67,
                            MaximumLevel = 99,
                            ItemId = 2,
                            Weight = 10000,
                        },
                    },
                    67)?.ItemId == 2,
                ref failures);
            var unidentifiedRuleSource = new ItemCore { AmplifyType = 0x80 };
            var epicMetadata = new ItemMetadata { Rarity = 4 };
            Check("current PVF starts unidentified epic disjoint at machine grade four",
                DisjointMachineResultCalculator.ResolveRule(
                    unidentifiedRuleSource,
                    epicMetadata,
                    3) == null
                && ReferenceEquals(
                    DisjointMachineResultCalculator.ResolveRule(
                        unidentifiedRuleSource,
                        epicMetadata,
                        4),
                    config.GetResult(3, 4, 1)),
                ref failures);
            var chronicleRuleSource = new ItemCore();
            chronicleRuleSource.ChronicleOption0.OptionId = 1;
            Check("chronicle equipment selects PVF state two result",
                ReferenceEquals(
                    DisjointMachineResultCalculator.ResolveRule(
                        chronicleRuleSource,
                        new ItemMetadata { Rarity = 3 },
                        1),
                    config.GetResult(0, 3, 2)),
                ref failures);

            const int unidentifiedEpicEquipmentId = 27769;
            var unidentifiedEpicMetadata = ItemMetadataResolver.Resolve(unidentifiedEpicEquipmentId);
            Check("unidentified epic fixture resolves from current PVF",
                unidentifiedEpicMetadata != null
                && unidentifiedEpicMetadata.ItemKind == "equipment"
                && unidentifiedEpicMetadata.Rarity == 4,
                ref failures);
            var systemDisjointInventory = CreateUnidentifiedEquipmentInventory(
                990510,
                unidentifiedEpicEquipmentId,
                71001);
            Check("system disjoint keeps unidentified equipment rejected",
                !InventoryDisjointService.TryDisjointItem(
                    systemDisjointInventory,
                    CreateDisjointRequest(37),
                    out var systemRejectedResult)
                && systemRejectedResult.ErrorCode == DisjointItemResult.ErrorInvalidTarget
                && systemRejectedResult.InventoryMutations.Count == 0
                && systemDisjointInventory.GetItem(InventoryListType.Main, 37)?.AmplifyType == 0x80,
                ref failures);

            var lowGradeInventory = CreateUnidentifiedEquipmentInventory(
                990511,
                unidentifiedEpicEquipmentId,
                71002);
            var lowGradeStore = CreateOperationStore(lowGradeInventory.CharacterId, 3);
            Check("player machine below PVF qualification returns machine grade error without mutation",
                !DisjointMachineService.TryDisjoint(
                    lowGradeInventory,
                    lowGradeInventory,
                    lowGradeStore,
                    37,
                    int.MaxValue,
                    out var lowGradeResult)
                && lowGradeResult.ErrorCode == DisjointMachineService.ErrorMachineGradeTooLow
                && ExpertJobStorePacketBuilder.BuildDisjointError(lowGradeResult.ErrorCode)
                    .SequenceEqual(new byte[] { 0, 0xD4 })
                && lowGradeInventory.GetItem(InventoryListType.Main, 37)?.AmplifyType == 0x80
                && lowGradeStore.DisjointMachine.Endurance == 300,
                ref failures);

            var qualifiedInventory = CreateUnidentifiedEquipmentInventory(
                990512,
                unidentifiedEpicEquipmentId,
                71003);
            var qualifiedStore = CreateOperationStore(qualifiedInventory.CharacterId, 4);
            Check("player machine at PVF qualification disjoints unidentified epic",
                DisjointMachineService.TryDisjoint(
                    qualifiedInventory,
                    qualifiedInventory,
                    qualifiedStore,
                    37,
                    int.MaxValue,
                    out var qualifiedResult)
                && qualifiedResult.ErrorCode == 0
                && qualifiedInventory.GetItem(InventoryListType.Main, 37) == null
                && qualifiedResult.DisjointResult.Materials.Any(material =>
                    material.ItemTemplateId == 10088692
                    && material.Count == 6)
                && qualifiedResult.DisjointResult.InventoryMutations.Any(mutation =>
                    mutation.ItemTemplateId == unidentifiedEpicEquipmentId)
                && qualifiedResult.DisjointResult.InventoryMutations.Any(mutation =>
                    mutation.ItemTemplateId == 10088692)
                && qualifiedStore.DisjointMachine.Endurance < 300,
                ref failures);

            const int testEquipmentId = 33000;
            var testMetadata = ItemMetadataResolver.Resolve(testEquipmentId);
            Check("machine disjoint test equipment resolves from current PVF",
                testMetadata != null && testMetadata.ItemKind == "equipment", ref failures);
            var equipmentSoulRule = DisjointConfigProvider.LoadSystemDisjoint()
                .GetExpandResult(testMetadata?.Rarity ?? -1);
            var equipmentSoulItemId = equipmentSoulRule?.ItemTemplateId ?? 0;
            var equipmentSoulResults =
                DisjointResultCalculator.CalculateEquipmentSoulResults(testMetadata);
            Check("shared disjoint layer contains only the PVF equipment soul",
                equipmentSoulRule != null
                && equipmentSoulRule.Enabled
                && equipmentSoulResults.Count == 1
                && equipmentSoulResults[0].ItemTemplateId == equipmentSoulItemId,
                ref failures);
            var extractionInventory = new InventoryService(990513, 990513);
            extractionInventory.SetItem(InventoryListType.Main, 3, new ItemCore
            {
                ItemId = 2600482,
                Uid = 71004,
                Value = 1,
            });
            extractionInventory.SetItem(InventoryListType.Main, 12, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = testEquipmentId,
                Uid = 71005,
            });
            extractionInventory.ClearDirtyState();
            Check("level-one enchanter extraction consumes equipment and grants PVF materials",
                ExpertJobExtractionService.TryExtract(
                    extractionInventory,
                    new ExpertJobExtractionCommand
                    {
                        ExtractorType = ExpertJobStateCodec.EnchanterType,
                        ExtractorSlotIndex = 3,
                        TargetListType = InventoryListType.Main,
                        TargetSlotIndex = 12,
                    },
                    0,
                    enchanterConfig,
                    out var extractionResult)
                && extractionResult.ErrorCode == 0
                && extractionInventory.GetItem(InventoryListType.Main, 3)?.ItemId == 2600482
                && extractionInventory.GetItem(InventoryListType.Main, 12) == null
                && extractionResult.Materials.Count > 0
                && extractionResult.Materials.All(material =>
                    material.ItemTemplateId > 0
                    && material.Count > 0
                    && extractionInventory.CountMainItem(material.ItemTemplateId) >= material.Count
                    && extractionResult.InventoryMutations.Any(mutation =>
                        mutation.ItemTemplateId == material.ItemTemplateId))
                && extractionResult.InventoryMutations.Any(mutation =>
                    mutation.ItemTemplateId == testEquipmentId),
                ref failures);
            var alchemistExtractionInventory = new InventoryService(990514, 990514);
            alchemistExtractionInventory.SetItem(InventoryListType.Main, 3, new ItemCore
            {
                ItemId = 2600463,
                Uid = 71010,
                Value = 1,
            });
            alchemistExtractionInventory.SetItem(InventoryListType.Main, 12, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = testEquipmentId,
                Uid = 71011,
            });
            alchemistExtractionInventory.ClearDirtyState();
            Check("level-one alchemist extractor consumes equipment and grants PVF material",
                ExpertJobExtractionService.TryExtract(
                    alchemistExtractionInventory,
                    new ExpertJobExtractionCommand
                    {
                        ExtractorType = ExpertJobStateCodec.AlchemistType,
                        ExtractorSlotIndex = 3,
                        TargetListType = InventoryListType.Main,
                        TargetSlotIndex = 12,
                    },
                    0,
                    alchemistConfig,
                    out var alchemistExtractionResult)
                && alchemistExtractionResult.ErrorCode == 0
                && alchemistExtractionInventory.GetItem(InventoryListType.Main, 3)?.ItemId == 2600463
                && alchemistExtractionInventory.GetItem(InventoryListType.Main, 12) == null
                && alchemistExtractionResult.Materials.Any(material =>
                    material.ItemTemplateId == 2610024
                    && material.Count > 0
                    && alchemistExtractionInventory.CountMainItem(2610024) >= material.Count),
                ref failures);
            var dollExtractionInventory = new InventoryService(990517, 990517);
            dollExtractionInventory.SetItem(InventoryListType.Main, 3, new ItemCore
            {
                ItemId = 2600474,
                Uid = 71013,
                Value = 1,
            });
            dollExtractionInventory.SetItem(InventoryListType.Main, 12, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = testEquipmentId,
                Uid = 71014,
            });
            dollExtractionInventory.ClearDirtyState();
            Check("level-one doll-controller extractor consumes equipment and grants PVF material",
                ExpertJobExtractionService.TryExtract(
                    dollExtractionInventory,
                    new ExpertJobExtractionCommand
                    {
                        ExtractorType = ExpertJobStateCodec.DollControllerType,
                        ExtractorSlotIndex = 3,
                        TargetListType = InventoryListType.Main,
                        TargetSlotIndex = 12,
                    },
                    0,
                    dollControllerConfig,
                    out var dollExtractionResult)
                && dollExtractionResult.ErrorCode == 0
                && dollExtractionInventory.GetItem(InventoryListType.Main, 3)?.ItemId == 2600474
                && dollExtractionInventory.GetItem(InventoryListType.Main, 12) == null
                && dollExtractionResult.Materials.Count > 0
                && dollExtractionResult.Materials.All(material =>
                    material.ItemTemplateId > 0
                    && material.Count > 0
                    && dollExtractionInventory.CountMainItem(material.ItemTemplateId)
                        >= material.Count),
                ref failures);
            var alchemistRecipeInventory = new InventoryService(990516, 990516);
            alchemistRecipeInventory.SetItem(InventoryListType.Main, 15, new ItemCore
            {
                ItemId = 2600492,
                Uid = 71012,
                Count = 1,
            });
            var alchemistRecipeState = new ExpertJobState();
            var learnedAlchemistRecipe = ExpertJobRecipeLearningService.TryLearn(
                alchemistRecipeInventory,
                InventoryListType.Main,
                15,
                2600492,
                0,
                alchemistRecipeState,
                alchemistConfig.RecipeConfig);
            var alchemistRecipeInfo = ExpertJobInfoBodyBuilder.BuildProjectedBody(
                ExpertJobStateCodec.AlchemistType,
                alchemistRecipeState,
                0);
            Check("alchemist design learns canonical recipe and projects mode-two refresh",
                learnedAlchemistRecipe.Handled
                && learnedAlchemistRecipe.Success
                && learnedAlchemistRecipe.RecipeId == 2600492
                && alchemistRecipeInventory.GetItem(InventoryListType.Main, 15) == null
                && alchemistRecipeState.LearnedRecipeIds.SequenceEqual(new[] { 2600492 })
                && alchemistRecipeInfo.Length == 7
                && alchemistRecipeInfo[0] == 0
                && alchemistRecipeInfo[1] == ExpertJobStateCodec.AlchemistType
                && alchemistRecipeInfo[2] == 1
                && BitConverter.ToInt32(alchemistRecipeInfo, 3) == 2600492,
                ref failures);
            var dollRecipeInventory = new InventoryService(990518, 990518);
            dollRecipeInventory.SetItem(InventoryListType.Main, 15, new ItemCore
            {
                ItemId = 2600502,
                Uid = 71015,
                Count = 1,
            });
            var dollRecipeState = new ExpertJobState();
            var learnedDollRecipe = ExpertJobRecipeLearningService.TryLearn(
                dollRecipeInventory,
                InventoryListType.Main,
                15,
                2600502,
                0,
                dollRecipeState,
                dollControllerConfig.RecipeConfig);
            var dollRecipeInfo = ExpertJobInfoBodyBuilder.BuildProjectedBody(
                ExpertJobStateCodec.DollControllerType,
                dollRecipeState,
                0);
            Check("doll-controller design learns canonical recipe and projects mode-four refresh",
                learnedDollRecipe.Handled
                && learnedDollRecipe.Success
                && learnedDollRecipe.RecipeId == 2600502
                && dollRecipeInventory.GetItem(InventoryListType.Main, 15) == null
                && dollRecipeState.LearnedRecipeIds.SequenceEqual(new[] { 2600502 })
                && dollRecipeInfo.Length == 7
                && dollRecipeInfo[0] == 0
                && dollRecipeInfo[1] == ExpertJobStateCodec.DollControllerType
                && dollRecipeInfo[2] == 1
                && BitConverter.ToInt32(dollRecipeInfo, 3) == 2600502,
                ref failures);
            var recipeInventory = new InventoryService(990515, 990515);
            recipeInventory.SetItem(InventoryListType.Main, 15, new ItemCore
            {
                ItemId = 2600512,
                Uid = 71006,
                Count = 1,
            });
            var recipeLearningState = new ExpertJobState();
            var learnedRecipe = ExpertJobRecipeLearningService.TryLearn(
                recipeInventory,
                InventoryListType.Main,
                15,
                2600512,
                0,
                recipeLearningState,
                enchanterConfig.RecipeConfig);
            Check("enchanter recipe item learns its canonical recipe id and consumes design",
                learnedRecipe.Handled
                && learnedRecipe.Success
                && learnedRecipe.RecipeId == 2600512
                && recipeLearningState.LearnedRecipeIds.SequenceEqual(new[] { 2600512 })
                && recipeInventory.GetItem(InventoryListType.Main, 15) == null,
                ref failures);
            recipeInventory.SetItem(InventoryListType.Main, 16, new ItemCore
            {
                ItemId = 2600512,
                Uid = 71007,
                Count = 1,
            });
            var repeatedRecipe = ExpertJobRecipeLearningService.TryLearn(
                recipeInventory,
                InventoryListType.Main,
                16,
                2600512,
                0,
                recipeLearningState,
                enchanterConfig.RecipeConfig);
            Check("already learned enchanter recipe consumes duplicate design without duplicating state",
                repeatedRecipe.Handled
                && repeatedRecipe.Success
                && repeatedRecipe.RecipeId == 2600512
                && recipeLearningState.LearnedRecipeIds.SequenceEqual(new[] { 2600512 })
                && recipeInventory.GetItem(InventoryListType.Main, 16) == null,
                ref failures);
            recipeInventory.SetItem(InventoryListType.Main, 17, new ItemCore
            {
                ItemId = 2600513,
                Uid = 71008,
                Count = 1,
            });
            var offsetBoundaryRecipe = ExpertJobRecipeLearningService.TryLearn(
                recipeInventory,
                InventoryListType.Main,
                17,
                2600513,
                0,
                recipeLearningState,
                enchanterConfig.RecipeConfig);
            Check("level-one enchanter learns a level-three design at the official offset boundary",
                offsetBoundaryRecipe.Handled
                && offsetBoundaryRecipe.Success
                && offsetBoundaryRecipe.RecipeId == 2600513
                && recipeLearningState.LearnedRecipeIds.SequenceEqual(
                    new[] { 2600512, 2600513 })
                && recipeInventory.GetItem(InventoryListType.Main, 17) == null,
                ref failures);
            recipeInventory.SetItem(InventoryListType.Main, 18, new ItemCore
            {
                ItemId = 2600514,
                Uid = 71009,
                Count = 1,
            });
            var levelRejectedRecipe = ExpertJobRecipeLearningService.TryLearn(
                recipeInventory,
                InventoryListType.Main,
                18,
                2600514,
                20,
                recipeLearningState,
                enchanterConfig.RecipeConfig);
            Check("level-two enchanter rejects a level-five design without mutation",
                levelRejectedRecipe.Handled
                && !levelRejectedRecipe.Success
                && levelRejectedRecipe.ErrorCode ==
                    ExpertJobRecipeLearningService.ErrorLevelTooLow
                && !recipeLearningState.LearnedRecipeIds.Contains(
                    levelRejectedRecipe.RecipeId)
                && recipeInventory.GetItem(InventoryListType.Main, 18)?.ItemId == 2600514,
                ref failures);
            Check("enchanter recipe level rejection preserves use-item error context",
                UseStackableAckBuilder.BuildError(
                        levelRejectedRecipe.ErrorCode,
                        (byte)InventoryListType.Main,
                        0x606C,
                        2600514)
                    .SequenceEqual(new byte[]
                    {
                        0,
                        0x0E,
                        0,
                        0x6C, 0x60, 0, 0,
                        0x42, 0xAE, 0x27, 0,
                    }),
                ref failures);
            var selfInventory = new InventoryService(990500, 990500);
            selfInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                5000);
            selfInventory.SetItem(InventoryListType.Main, 37, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = testEquipmentId,
                Uid = 70001,
            });
            selfInventory.ClearDirtyState();
            var operationStore = new ExpertJobStoreSession
            {
                OwnerCharacterId = selfInventory.CharacterId,
                Kind = ExpertJobStoreKind.DisjointMachine,
                DisjointMachine = new DisjointMachineState
                {
                    MachineGrade = 1,
                    Endurance = 300,
                },
                Cost = 100,
            };
            var operationSucceeded = DisjointMachineService.TryDisjoint(
                    selfInventory,
                    selfInventory,
                    operationStore,
                    37,
                    int.MaxValue,
                    out var operationResult);
            var operationStateMatches = operationSucceeded
                && operationResult.ErrorCode == 0
                && selfInventory.GetItem(InventoryListType.Main, 37) == null
                && operationResult.DisjointResult.Materials.Count > 0
                && operationResult.DisjointResult.Materials.All(material =>
                    selfInventory.CountMainItem(material.ItemTemplateId) >= material.Count)
                && operationResult.DisjointResult.Materials.Any(material =>
                    material.ItemTemplateId == equipmentSoulItemId)
                && operationResult.DisjointResult.InventoryMutations.Any(mutation =>
                    mutation.ItemTemplateId == equipmentSoulItemId)
                && operationResult.RequesterGold == 5000
                && operationResult.Endurance >= 297
                && operationResult.Endurance <= 299;
            Check("self machine disjoint mutates inventory and endurance",
                operationStateMatches, ref failures);

            var poorRequester = new InventoryService(990501, 990501);
            poorRequester.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                50);
            poorRequester.SetItem(InventoryListType.Main, 37, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = testEquipmentId,
                Uid = 70002,
            });
            var ownerInventory = new InventoryService(990502, 990502);
            ownerInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                1000);
            var paidStore = new ExpertJobStoreSession
            {
                OwnerCharacterId = ownerInventory.CharacterId,
                Kind = ExpertJobStoreKind.DisjointMachine,
                DisjointMachine = new DisjointMachineState
                {
                    MachineGrade = 1,
                    Endurance = 300,
                },
                Cost = 100,
            };
            Check("insufficient fee rejects without inventory mutation",
                !DisjointMachineService.TryDisjoint(
                    poorRequester,
                    ownerInventory,
                    paidStore,
                    37,
                    int.MaxValue,
                    out var rejectedOperation)
                && rejectedOperation.ErrorCode == DisjointMachineService.ErrorInsufficientGold
                && poorRequester.GetItem(InventoryListType.Main, 37)?.ItemId == testEquipmentId
                && poorRequester.GetMainVirtualCount(0)?.Count == 50
                && ownerInventory.GetMainVirtualCount(0)?.Count == 1000
                && paidStore.DisjointMachine.Endurance == 300,
                ref failures);

            RunEnchanterStoreUseChecks(ref failures);
            RunExpertJobCompoundChecks(ref failures);

            var initSnapshot = new SelectCharacterDataSnapshot();
            ExpertJobStateCodec.ProjectToSnapshot(
                ExpertJobStateCodec.DisjointerType,
                new ExpertJobState
                {
                    DisjointMachine = new DisjointMachineState
                    {
                        MachineGrade = 1,
                        Endurance = 300,
                    },
                },
                initSnapshot.InitializationSnapshot.ExpertJobInfo);
            Check("expert-job initialization packet keeps client one-based grade",
                new ExpertJobInfoBodyBuilder().TryBuild(initSnapshot, 0, out var expertJobInfoBody)
                && expertJobInfoBody.SequenceEqual(new byte[]
                {
                    0, 3,
                    1, 0, 0, 0,
                    0x2C, 0x01, 0, 0,
                }),
                ref failures);
            var recipeState = new ExpertJobState
            {
                GiveUpCount = 1,
            };
            recipeState.LearnedRecipeIds.Add(51001);
            recipeState.LearnedRecipeIds.Add(2600512);
            recipeState.EnchanterMachine = new EnchanterMachineState { Endurance = 300 };
            var recipeSnapshot = new ExpertJobInfoSnapshot();
            ExpertJobStateCodec.ProjectToSnapshot(1, recipeState, recipeSnapshot);
            Check("recipe profession projects normalized domain state",
                recipeSnapshot.State0 == 1
                && recipeSnapshot.Mode == 1
                && recipeSnapshot.Entries.SequenceEqual(new[] { 51001, 2600512 })
                && recipeSnapshot.CardQualificationLevels.SequenceEqual(new byte[] { 0 })
                && recipeSnapshot.EnchanterLevel == 1
                && recipeSnapshot.EnchanterEndurance == 300
                && recipeSnapshot.DisjointMachineGrade == 0
                && recipeSnapshot.DisjointMachineEndurance == 0,
                ref failures);
            var recipeInit = new SelectCharacterDataSnapshot();
            recipeInit.InitializationSnapshot.ExpertJobInfo = recipeSnapshot;
            Check("enchanter initialization writes recipes card qualifications level and endurance",
                new ExpertJobInfoBodyBuilder().TryBuild(recipeInit, 0, out var recipeBody)
                && recipeBody.Length == 21
                && recipeBody[0] == 1
                && recipeBody[1] == 1
                && recipeBody[2] == 2
                && BitConverter.ToInt32(recipeBody, 3) == 51001
                && BitConverter.ToInt32(recipeBody, 7) == 2600512
                && recipeBody[11] == 1
                && recipeBody[12] == 0
                && BitConverter.ToInt32(recipeBody, 13) == 1
                && BitConverter.ToInt32(recipeBody, 17) == 300,
                ref failures);

            var enchanterRepairInventory = new InventoryService(990514, 990514);
            enchanterRepairInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart, 0, 5000);
            var enchanterRepairState = new EnchanterMachineState { Endurance = 297 };
            Check("enchanter repair uses current PVF level rule",
                EnchanterMachineRepairService.TryRepair(
                    enchanterRepairInventory,
                    enchanterRepairState,
                    0,
                    out var enchanterRepair)
                && enchanterRepair.Cost == 100
                && enchanterRepair.Endurance == 300
                && enchanterRepair.Gold == 4900,
                ref failures);

            var repairInventory = new InventoryService(990503, 990503);
            repairInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                5000);
            repairInventory.ClearDirtyState();
            var repairState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 297,
            };
            Check("level-one repair uses current-client proportional PVF cost",
                DisjointMachineRepairService.TryRepair(
                    repairInventory,
                    repairState,
                    out var repairResult)
                && repairResult.Cost == 100
                && repairResult.Gold == 4900
                && repairResult.Endurance == 300
                && repairState.Endurance == 300
                && repairInventory.GetMainVirtualCount(0)?.Count == 4900,
                ref failures);
            Check("full endurance machine rejects repair",
                !DisjointMachineRepairService.TryRepair(
                    repairInventory,
                    repairState,
                    out var fullRepairResult)
                && fullRepairResult.ErrorCode == DisjointMachineRepairService.ErrorCannotRepair,
                ref failures);
            var partialRepairInventory = new InventoryService(990508, 990508);
            partialRepairInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                99);
            var partialRepairState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 297,
            };
            Check("insufficient full-repair gold buys official partial repair units",
                DisjointMachineRepairService.TryRepair(
                    partialRepairInventory,
                    partialRepairState,
                    out var partialRepairResult)
                && partialRepairResult.Cost == 99
                && partialRepairResult.Gold == 0
                && partialRepairResult.Endurance == 300
                && partialRepairState.Endurance == 300,
                ref failures);
            Check("repair notification field order",
                ExpertJobStorePacketBuilder.BuildRepairNotification(4900, 300)
                    .SequenceEqual(new byte[]
                    {
                        1,
                        0x24, 0x13, 0, 0,
                        0x2C, 0x01, 0, 0,
                    }),
                ref failures);

            var upgradeInventory = new InventoryService(990504, 990504);
            upgradeInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                50000);
            upgradeInventory.ClearDirtyState();
            var upgradeState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 300,
            };
            Check("level-one machine upgrades from current PVF rules",
                DisjointMachineUpgradeService.TryUpgrade(
                    upgradeInventory,
                    upgradeState,
                    20,
                    85,
                    out var upgradeResult)
                && upgradeResult.Cost == 30000
                && upgradeResult.Gold == 20000
                && upgradeResult.Grade == 2
                && upgradeResult.Endurance == 300
                && upgradeState.MachineGrade == 2
                && upgradeState.Endurance == 300
                && upgradeInventory.GetMainVirtualCount(0)?.Count == 20000,
                ref failures);

            var wornUpgradeInventory = new InventoryService(990505, 990505);
            wornUpgradeInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                50000);
            var wornUpgradeState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 299,
            };
            Check("worn machine rejects upgrade without mutation",
                !DisjointMachineUpgradeService.TryUpgrade(
                    wornUpgradeInventory,
                    wornUpgradeState,
                    20,
                    85,
                    out var wornUpgradeResult)
                && wornUpgradeResult.ErrorCode == DisjointMachineUpgradeService.ErrorCannotUpgrade
                && wornUpgradeState.MachineGrade == 1
                && wornUpgradeState.Endurance == 299
                && wornUpgradeInventory.GetMainVirtualCount(0)?.Count == 50000,
                ref failures);

            var inexperiencedUpgradeInventory = new InventoryService(990506, 990506);
            inexperiencedUpgradeInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                50000);
            var inexperiencedUpgradeState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 300,
            };
            Check("insufficient expert-job experience rejects upgrade",
                !DisjointMachineUpgradeService.TryUpgrade(
                    inexperiencedUpgradeInventory,
                    inexperiencedUpgradeState,
                    19,
                    85,
                    out var inexperiencedUpgradeResult)
                && inexperiencedUpgradeResult.ErrorCode == DisjointMachineUpgradeService.ErrorCannotUpgrade
                && inexperiencedUpgradeState.MachineGrade == 1
                && inexperiencedUpgradeInventory.GetMainVirtualCount(0)?.Count == 50000,
                ref failures);

            var lowLevelUpgradeInventory = new InventoryService(990509, 990509);
            lowLevelUpgradeInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                50000);
            var lowLevelUpgradeState = new DisjointMachineState
            {
                MachineGrade = 2,
                Endurance = 300,
            };
            Check("character level limit returns official upgrade error 14",
                !DisjointMachineUpgradeService.TryUpgrade(
                    lowLevelUpgradeInventory,
                    lowLevelUpgradeState,
                    133,
                    29,
                    out var lowLevelUpgradeResult)
                && lowLevelUpgradeResult.ErrorCode
                    == DisjointMachineUpgradeService.ErrorCharacterLevelTooLow
                && lowLevelUpgradeState.MachineGrade == 2
                && lowLevelUpgradeInventory.GetMainVirtualCount(0)?.Count == 50000,
                ref failures);

            var poorUpgradeInventory = new InventoryService(990507, 990507);
            poorUpgradeInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                29999);
            var poorUpgradeState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 300,
            };
            Check("insufficient gold returns client upgrade error 22",
                !DisjointMachineUpgradeService.TryUpgrade(
                    poorUpgradeInventory,
                    poorUpgradeState,
                    20,
                    85,
                    out var poorUpgradeResult)
                && poorUpgradeResult.ErrorCode == DisjointMachineUpgradeService.ErrorInsufficientGold
                && poorUpgradeState.MachineGrade == 1
                && poorUpgradeInventory.GetMainVirtualCount(0)?.Count == 29999,
                ref failures);
            Check("upgrade notification field order",
                ExpertJobStorePacketBuilder.BuildUpgradeNotification(20000, 2, 300)
                    .SequenceEqual(new byte[]
                    {
                        1,
                        0x20, 0x4E, 0, 0,
                        2, 0, 0, 0,
                        0x2C, 0x01, 0, 0,
                    }),
                ref failures);

            var packetResult = new DisjointMachineOperationResult
            {
                RequesterGold = 1234,
                OwnerGold = 1234,
                Endurance = 297,
                DisjointResult = new DisjointItemResult
                {
                    Request = new DisjointItemRequest
                    {
                        TargetSlotIndex = 37,
                        ItemSpace = InventoryListType.Main,
                    },
                },
            };
            packetResult.DisjointResult.Materials.Add(new DisjointMaterialResult
            {
                SlotIndex = 121,
                ItemTemplateId = 3037,
                Count = 5,
            });
            Check("machine disjoint success ack appends gold and endurance",
                ExpertJobStorePacketBuilder.BuildDisjointSuccess(packetResult).SequenceEqual(new byte[]
                {
                    1,
                    0x25, 0,
                    0,
                    1,
                    0x79, 0,
                    0xDD, 0x0B, 0, 0,
                    5, 0, 0, 0,
                    0xD2, 0x04, 0, 0,
                    0x29, 0x01, 0, 0,
                }), ref failures);
            Check("owner machine notification field order",
                ExpertJobStorePacketBuilder.BuildOwnerDisjointNotification(1234, 297)
                    .SequenceEqual(new byte[]
                    {
                        1,
                        0xD2, 0x04, 0, 0,
                        0x29, 0x01, 0, 0,
                    }), ref failures);
            Check("owner enchanter notification field order",
                ExpertJobStorePacketBuilder.BuildOwnerEnchantNotification(1234, 297)
                    .SequenceEqual(new byte[]
                    {
                        0xD2, 0x04, 0, 0,
                        0x29, 0x01, 0, 0,
                    }),
                ref failures);
            Check("enchanter success ack includes result experience and endurance",
                ExpertJobStorePacketBuilder.BuildEnchantSuccess(
                    new EnchanterStoreUseResult
                    {
                        EnchantSucceeded = true,
                        FinalExperience = 0x01020304,
                        Endurance = 297,
                    }).SequenceEqual(new byte[]
                    {
                        1,
                        1,
                        4, 3, 2, 1,
                        0,
                        0x29, 0x01, 0, 0,
                    }),
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string label, bool ok, ref int failures)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok)
                failures++;
        }

        private static void RunEnchanterStoreUseChecks(ref int failures)
        {
            const int targetEquipmentId = 18003;
            var targetMetadata = ItemMetadataResolver.Resolve(targetEquipmentId);
            Check("initial task card test target is current-PVF shoes",
                targetMetadata?.ItemKind == "equipment"
                && ItemMetadataResolver.ResolveEquipmentType(targetEquipmentId) == "[shoes]",
                ref failures);
            Check("current-PVF unbindable epic card accepts its declared weapon target",
                ItemMetadataResolver.TryValidateMonsterCardTarget(
                    10015144,
                    101040019,
                    0,
                    out _),
                ref failures);

            var requester = new InventoryService(990520, 990520);
            requester.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart, 0, 5000);
            requester.SetItem(InventoryListType.Main, 10, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = targetEquipmentId,
                Uid = 72001,
                Durability = 12,
            });
            requester.SetItem(InventoryListType.Main, 235, new ItemCore
            {
                ItemId = 3619,
                Count = 1,
            });
            requester.ClearDirtyState();
            var owner = new InventoryService(990521, 990521);
            owner.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart, 0, 1000);
            owner.ClearDirtyState();
            var store = new ExpertJobStoreSession
            {
                OwnerCharacterId = owner.CharacterId,
                OwnerUserId = 1006,
                Kind = ExpertJobStoreKind.EnchantShop,
                Cost = 100,
                Enchanter = new EnchanterStoreState
                {
                    Endurance = 300,
                    CardQualificationLevels = new byte[] { 0 },
                },
            };
            var command = new EnchanterStoreUseCommand
            {
                OwnerUserId = 1006,
                RecipeItemId = 10015129,
                Mode = 2,
                TargetListType = InventoryListType.Main,
                TargetSlotIndex = 10,
                CardListType = InventoryListType.Main,
                CardSlotIndex = 235,
            };

            Check("level-one task card enchants through player store transaction",
                EnchanterStoreUseService.TryEnchant(
                    requester, owner, store, command, 0, int.MaxValue, out var result)
                && result.EnchantSucceeded
                && result.ExperienceGain >= 3
                && result.ExperienceGain <= 6
                && result.FinalExperience == result.ExperienceGain
                && result.Endurance == 297
                && requester.GetItem(InventoryListType.Main, 235) == null
                && requester.GetItem(InventoryListType.Main, 10)?.EnchantCardId == 3619
                && requester.GetItem(InventoryListType.Main, 10)?.Durability == 12
                && requester.GetMainVirtualCount(0)?.Count == 4900
                && owner.GetMainVirtualCount(0)?.Count == 1100,
                ref failures);

            var rejectedRequester = new InventoryService(990522, 990522);
            rejectedRequester.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart, 0, 50);
            rejectedRequester.SetItem(InventoryListType.Main, 10, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = targetEquipmentId,
                Uid = 72002,
            });
            rejectedRequester.SetItem(InventoryListType.Main, 235, new ItemCore
            {
                ItemId = 3619,
                Count = 1,
            });
            store.Enchanter.Endurance = 300;
            Check("enchanter fee failure does not mutate card target or endurance",
                !EnchanterStoreUseService.TryEnchant(
                    rejectedRequester, owner, store, command, 0, int.MaxValue,
                    out var rejected)
                && rejected.ErrorCode == EnchanterStoreUseService.ErrorInsufficientGold
                && rejectedRequester.GetItem(InventoryListType.Main, 235)?.Count == 1
                && rejectedRequester.GetItem(InventoryListType.Main, 10)?.EnchantCardId == 0
                && rejectedRequester.GetMainVirtualCount(0)?.Count == 50
                && store.Enchanter.Endurance == 300,
                ref failures);

            var selfServiceInventory = new InventoryService(990523, 990523);
            selfServiceInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart, 0, 5000);
            selfServiceInventory.SetItem(InventoryListType.Main, 10, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = targetEquipmentId,
                Uid = 72003,
            });
            selfServiceInventory.SetItem(InventoryListType.Main, 235, new ItemCore
            {
                ItemId = 3619,
                Count = 1,
            });
            selfServiceInventory.ClearDirtyState();
            var selfServiceStore = new ExpertJobStoreSession
            {
                OwnerCharacterId = selfServiceInventory.CharacterId,
                OwnerUserId = 1006,
                Kind = ExpertJobStoreKind.EnchantShop,
                Cost = 100,
                Enchanter = new EnchanterStoreState
                {
                    Endurance = 300,
                    CardQualificationLevels = new byte[] { 0 },
                },
            };
            Check("self-service enchant does not transfer the store fee",
                EnchanterStoreUseService.TryEnchant(
                    selfServiceInventory,
                    selfServiceInventory,
                    selfServiceStore,
                    command,
                    0,
                    int.MaxValue,
                    out var selfServiceResult)
                && selfServiceResult.RequesterGold == 5000
                && selfServiceResult.OwnerGold == 5000
                && selfServiceInventory.GetMainVirtualCount(0)?.Count == 5000,
                ref failures);
        }

        private static void RunExpertJobCompoundChecks(ref int failures)
        {
            var inventory = new InventoryService(990523, 990523);
            inventory.SetItem(InventoryListType.Main, 10, new ItemCore
            {
                ItemId = 3227,
                Count = 2,
            });
            inventory.SetItem(InventoryListType.Main, 11, new ItemCore
            {
                ItemId = 3166,
                Count = 20,
            });
            inventory.SetItem(InventoryListType.Main, 12, new ItemCore
            {
                ItemId = 3227,
                Count = 3,
            });
            inventory.SetItem(InventoryListType.Main, 239, new ItemCore
            {
                ItemId = 3619,
                Count = 1,
                EnchantUpgradeCount = 2,
            });
            inventory.ClearDirtyState();
            var command = new ExpertJobCompoundCommand
            {
                RecipeItemId = 10015129,
                RequestedCount = 1,
                CardSlotIndex = 239,
            };

            Check("level-seven enchanter crafts current-PVF task-card bead",
                EnchanterCompoundService.TryCraftBead(
                    inventory,
                    command,
                    585,
                    out var result)
                && result.SuccessCount == 1
                && result.FailureCount == 0
                && result.AttemptedOutputs.Count == 1
                && result.AttemptedOutputs[0].ItemId == 2600313
                && result.AttemptedOutputs[0].Count == 1
                && result.Outputs.Count == 1
                && result.Outputs[0].ItemId == 2600313
                && result.Outputs[0].Count == 1
                && result.ExperienceGain >= 9
                && result.ExperienceGain <= 12
                && result.FinalExperience == 585 + result.ExperienceGain
                && inventory.GetItem(InventoryListType.Main, 239) == null
                && inventory.CountMainItem(3227) == 0
                && inventory.CountMainItem(3166) == 0
                && inventory.CountMainItem(2600313) == 1,
                ref failures);
            var craftedBead = inventory.GetItems(InventoryListType.Main)
                .Select(pair => pair.Value)
                .Single(item => item.ItemId == 2600313);
            Check("crafted bead preserves card upgrade count",
                craftedBead.EnchantUpgradeCount == 2,
                ref failures);

            var epicCardInventory = new InventoryService(990525, 990525);
            epicCardInventory.SetItem(InventoryListType.Main, 239, new ItemCore
            {
                ItemId = 10015144,
                Count = 1,
            });
            epicCardInventory.SetItem(InventoryListType.Main, 240, new ItemCore
            {
                ItemId = 3227,
                Count = 40,
            });
            epicCardInventory.SetItem(InventoryListType.Main, 241, new ItemCore
            {
                ItemId = 3167,
                Count = 60,
            });
            epicCardInventory.AttachMainVirtualCount(359, 3262, 20);
            var epicCardCommand = new ExpertJobCompoundCommand
            {
                RecipeItemId = 10015151,
                RequestedCount = 1,
                CardSlotIndex = 239,
            };
            Check("current-PVF unbindable epic card crafts its enchanter bead",
                EnchanterCompoundService.TryCraftBead(
                    epicCardInventory,
                    epicCardCommand,
                    1401,
                    out var epicCardResult)
                && epicCardResult.SuccessCount == 1
                && epicCardResult.Outputs.Count == 1
                && epicCardResult.Outputs[0].ItemId == 10015170
                && epicCardInventory.CountMainItem(10015144) == 0
                && epicCardInventory.CountMainItem(10015170) == 1,
                ref failures);

            var ack = ExpertJobCompoundPacketBuilder.BuildSuccess(result);
            Check("enchanter bead craft ACK uses current-client result layout",
                ack.Length == 19
                && ack[0] == 1
                && ack[1] == 1
                && BitConverter.ToInt32(ack, 2) == 2600313
                && BitConverter.ToInt32(ack, 6) == 1
                && BitConverter.ToInt32(ack, 10) == 1
                && BitConverter.ToInt32(ack, 14) == 0
                && ack[18] == 0,
                ref failures);

            var failedAckResult = new ExpertJobCompoundResult
            {
                SuccessCount = 0,
                FailureCount = 1,
            };
            failedAckResult.AttemptedOutputs.Add(new ExpertJobCompoundOutput
            {
                ItemId = 2600503,
                Count = 1,
            });
            var failedAck = ExpertJobCompoundPacketBuilder.BuildSuccess(failedAckResult);
            Check("expert-job craft failure ACK retains attempted output for client notice",
                failedAck.Length == 19
                && failedAck[0] == 1
                && failedAck[1] == 1
                && BitConverter.ToInt32(failedAck, 2) == 2600503
                && BitConverter.ToInt32(failedAck, 6) == 1
                && BitConverter.ToInt32(failedAck, 10) == 0
                && BitConverter.ToInt32(failedAck, 14) == 1
                && failedAck[18] == 0,
                ref failures);

            var missingMaterials = new InventoryService(990524, 990524);
            missingMaterials.SetItem(InventoryListType.Main, 239, new ItemCore
            {
                ItemId = 3619,
                Count = 1,
            });
            missingMaterials.ClearDirtyState();
            Check("bead craft material failure leaves card unchanged",
                !EnchanterCompoundService.TryCraftBead(
                    missingMaterials,
                    command,
                    585,
                    out var rejected)
                && rejected.ErrorCode == EnchanterCompoundService.ErrorInsufficientMaterials
                && missingMaterials.GetItem(InventoryListType.Main, 239)?.Count == 1,
                ref failures);

            var duplicateRequirementInventory = new InventoryService(990527, 990527);
            duplicateRequirementInventory.SetItem(
                InventoryListType.Main,
                20,
                new ItemCore { ItemId = 3227, Count = 5 });
            var duplicateRequirements = new[]
            {
                new InventoryMaterialRequirement(3227, 3),
                new InventoryMaterialRequirement(3227, 3),
            };
            Check("duplicate material requirements reject atomically when total is insufficient",
                !InventoryMaterialConsumptionService.TryConsume(
                    duplicateRequirementInventory,
                    duplicateRequirements,
                    null)
                && duplicateRequirementInventory.CountMainItem(3227) == 5,
                ref failures);

            var productInventory = new InventoryService(990525, 990525);
            productInventory.SetMainVirtualCount(354, 100);
            productInventory.SetItem(InventoryListType.Main, 21, new ItemCore
            {
                ItemId = 2610030,
                Count = 10,
            });
            productInventory.ClearDirtyState();
            var productState = new ExpertJobState();
            productState.LearnedRecipeIds.Add(2600141);
            var productCommand = new ExpertJobCompoundCommand
            {
                RecipeItemId = 2600141,
                RequestedCount = 10,
                CardSlotIndex = -1,
            };
            var enchanterConfig = EnchanterConfigProvider.Config;
            var productCrafted = ExpertJobCompoundService.TryCraftProduct(
                productInventory,
                productCommand,
                0,
                productState,
                enchanterConfig.RecipeConfig,
                enchanterConfig,
                out var productResult);
            Check("learned enchanter recipe crafts requested current-PVF products",
                productCrafted
                && productResult.ErrorCode == 0
                && productResult.SuccessCount == 10
                && productResult.FailureCount == 0
                && productResult.AttemptedOutputs.Count == 1
                && productResult.AttemptedOutputs[0].ItemId == 2610034
                && productResult.AttemptedOutputs[0].Count == 10
                && productResult.Outputs.Count == 1
                && productResult.Outputs[0].ItemId == 2610034
                && productResult.Outputs[0].Count == 10
                && productResult.ExperienceGain >= 0
                && productResult.ExperienceGain <= 10
                && productResult.FinalExperience == productResult.ExperienceGain
                && productInventory.CountMainItem(3033) == 0
                && productInventory.CountMainItem(2610030) == 0
                && productInventory.CountMainItem(2610034) == 10,
                ref failures);
            var productAck = productCrafted
                ? ExpertJobCompoundPacketBuilder.BuildSuccess(productResult)
                : Array.Empty<byte>();
            Check("enchanter product craft ACK reports batch output and counts",
                productAck.Length == 19
                && productAck[0] == 1
                && productAck[1] == 1
                && BitConverter.ToInt32(productAck, 2) == 2610034
                && BitConverter.ToInt32(productAck, 6) == 10
                && BitConverter.ToInt32(productAck, 10) == 10
                && BitConverter.ToInt32(productAck, 14) == 0
                && productAck[18] == 0,
                ref failures);

            const int extractorRecipeItemId = 2600513;
            var extractorInventory = new InventoryService(990526, 990526);
            extractorInventory.SetMainVirtualCount(0, 1_000_000);
            var extractorRecipeParsed =
                InventoryCompoundItemRecipeService.TryParseCompoundRecipe(
                    extractorRecipeItemId,
                    out var extractorRecipe);
            var extractorSlot = (short)30;
            if (extractorRecipeParsed)
            {
                foreach (var material in extractorRecipe.Materials)
                {
                    if (InventoryService.TryResolveMainVirtualSlotByItemId(
                            material.ItemTemplateId,
                            out var virtualSlot,
                            out _))
                    {
                        extractorInventory.SetMainVirtualCount(
                            virtualSlot,
                            material.Count);
                    }
                    else
                    {
                        extractorInventory.SetItem(
                            InventoryListType.Main,
                            extractorSlot++,
                            new ItemCore
                            {
                                ItemId = material.ItemTemplateId,
                                Count = material.Count,
                            });
                    }
                }
            }
            extractorInventory.ClearDirtyState();
            var extractorState = new ExpertJobState();
            extractorState.LearnedRecipeIds.Add(extractorRecipeItemId);
            var extractorCrafted = ExpertJobCompoundService.TryCraftProduct(
                extractorInventory,
                new ExpertJobCompoundCommand
                {
                    RecipeItemId = extractorRecipeItemId,
                    RequestedCount = 1,
                    CardSlotIndex = -1,
                },
                (uint)enchanterConfig.ExperienceThresholds[1],
                extractorState,
                enchanterConfig.RecipeConfig,
                enchanterConfig,
                out var extractorResult);
            Check("extractor product craft requests expert-job window inventory rescan",
                extractorRecipeParsed
                && extractorCrafted
                && extractorResult.ErrorCode == 0
                && extractorResult.ExtractorInventoryChanged
                && extractorResult.Outputs.Count == 1
                && enchanterConfig.Extractors.ContainsKey(
                    extractorResult.Outputs[0].ItemId),
                ref failures);

            CheckProductCraft(
                "level-one alchemist crafts its current-PVF auto-learn product",
                990528,
                2600149,
                1110,
                AlchemistConfigProvider.Config,
                1,
                2,
                ref failures);
            CheckProductCraft(
                "level-one doll-controller crafts its current-PVF auto-learn product",
                990529,
                2600083,
                2600029,
                DollControllerConfigProvider.Config,
                2,
                4,
                ref failures);

        }

        private static void CheckProductCraft(
            string label,
            int characterId,
            int recipeItemId,
            int expectedProductItemId,
            IExpertJobExtractionConfig config,
            int minimumExperience,
            int maximumExperience,
            ref int failures)
        {
            var inventory = new InventoryService(characterId, characterId);
            inventory.SetMainVirtualCount(0, 1_000_000);
            var parsed = InventoryCompoundItemRecipeService.TryParseCompoundRecipe(
                recipeItemId,
                out var recipe);
            var slot = (short)30;
            if (parsed)
            {
                foreach (var material in recipe.Materials)
                {
                    if (InventoryService.TryResolveMainVirtualSlotByItemId(
                            material.ItemTemplateId,
                            out var virtualSlot,
                            out _))
                    {
                        inventory.SetMainVirtualCount(virtualSlot, material.Count);
                    }
                    else
                    {
                        inventory.SetItem(
                            InventoryListType.Main,
                            slot++,
                            new ItemCore
                            {
                                ItemId = material.ItemTemplateId,
                                Count = material.Count,
                            });
                    }
                }
            }
            inventory.ClearDirtyState();
            var state = new ExpertJobState();
            state.LearnedRecipeIds.Add(recipeItemId);
            var crafted = ExpertJobCompoundService.TryCraftProduct(
                inventory,
                new ExpertJobCompoundCommand
                {
                    RecipeItemId = recipeItemId,
                    RequestedCount = 1,
                    CardSlotIndex = -1,
                },
                0,
                state,
                config.RecipeConfig,
                config,
                out var result);

            Check(label,
                parsed
                && crafted
                && result.ErrorCode == 0
                && result.SuccessCount == 1
                && result.FailureCount == 0
                && result.AttemptedOutputs.Count == 1
                && result.AttemptedOutputs[0].ItemId == expectedProductItemId
                && result.AttemptedOutputs[0].Count == 1
                && result.Outputs.Count == 1
                && result.Outputs[0].ItemId == expectedProductItemId
                && result.Outputs[0].Count == 1
                && result.ExperienceGain >= minimumExperience
                && result.ExperienceGain <= maximumExperience
                && inventory.CountMainItem(expectedProductItemId) == 1,
                ref failures);
        }

        private static void RunPersistenceChecks(ref int failures)
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"dfo-expert-job-{Guid.NewGuid():N}.db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO accounts (account_id, m_id) VALUES (990490, 'expert-job-test');
INSERT INTO characters (character_id, account_id, name)
VALUES (990490, 990490, 'expert-job-test');
INSERT INTO character_subtype0_fields (
    character_id, expert_job_type, expert_job_exp)
VALUES (990490, 3, 20);
INSERT INTO accounts (account_id, m_id) VALUES (990491, 'enchanter-test');
INSERT INTO characters (character_id, account_id, name)
VALUES (990491, 990491, 'enchanter-test');
INSERT INTO character_subtype0_fields (
    character_id, expert_job_type, expert_job_exp)
VALUES (990491, 1, 246);
INSERT INTO accounts (account_id, m_id) VALUES (990492, 'alchemist-test');
INSERT INTO characters (character_id, account_id, name)
VALUES (990492, 990492, 'alchemist-test');
INSERT INTO character_subtype0_fields (
    character_id, expert_job_type, expert_job_exp)
VALUES (990492, 2, 0);
INSERT INTO accounts (account_id, m_id) VALUES (990493, 'doll-controller-test');
INSERT INTO characters (character_id, account_id, name)
VALUES (990493, 990493, 'doll-controller-test');
INSERT INTO character_subtype0_fields (
    character_id, expert_job_type, expert_job_exp)
VALUES (990493, 4, 0);";
                        command.ExecuteNonQuery();
                    }

                    using (var transaction = connection.BeginTransaction())
                    {
                        SqliteExpertJobStateRepository.InitializeInTransaction(
                            connection,
                            transaction,
                            990490,
                            ExpertJobStateCodec.DisjointerType);
                        SqliteExpertJobStateRepository.InitializeInTransaction(
                            connection,
                            transaction,
                            990491,
                            ExpertJobStateCodec.EnchanterType);
                        SqliteExpertJobStateRepository.InitializeInTransaction(
                            connection,
                            transaction,
                            990492,
                            ExpertJobStateCodec.AlchemistType);
                        SqliteExpertJobStateRepository.InitializeInTransaction(
                            connection,
                            transaction,
                            990493,
                            ExpertJobStateCodec.DollControllerType);
                        transaction.Commit();
                    }
                }

                var repository = new SqliteExpertJobStateRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var initialized = repository.Load(
                    990490,
                    ExpertJobStateCodec.DisjointerType);
                Check("disjointer initialization persists explicit state",
                    initialized.DisjointMachine.MachineGrade == 1
                    && initialized.DisjointMachine.Endurance == 300,
                    ref failures);
                var initializedEnchanter = repository.Load(
                    990491,
                    ExpertJobStateCodec.EnchanterType);
                Check("enchanter load reconciles PVF auto-learn recipes",
                    initializedEnchanter.LearnedRecipeIds.SequenceEqual(
                        new[] { 10015129, 10015130 }),
                    ref failures);
                var initializedAlchemist = repository.Load(
                    990492,
                    ExpertJobStateCodec.AlchemistType);
                Check("alchemist initialization persists PVF auto-learn recipe",
                    initializedAlchemist.LearnedRecipeIds.SequenceEqual(new[] { 2600149 }),
                    ref failures);
                var initializedDollController = repository.Load(
                    990493,
                    ExpertJobStateCodec.DollControllerType);
                Check("doll-controller initialization persists PVF auto-learn recipe",
                    initializedDollController.LearnedRecipeIds.SequenceEqual(new[] { 2600083 }),
                    ref failures);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        Check("machine state and experience save atomically",
                            repository.SaveInTransaction(
                                connection,
                                transaction,
                                990490,
                                new DisjointMachineState
                                {
                                    MachineGrade = 7,
                                    Endurance = 269,
                                },
                                5),
                            ref failures);
                        Check("manual enchanter design uses the unified recipe table",
                            repository.SaveRecipeInTransaction(
                                connection,
                                transaction,
                                990491,
                                2600512),
                            ref failures);
                        Check("manual alchemist design uses the unified recipe table",
                            repository.SaveRecipeInTransaction(
                                connection,
                                transaction,
                                990492,
                                2600492),
                            ref failures);
                        Check("manual doll-controller design uses the unified recipe table",
                            repository.SaveRecipeInTransaction(
                                connection,
                                transaction,
                                990493,
                                2600502),
                            ref failures);
                        transaction.Commit();
                    }
                }

                var reloaded = repository.Load(
                    990490,
                    ExpertJobStateCodec.DisjointerType);
                Check("explicit machine state reloads without init blob",
                    reloaded.DisjointMachine.MachineGrade == 7
                    && reloaded.DisjointMachine.Endurance == 269
                    && ReadExpertJobExperience(connectionString, 990490) == 25,
                    ref failures);
                var reloadedEnchanter = repository.Load(
                    990491,
                    ExpertJobStateCodec.EnchanterType);
                Check("unified recipe persistence reloads auto and manual recipes",
                    reloadedEnchanter.LearnedRecipeIds.SequenceEqual(
                        new[] { 2600512, 10015129, 10015130 }.OrderBy(id => id)),
                    ref failures);
                var reloadedAlchemist = repository.Load(
                    990492,
                    ExpertJobStateCodec.AlchemistType);
                Check("alchemist reload keeps auto and manual recipes in unified state",
                    reloadedAlchemist.LearnedRecipeIds.SequenceEqual(
                        new[] { 2600149, 2600492 }),
                    ref failures);
                var reloadedDollController = repository.Load(
                    990493,
                    ExpertJobStateCodec.DollControllerType);
                Check("doll-controller reload keeps auto and manual recipes in unified state",
                    reloadedDollController.LearnedRecipeIds.SequenceEqual(
                        new[] { 2600083, 2600502 }),
                    ref failures);

                var recoverySessionId = Guid.NewGuid();
                InventoryLease recoveryLease;
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    recoveryLease = InventoryContext.Register(
                        recoverySessionId,
                        InventoryService.LoadFromDb(connection, 990490, 990490));
                }
                try
                {
                    const int rolledBackGold = 123456;
                    recoveryLease.Inventory.SetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart,
                        rolledBackGold);
                    InventoryRollbackRecoveryService.ReloadOnlineInventory(
                        connectionString,
                        recoveryLease);

                    InventoryContext.TryGetLease(990490, out var restoredLease);
                    InventoryService committedInventory;
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();
                        committedInventory = InventoryService.LoadFromDb(
                            connection,
                            990490,
                            990490);
                    }
                    Check("rollback recovery reloads the current lease without saving dirty state",
                        restoredLease != null
                        && ReferenceEquals(restoredLease, recoveryLease)
                        && (restoredLease.Inventory.GetMainVirtualCount(
                                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0)
                            != rolledBackGold
                        && (committedInventory.GetMainVirtualCount(
                                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0)
                            != rolledBackGold,
                        ref failures);
                }
                finally
                {
                    InventoryContext.Unregister(recoverySessionId, 990490);
                }
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }
        }

        private static long ReadExpertJobExperience(
            string connectionString,
            int characterId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT expert_job_exp
FROM character_subtype0_fields
WHERE character_id=@cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private static bool HasColumn(
            string connectionString,
            string tableName,
            string columnName)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"PRAGMA table_info({tableName});";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (string.Equals(
                                    reader.GetString(1),
                                    columnName,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static bool HasTable(string connectionString, string tableName)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type='table' AND name=@name;";
                    command.Parameters.AddWithValue("@name", tableName);
                    return Convert.ToInt32(command.ExecuteScalar()) > 0;
                }
            }
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            foreach (var path in new[]
            {
                databasePath,
                databasePath + "-wal",
                databasePath + "-shm",
            })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private static InventoryService CreateUnidentifiedEquipmentInventory(
            int characterId,
            int itemId,
            int uid)
        {
            var inventory = new InventoryService(characterId, characterId);
            inventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                5000);
            inventory.SetItem(InventoryListType.Main, 37, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = itemId,
                Uid = uid,
                AmplifyType = 0x80,
            });
            inventory.ClearDirtyState();
            return inventory;
        }

        private static ExpertJobStoreSession CreateOperationStore(int characterId, byte grade)
        {
            return new ExpertJobStoreSession
            {
                OwnerCharacterId = characterId,
                Kind = ExpertJobStoreKind.DisjointMachine,
                DisjointMachine = new DisjointMachineState
                {
                    MachineGrade = grade,
                    Endurance = 300,
                },
            };
        }

        private static DisjointItemRequest CreateDisjointRequest(short slotIndex)
        {
            return new DisjointItemRequest
            {
                TargetSlotIndex = slotIndex,
                ItemSpace = InventoryListType.Main,
                DisjointItemSlotIndex = -1,
            };
        }
    }
}
