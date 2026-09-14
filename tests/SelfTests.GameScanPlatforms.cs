// File purpose Platform manifest compatibility regression, reads only its own temp directory, no real platform or registry access
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunGameScanPlatformRegressionTests()
        {
            TestScanSteamLibraryFormats();
            TestScanSteamLockedLibraryList();
            TestScanManifestJsonStrings();
            TestScanEpicEscapedPaths();
            return 4;
        }

        private static string WriteScanSteamManifest(GenericExecutableFixture fixture,
            string library, string name, int appId)
        {
            string executable = fixture.WriteExecutable(Path.Combine(library,
                @"steamapps\common\" + name + "\\Entry.exe"), "d3d11.dll");
            File.WriteAllText(Path.Combine(fixture.Root, Path.Combine(library,
                "steamapps\\appmanifest_" + appId + ".acf")),
                "\"AppState\" { \"name\" \"" + name + "\" \"installdir\" \"" + name + "\" }");
            return executable;
        }

        private static void TestScanSteamLibraryFormats()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string first = WriteScanSteamManifest(fixture, "Steam", "DefaultGame", 1);
                string second = WriteScanSteamManifest(fixture, "Modern", "ModernGame", 2);
                string third = WriteScanSteamManifest(fixture, "Legacy", "LegacyGame", 3);
                string modern = Path.Combine(fixture.Root, "Modern").Replace("\\", "\\\\");
                string legacy = Path.Combine(fixture.Root, "Legacy").Replace('\\', '/');
                File.WriteAllText(Path.Combine(fixture.Root, @"Steam\steamapps\libraryfolders.vdf"),
                    "\"libraryfolders\" { \"0\" \"C:\\Bad\0Path\" \"1\" { \"path\" \"" + modern
                    + "\" \"apps\" { \"123456\" \"987654\" } } \"2\" \"" + legacy + "\" }");
                var hits = new List<ScanHit>();
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScan.FromSteamLibraries(Path.Combine(fixture.Root, "Steam"), null, hits, roots);
                Eq(3, hits.Count);
                Eq(true, hits.Exists(delegate(ScanHit hit) { return hit.Exe == first; }));
                Eq(true, hits.Exists(delegate(ScanHit hit) { return hit.Exe == second; }));
                Eq(true, hits.Exists(delegate(ScanHit hit) { return hit.Exe == third; }));

                hits.Clear(); roots.Clear();
                GameScan.FromSteamLibraries(Path.Combine(fixture.Root, "Steam"),
                    Path.Combine(fixture.Root, "Modern"), hits, roots);
                Eq(1, hits.Count);
                Eq(second, hits[0].Exe);
            }
        }

        private static void TestScanSteamLockedLibraryList()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string executable = WriteScanSteamManifest(fixture, "Steam", "DefaultGame", 1);
                string libraryList = Path.Combine(fixture.Root, @"Steam\steamapps\libraryfolders.vdf");
                File.WriteAllText(libraryList, "\"libraryfolders\" { }");
                var hits = new List<ScanHit>();
                using (var locked = new FileStream(libraryList, FileMode.Open, FileAccess.Read, FileShare.None))
                    GameScan.FromSteamLibraries(Path.Combine(fixture.Root, "Steam"), null, hits,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                Eq(1, hits.Count);
                Eq(executable, hits[0].Exe);
            }
        }

        private static void TestScanManifestJsonStrings()
        {
            string json = "{\"Note\":\"\\\"InstallLocation\\\":\\\"C:\\\\Wrong\\\"\","
                + "\"InstallLocation\":\"C:\\\\Games\\\\\\u6d4b\\u8bd5\","
                + "\"DisplayName\":\"Game \\\"Edition\\\" \\uD83D\\uDE80\","
                + "\"LaunchExecutable\":\"bin\\/Entry.exe\"}";
            Eq(@"C:\Games\测试", GameScan.JsonStr(json, "InstallLocation"));
            Eq("Game \"Edition\" \uD83D\uDE80", GameScan.JsonStr(json, "DisplayName"));
            Eq("bin/Entry.exe", GameScan.JsonStr(json, "LaunchExecutable"));
            Eq<string>(null, GameScan.JsonStr(json, "Missing"));
            Eq<string>(null, GameScan.JsonStr("{\"InstallLocation\":\"C:\\q\"}", "InstallLocation"));
            Eq<string>(null, GameScan.JsonStr("{\"InstallLocation\":\"\\u12ZZ\"}", "InstallLocation"));
            Eq<string>(null, GameScan.JsonStr("{\"InstallLocation\":null}", "InstallLocation"));
        }

        private static void TestScanEpicEscapedPaths()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string executable = fixture.WriteExecutable(@"测试\bin\Entry.exe", "d3d11.dll");
                // With two graphics entries the platform-specified launch file must still match exactly
                fixture.WriteExecutable(@"测试\Other.exe", "d3d11.dll");
                string manifestDir = Path.Combine(fixture.Root, "Manifests");
                Directory.CreateDirectory(manifestDir);
                string location = Path.Combine(fixture.Root, "测试").Replace("\\", "\\\\")
                    .Replace("测试", "\\u6d4b\\u8bd5");
                File.WriteAllText(Path.Combine(manifestDir, "game.item"),
                    "{\"InstallLocation\":\"" + location + "\","
                    + "\"DisplayName\":\"Puzzle \\\"Edition\\\"\","
                    + "\"LaunchExecutable\":\"bin\\/Entry.exe\"}");
                var hits = new List<ScanHit>();
                GameScan.FromEpicManifests(manifestDir, null, hits,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                Eq(1, hits.Count);
                Eq(executable, hits[0].Exe);
                Eq("Puzzle \"Edition\"", hits[0].Name);
            }
        }
    }
}
#endif
