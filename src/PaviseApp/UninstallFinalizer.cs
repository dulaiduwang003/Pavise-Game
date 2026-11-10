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
            string workingDirectoryValue = null;
            try
            {
                string fileName = Process.GetCurrentProcess().MainModule.FileName;
                string fullPath = Path.GetFullPath(executable);
                if (!Same(fullPath, fileName) || !SafeFile(fullPath))
                {
                    throw new IOException("Executable changed");
                }
                Plan plan = new Plan();
                plan.Executable = fullPath;
                using (Process process = Process.GetCurrentProcess())
                {
                    plan.Pid = process.Id;
                    plan.Started = process.StartTime.ToUniversalTime().Ticks;
                }
                plan.ManagedRoot = Path.GetFullPath(StartupPayload.ManagedRoot);
                plan.Managed = SnapshotManaged(plan.ManagedRoot);
                workingDirectoryValue = Path.Combine(Path.GetTempPath(), "Pavise-Uninstall-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectoryValue);
                RejectRedirects(workingDirectoryValue);
                string paviseUninstallPath = Path.Combine(workingDirectoryValue, "Pavise.Uninstall.exe");
                using (FileStream fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    plan.Hash = Hash(fileStream);
                    fileStream.Position = 0L;
                    using (FileStream currentFileStream = new FileStream(paviseUninstallPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        fileStream.CopyTo(currentFileStream);
                        currentFileStream.Flush(true);
                    }
                }
                if (!string.Equals(HashFile(paviseUninstallPath), plan.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Helper copy failed verification");
                }
                string cleanupPath = Path.Combine(workingDirectoryValue, "cleanup.dat");
                WritePlan(cleanupPath, plan);
                ProcessStartInfo processStartInfo = new ProcessStartInfo(paviseUninstallPath, "--finish-uninstall " + Quote(cleanupPath));
                processStartInfo.UseShellExecute = false;
                processStartInfo.CreateNoWindow = true;
                processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                processStartInfo.WorkingDirectory = workingDirectoryValue;
                using (Process start = Process.Start(processStartInfo))
                {
                    if (start == null)
                    {
                        throw new IOException("Helper did not start");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
                if (workingDirectoryValue != null)
                {
                    RemoveLaunchFiles(workingDirectoryValue);
                }
                return false;
            }
        }

        internal static bool Run(string manifest)
        {
            string directoryName = null;
            bool runSucceeded = false;
            try
            {
                string fileName = Process.GetCurrentProcess().MainModule.FileName;
                directoryName = Path.GetDirectoryName(Path.GetFullPath(fileName));
                if (!IsHelperDirectory(directoryName) || !Same(fileName, Path.Combine(directoryName, "Pavise.Uninstall.exe")) || !Same(manifest, Path.Combine(directoryName, "cleanup.dat")))
                {
                    return false;
                }
                RejectRedirects(directoryName);
                RejectRedirects(manifest);
                RejectRedirects(fileName);
                Plan plan = ReadPlan(manifest);
                if (!ValidPlan(plan, fileName) || !WaitForParent(plan))
                {
                    return false;
                }
                runSucceeded = DeleteVerified(plan.Executable, plan.Hash, 40, 100);
                foreach (string managed in plan.Managed)
                {
                    string fileName2 = Path.GetFileName(Path.GetDirectoryName(managed));
                    if (!DeleteVerified(managed, fileName2, 2, 100))
                    {
                        runSucceeded = false;
                    }
                    TryRemoveEmpty(Path.GetDirectoryName(managed));
                }
                if (plan.Managed.Count != 0)
                {
                    TryRemoveEmpty(plan.ManagedRoot);
                }
                return runSucceeded;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (directoryName != null && IsHelperDirectory(directoryName))
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(directoryName, "result.txt"), runSucceeded ? "removed" : "skipped");
                    }
                    catch
                    {
                    }
                    QueueHelperCleanup(directoryName);
                }
            }
        }

        private static bool ValidPlan(Plan plan, string helper)
        {
            if (plan.Pid <= 0 || plan.Started <= 0 || !IsHash(plan.Hash) || Same(plan.Executable, helper) || !SafeFile(plan.Executable) || !string.Equals(plan.Hash, HashFile(helper), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string paviseAutoPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaviseAuto", "Autostart");
            if (!Same(paviseAutoPath, plan.ManagedRoot))
            {
                return false;
            }
            foreach (string managed in plan.Managed)
            {
                if (!StartupPayload.IsManagedExecutable(managed, paviseAutoPath))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool WaitForParent(Plan plan)
        {
            Process processById;
            try
            {
                processById = Process.GetProcessById(plan.Pid);
            }
            catch (ArgumentException)
            {
                return true;
            }
            using (processById)
            {
                try
                {
                    if (processById.StartTime.ToUniversalTime().Ticks != plan.Started || !Same(processById.MainModule.FileName, plan.Executable))
                    {
                        return false;
                    }
                    processById.WaitForExit();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static List<string> SnapshotManaged(string root)
        {
            List<string> paviseAutoPaths = new List<string>();
            try
            {
                RejectRedirects(root);
                if (!Directory.Exists(root))
                {
                    return paviseAutoPaths;
                }
                foreach (string path in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    string paviseAutoPath = Path.Combine(path, "PaviseAuto.exe");
                    if (StartupPayload.IsVerifiedExecutable(paviseAutoPath, root))
                    {
                        paviseAutoPaths.Add(paviseAutoPath);
                    }
                }
            }
            catch
            {
            }
            return paviseAutoPaths;
        }

        private static bool DeleteVerified(string target, string expectedHash, int attempts, int delay)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    RejectRedirects(target);
                    if (!File.Exists(target))
                    {
                        return true;
                    }
                    if (!SafeFile(target) || !string.Equals(HashFile(target), expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    FileAttributes attributes = File.GetAttributes(target);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
                    }
                    File.Delete(target);
                    if (!File.Exists(target))
                    {
                        return true;
                    }
                }
                catch
                {
                }
                if (i + 1 < attempts)
                {
                    Thread.Sleep(delay);
                }
            }
            return false;
        }

        private static bool SafeFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                RejectRedirects(path);
                return !Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static void RejectRedirects(string path)
        {
            string fullPath = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(fullPath))
            {
                if ((File.Exists(fullPath) || Directory.Exists(fullPath)) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Redirected cleanup path");
                }
                fullPath = Path.GetDirectoryName(fullPath);
            }
        }

        private static bool IsHelperDirectory(string directory)
        {
            try
            {
                string fileName = Path.GetFileName(directory);
                Guid result;
                return fileName.StartsWith("Pavise-Uninstall-", StringComparison.Ordinal) && Guid.TryParseExact(fileName.Substring("Pavise-Uninstall-".Length), "N", out result) && Same(Path.GetDirectoryName(directory), Path.GetTempPath());
            }
            catch
            {
                return false;
            }
        }

        private static void WritePlan(string file, Plan plan)
        {
            using (FileStream output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (BinaryWriter binaryWriter = new BinaryWriter(output, new UTF8Encoding(false)))
                {
                    binaryWriter.Write("Pavise uninstall 1");
                    binaryWriter.Write(plan.Pid);
                    binaryWriter.Write(plan.Started);
                    binaryWriter.Write(plan.Executable);
                    binaryWriter.Write(plan.Hash);
                    binaryWriter.Write(plan.ManagedRoot);
                    binaryWriter.Write(plan.Managed.Count);
                    foreach (string managed in plan.Managed)
                    {
                        binaryWriter.Write(managed);
                    }
                }
            }
        }

        private static Plan ReadPlan(string file)
        {
            using (FileStream fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (BinaryReader binaryReader = new BinaryReader(fileStream, new UTF8Encoding(false)))
                {
                    if (fileStream.Length > 65536 || binaryReader.ReadString() != "Pavise uninstall 1")
                    {
                        throw new IOException("Invalid cleanup plan");
                    }
                    Plan plan = new Plan();
                    plan.Pid = binaryReader.ReadInt32();
                    plan.Started = binaryReader.ReadInt64();
                    plan.Executable = binaryReader.ReadString();
                    plan.Hash = binaryReader.ReadString();
                    plan.ManagedRoot = binaryReader.ReadString();
                    int managedFileCount = binaryReader.ReadInt32();
                    if (managedFileCount < 0 || managedFileCount > 64)
                    {
                        throw new IOException("Invalid managed payload count");
                    }
                    for (int i = 0; i < managedFileCount; i++)
                    {
                        plan.Managed.Add(binaryReader.ReadString());
                    }
                    if (fileStream.Position != fileStream.Length)
                    {
                        throw new IOException("Unexpected cleanup plan data");
                    }
                    return plan;
                }
            }
        }

        private static string HashFile(string path)
        {
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Hash(input);
            }
        }

        private static string Hash(Stream input)
        {
            using (SHA256 sHA = SHA256.Create())
            {
                byte[] hash = sHA.ComputeHash(input);
                StringBuilder stringBuilder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    stringBuilder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return stringBuilder.ToString();
            }
        }

        private static bool IsHash(string text)
        {
            if (text == null || text.Length != 64)
            {
                return false;
            }
            foreach (char character in text)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool Same(string first, string second)
        {
            try
            {
                return string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void TryRemoveEmpty(string path)
        {
            try
            {
                RejectRedirects(path);
                if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
                {
                    Directory.Delete(path, false);
                }
            }
            catch
            {
            }
        }

        private static void RemoveLaunchFiles(string directory)
        {
            if (!IsHelperDirectory(directory))
            {
                return;
            }
            foreach (string path in new string[2] { "cleanup.dat", "Pavise.Uninstall.exe" })
            {
                try
                {
                    RejectRedirects(Path.Combine(directory, path));
                    File.Delete(Path.Combine(directory, path));
                }
                catch
                {
                }
            }
            TryRemoveEmpty(directory);
        }

        private static void QueueHelperCleanup(string directory)
        {
            try
            {
                RejectRedirects(directory);
                int id = Process.GetCurrentProcess().Id;
                string s = "$p=Get-Process -Id " + id.ToString(CultureInfo.InvariantCulture) + " -ErrorAction SilentlyContinue;if($p){$p.WaitForExit()};for($i=0;$i -lt 40;$i++){@('Pavise.Uninstall.exe','cleanup.dat','result.txt')|ForEach-Object{$f=Join-Path '" + directory.Replace("'", "''") + "' $_;if(Test-Path -LiteralPath $f -PathType Leaf){Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue}};Remove-Item -LiteralPath '" + directory.Replace("'", "''") + "' -ErrorAction SilentlyContinue;if(!(Test-Path -LiteralPath '" + directory.Replace("'", "''") + "')){break};Start-Sleep -Milliseconds 100}";
                string toBase64StringResult = Convert.ToBase64String(Encoding.Unicode.GetBytes(s));
                string fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                ProcessStartInfo processStartInfo = new ProcessStartInfo(fileName, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + toBase64StringResult);
                processStartInfo.UseShellExecute = false;
                processStartInfo.CreateNoWindow = true;
                processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                processStartInfo.WorkingDirectory = Path.GetTempPath();
                using (Process.Start(processStartInfo))
                {
                }
            }
            catch
            {
            }
        }
    }
}
