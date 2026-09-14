// File purpose Uninstall-record path fallback regression, strings and its own temp directory only, no real registry I/O
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunGameScanInstalledRegressionTests()
        {
            TestInstalledCandidateExtraction();
            TestInstalledMissingLocationFallback();
            TestInstalledMissingIconFallback();
            TestInstalledUnresolvedDirectoryFallback();
            TestInstalledDuplicatePathsAndNames();
            TestInstalledRootScopeAndCancellation();
            TestInstalledParentRootDoesNotOverwritePlatform();
            return 7;
        }

        private static string WriteInstalledGameFixture(GenericExecutableFixture fixture, string directory)
        {
            string executable = fixture.WriteExecutable(Path.Combine(directory, @"Bin\Entry.exe"), "d3d11.dll");
            File.WriteAllText(Path.Combine(fixture.Root, Path.Combine(directory, "steam_api64.dll")), "fixture");
            return executable;
        }

        private static void TestInstalledCandidateExtraction()
        {
            string[] paths = GameScan.InstalledDirectoryCandidates(@"  ""C:\Games\Title\"" ",
                @"""C:\Games\Title\Bin\icon.ico"",-1",
                @"C:\Games\Title\Binaries\Win64\Remove.exe /quiet", @"C:/Games/Title/");
            Eq(3, paths.Length);
            Eq(@"C:\Games\Title", paths[0]);
            Eq(@"C:\Games\Title\Bin", paths[1]);
            Eq(@"C:\Games\Title\Binaries\Win64", paths[2]);

            paths = GameScan.InstalledDirectoryCandidates("relative", @"""unterminated", "MsiExec.exe /I{123}", null);
            Eq(0, paths.Length);
            paths = GameScan.InstalledDirectoryCandidates(null, null, @"C:\Games\Folder.exe\Title\uninstall.exe /S", null);
            Eq(1, paths.Length);
            Eq(@"C:\Games\Folder.exe\Title", paths[0]);
        }

        private static void TestInstalledMissingLocationFallback()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string executable = WriteInstalledGameFixture(fixture, "Title");
                string dir = Path.Combine(fixture.Root, "Title");
                string[] candidates = GameScan.InstalledDirectoryCandidates(Path.Combine(fixture.Root, "Removed"),
                    "\"" + Path.Combine(dir, @"Bin\icon.ico") + "\",0", null, null);
                var hits = new List<ScanHit>();
                GameScan.ScanInstalledRecord("Title", candidates, null, hits,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
                Eq(1, hits.Count); Eq(executable, hits[0].Exe); Eq(dir, hits[0].Root);
            }
        }

        private static void TestInstalledMissingIconFallback()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string executable = WriteInstalledGameFixture(fixture, "Title");
                string dir = Path.Combine(fixture.Root, "Title");
                string[] candidates = GameScan.InstalledDirectoryCandidates(null,
                    Path.Combine(fixture.Root, @"Removed\Old.exe") + ",0",
                    "\"" + Path.Combine(dir, "uninstall.exe") + "\" /quiet", null);
                var hits = new List<ScanHit>();
                GameScan.ScanInstalledRecord("Title", candidates, null, hits,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(), null);
                Eq(1, hits.Count); Eq(executable, hits[0].Exe);
            }
        }

        private static void TestInstalledUnresolvedDirectoryFallback()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string executable = WriteInstalledGameFixture(fixture, "Title");
                string signalOnly = Path.Combine(fixture.Root, "NoEntry");
                Directory.CreateDirectory(signalOnly);
                File.WriteAllText(Path.Combine(signalOnly, "steam_api64.dll"), "fixture");
                string utility = fixture.WriteExecutable(@"Utility\Editor.exe", "d3d11.dll");
                string[] candidates = GameScan.InstalledDirectoryCandidates(signalOnly, utility,
                    Path.Combine(fixture.Root, @"Removed\uninstall.exe"), Path.Combine(fixture.Root, "Title"));
                var hits = new List<ScanHit>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScan.ScanInstalledRecord("Title", candidates, null, hits, roots, seen, null);
                Eq(1, hits.Count); Eq(executable, hits[0].Exe);
                Eq(true, seen.Contains(signalOnly)); Eq(false, roots.Contains(signalOnly));
                // When the failed-scan leading directory is already seen, the game is still found via the record's fallback path
                hits.Clear(); roots.Clear(); seen.Remove(Path.Combine(fixture.Root, "Title"));
                GameScan.ScanInstalledRecord("Title", candidates, null, hits, roots, seen, null);
                Eq(1, hits.Count); Eq(executable, hits[0].Exe);
            }
        }

        private static void TestInstalledDuplicatePathsAndNames()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string first = fixture.WriteExecutable(@"FirstTitle\InternalName\Binaries\Win64\Entry.exe", "d3d11.dll");
                File.WriteAllText(Path.Combine(fixture.Root, @"FirstTitle\steam_api64.dll"), "fixture");
                string second = WriteInstalledGameFixture(fixture, "SecondTitle");
                string firstDir = Path.Combine(fixture.Root, "FirstTitle");
                string secondDir = Path.Combine(fixture.Root, "SecondTitle");
                var hits = new List<ScanHit>();
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScan.ScanInstalledRecord("Same display name", new[] { firstDir, secondDir }, null, hits, roots, seen, null);
                Eq(1, hits.Count); Eq(first, hits[0].Exe);
                // An already-recognized same directory terminates the same record without pulling in its install source
                GameScan.ScanInstalledRecord("Same display name", new[] { firstDir, secondDir }, null, hits, roots, seen, null);
                Eq(1, hits.Count);
                // The same display name does not stop another install directory from being recognized
                GameScan.ScanInstalledRecord("Same display name", new[] { secondDir }, null, hits, roots, seen, null);
                Eq(2, hits.Count); Eq(second, hits[1].Exe);

                GameScan.ScanInstalledRecord("Internal fallback title",
                    GameScan.InstalledDirectoryCandidates(null, first, null, null), null, hits, roots, seen, null);
                Eq(2, hits.Count); Eq(firstDir, hits[0].Root); Eq("Same display name", hits[0].Name);

                hits.Clear(); roots.Clear(); seen.Clear(); roots.Add(firstDir);
                GameScan.ScanInstalledRecord("Title", new[] { firstDir, secondDir }, null, hits, roots, seen, null);
                Eq(1, hits.Count); Eq(second, hits[0].Exe);
            }
        }

        private static void TestInstalledRootScopeAndCancellation()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string game = WriteInstalledGameFixture(fixture, "Title");
                WriteInstalledGameFixture(fixture, "Desktop");
                string dir = Path.Combine(fixture.Root, "Title");
                string shared = Path.Combine(fixture.Root, "Desktop");
                var hits = new List<ScanHit>();
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScan.ScanInstalledRecord("Title", new[] { shared, dir }, null, hits, roots, seen, delegate { return true; });
                Eq(0, hits.Count); Eq(0, seen.Count);
                GameScan.ScanInstalledRecord("Title", new[] { shared, dir }, null, hits, roots, seen, null);
                Eq(1, hits.Count); Eq(game, hits[0].Exe); Eq(false, seen.Contains(shared));

                hits.Clear(); roots.Clear(); seen.Clear();
                GameScan.ScanInstalledRecord("Title", new[] { dir }, dir + "-other", hits, roots, seen, null);
                Eq(0, hits.Count); Eq(0, seen.Count);
            }
        }

        private static void TestInstalledParentRootDoesNotOverwritePlatform()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string game = fixture.WriteExecutable(@"Install\Content\Entry.exe", "d3d11.dll");
                string parent = Path.Combine(fixture.Root, "Install");
                string content = Path.Combine(parent, "Content");
                File.WriteAllText(Path.Combine(parent, "steam_api64.dll"), "fixture");
                WriteInstalledGameFixture(fixture, "InstallSource");
                var hits = new List<ScanHit>();
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScan.AddManifestHit(null, hits, roots, "Official title", content, game);
                GameScan.ScanInstalledRecord("Uninstall title", new[] { parent, Path.Combine(fixture.Root, "InstallSource") },
                    null, hits, roots, new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
                Eq(1, hits.Count); Eq(game, hits[0].Exe); Eq("Official title", hits[0].Name);
                Eq(content, hits[0].Root); Eq(true, roots.Contains(parent));
                GameScan.ScanInstalledRecord("Second uninstall record", new[] { parent, Path.Combine(fixture.Root, "InstallSource") },
                    null, hits, roots, new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
                Eq(1, hits.Count); Eq("Official title", hits[0].Name); Eq(content, hits[0].Root);
            }
        }
    }
}
#endif
