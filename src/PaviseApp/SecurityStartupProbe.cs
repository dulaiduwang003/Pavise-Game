using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class SecurityProbeTerminationException : Exception
    {
        internal SecurityProbeTerminationException(string message, Exception cause) : base(message, cause) { }
    }
    // Run inspection in an owned startup helper so hung providers cannot outlive startup.
    internal static class SecurityStartupProbe
    {
        internal const string HelperArgument = "--security-startup-snapshot";
        private const string TokenVariable = "PAVISE_SECURITY_STARTUP_TOKEN";
        private const int TimeoutMs = 8000, MaxProducts = 32, MaxRoots = 64;
        private const int MaxWireBytes = 262144;
        private static readonly string[] Providers = { "AntiVirusProduct", "FirewallProduct", "AntiSpywareProduct" };
        private static Process retainedHelper;

        internal static SecurityProductSnapshot ReadOnce()
        {
            string token = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(Application.ExecutablePath, HelperArgument + " " + token) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.EnvironmentVariables[TokenVariable] = token;
            Process child = null;
            bool confirmedExit = false;
            try
            {
                child = Process.Start(start);
                if (child == null) return SecurityProductSnapshot.Unknown(true, "Helper did not start");
                Task<string> output = child.StandardOutput.ReadToEndAsync();
                Task<string> error = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(TimeoutMs))
                {
                    // This is our own inspection-only child, never a security product.
                    StopHelper(child);
                    confirmedExit = true;
                    return SecurityProductSnapshot.Unknown(true, "Helper deadline exhausted");
                }
                confirmedExit = true;
                if (child.ExitCode != 0 || !output.Wait(1000) || !error.Wait(1000))
                    return SecurityProductSnapshot.Unknown(true, "Helper failed");
                return Decode(output.Result);
            }
            catch (SecurityProbeTerminationException)
            { retainedHelper = child; child = null; throw; }
            catch (Exception)
            {
                if (child != null && !confirmedExit)
                    try { StopHelper(child); }
                    catch (SecurityProbeTerminationException) { retainedHelper = child; child = null; throw; }
                throw;
            }
            finally { if (child != null) child.Dispose(); }
        }

        private static void StopHelper(Process child)
        {
            try
            {
                if (!child.HasExited) child.Kill();
                if (!child.WaitForExit(2000)) throw new SecurityProbeTerminationException("Startup inspection termination deadline exhausted; runtime must not start", null);
            }
            catch (SecurityProbeTerminationException) { throw; }
            catch (Exception error)
            { throw new SecurityProbeTerminationException("Startup inspection termination unconfirmed; runtime must not start", error); }
        }

        internal static bool TryRunHelper(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || Array.IndexOf(args, HelperArgument) < 0) return false;
            string expected = Environment.GetEnvironmentVariable(TokenVariable);
            Environment.SetEnvironmentVariable(TokenVariable, null);
            Guid token;
            if (args.Length != 2 || args[0] != HelperArgument || !Guid.TryParseExact(args[1], "N", out token)
                || !string.Equals(args[1], expected, StringComparison.Ordinal)) { exitCode = 13; return true; }
            try { Console.Out.Write(Encode(Collect())); }
            catch (Exception) { exitCode = 14; }
            return true;
        }

        private sealed class Facts
        {
            internal readonly Dictionary<string, SecurityProductInfo> Products = new Dictionary<string, SecurityProductInfo>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Failures = new List<string>();
            internal bool Unknown;
            internal bool WscUnknown, AntivirusInventoryComplete;
            internal void Failed(string source, Exception error, bool affectsStatus)
            {
                if (affectsStatus)
                {
                    if (source.StartsWith("WSC", StringComparison.Ordinal)) WscUnknown = true;
                    else Unknown = true;
                }
                if (Failures.Count < 12) Failures.Add(source + ":" + error.GetType().Name);
            }
            internal void Root(string path)
            {
                string root = SecurityProductCatalog.ProtectedRoot(path);
                if (root != null && Roots.Count < MaxRoots) Roots.Add(root);
            }
            internal void Product(SecurityProductInfo product, bool authoritative)
            {
                string key = ProductIdentity(product);
                SecurityProductInfo previous;
                if (Products.TryGetValue(key, out previous))
                {
                    // WSC is authoritative for protection. A running service only
                    // corroborates installation; it must never turn WSC Off into On.
                    if (!authoritative && previous.State != SecurityProductState.Unknown) return;
                    if (authoritative && previous.State == SecurityProductState.Active) return;
                    if (!authoritative && previous.IsRunning && !product.IsRunning) return;
                }
                if (Products.ContainsKey(key) || Products.Count < MaxProducts) Products[key] = product;
                else Unknown = true;
            }
        }

        private static string ProductIdentity(SecurityProductInfo product)
        {
            if (product.VendorKey != "360") return product.VendorKey.Length == 0 ? product.Name : product.VendorKey;
            string name = product.Name.ToLowerInvariant();
            // These can be installed independently. An inactive antivirus must
            // not suppress the separate Safe Guard service's unknown status.
            if (Has(name, "total security")) return "360:total";
            if (Has(name, "杀毒", "antivirus", "internet security")) return "360:antivirus";
            if (Has(name, "卫士", "360safe")) return "360:guard";
            return "360:" + name;
        }

        private static SecurityProductSnapshot Collect()
        {
            var facts = new Facts();
            try { ReadWsc(facts); } catch (Exception error) { facts.Failed("WSC", error, true); }
            foreach (string provider in Providers)
                try { ReadProvider(provider, facts); } catch (Exception error) { facts.Failed(provider, error, provider == "AntiVirusProduct"); }
            try { ReadServices(facts); } catch (Exception error) { facts.Failed("services", error, true); }
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    try { ReadInstalled(hive, view, facts); } catch (Exception error) { facts.Failed("installed-" + hive + "-" + view, error, true); }
            if (facts.Products.ContainsKey("defender"))
                try { ReadDefender(facts); } catch (Exception error) { facts.Failed("Defender-status", error, false); }
            return CompleteSnapshot(facts);
        }

        // A complete inventory plus resolved per-product status can replace a failed WSC status query.
        private static SecurityProductSnapshot CompleteSnapshot(Facts facts)
        {
            var products = new List<SecurityProductInfo>(facts.Products.Values);
            products.Sort(delegate(SecurityProductInfo a, SecurityProductInfo b) { return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); });
            bool unknown = facts.Unknown || (facts.WscUnknown && (!facts.AntivirusInventoryComplete || products.Count == 0));
            foreach (SecurityProductInfo item in products) if (item.State == SecurityProductState.Unknown) unknown = true;
            var roots = new List<string>(facts.Roots); roots.Sort(StringComparer.OrdinalIgnoreCase);
            return new SecurityProductSnapshot(true, unknown, products.ToArray(), roots.ToArray(), string.Join("; ", facts.Failures.ToArray()));
        }

        internal static SecurityProductState DefenderRuntimeState(bool? serviceEnabled, bool? antivirusEnabled,
            bool? realTimeEnabled, string serviceState, string startMode)
        {
            if (serviceEnabled == true && (antivirusEnabled == true || realTimeEnabled == true))
                return SecurityProductState.Active;
            if (serviceEnabled == false && antivirusEnabled == false && realTimeEnabled == false
                && string.Equals(serviceState, "Stopped", StringComparison.OrdinalIgnoreCase)
                && string.Equals(startMode, "Disabled", StringComparison.OrdinalIgnoreCase))
                return SecurityProductState.Inactive;
            return SecurityProductState.Unknown;
        }

        private static void ReadDefender(Facts facts)
        {
            bool? service = null, antivirus = null, realtime = null;
            using (var searcher = Query("root\\Microsoft\\Windows\\Defender",
                "SELECT AMServiceEnabled,AntivirusEnabled,RealTimeProtectionEnabled FROM MSFT_MpComputerStatus"))
            using (ManagementObjectCollection rows = searcher.Get())
            {
                int count = 0;
                foreach (ManagementObject row in rows) using (row)
                {
                    if (++count > 1) return;
                    service = row["AMServiceEnabled"] as bool?;
                    antivirus = row["AntivirusEnabled"] as bool?;
                    realtime = row["RealTimeProtectionEnabled"] as bool?;
                }
            }
            string state = null, start = null;
            using (var searcher = Query("root\\CIMV2", "SELECT State,StartMode FROM Win32_Service WHERE Name='WinDefend'"))
            using (ManagementObjectCollection rows = searcher.Get())
            {
                int count = 0;
                foreach (ManagementObject row in rows) using (row)
                {
                    if (++count > 1) return;
                    state = row["State"] as string; start = row["StartMode"] as string;
                }
            }
            SecurityProductState resolved = DefenderRuntimeState(service, antivirus, realtime, state, start);
            if (resolved == SecurityProductState.Unknown) return;
            SecurityProductInfo previous = facts.Products["defender"];
            facts.Products["defender"] = new SecurityProductInfo(previous.Name, "defender", resolved, false, true,
                resolved == SecurityProductState.Active);
        }

        // The Windows SDK declares both interfaces as IDispatch/dual. Their
        // method order below follows iwscapi.h, including inherited dispatch slots.
        [ComImport, Guid("722A338C-6E8E-4E72-AC27-1417FB0C81C2"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IWscProductList
        {
            void Initialize(uint provider);
            int Count { get; }
            IWscProduct GetItem(uint index);
        }
        [ComImport, Guid("8C38232E-3A45-4A27-92B0-1A16A975F669"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IWscProduct
        {
            string ProductName { [return: MarshalAs(UnmanagedType.BStr)] get; }
            int ProductState { get; }
            int SignatureStatus { get; }
            string RemediationPath { [return: MarshalAs(UnmanagedType.BStr)] get; }
            string ProductStateTimestamp { [return: MarshalAs(UnmanagedType.BStr)] get; }
        }

        private static void ReadWsc(Facts facts)
        {
            object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("17072F7B-9ABE-4A74-A261-1EB76B55107A"), true));
            try
            {
                var list = (IWscProductList)instance;
                list.Initialize(4);
                int count = list.Count;
                if (count < 0 || count > MaxProducts) facts.Unknown = true;
                for (uint index = 0; index < Math.Min(Math.Max(count, 0), MaxProducts); index++)
                {
                    IWscProduct product = null;
                    try
                    {
                        product = list.GetItem(index);
                        string name = CleanName(product.ProductName);
                        if (name.Length == 0) throw new InvalidDataException("Empty security product name");
                        SecurityProductState state = SecurityProductState.Unknown;
                        try { state = MapWscState(product.ProductState); }
                        catch (Exception error) { facts.Failed("WSC-state", error, true); }
                        string vendor = VendorForName(name);
                        facts.Product(new SecurityProductInfo(name, vendor, state, vendor != "defender", state != SecurityProductState.Unknown,
                            state == SecurityProductState.Active), true);
                    }
                    catch (Exception error) { facts.Failed("WSC-product", error, true); }
                    finally { if (product != null && Marshal.IsComObject(product)) Marshal.FinalReleaseComObject(product); }
                }
            }
            finally { if (Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
        }

        internal static SecurityProductState MapWscState(int state)
        { return state == 0 ? SecurityProductState.Active : (state >= 1 && state <= 3 ? SecurityProductState.Inactive : SecurityProductState.Unknown); }

        private static ManagementObjectSearcher Query(string scope, string query)
        {
            var searcher = new ManagementObjectSearcher(scope, query);
            searcher.Options.Timeout = TimeSpan.FromSeconds(2);
            searcher.Options.ReturnImmediately = true; searcher.Options.Rewindable = false;
            return searcher;
        }

        private static void ReadProvider(string provider, Facts facts)
        {
            using (var searcher = Query("root\\SecurityCenter2", "SELECT displayName, pathToSignedProductExe, pathToSignedReportingExe FROM " + provider))
            using (ManagementObjectCollection rows = searcher.Get())
            {
                int count = 0;
                foreach (ManagementObject row in rows)
                    using (row)
                    {
                        if (++count > MaxRoots) { facts.Unknown = true; return; }
                        facts.Root(ExecutablePath(row["pathToSignedProductExe"] as string));
                        facts.Root(ExecutablePath(row["pathToSignedReportingExe"] as string));
                        if (provider != "AntiVirusProduct") continue;
                        string name = CleanName(row["displayName"] as string);
                        if (name.Length == 0) continue;
                        string vendor = VendorForName(name);
                        facts.Product(new SecurityProductInfo(name, vendor, SecurityProductState.Unknown, vendor != "defender", false, false), false);
                    }
                if (provider == "AntiVirusProduct") facts.AntivirusInventoryComplete = true;
            }
        }

        private static void ReadServices(Facts facts)
        {
            const string filter = "Name LIKE 'AVP%' OR Name LIKE 'kav%' OR Name LIKE 'McAfee%' OR Name LIKE 'mfe%' OR Name='McAPExe' OR Name='HipsDaemon' OR Name='QQPCRTP' OR Name='ZhuDongFangYu' OR Name='360rp' OR Name='QHActiveDefense'";
            using (var searcher = Query("root\\CIMV2", "SELECT Name,DisplayName,PathName,State FROM Win32_Service WHERE " + filter))
            using (ManagementObjectCollection rows = searcher.Get())
            {
                int count = 0;
                foreach (ManagementObject row in rows)
                    using (row)
                    {
                        if (++count > 64) { facts.Unknown = true; break; }
                        try
                        {
                            string path = ExecutablePath(row["PathName"] as string);
                            string productName;
                            string vendor = CorroboratedExecutable(path, "", out productName);
                            if (vendor.Length == 0) continue;
                            string state = row["State"] as string;
                            bool known = !string.IsNullOrEmpty(state), running = string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase);
                            facts.Root(path);
                            facts.Product(new SecurityProductInfo(productName, vendor, SecurityProductState.Unknown, true, known, running), false);
                        }
                        catch (Exception error) { facts.Failed("service-identity", error, true); }
                    }
            }
        }

        private static void ReadInstalled(RegistryHive hive, RegistryView view, Facts facts)
        {
            using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
            using (RegistryKey uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false))
            {
                if (uninstall == null) return;
                string[] names = uninstall.GetSubKeyNames();
                if (names.Length > 2048) facts.Unknown = true;
                for (int i = 0; i < Math.Min(names.Length, 2048); i++)
                    try
                    {
                        using (RegistryKey key = uninstall.OpenSubKey(names[i], false))
                        {
                            if (key == null) continue;
                            string display = key.GetValue("DisplayName") as string;
                            string vendor = VendorForName(display);
                            if (vendor.Length == 0 || vendor == "defender" || !PublisherMatches(vendor, key.GetValue("Publisher") as string)) continue;
                            string path = ExecutablePath(key.GetValue("DisplayIcon") as string);
                            if (path.Length == 0) path = ExecutablePath(key.GetValue("UninstallString") as string);
                            string productName;
                            if (CorroboratedExecutable(path, vendor, out productName) != vendor) continue;
                            facts.Root(path);
                            facts.Product(new SecurityProductInfo(productName, vendor, SecurityProductState.Unknown, true, false, false), false);
                        }
                    }
                    catch (Exception error) { facts.Failed("installed-identity", error, true); }
            }
        }

        internal static string CorroboratedMetadata(string path, string product, string description, string company, string expected)
        {
            string vendor = VendorForName(product);
            if (vendor.Length == 0) vendor = VendorForName(description);
            if (vendor.Length == 0 || vendor == "defender" || (expected.Length != 0 && vendor != expected) || !PublisherMatches(vendor, company)) return "";
            string file = Path.GetFileName(path).ToLowerInvariant();
            // Reject browser, storefront and updater-only products from the same publisher.
            if (file.IndexOf("browser", StringComparison.Ordinal) >= 0 || file == "360se.exe" || file == "360chrome.exe" || file == "qq.exe") return "";
            return vendor;
        }

        private static string CorroboratedExecutable(string path, string expected, out string productName)
        {
            productName = "";
            if (path.Length == 0 || !File.Exists(path)) return "";
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            string vendor = CorroboratedMetadata(path, version.ProductName, version.FileDescription, version.CompanyName, expected);
            if (vendor.Length == 0) return "";
            productName = CleanName(VendorForName(version.ProductName) == vendor ? version.ProductName : version.FileDescription);
            if (productName.Length == 0) productName = DisplayName(vendor);
            return vendor;
        }

        internal static string VendorForName(string text)
        {
            string value = (text ?? "").ToLowerInvariant();
            if (Has(value, "windows defender", "microsoft defender", "windows 防御", "microsoft 防御")) return "defender";
            if (Has(value, "火绒", "huorong")) return Has(value, "应用商店", "app store") ? "" : "huorong";
            if (Has(value, "腾讯电脑管家", "qq电脑管家", "qqpcmgr", "tencent pc manager", "tencent computer manager")) return "tencent";
            if (Has(value, "360安全卫士", "360 安全卫士", "360杀毒", "360 杀毒", "360 total security", "360 antivirus", "360 internet security", "360safe")) return "360";
            if (Has(value, "kaspersky", "卡巴斯基")) return Has(value, "password", "vpn") ? "" : "kaspersky";
            if (Has(value, "mcafee", "迈克菲", "麦咖啡")) return Has(value, "webadvisor", "web advisor", "safe connect", "true key", "secure vpn") ? "" : "mcafee";
            return "";
        }

        internal static bool PublisherMatches(string vendor, string text)
        {
            string value = (text ?? "").ToLowerInvariant();
            switch (vendor)
            {
                case "huorong": return Has(value, "huorong", "火绒");
                case "tencent": return Has(value, "tencent", "腾讯");
                case "360": return Has(value, "qihoo", "qihu", "奇虎", "360", "奇安信");
                case "kaspersky": return Has(value, "kaspersky", "卡巴斯基");
                case "mcafee": return Has(value, "mcafee", "迈克菲", "麦咖啡");
                default: return false;
            }
        }
        private static bool Has(string value, params string[] tokens)
        { foreach (string token in tokens) if (value.IndexOf(token, StringComparison.Ordinal) >= 0) return true; return false; }
        private static string DisplayName(string vendor)
        {
            switch (vendor) { case "360": return "360 安全软件"; case "huorong": return "火绒安全"; case "tencent": return "腾讯电脑管家"; case "mcafee": return "McAfee"; case "kaspersky": return "Kaspersky"; default: return vendor; }
        }
        private static string CleanName(string name)
        {
            var value = new StringBuilder();
            foreach (char character in name ?? "") if (!char.IsControl(character) && value.Length < 160) value.Append(character);
            return value.ToString().Trim();
        }
        internal static string ExecutablePath(string command)
        {
            string value = Environment.ExpandEnvironmentVariables((command ?? "").Trim());
            if (value.StartsWith("\"", StringComparison.Ordinal))
            { int end = value.IndexOf('"', 1); if (end < 0) return ""; value = value.Substring(1, end - 1); }
            else
            { int end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (end < 0) return ""; value = value.Substring(0, end + 4); }
            // Only local absolute executables; do not touch UNC shares or device paths.
            if (value.Length < 4 || !char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\' || !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "";
            try { return Path.GetFullPath(value); } catch (Exception) { return ""; }
        }

        internal static string Encode(SecurityProductSnapshot snapshot)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(1); writer.Write(snapshot.HasUnknownState); writer.Write(snapshot.FailureSummary);
                SecurityProductInfo[] products = snapshot.Products; writer.Write(products.Length);
                foreach (SecurityProductInfo item in products)
                { writer.Write(item.Name); writer.Write(item.VendorKey); writer.Write((int)item.State); writer.Write(item.IsThirdParty); writer.Write(item.RuntimeKnown); writer.Write(item.IsRunning); }
                string[] roots = snapshot.ProtectedRoots; writer.Write(roots.Length); foreach (string root in roots) writer.Write(root);
                writer.Flush(); return Convert.ToBase64String(stream.ToArray());
            }
        }
        internal static SecurityProductSnapshot Decode(string payload)
        {
            if (payload == null || payload.Length > MaxWireBytes) throw new InvalidDataException("Security snapshot too large");
            using (var stream = new MemoryStream(Convert.FromBase64String(payload)))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Security snapshot version");
                bool unknown = reader.ReadBoolean(); string failure = reader.ReadString();
                int count = reader.ReadInt32(); if (count < 0 || count > MaxProducts) throw new InvalidDataException("Security product count");
                var products = new SecurityProductInfo[count];
                for (int i = 0; i < count; i++)
                {
                    string name = reader.ReadString(), vendor = reader.ReadString(); int state = reader.ReadInt32();
                    if (name.Length > 160 || vendor.Length > 160 || state < 0 || state > 2) throw new InvalidDataException("Security product data");
                    products[i] = new SecurityProductInfo(name, vendor, (SecurityProductState)state, reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean());
                }
                count = reader.ReadInt32(); if (count < 0 || count > MaxRoots) throw new InvalidDataException("Security root count");
                var roots = new string[count];
                for (int i = 0; i < count; i++) { roots[i] = reader.ReadString(); if (roots[i].Length > 32767) throw new InvalidDataException("Security root length"); }
                if (stream.Position != stream.Length) throw new InvalidDataException("Security trailing data");
                return new SecurityProductSnapshot(true, unknown, products, roots, failure);
            }
        }
    }
}
