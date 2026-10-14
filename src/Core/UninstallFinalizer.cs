using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static class UninstallFinalizer
    {
        private const string Prefix = "Pavise-Uninstall-";
        private const string HelperName = "Pavise.Uninstall.exe";
        private const string ManifestName = "cleanup.dat";
        private const string Signature = "Pavise uninstall 1";
#if PAVISE_SELFTEST
        internal static string LastLaunchedDirectoryForTest;
#endif

        private sealed class Plan
        {
            internal int Pid;
            internal long Started;
            internal string Executable;
            internal string Hash;
            internal string ManagedRoot;
            internal List<string> Managed = new List<string>();
        }

        internal static bool Launch(string executable, out string failure)
        {
            failure = null;
            string directory = null;
            try
            {
                string current = Process.GetCurrentProcess().MainModule.FileName;
                string source = Path.GetFullPath(executable);
                if (!Same(source, current) || !SafeFile(source)) throw new IOException("Executable changed");
                var plan = new Plan();
                plan.Executable = source;
                using (var process = Process.GetCurrentProcess())
                { plan.Pid = process.Id; plan.Started = process.StartTime.ToUniversalTime().Ticks; }
                plan.ManagedRoot = Path.GetFullPath(StartupPayload.ManagedRoot);
                plan.Managed = SnapshotManaged(plan.ManagedRoot);
                directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                RejectRedirects(directory);
                string helper = Path.Combine(directory, HelperName);
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    plan.Hash = Hash(input); input.Position = 0;
                    using (var output = new FileStream(helper, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { input.CopyTo(output); output.Flush(true); }
                }
                if (!string.Equals(HashFile(helper), plan.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Helper copy failed verification");
                string manifest = Path.Combine(directory, ManifestName);
                WritePlan(manifest, plan);
                using (Process child = Process.Start(new ProcessStartInfo(helper,
                    "--finish-uninstall " + Quote(manifest))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = directory
                }))
                { if (child == null) throw new IOException("Helper did not start"); }
#if PAVISE_SELFTEST
                LastLaunchedDirectoryForTest = directory;
#endif
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
                if (directory != null) RemoveLaunchFiles(directory);
                return false;
            }
        }

        internal static bool Run(string manifest)
        {
            string directory = null;
            bool ok = false;
            try
            {
                string helper = Process.GetCurrentProcess().MainModule.FileName;
                directory = Path.GetDirectoryName(Path.GetFullPath(helper));
                if (!IsHelperDirectory(directory) || !Same(helper, Path.Combine(directory, HelperName))
                    || !Same(manifest, Path.Combine(directory, ManifestName))) return false;
                RejectRedirects(directory); RejectRedirects(manifest); RejectRedirects(helper);
                Plan plan = ReadPlan(manifest);
                if (!ValidPlan(plan, helper) || !WaitForParent(plan)) return false;
                ok = DeleteVerified(plan.Executable, plan.Hash, 40, 100);
                foreach (string managed in plan.Managed)
                {
                    string hash = Path.GetFileName(Path.GetDirectoryName(managed));
                    if (!DeleteVerified(managed, hash, 2, 100)) ok = false;
                    TryRemoveEmpty(Path.GetDirectoryName(managed));
                }
                if (plan.Managed.Count != 0) TryRemoveEmpty(plan.ManagedRoot);
                return ok;
            }
            catch { return false; }
            finally
            {
                if (directory != null && IsHelperDirectory(directory))
                {
                    try { File.WriteAllText(Path.Combine(directory, "result.txt"), ok ? "removed" : "skipped"); } catch { }
                    QueueHelperCleanup(directory);
                }
            }
        }

        private static bool ValidPlan(Plan plan, string helper)
        {
            if (plan.Pid <= 0 || plan.Started <= 0 || !IsHash(plan.Hash)
                || Same(plan.Executable, helper) || !SafeFile(plan.Executable)
                || !string.Equals(plan.Hash, HashFile(helper), StringComparison.OrdinalIgnoreCase)) return false;
            string expectedRoot;
#if PAVISE_SELFTEST
            expectedRoot = plan.ManagedRoot;
            string testParent = Path.GetDirectoryName(expectedRoot);
            if (!IsUnder(testParent, Path.GetTempPath())
                || !Path.GetFileName(testParent).StartsWith("Pavise-UninstallFixture-", StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(expectedRoot), "Autostart", StringComparison.Ordinal)) return false;
#else
            expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PaviseAuto", "Autostart");
#endif
            if (!Same(expectedRoot, plan.ManagedRoot)) return false;
            foreach (string target in plan.Managed)
                if (!StartupPayload.IsManagedExecutable(target, expectedRoot)) return false;
            return true;
        }

        private static bool WaitForParent(Plan plan)
        {
            Process parent;
            try { parent = Process.GetProcessById(plan.Pid); }
            catch (ArgumentException) { return true; }
            using (parent)
            {
                try
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != plan.Started
                        || !Same(parent.MainModule.FileName, plan.Executable)) return false;
                    // 父进程仍存活时保留其可执行文件。
                    parent.WaitForExit();
                    return true;
                }
                catch (InvalidOperationException) { return true; }
                catch { return false; }
            }
        }

        private static List<string> SnapshotManaged(string root)
        {
            var result = new List<string>();
            try
            {
                RejectRedirects(root);
                if (!Directory.Exists(root)) return result;
                foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    string candidate = Path.Combine(directory, StartupPayload.ExecutableName);
                    if (StartupPayload.IsVerifiedExecutable(candidate, root)) result.Add(candidate);
                }
            }
            catch { }
            return result;
        }

        private static bool DeleteVerified(string target, string expectedHash, int attempts, int delay)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    RejectRedirects(target);
                    if (!File.Exists(target)) return true;
                    if (!SafeFile(target) || !string.Equals(HashFile(target), expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
                    FileAttributes attributes = File.GetAttributes(target);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(target);
                    if (!File.Exists(target)) return true;
                }
                catch { }
                if (i + 1 < attempts) Thread.Sleep(delay);
            }
            return false;
        }

        private static bool SafeFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)
                    || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
                RejectRedirects(path);
                return !Directory.Exists(path);
            }
            catch { return false; }
        }

        private static void RejectRedirects(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Redirected cleanup path");
        }

        private static bool IsHelperDirectory(string directory)
        {
            try
            {
                string name = Path.GetFileName(directory);
                Guid token;
                return name.StartsWith(Prefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(name.Substring(Prefix.Length), "N", out token)
                    && Same(Path.GetDirectoryName(directory), Path.GetTempPath());
            }
            catch { return false; }
        }

        private static void WritePlan(string file, Plan plan)
        {
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(Signature); writer.Write(plan.Pid); writer.Write(plan.Started);
                writer.Write(plan.Executable); writer.Write(plan.Hash); writer.Write(plan.ManagedRoot);
                writer.Write(plan.Managed.Count);
                foreach (string managed in plan.Managed) writer.Write(managed);
            }
        }

        private static Plan ReadPlan(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false)))
            {
                if (stream.Length > 65536 || reader.ReadString() != Signature) throw new IOException("Invalid cleanup plan");
                var plan = new Plan();
                plan.Pid = reader.ReadInt32(); plan.Started = reader.ReadInt64();
                plan.Executable = reader.ReadString(); plan.Hash = reader.ReadString(); plan.ManagedRoot = reader.ReadString();
                int count = reader.ReadInt32();
                if (count < 0 || count > 64) throw new IOException("Invalid managed payload count");
                for (int i = 0; i < count; i++) plan.Managed.Add(reader.ReadString());
                if (stream.Position != stream.Length) throw new IOException("Unexpected cleanup plan data");
                return plan;
            }
        }

        private static string HashFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return Hash(stream);
        }

        private static string Hash(Stream input)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(input);
                var text = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static bool IsHash(string text)
        {
            if (text == null || text.Length != 64) return false;
            foreach (char c in text) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        private static bool Same(string first, string second)
        {
            try { return string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static bool IsUnder(string path, string root)
        {
            try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static string Quote(string value)
        { return "\"" + value.Replace("\"", "\\\"") + "\""; }

        private static void TryRemoveEmpty(string path)
        {
            try { RejectRedirects(path); if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0) Directory.Delete(path, false); }
            catch { }
        }

        private static void RemoveLaunchFiles(string directory)
        {
            if (!IsHelperDirectory(directory)) return;
            foreach (string name in new[] { ManifestName, HelperName })
            { try { RejectRedirects(Path.Combine(directory, name)); File.Delete(Path.Combine(directory, name)); } catch { } }
            TryRemoveEmpty(directory);
        }

        private static void QueueHelperCleanup(string directory)
        {
#if PAVISE_SELFTEST
            return;
#else
            try
            {
                RejectRedirects(directory);
                int pid = Process.GetCurrentProcess().Id;
                string script = "$p=Get-Process -Id " + pid.ToString(CultureInfo.InvariantCulture)
                    + " -ErrorAction SilentlyContinue;if($p){$p.WaitForExit()};"
                    + "for($i=0;$i -lt 40;$i++){"
                    + "@('" + HelperName + "','" + ManifestName + "','result.txt')|ForEach-Object{"
                    + "$f=Join-Path '" + directory.Replace("'", "''") + "' $_;"
                    + "if(Test-Path -LiteralPath $f -PathType Leaf){Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue}};"
                    + "Remove-Item -LiteralPath '" + directory.Replace("'", "''") + "' -ErrorAction SilentlyContinue;"
                    + "if(!(Test-Path -LiteralPath '" + directory.Replace("'", "''") + "')){break};Start-Sleep -Milliseconds 100}";
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                using (Process process = Process.Start(new ProcessStartInfo(powershell,
                    "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    // 清理进程从目标目录之外启动，避免工作目录无法删除。
                    WorkingDirectory = Path.GetTempPath()
                })) { }
            }
            catch { }
#endif
        }
    }
}
