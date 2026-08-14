using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class ChronicleRefineSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            var body = new byte[] { 0x69, 0x00, 0x08, 0xF9, 0x99, 0x00, 0x00, 0x0D, 0x00, 0x58, 0x0F, 0x02, 0x00, 0x06 };

            Check(ChronicleRefineRequest.TryParse(body, out var request), "captured request parses", ref failures);
            Check(request.MaterialSlotIndex == 105, "material slot", ref failures);
            Check(request.MaterialItemTemplateId == 10090760, "material template", ref failures);
            Check(request.MaterialPadding == 0, "padding", ref failures);
            Check(request.TargetSlotIndex == 13, "target slot", ref failures);
            Check(request.TargetItemTemplateId == 135000, "target template", ref failures);
            Check(request.OptionNo == 6, "option number", ref failures);
            Check(!ChronicleRefineRequest.TryParse(new byte[13], out _), "short request rejected", ref failures);
            Check(!ChronicleRefineRequest.TryParse(new byte[15], out _), "trailing request rejected", ref failures);
            var nonMainMaterial = (byte[])body.Clone();
            nonMainMaterial[6] = 1;
            Check(!ChronicleRefineRequest.TryParse(nonMainMaterial, out _), "non-main material space rejected", ref failures);

            var command = request.ToCommand(2, 0x13);
            Check(command.CharacterJob == 2 && command.FirstGrowType == 3, "character fields normalized", ref failures);
            Check(ChronicleRefineJobMatcher.Matches("`[fighter]` `[at fighter]`", 1)
                && ChronicleRefineJobMatcher.Matches("`[fighter]` `[at fighter]`", 7)
                && !ChronicleRefineJobMatcher.Matches("`[fighter]` `[at fighter]`", 0),
                "multi-job PVF field matches each listed profession", ref failures);

            var stackable = StackableItemFile.Parse(@"
[name]
    `异次元之绿色气息`
[type]
    2
[3choro enchant]
    [probability]
        100 11
    [check]
        0 1 `weapon`
        [skill]
            6 `[swordman]` 38 `[dungeon type]` `[level]` 1 `%` -8
        [/skill]
    [/check]
    [check]
        0 2
        `coat`
        [skill]
            6
            `[swordman]` 99 `[dungeon type]` `[level]` 1 `%` -8
        [/skill]
    [/check]
");
            Check(stackable.Type == 2, "green aura type parsed", ref failures);
            Check(stackable.ThreeChronicleEnchant != null
                && stackable.ThreeChronicleEnchant.Probabilities.Count == 2
                && stackable.ThreeChronicleEnchant.Probabilities[0] == 100
                && stackable.ThreeChronicleEnchant.Probabilities[1] == 11,
                "first and second probabilities parsed", ref failures);
            Check(stackable.ThreeChronicleEnchant.Checks.Count == 2
                && stackable.ThreeChronicleEnchant.Checks[0].Values.Count == 2
                && stackable.ThreeChronicleEnchant.Checks[0].Values[0] == 0
                && stackable.ThreeChronicleEnchant.Checks[0].Values[1] == 1
                && stackable.ThreeChronicleEnchant.Checks[0].EquipmentType == "weapon",
                "job grow-type and equipment check parsed", ref failures);
            Check(stackable.ThreeChronicleEnchant.Checks[1].Values[1] == 2
                && stackable.ThreeChronicleEnchant.Checks[1].EquipmentType == "coat"
                && stackable.ThreeChronicleEnchant.Checks[1].Skills[0].SkillId == 99,
                "duplicate option number stays scoped to its check", ref failures);
            var selected = stackable.ThreeChronicleEnchant?.GetSkill(6);
            Check(selected != null && selected.Job == "[swordman]" && selected.SkillId == 38,
                "option 6 resolves swordman skill 38", ref failures);
            Check(ChronicleRefineProbability.IsSuccess(100, 99), "100 percent always succeeds", ref failures);
            Check(ChronicleRefineProbability.IsSuccess(11, 11)
                && !ChronicleRefineProbability.IsSuccess(11, 12), "legacy inclusive 11 probability boundary", ref failures);
            ChronicleRefineMaterialResolver.Warmup();
            Check(ChronicleRefineMaterialResolver.TryGetAuraType(1254, out var redAuraType)
                && redAuraType == 0
                && ChronicleRefineMaterialResolver.TryGetAuraType(1255, out var blueAuraType)
                && blueAuraType == 1
                && ChronicleRefineMaterialResolver.TryGetAuraType(1256, out var greenAuraType)
                && greenAuraType == 2
                && ChronicleRefineMaterialResolver.TryGetAuraType(10090758, out var sacredRedAuraType)
                && sacredRedAuraType == 0
                && ChronicleRefineMaterialResolver.TryGetAuraType(10090759, out var sacredBlueAuraType)
                && sacredBlueAuraType == 1
                && ChronicleRefineMaterialResolver.TryGetAuraType(10090760, out var sacredGreenAuraType)
                && sacredGreenAuraType == 2,
                "all six real aura definitions recognized from PVF", ref failures);
            var greenAuraItemId = 0;
            Check(ChronicleRefineMaterialResolver.TryGetPacketAuraItemId(0, out var redAuraItemId)
                && redAuraItemId == 1254
                && ChronicleRefineMaterialResolver.TryGetPacketAuraItemId(1, out var blueAuraItemId)
                && blueAuraItemId == 1255
                && ChronicleRefineMaterialResolver.TryGetPacketAuraItemId(2, out greenAuraItemId)
                && greenAuraItemId == 1256,
                "canonical packet aura ids match the 86 protocol range", ref failures);
            Check(ChronicleRefineResult.ErrorDurability == 0x07
                && ChronicleRefineResult.ErrorInventoryFull == 0x15
                && ChronicleRefineResult.ErrorTemplateMismatch == 0x17
                && ChronicleRefineResult.ErrorUnidentified == 0xAE
                && ChronicleRefineResult.ErrorLocked == 0xD5,
                "legacy validation error codes", ref failures);
            ValidateRealAuraPvf(ref failures);
            ValidateFighterChroniclePvf(ref failures);
            ValidateRealFirstRefineTransaction(ref failures);

            var middle = MakeEquipListCodec.BuildMiddleData1A(new[]
            {
                new MakeEquipListCodec.ChronicleOptionFields
                {
                    OptionId = greenAuraItemId,
                    CharacJob = 0,
                    FirstGrowType = 1,
                    EquipmentType = (byte)EquipmentType.Weapon,
                    OptionNo = command.OptionNo,
                },
            });
            var parsed = MakeEquipListCodec.BuildChronicleOptions(middle);
            Check(parsed.Length == 1, "one chronicle option encoded", ref failures);
            Check(parsed[0].OptionId == greenAuraItemId
                && parsed[0].EquipmentType == (byte)EquipmentType.Weapon
                && parsed[0].OptionNo == 6, "chronicle option round trip", ref failures);
            var legacyMiddle = MakeEquipListCodec.BuildMiddleData1A(new[]
            {
                new MakeEquipListCodec.ChronicleOptionFields
                {
                    OptionId = 23,
                    CharacJob = 0,
                    FirstGrowType = 3,
                    EquipmentType = 2,
                    OptionNo = 6,
                },
            });
            var migratedMiddle = ChronicleRefineProtocol.NormalizeMiddleData(EquipmentType.Coat, legacyMiddle);
            var migrated = MakeEquipListCodec.BuildChronicleOptions(migratedMiddle);
            Check(migrated.Length == 1
                && migrated[0].OptionId == greenAuraItemId
                && migrated[0].EquipmentType == (byte)EquipmentType.Coat
                && migrated[0].OptionNo == 6,
                "legacy skill-id chronicle entry migrates to aura-id protocol", ref failures);
            Check(Hex(migratedMiddle) == "01-E8-04-00-00-00-00-00-00-00-00-03-00-0D-00-06-00",
                "captured coat refine entry matches legacy packet layout", ref failures);

            var unknownOptions = ChronicleRefineService.NormalizeOptions(new[]
            {
                new ChronicleOption
                {
                    OptionId = 99999999,
                    CharacJob = 0,
                    FirstGrowType = 3,
                    EquipmentType = (byte)EquipmentType.Coat,
                    OptionNo = 6,
                },
            }, EquipmentType.Coat);
            Check(unknownOptions.Count == 1
                && unknownOptions[0].OptionId == 99999999
                && unknownOptions[0].EquipmentType == (byte)EquipmentType.Coat,
                "unknown chronicle option is not remapped to red aura", ref failures);
            var successAck = ChronicleRefineAckBuilder.BuildSuccess(new ChronicleRefineResult
            {
                Command = new ChronicleRefineCommand { MaterialSlotIndex = 105, TargetSlotIndex = 13 },
                MaterialRemainingStackCount = 9,
                RefineSucceeded = true,
            });
            Check(Hex(successAck) == "01-69-00-09-00-01", "success ACK matches legacy layout", ref failures);
            var responsePacket = GamePacketEnvelopeBuilder.Build(0x01, 0x0172, successAck);
            Check(responsePacket.Length >= 3
                && responsePacket[0] == 0x01
                && responsePacket[1] == 0x72
                && responsePacket[2] == 0x01,
                "86 refine response reuses request opcode", ref failures);

            var failureResult = new ChronicleRefineResult
            {
                Command = new ChronicleRefineCommand { MaterialSlotIndex = 105, TargetSlotIndex = 13 },
                MaterialRemainingStackCount = 8,
                RefineSucceeded = false,
                TargetDestroyed = true,
            };
            failureResult.FailureRewards.Add(new DisjointMaterialResult
            {
                SlotIndex = 121,
                ItemTemplateId = 3311,
                Count = 4,
            });
            var failureAck = ChronicleRefineAckBuilder.BuildSuccess(failureResult);
            Check(Hex(failureAck) == "01-69-00-08-00-00-00-0D-00-01-79-00-EF-0C-00-00-04-00-00-00",
                "86 failure ACK includes reserved byte and standard reward row", ref failures);
            failureResult.FailureRewards.Add(new DisjointMaterialResult
            {
                SlotIndex = 122,
                ItemTemplateId = 3229,
                Count = 3,
            });
            failureResult.FailureRewards.Add(new DisjointMaterialResult
            {
                SlotIndex = 123,
                ItemTemplateId = 3037,
                Count = 20,
            });
            var threeRewardAck = ChronicleRefineAckBuilder.BuildSuccess(failureResult);
            Check(threeRewardAck.Length == 40
                && threeRewardAck[6] == 0x00
                && BitConverter.ToInt16(threeRewardAck, 7) == 13
                && threeRewardAck[9] == 0x03
                && BitConverter.ToInt16(threeRewardAck, 10) == 121
                && BitConverter.ToInt32(threeRewardAck, 12) == 3311
                && BitConverter.ToInt32(threeRewardAck, 16) == 4,
                "failure ACK matches client 0x00CE9C70 reward layout", ref failures);

            Console.WriteLine(failures == 0 ? "ChronicleRefineSelfTest OK" : $"ChronicleRefineSelfTest FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(bool condition, string name, ref int failures)
        {
            if (condition)
                Console.WriteLine($"  [PASS] {name}");
            else
            {
                Console.WriteLine($"  [FAIL] {name}");
                failures++;
            }
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes ?? Array.Empty<byte>());
        }

        private static void ValidateRealAuraPvf(ref int failures)
        {
            string archivePath;
            try
            {
                archivePath = GameWorld.GameWorldConfig.PvfArchivePath;
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine("  [SKIP] real aura PVF validation (Script.pvf missing)");
                return;
            }

            if (!File.Exists(archivePath))
                return;

            var materials = new[]
            {
                (Id: 1254, Type: 0),
                (Id: 1255, Type: 1),
                (Id: 1256, Type: 2),
                (Id: 10090758, Type: 0),
                (Id: 10090759, Type: 1),
                (Id: 10090760, Type: 2),
            };
            foreach (var material in materials)
            {
                var loaded = ItemMetadataResolver.TryLoadStackableFile(material.Id, out var definition);
                Check(loaded
                    && definition.Type == material.Type
                    && definition.ThreeChronicleEnchant != null
                    && definition.ThreeChronicleEnchant.Probabilities.Count >= 2
                    && definition.ThreeChronicleEnchant.Checks.Count > 0
                    && definition.ThreeChronicleEnchant.Skills.Count > 0,
                    $"real aura PVF id={material.Id} type={material.Type} checks={definition.ThreeChronicleEnchant?.Checks.Count ?? 0} skills={definition.ThreeChronicleEnchant?.Skills.Count ?? 0}",
                    ref failures);

                if (loaded && material.Type == 0)
                {
                    ThreeChronicleEnchantCheck redCoatCheck = null;
                    var matchingCheckCount = 0;
                    foreach (var check in definition.ThreeChronicleEnchant.Checks)
                    {
                        if (check.Values.Count >= 2
                            && check.Values[0] == 0
                            && check.Values[1] == 3
                            && NormalizeEquipmentType(check.EquipmentType) == "coat")
                        {
                            matchingCheckCount++;
                            if (check.Skills.Exists(skill => skill.OptionNo == 7))
                                redCoatCheck = check;
                        }
                    }
                    var options = string.Empty;
                    if (redCoatCheck != null)
                    {
                        foreach (var skill in redCoatCheck.Skills)
                            options += (options.Length == 0 ? string.Empty : ",") + skill.OptionNo;
                    }
                    Check(redCoatCheck != null && redCoatCheck.Skills.Exists(skill => skill.OptionNo == 7),
                        $"red aura id={material.Id} swordman grow=3 coat option=7 matchingChecks={matchingCheckCount} options=[{options}]",
                        ref failures);
                }
            }
        }

        private static void ValidateRealFirstRefineTransaction(ref int failures)
        {
            try
            {
                if (!File.Exists(GameWorld.GameWorldConfig.PvfArchivePath))
                    return;
            }
            catch (FileNotFoundException)
            {
                return;
            }

            const int accountId = 97001;
            const int characterId = 97002;
            const int materialId = 10090760;
            const int targetId = 135000;
            const short materialSlot = 105;
            const short targetSlot = 13;

            var metadata = ItemMetadataResolver.Resolve(targetId);
            if (!ItemMetadataResolver.TryLoadStackableFile(materialId, out var material)
                || material.ThreeChronicleEnchant == null
                || !ChronicleRefineMaterialResolver.TryGetFragmentItemId(material, out var fragmentItemId))
            {
                Check(false, "real first refine definitions load", ref failures);
                return;
            }

            Check(fragmentItemId == 3311, "fragment item id resolved from aura need-material PVF", ref failures);
            var failureRewards = ChronicleRefineService.BuildFailureRewards(metadata, 0, fragmentItemId);
            var fragmentReward = failureRewards.Find(reward => reward.ItemTemplateId == fragmentItemId);
            Check(failureRewards.Count == 3
                && fragmentReward != null
                && fragmentReward.Count == 1,
                "real failure rewards contain three item types and dimensional fragment", ref failures);
            var reinforcedFailureRewards = ChronicleRefineService.BuildFailureRewards(metadata, 3, fragmentItemId);
            var reinforcedFragment = reinforcedFailureRewards.Find(reward => reward.ItemTemplateId == fragmentItemId);
            Check(reinforcedFailureRewards.Count == 3
                && reinforcedFragment != null
                && reinforcedFragment.Count == 4,
                "+3 equipment yields four fragments without extra reward types", ref failures);

            var targetType = NormalizeEquipmentType(metadata.EquipmentType);
            ThreeChronicleEnchantCheck selectedCheck = null;
            foreach (var check in material.ThreeChronicleEnchant.Checks)
            {
                if (check.Values.Count >= 2
                    && check.Skills.Count > 0
                    && NormalizeEquipmentType(check.EquipmentType) == targetType)
                {
                    selectedCheck = check;
                    break;
                }
            }

            if (selectedCheck == null)
            {
                Check(false, $"real first refine compatible check target={targetType}", ref failures);
                return;
            }

            var skill = selectedCheck.Skills[0];
            var inventory = new InventoryService(characterId, accountId);
            inventory.SetItem(InventoryListType.Main, materialSlot, new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = materialId,
                Count = 2,
            });
            inventory.SetItem(InventoryListType.Main, targetSlot, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = targetId,
                Uid = 10001,
                Durability = metadata.Durability,
            });

            var ok = ChronicleRefineService.TryRefine(inventory, new ChronicleRefineCommand
            {
                MaterialSlotIndex = materialSlot,
                MaterialItemTemplateId = materialId,
                TargetSlotIndex = targetSlot,
                TargetItemTemplateId = targetId,
                OptionNo = (byte)skill.OptionNo,
                CharacterJob = (byte)selectedCheck.Values[0],
                FirstGrowType = (byte)selectedCheck.Values[1],
            }, out var result);

            var remaining = inventory.GetItem(InventoryListType.Main, materialSlot);
            var refined = inventory.GetItem(InventoryListType.Main, targetSlot);
            var persistedOptions = refined?.ChronicleOptions;
            Check(ok && result.RefineSucceeded && result.OptionCount == 1,
                "real first refine succeeds", ref failures);
            Check(remaining != null && remaining.Count == 1,
                "real first refine consumes one aura", ref failures);
            Check(refined != null
                && persistedOptions != null
                && persistedOptions.Count == 1
                && ChronicleRefineMaterialResolver.TryGetPacketAuraItemId(2, out var expectedGreenAuraItemId)
                && persistedOptions[0].OptionId == expectedGreenAuraItemId
                && persistedOptions[0].EquipmentType == (byte)EquipmentType.Coat,
                "real first refine stores target option in ItemCore", ref failures);

            var roundTrip = refined == null ? null : ItemCore.FromBytes(refined.ToBytes());
            Check(roundTrip?.ChronicleOptions.Count == 1
                && ChronicleRefineMaterialResolver.TryGetPacketAuraItemId(2, out var roundTripAuraItemId)
                && roundTrip.ChronicleOptions[0].OptionId == roundTripAuraItemId
                && roundTrip.ChronicleOptions[0].OptionNo == (byte)skill.OptionNo,
                "refine option survives ItemCore persistence codec", ref failures);

            const int failureMaterialId = 1256;
            var failureInventory = new InventoryService(characterId + 1, accountId);
            failureInventory.SetItem(InventoryListType.Main, materialSlot, new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = failureMaterialId,
                Count = 1,
            });
            var failureTarget = new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = targetId,
                Uid = 10002,
                Durability = metadata.Durability,
                Upgrade = 3,
            };
            ChronicleRefineMaterialResolver.TryGetPacketAuraItemId(0, out var redAuraItemId);
            failureTarget.SetChronicleOptions(new[]
            {
                new ChronicleOption
                {
                    OptionId = redAuraItemId,
                    CharacJob = (byte)selectedCheck.Values[0],
                    FirstGrowType = (byte)selectedCheck.Values[1],
                    EquipmentType = (byte)EquipmentType.Coat,
                    OptionNo = (byte)skill.OptionNo,
                },
            });
            failureInventory.SetItem(InventoryListType.Main, targetSlot, failureTarget);
            var failureCommand = new ChronicleRefineCommand
            {
                MaterialSlotIndex = materialSlot,
                MaterialItemTemplateId = failureMaterialId,
                TargetSlotIndex = targetSlot,
                TargetItemTemplateId = targetId,
                OptionNo = (byte)skill.OptionNo,
                CharacterJob = (byte)selectedCheck.Values[0],
                FirstGrowType = (byte)selectedCheck.Values[1],
            };
            var failedAsExpected = ChronicleRefineService.TryRefine(
                failureInventory, failureCommand, () => 100, out var failureResult);
            var failureRewardsGranted = failureResult.FailureRewards.Count == 3;
            foreach (var reward in failureResult.FailureRewards)
            {
                var granted = failureInventory.GetItem(InventoryListType.Main, reward.SlotIndex);
                var virtualGrant = failureInventory.GetMainVirtualCount(reward.SlotIndex);
                failureRewardsGranted = failureRewardsGranted
                    && ((granted != null
                            && granted.ItemId == reward.ItemTemplateId
                            && granted.Count == reward.Count)
                        || (virtualGrant != null
                            && virtualGrant.ItemId == reward.ItemTemplateId
                            && virtualGrant.Count == reward.Count));
            }
            var failureFragment = failureResult.FailureRewards.Find(
                reward => reward.ItemTemplateId == fragmentItemId);
            Check(failedAsExpected
                && !failureResult.RefineSucceeded
                && failureResult.TargetDestroyed
                && failureInventory.GetItem(InventoryListType.Main, materialSlot) == null
                && failureInventory.GetItem(InventoryListType.Main, targetSlot) == null
                && failureRewardsGranted
                && failureFragment?.Count == 4,
                "forced second refine failure destroys target and grants all three rewards",
                ref failures);
        }
        private static void ValidateFighterChroniclePvf(ref int failures)
        {
            const int targetItemId = 605000;
            var metadata = ItemMetadataResolver.Resolve(targetItemId);
            if (!ItemMetadataResolver.TryLoadEquipmentFile(targetItemId, out var equipment))
                return;

            Check(ChronicleRefineJobMatcher.Matches(equipment.UsableJob, 1)
                && ChronicleRefineJobMatcher.Matches(equipment.UsableJob, 7)
                && !ChronicleRefineJobMatcher.Matches(equipment.UsableJob, 0),
                "reported weapon usable-job supports fighter and at-fighter", ref failures);

            var materialIds = new[] { 1254, 1255, 1256, 10090758, 10090759, 10090760 };
            foreach (var materialItemId in materialIds)
            {
                if (!ItemMetadataResolver.TryLoadStackableFile(materialItemId, out var material)
                    || material?.ThreeChronicleEnchant == null)
                    continue;

                var femaleFighterMatch = false;
                var maleFighterMatch = false;
                var mismatchedJobScopes = 0;
                foreach (var check in material.ThreeChronicleEnchant.Checks)
                {
                    if (check.Values.Count < 2 || check.Values[0] < 0 || check.Values[0] > byte.MaxValue)
                        continue;

                    var checkJob = (byte)check.Values[0];
                    if (check.Skills.Count > 0
                        && !check.Skills.Exists(skill => ChronicleRefineJobMatcher.Matches(skill.Job, checkJob)))
                        mismatchedJobScopes++;

                    if (check.Values[1] != 1
                        || NormalizeEquipmentType(check.EquipmentType) != NormalizeEquipmentType(metadata.EquipmentType)
                        || !check.Skills.Exists(skill => skill.OptionNo == 6
                            && ChronicleRefineJobMatcher.Matches(skill.Job, checkJob)))
                        continue;

                    if (checkJob == 1)
                        femaleFighterMatch = true;
                    else if (checkJob == 7)
                        maleFighterMatch = true;
                }

                Check(femaleFighterMatch && maleFighterMatch,
                    $"aura {materialItemId} supports fighter and at-fighter grow=1 weapon option=6",
                    ref failures);
                Check(mismatchedJobScopes == 0,
                    $"aura {materialItemId} keeps every parsed check scoped to its profession",
                    ref failures);
            }
        }

        private static string NormalizeEquipmentType(string value)
        {
            return (value ?? string.Empty).Trim().Trim('`').Trim('[', ']').Trim().ToLowerInvariant();
        }
    }
}
