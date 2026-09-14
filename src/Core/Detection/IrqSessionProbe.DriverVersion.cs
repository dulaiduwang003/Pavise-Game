// @author bdth 2074055628@qq.com
// File purpose Driver version resolution and driver image path normalization
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal sealed partial class IrqSessionProbe : IDisposable
    {
        internal static string DriverVersionOf(string moduleName)
        {
            return CreateDriverVersionReader()(moduleName);
        }

        internal static Func<string, string> CreateDriverVersionReader()
        {
            return CreateDriverVersionReader(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                InterruptAttribution.LoadedModuleImagePaths,
                ServiceDriverImagePaths, ReadDriverFileVersion);
        }

        // Each page refresh or completed capture uses one immutable query scope
        // Snapshot is taken lazily, so the system is not queried when no driver needs verifying
        internal static Func<string, string> CreateDriverVersionReader(
            string windowsDirectory, Func<List<string>> loadedImages,
            Func<List<string>> serviceImages, Func<string, string> fileVersion)
        {
            var lookupGate = new object();
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> loaded = null, services = null;
            bool loadedRead = false, servicesRead = false;
            return delegate(string moduleName)
            {
                if (string.IsNullOrEmpty(moduleName)
                    || moduleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || moduleName == "." || moduleName == "..") return "";
                lock (lookupGate)
                {
                    string version;
                    if (versions.TryGetValue(moduleName, out version)) return version;
                    if (!loadedRead)
                    {
                        loadedRead = true;
                        try { if (loadedImages != null) loaded = loadedImages(); } catch { }
                    }
                    string path;
                    bool known = FindDriverImagePath(moduleName, windowsDirectory, loaded, out path);
                    if (!known)
                    {
                        if (!servicesRead)
                        {
                            servicesRead = true;
                            try { if (serviceImages != null) services = serviceImages(); } catch { }
                        }
                        known = FindDriverImagePath(moduleName, windowsDirectory, services, out path);
                    }
                    // An explicit loaded or registered path is the most authoritative; if it is gone,
                    // unreadable, or ambiguous, a same-named old copy under System32
                    // must not pass itself off as the current driver
                    version = known ? DriverFileVersion(path, fileVersion)
                        : ConventionalDriverVersion(moduleName, windowsDirectory, fileVersion);
                    versions[moduleName] = version;
                    return version;
                }
            };
        }

        private static string DriverFileVersion(string path, Func<string, string> fileVersion)
        {
            if (string.IsNullOrEmpty(path) || fileVersion == null) return "";
            try { return fileVersion(path) ?? ""; } catch { return ""; }
        }

        private static string ConventionalDriverVersion(string moduleName,
            string windowsDirectory, Func<string, string> fileVersion)
        {
            try
            {
                string driver = DriverFileVersion(NormalizeDriverImagePath(
                    @"System32\drivers\" + moduleName, windowsDirectory), fileVersion);
                string direct = DriverFileVersion(NormalizeDriverImagePath(
                    @"System32\" + moduleName, windowsDirectory), fileVersion);
                // Both same-named files are readable, which proves nothing about which one is loaded
                return driver.Length > 0 && direct.Length > 0 ? ""
                    : driver.Length > 0 ? driver : direct;
            }
            catch { return ""; }
        }

        private static bool FindDriverImagePath(string moduleName, string windowsDirectory,
            List<string> images, out string path)
        {
            path = null;
            if (images == null) return false;
            bool found = false, ambiguous = false;
            foreach (string image in images)
            {
                string normalized = NormalizeDriverImagePath(image, windowsDirectory);
                string name;
                try
                {
                    name = Path.GetFileName(normalized.Length > 0 ? normalized
                        : (image ?? "").Trim().Trim('"').Replace('/', '\\'));
                }
                catch { continue; }
                if (!string.Equals(name, moduleName, StringComparison.OrdinalIgnoreCase)) continue;
                found = true;
                if (normalized.Length == 0) ambiguous = true;
                else if (path == null) path = normalized;
                else if (!string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase))
                    ambiguous = true;
            }
            if (ambiguous) path = null;
            return found;
        }

        internal static string NormalizeDriverImagePath(string image, string windowsDirectory)
        {
            try
            {
                string path = (image ?? "").Trim().Replace('/', '\\');
                if (path.StartsWith("\"", StringComparison.Ordinal))
                {
                    if (path.Length < 2 || !path.EndsWith("\"", StringComparison.Ordinal)) return "";
                    path = path.Substring(1, path.Length - 2).Trim();
                }
                if (path.IndexOf('"') >= 0 || string.IsNullOrEmpty(windowsDirectory)) return "";
                if (path.StartsWith(@"\??\", StringComparison.Ordinal)
                    || path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
                string windows = Path.GetFullPath(windowsDirectory).TrimEnd('\\');
                foreach (string prefix in new[] { @"%SystemRoot%\", @"%windir%\", @"\SystemRoot\" })
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    { path = Path.Combine(windows, path.Substring(prefix.Length)); break; }
                path = Environment.ExpandEnvironmentVariables(path);
                if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
                    path = Path.Combine(windows, path);
                if (path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
                    path = DosDriverImagePath(path);
                // Drive-relative paths, UNC, wildcards, and device paths are never resolved by consulting the working directory
                // or by accessing a remote share
                if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\'
                    || path.IndexOf(':', 2) >= 0 || path.IndexOfAny(new[] { '*', '?', '\0', '%' }) >= 0)
                    return "";
                return Path.GetFullPath(path);
            }
            catch { return ""; }
        }

        private static string DosDriverImagePath(string path)
        {
            try
            {
                foreach (string drive in Environment.GetLogicalDrives())
                {
                    var target = new System.Text.StringBuilder(32768);
                    if (QueryDosDevice(drive.TrimEnd('\\'), target, target.Capacity) == 0) continue;
                    string prefix = target.ToString().TrimEnd('\\') + "\\";
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return drive + path.Substring(prefix.Length);
                }
            }
            catch { }
            return "";
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint QueryDosDevice(string deviceName,
            System.Text.StringBuilder targetPath, int maxChars);

        private static List<string> ServiceDriverImagePaths()
        {
            var paths = new List<string>();
            using (Microsoft.Win32.RegistryKey services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services"))
            {
                if (services == null) return paths;
                foreach (string serviceName in services.GetSubKeyNames())
                {
                    try
                    {
                        using (Microsoft.Win32.RegistryKey service = services.OpenSubKey(serviceName))
                        {
                            if (service == null) continue;
                            object kind = service.GetValue("Type");
                            if (kind == null || (Convert.ToInt32(kind) & 0x0B) == 0) continue;
                            // Service name is not necessarily the driver module name
                            string image = service.GetValue("ImagePath", null,
                                Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                            if (!string.IsNullOrWhiteSpace(image)) paths.Add(image);
                            else paths.Add(@"System32\drivers\" + serviceName + ".sys");
                        }
                    }
                    catch { }
                }
            }
            return paths;
        }

        private static string ReadDriverFileVersion(string path)
        {
            try
            {
                var before = new FileInfo(path);
                if (!before.Exists) return "";
                long length = before.Length, changed = before.LastWriteTimeUtc.Ticks;
                var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                var after = new FileInfo(path);
                if (!after.Exists || after.Length != length || after.LastWriteTimeUtc.Ticks != changed)
                    return "";
                // Identity format stays unchanged so existing valid history remains readable
                return (version.FileVersion ?? "").Trim() + "#"
                    + (changed / TimeSpan.TicksPerSecond).ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return ""; }
        }

        public void Dispose()
        {
            // Normal end is closed out by ReportFinish; process exit or exception Dispose only drops the half match
            // and never writes a fragment lacking the final placement re-check into history
            lock (takeGate)
            {
                IIrqSessionCapture discard = null;
                try
                {
                    lock (gate)
                    {
                        disposed = true;
                        discard = InvalidateLocked();
                    }
                }
                catch { }
                StopAndDiscard(discard);
            }
        }
    }
}
