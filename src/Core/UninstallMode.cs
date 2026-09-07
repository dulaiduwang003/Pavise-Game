// @author bdth 2074055628@qq.com
// 文件用途 卸载 Pavise 的程序内实现 按收据还原系统改动 再删开机任务 托管电源方案 设置 数据与旧版本残留
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class UninstallReport
    {
        public bool Cleared;
        public int Files;
        public string Failure;
        public int Leftovers;
    }

    internal static class UninstallMode
    {
        internal static readonly string[] LegacyRegistryKeys =
            { @"Software\Aegis", @"Software\PaviseSelfTest", @"Software\PaviseTest" };
        internal static readonly string[] TempDirectoryPatterns =
            { "PaviseShot_*", "PaviseSelftestData-*", "PaviseSelftest-*" };
        private static readonly string[] LegacyTaskNames = { "Aegis" };

        public static UninstallReport Execute(string dataDir, string exeDir, Func<bool> stopRuntime)
        {
            var report = new UninstallReport();
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                report.Failure = Lang.T("wipe.pathfail");
                return report;
            }
            try
            {
                if (stopRuntime == null || !stopRuntime())
                {
                    report.Failure = Lang.T("wipe.stopfail");
                    return report;
                }
            }
            catch (Exception ex)
            {
                report.Failure = Lang.T("wipe.stopfail") + " (" + ex.GetType().Name + ")";
                return report;
            }
            int files;
            string unrestored;
            try
            {
                if (!LegacyPurge.WipeAll(dataDir, true, Lang.T("btn.uninstall"), out files, out unrestored))
                {
                    report.Failure = unrestored;
                    return report;
                }
            }
            catch (Exception ex)
            {
                report.Failure = Lang.T("wipe.cleanupfail") + " (" + ex.GetType().Name + ")";
                return report;
            }
            report.Files = files;
            report.Cleared = true;
            report.Leftovers = CleanLeftovers(dataDir, exeDir);
            return report;
        }

        // 收据还原与本机数据已经清完 这里只清历史版本留下的东西 单项失败不影响结果 只是少清一项
        private static int CleanLeftovers(string dataDir, string exeDir)
        {
            int cleaned = 0;
            try { if (TaskHelper.DeleteStartupTask() == 0) cleaned++; } catch { }
            foreach (string task in LegacyTaskNames)
                try { if (TaskHelper.Run("/Delete /F /TN " + task) == 0) cleaned++; } catch { }
            try { cleaned += PowerPlan.DeleteManagedPlansByName(); } catch { }
            foreach (string key in LegacyRegistryKeys)
            {
                try
                {
                    using (RegistryKey probe = Registry.CurrentUser.OpenSubKey(key))
                        if (probe == null) continue;
                    Registry.CurrentUser.DeleteSubKeyTree(key, false);
                    cleaned++;
                }
                catch { }
            }
            foreach (string dir in LegacyDataDirs(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), exeDir))
            {
                int n;
                string error;
                try { if (Directory.Exists(dir) && LegacyPurge.TryDeleteDataTree(dir, out n, out error)) cleaned++; }
                catch { }
            }
            if (!string.IsNullOrEmpty(exeDir) && !SamePath(exeDir, dataDir))
            {
                int n;
                string error;
                try { if (LegacyPurge.DeleteOwnedFiles(exeDir, out n, out error)) cleaned += n; }
                catch { }
            }
            cleaned += CleanTemp();
            return cleaned;
        }

        // 旧版本用过的数据目录 程序自己所在的目录及其上级一概不碰 便携版装在这些位置时不能连程序一起删
        internal static List<string> LegacyDataDirs(string roaming, string local, string common, string exeDir)
        {
            var result = new List<string>();
            foreach (string candidate in new[]
            {
                Join(roaming, "Aegis"), Join(local, "Pavise"), Join(common, "Pavise")
            })
            {
                if (candidate == null) continue;
                if (!string.IsNullOrEmpty(exeDir) && IsSameOrParent(candidate, exeDir)) continue;
                result.Add(candidate);
            }
            return result;
        }

        private static string Join(string root, string name)
        {
            if (string.IsNullOrEmpty(root)) return null;
            try { return Path.GetFullPath(Path.Combine(root, name)); }
            catch { return null; }
        }

        private static bool IsSameOrParent(string parent, string child)
        {
            try
            {
                string p = Path.GetFullPath(parent).TrimEnd('\\', '/');
                string c = Path.GetFullPath(child).TrimEnd('\\', '/');
                if (string.Equals(p, c, StringComparison.OrdinalIgnoreCase)) return true;
                return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || c.StartsWith(p + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
                    Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static int CleanTemp()
        {
            int cleaned = 0;
            string temp;
            try { temp = Path.GetTempPath(); }
            catch { return 0; }
            foreach (string pattern in TempDirectoryPatterns)
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(temp, pattern))
                    {
                        try { Directory.Delete(dir, true); cleaned++; }
                        catch { }
                    }
                }
                catch { }
            }
            try
            {
                foreach (string file in Directory.GetFiles(temp, "Pavise*"))
                {
                    try { File.Delete(file); cleaned++; }
                    catch { }
                }
            }
            catch { }
            return cleaned;
        }

        // 命令行 --uninstall 结果文件 的无界面入口 给自动化用 不需要界面
        public static bool Run(string resultPath)
        {
            var report = new StringBuilder();
            bool ok = false;
            try
            {
                Paths.Init();
                Lang.Init();
                string exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                UninstallReport result = Execute(Paths.Data, exeDir, delegate { StopOtherInstances(); return true; });
                ok = result.Cleared;
                report.Append("cleared=").Append(ok ? "1" : "0").Append("\r\n");
                report.Append("files=").Append(result.Files).Append("\r\n");
                report.Append("leftovers=").Append(result.Leftovers).Append("\r\n");
                if (!string.IsNullOrEmpty(result.Failure)) report.Append("unrestored=").Append(result.Failure).Append("\r\n");
            }
            catch (Exception ex)
            {
                report.Append("cleared=0\r\nerror=").Append(ex.GetType().Name).Append(' ').Append(ex.Message).Append("\r\n");
                ok = false;
            }
            try
            {
                if (!string.IsNullOrEmpty(resultPath))
                    File.WriteAllText(resultPath, report.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return ok;
        }

        // 还原持久项时不能有别的实例在写收据 先发退出信号 等不到就强杀
        private static void StopOtherInstances()
        {
            try { using (var exit = EventWaitHandle.OpenExisting("Global\\Pavise_Exit")) exit.Set(); }
            catch { }
            int self = Process.GetCurrentProcess().Id;
            for (int i = 0; i < 16; i++)
            {
                if (!OthersAlive(self)) return;
                Thread.Sleep(500);
            }
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != self && p.ProcessName.StartsWith("Pavise", StringComparison.OrdinalIgnoreCase))
                    { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        private static bool OthersAlive(int self)
        {
            bool alive = false;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != self && p.ProcessName.StartsWith("Pavise", StringComparison.OrdinalIgnoreCase)
                        && !p.ProcessName.EndsWith("selftest", StringComparison.OrdinalIgnoreCase)) alive = true;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return alive;
        }
    }
}
