#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunGameScanRegressionTests()
        {
            TestManifestAmbiguousEntries();
            TestManifestExplicitEntry();
            TestManifestInvalidEntryFallback();
            TestManifestEmptyAndScope();
            TestManifestOverlappingRoots();
        }

        private static void TestManifestAmbiguousEntries()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string first = fixture.WriteExecutable("Alpha.exe", "d3d11.dll");
                string second = fixture.WriteExecutable(@"Bin\Beta.exe", "d3d12.dll");
                var hits = new List<ScanHit>();
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScan.AddManifestHit(null, hits, roots, "游戏标题", fixture.Root, null);
                Eq(2, hits.Count);
                Eq(first, hits[0].Exe); Eq(second, hits[1].Exe);
                foreach (ScanHit hit in hits)
                {
                    Eq("游戏标题", hit.Name); Eq(fixture.Root, hit.Root);
                    Eq(true, hit.NeedsChoice);
                }
                GameScan.AddManifestHit(null, hits, roots, "Other platform", fixture.Root + "\\", null);
                Eq(2, hits.Count); Eq(1, roots.Count);
            }
            using (var fixture = new GenericExecutableFixture())
            {
                fixture.WriteExecutable("One.exe", "kernel32.dll");
                fixture.WriteExecutable("Two.exe", "kernel32.dll");
                var hits = new List<ScanHit>();
                GameScan.AddManifestHit(null, hits, new HashSet<string>(), "Dynamic graphics", fixture.Root, null);
                Eq(2, hits.Count); Eq(true, hits[0].NeedsChoice);
            }
        }

        private static void TestManifestExplicitEntry()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                fixture.WriteExecutable("Alpha.exe", "d3d11.dll");
                string second = fixture.WriteExecutable(@"Bin\Beta.exe", "d3d12.dll");
                var hits = new List<ScanHit>();
                GameScan.AddManifestHit(null, hits, new HashSet<string>(), "Title", "\"" + fixture.Root + "\\\"", "Bin/Beta.exe");
                Eq(1, hits.Count); Eq(second, hits[0].Exe); Eq(false, hits[0].NeedsChoice);
            }
        }

        private static void TestManifestInvalidEntryFallback()
        {
            using (var fixture = new GenericExecutableFixture())
            using (var outside = new GenericExecutableFixture())
            {
                string game = fixture.WriteExecutable("Game.exe", "d3d11.dll");
                fixture.WriteExecutable("Utility.exe", "kernel32.dll");
                string other = outside.WriteExecutable("Other.exe", "d3d12.dll");
                string broken = Path.Combine(fixture.Root, "Broken.exe");
                File.WriteAllText(broken, "not a PE");
                string[] entries = { other, broken, "Missing.exe", null };
                foreach (string entry in entries)
                {
                    var hits = new List<ScanHit>();
                    GameScan.AddManifestHit(null, hits, new HashSet<string>(), "Title", fixture.Root, entry);
                    Eq(1, hits.Count); Eq(game, hits[0].Exe); Eq(false, hits[0].NeedsChoice);
                }
            }
        }

        private static void TestManifestEmptyAndScope()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                var hits = new List<ScanHit>();
                var roots = new HashSet<string>();
                GameScan.AddManifestHit(null, hits, roots, "Title", fixture.Root, null);
                Eq(0, hits.Count); Eq(0, roots.Count);
                string game = fixture.WriteExecutable("Game.exe", "d3d11.dll");
                GameScan.AddManifestHit(fixture.Root + "-other", hits, roots, "Title", fixture.Root, null);
                Eq(0, hits.Count);
                GameScan.AddManifestHit(fixture.Root, hits, roots, "Title", fixture.Root, null);
                Eq(1, hits.Count); Eq(game, hits[0].Exe);
            }
        }

        private static void TestManifestOverlappingRoots()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string game = fixture.WriteExecutable(@"Install\Content\Alpha.exe", "d3d11.dll");
                string parent = Path.Combine(fixture.Root, "Install");
                string content = Path.Combine(parent, "Content");
                var hits = new List<ScanHit>();
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScan.AddManifestHit(null, hits, roots, "Official title", content, game);
                GameScan.AddManifestHit(null, hits, roots, "Parent fallback", parent, null);
                Eq(1, hits.Count); Eq("Official title", hits[0].Name); Eq(content, hits[0].Root);
                Eq(false, hits[0].NeedsChoice); Eq(true, roots.Contains(parent));

                string second = fixture.WriteExecutable(@"Install\Beta.exe", "d3d12.dll");
                roots.Remove(parent);
                GameScan.AddManifestHit(null, hits, roots, "Parent fallback", parent, null);
                Eq(2, hits.Count); Eq(game, hits[0].Exe); Eq(second, hits[1].Exe);
                Eq("Official title", hits[0].Name); Eq(content, hits[0].Root);
                Eq(true, hits[1].NeedsChoice);
            }
        }
    }
}
#endif
