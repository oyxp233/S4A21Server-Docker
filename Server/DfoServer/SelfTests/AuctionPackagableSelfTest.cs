using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.GameWorld.Auction;
using PvfLib;

namespace DfoServer.SelfTests
{
    // Exact raw-node scalar projection, not a complete native eligibility proof.
    public static class AuctionPackagableSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] auction packagable: {name}"); if (ok) passed++; else failed++; }
            try
            {
                const string hash = "963cd331342a0066c01cb189ec2cd822b8b8206065b109ec63ca664adb3da8d8";
                var cases = new (string Name, string Tags, AuctionPackagableGate Gate, int Count)[] {
                    ("absent", "", AuctionPackagableGate.Passed, 0),
                    ("zero", "[packagable]\n0\n", AuctionPackagableGate.Rejected, 1),
                    ("one", "[packagable]\n1\n", AuctionPackagableGate.Passed, 1),
                    ("negative nonzero", "[packagable]\n-1\n", AuctionPackagableGate.Passed, 1),
                    ("positive signed", "[packagable]\n+2\n", AuctionPackagableGate.Passed, 1),
                    ("signed zero", "[packagable]\n-0\n", AuctionPackagableGate.Rejected, 1),
                    ("minimum int", "[packagable]\n-2147483648\n", AuctionPackagableGate.Passed, 1),
                    ("maximum int", "[packagable]\n2147483647\n", AuctionPackagableGate.Passed, 1),
                    ("zero after one", "[packagable]\n1\n[packagable]\n0\n", AuctionPackagableGate.Rejected, 2),
                    ("one after zero", "[packagable]\n0\n[packagable]\n1\n", AuctionPackagableGate.Rejected, 2),
                    ("all nonzero repeats", "[packagable]\n1\n[packagable]\n-2\n", AuctionPackagableGate.Passed, 2),
                    ("missing scalar", "[packagable]\n", AuctionPackagableGate.Unavailable, 1),
                    ("text scalar", "[packagable]\ninvalid\n", AuctionPackagableGate.Unavailable, 1),
                    ("integer overflow", "[packagable]\n2147483648\n", AuctionPackagableGate.Unavailable, 1),
                    ("fraction", "[packagable]\n0.0\n", AuctionPackagableGate.Unavailable, 1),
                    ("extra scalar same line", "[packagable]\n1 0\n", AuctionPackagableGate.Unavailable, 1),
                    ("extra scalar next line", "[packagable]\n1\n0\n", AuctionPackagableGate.Unavailable, 1),
                    ("quoted scalar", "[packagable]\n`0`\n", AuctionPackagableGate.Unavailable, 1),
                    ("missing before nonzero", "[packagable]\n[packagable]\n1\n", AuctionPackagableGate.Unavailable, 2),
                    ("zero plus malformed", "[packagable]\n0\n[packagable]\ninvalid\n", AuctionPackagableGate.Unavailable, 2),
                    ("nested scalar", "[other]\n[packagable]\n1\n[/other]\n", AuctionPackagableGate.Unavailable, 1),
                    ("block scalar", "[packagable]\n1\n[/packagable]\n", AuctionPackagableGate.Unavailable, 1),
                    ("tag whitespace", "[ packagable ]\n1\n", AuctionPackagableGate.Unavailable, 1)
                };
                var temporalCases = new (string Name, string Tags, bool Absent)[] {
                    ("absent", "", true),
                    ("usable period0", "[usable period]\n0\n", false),
                    ("expiration date0", "[expiration date]\n0\n", false),
                    ("missing temporal value", "[usable period]\n", false),
                    ("malformed temporal value", "[expiration date]\ninvalid\n", false),
                    ("uppercase", "[USABLE PERIOD]\n0\n", false),
                    ("nested", "[other]\n[expiration date]\n0\n[/other]\n", false),
                    ("duplicate", "[usable period]\n0\n[Usable Period]\n1\n", false),
                    ("whitespace tag", "[ expiration date ]\n0\n", false)
                };
                foreach (bool indexed in new[] { false, true })
                {
                    var files = new Dictionary<string, string>();
                    foreach (var kind in new[] { "equipment", "stackable" })
                    {
                        int baseId = kind == "equipment" ? 1000 : 2000;
                        string extension = kind == "equipment" ? ".equ" : ".stk";
                        files[kind + "/" + kind + ".lst"] = string.Join("\n", Enumerable.Range(0, cases.Length)
                            .Select(i => (baseId + i) + " `" + i + extension + "`"));
                        for (int i = 0; i < cases.Length; i++)
                            files[kind + "/" + i + extension] = "[name]\n`fixture`\n"
                                + (kind == "equipment" ? "[equipment type]\n`[weapon]` 0\n" : "[stackable type]\n`[material]` 0\n")
                                + cases[i].Tags + "[attach type]\n`[free]`\n";
                        for (int i = 0; i < temporalCases.Length; i++)
                        {
                            int id = baseId + 100 + i;
                            string file = "time" + i + extension;
                            files[kind + "/" + kind + ".lst"] += "\n" + id + " `" + file + "`";
                            files[kind + "/" + file] = "[name]\n`fixture`\n"
                                + (kind == "equipment" ? "[equipment type]\n`[weapon]` 0\n" : "[stackable type]\n`[material]` 0\n")
                                + temporalCases[i].Tags + "[attach type]\n`[free]`\n";
                        }
                    }
                    string Read(string path) => files.TryGetValue(path, out var text) ? text : null;
                    var catalog = indexed ? AuctionTemplateCatalog.LoadIndexed(Read, hash) : AuctionTemplateCatalog.Load(Read, hash);
                    foreach (int baseId in new[] { 1000, 2000 })
                        for (int i = 0; i < cases.Length; i++)
                        {
                            var projection = catalog.Templates[baseId + i];
                            Check($"{(indexed ? "indexed" : "eager")} {projection.Kind} {cases[i].Name}",
                                projection.PackagableGate == cases[i].Gate && projection.PackagableTagCount == cases[i].Count);
                        }
                    foreach (int baseId in new[] { 1000, 2000 })
                        for (int i = 0; i < temporalCases.Length; i++)
                            Check($"{(indexed ? "indexed" : "eager")} temporal absence {baseId} {temporalCases[i].Name}",
                                catalog.Templates[baseId + 100 + i].TemporalTagsAbsent == temporalCases[i].Absent);
                    var originalModel = EquipmentFile.Parse(files["equipment/1.equ"]);
                    var snapshot = AuctionTemplateCatalog.LoadIndexed(Read, hash, _ => originalModel).Templates[1001];
                    originalModel.Root.Children.Clear();
                    Check("packagable facts do not retain mutable raw-node state " + indexed,
                        snapshot.PackagableGate == AuctionPackagableGate.Rejected && snapshot.PackagableTagCount == 1);
                }
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] unexpected auction packagable exception: " + ex); failed++; }
            Console.WriteLine($"AuctionPackagableSelfTest: passed={passed} failed={failed}; raw-node in-memory resources only.");
            return failed == 0 ? 0 : 1;
        }
    }
}
