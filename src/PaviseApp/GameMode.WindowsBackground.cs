using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class WindowsPathRead
        {
            internal string Path;
            internal readonly WindowsBackgroundGovernor.FailurePhase Budget = new WindowsBackgroundGovernor.FailurePhase();
        }

        private readonly WindowsBackgroundGovernor windowsBackground = new WindowsBackgroundGovernor(new WindowsBackgroundJobs());
        private readonly Dictionary<string, WindowsPathRead> windowsBackgroundPaths = new Dictionary<string, WindowsPathRead>(StringComparer.Ordinal);
        private WindowsBackgroundGovernor.FailurePhase windowsServiceReadBudget = new WindowsBackgroundGovernor.FailurePhase();
        private long windowsBackgroundSession = -1, windowsBackgroundNext;

        private string ReadWindowsBackgroundPath(ProcEntry process)
        {
            if (process == null || process.Pid <= 4 || process.Creation <= 0) return null;
            if (!string.IsNullOrEmpty(process.Path)) return process.Path;
            string key = process.Pid + "/" + process.Creation;
            WindowsPathRead entry;
            if (!windowsBackgroundPaths.TryGetValue(key, out entry)) windowsBackgroundPaths[key] = entry = new WindowsPathRead();
            if (entry.Path != null) return entry.Path;
            long now = AutoCores.NowTicks;
            if (!entry.Budget.Allowed(now)) return null;
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = GameAffinityJobNative.OpenIdentity(process.Pid, process.Creation);
                if (handle != IntPtr.Zero) entry.Path = Native.ImagePath(handle);
            }
            catch { entry.Path = null; }
            finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
            if (!string.IsNullOrEmpty(entry.Path)) return entry.Path;
            entry.Budget.Fail(now);
            Logger.Warn("微软后台身份读取失败：进程编号=" + process.Pid + " 尝试=" + entry.Budget.Failures
                + "/3" + (entry.Budget.Exhausted ? "；停止本局该目标的主动读取，保留恢复所有权" : ""));
            return null;
        }

        private bool ExplicitWindowsBackgroundProtection(ProcEntry process, string path)
        {
            lock (sync)
            {
                // 系统预设名单不豁免已核实的内置治理对象，用户手工添加的规则仍然生效。
                return WindowsBackgroundPolicy.ExplicitlyProtected(process, path, whiteRules, IsPresetWhitelistName);
            }
        }

        private void StepWindowsBackground(ProcessSnapshot snapshot, HashSet<int> gamePids, bool switchedOut)
        {
            if (!EffSuppress || switchedOut || !enabled || stopping || panicReq || ProfileStoreSaveFailed)
            { windowsBackground.Restore(); return; }
            int epoch = FamilyPolicyEpoch;
            long serial = Interlocked.Read(ref gameSessionStarts);
            long now = AutoCores.NowTicks;
            if (serial != windowsBackgroundSession)
            {
                windowsBackgroundSession = serial; windowsBackgroundNext = 0;
                windowsBackgroundPaths.Clear(); windowsServiceReadBudget = new WindowsBackgroundGovernor.FailurePhase();
            }
            if (now < windowsBackgroundNext && !firstSweep) return;
            windowsBackgroundNext = now + TimeSpan.FromMilliseconds(1500).Ticks;
            List<KeyValuePair<string, int>> services = null;
            if (windowsServiceReadBudget.Allowed(now))
            {
                services = SvcQuery.RunningServices();
                if (services == null)
                {
                    windowsServiceReadBudget.Fail(now);
                    Logger.Warn("微软后台服务枚举失败：尝试=" + windowsServiceReadBudget.Failures + "/3"
                        + (windowsServiceReadBudget.Exhausted ? "；本局停止主动枚举，保留恢复所有权" : ""));
                }
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var protectedPids = new HashSet<int>(gamePids ?? EmptyPidSet);
            protectedPids.Add(selfPid);
            WhitelistEvaluation whitelist = EvaluateWhitelist(snapshot);
            protectedPids.UnionWith(whitelist.RuntimeProtected);
            lock (sync)
                WindowsBackgroundPolicy.AddExplicitFamilies(protectedPids, whiteRules, whitelist.ByRule, IsPresetWhitelistName);
            Dictionary<int, WindowsBackgroundTarget> selected = WindowsBackgroundPolicy.Select(snapshot, services, selfSession,
                windows, programFiles, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), ReadWindowsBackgroundPath, protectedPids);
            foreach (WindowsBackgroundTarget target in new List<WindowsBackgroundTarget>(selected.Values))
                if (ExplicitWindowsBackgroundProtection(target.Process, target.Path)) selected.Remove(target.Process.Pid);
            PolicySnapshot policy = sessionPolicy ?? PolicyResolver.Global();
            ulong allowed = CpuTopology.BackgroundAllowedMask();
            ulong isolated = ManualCoreBackgroundTarget();
            if (isolated != 0) allowed &= isolated;
            ulong gameReserved = GameBackgroundReservedCores();
            allowed &= ~gameReserved;
            ulong mask = CpuTopology.MultiGroup ? 0 : WindowsBackgroundPolicy.SelectMask(CpuTopology.PhysicalCoreMasks(), allowed,
                CpuTopology.EffMask, policy.BackgroundGovernanceMode);
            lock (familyPolicyGate)
            {
                if (!FamilyPolicyEpochCurrent(epoch) || SmartSceneSwitchedOut) { windowsBackground.Restore(); return; }
                windowsBackground.Update(serial, policy.BackgroundGovernanceMode, mask, selected, delegate(WindowsBackgroundTarget target)
                {
                    BackgroundReleaseState released = core.ReleaseBackgroundForRenderer(target.Process.Pid, target.Process.Creation, target.Process.Name);
                    return released == BackgroundReleaseState.Ready || released == BackgroundReleaseState.Gone;
                }, () => FamilyPolicyEpochCurrent(epoch) && !SmartSceneSwitchedOut && GameBackgroundReservedCores() == gameReserved,
                    now, !CpuTopology.MultiGroup && (gameReserved != 0 || isolated != 0));
            }
        }

        private bool DelegateWindowsBackground(ProcEntry process)
        {
            if (windowsBackground.Reserved(process.Pid, process.Creation)) return true;
            string name = WhitelistRule.NormalizeName(process.Name).ToLowerInvariant();
            if (name != "searchindexer" && name != "searchprotocolhost" && name != "searchfilterhost"
                && name != "widgets" && name != "widgetservice") return false;
            return WindowsBackgroundPolicy.DelegatedImage(name, ReadWindowsBackgroundPath(process),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        }
    }
}
