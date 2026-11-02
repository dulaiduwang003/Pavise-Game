#if PAVISE_SELFTEST && PAVISE_PLATFORM_RULES
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    // Synthetic snapshots only. The launcher freezes sources and guards native
    // process discovery, policy work, and restoration before building this entry.
    internal static class PlatformRuntimeRulesRunner
    {
        private static int assertions, settingsCases, runtimeCases, session;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly string[] WeGameNames = { "wegame", "wegame_env", "wegameclient", "pallas", "rail", "tcls_core" };
        private static void Check(bool value, string message)
        { assertions++; if (!value) throw new InvalidOperationException(message); }
        private static object Call(object target, string method, params object[] args)
        { return target.GetType().GetMethod(method, Hidden).Invoke(target, args); }
        private static T Result<T>(object target, string field)
        { return (T)target.GetType().GetField(field).GetValue(target); }
        private static ProcEntry Entry(int id, int parent, long born, string path)
        { return new ProcEntry { Pid = id, ParentPid = parent, Creation = born, Name = Path.GetFileNameWithoutExtension(path), Path = path, Session = session }; }
        private static GameMode Mode(string directory)
        { Directory.CreateDirectory(directory); return new GameMode(directory, new SuppressionCore()); }
        private static object Evaluate(GameMode mode, ProcessSnapshot snapshot)
        { return Call(mode, "EvaluateWhitelist", snapshot); }
        private static HashSet<int> Protected(GameMode mode, ProcessSnapshot snapshot)
        { return Result<HashSet<int>>(Evaluate(mode, snapshot), "Protected"); }
        private static List<WhitelistRuleView> PlatformRows(GameMode mode)
        { return mode.GetWhitelistRulesFast().FindAll(delegate(WhitelistRuleView view) { return view.PlatformId != null; }); }
        private static void OnlySteam(GameMode mode, bool present)
        {
            var rows = PlatformRows(mode);
            Check(rows.Count == (present ? 1 : 0), "Unexpected built-in platform row count");
            foreach (var row in rows) Check(row.PlatformId == "steam" && row.Key == "platform:steam" && !row.Required,
                "Only Steam may have a removable built-in platform row");
            Check(!PlatformRuntimePolicy.Enabled("wegame"), "WeGame built-in protection must remain disabled");
        }
        private static void SeedReadOnlyCatalogs()
        {
            Action<Type, string, object> set = delegate(Type type, string name, object value) {
                type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);
            };
            set(typeof(PeripheralVendorProbe), "tokens", new string[0]);
            set(typeof(PeripheralVendorProbe), "stamp", Environment.TickCount);
            set(typeof(PeripheralVendorProbe), "scanned", true);
            set(typeof(SecurityProductCatalog), "roots", new[] { @"C:\FixtureSecurity" });
            set(typeof(SecurityProductCatalog), "stamp", Environment.TickCount);
            set(typeof(SecurityProductCatalog), "scanned", true);
            set(typeof(SecurityProductCatalog), "refreshing", false);
        }
        private static void OldSettings(string output)
        {
            string[] values = { "", "wegame", "WeGaMe", "unknown", "wegame;unknown", "steam", "steam;wegame", "WeGaMe;StEaM", "unknown;steam;wegame;steam" };
            foreach (string value in values)
            {
                settingsCases++; Settings.UseTransientStoreForCurrentProcess();
                Check(Settings.SaveStr(PlatformRuntimePolicy.RemovedKey, value), "Could not seed legacy platform preference");
                bool steamRemoved = value.IndexOf("steam", StringComparison.OrdinalIgnoreCase) >= 0;
                Check(PlatformRuntimePolicy.Mask == (steamRemoved ? 2 : 0), "Legacy WeGame/unknown token changed the Steam bit: " + value);
                Check(PlatformRuntimePolicy.Enabled("steam") == !steamRemoved, "Legacy Steam choice changed: " + value);
                Check((PlatformRuntimePolicy.Mask != 0) == steamRemoved, "Restore action state must depend only on an actual Steam removal");
                int before = Settings.MutationGeneration;
                Check(!PlatformRuntimePolicy.TryRemove("wegame") && !PlatformRuntimePolicy.TryRemove("unknown")
                    && !PlatformRuntimePolicy.TryRemove("steam;wegame") && before == Settings.MutationGeneration,
                    "Unsupported removals must be rejected without saving");
                string directory = Path.Combine(output, "legacy-" + settingsCases); var mode = Mode(directory); OnlySteam(mode, !steamRemoved);
                Check(!mode.RemoveWhitelistRule("platform:wegame"), "An obsolete WeGame row key must not become a rule");
                Check(mode.ResetWhitelist(), "ResetWhitelist failed for a legacy preference");
                Check(PlatformRuntimePolicy.Mask == (steamRemoved ? 2 : 0), "ResetWhitelist unexpectedly changed platform choices");
                OnlySteam(Mode(directory), !steamRemoved);
                Check(mode.RestorePlatformProtection() && PlatformRuntimePolicy.Mask == 0, "Restoring supported defaults failed");
                OnlySteam(mode, true); OnlySteam(Mode(directory), true);
            }
            Settings.UseTransientStoreForCurrentProcess();
            Check(AppDataReset.IsUserPreference(PlatformRuntimePolicy.RemovedKey), "Application reset no longer recognizes the legacy preference key");
            Check(Settings.SaveStr(PlatformRuntimePolicy.RemovedKey, "steam;wegame"), "Could not seed reset preference");
            Check(PlatformRuntimePolicy.Mask == 2, "Reset fixture must start with Steam removed");
            int removed; string failure;
            Check(Settings.ClearUserPreferencesForReset(AppDataReset.IsUserPreference, out removed, out failure)
                && removed == 1 && string.IsNullOrEmpty(failure), "Reset must clear the platform preference in the transient store");
            Check(PlatformRuntimePolicy.Mask == 0 && PlatformRuntimePolicy.Enabled("steam")
                && !PlatformRuntimePolicy.Enabled("wegame"), "Application reset revived obsolete WeGame protection");
        }
        private static ProcessSnapshot RuntimeSnapshot()
        {
            var entries = new List<ProcEntry>();
            for (int i = 0; i < WeGameNames.Length; i++)
            {
                int id = 100 + i * 10;
                entries.Add(Entry(id, 0, 10, @"C:\WeGame\" + WeGameNames[i] + ".exe"));
                entries.Add(Entry(id + 1, id, 11, @"C:\WeGame\web" + i + @"\browser.exe"));
                entries.Add(Entry(id + 2, id + 1, 12, @"C:\Other\session-worker" + i + ".exe"));
            }
            entries.Add(Entry(300, 0, 10, @"C:\Listary\ListaryHookHost64.exe"));
            entries.Add(Entry(301, 300, 11, @"C:\Input\input-worker.exe"));
            entries.Add(Entry(310, 0, 10, @"C:\AntiCheatExpert\SGuard64.exe"));
            entries.Add(Entry(311, 310, 11, @"C:\Safety\ac-worker.exe"));
            entries.Add(Entry(320, 0, 10, @"C:\FixtureSecurity\security-agent.exe"));
            entries.Add(Entry(330, 0, 10, @"C:\Other\browser.exe"));
            return new ProcessSnapshot(entries.ToArray());
        }
        private static void AssertWeGameOrdinary(GameMode mode, ProcessSnapshot snapshot)
        {
            object evaluation = Evaluate(mode, snapshot);
            var protectedPids = Result<HashSet<int>>(evaluation, "Protected");
            var runtime = Result<HashSet<int>>(evaluation, "RuntimeProtected");
            var owners = Result<Dictionary<int, PlatformProcessOwner>>(evaluation, "PlatformProcesses");
            for (int i = 0; i < WeGameNames.Length; i++)
            {
                runtimeCases++; int id = 100 + i * 10;
                string name = WeGameNames[i], path = @"C:\WeGame\" + name + ".exe";
                Check(WeGameRuntimeProtection.IsRuntimeName(name.ToUpperInvariant() + ".exe")
                    && PlatformRuntimePolicy.RuntimePlatform(name) == "wegame", "WeGame runtime classification was removed: " + name);
                Check(!WeGameRuntimeProtection.IsProtectedRuntimeName(name) && !WeGameRuntimeProtection.IsCriticalRuntime(name, path),
                    "WeGame runtime still has a low-level automatic exemption: " + name);
                Check(!FamilyBoundary.CatalogProtected(id, 10, name, path), "WeGame catalog cache retained protection: " + name);
                Check(GamePerformanceTargetPolicy.KnownNonGame(name, path)
                    && !GameSessionDetector.IsLibraryCandidate(name, path, @"C:\Windows"), "WeGame runtime became a renderer candidate: " + name);
                for (int child = id; child <= id + 2; child++)
                {
                    Check(owners.ContainsKey(child) && owners[child].Platform == "wegame", "Platform ownership metadata missing: " + child);
                    Check(!protectedPids.Contains(child) && !runtime.Contains(child), "WeGame runtime/descendant kept automatic protection: " + child);
                    ProcEntry process = Array.Find(snapshot.Entries, delegate(ProcEntry p) { return p.Pid == child; });
                    bool platformRemoved = owners.ContainsKey(child) && !PlatformRuntimePolicy.Enabled(owners[child].Platform);
                    Check(platformRemoved && FamilyBoundary.BasicBackgroundEligible(child, 99999, process.Name, process.Path,
                        session, session, 99998, true, @"C:\Windows", true, @"C:\WeGame", false, true, process.Creation, platformRemoved),
                        "Game ancestry/directory/visible family incorrectly exempted ordinary WeGame background: " + child);
                }
            }
            foreach (int pid in new[] { 300, 301, 310, 311, 320 })
                Check(protectedPids.Contains(pid), "Independent input/anti-cheat/security protection was disturbed: " + pid);
            Check(!protectedPids.Contains(330), "An unrelated browser acquired a platform exemption");
        }
        private static void RuntimeAndManual(string output)
        {
            Settings.UseTransientStoreForCurrentProcess();
            var directory = Path.Combine(output, "runtime-model"); var mode = Mode(directory); var snapshot = RuntimeSnapshot();
            Check(PlatformRuntimePolicy.Ids.Length == 1 && PlatformRuntimePolicy.Ids[0] == "steam", "Built-in platform policy must contain only Steam");
            OnlySteam(mode, true); AssertWeGameOrdinary(mode, snapshot);
            Check(mode.ResetWhitelist(), "Default whitelist reset failed"); AssertWeGameOrdinary(mode, snapshot);
            Check(mode.RestorePlatformProtection(), "Platform defaults restore failed"); AssertWeGameOrdinary(mode, snapshot);
            AssertWeGameOrdinary(Mode(directory), snapshot);
            Check(mode.AddWhitelistAuto(@"C:\WeGame\wegame.exe"), "Explicit WeGame application-family rule could not be added");
            foreach (int pid in new[] { 100, 101, 102 }) Check(Protected(mode, snapshot).Contains(pid), "Manual family rule did not protect its verified descendants");
            var reload = Mode(directory); Check(Protected(reload, snapshot).Contains(102), "Manual WeGame rule did not survive file reload");
            WhitelistRule manual; Check(WhitelistRule.TryCreate(WhitelistRuleKind.ApplicationFamily, @"C:\WeGame\wegame.exe", out manual), "Invalid manual family fixture");
            Check(reload.RemoveWhitelistRule(manual.Key), "Explicit WeGame family rule could not be removed");
            AssertWeGameOrdinary(reload, snapshot); AssertWeGameOrdinary(Mode(directory), snapshot);
            Check(FamilyBoundary.CatalogProtected(310, 10, "SGuard64", @"C:\AntiCheatExpert\SGuard64.exe")
                && !FamilyBoundary.BasicBackgroundEligible(310, 99999, "SGuard64", @"C:\AntiCheatExpert\SGuard64.exe",
                    session, session, 99998, true, @"C:\Windows", true, @"C:\AntiCheatExpert", false, true, 10, true),
                "Platform removal bypassed the independent anti-cheat safety boundary");
            Check(!FamilyBoundary.BasicBackgroundEligible(100, 99999, "wegame", @"C:\WeGame\wegame.exe", session, session, 100,
                true, @"C:\Windows", true, @"C:\WeGame", false, true, 10, true), "Foreground WeGame must keep ordinary foreground protection");
            var family = new HashSet<int> { 100 };
            Check(!FamilyBoundary.IsGameOrWhitelistProtected(100, 500, false, true, true, family, family, true), "Game-family inheritance bypassed removed platform policy");
            Check(FamilyBoundary.IsGameOrWhitelistProtected(100, 100, false, true, true, family, family, true)
                && FamilyBoundary.IsGameOrWhitelistProtected(100, 500, true, true, true, family, family, true), "Renderer/manual whitelist boundary was lost");
        }
        private static void PersistedManualFormats(string output)
        {
            foreach (WhitelistRuleKind kind in new[] { WhitelistRuleKind.LegacyName, WhitelistRuleKind.ExactPath, WhitelistRuleKind.ApplicationFamily })
            {
                Settings.UseTransientStoreForCurrentProcess();
                string directory = Path.Combine(output, "manual-format-" + kind); Directory.CreateDirectory(directory);
                WhitelistRule rule; Check(WhitelistRule.TryCreate(kind, kind == WhitelistRuleKind.LegacyName ? "wegame" : @"C:\WeGame\wegame.exe", out rule), "Invalid persisted manual rule");
                string line = rule.Serialize(); WhitelistRule parsed;
                Check(WhitelistRule.TryParseVersioned(line, out parsed) && parsed.Key == rule.Key, "N/P/F manual serialization failed");
                File.WriteAllLines(Path.Combine(directory, "Pavise.whitelist.txt"), new[] { WhitelistRule.Header, line,
                    GameMode.BuildWhitelistFooter(new[] { rule }) });
                var mode = Mode(directory); OnlySteam(mode, true);
                var snapshot = new ProcessSnapshot(new[] { Entry(100, 0, 10, @"C:\WeGame\wegame.exe"), Entry(101, 100, 11, @"C:\Elsewhere\child.exe") });
                Check(Protected(mode, snapshot).Contains(100), "Explicit N/P/F WeGame rule was lost during loading");
                Check(Protected(mode, snapshot).Contains(101), "Existing explicit N/P/F family scope changed");
                Check(mode.RemoveWhitelistRule(rule.Key) && !Protected(mode, snapshot).Contains(100) && !Protected(mode, snapshot).Contains(101), "Manual N/P/F deletion retained protection");
                Check(!Protected(Mode(directory), snapshot).Contains(100), "Deleted manual rule returned after loading");
            }
        }
        private static void SteamAndFailedSaves(string output)
        {
            Settings.UseTransientStoreForCurrentProcess(); var mode = Mode(Path.Combine(output, "steam-model"));
            Check(OverlayHostCatalog.ShouldProtectProcess("steamwebhelper", @"C:\Steam\steamwebhelper.exe", true), "Default Steam overlay protection changed");
            Check(mode.RemoveWhitelistRule("platform:steam") && PlatformRuntimePolicy.Mask == 2, "Steam removal must retain its historical bit 2");
            OnlySteam(mode, false); OnlySteam(Mode(Path.Combine(output, "steam-model")), false);
            Check(!OverlayHostCatalog.ShouldProtectProcess("steamwebhelper", @"C:\Steam\steamwebhelper.exe", true), "Steam overlay removal was ignored");
            Check(mode.ResetWhitelist() && PlatformRuntimePolicy.Mask == 2, "Whitelist reset must preserve Steam removal choice");
            Check(mode.RestorePlatformProtection() && PlatformRuntimePolicy.Mask == 0, "Steam restoration failed"); OnlySteam(mode, true);
            Check(OverlayHostCatalog.ShouldProtectProcess("steamwebhelper", @"C:\Steam\steamwebhelper.exe", true), "Steam overlay was not restored");
            Settings.SuspendWritesForReset();
            Check(!mode.RemoveWhitelistRule("platform:steam") && PlatformRuntimePolicy.Mask == 0 && PlatformRuntimePolicy.Enabled("steam"), "Failed Steam removal changed effective policy");
            Check(mode.WhitelistLastError.Length > 0, "Failed removal must expose an error"); OnlySteam(mode, true);
            Settings.UseTransientStoreForCurrentProcess(); Check(mode.RemoveWhitelistRule("platform:steam"), "Failed-restore fixture could not remove Steam");
            Settings.SuspendWritesForReset();
            Check(!mode.RestorePlatformProtection() && PlatformRuntimePolicy.Mask == 2, "Failed restore changed the saved removal");
            Check(mode.WhitelistLastError.Length > 0, "Failed restore must expose an error"); OnlySteam(mode, false);
            Settings.UseTransientStoreForCurrentProcess();
            Check(OverlayHostCatalog.ShouldProtectProcess("RTSS", @"C:\Tools\RTSS.exe", true)
                && GamePerformanceTargetPolicy.KnownNonGame("steam", @"C:\Steam\steam.exe"), "Independent overlay/non-game role recognition changed");
        }
        private static void RendererHandoff(string output)
        {
            Settings.UseTransientStoreForCurrentProcess(); var mode = Mode(Path.Combine(output, "renderer-model"));
            var snapshot = new ProcessSnapshot(new[] {
                Entry(400, 0, 10, @"C:\WeGame\wegame.exe"), Entry(500, 400, 20, @"C:\Games\game.exe"),
                Entry(501, 500, 21, @"C:\Games\render-worker.exe"), Entry(100, 500, 22, @"C:\Games\wegame_env.exe"),
                Entry(101, 100, 23, @"C:\Games\web\browser.exe") });
            object first = Evaluate(mode, snapshot);
            Check(Result<Dictionary<int, PlatformProcessOwner>>(first, "PlatformProcesses").ContainsKey(501), "Handoff fixture must begin with platform ancestry");
            typeof(GameMode).GetField("activeDetection", Hidden).SetValue(mode, new GameDetection { RendererPid = 500, RendererCreation = 20 });
            object second = Evaluate(mode, snapshot); var owners = Result<Dictionary<int, PlatformProcessOwner>>(second, "PlatformProcesses");
            Check(!Object.ReferenceEquals(first, second) && !owners.ContainsKey(500) && !owners.ContainsKey(501), "Renderer handoff failed to invalidate cached platform ancestry");
            var gameFamily = new HashSet<int> { 500, 501, 100, 101 };
            foreach (int pid in new[] { 100, 101 })
            {
                Check(owners.ContainsKey(pid) && owners[pid].Platform == "wegame" && !Result<HashSet<int>>(second, "RuntimeProtected").Contains(pid)
                    && !Result<HashSet<int>>(second, "Protected").Contains(pid), "WeGame descendant inherited the actual renderer's protection");
                ProcEntry process = Array.Find(snapshot.Entries, delegate(ProcEntry p) { return p.Pid == pid; });
                bool removed = owners.ContainsKey(pid) && !PlatformRuntimePolicy.Enabled(owners[pid].Platform);
                Check(!FamilyBoundary.IsGameOrWhitelistProtected(pid, 500, false, true, true, gameFamily, gameFamily, removed)
                    && FamilyBoundary.BasicBackgroundEligible(pid, 99999, process.Name, process.Path, session, session, 99998,
                        true, @"C:\Windows", true, @"C:\Games", false, true, process.Creation, removed),
                    "A WeGame process below the renderer must remain eligible for ordinary background handling");
            }
            Check(mode.AddWhitelistAuto(@"C:\Games\wegame_env.exe") && Protected(mode, snapshot).Contains(100) && Protected(mode, snapshot).Contains(101),
                "Explicit family rule must still protect WeGame processes below a renderer");
            WhitelistRule rule; Check(WhitelistRule.TryCreate(WhitelistRuleKind.ApplicationFamily, @"C:\Games\wegame_env.exe", out rule)
                && mode.RemoveWhitelistRule(rule.Key), "Renderer-child manual rule removal failed");
            Check(!Protected(mode, snapshot).Contains(100) && !Protected(mode, snapshot).Contains(101), "Renderer-child manual removal left an automatic exemption");
        }
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) throw new ArgumentException("A temporary output directory is required");
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init(); SeedReadOnlyCatalogs();
                using (var self = Process.GetCurrentProcess()) session = self.SessionId;
                OldSettings(args[0]); RuntimeAndManual(args[0]); PersistedManualFormats(args[0]); SteamAndFailedSaves(args[0]); RendererHandoff(args[0]);
                Console.WriteLine("PASS platform runtime rules assertions=" + assertions + " legacy_settings_cases=" + settingsCases + " runtime_cases=" + runtimeCases
                    + " ui_windows=0 product_workers=0 native_process_mutations=0"); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Platform rule tests cannot launch the application runtime"); } }
}
#endif
