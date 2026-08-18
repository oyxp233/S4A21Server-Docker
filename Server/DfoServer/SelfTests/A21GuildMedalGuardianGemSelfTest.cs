using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network.Parsers.Inventory;
using System;

namespace DfoServer.SelfTests
{
    public static class A21GuildMedalGuardianGemSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_GUILD_MEDAL_GUARDIAN_GEM selftest ===");
            var failures = 0;

            var requestBody = new byte[]
            {
                0x8C, 0xAD, 0xFB, 0x05, 0x31, 0x00, 0xBA, 0x5F, 0x01, 0x00, 0x00,
            };
            Check(
                "USE_GEM request parses medal/slot/gem/socket",
                UseGuardianGemRequest.TryParse(requestBody, out var request)
                && request.EquippedMedalItemTemplateId == 100380044
                && request.MaterialSlotIndex == 49
                && request.GuardianGemItemTemplateId == 90042
                && request.SocketIndex == 0,
                ref failures);

            var stackable = PvfLib.StackableItemFile.Parse(
                "[stackable type]\n"
                + "    `flag gem`\n"
                + "[/stackable type]\n"
                + "[enchant]\n"
                + "    [move speed]\n"
                + "        30\n"
                + "[/enchant]\n");
            Check(
                "[enchant] tag parses guardian gem effect type",
                stackable != null
                && stackable.GuardianGemEnchantEntries.Count == 1
                && string.Equals(stackable.GuardianGemEnchantEntries[0].EffectType, "move speed", StringComparison.OrdinalIgnoreCase)
                && stackable.GuardianGemEnchantEntries[0].Values.Count == 1
                && stackable.GuardianGemEnchantEntries[0].Values[0] == 30,
                ref failures);

            var inlineStackable = PvfLib.StackableItemFile.Parse(
                "[enchant]\n"
                + "    [move speed] 30\n"
                + "[/enchant]\n");
            Check(
                "[enchant] inline values also parse",
                inlineStackable != null
                && inlineStackable.GuardianGemEnchantEntries.Count == 1
                && string.Equals(inlineStackable.GuardianGemEnchantEntries[0].EffectType, "move speed", StringComparison.OrdinalIgnoreCase)
                && inlineStackable.GuardianGemEnchantEntries[0].Values.Count == 1
                && inlineStackable.GuardianGemEnchantEntries[0].Values[0] == 30,
                ref failures);

            Check(
                "guardian gem key encodes as itemId-89999",
                ItemCore.EncodeGuardianGemKey(90032) == 33
                && ItemCore.DecodeGuardianGemItemId(33) == 90032,
                ref failures);

            var inventory = new InventoryService(1, 1);
            var medal = new ItemCore
            {
                ItemKind = ItemCore.KindGuildMedal,
                ItemId = 100380044,
            };
            medal.SetGuardianGemItemId(1, 90032);
            inventory.SetItem(InventoryListType.Equipment, (short)EquipmentType.GuildMedal, medal);
            inventory.SetItem(InventoryListType.GuildMedal, 49, new ItemCore
            {
                ItemKind = ItemCore.KindGuardianGem,
                ItemId = 90032,
                Count = 1,
            });
            Check(
                "same guardian gem effect rejects duplicate socket insert",
                !InventoryEquipmentMutationService.TryUseGuardianGem(
                    inventory,
                    new GuardianGemUseCommand
                    {
                        EquippedMedalItemTemplateId = 100380044,
                        MaterialSlotIndex = 49,
                        GuardianGemItemTemplateId = 90032,
                        SocketIndex = 0,
                    },
                    out var duplicateResult)
                && duplicateResult != null
                && duplicateResult.ErrorCode == GuardianGemUseResult.ErrorGuardianGemMissing,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_GUILD_MEDAL_GUARDIAN_GEM selftest passed."
                    : $"A21_GUILD_MEDAL_GUARDIAN_GEM selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
