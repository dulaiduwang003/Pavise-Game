#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunGameScanShortcutRegressionTests()
        {
            TestShortcutGameDiscovery();
            TestShortcutRejectsAppsAndBrokenLinks();
            TestShortcutAmbiguousAndToolEntries();
            TestShortcutScopeCancellationAndDepth();
        }

        private static string WriteScanShortcut(GenericExecutableFixture fixture, string relative, string target)
        {
            string path = Path.Combine(fixture.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Eq(true, GameExecutableResolver.CreateShortcutForTest(path, target));
            return path;
        }

        private static void MarkShortcutGame(string executable)
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable), "steam_appid.txt"), "123");
        }

        private static void TestShortcutGameDiscovery()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string game = fixture.WriteExecutable(@"PortableTitle\Bin\Win64\Entry.exe", "d3d11.dll");
                MarkShortcutGame(game);
                WriteScanShortcut(fixture, @"Desktop\中文游戏名.lnk", game);
                WriteScanShortcut(fixture, @"StartMenu\Publisher\SameGame.lnk", game);
                string dir = Path.Combine(fixture.Root, "PortableTitle");
                string[] locations = { Path.Combine(fixture.Root, "Desktop"), Path.Combine(fixture.Root, "StartMenu") };
                var hits = new List<ScanHit>();
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScan.ScanShortcutDirectories(locations, null, hits, roots, null);
                Eq(1, hits.Count); Eq(1, roots.Count);
                Eq(game, hits[0].Exe); Eq(dir, hits[0].Root); Eq("中文游戏名", hits[0].Name);
                Eq(false, hits[0].NeedsChoice);

                hits.Clear(); roots.Clear();
                GameScan.AddManifestHit(null, hits, roots, "Official title", dir, game);
                GameScan.ScanShortcutDirectories(locations, null, hits, roots, null);
                Eq(1, hits.Count); Eq("Official title", hits[0].Name);
            }
        }

        private static void TestShortcutRejectsAppsAndBrokenLinks()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string app = fixture.WriteExecutable(@"DesktopApp\Entry.exe", "d3d11.dll");
                string game = fixture.WriteExecutable(@"PortableTitle\Entry.exe", "d3d12.dll");
                MarkShortcutGame(game);
                WriteScanShortcut(fixture, @"Links\App.lnk", app);
                WriteScanShortcut(fixture, @"Links\Stale.lnk", Path.Combine(fixture.Root, @"Missing\Gone.exe"));
                File.WriteAllText(Path.Combine(fixture.Root, @"Links\Broken.lnk"), "invalid shortcut");
                WriteScanShortcut(fixture, @"Links\Game.lnk", game);
                var hits = new List<ScanHit>();
                GameScan.ScanShortcutDirectories(new[] { Path.Combine(fixture.Root, "Links") }, null, hits,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
                Eq(1, hits.Count); Eq(game, hits[0].Exe);
            }
        }

        private static void TestShortcutAmbiguousAndToolEntries()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string first = fixture.WriteExecutable(@"Multiple\Alpha.exe", "d3d11.dll");
                string second = fixture.WriteExecutable(@"Multiple\Beta.exe", "d3d12.dll");
                MarkShortcutGame(first);
                WriteScanShortcut(fixture, @"Links\One.lnk", first);
                WriteScanShortcut(fixture, @"Links\Two.lnk", second);
                var hits = new List<ScanHit>();
                GameScan.ScanShortcutDirectories(new[] { Path.Combine(fixture.Root, "Links") }, null, hits,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
                Eq(2, hits.Count);
                foreach (ScanHit hit in hits) { Eq(true, hit.NeedsChoice); Eq("Multiple", hit.Name); }
            }
            using (var fixture = new GenericExecutableFixture())
            {
                string game = fixture.WriteExecutable(@"ActualTitle\Entry.exe", "d3d12.dll");
                string tool = fixture.WriteExecutable(@"ActualTitle\Utility.exe", "kernel32.dll");
                MarkShortcutGame(game);
                WriteScanShortcut(fixture, @"Links\Remove application.lnk", tool);
                var hits = new List<ScanHit>();
                GameScan.ScanShortcutDirectories(new[] { Path.Combine(fixture.Root, "Links") }, null, hits,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
                Eq(1, hits.Count); Eq(game, hits[0].Exe); Eq("ActualTitle", hits[0].Name);
            }
        }

        private static void TestShortcutScopeCancellationAndDepth()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string game = fixture.WriteExecutable(@"DeepTitle\Entry.exe", "d3d11.dll");
                string other = fixture.WriteExecutable(@"OutsideDepth\Entry.exe", "d3d11.dll");
                MarkShortcutGame(game); MarkShortcutGame(other);
                WriteScanShortcut(fixture, @"Links\A\B\C\D\Game.lnk", game);
                WriteScanShortcut(fixture, @"Links\A\B\C\D\E\Other.lnk", other);
                string[] locations = { Path.Combine(fixture.Root, "Links") };
                var hits = new List<ScanHit>();
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScan.ScanShortcutDirectories(locations, null, hits, roots, delegate { return true; });
                Eq(0, hits.Count); Eq(0, roots.Count);
                GameScan.ScanShortcutDirectories(locations, Path.Combine(fixture.Root, "Elsewhere"), hits, roots, null);
                Eq(0, hits.Count);
                GameScan.ScanShortcutDirectories(locations, null, hits, roots, null);
                Eq(1, hits.Count); Eq(game, hits[0].Exe);
            }
        }
    }
}
#endif
