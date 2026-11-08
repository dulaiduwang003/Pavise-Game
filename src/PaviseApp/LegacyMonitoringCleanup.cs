using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace PaviseApp
{
    internal static class LegacyMonitoringCleanup
    {
        internal const string OwnershipKey = "RtssInstallationOwnerV1";
        internal const string ResourceName = "Pavise.LegacyCleanup.RtssUninstall.ps1";
        private static int queued;
        private static readonly string[] RetiredPreferences = {
            "PerfMonitorEnabledV1", "PerfMonitorEnabledV2", "GameRunReportsEnabledV1",
            "PerfMonitorDisplayIntervalMsV2", "PerfMonitorTextStyleV2", "PerfMonitorColorsV1",
            "PerfMonitorLayoutV1", "PerfMonitorMetricsV1", "PerfMonitorIntervalMsV1",
            "PerfMonitorAppearanceV1", "PerfMonitorHotkeyV1"
        };

        internal static void Start()
        {
            if (Interlocked.Exchange(ref queued, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate {
                try
                {
                    foreach (string key in RetiredPreferences) Settings.TryRemove(key, false);
                    // Absence of ownership means zero process inspection and zero changes to RTSS.
                    if (String.IsNullOrEmpty(Settings.LoadStr(OwnershipKey, ""))) return;
                    RunOwnedCleanup();
                }
                catch (Exception error) { Logger.Warn("Legacy component cleanup retained its receipt: " + error.GetType().Name); }
            });
        }

        private static void RunOwnedCleanup()
        {
            byte[] script;
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            using (var output = new MemoryStream())
            {
                if (resource == null) throw new InvalidOperationException("Missing legacy cleanup resource");
                resource.CopyTo(output); script = output.ToArray();
            }
            // The hash names an immutable copy and is rechecked before launching it.
            string digest;
            using (var hash = SHA256.Create()) digest = BitConverter.ToString(hash.ComputeHash(script)).Replace("-", "");
            string directory = Path.Combine(Paths.Data, "Maintenance");
            string path = Path.GetFullPath(Path.Combine(directory, "OwnedRtssCleanup-" + digest + ".ps1"));
            if (path.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) throw new InvalidOperationException("Invalid cleanup path");
            Directory.CreateDirectory(directory);
            for (string current = directory; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Redirected cleanup directory");
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(script, 0, script.Length);
            }
            catch (IOException) { if (!File.Exists(path)) throw; }
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create())
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                    || BitConverter.ToString(hash.ComputeHash(locked)).Replace("-", "") != digest)
                    throw new InvalidOperationException("Legacy cleanup verification failed");
                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                var start = new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + path + "\" -Uninstall") {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true, ErrorDialog = false
                };
                using (Process process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    process.WaitForExit();
                    if (process.ExitCode != 0) Logger.Warn("Legacy RTSS cleanup did not complete; ownership was retained for retry.");
                    else Logger.Log("Legacy component cleanup: " + output.Result.Trim());
                    GC.KeepAlive(error);
                }
            }
            // User data and installation directories are never deleted by this migration.
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
