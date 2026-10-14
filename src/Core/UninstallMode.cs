// 作者：bdth 2074055628@qq.com
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
        public bool RuntimeStopped;
        public int Warnings;
        public bool StartupRemoved;
    }

    internal static class UninstallMode
    {
        public static UninstallReport ExecuteInteractive(string dataDir, string exePath,
            Func<bool> stopRuntime, Action<string> progress)
        {
            return ExecuteInteractive(dataDir, exePath, stopRuntime, progress,
                TaskHelper.RemoveStartupTaskForUninstall);
        }

        internal static UninstallReport ExecuteInteractive(string dataDir, string exePath,
            Func<bool> stopRuntime, Action<string> progress, Func<bool> removeStartupTask)
        {
            var report = new UninstallReport();
            if (!LegacyPurge.IsSafeInteractiveDataRoot(dataDir) || string.IsNullOrWhiteSpace(exePath))
            { report.Failure = Lang.T("wipe.pathfail"); return report; }
            Report(progress, AutomaticPolicy.Text("停止对局守护", "Stopping game protection"));
            try { report.RuntimeStopped = stopRuntime != null && stopRuntime(); }
            catch { report.RuntimeStopped = false; }
            // 删除数据前统一停止运行时，单项恢复失败不阻断其他项。
            if (!report.RuntimeStopped)
            { report.Failure = Lang.T("wipe.stopfail"); return report; }

            Report(progress, AutomaticPolicy.Text("还原 Pavise 的系统改动", "Restoring changes made by Pavise"));
            List<string> unrestored;
            try { unrestored = LegacyPurge.RestorePersistent(dataDir, progress); }
            catch { unrestored = new List<string> { "restore" }; }
            if (unrestored == null) unrestored = new List<string> { "restore" };
            report.Warnings += unrestored.Count;

            Report(progress, AutomaticPolicy.Text("清除开机启动", "Removing startup registration"));
            try { report.StartupRemoved = removeStartupTask != null && removeStartupTask(); }
            catch { report.StartupRemoved = false; }
            if (!report.StartupRemoved) report.Warnings++;

            Report(progress, AutomaticPolicy.Text("清理软件配置与记录", "Clearing app settings and records"));
            if (unrestored.Count == 0)
            {
                int files, warnings;
                try
                {
                    report.Cleared = LegacyPurge.ClearRestoredData(dataDir, out files, out warnings);
                    report.Files = files; report.Warnings += warnings;
                }
                catch { report.Warnings++; }
            }
            else
            {
                // 清除偏好时保留恢复失败项所需原值。
                try
                {
                    ResetDataReport cleared = AppDataReset.ClearStoppedData(dataDir, progress);
                    report.Cleared = cleared.Completed;
                    report.Files = cleared.Files;
                    report.Warnings += cleared.Warnings;
                }
                catch { report.Warnings++; }
            }
            Report(progress, AutomaticPolicy.Text("准备退出并删除程序", "Preparing to exit and remove the executable"));
            return report;
        }

        private static void Report(Action<string> progress, string message)
        { try { if (progress != null) progress(message); } catch { } }

        public static UninstallReport Execute(string dataDir, string exeDir, Func<bool> stopRuntime)
        {
            return Execute(dataDir, exeDir, stopRuntime, TaskHelper.RemoveStartupTaskForUninstall);
        }

        internal static UninstallReport Execute(string dataDir, string exeDir, Func<bool> stopRuntime,
            Func<bool> removeStartupTask)
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
            // 确认开机任务删除后才清理配置，查询失败不等于任务不存在。
            try
            {
                if (removeStartupTask == null || !removeStartupTask())
                {
                    report.Failure = Lang.F("uninstall.taskfail", TaskHelper.LastSchtasksError ?? TaskHelper.TaskName);
                    return report;
                }
            }
            catch (Exception ex)
            {
                report.Failure = Lang.F("uninstall.taskfail", TaskHelper.TaskName + " (" + ex.GetType().Name + ")");
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
            report.Leftovers = 0;
            return report;
        }

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

        public static bool Run(string resultPath)
        {
            var report = new StringBuilder();
            bool ok = false;
            Mutex shutdownGate = null;
            try
            {
                Paths.Init();
                Lang.Init();
                string exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                UninstallReport result = Execute(Paths.Data, exeDir, delegate
                {
                    shutdownGate = StopOtherInstances();
                    return shutdownGate != null;
                });
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
            finally
            {
                if (shutdownGate != null)
                {
                    try { shutdownGate.ReleaseMutex(); } catch { }
                    shutdownGate.Dispose();
                }
            }
            try
            {
                if (!string.IsNullOrEmpty(resultPath))
                    File.WriteAllText(resultPath, report.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return ok;
        }

        // 恢复和删除期间持有单实例锁，停止失败时保留数据。
        private static Mutex StopOtherInstances()
        {
            try { using (var exit = EventWaitHandle.OpenExisting("Global\\Pavise_Exit")) exit.Set(); }
            catch { }
            return AcquireShutdownGate("Global\\Pavise_SingleInstance", 10000);
        }

        internal static Mutex AcquireShutdownGate(string mutexName, int timeoutMilliseconds)
        {
            Mutex gate = null;
            try
            {
                gate = new Mutex(false, mutexName);
                try { if (gate.WaitOne(timeoutMilliseconds)) return gate; }
                catch (AbandonedMutexException) { return gate; }
            }
            catch { }
            if (gate != null) gate.Dispose();
            return null;
        }
    }
}
