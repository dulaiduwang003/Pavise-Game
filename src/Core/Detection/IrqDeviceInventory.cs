// @author bdth 2074055628@qq.com
// 文件用途 枚举这台机器上所有能改中断亲和的设备 以及它们当前的亲和策略

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal enum IrqGrade { None, Fine, Long, Heavy, Severe }

    internal sealed class IrqDevice
    {
        public string InstanceId = "";
        public string Name = "";
        public string Service = "";
        public string Bus = "";
        public string ClassGuid = "";
        public int Policy;
        public ulong Mask;
        public bool DevicePriorityHigh;
        public double MaxUs;
        public double TotalUs;
        public long Dpc;
        public long Over500Us;
        public long Over1Ms;
        public ulong SeenOnCpus;
        public bool FrameworkStats;
        public string StatsDriver = "";

        public bool FromCheckup;
        public bool ManagedElsewhere;
        public bool InputRisk;

        public int MessageCount;
        public bool MultiMessageRisk;

        public bool CompletionFollowsIssuer;

        public bool IsPinned { get { return Policy == 4 && Mask != 0; } }

        public bool Effective
        {
            get { return IsPinned && SeenOnCpus != 0 && (SeenOnCpus & ~Mask) == 0; }
        }

        public bool RebootedSincePin;

        public bool AwaitingReboot { get { return IsPinned && !Effective && !RebootedSincePin; } }

        public bool PlacementMismatch
        {
            get
            {
                if (!IsPinned || RebootedSincePin == false) return false;
                if (SeenOnCpus == 0) return false;
                if (Verdict != null && Verdict.MaskTruncated) return false;
                return (SeenOnCpus & ~Mask) != 0;
            }
        }

        public bool Unverified
        {
            get { return IsPinned && !Effective && RebootedSincePin && !PlacementMismatch; }
        }
        public bool SharedStats;

        public IrqDriverVerdict Verdict;
        public bool Worth { get { return Verdict != null && Verdict.Worth; } }
    }

    internal static class IrqDeviceInventory
    {
        private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\";
        private const string AffSuffix = @"\Device Parameters\Interrupt Management\Affinity Policy";
        private const string IntrSuffix = @"\Device Parameters\Interrupt Management";

        private static readonly string[] Buses = { "PCI", "USB", "HDAUDIO", "ACPI" };
        private const int MaxPerBus = 512;

        public static List<IrqDevice> Enumerate()
        {
            var list = new List<IrqDevice>();
            foreach (string bus in Buses)
            {
                try { Walk(bus, list); }
                catch { }
            }
            return list;
        }

        private static void Walk(string bus, List<IrqDevice> list)
        {
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(EnumRoot + bus))
            {
                if (root == null) return;
                int seen = 0;
                foreach (string hw in root.GetSubKeyNames())
                {
                    using (RegistryKey hwKey = root.OpenSubKey(hw))
                    {
                        if (hwKey == null) continue;
                        foreach (string inst in hwKey.GetSubKeyNames())
                        {
                            if (++seen > MaxPerBus) return;
                            string id = bus + "\\" + hw + "\\" + inst;
                            IrqDevice d = Read(id, bus);
                            if (d != null) list.Add(d);
                        }
                    }
                }
            }
        }

        private static IrqDevice Read(string instanceId, string bus)
        {
            using (RegistryKey im = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId + IntrSuffix))
            {
                if (im == null) return null;
            }
            using (RegistryKey ctl = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId + @"\Control"))
            {
                if (ctl == null) return null;
            }
            var d = new IrqDevice();
            d.InstanceId = instanceId;
            d.Bus = bus;
            d.ClassGuid = ReadStr(instanceId, "ClassGUID");
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId))
                {
                    if (k != null)
                    {
                        d.Name = (k.GetValue("FriendlyName") as string)
                            ?? (k.GetValue("DeviceDesc") as string) ?? "";
                        int semi = d.Name.LastIndexOf(';');
                        if (semi >= 0 && semi + 1 < d.Name.Length) d.Name = d.Name.Substring(semi + 1);
                        d.Service = (k.GetValue("Service") as string) ?? "";
                    }
                }
            }
            catch { }
            if (d.Name.Length == 0) d.Name = DriverDeviceResolver.ShortId(instanceId);
            d.InputRisk = LooksLikeInput(d);
            d.MessageCount = ReadMessageCount(instanceId);
            d.MultiMessageRisk = LooksMultiQueue(d);
            d.CompletionFollowsIssuer = LooksStorage(d);

            try
            {
                using (RegistryKey a = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId + AffSuffix))
                {
                    if (a != null)
                    {
                        object p = a.GetValue("DevicePolicy");
                        if (p != null) try { d.Policy = Convert.ToInt32(p); } catch { }
                        var raw = a.GetValue("AssignmentSetOverride") as byte[];
                        if (raw != null) d.Mask = IrqAffinityEngine.BytesToMask(raw);
                        object pri = a.GetValue("DevicePriority");
                        if (pri != null)
                            try { d.DevicePriorityHigh = Convert.ToInt32(pri) >= 3; } catch { }
                    }
                }
            }
            catch { }
            return d;
        }

        private static string ReadStr(string instanceId, string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId))
                    return k == null ? "" : ((k.GetValue(name) as string) ?? "");
            }
            catch { return ""; }
        }

        private static readonly string[] InputClassGuids =
        {
            "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}",
            "{4d36e96b-e325-11ce-bfc1-08002be10318}",
            "{4d36e96f-e325-11ce-bfc1-08002be10318}",
        };

        private static readonly string[] InputServiceHints =
        { "xhc", "ehci", "ohci", "uhci", "usbhub", "hidusb", "kbdhid", "mouhid", "i8042prt" };

        private const string MsiSuffix = IntrSuffix + @"\MessageSignaledInterruptProperties";

        private static int ReadMessageCount(string instanceId)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(EnumRoot + instanceId + MsiSuffix))
                {
                    if (k == null) return 0;
                    object limit = k.GetValue("MessageNumberLimit");
                    if (limit == null) return 0;
                    return Convert.ToInt32(limit);
                }
            }
            catch { return 0; }
        }

        internal static bool LooksMultiQueue(IrqDevice d)
        {
            if (d == null) return false;
            if (d.MessageCount > 1) return true;
            return string.Equals(d.ClassGuid, NetClassGuid, StringComparison.OrdinalIgnoreCase)
                && d.MessageCount == 0;
        }

        private static readonly string[] StorageServiceHints =
        { "storport", "stornvme", "storahci", "storufs", "iastor", "nvme", "sata", "raid" };

        internal static bool LooksStorage(IrqDevice d)
        {
            if (d == null) return false;
            if (string.Equals(d.ClassGuid, ScsiClassGuid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.ClassGuid, HdcClassGuid, StringComparison.OrdinalIgnoreCase)) return true;
            string svc = (d.Service ?? "").ToLowerInvariant();
            if (svc.Length == 0) return false;
            foreach (string h in StorageServiceHints)
                if (svc.IndexOf(h, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        internal static bool LooksLikeInput(IrqDevice d)
        {
            if (d == null) return false;
            foreach (string g in InputClassGuids)
                if (string.Equals(d.ClassGuid, g, StringComparison.OrdinalIgnoreCase)) return true;
            string svc = (d.Service ?? "").ToLowerInvariant();
            if (svc.Length > 0)
                foreach (string h in InputServiceHints)
                    if (svc.IndexOf(h, StringComparison.Ordinal) >= 0) return true;
            return string.Equals(d.Bus, "USB", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class FrameworkOwner
        {
            public readonly string Driver;
            public readonly string[] ClassGuids;
            public FrameworkOwner(string driver, string[] guids) { Driver = driver; ClassGuids = guids; }
        }

        private const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";
        private const string ScsiClassGuid = "{4d36e97b-e325-11ce-bfc1-08002be10318}";
        private const string HdcClassGuid = "{4d36e96a-e325-11ce-bfc1-08002be10318}";

        private static readonly FrameworkOwner[] FrameworkOwners =
        {
            new FrameworkOwner("ndis", new[] { NetClassGuid }),
            new FrameworkOwner("storport", new[] { ScsiClassGuid, HdcClassGuid }),
        };

        private static FrameworkOwner OwnerForDriver(string driverImageName)
        {
            string svc = driverImageName ?? "";
            int dot = svc.LastIndexOf('.');
            if (dot > 0) svc = svc.Substring(0, dot);
            foreach (FrameworkOwner o in FrameworkOwners)
                if (string.Equals(o.Driver, svc, StringComparison.OrdinalIgnoreCase)) return o;
            return null;
        }

        private static bool OwnsDevice(FrameworkOwner o, IrqDevice d)
        {
            if (o == null || d == null) return false;
            foreach (string g in o.ClassGuids)
                if (string.Equals(d.ClassGuid, g, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int ClassMemberCount(List<IrqDevice> devices, FrameworkOwner o)
        {
            int n = 0;
            foreach (IrqDevice d in devices) if (OwnsDevice(o, d)) n++;
            return n;
        }

        internal static void AttachCheckup(List<IrqDevice> devices, IrqCheckupResult ck)
        {
            if (devices == null || ck == null || !ck.Ok) return;
            foreach (IrqDevice d in devices)
            {
                if (d.Dpc > 0) continue;
                foreach (IrqCheckupDevice c in ck.Devices)
                {
                    if (!string.Equals(c.InstanceId, d.InstanceId, StringComparison.OrdinalIgnoreCase)) continue;
                    d.MaxUs = c.P99Us;
                    d.Dpc = c.Dpc;
                    d.SeenOnCpus = c.CpuMask;
                    d.FromCheckup = true;
                    if (c.StatsDriver != null && !string.Equals(c.StatsDriver, d.Service,
                            StringComparison.OrdinalIgnoreCase))
                    { d.FrameworkStats = true; d.StatsDriver = c.StatsDriver; }
                    break;
                }
            }
        }

        public static void AttachVerdicts(List<IrqDevice> devices, List<IrqDriverVerdict> verdicts)
        {
            if (devices == null) return;
            var perService = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (IrqDevice d in devices)
            {
                if (d.Service.Length == 0) continue;
                int n; perService.TryGetValue(d.Service, out n);
                perService[d.Service] = n + 1;
            }
            if (verdicts == null) return;
            foreach (IrqDevice d in devices)
            {
                if (d.Service.Length == 0) continue;
                foreach (IrqDriverVerdict v in verdicts)
                {
                    string svc = v.Driver ?? "";
                    int dot = svc.LastIndexOf('.');
                    if (dot > 0) svc = svc.Substring(0, dot);
                    if (!string.Equals(svc, d.Service, StringComparison.OrdinalIgnoreCase)) continue;
                    d.Verdict = v;
                    d.MaxUs = v.WorstMaxUs;
                    d.Over500Us = v.TotalOver500;
                    d.Dpc = (long)v.DpcPerMinute;
                    d.SeenOnCpus = v.CpuMask;
                    int n; perService.TryGetValue(d.Service, out n);
                    d.SharedStats = n > 1;
                    break;
                }
            }
            AttachFrameworkVerdicts(devices, verdicts);
        }

        private static void AttachFrameworkVerdicts(List<IrqDevice> devices, List<IrqDriverVerdict> verdicts)
        {
            foreach (IrqDriverVerdict v in verdicts)
            {
                FrameworkOwner o = OwnerForDriver(v.Driver);
                if (o == null) continue;
                int members = ClassMemberCount(devices, o);
                if (members == 0) continue;
                foreach (IrqDevice d in devices)
                {
                    if (d.Dpc > 0 || d.Verdict != null || !OwnsDevice(o, d)) continue;
                    d.Verdict = v;
                    d.MaxUs = v.WorstMaxUs;
                    d.Over500Us = v.TotalOver500;
                    d.Dpc = (long)v.DpcPerMinute;
                    d.SeenOnCpus = v.CpuMask;
                    d.FrameworkStats = true; d.StatsDriver = v.Driver;
                    d.SharedStats = members > 1;
                }
            }
        }

        internal static void AttachScan(List<IrqDevice> devices, IrqScanResult scan)
        {
            if (devices == null || scan == null) return;
            var perService = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (IrqDevice d in devices)
            {
                if (d.Service.Length == 0) continue;
                int n; perService.TryGetValue(d.Service, out n);
                perService[d.Service] = n + 1;
            }
            foreach (IrqDevice d in devices)
            {
                if (d.Service.Length == 0) continue;
                foreach (IrqCandidate c in scan.Candidates)
                {
                    string svc = c.Driver;
                    int dot = svc.LastIndexOf('.');
                    if (dot > 0) svc = svc.Substring(0, dot);
                    if (!string.Equals(svc, d.Service, StringComparison.OrdinalIgnoreCase)) continue;
                    d.MaxUs = c.MaxUs; d.TotalUs = c.TotalUs; d.Dpc = c.Dpc; d.SeenOnCpus = c.CpuMask;
                    d.Over500Us = c.Over500Us; d.Over1Ms = c.Over1Ms;
                    int n; perService.TryGetValue(d.Service, out n);
                    d.SharedStats = n > 1;
                    break;
                }
            }
            AttachFrameworkScan(devices, scan);
        }

        private static void AttachFrameworkScan(List<IrqDevice> devices, IrqScanResult scan)
        {
            foreach (IrqCandidate c in scan.Candidates)
            {
                FrameworkOwner o = OwnerForDriver(c.Driver);
                if (o == null) continue;
                int members = ClassMemberCount(devices, o);
                if (members == 0) continue;
                foreach (IrqDevice d in devices)
                {
                    if (d.Dpc > 0 || !OwnsDevice(o, d)) continue;
                    d.MaxUs = c.MaxUs; d.TotalUs = c.TotalUs; d.Dpc = c.Dpc; d.SeenOnCpus = c.CpuMask;
                    d.Over500Us = c.Over500Us; d.Over1Ms = c.Over1Ms;
                    d.FrameworkStats = true; d.StatsDriver = c.Driver;
                    d.SharedStats = members > 1;
                }
            }
        }

        public static void MarkOwnership(List<IrqDevice> devices)
        {
            if (devices == null) return;
            foreach (IrqDevice d in devices)
            {
                if (!d.IsPinned) { d.RebootedSincePin = true; continue; }
                try { d.RebootedSincePin = IrqRelocate.RebootedSinceWrite(d.InstanceId); }
                catch { d.RebootedSincePin = true; }
            }

            List<string> owned;
            try { owned = IrqRelocate.OwnedElsewhere(); }
            catch { return; }
            foreach (IrqDevice d in devices)
                foreach (string g in owned)
                    if (string.Equals(g, d.InstanceId, StringComparison.OrdinalIgnoreCase))
                    { d.ManagedElsewhere = true; break; }
        }

        public static void Sort(List<IrqDevice> devices)
        {
            if (devices == null) return;
            devices.Sort(delegate (IrqDevice a, IrqDevice b)
            {
                if (a.MaxUs != b.MaxUs) return b.MaxUs.CompareTo(a.MaxUs);
                if (a.Dpc != b.Dpc) return b.Dpc.CompareTo(a.Dpc);
                int c = string.Compare(a.Bus, b.Bus, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }

        internal const double GradeLongUs = 100.0;
        internal const double GradeHeavyUs = 500.0;
        internal const double GradeSevereUs = 1000.0;

        internal static IrqGrade Grade(IrqDevice d)
        {
            if (d == null || d.Dpc <= 0) return IrqGrade.None;
            if (d.MaxUs >= GradeSevereUs) return IrqGrade.Severe;
            if (d.MaxUs >= GradeHeavyUs) return IrqGrade.Heavy;
            if (d.MaxUs >= GradeLongUs) return IrqGrade.Long;
            return IrqGrade.Fine;
        }

        internal static string GradeText(IrqGrade g)
        {
            switch (g)
            {
                case IrqGrade.Fine: return Lang.T("irq.grade.fine");
                case IrqGrade.Long: return Lang.T("irq.grade.long");
                case IrqGrade.Heavy: return Lang.T("irq.grade.heavy");
                case IrqGrade.Severe: return Lang.T("irq.grade.severe");
                default: return "";
            }
        }

        internal static string BudgetText(double maxUs)
        {
            if (maxUs <= 0) return "";
            return Lang.F("irq.budget", (maxUs / 16667.0 * 100.0).ToString("F1"),
                (maxUs / 6944.0 * 100.0).ToString("F1"));
        }

        internal static string PolicyText(IrqDevice d)
        {
            if (d == null) return "";
            switch (d.Policy)
            {
                case 0: return Lang.T("irqdev.policy.default");
                case 1: return Lang.T("irqdev.policy.allclose");
                case 2: return Lang.T("irqdev.policy.one");
                case 3: return Lang.T("irqdev.policy.allnuma");
                case 4: return Lang.F("irqdev.policy.pinned", IrqRelocate.MaskText(d.Mask));
                case 5: return Lang.T("irqdev.policy.spread");
                default: return Lang.F("irqdev.policy.other", d.Policy);
            }
        }
    }
}
