using System;
using System.Collections.Generic;
using System.ComponentModel;
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

        private const string Prefix = "Pavise-Uninstall-";

        private const string HelperName = "Pavise.Uninstall.exe";

        private const string ManifestName = "cleanup.dat";

        private const string Signature = "Pavise uninstall 1";

        // 父进程身份读不到时最多重读 10 次 每次隔 100 毫秒
        private const int ParentIdentityAttempts = 10;

        private const int ParentIdentityRetryMilliseconds = 100;

        // 清理脚本最多等辅助进程 60 秒 辅助进程排好清理后马上退出 正常只要几毫秒
        private const int HelperCleanupWaitMilliseconds = 60000;

#if PAVISE_SELFTEST
        internal static string LastLaunchedDirectoryForTest;

        // 测试替换父进程身份的读取 稳定模拟父进程退出途中读不到主模块的情形
        internal interface IParentReaderForTest
        {
            string Module(Process parent);

            bool Exited(Process parent);
        }

        internal static IParentReaderForTest ParentReaderForTest = null;
#endif

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
                    using (FileStream fileStream2 = new FileStream(paviseUninstallPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        fileStream.CopyTo(fileStream2);
                        fileStream2.Flush(true);
                    }
                }
                if (!string.Equals(HashFile(paviseUninstallPath), plan.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Helper copy failed verification");
                }
                string cleanupPath = Path.Combine(workingDirectoryValue, ManifestName);
                WritePlan(cleanupPath, plan);
                ProcessStartInfo processStartInfo = new ProcessStartInfo(paviseUninstallPath, "--finish-uninstall " + Quote(cleanupPath));
                processStartInfo.UseShellExecute = false;
                processStartInfo.CreateNoWindow = true;
                processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                processStartInfo.WorkingDirectory = workingDirectoryValue;
                using (Process process2 = Process.Start(processStartInfo))
                {
                    if (process2 == null)
                    {
                        throw new IOException("Helper did not start");
                    }
                }
#if PAVISE_SELFTEST
                LastLaunchedDirectoryForTest = workingDirectoryValue;
#endif
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
                if (!IsHelperDirectory(directoryName) || !Same(fileName, Path.Combine(directoryName, "Pavise.Uninstall.exe")) || !Same(manifest, Path.Combine(directoryName, ManifestName)))
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
                foreach (string item in plan.Managed)
                {
                    string fileName2 = Path.GetFileName(Path.GetDirectoryName(item));
                    if (!DeleteVerified(item, fileName2, 2, 100))
                    {
                        runSucceeded = false;
                    }
                    TryRemoveEmpty(Path.GetDirectoryName(item));
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
#if PAVISE_SELFTEST
            string expectedRoot = plan.ManagedRoot;
            string testParent = Path.GetDirectoryName(expectedRoot);
            if (!IsUnder(testParent, Path.GetTempPath())
                || !Path.GetFileName(testParent).StartsWith("Pavise-UninstallFixture-", StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(expectedRoot), "Autostart", StringComparison.Ordinal)) return false;
            if (!Same(expectedRoot, plan.ManagedRoot)) return false;
            foreach (string target in plan.Managed)
                if (!StartupPayload.IsManagedExecutable(target, expectedRoot)) return false;
            return true;
#endif
            string paviseAutoPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaviseAuto", "Autostart");
            if (!Same(paviseAutoPath, plan.ManagedRoot))
            {
                return false;
            }
            foreach (string item in plan.Managed)
            {
                if (!StartupPayload.IsManagedExecutable(item, paviseAutoPath))
                {
                    return false;
                }
            }
            return true;
        }

        // 父进程退出途中读主模块会报 ERROR_PARTIAL_COPY 读不到身份时先看这个 PID 上的进程是否已经结束
        // 结束了说明原父进程也已退出 不用再等 删除前 DeleteVerified 照样核对哈希
        // 还活着就隔一会重读 次数用尽按身份不符处理 本体保留 结果写 skipped
        private static bool WaitForParent(Plan plan)
        {
            for (int attempt = 1; ; attempt++)
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
                        if (processById.StartTime.ToUniversalTime().Ticks != plan.Started || !Same(ParentModule(processById), plan.Executable))
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
                    catch (Win32Exception)
                    {
                        if (ParentExited(processById))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                }
                if (attempt >= ParentIdentityAttempts)
                {
                    return false;
                }
                Thread.Sleep(ParentIdentityRetryMilliseconds);
            }
        }

        private static string ParentModule(Process parent)
        {
#if PAVISE_SELFTEST
            if (ParentReaderForTest != null)
            {
                return ParentReaderForTest.Module(parent);
            }
#endif
            return parent.MainModule.FileName;
        }

        private static bool ParentExited(Process parent)
        {
#if PAVISE_SELFTEST
            if (ParentReaderForTest != null)
            {
                return ParentReaderForTest.Exited(parent);
            }
#endif
            try
            {
                return parent.WaitForExit(0);
            }
            catch
            {
                return false;
            }
        }

        private static List<string> SnapshotManaged(string root)
        {
            List<string> list = new List<string>();
            try
            {
                RejectRedirects(root);
                if (!Directory.Exists(root))
                {
                    return list;
                }
                string[] directories = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
                foreach (string path in directories)
                {
                    string paviseAutoPath = Path.Combine(path, "PaviseAuto.exe");
                    if (StartupPayload.IsVerifiedExecutable(paviseAutoPath, root))
                    {
                        list.Add(paviseAutoPath);
                    }
                }
            }
            catch
            {
            }
            return list;
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
                    foreach (string item in plan.Managed)
                    {
                        binaryWriter.Write(item);
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
                    int int32 = binaryReader.ReadInt32();
                    if (int32 < 0 || int32 > 64)
                    {
                        throw new IOException("Invalid managed payload count");
                    }
                    for (int i = 0; i < int32; i++)
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
                byte[] rawBytes = hash;
                foreach (byte b in rawBytes)
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

        private static bool IsUnder(string path, string root)
        {
            try
            {
                return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
            string[] textEntries = new string[2] { ManifestName, "Pavise.Uninstall.exe" };
            foreach (string path in textEntries)
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
#if PAVISE_SELFTEST
            return;
#endif
            try
            {
                RejectRedirects(directory);
                int id;
                long startedTicks;
                using (Process current = Process.GetCurrentProcess())
                {
                    id = current.Id;
                    startedTicks = current.StartTime.ToUniversalTime().Ticks;
                }
                using (Process.Start(HelperCleanupStart(HelperCleanupScript(directory, id, startedTicks, HelperCleanupWaitMilliseconds))))
                {
                }
            }
            catch
            {
            }
        }

        // 按 PID 加创建时间认辅助进程 PID 不在或已被别的进程复用 说明辅助进程已经退出 直接清理
        // 等待超时就以退出码 2 结束 不删任何文件 辅助目录留在 TEMP 里 Pavise 已经卸载 之后只由系统的临时文件清理或用户删除
        internal static string HelperCleanupScript(string directory, int helperId, long helperStartedTicks, int waitMilliseconds)
        {
            string quoted = "'" + directory.Replace("'", "''") + "'";
            return "$p=Get-Process -Id " + helperId.ToString(CultureInfo.InvariantCulture) + " -ErrorAction SilentlyContinue;"
                + "if($p){try{$s=$p.StartTime.ToUniversalTime().Ticks}catch{$s=0};"
                + "if($s -eq " + helperStartedTicks.ToString(CultureInfo.InvariantCulture) + "){"
                + "if(-not $p.WaitForExit(" + waitMilliseconds.ToString(CultureInfo.InvariantCulture) + ")){exit 2}}};"
                + "for($i=0;$i -lt 40;$i++){@('Pavise.Uninstall.exe','cleanup.dat','result.txt')|ForEach-Object{$f=Join-Path " + quoted + " $_;if(Test-Path -LiteralPath $f -PathType Leaf){Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue}};Remove-Item -LiteralPath " + quoted + " -ErrorAction SilentlyContinue;if(!(Test-Path -LiteralPath " + quoted + ")){break};Start-Sleep -Milliseconds 100}";
        }

        internal static ProcessStartInfo HelperCleanupStart(string script)
        {
            string fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            ProcessStartInfo processStartInfo = new ProcessStartInfo(fileName, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            processStartInfo.UseShellExecute = false;
            processStartInfo.CreateNoWindow = true;
            processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            processStartInfo.WorkingDirectory = Path.GetTempPath();
            return processStartInfo;
        }
    }
}
