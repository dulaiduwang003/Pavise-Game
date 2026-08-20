// @author bdth 2074055628@qq.com
// 文件用途 枚举这台机器上所有能改中断亲和的设备 以及它们当前的亲和策略
//
// 为什么不走驱动名映射
//   驱动名到设备那条路只对即插即用设备驱动成立
//   ndis.sys tcpip.sys dxgkrnl.sys ntoskrnl.exe 这些框架层没有自己的设备
//   实测一台机器上超过一半的 DPC 时长落在它们头上 按驱动映射就永远够不着
//   反过来枚举设备就全通了 DPC 统计从 能不能动手的判据 降级成 该动哪个的参考
//
// 判据是设备自己有没有 Interrupt Management 子键
//   有这个键就说明它参与中断资源分配 才谈得上改亲和
//   没有的挂在总线下面 或者根本不产生中断 列出来只会让人误点
//
// 注意 这里读到的策略是注册表里写着的 不是运行时真正生效的
//   Affinity Policy 是设备启动分配中断资源时读的 改完要重启设备或重启系统
//   所以界面上必须写清 已写入 与 已生效 是两回事

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
        // 注册表里写着的策略 0 或缺失表示系统默认
        public int Policy;
        public ulong Mask;
        // 这台设备的驱动在本次扫描里的中断表现 没有对应数据时为空
        public double MaxUs;
        public double TotalUs;
        public long Dpc;
        // 单次最长只说明最坏那一次 超时次数才说明它是偶发还是一直在拖
        // 之前 Attach 把这两个字段丢了 用户只看得到一个孤零零的最大值 没法判断
        public long Over500Us;
        public long Over1Ms;
        public ulong SeenOnCpus;
        // 被别的开关占着 中断页就不能再动它 两套备份压同一个值 还原会变成随机数
        public bool ManagedElsewhere;
        // 键鼠的中断多半不是自己产生的 是挂在 USB 主控下面由主控统一上报
        //   所以真正会被钉的是主控 一钉就是这条总线上所有输入设备一起走
        //   移到跑得慢的核上 DPC 会变长 输入延迟跟着变 得先告诉用户再让他决定
        public bool InputRisk;

        // 注册表里已经指定了落点 掩码就写在这台设备自己那儿
        //   目标核是每台设备各自的 所以判定不需要外面传一个全局目标进来
        public bool IsPinned { get { return Policy == 4 && Mask != 0; } }

        // 中断真的落在它自己那组核里了 这才叫生效
        //   写入和生效必须分开判 设备是启动分配中断资源时读策略的 不重启就还是老落点
        //   没观测到中断的设备返回 false 不能拿没有证据当成功
        public bool Effective
        {
            get { return IsPinned && SeenOnCpus != 0 && (SeenOnCpus & ~Mask) == 0; }
        }
        // 同一个驱动挂着多个设备时 中断数据是这个驱动的合计 不是这一台的
        public bool SharedStats;
    }

    internal static class IrqDeviceInventory
    {
        private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\";
        private const string AffSuffix = @"\Device Parameters\Interrupt Management\Affinity Policy";
        private const string IntrSuffix = @"\Device Parameters\Interrupt Management";

        private static readonly string[] Buses = { "PCI", "USB", "HDAUDIO", "ACPI" };
        // 一个总线下面几千个实例时不值得全扫 真正带中断的设备远没那么多
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
            // 只留现在真的在机器上并且已启动的设备
            // 拔掉的卡在注册表里会一直留着 列出来会让人对着一块不存在的显卡按按钮
            // Control 是易失键 只有设备启动后才存在 里面的 AllocConfig 是已分配的硬件资源
            // 一开始拿 ActiveService 判是错的 那个值根本不在这个键里 实测查出来是零个设备
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

        // 输入相关设备类 键鼠 HID 以及它们挂着的 USB 主控
        //   HIDClass 4d36e96b 键盘 4d36e96f 鼠标 745a17a0 HID
        //   主控按服务名认 各家 xHCI 驱动名字不同 但都带 xhc ehci ohci uhci
        //   i8042prt 是 PS/2 键鼠 老机器上还在用
        private static readonly string[] InputClassGuids =
        {
            "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}",
            "{4d36e96b-e325-11ce-bfc1-08002be10318}",
            "{4d36e96f-e325-11ce-bfc1-08002be10318}",
        };

        private static readonly string[] InputServiceHints =
        { "xhc", "ehci", "ohci", "uhci", "usbhub", "hidusb", "kbdhid", "mouhid", "i8042prt" };

        internal static bool LooksLikeInput(IrqDevice d)
        {
            if (d == null) return false;
            foreach (string g in InputClassGuids)
                if (string.Equals(d.ClassGuid, g, StringComparison.OrdinalIgnoreCase)) return true;
            string svc = (d.Service ?? "").ToLowerInvariant();
            if (svc.Length > 0)
                foreach (string h in InputServiceHints)
                    if (svc.IndexOf(h, StringComparison.Ordinal) >= 0) return true;
            // USB 总线上的设备一律算 那条总线上插着什么我们看不全
            return string.Equals(d.Bus, "USB", StringComparison.OrdinalIgnoreCase);
        }

        // 把一次扫描里的驱动数据贴到设备上 贴不上的设备就是没产生可观测中断
        // 排序放在贴完数据之后 因为要按中断表现排
        //   本机实测 17 台在场设备里只有 5 台产生了可观测中断 其余是桥和空设备
        //   全列出来是为了够得着 排序保证该动的那台在最上面
        public static void MarkOwnership(List<IrqDevice> devices)
        {
            if (devices == null) return;
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

        public static void Attach(List<IrqDevice> devices, IrqScanResult scan)
        {
            if (devices == null || scan == null || !scan.Ok) return;
            // 一个驱动挂几台设备时 那份中断统计是驱动的合计 要标出来 不能让人以为是单台的
            var perService = new Dictionary<string, int>();
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
        }

        // 单次 DPC 多长才算长 这几条线不是我拍的
        //   100us  微软给驱动作者的 DPC 时长建议上限 正常驱动应该在这条线以内
        //   500us  一次就吃掉 144fps 一帧预算的 7% 音视频类工具普遍拿它当丢帧门槛
        //   1000us 一次就可能把一帧顶过 60fps 的预算
        // 分档只回答 这台设备的中断长不长 不回答 挪了能不能提升帧率
        // 后者这个项目没有实测证据 界面上必须照实说 不能拿分档暗示收益
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

        // 把微秒换成用户看得懂的东西 一帧预算的百分之几
        // 60 和 144 是固定参照点 不去猜用户的刷新率 猜错了比不给更糟
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
