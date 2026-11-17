using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    // 微软后台（诊断服务、搜索索引、小组件及其 WebView2）并入普通后台治理。这里只按服务身份、可信路径、
    // 创建时间和家族关系核实对象；写入强度、核心放置、切出释放和退局恢复全部沿用普通后台的同一套策略与恢复归属。
    internal partial class GameMode
    {
        private sealed class WindowsBackgroundRead
        {
            internal int Failures;
            internal long Next;
            internal bool Allowed(long now) { return Failures < 3 && now >= Next; }
            internal void Fail(long now) { Failures++; Next = now + TimeSpan.FromSeconds(Failures * 2).Ticks; }
        }

        private readonly Dictionary<string, string> windowsBackgroundPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, WindowsBackgroundRead> windowsBackgroundPathReads = new Dictionary<string, WindowsBackgroundRead>(StringComparer.Ordinal);
        private readonly HashSet<string> windowsServiceHosts = new HashSet<string>(StringComparer.Ordinal);
        private List<KeyValuePair<string, int>> windowsServices;
        private WindowsBackgroundRead windowsServiceRead = new WindowsBackgroundRead();
        private long windowsBackgroundSession = -1;

        private string ReadWindowsBackgroundPath(ProcEntry process)
        {
            if (process == null || process.Pid <= 4 || process.Creation <= 0) return null;
            if (!string.IsNullOrEmpty(process.Path)) return process.Path;
            string key = process.Pid + "/" + process.Creation;
            string path;
            if (windowsBackgroundPaths.TryGetValue(key, out path)) return path;
            WindowsBackgroundRead read;
            if (!windowsBackgroundPathReads.TryGetValue(key, out read)) windowsBackgroundPathReads[key] = read = new WindowsBackgroundRead();
            long now = AutoCores.NowTicks;
            if (!read.Allowed(now)) return null;
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = GameAffinityJobNative.OpenIdentity(process.Pid, process.Creation);
                if (handle != IntPtr.Zero) path = Native.ImagePath(handle);
            }
            catch { path = null; }
            finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
            if (!string.IsNullOrEmpty(path))
            {
                windowsBackgroundPaths[key] = path;
                return path;
            }
            read.Fail(now);
            Logger.Warn("微软后台身份读取失败：进程编号=" + process.Pid + " 尝试=" + read.Failures
                + "/3" + (read.Failures >= 3 ? "；本局不再读取该目标" : ""));
            return null;
        }

        private static bool WindowsServiceHostName(string name)
        {
            return string.Equals(name, "svchost", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "svchost.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "SearchIndexer", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "SearchIndexer.exe", StringComparison.OrdinalIgnoreCase);
        }

        // 服务表只在出现新的服务宿主时重读；宿主身份未变时沿用上一次结果，不在每轮扫描枚举服务控制管理器。
        private List<KeyValuePair<string, int>> WindowsBackgroundServices(ProcessSnapshot snapshot, long now)
        {
            bool unseen = false;
            var hosts = new List<string>();
            foreach (ProcEntry process in snapshot.Entries)
            {
                if (process.Pid <= 4 || process.Session != 0 || process.Creation <= 0 || !WindowsServiceHostName(process.Name)) continue;
                string key = process.Pid + "/" + process.Creation;
                hosts.Add(key);
                if (!windowsServiceHosts.Contains(key)) unseen = true;
            }
            if (!unseen || !windowsServiceRead.Allowed(now)) return windowsServices;
            List<KeyValuePair<string, int>> services = SvcQuery.RunningServices();
            if (services == null)
            {
                windowsServiceRead.Fail(now);
                Logger.Warn("微软后台服务枚举失败：尝试=" + windowsServiceRead.Failures + "/3"
                    + (windowsServiceRead.Failures >= 3 ? "；本局沿用上一次结果，不再主动枚举" : ""));
                return windowsServices;
            }
            windowsServices = services;
            windowsServiceHosts.Clear();
            windowsServiceHosts.UnionWith(hosts);
            return services;
        }

        private bool ExplicitWindowsBackgroundProtection(ProcEntry process, string path)
        {
            lock (sync)
            {
                // 系统预设名单不豁免已核实的内置对象，用户手工添加的规则仍然生效。
                return WindowsBackgroundPolicy.ExplicitlyProtected(process, path, whiteRules, IsPresetWhitelistName);
            }
        }

        // 普通后台扫描每轮调用一次，返回本轮已核实的微软后台对象。
        private Dictionary<int, WindowsBackgroundTarget> SelectWindowsBackground(ProcessSnapshot snapshot, HashSet<int> gamePids, WhitelistEvaluation whitelist)
        {
            long serial = Interlocked.Read(ref gameSessionStarts);
            if (serial != windowsBackgroundSession)
            {
                windowsBackgroundSession = serial;
                windowsBackgroundPaths.Clear();
                windowsBackgroundPathReads.Clear();
                windowsServiceHosts.Clear();
                windowsServices = null;
                windowsServiceRead = new WindowsBackgroundRead();
            }
            var protectedPids = new HashSet<int>(gamePids ?? EmptyPidSet);
            protectedPids.Add(selfPid);
            protectedPids.UnionWith(whitelist.RuntimeProtected);
            lock (sync)
                WindowsBackgroundPolicy.AddExplicitFamilies(protectedPids, whiteRules, whitelist.ByRule, IsPresetWhitelistName);
            Dictionary<int, WindowsBackgroundTarget> selected = WindowsBackgroundPolicy.Select(snapshot, WindowsBackgroundServices(snapshot, AutoCores.NowTicks), selfSession,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), ReadWindowsBackgroundPath, protectedPids);
            foreach (WindowsBackgroundTarget target in new List<WindowsBackgroundTarget>(selected.Values))
                if (ExplicitWindowsBackgroundProtection(target.Process, target.Path)) selected.Remove(target.Process.Pid);
            // 诊断服务与索引宿主以 SYSTEM 运行，普通后台写入前需要调试权限才能打开。
            if (selected.Count != 0) Native.TryEnableDebugPrivilege();
            return selected;
        }
    }
}
