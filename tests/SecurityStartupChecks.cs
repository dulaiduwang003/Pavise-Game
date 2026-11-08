using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static class Logger { internal static void Warn(string text) { } internal static void Log(string text) { } }
    internal static class FamilyBoundary
    { internal static bool UnderRoot(string path, string root) { return path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); } }
    internal static class WhitelistRule
    { internal static bool PathEquals(string left, string right) { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); } }

    internal static class SecurityStartupChecks
    {
        private static int checks;
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        internal static SecurityProductSnapshot CollectFixture()
        {
            string mode = Environment.GetEnvironmentVariable("PAVISE_SECURITY_FIXTURE");
            string log = Environment.GetEnvironmentVariable("PAVISE_SECURITY_FIXTURE_LOG");
            File.AppendAllText(log, Process.GetCurrentProcess().Id + Environment.NewLine);
            if (mode == "timeout" || mode == "termination" || mode == "exitquery") Thread.Sleep(30000);
            if (mode == "failure") throw new IOException("Synthetic provider denial");
            return new SecurityProductSnapshot(true, false, new[] {
                new SecurityProductInfo("Microsoft Defender Antivirus", "defender", SecurityProductState.Active, false, true, true),
                new SecurityProductInfo("Fixture AV", "fixture", SecurityProductState.Inactive, true, true, false)
            }, new[] { @"C:\FixtureSecurity" }, "");
        }
        private static void PureChecks()
        {
            Check(SecurityStartupProbe.MapWscState(0) == SecurityProductState.Active, "WSC On mapping");
            for (int i = 1; i <= 3; i++) Check(SecurityStartupProbe.MapWscState(i) == SecurityProductState.Inactive, "WSC inactive mapping");
            Check(SecurityStartupProbe.MapWscState(999) == SecurityProductState.Unknown, "Invalid WSC state guessed");
            string[] names = { "360安全卫士", "火绒安全软件", "腾讯电脑管家", "McAfee Total Protection", "Kaspersky Premium", "Microsoft Defender Antivirus" };
            string[] vendors = { "360", "huorong", "tencent", "mcafee", "kaspersky", "defender" };
            for (int i = 0; i < names.Length; i++) Check(SecurityStartupProbe.VendorForName(names[i]) == vendors[i], "Expected vendor missing: " + names[i]);
            foreach (string name in new[] { "360安全浏览器", "360极速浏览器", "腾讯QQ", "火绒应用商店", "McAfee WebAdvisor", "Kaspersky Password Manager" })
                Check(SecurityStartupProbe.VendorForName(name) == "", "Non-AV product misclassified: " + name);
            Check(SecurityStartupProbe.CorroboratedMetadata(@"C:\test\HipsDaemon.exe", "", "", "", "") == "", "Process name alone accepted");
            Check(SecurityStartupProbe.CorroboratedMetadata(@"C:\test\HipsDaemon.exe", "火绒安全软件", "", "Unknown publisher", "") == "", "Uncorroborated publisher accepted");
            Check(SecurityStartupProbe.CorroboratedMetadata(@"C:\test\HipsDaemon.exe", "火绒安全软件", "", "Beijing Huorong Network Technology", "") == "huorong", "Product/company corroboration rejected");
            Check(SecurityStartupProbe.ExecutablePath("\"C:\\Program Files\\Vendor\\av.exe\" --service") == @"C:\Program Files\Vendor\av.exe", "Quoted executable parsing");
            Check(SecurityStartupProbe.ExecutablePath(@"C:\Program Files\Vendor\av.exe,0") == @"C:\Program Files\Vendor\av.exe", "Icon executable parsing");
            Check(SecurityStartupProbe.ExecutablePath(@"\\server\share\av.exe") == "", "Network path accepted");
            Check(SecurityStartupProbe.ExecutablePath("av.exe") == "", "Relative executable accepted");
            Check(SecurityProductCatalog.ProductRoot(@"C:\Windows\System32\security.exe", new string[0], @"C:\Windows") == null, "Windows root became broad exemption");
            var original = new SecurityProductSnapshot(true, false, new[] { new SecurityProductInfo("AV", "av", SecurityProductState.Active, true, true, true) }, new[] { @"C:\AV" }, "");
            SecurityProductInfo[] products = original.Products; products[0] = null;
            string[] roots = original.ProtectedRoots; roots[0] = @"C:\";
            Check(original.Products[0] != null && original.ProtectedRoots[0] == @"C:\AV", "Snapshot arrays mutable through getter");
            var decoded = SecurityStartupProbe.Decode(SecurityStartupProbe.Encode(original));
            Check(decoded.ScanCompleted && decoded.Products[0].State == SecurityProductState.Active && decoded.ProtectedRoots[0] == @"C:\AV", "Wire roundtrip lost facts");
            bool rejected = false; try { SecurityStartupProbe.Decode(new string('x', 262145)); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Oversized helper payload accepted");
            Type factsType = typeof(SecurityStartupProbe).GetNestedType("Facts", BindingFlags.NonPublic);
            object facts = Activator.CreateInstance(factsType, true);
            MethodInfo merge = factsType.GetMethod("Product", BindingFlags.Instance | BindingFlags.NonPublic);
            merge.Invoke(facts, new object[] { new SecurityProductInfo("McAfee", "mcafee", SecurityProductState.Inactive, true, true, false), true });
            merge.Invoke(facts, new object[] { new SecurityProductInfo("McAfee", "mcafee", SecurityProductState.Unknown, true, true, true), false });
            var found = (Dictionary<string, SecurityProductInfo>)factsType.GetField("Products", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(facts);
            Check(found["mcafee"].State == SecurityProductState.Inactive, "Running service overrode authoritative disabled state");
            merge.Invoke(facts, new object[] { new SecurityProductInfo("360杀毒", "360", SecurityProductState.Inactive, true, true, false), true });
            merge.Invoke(facts, new object[] { new SecurityProductInfo("360安全卫士极速版", "360", SecurityProductState.Unknown, true, true, true), false });
            Check(found["360:antivirus"].State == SecurityProductState.Inactive && found["360:guard"].State == SecurityProductState.Unknown,
                "Inactive 360 Antivirus concealed independent Safe Guard evidence");
            merge.Invoke(facts, new object[] { new SecurityProductInfo("360 Antivirus", "360", SecurityProductState.Unknown, true, true, true), false });
            Check(found["360:antivirus"].State == SecurityProductState.Inactive, "Fallback overrode WSC authority for same 360 family");
            merge.Invoke(facts, new object[] { new SecurityProductInfo("360安全卫士", "360", SecurityProductState.Active, true, true, true), true });
            merge.Invoke(facts, new object[] { new SecurityProductInfo("360安全卫士", "360", SecurityProductState.Inactive, true, true, false), true });
            Check(found["360:guard"].State == SecurityProductState.Active, "Known active 360 family lost its warning");
            merge.Invoke(facts, new object[] { new SecurityProductInfo("360 Total Security", "360", SecurityProductState.Unknown, true, false, false), false });
            Check(found.ContainsKey("360:total") && found.Count == 4, "Independent 360 Total Security merged into another product");
            Check(SecurityStartupProbe.DefenderRuntimeState(false, false, false, "Stopped", "Disabled") == SecurityProductState.Inactive, "Disabled Defender not resolved");
            Check(SecurityStartupProbe.DefenderRuntimeState(true, true, true, "Running", "Auto") == SecurityProductState.Active, "Active Defender hidden");
            Check(SecurityStartupProbe.DefenderRuntimeState(true, true, false, "Running", "Auto") == SecurityProductState.Active, "Realtime toggle alone hid running antivirus");
            Check(SecurityStartupProbe.DefenderRuntimeState(null, false, false, "Stopped", "Disabled") == SecurityProductState.Unknown, "Missing status guessed disabled");
            Check(SecurityStartupProbe.DefenderRuntimeState(false, false, false, "Running", "Disabled") == SecurityProductState.Unknown, "Conflicting service state ignored");
            Check(SecurityStartupProbe.DefenderRuntimeState(false, false, false, "Stopped", "Auto") == SecurityProductState.Unknown, "Transient stopped service assumed disabled");
            object disabled = Activator.CreateInstance(factsType, true);
            var disabledProducts = (Dictionary<string, SecurityProductInfo>)factsType.GetField("Products", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(disabled);
            disabledProducts["defender"] = new SecurityProductInfo("Windows Defender", "defender", SecurityProductState.Inactive, false, true, false);
            factsType.GetField("WscUnknown", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(disabled, true);
            var complete = typeof(SecurityStartupProbe).GetMethod("CompleteSnapshot", BindingFlags.Static | BindingFlags.NonPublic);
            Check(((SecurityProductSnapshot)complete.Invoke(null, new[] { disabled })).HasUnknownState, "Incomplete inventory suppressed warning");
            factsType.GetField("AntivirusInventoryComplete", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(disabled, true);
            Check(!((SecurityProductSnapshot)complete.Invoke(null, new[] { disabled })).HasUnknownState, "Resolved Defender retained WSC-only uncertainty");
            disabledProducts["huorong"] = new SecurityProductInfo("火绒", "huorong", SecurityProductState.Unknown, true, false, false);
            Check(((SecurityProductSnapshot)complete.Invoke(null, new[] { disabled })).HasUnknownState, "Defender fallback concealed unknown third-party antivirus");
            disabledProducts.Remove("huorong");
            factsType.GetField("Unknown", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(disabled, true);
            Check(((SecurityProductSnapshot)complete.Invoke(null, new[] { disabled })).HasUnknownState, "Defender fallback concealed unrelated provider failure");
        }
        public static int Main(string[] args)
        {
            int helperExit;
            if (SecurityStartupProbe.TryRunHelper(args, out helperExit)) return helperExit;
            try
            {
                PureChecks();
                string log = Environment.GetEnvironmentVariable("PAVISE_SECURITY_FIXTURE_LOG");
                string mode = Environment.GetEnvironmentVariable("PAVISE_SECURITY_FIXTURE");
                for (int i = 0; i < 100; i++) { SecurityProductCatalog.Roots(); SecurityProductCatalog.IsSecurityProduct(@"C:\FixtureSecurity\av.exe"); SecurityProductCatalog.CurrentGeneration(); GC.KeepAlive(SecurityProductCatalog.StartupSnapshot); }
                Check(!File.Exists(log), "Getter launched inspection before explicit startup");
                var watch = Stopwatch.StartNew();
                if (mode == "termination" || mode == "exitquery")
                {
                    bool blocked = false;
                    try { SecurityProductCatalog.StartupScanOnce(); } catch (SecurityProbeTerminationException) { blocked = true; }
                    Check(blocked && SecurityProductCatalog.StartupSnapshot.HasUnknownState, "Unconfirmed helper termination was swallowed");
                    var owner = (Process)typeof(SecurityStartupProbe).GetField("retainedHelper", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                    Check(owner != null && !owner.HasExited && owner.Handle != IntPtr.Zero, "Unconfirmed helper owner/handle discarded");
                    try
                    {
                        blocked = false; try { SecurityProductCatalog.StartupScanOnce(); } catch (SecurityProbeTerminationException) { blocked = true; }
                        Check(blocked && File.ReadAllLines(log).Length == 1, "Terminal startup failure reopened scan or allowed runtime");
                    }
                    finally { owner.Kill(); owner.WaitForExit(2000); owner.Dispose(); }
                    Console.WriteLine("PASS security startup mode=" + mode + " retained ownership, propagated terminal status, no retry; synthetic child cleaned by fixture");
                    return 0;
                }
                SecurityProductCatalog.StartupScanOnce(); watch.Stop();
                var snapshot = SecurityProductCatalog.StartupSnapshot;
                Check(snapshot.ScanCompleted, "Startup did not seal result");
                if (mode == "success") Check(snapshot.Products.Length == 2 && !snapshot.HasUnknownState && SecurityProductCatalog.IsSecurityProduct(@"C:\FixtureSecurity\av.exe"), "Successful startup facts lost");
                else Check(snapshot.HasUnknownState, "Failed provider became healthy absence");
                for (int i = 0; i < 1000; i++) { SecurityProductCatalog.StartupScanOnce(); SecurityProductCatalog.Roots(); SecurityProductCatalog.CurrentGeneration(); }
                string[] launches = File.ReadAllLines(log); Check(launches.Length == 1, "Repeated calls refilled startup scan budget");
                if (mode == "timeout")
                {
                    Check(watch.ElapsedMilliseconds >= 7500 && watch.ElapsedMilliseconds < 12000, "Helper timeout did not bound startup");
                    bool exited = false; try { using (Process child = Process.GetProcessById(int.Parse(launches[0]))) exited = child.HasExited; } catch (ArgumentException) { exited = true; }
                    Check(exited, "Timed out scanner remained alive after startup returned");
                }
                Console.WriteLine("PASS security startup mode=" + mode + " checks=" + checks + " scans=1; OS providers=fake; UI/runtime getters=cache only; product_started=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
