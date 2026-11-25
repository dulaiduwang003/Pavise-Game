using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PaviseApp
{
    internal static class RunningProgramDiscovery
    {
        internal static List<RunningProgram> Scan(Func<bool> canceled)
        {
            return Scan(canceled, false);
        }

        internal static List<RunningProgram> ScanForWhitelist(Func<bool> canceled)
        {
            return Scan(canceled, true);
        }

        internal static List<RunningProgram> ScanForBackgroundFocus(Func<bool> canceled, ProcessSnapshot snapshot)
        {
            return snapshot == null ? null : Scan(canceled, true, snapshot);
        }

        private static List<RunningProgram> Scan(Func<bool> canceled, bool whitelist, ProcessSnapshot focusSnapshot = null)
        {
            Process[] runningProcesses = null;
            try
            {
                if (IsCanceled(canceled))
                {
                    return null;
                }
                int sessionId;
                int id;
                using (Process process = Process.GetCurrentProcess())
                {
                    sessionId = process.SessionId;
                    id = process.Id;
                }
                if (sessionId < 0)
                {
                    return null;
                }
                string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string windowsRoot = (string.IsNullOrEmpty(folderPath) ? "C:\\Windows\\" : (folderPath.TrimEnd('\\') + "\\"));
                bool succeeded;
                HashSet<int> hashSet = RunningProgramWindows.Capture(out succeeded);
                if (!succeeded)
                {
                    return null;
                }
                if (IsCanceled(canceled))
                {
                    return null;
                }
                runningProcesses = Process.GetProcesses();
                Dictionary<string, RunningProgram> dictionary = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < runningProcesses.Length; i++)
                {
                    if (IsCanceled(canceled))
                    {
                        return null;
                    }
                    Process process2 = runningProcesses[i];
                    try
                    {
                        int id2 = process2.Id;
                        // 游戏库只收有窗口的程序 白名单还要收没有窗口的后台进程 驱动助手和服务助手多半没有窗口 却正是会被治理的那些
                        bool hasWindow = hashSet.Contains(id2);
                        if (id2 <= 4 || id2 == id || (!whitelist && !hasWindow) || process2.SessionId != sessionId)
                        {
                            continue;
                        }
                        IntPtr intPtr = Native.OpenProcess(4096, false, id2);
                        if (intPtr == IntPtr.Zero)
                        {
                            continue;
                        }
                        string pathValue;
                        ProcEntry focusProcess = focusSnapshot == null ? null : focusSnapshot.Find(id2);
                        try
                        {
                            if (focusSnapshot != null && (focusProcess == null || focusProcess.Session != sessionId
                                || Native.QueryProcessIdentityState(intPtr, focusProcess.Creation) != 0))
                                continue;
                            pathValue = Native.ImagePath(intPtr);
                        }
                        finally
                        {
                            Native.CloseHandle(intPtr);
                        }
                        string processNameValue = focusProcess == null ? GameSessionDetector.ImageNameFromVerifiedPath(pathValue) : focusProcess.Name;
                        if (!(whitelist ? IsWhitelistCandidate(processNameValue, pathValue, windowsRoot)
                            : GameSessionDetector.IsLibraryCandidate(processNameValue, pathValue, windowsRoot)))
                        {
                            continue;
                        }
                        string key = focusSnapshot == null ? pathValue : BackgroundFocusItem.Identity(processNameValue, pathValue);
                        if (key == null) continue;
                        RunningProgram value;
                        if (!dictionary.TryGetValue(key, out value))
                        {
                            RunningProgram runningProgram = new RunningProgram();
                            runningProgram.Path = pathValue;
                            runningProgram.ProcessName = processNameValue;
                            runningProgram.Title = processNameValue;
                            value = runningProgram;
                            dictionary.Add(key, value);
                        }
                        if (hasWindow) value.HasWindow = true;
                        if (!value.ProcessIds.Contains(id2))
                        {
                            value.ProcessIds.Add(id2);
                            try
                            {
                                long workingSet64Limit = Math.Max(0L, process2.WorkingSet64);
                                value.Memory = ((workingSet64Limit > long.MaxValue - value.Memory) ? long.MaxValue : (value.Memory + workingSet64Limit));
                            }
                            catch
                            {
                            }
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        runningProcesses[i] = null;
                        if (process2 != null)
                        {
                            try
                            {
                                process2.Dispose();
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                List<RunningProgram> list = new List<RunningProgram>(dictionary.Values);
                foreach (RunningProgram item in list)
                {
                    if (IsCanceled(canceled))
                    {
                        return null;
                    }
                    item.Title = TitleOf(item.Path, item.ProcessName);
                }
                return IsCanceled(canceled) ? null : list;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (runningProcesses != null)
                {
                    Process[] processItems = runningProcesses;
                    foreach (Process process3 in processItems)
                    {
                        if (process3 != null)
                        {
                            try
                            {
                                process3.Dispose();
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
        }

        internal static bool IsWhitelistCandidate(string name, string path, string windowsRoot)
        {
            // 白名单同样保护普通应用 不套用仅针对游戏的排除规则
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)
                || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || GameSessionDetector.IsNonGameRole(name, path)) return false;
            return string.IsNullOrEmpty(windowsRoot)
                || !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCanceled(Func<bool> canceled)
        {
            if (canceled != null)
            {
                return canceled();
            }
            return false;
        }

        private static string TitleOf(string path, string fallback)
        {
            try
            {
                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
                string fileDescription = ((!string.IsNullOrWhiteSpace(versionInfo.FileDescription)) ? versionInfo.FileDescription : versionInfo.ProductName);
                if (!string.IsNullOrWhiteSpace(fileDescription))
                {
                    return fileDescription.Trim();
                }
            }
            catch
            {
            }
            return fallback;
        }
    }
}
