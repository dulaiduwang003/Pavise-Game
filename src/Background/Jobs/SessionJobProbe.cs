using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    // 自动启动交接时读出本进程所在作业的限制与成员 只用来在启动日志里区分现代待机的系统会话作业和其他作业
    internal sealed class SessionJobFacts
    {
        // Known 为 false 表示作业归属未知 Members 为 -1 表示成员列表不可读
        internal bool Known, InJob, AoAc, SessionShellInJob;
        internal uint LimitFlags, UiRestrictions, CpuRateFlags;
        internal int Members = -1;
        internal int AnchorKind, AnchorPid, AnchorSession = -1;
        internal long AnchorCreation;
        internal string AnchorState = "shell-window-absent";
        internal string AnchorObservations = "";
        // 失败步骤及系统错误码 成功时为空
        internal string Failure = "";

        internal string Describe()
        {
            if (!Known) return Logger.Text("作业归属=读不到", "jobMembership=unreadable") + (string.IsNullOrEmpty(Failure) ? "" : Logger.Text(" 失败步骤=", " failedStep=") + Failure);
            if (!InJob) return Logger.Text("作业归属=不在作业里", "jobMembership=not-in-job");
            return Logger.Text("现代待机=", "modernStandby=") + (AoAc ? Logger.Text("是", "yes") : Logger.Text("否", "no")) + " jobFlags=0x" + LimitFlags.ToString("X", CultureInfo.InvariantCulture)
                + " uiLimits=0x" + UiRestrictions.ToString("X", CultureInfo.InvariantCulture) + " cpuRate=0x" + CpuRateFlags.ToString("X", CultureInfo.InvariantCulture)
                + Logger.Text(" 成员=", " members=") + (Members < 0 ? Logger.Text("读不到", "unreadable") : Members.ToString(CultureInfo.InvariantCulture)) + Logger.Text(" 含会话外壳=", " sessionShellInJob=") + (SessionShellInJob ? Logger.Text("是", "yes") : Logger.Text("否", "no"))
                + Logger.Text(" 锚点=", " anchor=") + (AnchorKind == SessionJobAnchors.DesktopShell ? Logger.Text("当前桌面壳", "current-desktop-shell") : AnchorKind == SessionJobAnchors.SessionHost ? Logger.Text("会话宿主", "session-host") : Logger.Text("无", "none"))
                + Logger.Text(" 锚点PID=", " anchorPid=") + AnchorPid + Logger.Text(" 锚点状态=", " anchorState=") + DescribeAnchor()
                + (string.IsNullOrEmpty(AnchorObservations) ? "" : Logger.Text(" 锚点读取明细=", " anchorReads=") + AnchorObservations)
                + (string.IsNullOrEmpty(Failure) ? "" : Logger.Text(" 失败步骤=", " failedStep=") + Failure);
        }

        private string DescribeAnchor()
        {
            if (AnchorState == "verified") return Logger.Text("身份及两次成员交集已核验", "identity and two-read member intersection verified");
            if (AnchorState == "shell-window-absent") return Logger.Text("系统未提供桌面壳窗口", "system provided no desktop shell window");
            if (AnchorState == "anchor-not-in-job") return Logger.Text("可信会话进程不在当前作业", "trusted session process not in the current job");
            if (AnchorState == "members-unreadable") return Logger.Text("作业成员不可读取", "job members unreadable");
            return Logger.Text("会话进程身份不可核验", "session process identity cannot be verified");
        }
    }

    // 限制读一次 成员读两次取交集 缓冲不足时只扩容重读一次
    internal static class SessionJobProbe
    {
        private const int ExtendedLimitInformation = 9, BasicUiRestrictions = 4, CpuRateControlInformation = 15, BasicProcessIdList = 3;
        private const int MoreData = 234;
        private const uint BreakawayOk = 0x800, SilentBreakawayOk = 0x1000;
        // 名称只用来找候选 是否可信另看固定句柄 系统路径 创建时间和成员交集
        internal const string SessionShellProcess = "sihost";

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CpuRate
        {
            public uint ControlFlags, Value;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out ExtendedLimits info, int length, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out uint info, int length, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out CpuRate info, int length, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr buffer, int length, IntPtr returnLength);

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool GetPwrCapabilities(IntPtr capabilities);

        // SYSTEM_POWER_CAPABILITIES 共 76 字节 AoAc 在第 20 字节
        private const int PowerCapabilitiesBytes = 76, AoAcOffset = 20;

        // 现代待机的系统会话作业 不带任何限制 允许子进程静默脱离 并且可信的会话外壳或宿主也在作业里
        // 不推断作业类型或未知祖先 限制或成员读不全时一律不算
        internal static bool SystemSessionJob(SessionJobFacts facts, out string reason)
        {
            if (facts == null || !facts.Known) { reason = Logger.Text("作业归属读不到", "job membership unreadable"); return false; }
            if (!facts.InJob) { reason = Logger.Text("不在作业里", "not in a job"); return false; }
            if (!string.IsNullOrEmpty(facts.Failure)) { reason = Logger.Text("作业信息没有读全", "job information not fully read"); return false; }
            if (!facts.AoAc) { reason = Logger.Text("未满足现代待机兼容路径条件", "modern standby compatibility path conditions not met"); return false; }
            if ((facts.LimitFlags & ~(BreakawayOk | SilentBreakawayOk)) != 0) { reason = Logger.Text("作业带有限制或关闭即终止标志", "job has limits or the kill-on-close flag"); return false; }
            if ((facts.LimitFlags & SilentBreakawayOk) == 0) { reason = Logger.Text("作业不允许子进程自行脱离，未满足兼容路径条件", "job does not let child processes break away on their own, compatibility path conditions not met"); return false; }
            if (facts.UiRestrictions != 0) { reason = Logger.Text("作业带有界面限制", "job has UI restrictions"); return false; }
            if ((facts.CpuRateFlags & 1) != 0) { reason = Logger.Text("作业启用了处理器限额", "job has CPU rate control enabled"); return false; }
            if (facts.Members <= 0) { reason = Logger.Text("作业成员证据不足", "insufficient job member evidence"); return false; }
            if (facts.AnchorState != "verified"
                || (facts.AnchorKind != SessionJobAnchors.SessionHost && facts.AnchorKind != SessionJobAnchors.DesktopShell)
                || facts.AnchorPid <= 0 || facts.AnchorCreation <= 0 || facts.AnchorSession <= 0)
            { reason = Logger.Text("缺少可信会话外壳或宿主的身份与成员交集，不能排除私有容器", "missing identity and member intersection of a trusted session shell or host, a private container cannot be ruled out"); return false; }
            reason = "";
            return true;
        }

        internal static SessionJobFacts ReadOwn()
        {
            long creation;
            try { creation = SessionJobAnchors.CurrentCreation(); }
            catch { return new SessionJobFacts { Failure = "owner-identity" }; }
            return ReadOwn(creation, SessionJobAnchors.CaptureDesktop(creation));
        }

        private static SessionJobFacts ReadOwn(long ownerCreation, SessionJobAnchor hint)
        {
            var facts = new SessionJobFacts();
            try
            {
                bool inJob;
                if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob)) { facts.Failure = "membership:" + Marshal.GetLastWin32Error(); return facts; }
                facts.Known = true;
                facts.InJob = inJob;
                if (!inJob) return facts;
                string failure;
                facts.AoAc = AoAcSupported(out failure);
                ExtendedLimits limits;
                if (!QueryInformationJobObject(IntPtr.Zero, ExtendedLimitInformation, out limits, Marshal.SizeOf(typeof(ExtendedLimits)), IntPtr.Zero))
                { failure = Join(failure, "limits:" + Marshal.GetLastWin32Error()); facts.LimitFlags = uint.MaxValue; }
                else facts.LimitFlags = limits.Basic.LimitFlags;
                uint ui;
                if (!QueryInformationJobObject(IntPtr.Zero, BasicUiRestrictions, out ui, 4, IntPtr.Zero))
                { failure = Join(failure, "ui:" + Marshal.GetLastWin32Error()); facts.UiRestrictions = uint.MaxValue; }
                else facts.UiRestrictions = ui;
                CpuRate rate;
                if (!QueryInformationJobObject(IntPtr.Zero, CpuRateControlInformation, out rate, Marshal.SizeOf(typeof(CpuRate)), IntPtr.Zero))
                { failure = Join(failure, "cpu:" + Marshal.GetLastWin32Error()); facts.CpuRateFlags = uint.MaxValue; }
                else facts.CpuRateFlags = rate.ControlFlags;
                facts.Failure = failure;
                SessionJobAnchors.ReadEvidence(facts, ownerCreation, hint, delegate { return ReadMembers(facts); });
            }
            catch (Exception error)
            {
                facts.Failure = "exception-" + error.GetType().Name.ToLowerInvariant();
                if (facts.Failure.Length > 48) facts.Failure = facts.Failure.Substring(0, 48);
            }
            return facts;
        }

        private static bool AoAcSupported(out string failure)
        {
            failure = "";
            IntPtr buffer = Marshal.AllocHGlobal(PowerCapabilitiesBytes + 4);
            try
            {
                for (int i = 0; i < PowerCapabilitiesBytes + 4; i++) Marshal.WriteByte(buffer, i, 0);
                if (!GetPwrCapabilities(buffer)) { failure = "power:" + Marshal.GetLastWin32Error(); return false; }
                return Marshal.ReadByte(buffer, AoAcOffset) != 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // 只留第一次失败 后面的错误不覆盖根因
        private static string Join(string first, string second)
        {
            if (string.IsNullOrEmpty(first)) return second ?? "";
            return first;
        }

        private static HashSet<int> ReadMembers(SessionJobFacts facts)
        {
            int capacity = 2048;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int bytes = checked(8 + IntPtr.Size * capacity);
                IntPtr buffer = Marshal.AllocHGlobal(bytes);
                try
                {
                    Marshal.WriteInt64(buffer, 0, 0);
                    bool ok = QueryInformationJobObject(IntPtr.Zero, BasicProcessIdList, buffer, bytes, IntPtr.Zero);
                    int error = ok ? 0 : Marshal.GetLastWin32Error();
                    if (!ok && error == MoreData && attempt == 0)
                    {
                        capacity = Math.Max(capacity * 2, checked(Marshal.ReadInt32(buffer, 0) + 256));
                        continue;
                    }
                    if (!ok) { facts.Failure = Join(facts.Failure, "members:" + error); return null; }
                    int assigned = Marshal.ReadInt32(buffer, 0);
                    int listed = Marshal.ReadInt32(buffer, 4);
                    if (listed < 0 || listed > capacity || assigned != listed) { facts.Failure = Join(facts.Failure, "members:range"); return null; }
                    facts.Members = listed;
                    var members = new HashSet<int>();
                    for (int i = 0; i < listed; i++)
                    {
                        long pid = Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size).ToInt64();
                        if (pid <= 0 || pid > int.MaxValue) { facts.Failure = Join(facts.Failure, "members:pid"); return null; }
                        members.Add((int)pid);
                    }
                    return members;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            facts.Failure = Join(facts.Failure, "members:more");
            return null;
        }
    }

    internal sealed class SessionJobAnchor
    {
        internal int Kind, Pid, Session = -1;
        internal long Creation;
        internal string Path = "", State = "shell-window-absent";
    }

    // 会话外壳 explorer 与会话宿主 sihost 的身份核验 固定句柄后核对系统路径 会话和创建时间
    internal static class SessionJobAnchors
    {
        internal const int SessionHost = 1, DesktopShell = 2;
        private const uint QueryAndSynchronize = 0x101000;
        private sealed class Pin : IDisposable
        {
            internal IntPtr Handle;
            internal SessionJobAnchor Identity;
            public void Dispose() { if (Handle != IntPtr.Zero) { CloseHandle(Handle); Handle = IntPtr.Zero; } }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr handle, int flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetWindowsDirectory(StringBuilder path, uint length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetSystemDirectory(StringBuilder path, uint length);

        private static string SystemPath(int kind)
        {
            var path = new StringBuilder(32768);
            uint count = kind == DesktopShell ? GetWindowsDirectory(path, (uint)path.Capacity) : GetSystemDirectory(path, (uint)path.Capacity);
            if (count == 0 || count >= path.Capacity) throw new IOException("系统目录不可核验");
            return System.IO.Path.Combine(path.ToString(), kind == DesktopShell ? "explorer.exe" : "sihost.exe");
        }

        private static int CurrentSession()
        {
            uint session;
            using (Process self = Process.GetCurrentProcess())
                if (!ProcessIdToSessionId((uint)self.Id, out session) || session > int.MaxValue)
                    throw new IOException("本进程会话编号不可核验");
            return (int)session;
        }

        internal static long CurrentCreation()
        {
            long creation, exit, kernel, user;
            if (!GetProcessTimes(new IntPtr(-1), out creation, out exit, out kernel, out user) || creation <= 0)
                throw new IOException("本进程创建时间不可核验");
            return creation;
        }

        private static bool Read(IntPtr handle, int kind, out SessionJobAnchor identity)
        {
            identity = null;
            uint pid = GetProcessId(handle), session;
            long creation, exit, kernel, user;
            var path = new StringBuilder(32768); int length = path.Capacity;
            if (pid == 0 || pid > int.MaxValue || WaitForSingleObject(handle, 0) != 258
                || !GetProcessTimes(handle, out creation, out exit, out kernel, out user) || creation <= 0 || exit != 0
                || !ProcessIdToSessionId(pid, out session) || session > int.MaxValue
                || !QueryFullProcessImageName(handle, 0, path, ref length) || length <= 0
                || WaitForSingleObject(handle, 0) != 258) return false;
            identity = new SessionJobAnchor { Kind = kind, Pid = (int)pid, Creation = creation, Session = (int)session, Path = path.ToString(), State = "verified" };
            return true;
        }

        internal static bool IdentityMatches(SessionJobAnchor expected, SessionJobAnchor actual,
            long ownerCreation, int session, string systemPath)
        {
            return expected != null && actual != null && (expected.Kind == DesktopShell || expected.Kind == SessionHost)
                && expected.Kind == actual.Kind && expected.Pid > 0 && expected.Pid == actual.Pid
                && expected.Creation > 0 && expected.Creation == actual.Creation && actual.Creation < ownerCreation
                && expected.Session == session && actual.Session == session && session > 0
                && !string.IsNullOrEmpty(systemPath) && string.Equals(actual.Path, systemPath, StringComparison.OrdinalIgnoreCase);
        }

        private static Pin Open(SessionJobAnchor expected, long ownerCreation, int session)
        {
            if (expected == null || expected.Pid <= 0) return null;
            IntPtr handle = OpenProcess(QueryAndSynchronize, false, expected.Pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                SessionJobAnchor actual;
                if (!Read(handle, expected.Kind, out actual)) return null;
                if (expected.Creation == 0) expected = new SessionJobAnchor { Kind = expected.Kind, Pid = expected.Pid, Creation = actual.Creation, Session = session };
                if (!IdentityMatches(expected, actual, ownerCreation, session, SystemPath(expected.Kind))) return null;
                var pin = new Pin { Handle = handle, Identity = actual };
                handle = IntPtr.Zero;
                return pin;
            }
            finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
        }

        internal static SessionJobAnchor CaptureDesktop(long ownerCreation)
        {
            var unavailable = new SessionJobAnchor();
            try
            {
                IntPtr window = GetShellWindow(); uint pid;
                if (window == IntPtr.Zero) return unavailable;
                unavailable.State = "shell-identity-unknown";
                if (GetWindowThreadProcessId(window, out pid) == 0 || pid == 0 || pid > int.MaxValue) return unavailable;
                using (Pin pin = Open(new SessionJobAnchor { Kind = DesktopShell, Pid = (int)pid }, ownerCreation, CurrentSession()))
                {
                    if (pin == null || GetShellWindow() != window) return unavailable;
                    uint after;
                    if (GetWindowThreadProcessId(window, out after) == 0 || after != pid) return unavailable;
                    return pin.Identity;
                }
            }
            catch { unavailable.State = "shell-identity-unknown"; return unavailable; }
        }

        internal static void ReadEvidence(SessionJobFacts facts, long ownerCreation, SessionJobAnchor hint,
            Func<HashSet<int>> readMembers)
        {
            var pins = new List<Pin>();
            facts.AnchorState = hint == null ? "shell-window-absent" : hint.State;
            facts.AnchorObservations = "hint:" + facts.AnchorState + ":" + (hint == null ? 0 : hint.Pid)
                + ":" + (hint == null ? 0 : hint.Creation) + ":" + (hint == null ? -1 : hint.Session);
            bool unknown = false;
            try
            {
                int session = CurrentSession();
                if (hint != null && hint.Pid > 0)
                {
                    Pin desktop = Open(hint, ownerCreation, session);
                    if (desktop == null) { unknown = true; Observe(facts, "desktop:" + hint.Pid + ":identity-unknown"); } else pins.Add(desktop);
                }
                foreach (Process candidate in Process.GetProcessesByName(SessionJobProbe.SessionShellProcess))
                {
                    try
                    {
                        if (candidate.SessionId != session) continue;
                        Pin host = Open(new SessionJobAnchor { Kind = SessionHost, Pid = candidate.Id }, ownerCreation, session);
                        if (host == null) { unknown = true; Observe(facts, "host:" + candidate.Id + ":identity-unknown"); } else pins.Add(host);
                    }
                    catch { unknown = true; }
                    finally { candidate.Dispose(); }
                }
                HashSet<int> first = readMembers(), second = readMembers();
                if (first == null || second == null) { facts.AnchorState = "members-unreadable"; return; }
                foreach (Pin pin in pins)
                {
                    int pid = pin.Identity.Pid;
                    Observe(facts, "candidate:" + pin.Identity.Kind + ":" + pid + ":" + pin.Identity.Creation
                        + ":" + pin.Identity.Session + ":read:" + (first.Contains(pid) ? "1" : "0") + (second.Contains(pid) ? "1" : "0"));
                    if (!first.Contains(pid) || !second.Contains(pid)) continue;
                    SessionJobAnchor after;
                    if (!Read(pin.Handle, pin.Identity.Kind, out after)
                        || !IdentityMatches(pin.Identity, after, ownerCreation, session, SystemPath(pin.Identity.Kind)))
                    { unknown = true; continue; }
                    facts.AnchorKind = after.Kind; facts.AnchorPid = after.Pid; facts.AnchorCreation = after.Creation;
                    facts.AnchorSession = after.Session; facts.AnchorState = "verified";
                    facts.SessionShellInJob = after.Kind == SessionHost;
                    return;
                }
                facts.AnchorState = unknown ? "anchor-identity-unknown" : pins.Count > 0 ? "anchor-not-in-job" : facts.AnchorState;
            }
            catch { facts.AnchorState = "anchor-identity-unknown"; }
            finally { foreach (Pin pin in pins) pin.Dispose(); }
        }

        // 每次启动只记有限的候选 原始提示保留 不被汇总状态覆盖
        private static void Observe(SessionJobFacts facts, string observation)
        {
            if (facts.AnchorObservations.Length + observation.Length + 1 <= 512) facts.AnchorObservations += "|" + observation;
        }
    }
}
