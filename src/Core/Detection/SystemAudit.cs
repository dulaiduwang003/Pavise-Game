// @author bdth 2074055628@qq.com
// 文件用途 系统体检 聚合本机能力 实测数据与持久设置 输出带依据等级的结论清单

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class AuditRow
    {
        public string Name;
        public string Value;
        public string Note;
        public string Evidence;
        public bool Warn;
    }

    internal sealed class AuditReport
    {
        public readonly List<AuditRow> Capability = new List<AuditRow>();
        public readonly List<AuditRow> Machine = new List<AuditRow>();
        public readonly List<AuditRow> Persistent = new List<AuditRow>();
        public readonly List<AuditRow> Verdicts = new List<AuditRow>();
        public bool MeasureOk;
        public int MeasureWindowMs;
    }

    internal static class SystemAudit
    {
        public const string EvMeasuredLocal = "本机实测";
        public const string EvMeasuredBench = "台架实测";
        public const string EvMechanism = "机制明确";
        public const string EvUnverified = "未验证";

        public static int InterruptTier(double worstRate)
        {
            if (worstRate < 0.01) return 0;
            if (worstRate < 0.05) return 1;
            return 2;
        }

        public static string InterruptTierText(int tier)
        {
            if (tier == 0) return "干净";
            return tier == 1 ? "正常" : "异常";
        }

        public static string PercentText(double rate)
        {
            return (rate * 100.0).ToString("F2") + "%";
        }

        private static string CoreLabel(ulong mask)
        {
            var parts = new List<string>();
            for (int i = 0; i < 64; i++) if (((mask >> i) & 1UL) != 0) parts.Add(i.ToString());
            return string.Join(" ", parts.ToArray());
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        public const byte EnergySaverOn = 1;

        public static bool TryEnergySaver(out bool on)
        {
            on = false;
            try
            {
                SystemPowerStatus status;
                if (!GetSystemPowerStatus(out status)) return false;
                if (status.SystemStatusFlag > 1) return false;
                on = status.SystemStatusFlag == EnergySaverOn;
                return true;
            }
            catch { return false; }
        }

        public static bool TryMemory(out double usedRatio, out double totalGb, out double availGb)
        {
            usedRatio = 0; totalGb = 0; availGb = 0;
            try
            {
                var status = new MemoryStatusEx();
                status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return false;
                totalGb = status.TotalPhys / 1073741824.0;
                availGb = status.AvailPhys / 1073741824.0;
                usedRatio = 1.0 - (status.AvailPhys / (double)status.TotalPhys);
                return true;
            }
            catch { return false; }
        }

        public static bool RefreshRateIsBest(int current, int best)
        {
            return current <= 0 || best <= 0 || current >= best;
        }

        private static string WindowsText()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key == null) return Environment.OSVersion.VersionString;
                    string name = key.GetValue("ProductName") as string;
                    string display = key.GetValue("DisplayVersion") as string;
                    object build = key.GetValue("CurrentBuildNumber");
                    object ubr = key.GetValue("UBR");
                    string text = string.IsNullOrEmpty(name) ? "Windows" : name;
                    int buildNumber;
                    if (build != null && int.TryParse(build.ToString(), out buildNumber) && buildNumber >= 22000)
                        text = text.Replace("Windows 10", "Windows 11");
                    if (!string.IsNullOrEmpty(display)) text += " " + display;
                    if (build != null) text += " " + build + (ubr != null ? "." + ubr : "");
                    return text;
                }
            }
            catch { return Environment.OSVersion.VersionString; }
        }

        public const int EcoQosFullBuild = 22000;

        public static int WindowsBuild()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        object build = key.GetValue("CurrentBuildNumber");
                        int parsed;
                        if (build != null && int.TryParse(build.ToString(), out parsed)) return parsed;
                    }
                }
            }
            catch { }
            try { return Environment.OSVersion.Version.Build; }
            catch { return 0; }
        }

        private static bool GameDvrOn()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore"))
                {
                    if (key == null) return false;
                    object value = key.GetValue("GameDVR_Enabled");
                    return value == null || Convert.ToInt32(value) != 0;
                }
            }
            catch { return false; }
        }

        public sealed class LoadEntry
        {
            public string Name;
            public double Ratio;
        }

        private sealed class Sample
        {
            public string Name;
            public long Started;
            public TimeSpan Cpu;
        }

        public static List<LoadEntry> TopConsumers(int windowMs, int take)
        {
            var result = new List<LoadEntry>();
            try
            {
                var before = new Dictionary<int, Sample>();
                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            if (p.Id <= 4) continue;
                            before[p.Id] = new Sample
                            {
                                Name = p.ProcessName, Started = p.StartTime.Ticks, Cpu = p.TotalProcessorTime
                            };
                        }
                        catch { }
                    }
                }
                if (before.Count == 0) return result;
                System.Threading.Thread.Sleep(windowMs);
                double span = windowMs / 1000.0 * Environment.ProcessorCount;
                if (span <= 0) return result;
                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            Sample old;
                            if (!before.TryGetValue(p.Id, out old)) continue;
                            if (p.StartTime.Ticks != old.Started) continue;
                            double delta = (p.TotalProcessorTime - old.Cpu).TotalSeconds;
                            if (delta <= 0) continue;
                            result.Add(new LoadEntry { Name = old.Name, Ratio = delta / span });
                        }
                        catch { }
                    }
                }
            }
            catch { return result; }
            result.Sort(delegate(LoadEntry a, LoadEntry b) { return b.Ratio.CompareTo(a.Ratio); });
            if (result.Count > take) result.RemoveRange(take, result.Count - take);
            return result;
        }

        private sealed class Facts
        {
            public bool Nv;
            public GpuAdapter[] Gpus = new GpuAdapter[0];
            public bool NvHardware;
            public bool AmdHardware;
            public bool IntegratedOnly;
            public bool Partition;
            public bool Eco;
            public bool EcoFull;
            public bool Hags;
            public VbsTweak.State Vbs = new VbsTweak.State();
            public bool GameMode;
            public bool MpoOff;
            public bool Dvr;
            public bool SuppressOn;
            public string Plan = "读取失败";
            public bool MemOk;
            public double UsedRatio, TotalGb, AvailGb;
            public List<string> ClockStale;
            public bool PageOk;
            public double PageFileGb;
            public string Link;
            public List<InputDevice> Inputs;
            public AccessibilityState Access;
            public bool PointerPrecision;
            public bool HidPowerSave;
            public bool QueueTampered;
            public int? ThreadDpc;
        }

        private static Facts Gather()
        {
            var f = new Facts();
            try { f.Nv = NvApi.Available; } catch { }
            try
            {
                f.Gpus = GpuInventory.Adapters();
                foreach (GpuAdapter g in f.Gpus)
                {
                    if (g.Vendor == GpuVendor.Nvidia) f.NvHardware = true;
                    if (g.Vendor == GpuVendor.Amd) f.AmdHardware = true;
                }
                f.IntegratedOnly = GpuInventory.IntegratedOnly;
            }
            catch { }
            try { f.Partition = CpuTopology.HasSafeBackgroundPartition(); } catch { }
            try { f.Eco = Native.PowerThrottlingSupported; } catch { }
            f.EcoFull = f.Eco && WindowsBuild() >= EcoQosFullBuild;
            try { f.Hags = HagsTweak.CurrentlyOn(); } catch { }
            try { f.Vbs = VbsTweak.Query(); } catch { }
            try { f.GameMode = GameModeGuard.CurrentlyOn(); } catch { }
            try { f.MpoOff = MpoTweak.CurrentlyDisabled(); } catch { }
            f.Dvr = GameDvrOn();
            try { f.SuppressOn = Settings.Load("GmSuppress", true); } catch { f.SuppressOn = true; }
            try { f.Plan = PowerPlan.CurrentPlanLabel(); } catch { }
            f.MemOk = TryMemory(out f.UsedRatio, out f.TotalGb, out f.AvailGb);
            try { f.ClockStale = PlatformClockTweak.StaleOverrides(); } catch { }
            f.PageOk = TryPageFile(out f.PageFileGb);
            try { f.Link = LinkKind(); } catch { }
            try { f.Inputs = InputChainProbe.Devices(); } catch { }
            try { f.Access = InputChainProbe.ReadAccessibility(); } catch { }
            try { f.PointerPrecision = InputChainProbe.PointerPrecisionOn(); } catch { }
            try { f.HidPowerSave = HidPowerTweak.PowerSaveActive(); } catch { }
            try { f.QueueTampered = InputMythTweak.NeedsRepair(); } catch { }
            try { f.ThreadDpc = InputMythTweak.ThreadDpcOverride(); } catch { }
            return f;
        }

        public static bool TryPageFile(out double pageFileGb)
        {
            pageFileGb = 0;
            try
            {
                var status = new MemoryStatusEx();
                status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return false;
                double limit = status.TotalPageFile / 1073741824.0;
                double phys = status.TotalPhys / 1073741824.0;
                pageFileGb = Math.Max(0, limit - phys);
                return true;
            }
            catch { return false; }
        }

        internal static bool PageFileLooksDisabled(double pageFileGb)
        {
            return pageFileGb < 0.5;
        }

        private static string LinkKind()
        {
            bool wifi = false, wired = false;
            foreach (System.Net.NetworkInformation.NetworkInterface ni
                in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var kind = ni.NetworkInterfaceType;
                if (kind == System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    || kind == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                bool gateway = false;
                try
                {
                    foreach (System.Net.NetworkInformation.GatewayIPAddressInformation gw
                        in ni.GetIPProperties().GatewayAddresses)
                        if (gw != null && gw.Address != null
                            && !gw.Address.Equals(System.Net.IPAddress.Any)) { gateway = true; break; }
                }
                catch { }
                if (!gateway) continue;
                if (kind == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) wifi = true;
                else wired = true;
            }
            if (wired && wifi) return "both";
            if (wired) return "wired";
            if (wifi) return "wifi";
            return "none";
        }

        public static AuditReport Collect(int measureWindowMs)
        {
            var report = new AuditReport();
            report.MeasureWindowMs = measureWindowMs;

            var attrib = new InterruptAttribution();
            bool attribOn = false;
            try { attribOn = attrib.Start(); } catch { }

            double cpuBusy = 0;
            double[] rates = null;
            try { rates = DpcSampler.MeasureLoad(measureWindowMs, out cpuBusy); } catch { }
            report.MeasureOk = rates != null;

            InterruptAttributionResult culprits = null;
            if (attribOn)
            {
                try { culprits = attrib.Stop(); } catch { }
            }

            double worstIrq = 0; ulong worstCore = 0;
            if (rates != null)
            {
                foreach (ulong core in CpuTopology.PhysicalCoreMasks())
                {
                    double r = CpuPartitionPolicy.CoreInterruptRate(rates, core);
                    if (r > worstIrq) { worstIrq = r; worstCore = core; }
                }
            }

            int hzCur, hzBest;
            DisplayGuard.QueryRefreshRates(out hzCur, out hzBest);

            Facts facts = Gather();
            BuildCapability(report, facts);
            BuildMachine(report, facts, cpuBusy, worstIrq, worstCore, hzCur, hzBest, culprits);
            BuildInputChain(report, facts);
            BuildPersistent(report, facts);
            BuildVerdicts(report, facts, worstIrq, hzCur, hzBest, culprits);
            return report;
        }

        private static void BuildCapability(AuditReport report, Facts facts)
        {
            if (facts.Gpus.Length > 0)
            {
                report.Capability.Add(new AuditRow
                {
                    Name = "显卡",
                    Value = GpuInventory.Describe(),
                    Note = facts.IntegratedOnly
                        ? "本机只有核显 显卡页的驱动深度调优不适用 但后台压制 绑核 电源计划照常有效 帧数收益主要从那边来"
                        : "带独显 显卡页的驱动项能不能用 看下面两行接口检测",
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }

            report.Capability.Add(new AuditRow
            {
                Name = "NVIDIA 驱动接口",
                Value = facts.Nv ? "可用" : "不可用",
                Note = facts.Nv
                    ? "电源 帧率上限 预渲染这些驱动项在本机可用 用写入实测能验证是不是真写进去了"
                    : facts.NvHardware
                        ? "有 N 卡但驱动接口调不起来 多半是驱动太老或者装的精简版 显卡页 NVIDIA 区整体停用"
                        : "本机没有 NVIDIA 显卡 显卡页 NVIDIA 区整体停用",
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            report.Capability.Add(new AuditRow
            {
                Name = "AMD 显卡",
                Value = facts.AmdHardware ? "已识别" : "未检测到",
                Note = facts.AmdHardware
                    ? "驱动专项调优只覆盖 NVIDIA A 卡的收益来自压制 绑核 电源这些通用优化 逐游戏高性能 GPU 偏好对 A 卡同样生效"
                    : "本机没有 AMD 显卡",
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            report.Capability.Add(new AuditRow
            {
                Name = "CPU Sets 分区",
                Value = facts.Partition ? "可用" : "不可用",
                Note = facts.Partition ? "可以把后台赶去单独的核心 好核心留给游戏"
                    : "核心不够分 压制只降优先级 实测这一步已经占了绝大部分收益",
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            report.Capability.Add(new AuditRow
            {
                Name = "效率模式 EcoQoS",
                Value = !facts.Eco ? "不支持" : (facts.EcoFull ? "支持" : "接口可用"),
                Note = !facts.Eco
                    ? "本机查不到这个状态 温和档会自动跳过这一手段"
                    : (facts.EcoFull
                        ? "温和档能让系统把后台降频 挪去省电核心"
                        : "Windows 10 上也能压 只是没有 Windows 11 压得彻底 效果还是有"),
                Evidence = EvMeasuredLocal,
                Warn = !facts.Eco
            });

            report.Capability.Add(new AuditRow
            {
                Name = "操作系统",
                Value = WindowsText(),
                Note = "系统版本决定哪些手段可用 同一个开关在不同版本上干的事不一样",
                Evidence = EvMeasuredLocal,
                Warn = false
            });
        }

        internal static string TopDriversText(InterruptAttributionResult culprits, int max)
        {
            if (culprits == null || !culprits.Ok || culprits.Drivers.Count == 0) return null;
            var parts = new List<string>();
            for (int i = 0; i < culprits.Drivers.Count && i < max; i++)
            {
                DriverInterrupt d = culprits.Drivers[i];
                double share = culprits.DpcTotal + culprits.IsrTotal > 0
                    ? (double)(d.Dpc + d.Isr) / (culprits.DpcTotal + culprits.IsrTotal) : 0;
                parts.Add(d.Driver + " " + PercentText(share));
            }
            return string.Join("   ", parts.ToArray());
        }

        private static void BuildMachine(AuditReport report, Facts facts, double cpuBusy, double worstIrq, ulong worstCore,
            int hzCur, int hzBest, InterruptAttributionResult culprits)
        {
            int logical = Environment.ProcessorCount;
            int physical = 0;
            try { physical = CpuTopology.PhysicalCoreCount; } catch { }
            string arch = CpuTopology.Hybrid ? "混合架构 P 核加 E 核 默认全核 可手动切性能核"
                : CpuTopology.AsymCache ? "非对称缓存 X3D 默认全核 可手动切大缓存 CCD"
                : CpuTopology.PartitionTag == "symmetric-ccd" ? "对称多 CCD 默认全核 可手动切一个完整 CCD"
                : CpuTopology.PartitionTag == "pool-iso-core0" ? "同构 默认全核 可手动切游戏核心分区"
                : "同构";
            report.Machine.Add(new AuditRow
            {
                Name = "CPU 拓扑",
                Value = physical + " 物理核 " + logical + " 线程",
                Note = arch + (CpuTopology.HasSafeBackgroundPartition()
                    ? " 手动游戏分区 0x" + CpuTopology.StrictBoostMask.ToString("X") : " 本机不划分后台核心"),
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            if (report.MeasureOk)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = "整机 CPU 占用",
                    Value = PercentText(cpuBusy),
                    Note = "体检窗口 " + (report.MeasureWindowMs >= 1000
                        ? (report.MeasureWindowMs / 1000) + " 秒" : report.MeasureWindowMs + " 毫秒") + " 内的平均占用",
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });

                int tier = InterruptTier(worstIrq);
                string topDrivers = TopDriversText(culprits, 3);
                report.Machine.Add(new AuditRow
                {
                    Name = "中断分布",
                    Value = (worstCore != 0 ? "线程 " + CoreLabel(worstCore) + " " : "")
                        + "被硬件中断占去 " + PercentText(worstIrq) + " " + InterruptTierText(tier),
                    Note = topDrivers != null
                        ? "本次窗口内中断主要来自 " + topDrivers + (tier == 2
                            ? " 做法 这几个里 nvlddmkm 是显卡 dxgkrnl 是显示子系统 对应去系统环境页开 GPU USB 两个避让开关 重启后再测 stornvme storport 是硬盘 网卡类走 ndis tcpip 这两类的中断路由在驱动层 本软件不碰"
                            : " 当前量级不影响帧 列出来只作参考")
                        : tier == 2
                            ? "到了能影响帧的量级 做法 去系统环境页把 GPU 中断亲和优化 USB 控制器中断避让 两个开关全开 重启后回这里再测对比"
                            : report.MeasureWindowMs < 10000
                                ? "短窗口只能看出量级 分辨率约 0.5% 要精确值请用 30 秒测量"
                                : "长窗口测量 分辨率约 0.05%",
                    Evidence = EvMeasuredLocal,
                    Warn = tier == 2
                });
            }
            else
            {
                report.Machine.Add(new AuditRow
                {
                    Name = "中断分布",
                    Value = "测量失败",
                    Note = "处理器性能接口不可用",
                    Evidence = EvMeasuredLocal,
                    Warn = true
                });
            }

            bool hzRead = hzCur > 0 && hzBest > 0;
            bool hzOk = RefreshRateIsBest(hzCur, hzBest);
            report.Machine.Add(new AuditRow
            {
                Name = "主屏刷新率",
                Value = hzRead
                    ? hzCur + " Hz" + (hzOk ? " 已是最高" : " 可用 " + hzBest + " Hz")
                    : "读取失败",
                Note = !hzRead
                    ? "读不到显示模式 远程桌面和部分虚拟显示器上会这样 这一项本次不做判断"
                    : (hzOk
                        ? "当前分辨率下没有更高的刷新率可选"
                        : "系统跑在 " + hzCur + "Hz 这块屏在同分辨率下支持 " + hzBest
                            + "Hz 这是实打实的帧数损失 做法 系统设置 显示 高级显示 里把刷新率改到 "
                            + hzBest + "Hz 这是持久设置 改一次就一直生效"),
                Evidence = EvMeasuredLocal,
                Warn = !hzOk || !hzRead
            });

            string throttleNow = null;
            try { throttleNow = GpuThrottleProbe.InstantText(); } catch { }
            if (throttleNow != null)
            {
                bool throttled = throttleNow != "无限制";
                report.Machine.Add(new AuditRow
                {
                    Name = "NVIDIA 降频状态",
                    Value = throttleNow,
                    Note = throttled
                        ? "显卡正被这些原因压着 游戏中要是长期这样 瓶颈在散热或供电 不在调度"
                        : "当前没有降频 游戏中要是被压制 每局结束会写进运行日志",
                    Evidence = EvMeasuredLocal,
                    Warn = throttled
                });
            }

            bool saverOn;
            if (TryEnergySaver(out saverOn))
            {
                report.Machine.Add(new AuditRow
                {
                    Name = "节能模式",
                    Value = saverOn ? "开着" : "关着",
                    Note = saverOn
                        ? "系统正在省电 会限亮度拦后台 电源模式也被锁住改不了 Pavise 的电源优化会失效 打游戏前去设置里关掉"
                        : "没有拦着电源优化",
                    Evidence = EvMeasuredLocal,
                    Warn = saverOn
                });
            }

            bool presenceOff = false;
            try { presenceOff = PresenceQos.CurrentlyDisabled(); } catch { }
            report.Machine.Add(new AuditRow
            {
                Name = "无输入降级",
                Value = presenceOff ? "已关闭" : "生效中",
                Note = presenceOff
                    ? "长时间不碰键鼠也不会把前台程序降级"
                    : "系统默认会在长时间没有键鼠输入后给前台程序降级 手柄游戏 过场动画 挂机正好中招 策略页可以关掉它",
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            bool rebarOn = false;
            ulong rebarWindow = 0;
            string rebarGpu = null;
            bool rebarNvidia = false;
            bool rebarRead = false;
            try { rebarRead = RebarProbe.TryDetect(out rebarOn, out rebarWindow, out rebarGpu, out rebarNvidia); } catch { }
            if (rebarRead)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = "ReBAR 显存直通",
                    Value = (rebarOn ? "已开启" : "未开启") + " 窗口 " + RebarProbe.WindowText(rebarWindow),
                    Note = (string.IsNullOrEmpty(rebarGpu) ? "" : rebarGpu + " ")
                        + (rebarNvidia
                            ? (rebarOn ? "显卡页的 ReBAR 强开可用" : "没开启时 ReBAR 强开无效 要在 BIOS 里打开")
                            : (rebarOn ? "A 卡叫 SAM 驱动侧已自动受益 不需要 Pavise 干预" : "A 卡叫 SAM 要在 BIOS 里打开 Above 4G 和 ReBAR")),
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }

            if (facts.MemOk)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = "内存",
                    Value = facts.TotalGb.ToString("F1") + " GB 已用 " + PercentText(facts.UsedRatio)
                        + " 可用 " + facts.AvailGb.ToString("F1") + " GB",
                    Note = facts.UsedRatio >= 0.85
                        ? "可用内存偏少 大型游戏可能被挤去拿硬盘顶内存而卡顿 建议关掉部分后台或者开启待机内存清理"
                        : "内存余量充足",
                    Evidence = EvMeasuredLocal,
                    Warn = facts.UsedRatio >= 0.85
                });
            }

            if (facts.PageOk)
            {
                bool pageDisabled = PageFileLooksDisabled(facts.PageFileGb);
                report.Machine.Add(new AuditRow
                {
                    Name = "页面文件",
                    Value = pageDisabled ? "已被关闭" : facts.PageFileGb.ToString("F1") + " GB",
                    Note = pageDisabled
                        ? "老优化教程爱关页面文件 内存吃紧时游戏会直接崩溃或闪退 而不是变慢 建议在系统高级设置里交回系统管理"
                        : "由系统管理或大小正常 不用处理",
                    Evidence = EvMeasuredLocal,
                    Warn = pageDisabled
                });
            }

            if (facts.Link != null && facts.Link != "none")
            {
                bool wifiOnly = facts.Link == "wifi";
                report.Machine.Add(new AuditRow
                {
                    Name = "网络链路",
                    Value = facts.Link == "wired" ? "有线" : wifiOnly ? "无线 Wi-Fi" : "有线加无线同时在线",
                    Note = wifiOnly
                        ? "无线的延迟和突刺天生比有线高 策略页的无线扫描抑制能砍掉后台扫信道那类周期突刺 有条件还是上网线"
                        : facts.Link == "wired" ? "有线链路 延迟稳定性最好"
                        : "两条链路都通 游戏流量走哪条取决于系统路由 想确保走有线可以临时关掉 Wi-Fi",
                    Evidence = EvMeasuredLocal,
                    Warn = wifiOnly
                });
            }

            try
            {
                SystemPowerStatus power;
                if (GetSystemPowerStatus(out power) && power.BatteryFlag != 128)
                {
                    bool onAc = power.AcLineStatus == 1;
                    report.Machine.Add(new AuditRow
                    {
                        Name = "供电方式",
                        Value = onAc ? "外接电源" : "电池供电",
                        Note = onAc ? "笔记本已接电源 性能不受电池策略限制"
                            : "电池供电时厂商固件通常会限制 CPU 和 GPU 功耗 这时候任何调度优化都补不回损失的性能 插上电源再对比",
                        Evidence = EvMechanism,
                        Warn = !onAc
                    });
                }
            }
            catch { }

            int topWindow = Math.Max(600, Math.Min(3000, report.MeasureWindowMs / 10));
            var top = TopConsumers(topWindow, 3);
            if (top.Count > 0)
            {
                var parts = new List<string>();
                foreach (LoadEntry e in top) parts.Add(e.Name + " " + PercentText(e.Ratio));
                report.Machine.Add(new AuditRow
                {
                    Name = "后台占用前三",
                    Value = string.Join("   ", parts.ToArray()),
                    Note = "最近 " + (topWindow / 1000.0).ToString("0.#")
                        + " 秒内最吃 CPU 的程序 压制最能从它们身上抢回资源 有想保护的就加白名单",
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }
        }

        internal static string InputSummary(List<InputDevice> devices, bool mouse)
        {
            if (devices == null) return null;
            var seen = new List<string>();
            foreach (InputDevice d in devices)
            {
                if (d.IsMouse != mouse) continue;
                if (d.Transport == InputTransport.Virtual) continue;
                string text = InputChainProbe.TransportText(d.Transport);
                if (!seen.Contains(text)) seen.Add(text);
            }
            return seen.Count == 0 ? null : string.Join(" 加 ", seen.ToArray());
        }

        private static void BuildInputChain(AuditReport report, Facts facts)
        {
            bool bt = false;
            try { bt = InputChainProbe.AnyBluetooth(facts.Inputs); } catch { }
            string mice = InputSummary(facts.Inputs, true);
            string keys = InputSummary(facts.Inputs, false);
            if (mice != null || keys != null)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = "键鼠链路",
                    Value = (mice == null ? "" : "鼠标 " + mice) + (mice != null && keys != null ? "   " : "")
                        + (keys == null ? "" : "键盘 " + keys),
                    Note = bt
                        ? "蓝牙键鼠是整条延迟链上外设段唯一的两位数毫秒损失 台架实测同一只鼠标蓝牙约 10.4 毫秒 换 2.4G 接收器约 3.6 毫秒 "
                            + "而 2.4G 和有线基本无差 做法 插上随附的 2.4G 接收器 或者换有线 这个没有软件解法"
                        : "USB 直连和 2.4G 接收器的外设延迟都在 1 到 5 毫秒 已经是这一段的下限 再往下抠要动硬件",
                    Evidence = bt ? EvMeasuredBench : EvMeasuredLocal,
                    Warn = bt
                });
            }

            if (facts.Access != null && facts.Access.Read)
            {
                bool swallow = facts.Access.FilterKeysSwallowing;
                bool needFix = facts.Access.AnyNeedsFix;
                report.Machine.Add(new AuditRow
                {
                    Name = "辅助功能拦截",
                    Value = swallow ? "筛选键正开着" : needFix ? "热键激活" : "干净",
                    Note = swallow
                        ? "筛选键按微软自己的定义就是让键盘忽略短促或重复的击键 现在每次击键要等 "
                            + facts.Access.DelayBeforeAcceptanceMs
                            + " 毫秒才被接受 这是本页所有键鼠项里唯一能救回两位数毫秒的一条 做法 系统环境页拨开 辅助功能拦截 开关"
                        : needFix
                            ? "三个功能本身都没开 但热键还激活着 连按五次 Shift 或长按右 Shift 八秒会弹窗打断全屏游戏 "
                                + "做法 系统环境页拨开 辅助功能拦截 开关 一次清掉 不需要管理员也不用重启"
                            : "筛选键 粘滞键 切换键都没开 热键也没激活 不会拦你的击键",
                    Evidence = EvMechanism,
                    Warn = needFix
                });
            }

            report.Machine.Add(new AuditRow
            {
                Name = "键鼠设备省电",
                Value = facts.HidPowerSave ? "允许挂起" : "已禁止挂起",
                Note = facts.HidPowerSave
                    ? "系统被允许在空闲后挂起键鼠所在的 USB 设备 微软自己的选择性暂停文档承认 退出挂起的延迟会表现为屏幕上的顿挫 "
                        + "治的是空闲后第一下操作的抖动 不是稳态延迟 做法 系统环境页拨开 键鼠设备省电 开关"
                    : "键鼠 USB 设备都不允许省电挂起 没有唤醒顿挫",
                Evidence = EvMechanism,
                Warn = facts.HidPowerSave
            });

            report.Machine.Add(new AuditRow
            {
                Name = "指针精度增强",
                Value = facts.PointerPrecision ? "开着" : "关着",
                Note = facts.PointerPrecision
                    ? "这一项不是延迟问题 是一致性问题 同样的物理位移会因为移动快慢产生不同的屏幕位移 肌肉记忆建不起来 "
                        + "竞技射击一般关掉 在系统环境页有开关 也可以自己去 系统设置 蓝牙和其他设备 鼠标 其他鼠标设置 指针选项 取消 提高指针精确度 "
                        + "注意游戏里走 Raw Input 的话本来就绕过它 只影响桌面和没走 Raw Input 的游戏"
                    : "已关闭 鼠标位移不再被系统加速曲线改写",
                Evidence = EvMechanism,
                Warn = false
            });

            bool dpc = facts.ThreadDpc.HasValue;
            if (facts.QueueTampered || dpc)
            {
                var parts = new List<string>();
                if (facts.QueueTampered) parts.Add("键鼠队列长度被改过");
                if (dpc) parts.Add("ThreadDpcEnable 被写成 " + facts.ThreadDpc.Value);
                report.Machine.Add(new AuditRow
                {
                    Name = "第三方键鼠改动",
                    Value = string.Join("  ", parts.ToArray()),
                    Note = (facts.QueueTampered
                            ? "队列长度控制的是能缓存多少条输入 不是这些数据被处理的快慢 所以调它不降延迟 调低了反而在高回报率鼠标上丢输入 "
                                + "做法 系统环境页拨开 键鼠队列校正 开关改回默认 " + InputMythTweak.SystemDefault + " 重启生效  "
                            : "")
                        + (dpc
                            ? "ThreadDpcEnable 找不到任何可信的对照测量支撑 属于优化脚本的祖传条目 本软件不代改也不建议留着 "
                                + "要清就手动删 HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel 下的 ThreadDpcEnable"
                            : ""),
                    Evidence = EvMechanism,
                    Warn = true
                });
            }
        }

        private static void BuildPersistent(AuditReport report, Facts facts)
        {
            report.Persistent.Add(new AuditRow
            {
                Name = "HAGS 硬件加速 GPU 调度",
                Value = facts.Hags ? "开启" : "关闭",
                Note = "改动需要重启生效 收益因机而异 没有普适结论",
                Evidence = EvUnverified,
                Warn = false
            });

            report.Persistent.Add(new AuditRow
            {
                Name = "VBS 基于虚拟化的安全",
                Value = !facts.Vbs.WmiOk ? "读取失败" : (facts.Vbs.VbsRunning ? "运行中" : "未运行"),
                Note = facts.Vbs.VbsRunning ? "关闭可能带来性能提升 代价是内存完整性 WSL2 Docker 和 Windows 沙盒都会受影响"
                    : "已经是关闭状态 不用处理",
                Evidence = EvMechanism,
                Warn = false
            });

            report.Persistent.Add(new AuditRow
            {
                Name = "Windows 游戏模式",
                Value = facts.GameMode ? "开启" : "关闭",
                Note = facts.GameMode ? "系统会在游戏时抑制部分后台活动 保持就行"
                    : "现在是关着的 常见于旧优化教程 建议在系统环境页开启守护",
                Evidence = EvMechanism,
                Warn = !facts.GameMode
            });

            report.Persistent.Add(new AuditRow
            {
                Name = "MPO 多平面叠加",
                Value = facts.MpoOff ? "已被禁用" : "系统默认",
                Note = facts.MpoOff
                    ? "被某个工具或手动教程关掉了 画面会全部改走合成 录屏直播远程这类抓屏程序也可能受影响 "
                        + "想改回默认删掉注册表 HKLM\\SOFTWARE\\Microsoft\\Windows\\Dwm 下的 OverlayTestMode 重启即可"
                    : "保持系统默认 这一项 Pavise 不再提供开关",
                Evidence = EvMechanism,
                Warn = facts.MpoOff
            });

            report.Persistent.Add(new AuditRow
            {
                Name = "Game DVR 后台录制",
                Value = facts.Dvr ? "开启" : "关闭",
                Note = facts.Dvr ? "Xbox Game Bar 一直在后台悄悄录着画面 占帧数 建议在优化策略页关掉"
                    : "已关闭 不用处理",
                Evidence = EvMechanism,
                Warn = facts.Dvr
            });

            report.Persistent.Add(new AuditRow
            {
                Name = "当前电源计划",
                Value = facts.Plan,
                Note = "开了电源计划开关后 对局中会自动切到高性能或卓越性能 结束后还原 不用手动改",
                Evidence = EvMechanism,
                Warn = false
            });

            if (facts.ClockStale != null)
            {
                bool stale = facts.ClockStale.Count > 0;
                report.Persistent.Add(new AuditRow
                {
                    Name = "平台时钟",
                    Value = stale ? "陈旧覆盖 " + string.Join(" ", facts.ClockStale.ToArray()) : "系统默认",
                    Note = stale
                        ? "启动配置里有老教程写的时钟覆盖 强制 HPET 在现代平台上是纯减益 做法 系统环境页找 平台时钟校正 卡片 拨开开关"
                        : "启动配置干净 系统用最快的 TSC 计时",
                    Evidence = EvMechanism,
                    Warn = stale
                });
            }
        }

        private static void BuildVerdicts(AuditReport report, Facts facts, double worstIrq, int hzCur, int hzBest,
            InterruptAttributionResult culprits)
        {
            if (hzCur > 0 && hzBest > 0 && !RefreshRateIsBest(hzCur, hzBest))
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "主屏刷新率",
                    Value = "强烈建议处理",
                    Note = "当前 " + hzCur + "Hz 可用 " + hzBest + "Hz 这一项比本页任何调度优化的收益都直接 "
                        + "帧数上限是翻倍级别的差距 而且没有任何代价 做法 系统设置 显示 高级显示 里把刷新率改到 "
                        + hzBest + "Hz 这是持久设置 改一次就一直生效 Pavise 不代改",
                    Evidence = EvMechanism,
                    Warn = true
                });
            }

            if (facts.Access != null && facts.Access.FilterKeysSwallowing)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "筛选键",
                    Value = "强烈建议处理",
                    Note = "筛选键在输入路径上主动插入了 " + facts.Access.DelayBeforeAcceptanceMs
                        + " 毫秒的接受延迟 键鼠这条链上外设段总共才 1 到 5 毫秒 这一项一个人就顶掉了好几倍 "
                        + "多数人是玩游戏时长按 Shift 被弹窗误开的 做法 系统环境页拨开 辅助功能拦截 开关 不需要管理员也不用重启",
                    Evidence = EvMechanism,
                    Warn = true
                });
            }
            else if (facts.Access != null && facts.Access.AnyNeedsFix)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "辅助功能热键",
                    Value = "建议关闭",
                    Note = "功能本身没开 但热键还留着 连按五次 Shift 或长按右 Shift 八秒会在全屏游戏里弹窗 "
                        + "打断一次团战的代价比任何毫秒级优化都大 做法 系统环境页拨开 辅助功能拦截 开关",
                    Evidence = EvMechanism,
                    Warn = false
                });
            }

            bool btInput = false;
            try { btInput = InputChainProbe.AnyBluetooth(facts.Inputs); } catch { }
            if (btInput)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "蓝牙键鼠",
                    Value = "建议换连接方式",
                    Note = "台架实测同一只鼠标蓝牙约 10.4 毫秒 2.4G 接收器约 3.6 毫秒 而 2.4G 与有线基本无差 "
                        + "无线比有线慢是过时说法 但蓝牙确实慢 而且这七毫秒比本页所有软件优化加起来都多 "
                        + "做法 插 2.4G 接收器或换有线 没有软件解法",
                    Evidence = EvMeasuredBench,
                    Warn = true
                });
            }

            if (facts.HidPowerSave)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "键鼠设备省电",
                    Value = "建议关闭",
                    Note = "治的是空闲一段时间后第一下操作的顿挫 不是稳态延迟 台式机没有代价直接关 "
                        + "笔记本要权衡续航 做法 系统环境页拨开 键鼠设备省电 开关",
                    Evidence = EvMechanism,
                    Warn = false
                });
            }

            if (facts.QueueTampered)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "键鼠队列长度",
                    Value = "建议改回默认",
                    Note = "有工具把它改过 这个值决定能缓存多少条输入 不决定输入被处理的快慢 所以调它不降延迟 "
                        + "调低了还会在高回报率鼠标上丢输入 做法 系统环境页拨开 键鼠队列校正 开关 重启生效",
                    Evidence = EvMechanism,
                    Warn = true
                });
            }

            report.Verdicts.Add(new AuditRow
            {
                Name = "输入延迟的大头在哪",
                Value = "看清楚再动手",
                Note = "端到端延迟三段 外设 1 到 5 毫秒 渲染管线在 GPU 打满时 20 到 60 毫秒 显示器 3 到 20 毫秒 "
                    + "所以键鼠注册表偏方是在错的量级上花力气 真正的大头是渲染队列堆积 "
                    + "做法 游戏支持 NVIDIA Reflex 或 AMD Anti-Lag 就在游戏里开 没有就把帧率上限压到 GPU 占用 90 到 95 百分比 "
                    + "别顶着刷新率上限跑 那会隐式触发垂直同步 反而多加半帧到一帧 显卡页的帧率上限和低延迟就是干这个的",
                Evidence = EvMeasuredBench,
                Warn = false
            });

            report.Verdicts.Add(new AuditRow
            {
                Name = "后台压制",
                Value = facts.SuppressOn ? "已开启 不用处理" : "建议开启",
                Note = "合成台架六轮配对实测 1% 最差帧改善中位 90.7% 而且免疫随时间累积的恶化",
                Evidence = EvMeasuredBench,
                Warn = false
            });

            report.Verdicts.Add(new AuditRow
            {
                Name = "Windows 游戏模式",
                Value = facts.GameMode ? "已开启 不用处理" : "建议开启",
                Note = "系统原生的游戏时段后台抑制 没有兼容风险",
                Evidence = EvMechanism,
                Warn = false
            });

            report.Verdicts.Add(new AuditRow
            {
                Name = "NVIDIA 深度调优",
                Value = facts.Nv ? "可以尝试" : "本机不适用",
                Note = facts.Nv ? "先用写入实测确认本机驱动接受写入 再按游戏开启"
                    : facts.IntegratedOnly
                        ? "核显没有对应的调优接口 这一项跳过 收益从压制 绑核和电源那边拿"
                        : "没有 NVIDIA 驱动接口",
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            if (report.MeasureOk)
            {
                int tier = InterruptTier(worstIrq);
                string culprit = culprits != null && culprits.Ok && culprits.TopDpc != null
                    ? culprits.TopDpc : null;
                report.Verdicts.Add(new AuditRow
                {
                    Name = "中断负载",
                    Value = tier == 2 ? "建议处理" : "不用处理",
                    Note = tier == 2
                        ? (culprit != null
                            ? "有个核心 " + PercentText(worstIrq) + " 的时间在处理硬件中断 头号来源是 " + culprit
                                + " 做法 它属于显卡就开 GPU 中断亲和 硬盘和网卡类的中断路由属于驱动层 本软件的避让开关够不到 只能等驱动更新"
                            : "有个核心 " + PercentText(worstIrq) + " 的时间在处理硬件中断 做法 系统环境页开两个避让开关 重启后再体检对比 数值降了就是它们 降不下来是硬盘 网卡或主板设备在响 那层属于驱动和硬件 本软件不碰")
                        : "当前量级 " + PercentText(worstIrq) + " 很小 感觉不出来 不用管",
                    Evidence = EvMeasuredLocal,
                    Warn = tier == 2
                });
            }

            if (facts.Dvr)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "Game DVR 后台录制",
                    Value = "建议关闭",
                    Note = "后台录制一直开着就一直有开销 多数人根本不用 Xbox Game Bar 录制",
                    Evidence = EvMechanism,
                    Warn = false
                });
            }

            if (facts.MemOk && facts.UsedRatio >= 0.85)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "内存余量",
                    Value = "建议处理",
                    Note = "只剩 " + facts.AvailGb.ToString("F1") + " GB 可用 内存不够时系统会拿硬盘顶内存 那是毫秒级的等待 "
                        + "比任何调度问题都更容易造成明显卡顿 做法 退掉吃内存的后台程序 长期紧张就加内存条 这个没有软件解法",
                    Evidence = EvMechanism,
                    Warn = true
                });
            }

            if (facts.Vbs.WmiOk && facts.Vbs.VbsRunning)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "VBS",
                    Value = "可以尝试关闭",
                    Note = "有代价 影响内存完整性 WSL2 Docker 和沙盒 用这些功能就别关 做法 系统环境页找 VBS 卡片 拨开开关 重启生效",
                    Evidence = EvMechanism,
                    Warn = false
                });
            }

            if (facts.ClockStale != null && facts.ClockStale.Count > 0)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "平台时钟",
                    Value = "建议校正",
                    Note = "强制 HPET 是十年前的教程遗产 TSC 计时比它快几个数量级 做法 系统环境页找 平台时钟校正 卡片 拨开开关就清掉 重启生效 可还原",
                    Evidence = EvMechanism,
                    Warn = true
                });
            }

            if (facts.PageOk && PageFileLooksDisabled(facts.PageFileGb))
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "页面文件",
                    Value = "建议恢复",
                    Note = "关页面文件不提帧 只把内存吃紧时的变慢换成直接崩溃 部分游戏和反作弊还要求它存在",
                    Evidence = EvMechanism,
                    Warn = true
                });
            }
        }
    }
}
