using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 最内层作业的限制和成员只能由所属进程读取，守护须在握手中报告这些信息。
    internal sealed class HardThreadGuardianJobFacts
    {
        // Known=false 表示作业归属未知；Members=-1 表示成员列表不可读。
        internal bool Known, InJob, AoAc, SessionShellInJob;
        internal uint LimitFlags, UiRestrictions, CpuRateFlags;
        internal int Members = -1;
        internal int EvidenceVersion = 2, AnchorKind, AnchorPid, AnchorSession = -1;
        internal long AnchorCreation;
        internal string AnchorState = "shell-window-absent";
        internal string AnchorObservations = "";
        // 失败阶段及系统错误码；成功为空，仅使用小写字母、数字、冒号和连字符。
        internal string Failure = "";

        internal string Encode()
        {
            return (string.IsNullOrEmpty(AnchorObservations) ? "v2;known=" : "v3;known=") + (Known ? "1" : "0") + ";injob=" + (InJob ? "1" : "0") + ";aoac=" + (AoAc ? "1" : "0")
                + ";flags=" + LimitFlags.ToString("x", CultureInfo.InvariantCulture) + ";ui=" + UiRestrictions.ToString("x", CultureInfo.InvariantCulture)
                + ";cpu=" + CpuRateFlags.ToString("x", CultureInfo.InvariantCulture) + ";members=" + Members.ToString(CultureInfo.InvariantCulture)
                + ";shell=" + (SessionShellInJob ? "1" : "0") + ";fail=" + (Failure ?? "")
                + ";anchor=" + AnchorKind.ToString(CultureInfo.InvariantCulture) + "," + AnchorPid.ToString(CultureInfo.InvariantCulture)
                + "," + AnchorCreation.ToString(CultureInfo.InvariantCulture) + "," + AnchorSession.ToString(CultureInfo.InvariantCulture)
                + ";anchorstate=" + (AnchorState ?? "")
                + (string.IsNullOrEmpty(AnchorObservations) ? "" : ";observations=" + AnchorObservations);
        }

        internal static HardThreadGuardianJobFacts Parse(string text)
        {
            if (text == null || text.Length > 1024) return null;
            string[] parts = text.Split(';');
            bool legacy = parts.Length == 10 && parts[0] == "v1";
            bool detailed = parts.Length == 13 && parts[0] == "v3";
            if (!legacy && !detailed && (parts.Length != 12 || parts[0] != "v2")) return null;
            string[] names = { "known", "injob", "aoac", "flags", "ui", "cpu", "members", "shell", "fail" };
            var values = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                string prefix = names[i] + "=";
                if (!parts[i + 1].StartsWith(prefix, StringComparison.Ordinal)) return null;
                values[i] = parts[i + 1].Substring(prefix.Length);
            }
            var facts = new HardThreadGuardianJobFacts();
            if (!Flag(values[0], out facts.Known) || !Flag(values[1], out facts.InJob) || !Flag(values[2], out facts.AoAc)
                || !Hex(values[3], out facts.LimitFlags) || !Hex(values[4], out facts.UiRestrictions) || !Hex(values[5], out facts.CpuRateFlags)
                || !int.TryParse(values[6], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out facts.Members) || facts.Members < -1
                || !Flag(values[7], out facts.SessionShellInJob)) return null;
            if (values[8].Length > 48) return null;
            foreach (char c in values[8])
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != ':' && c != '-') return null;
            facts.Failure = values[8];
            facts.EvidenceVersion = legacy ? 1 : detailed ? 3 : 2;
            if (detailed)
            {
                if (!parts[12].StartsWith("observations=", StringComparison.Ordinal)) return null;
                facts.AnchorObservations = parts[12].Substring(13);
                if (facts.AnchorObservations.Length == 0 || facts.AnchorObservations.Length > 512) return null;
                foreach (char c in facts.AnchorObservations)
                    if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != ':' && c != '-' && c != ',' && c != '|') return null;
            }
            if (!legacy)
            {
                if (!parts[10].StartsWith("anchor=", StringComparison.Ordinal) || !parts[11].StartsWith("anchorstate=", StringComparison.Ordinal)) return null;
                string[] anchor = parts[10].Substring(7).Split(',');
                if (anchor.Length != 4 || !int.TryParse(anchor[0], NumberStyles.None, CultureInfo.InvariantCulture, out facts.AnchorKind)
                    || !int.TryParse(anchor[1], NumberStyles.None, CultureInfo.InvariantCulture, out facts.AnchorPid)
                    || !long.TryParse(anchor[2], NumberStyles.None, CultureInfo.InvariantCulture, out facts.AnchorCreation)
                    || !int.TryParse(anchor[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out facts.AnchorSession)) return null;
                facts.AnchorState = parts[11].Substring(12);
                if (facts.AnchorState.Length == 0 || facts.AnchorState.Length > 48) return null;
                foreach (char c in facts.AnchorState)
                    if (!(c >= 'a' && c <= 'z') && c != '-') return null;
                if (facts.AnchorState == "verified")
                {
                    if ((facts.AnchorKind != HardThreadGuardianJobAnchors.SessionHost && facts.AnchorKind != HardThreadGuardianJobAnchors.DesktopShell)
                        || facts.AnchorPid <= 0 || facts.AnchorCreation <= 0 || facts.AnchorSession <= 0) return null;
                }
                else if (facts.AnchorKind != 0 || facts.AnchorPid != 0 || facts.AnchorCreation != 0 || facts.AnchorSession != -1) return null;
            }
            return facts;
        }

        private static bool Flag(string text, out bool value)
        {
            value = text == "1";
            return text == "0" || text == "1";
        }

        private static bool Hex(string text, out uint value)
        {
            value = 0;
            return text.Length >= 1 && text.Length <= 8 && uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        internal string Describe()
        {
            if (!Known) return "作业归属=读不到" + (string.IsNullOrEmpty(Failure) ? "" : " 失败步骤=" + Failure);
            if (!InJob) return "作业归属=不在作业里";
            return "现代待机=" + (AoAc ? "是" : "否") + " jobFlags=0x" + LimitFlags.ToString("X", CultureInfo.InvariantCulture)
                + " uiLimits=0x" + UiRestrictions.ToString("X", CultureInfo.InvariantCulture) + " cpuRate=0x" + CpuRateFlags.ToString("X", CultureInfo.InvariantCulture)
                + " 成员=" + (Members < 0 ? "读不到" : Members.ToString(CultureInfo.InvariantCulture)) + " 含会话外壳=" + (SessionShellInJob ? "是" : "否")
                + " 锚点=" + (AnchorKind == 2 ? "当前桌面壳" : AnchorKind == 1 ? "会话宿主" : "无")
                + " 锚点PID=" + AnchorPid + " 锚点状态=" + DescribeAnchor()
                + (string.IsNullOrEmpty(AnchorObservations) ? "" : " 锚点读取明细=" + AnchorObservations)
                + (string.IsNullOrEmpty(Failure) ? "" : " 失败步骤=" + Failure);
        }

        private string DescribeAnchor()
        {
            if (EvidenceVersion < 2) return "旧版证据不足";
            if (AnchorState == "verified") return "身份及两次成员交集已核验";
            if (AnchorState == "shell-window-absent") return "系统未提供桌面壳窗口";
            if (AnchorState == "anchor-not-in-job") return "可信会话进程不在当前作业";
            if (AnchorState == "members-unreadable") return "作业成员不可读取";
            return "会话进程身份不可核验";
        }
    }

    // 限制读取一次，成员取两次交集；每次缓冲不足时仅扩容重读一次。
    internal static class HardThreadGuardianJobProbe
    {
        private const int ExtendedLimitInformation = 9, BasicUiRestrictions = 4, CpuRateControlInformation = 15, BasicProcessIdList = 3;
        private const int MoreData = 234;
        // 名称只用于寻找候选，准入另行核验固定句柄、系统路径、创建时间和成员交集。
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

        // SYSTEM_POWER_CAPABILITIES 共 76 字节，AoAc 在第 20 字节。
        private const int PowerCapabilitiesBytes = 76, AoAcOffset = 20;

        internal static bool AoAcSupported(out string failure)
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

        internal static HardThreadGuardianJobFacts ReadOwn()
        {
            try
            {
                long creation = HardThreadGuardianJobAnchors.CurrentCreation();
                return ReadOwn(creation, HardThreadGuardianJobAnchors.CaptureDesktop(creation));
            }
            catch { return new HardThreadGuardianJobFacts { Failure = "owner-identity" }; }
        }

        internal static HardThreadGuardianJobFacts ReadOwn(long ownerCreation, HardThreadGuardianJobAnchor hint)
        {
            var facts = new HardThreadGuardianJobFacts();
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
                HardThreadGuardianJobAnchors.ReadEvidence(facts, ownerCreation, hint, delegate { return ReadMembers(facts); });
            }
            catch (Exception error)
            {
                facts.Failure = "exception-" + error.GetType().Name.ToLowerInvariant();
                if (facts.Failure.Length > 48) facts.Failure = facts.Failure.Substring(0, 48);
            }
            return facts;
        }

        private static string Join(string first, string second)
        {
            if (string.IsNullOrEmpty(first)) return second ?? "";
            // 保留首次失败，避免后续错误覆盖根因。
            return first;
        }

        private static HashSet<int> ReadMembers(HardThreadGuardianJobFacts facts)
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
}
