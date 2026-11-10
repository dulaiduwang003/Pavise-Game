using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    // 安全软件清单在启动时一次封存，会话和设置变化均不能重新打开探测。
    internal static class SecurityProductCatalog
    {
        private static readonly object sync = new object();
        private static SecurityProductSnapshot snapshot = SecurityProductSnapshot.Unknown(false, "");
        private static string[] roots = new string[0];
        private static int startupClaimed;
        private static int generation;
        private static SecurityProbeTerminationException startupBlocked;

        internal static SecurityProductSnapshot StartupSnapshot { get { lock (sync) return snapshot; } }

        internal static void StartupScanOnce()
        {
            if (Interlocked.CompareExchange(ref startupClaimed, 1, 0) != 0)
            { if (startupBlocked != null) throw startupBlocked; return; }
            SecurityProductSnapshot fresh;
            try { fresh = RunStartupProbe(); }
            catch (SecurityProbeTerminationException error)
            {
                lock (sync) { startupBlocked = error; snapshot = SecurityProductSnapshot.Unknown(true, "Helper termination unconfirmed; startup stopped"); }
                throw;
            }
            catch (Exception error) { fresh = SecurityProductSnapshot.Unknown(true, error.GetType().Name); }
            if (fresh == null) fresh = SecurityProductSnapshot.Unknown(true, "Empty startup result");
            lock (sync) { snapshot = fresh; roots = fresh.ProtectedRoots; generation = 1; }
            if (fresh.FailureSummary.Length != 0)
            {
                if (!fresh.ScanCompleted || fresh.HasUnknownState)
                    Logger.Warn("启动安全检查次数已耗尽，尝试=1/1；运行期间不重试： " + fresh.FailureSummary);
                else
                    Logger.Log("启动安全检查部分来源失败，尝试=1/1；备用来源已确认状态： " + fresh.FailureSummary);
            }
            int active = 0, inactive = 0, uncertain = 0;
            foreach (SecurityProductInfo product in fresh.Products)
            {
                if (product.State == SecurityProductState.Active) active++;
                else if (product.State == SecurityProductState.Inactive) inactive++;
                else uncertain++;
            }
            Logger.Log("启动安全清单已封存，产品数=" + fresh.Products.Length + " 受保护根进程=" + roots.Length
                + " 活动=" + active + " 非活动=" + inactive + " 待确认=" + uncertain + " 未知=" + fresh.HasUnknownState);
        }

        private static SecurityProductSnapshot RunStartupProbe() { return SecurityStartupProbe.ReadOnce(); }
        internal static int CurrentGeneration() { lock (sync) return generation; }
        internal static string[] Roots() { lock (sync) return (string[])roots.Clone(); }
        internal static bool IsSecurityProduct(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (sync)
            {
                foreach (string root in roots) if (FamilyBoundary.UnderRoot(path, root)) return true;
                return false;
            }
        }

        internal static string ProtectedRoot(string path)
        { return ProductRoot(path, StandardBases(), Environment.GetFolderPath(Environment.SpecialFolder.Windows)); }
        private static string[] StandardBases()
        {
            List<string> folderPaths = new List<string>();
            foreach (Environment.SpecialFolder folder in new Environment.SpecialFolder[3]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData
            })
            {
                string folderPath;
                try
                {
                    folderPath = Environment.GetFolderPath(folder);
                }
                catch
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(folderPath))
                {
                    folderPaths.Add(folderPath.TrimEnd('\\'));
                }
            }
            return folderPaths.ToArray();
        }

        internal static string ProductRoot(string registered, string[] bases, string windowsRoot)
        {
            if (string.IsNullOrEmpty(registered))
            {
                return null;
            }
            string expandEnvironmentVariablesResult = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
            if (expandEnvironmentVariablesResult.Length < 4 || expandEnvironmentVariablesResult[1] != ':' || expandEnvironmentVariablesResult[2] != '\\')
            {
                return null;
            }
            string directoryName;
            try
            {
                directoryName = Path.GetDirectoryName(expandEnvironmentVariablesResult);
            }
            catch
            {
                return null;
            }
            if (string.IsNullOrEmpty(directoryName) || directoryName.TrimEnd('\\').Length <= 2)
            {
                return null;
            }
            directoryName = directoryName.TrimEnd('\\');
            if (!string.IsNullOrEmpty(windowsRoot) && (WhitelistRule.PathEquals(directoryName, windowsRoot.TrimEnd('\\')) || FamilyBoundary.UnderRoot(directoryName, windowsRoot)))
            {
                return null;
            }
            foreach (string entryText in bases ?? new string[0])
            {
                if (!string.IsNullOrEmpty(entryText))
                {
                    if (WhitelistRule.PathEquals(directoryName, entryText))
                    {
                        return null;
                    }
                    if (FamilyBoundary.UnderRoot(directoryName, entryText))
                    {
                        int splitCount = directoryName.Substring(entryText.Length).Trim('\\').Split('\\')
                            .Length;
                        return (splitCount >= 3) ? Path.GetDirectoryName(directoryName) : directoryName;
                    }
                }
            }
            return directoryName;
        }


    }
}
