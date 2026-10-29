using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;

namespace DfoServer.SelfTests
{
    // Read-only integration profile for the changed indexed composition path.
    // Exactly two requested template projections; never enumerate Values.
    public static class AuctionRuntimeCatalogSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            { Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] auction runtime catalog: {name}"); if (condition) passed++; else failed++; }
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            long peakBefore = process.PeakWorkingSet64;
            var total = Stopwatch.StartNew();
            try
            {
                // Necessary verification: LoadIndexed and the fingerprint owner
                // are new code paths. This is not another broad PVF investigation.
                string path = GameWorldConfig.PvfArchivePath;
                var stage = Stopwatch.StartNew();
                string diskHash;
                using (var stream = File.OpenRead(path))
                    diskHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                Console.WriteLine($"AuctionRuntimeCatalog profile: fileSha256={diskHash}; hashElapsedMs={stage.Elapsed.TotalMilliseconds:F1}");

                stage.Restart();
                // Use the formal composition function, including its injected
                // shared metadata readers, rather than copying loading logic.
                var catalog = ServerRuntimeBuilder.LoadAuctionCatalog();
                Console.WriteLine($"AuctionRuntimeCatalog profile: indexElapsedMs={stage.Elapsed.TotalMilliseconds:F1}; indexedRows={catalog.Templates.Count}");
                Check("catalog shared archive and current file fingerprints agree", catalog.SourcePvfSha256 == PvfArchiveAccessor.SourceSha256
                    && PvfArchiveAccessor.SourceSha256 == diskHash);
                var equipmentIndex = ItemMetadataResolver.EquipmentList.Value;
                var stackableIndex = LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst"));
                var expectedKeys = equipmentIndex.Entries.Select(entry => entry.Id)
                    .Concat(stackableIndex.Entries.Select(entry => entry.Id)).ToHashSet();
                Check("indexed Count matches the configured archive indexes", catalog.Templates.Count == expectedKeys.Count);
                Check("Keys match both configured indexes without requesting projections", expectedKeys.SetEquals(catalog.Templates.Keys));
                Check("virtual currency id0 remains in the descriptive index", catalog.Templates.ContainsKey(0));
                Check("virtual currency id0 is outside ordinary instance kind resolution",
                    !ItemMetadataResolver.TryResolveItemKind(0, out var zeroKind) && zeroKind == ItemCore.KindUnknown);
                Check("currency slot0 remains an independent virtual-count slot", InventoryService.TryResolveMainVirtualItemId(0, out var virtualId)
                    && virtualId == 0 && InventoryService.MainSlotStart > InventoryService.MainVirtualCurrencySlotEnd);

                stage.Restart();
                int weaponId = equipmentIndex.Entries.First(entry => entry.Id > 0).Id;
                var weaponEntry = ItemMetadataResolver.EquipmentList.Value.GetById(weaponId);
                Check("sample equipment is present in shared equipment index", weaponEntry != null);
                if (weaponEntry == null) throw new InvalidDataException("Sample equipment missing from configured index.");
                bool knownWeapon = catalog.TryGet(weaponId, out var weapon);
                Check("known weapon projection is loaded on demand", knownWeapon && weapon.Kind == "equipment");
                if (!knownWeapon) throw new InvalidDataException("Known weapon projection unavailable.");
                string equipmentResource = "equipment/" + weaponEntry.FilePath.Replace('\\', '/');
                Check("weapon resource matches the exact shared equipment index", weapon.ResourcePath == equipmentResource
                    && weapon.SourcePvfSha256 == diskHash);
                bool hasEquipment = ItemMetadataResolver.TryLoadEquipmentFile(weaponId, out var equipment);
                Check("weapon projection copies the shared metadata model", hasEquipment && weapon.DisplayName == equipment.Name
                    && weapon.MinimumLevel == equipment.MinimumLevel && weapon.Rarity == equipment.Rarity
                    && weapon.EquipmentType == equipment.EquipmentType && weapon.SubType == equipment.SubType);
                var weaponMetadata = ItemMetadataResolver.Resolve(weaponId);
                Check("weapon general metadata shares kind path and descriptive facts", weaponMetadata.ItemKind == "equipment"
                    && "equipment/" + weaponMetadata.PvfFilePath.Replace('\\', '/') == equipmentResource
                    && weaponMetadata.MinimumLevel == weapon.MinimumLevel && weaponMetadata.Rarity == weapon.Rarity
                    && EquipmentTypeInfo.ParseOrUnknown(weaponMetadata.EquipmentType) == EquipmentTypeInfo.ParseOrUnknown(weapon.EquipmentType));
                Check("repeated weapon lookup retains the same projected instance", catalog.TryGet(weaponId, out var repeatedWeapon)
                    && ReferenceEquals(weapon, repeatedWeapon) && ReferenceEquals(weapon, catalog.Templates[weaponId]));
                Check("shared equipment model cache remains its sole model owner", ItemMetadataResolver.TryLoadEquipmentFile(weaponId, out var repeatedEquipment)
                    && ReferenceEquals(equipment, repeatedEquipment));

                // Exclude ids using only the shared equipment index and catalog
                // Keys. Do not open/parse all bodies to discover a stackable.
                var equipmentIds = new HashSet<int>(ItemMetadataResolver.EquipmentList.Value.Entries.Select(entry => entry.Id));
                int stackId = catalog.Templates.Keys.First(id => id > InventoryService.MainVirtualCurrencySlotEnd && !equipmentIds.Contains(id));
                bool knownStack = catalog.TryGet(stackId, out var stack);
                Check("first non-equipment indexed id resolves as stackable", knownStack && stack.Kind == "stackable"
                    && stack.ResourcePath.StartsWith("stackable/", StringComparison.Ordinal) && stack.SourcePvfSha256 == diskHash);
                if (!knownStack) throw new InvalidDataException("Indexed stackable projection unavailable.");
                bool hasStack = ItemMetadataResolver.TryLoadStackableFile(stackId, out var stackable);
                Check("stackable projection copies its shared metadata model", hasStack && stack.DisplayName == stackable.Name
                    && stack.MinimumLevel == stackable.MinimumLevel && stack.Rarity == stackable.Rarity && stack.StackableType == stackable.StackableType);
                var stackMetadata = ItemMetadataResolver.Resolve(stackId);
                Check("stackable general metadata shares the exact resource and kind", stackMetadata.ItemKind == "stackable"
                    && "stackable/" + stackMetadata.PvfFilePath.Replace('\\', '/') == stack.ResourcePath
                    && stackMetadata.StackableType == stack.StackableType && stackMetadata.MinimumLevel == stack.MinimumLevel && stackMetadata.Rarity == stack.Rarity);
                Check("repeated stack lookup retains the same projected instance", catalog.TryGet(stackId, out var repeatedStack)
                    && ReferenceEquals(stack, repeatedStack) && ReferenceEquals(stack, catalog.Templates[stackId]));
                Check("shared stack model cache remains its sole model owner", ItemMetadataResolver.TryLoadStackableFile(stackId, out var repeatedStackable)
                    && ReferenceEquals(stackable, repeatedStackable));
                Check("unknown id is false without fallback", !catalog.Templates.ContainsKey(int.MaxValue)
                    && !catalog.TryGet(int.MaxValue, out _) && !catalog.TryGet(-1, out _));
                Console.WriteLine($"AuctionRuntimeCatalog profile: projectedTemplateIds={weaponId},{stackId}; requestedProjectionCount=2; targetedElapsedMs={stage.Elapsed.TotalMilliseconds:F1}");
            }
            catch (Exception exception)
            {
                Console.WriteLine("[FAIL] auction runtime catalog profile: " + exception);
                failed++;
            }
            finally
            {
                total.Stop(); process.Refresh();
                Console.WriteLine($"AuctionRuntimeCatalog diagnostics: elapsedMs={total.Elapsed.TotalMilliseconds:F1}; processPeakWorkingSetBeforeBytes={peakBefore}; processPeakWorkingSetAfterBytes={process.PeakWorkingSet64}; process-wide peaks, not game FPS or exclusive catalog allocation.");
            }
            Console.WriteLine($"AuctionRuntimeCatalogSelfTest: passed={passed} failed={failed}; configured archive, no server/client or database.");
            return failed == 0 ? 0 : 1;
        }
    }
}
