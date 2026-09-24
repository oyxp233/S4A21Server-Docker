using DfoServer.Game.SecretShop;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class SecretShopOfferSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== SECRET_SHOP_OFFER selftest ===");

            const string catalogText = @"
[level section]
1 1 99
[/level section]
[level npc]
1 1002 100 100 100 100 100
[/level npc]
[npc]
1002
[level]
1 1 1001 0 0 0 1 1 1002 0 100 0 0 1 1
[/level]
[/npc]
";

            var catalog = SecretShopCatalog.Parse(catalogText);
            var npcA = SecretShopOfferFactory.RollNpc(
                catalog,
                dungeonId: 123,
                dungeonBasisLevel: 50,
                partySize: 2,
                _ => 0);
            var npcB = SecretShopOfferFactory.RollNpc(
                catalog,
                dungeonId: 123,
                dungeonBasisLevel: 50,
                partySize: 2,
                _ => 0);

            var failures = 0;
            if (npcA.NpcId != 1002 || npcB.NpcId != 1002)
                failures++;

            var itemsA = SecretShopOfferFactory.RollItems(
                catalog,
                npcA.NpcId,
                dungeonId: 123,
                dungeonBasisLevel: 50,
                _ => 0);
            var itemsB = SecretShopOfferFactory.RollItems(
                catalog,
                npcB.NpcId,
                dungeonId: 123,
                dungeonBasisLevel: 50,
                _ => 1);

            if (itemsA.Count != 1 || itemsB.Count != 1
                || itemsA[0].ItemId == itemsB[0].ItemId)
            {
                failures++;
            }

            var offerA = SecretShopOfferFactory.CreateForNpc(
                catalog,
                123,
                50,
                npcA.NpcId,
                _ => 0);
            var offerB = SecretShopOfferFactory.CreateForNpc(
                catalog,
                123,
                50,
                npcB.NpcId,
                _ => 1);

            if (!offerA.IsSecretShop || !offerB.IsSecretShop
                || offerA.Items.Single().ItemId == offerB.Items.Single().ItemId)
            {
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "SECRET_SHOP_OFFER selftest passed."
                    : $"SECRET_SHOP_OFFER selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }
    }
}
