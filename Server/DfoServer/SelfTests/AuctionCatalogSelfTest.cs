using DfoServer.Game.Auction.Rules;
using DfoServer.GameWorld.Auction;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class AuctionCatalogSelfTest
    {
        private const string Fingerprint = "963cd331342a0066c01cb189ec2cd822b8b8206065b109ec63ca664adb3da8d8";

        public static int Run()
        {
            var failures = 0;
            Console.WriteLine("=== AUCTION_CATALOG selftest ===");
            try
            {
                CheckTemplateProjection(ref failures);
                CheckRandomCategory(ref failures);
                CheckEntitlement(ref failures);
                Check("unverified fees cannot produce an executable rate",
                    AuctionFeeRules.Availability == AuctionFeeRuleAvailability.Unavailable,
                    ref failures);
            }
            catch (Exception exception)
            {
                Console.WriteLine("[FAIL] unexpected exception: " + exception);
                failures++;
            }
            Console.WriteLine("AUCTION_CATALOG failures=" + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void CheckTemplateProjection(ref int failures)
        {
            var resources = new Dictionary<string, string>
            {
                ["equipment/equipment.lst"] = "101 `weapon/test.equ`",
                ["stackable/stackable.lst"] = "201 `material/test.stk`",
                ["equipment/weapon/test.equ"] = @"
[name]
`已知武器`
[minimum level]
50
[rarity]
3
[equipment type]
`[weapon]` 0
[sub type]
2
[usable job]
`[swordman]` `[fighter]`
[/usable job]
[random option]
1
[auction free]
[attach type]
`[sealing]`
[impossible contents]
`gift`
[/impossible contents]",
                ["stackable/material/test.stk"] = @"
[name]
`已知材料`
[minimum level]
1
[rarity]
2
[stackable type]
`[material]` 0
[usable job]
`[all]`
[/usable job]
[trade limit max]
3
[attach type]
`[trade]`"
            };
            var reads = 0;
            string Read(string path) { reads++; return resources.TryGetValue(path, out var text) ? text : null; }
            var equipment = EquipmentFile.Parse(resources["equipment/weapon/test.equ"]);
            var stackable = StackableItemFile.Parse(resources["stackable/material/test.stk"]);
            var catalog = AuctionTemplateCatalog.Load(Read, Fingerprint.ToUpperInvariant(),
                id => id == 101 ? equipment : null, id => id == 201 ? stackable : null);
            Check("uses injected models and exact normalized resource fingerprint",
                reads == 2 && catalog.SourcePvfSha256 == Fingerprint && catalog.Templates.Count == 2,
                ref failures);
            Check("auction free preserves exact model tag presence without granting eligibility",
                catalog.Templates[101].HasAuctionFree && !catalog.Templates[201].HasAuctionFree,
                ref failures);
            Check("retains template metadata and all usable jobs",
                catalog.TryGet(101, out var weapon) && weapon.DisplayName == "已知武器"
                && weapon.MinimumLevel == 50 && weapon.Rarity == 3 && weapon.SubType == 2
                && weapon.UsableJobLabels.Count == 2 && weapon.HasRandomOptionDefinition,
                ref failures);
            Check("stackable metadata is projected without equipment fallback",
                catalog.TryGet(201, out var material) && material.Kind == "stackable"
                && material.TradeLimit == 3 && material.UsableJobLabels.Count == 1
                && material.UsableJobLabels[0] == "all", ref failures);
            equipment.Name = "mutated";
            equipment.ImpossibleContentItems.Clear();
            Check("projection does not retain mutable PVF model collections",
                weapon.DisplayName == "已知武器" && weapon.ImpossibleContents.Count == 1,
                ref failures);
            Check("unknown template fails lookup without special metadata fallback",
                !catalog.TryGet(999, out _) && !catalog.TryGet(0, out _), ref failures);
            var readCount = reads;
            catalog.TryGet(101, out _);
            catalog.TryGet(201, out _);
            Check("lookups never reread PVF", reads == readCount, ref failures);
            CheckThrows<InvalidDataException>("missing known template fails catalog load",
                () => AuctionTemplateCatalog.Load(Read, Fingerprint, _ => null, _ => stackable), ref failures);
            CheckThrows<ArgumentException>("resource fingerprint is mandatory",
                () => AuctionTemplateCatalog.Load(Read, "not-a-hash"), ref failures);
            resources["stackable/stackable.lst"] = "101 `material/test.stk`";
            CheckThrows<InvalidDataException>("ambiguous template ownership is rejected",
                () => AuctionTemplateCatalog.Load(Read, Fingerprint, _ => equipment, _ => stackable), ref failures);
            resources["stackable/stackable.lst"] = "201 `../outside.stk`";
            CheckThrows<InvalidDataException>("resource traversal is rejected",
                () => AuctionTemplateCatalog.Load(Read, Fingerprint, _ => equipment, _ => stackable), ref failures);
        }

        private static void CheckRandomCategory(ref int failures)
        {
            var catalog = AuctionRandomCategoryCatalog.Parse(
                "[random category] -1 `` `属性` 0 1 `属性` `力量` 4 15 70 100 140 "
                + "619 `其它` `全属强` 3 232 237 163 [/random category]", Fingerprint);
            Check("headings are excluded and category ids retain values above byte range",
                catalog.Categories.Count == 2 && !catalog.TryGet(-1, out _)
                && catalog.TryGet(619, out var category) && category.OptionTypes.Count == 3
                && category.OptionTypes[0] == 232, ref failures);
            CheckThrows<InvalidDataException>("truncated category cannot silently drop filters",
                () => AuctionRandomCategoryCatalog.Parse("[random category] 1 `a` `b` 2 15 [/random category]", Fingerprint), ref failures);
            CheckThrows<InvalidDataException>("option type overflow is rejected before narrowing",
                () => AuctionRandomCategoryCatalog.Parse("[random category] 1 `a` `b` 1 256 [/random category]", Fingerprint), ref failures);
            CheckThrows<InvalidDataException>("duplicate category cannot overwrite filters",
                () => AuctionRandomCategoryCatalog.Parse("[random category] 1 `a` `b` 1 15 1 `a` `b` 1 70 [/random category]", Fingerprint), ref failures);
            CheckThrows<InvalidDataException>("missing mapping is not an empty match-all catalog",
                () => AuctionRandomCategoryCatalog.Parse(null, Fingerprint), ref failures);
        }

        private static void CheckEntitlement(ref int failures)
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using (var seed = connection.CreateCommand())
            {
                seed.CommandText = @"
CREATE TABLE account_premiums(account_id INTEGER, premium_type INTEGER, end_time INTEGER NOT NULL,
 PRIMARY KEY(account_id,premium_type));
INSERT INTO account_premiums VALUES(7,29,1000),(7,5,999999),(8,29,2000);";
                seed.ExecuteNonQuery();
            }
            var atBoundary = AuctionEntitlementReader.Read(connection, null, 7, 1000);
            Check("expiry equality is inactive and future expiry is active",
                !atBoundary.IsActive && atBoundary.ExpiresAtUnix == 1000
                && AuctionEntitlementReader.Read(connection, null, 7, 999).IsActive,
                ref failures);
            Check("other premium types and accounts cannot grant auction entitlement",
                !AuctionEntitlementReader.Read(connection, null, 7, 1001).IsActive
                && !AuctionEntitlementReader.Read(connection, null, 9, 0).ExpiresAtUnix.HasValue,
                ref failures);
            using (var transaction = connection.BeginTransaction())
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE account_premiums SET end_time=3000 WHERE account_id=7 AND premium_type=29;";
                update.ExecuteNonQuery();
                Check("reads use caller transaction and explicit time",
                    AuctionEntitlementReader.Read(connection, transaction, 7, 2000).IsActive,
                    ref failures);
                transaction.Rollback();
            }
            Check("reader does not change entitlement or mutate prior snapshots",
                AuctionEntitlementReader.Read(connection, null, 7, 1000).ExpiresAtUnix == 1000
                && !atBoundary.IsActive, ref failures);
            using var other = new SqliteConnection("Data Source=:memory:");
            other.Open();
            using var wrongTransaction = other.BeginTransaction();
            CheckThrows<ArgumentException>("foreign transaction rejected",
                () => AuctionEntitlementReader.Read(connection, wrongTransaction, 7, 1000), ref failures);
            CheckThrows<ArgumentOutOfRangeException>("invalid owner rejected",
                () => AuctionEntitlementReader.Read(connection, null, 0, 1000), ref failures);
        }

        private static void CheckThrows<T>(string label, Action action, ref int failures) where T : Exception
        {
            try { action(); Check(label, false, ref failures); }
            catch (T) { Check(label, true, ref failures); }
        }

        private static void Check(string label, bool passed, ref int failures)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label}");
            if (!passed) failures++;
        }
    }
}
