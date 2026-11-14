using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal sealed class HardThreadGuardianJobAnchor
    {
        internal int Kind, Pid, Session = -1;
        internal long Creation;
        internal string Path = "", State = "shell-window-absent";

        internal string EncodeHint()
        {
            return "job-anchor:v1:" + Pid.ToString(CultureInfo.InvariantCulture) + ":" + Creation.ToString(CultureInfo.InvariantCulture) + ":" + Session.ToString(CultureInfo.InvariantCulture) + ":" + State;
        }

        internal static HardThreadGuardianJobAnchor ParseHint(string text)
        {
            string[] fields = (text ?? "").Split(':');
            int pid, session; long creation;
            if (text == null || text.Length > 160 || fields.Length != 6 || fields[0] != "job-anchor" || fields[1] != "v1"
                || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out pid)
                || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out creation)
                || !int.TryParse(fields[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out session))
                throw new InvalidDataException("守护会话锚点提示格式无效");
            bool verified = pid > 0 && creation > 0 && session > 0 && fields[5] == "verified";
            bool absent = pid == 0 && creation == 0 && session == -1
                && (fields[5] == "shell-window-absent" || fields[5] == "shell-identity-unknown");
            if (!verified && !absent) throw new InvalidDataException("守护会话锚点提示身份无效");
            // 提示只携带候选身份，接收方仍须独立读取固定进程句柄和作业成员。
            return new HardThreadGuardianJobAnchor { Kind = verified ? HardThreadGuardianJobAnchors.DesktopShell : 0,
                Pid = pid, Creation = creation, Session = session, State = fields[5] };
        }
    }

    internal static class HardThreadGuardianJobAnchors
    {
        internal const int SessionHost = 1, DesktopShell = 2;
        private const uint QueryAndSynchronize = 0x101000;
        private sealed class Pin : IDisposable
        {
            internal IntPtr Handle;
            internal HardThreadGuardianJobAnchor Identity;
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

        internal static int CurrentSession()
        {
            uint session;
            using (Process self = Process.GetCurrentProcess())
                if (!ProcessIdToSessionId((uint)self.Id, out session) || session > int.MaxValue)
                    throw new IOException("守护会话编号不可核验");
            return (int)session;
        }

        internal static long CurrentCreation()
        {
            long creation, exit, kernel, user;
            if (!GetProcessTimes(new IntPtr(-1), out creation, out exit, out kernel, out user) || creation <= 0)
                throw new IOException("守护调用方创建时间不可核验");
            return creation;
        }

        private static bool Read(IntPtr handle, int kind, out HardThreadGuardianJobAnchor identity)
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
            identity = new HardThreadGuardianJobAnchor { Kind = kind, Pid = (int)pid, Creation = creation, Session = (int)session, Path = path.ToString(), State = "verified" };
            return true;
        }

        internal static bool IdentityMatches(HardThreadGuardianJobAnchor expected, HardThreadGuardianJobAnchor actual,
            long ownerCreation, int session, string systemPath)
        {
            return expected != null && actual != null && (expected.Kind == DesktopShell || expected.Kind == SessionHost)
                && expected.Kind == actual.Kind && expected.Pid > 0 && expected.Pid == actual.Pid
                && expected.Creation > 0 && expected.Creation == actual.Creation && actual.Creation < ownerCreation
                && expected.Session == session && actual.Session == session && session > 0
                && !string.IsNullOrEmpty(systemPath) && string.Equals(actual.Path, systemPath, StringComparison.OrdinalIgnoreCase);
        }

        private static Pin Open(HardThreadGuardianJobAnchor expected, long ownerCreation, int session)
        {
            if (expected == null || expected.Pid <= 0) return null;
            IntPtr handle = OpenProcess(QueryAndSynchronize, false, expected.Pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                HardThreadGuardianJobAnchor actual;
                if (!Read(handle, expected.Kind, out actual)) return null;
                if (expected.Creation == 0) expected = new HardThreadGuardianJobAnchor { Kind = expected.Kind, Pid = expected.Pid, Creation = actual.Creation, Session = session };
                if (!IdentityMatches(expected, actual, ownerCreation, session, SystemPath(expected.Kind))) return null;
                var pin = new Pin { Handle = handle, Identity = actual };
                handle = IntPtr.Zero;
                return pin;
            }
            finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
        }

        internal static HardThreadGuardianJobAnchor CaptureDesktop(long ownerCreation)
        {
            var unavailable = new HardThreadGuardianJobAnchor();
            try
            {
                IntPtr window = GetShellWindow(); uint pid;
                if (window == IntPtr.Zero) return unavailable;
                unavailable.State = "shell-identity-unknown";
                if (GetWindowThreadProcessId(window, out pid) == 0 || pid == 0 || pid > int.MaxValue) return unavailable;
                using (Pin pin = Open(new HardThreadGuardianJobAnchor { Kind = DesktopShell, Pid = (int)pid }, ownerCreation, CurrentSession()))
                {
                    if (pin == null || GetShellWindow() != window) return unavailable;
                    uint after;
                    if (GetWindowThreadProcessId(window, out after) == 0 || after != pid) return unavailable;
                    return pin.Identity;
                }
            }
            catch { unavailable.State = "shell-identity-unknown"; return unavailable; }
        }

        internal static bool MemberIntersection(int pid, HashSet<int> first, HashSet<int> second)
        { return pid > 0 && first != null && second != null && first.Contains(pid) && second.Contains(pid); }

        internal static void ReadEvidence(HardThreadGuardianJobFacts facts, long ownerCreation, HardThreadGuardianJobAnchor hint,
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
                foreach (Process candidate in Process.GetProcessesByName(HardThreadGuardianJobProbe.SessionShellProcess))
                {
                    try
                    {
                        if (candidate.SessionId != session) continue;
                        Pin host = Open(new HardThreadGuardianJobAnchor { Kind = SessionHost, Pid = candidate.Id }, ownerCreation, session);
                        if (host == null) { unknown = true; Observe(facts, "host:" + candidate.Id + ":identity-unknown"); } else pins.Add(host);
                    }
                    catch { unknown = true; }
                    finally { candidate.Dispose(); }
                }
                HashSet<int> first = readMembers(), second = readMembers();
                if (first == null || second == null) { facts.AnchorState = "members-unreadable"; return; }
                foreach (Pin pin in pins)
                {
                    Observe(facts, "candidate:" + pin.Identity.Kind + ":" + pin.Identity.Pid + ":" + pin.Identity.Creation
                        + ":" + pin.Identity.Session + ":read:" + (first.Contains(pin.Identity.Pid) ? "1" : "0") + (second.Contains(pin.Identity.Pid) ? "1" : "0"));
                    if (!MemberIntersection(pin.Identity.Pid, first, second)) continue;
                    HardThreadGuardianJobAnchor after;
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

        private static void Observe(HardThreadGuardianJobFacts facts, string observation)
        {
            // 只在一次启动回执内记录有限候选，保留原始提示而不由聚合状态覆盖。
            if (facts.AnchorObservations.Length + observation.Length + 1 <= 512) facts.AnchorObservations += "|" + observation;
        }

        internal static string VerifyReported(HardThreadGuardianJobFacts facts, long ownerCreation)
        {
            try
            {
                if (facts == null || facts.EvidenceVersion < 2 || facts.AnchorState != "verified") return "缺少已核验的会话锚点";
                var expected = new HardThreadGuardianJobAnchor { Kind = facts.AnchorKind, Pid = facts.AnchorPid, Creation = facts.AnchorCreation, Session = facts.AnchorSession };
                if (expected.Kind == DesktopShell)
                {
                    HardThreadGuardianJobAnchor shell = CaptureDesktop(ownerCreation);
                    if (!IdentityMatches(expected, shell, ownerCreation, CurrentSession(), SystemPath(DesktopShell))) return "当前桌面外壳身份已变化或不可核验";
                }
                using (Pin pin = Open(expected, ownerCreation, CurrentSession()))
                    return pin == null ? "会话锚点身份、路径或创建时间不可核验" : null;
            }
            catch { return "会话锚点复核失败"; }
        }
    }
}
