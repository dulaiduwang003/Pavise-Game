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

        private static List<RunningProgram> Scan(Func<bool> canceled, bool whitelist)
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
                HashSet<int> visiblePids = RunningProgramWindows.Capture(out succeeded);
                if (!succeeded)
                {
                    return null;
                }
                if (IsCanceled(canceled))
                {
                    return null;
                }
                runningProcesses = Process.GetProcesses();
                Dictionary<string, RunningProgram> runningProgramsByName = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < runningProcesses.Length; i++)
                {
                    if (IsCanceled(canceled))
                    {
                        return null;
                    }
                    Process runningProcesse = runningProcesses[i];
                    try
                    {
                        int id2 = runningProcesse.Id;
                        if (id2 <= 4 || id2 == id || !visiblePids.Contains(id2) || runningProcesse.SessionId != sessionId)
                        {
                            continue;
                        }
                        IntPtr processHandle = Native.OpenProcess(4096, false, id2);
                        if (processHandle == IntPtr.Zero)
                        {
                            continue;
                        }
                        string pathValue;
                        try
                        {
                            pathValue = Native.ImagePath(processHandle);
                        }
                        finally
                        {
                            Native.CloseHandle(processHandle);
                        }
                        string processNameValue = GameSessionDetector.ImageNameFromVerifiedPath(pathValue);
                        if (!(whitelist ? IsWhitelistCandidate(processNameValue, pathValue, windowsRoot)
                            : GameSessionDetector.IsLibraryCandidate(processNameValue, pathValue, windowsRoot)))
                        {
                            continue;
                        }
                        RunningProgram currentRunningProgram;
                        if (!runningProgramsByName.TryGetValue(pathValue, out currentRunningProgram))
                        {
                            RunningProgram runningProgram = new RunningProgram();
                            runningProgram.Path = pathValue;
                            runningProgram.ProcessName = processNameValue;
                            runningProgram.Title = processNameValue;
                            currentRunningProgram = runningProgram;
                            runningProgramsByName.Add(pathValue, currentRunningProgram);
                        }
                        if (!currentRunningProgram.ProcessIds.Contains(id2))
                        {
                            currentRunningProgram.ProcessIds.Add(id2);
                            try
                            {
                                long workingSet64Limit = Math.Max(0L, runningProcesse.WorkingSet64);
                                currentRunningProgram.Memory = ((workingSet64Limit > long.MaxValue - currentRunningProgram.Memory) ? long.MaxValue : (currentRunningProgram.Memory + workingSet64Limit));
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
                        if (runningProcesse != null)
                        {
                            try
                            {
                                runningProcesse.Dispose();
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                List<RunningProgram> valuesSnapshot = new List<RunningProgram>(runningProgramsByName.Values);
                foreach (RunningProgram currentValuesSnapshot in valuesSnapshot)
                {
                    if (IsCanceled(canceled))
                    {
                        return null;
                    }
                    currentValuesSnapshot.Title = TitleOf(currentValuesSnapshot.Path, currentValuesSnapshot.ProcessName);
                }
                return IsCanceled(canceled) ? null : valuesSnapshot;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (runningProcesses != null)
                {
                    foreach (Process processItem in runningProcesses)
                    {
                        if (processItem != null)
                        {
                            try
                            {
                                processItem.Dispose();
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
            // 白名单同样保护普通应用，不套用仅针对游戏的排除规则。
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
