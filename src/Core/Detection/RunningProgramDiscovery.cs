// File purpose Read-only discovery shared by the game and whitelist pickers; UI resources are owned by each side
// A failed or cancelled snapshot and an empty snapshot are two different things
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PaviseApp
{
    internal sealed class RunningProgram
    {
        public string Path;
        public string Title;
        public string ProcessName;
        public long Memory;
        public readonly List<int> ProcessIds = new List<int>();

        internal static string FormatMemory(long bytes)
        {
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("0.0") + " GB";
            if (bytes >= 1048576L) return (bytes / 1048576.0).ToString("0") + " MB";
            if (bytes <= 0) return "";
            return (bytes / 1024.0).ToString("0") + " KB";
        }
    }

    internal static class RunningProgramDiscovery
    {
        internal static List<RunningProgram> Scan(Func<bool> canceled)
        {
            Process[] processes = null;
            try
            {
                if (IsCanceled(canceled)) return null;
                int session, selfPid;
                using (Process current = Process.GetCurrentProcess())
                {
                    session = current.SessionId;
                    selfPid = current.Id;
                }
                if (session < 0) return null;

                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string windowsRoot = string.IsNullOrEmpty(windows)
                    ? @"C:\Windows\" : windows.TrimEnd('\\') + "\\";
                bool windowsRead;
                HashSet<int> visible = GameSessionDetector.VisibleWindowPids(true, out windowsRead);
                if (!windowsRead) return null;
                if (IsCanceled(canceled)) return null;
                processes = Process.GetProcesses();
                var byPath = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < processes.Length; i++)
                {
                    if (IsCanceled(canceled)) return null;
                    Process process = processes[i];
                    try
                    {
                        int pid = process.Id;
                        if (pid <= 4 || pid == selfPid || !visible.Contains(pid)) continue;
                        if (process.SessionId != session) continue;

                        string path;
                        IntPtr handle = Native.OpenProcess(
                            Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                        if (handle == IntPtr.Zero) continue;
                        try { path = Native.ImagePath(handle); }
                        finally { Native.CloseHandle(handle); }
                        string name = GameSessionDetector.ImageNameFromVerifiedPath(path);
                        if (!GameSessionDetector.IsLibraryCandidate(name, path, windowsRoot)) continue;

                        RunningProgram program;
                        if (!byPath.TryGetValue(path, out program))
                        {
                            program = new RunningProgram
                            {
                                Path = path,
                                ProcessName = name,
                                Title = name
                            };
                            byPath.Add(path, program);
                        }
                        if (program.ProcessIds.Contains(pid)) continue;
                        program.ProcessIds.Add(pid);
                        try
                        {
                            long memory = Math.Max(0, process.WorkingSet64);
                            program.Memory = memory > long.MaxValue - program.Memory
                                ? long.MaxValue : program.Memory + memory;
                        }
                        catch { }
                    }
                    // In an overall successful snapshot, individual processes may exit midway or refuse queries
                    // they do not invalidate the other entries
                    catch { }
                    finally
                    {
                        processes[i] = null;
                        if (process != null) { try { process.Dispose(); } catch { } }
                    }
                }

                var result = new List<RunningProgram>(byPath.Values);
                foreach (RunningProgram program in result)
                {
                    if (IsCanceled(canceled)) return null;
                    program.Title = TitleOf(program.Path, program.ProcessName);
                }
                return IsCanceled(canceled) ? null : result;
            }
            catch { return null; }
            finally
            {
                if (processes != null)
                    foreach (Process process in processes)
                        if (process != null) { try { process.Dispose(); } catch { } }
            }
        }

        private static bool IsCanceled(Func<bool> canceled)
        {
            return canceled != null && canceled();
        }

        private static string TitleOf(string path, string fallback)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                string title = !string.IsNullOrWhiteSpace(info.FileDescription)
                    ? info.FileDescription : info.ProductName;
                if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
            }
            catch { }
            return fallback;
        }
    }
}
