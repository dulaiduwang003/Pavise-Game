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
            return string.Join("/", parts.ToArray());
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
                    if (build != null) text += " (" + build + (ubr != null ? "." + ubr : "") + ")";
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
        }

        private static Facts Gather()
        {
            var f = new Facts();
            try { f.Nv = NvApi.Available; } catch { }
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
            return f;
        }

        public static AuditReport Collect(int measureWindowMs)
        {
            var report = new AuditReport();
            report.MeasureWindowMs = measureWindowMs;

            double cpuBusy = 0;
            double[] rates = null;
            try { rates = DpcSampler.MeasureLoad(measureWindowMs, out cpuBusy); } catch { }
            report.MeasureOk = rates != null;

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
            BuildMachine(report, facts, cpuBusy, worstIrq, worstCore, hzCur, hzBest);
            BuildPersistent(report, facts);
            BuildVerdicts(report, facts, worstIrq, hzCur, hzBest);
            return report;
        }

        private static void BuildCapability(AuditReport report, Facts facts)
        {
            report.Capability.Add(new AuditRow
            {
                Name = "NVIDIA 驱动接口",
                Value = facts.Nv ? "可用" : "不可用",
                Note = facts.Nv ? "显卡深度调优（电源 / 帧率上限 / 预渲染）在本机可用；用「写入实测」能验证是不是真写进去了"
                    : "本机没有可用的 NVIDIA 驱动，显卡页的深度调优整体停用",
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            bool amd = false;
            try { amd = AdlxApi.Available; } catch { }
            report.Capability.Add(new AuditRow
            {
                Name = "AMD 驱动接口",
                Value = amd ? "可用" : "不可用",
                Note = amd
                    ? "显卡页 AMD 区可用。效果未经实机验证，可用「AMD 写入实测」核对写入"
                    : "无 A 卡或驱动太老，显卡页 AMD 区不可用",
                Evidence = amd ? EvUnverified : EvMeasuredLocal,
                Warn = false
            });

            report.Capability.Add(new AuditRow
            {
                Name = "CPU Sets 分区",
                Value = facts.Partition ? "可用" : "不可用",
                Note = facts.Partition ? "可以把后台赶去单独的核心，好核心留给游戏"
                    : "核心不够分，压制只降优先级——实测这一步已占绝大部分收益",
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            report.Capability.Add(new AuditRow
            {
                Name = "效率模式 EcoQoS",
                Value = !facts.Eco ? "不支持" : (facts.EcoFull ? "支持" : "接口可用"),
                Note = !facts.Eco
                    ? "本机查不到这个状态，温和档会自动跳过这一手段"
                    : (facts.EcoFull
                        ? "温和档能让系统把后台降频、挪去省电核心"
                        : "Windows 10 上也能压，只是没有 Windows 11 压得彻底；效果仍然有"),
                Evidence = EvMeasuredLocal,
                Warn = !facts.Eco
            });

            report.Capability.Add(new AuditRow
            {
                Name = "操作系统",
                Value = WindowsText(),
                Note = "系统版本决定哪些手段可用：同一个开关在不同版本上干的事并不一样",
                Evidence = EvMeasuredLocal,
                Warn = false
            });
        }

        private static void BuildMachine(AuditReport report, Facts facts, double cpuBusy, double worstIrq, ulong worstCore,
            int hzCur, int hzBest)
        {
            int logical = Environment.ProcessorCount;
            int physical = 0;
            try { physical = CpuTopology.PhysicalCoreCount; } catch { }
            string arch = CpuTopology.Hybrid ? "混合架构（P 核 + E 核）"
                : (CpuTopology.AsymCache ? "非对称缓存（X3D）" : "同构");
            report.Machine.Add(new AuditRow
            {
                Name = "CPU 拓扑",
                Value = physical + " 物理核 / " + logical + " 线程",
                Note = arch + (CpuTopology.HasSafeBackgroundPartition()
                    ? "，竞技游戏分区 0x" + CpuTopology.StrictBoostMask.ToString("X") : "，本机不划分后台核心"),
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
                        ? (report.MeasureWindowMs / 1000) + " 秒" : report.MeasureWindowMs + " 毫秒") + "内的平均占用",
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });

                int tier = InterruptTier(worstIrq);
                report.Machine.Add(new AuditRow
                {
                    Name = "中断分布",
                    Value = "最脏核 " + (worstCore != 0 ? CoreLabel(worstCore) + " · " : "") + PercentText(worstIrq)
                        + " · " + InterruptTierText(tier),
                    Note = report.MeasureWindowMs < 10000
                        ? "短窗口只能看出量级（分辨率约 0.5%），需要精确值请用 30 秒测量"
                        : "长窗口测量，分辨率约 0.05%",
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
                    ? hzCur + " Hz" + (hzOk ? "（已是最高）" : " · 可用 " + hzBest + " Hz")
                    : "读取失败",
                Note = !hzRead
                    ? "读不到显示模式，远程桌面和部分虚拟显示器上会这样；这一项本次不做判断"
                    : (hzOk
                        ? "当前分辨率下没有更高的刷新率可选"
                        : "系统跑在 " + hzCur + "Hz，而这块屏在同分辨率下支持 " + hzBest
                            + "Hz——这是实打实的帧数损失，开启刷新率守护或在系统显示设置里改掉"),
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
                        ? "显卡正被这些原因压着；若游戏中长期如此，瓶颈在散热或供电，不在调度"
                        : "当前无降频；游戏中如被压制，每局结束写进运行日志",
                    Evidence = EvMeasuredLocal,
                    Warn = throttled
                });
            }

            bool rebarOn = false;
            ulong rebarWindow = 0;
            string rebarGpu = null;
            bool rebarRead = false;
            try { rebarRead = RebarProbe.TryDetect(out rebarOn, out rebarWindow, out rebarGpu); } catch { }
            if (rebarRead)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = "ReBAR 显存直通",
                    Value = (rebarOn ? "已开启" : "未开启") + " · 窗口 " + RebarProbe.WindowText(rebarWindow),
                    Note = (string.IsNullOrEmpty(rebarGpu) ? "" : rebarGpu + "；")
                        + (rebarOn ? "显卡页「ReBAR 强开」可用" : "未开启时「ReBAR 强开」无效，要在 BIOS 里打开"),
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }

            if (facts.MemOk)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = "内存",
                    Value = facts.TotalGb.ToString("F1") + " GB · 已用 " + PercentText(facts.UsedRatio)
                        + " · 可用 " + facts.AvailGb.ToString("F1") + " GB",
                    Note = facts.UsedRatio >= 0.85
                        ? "可用内存偏少，大型游戏可能被挤去拿硬盘顶内存而卡顿，建议关掉部分后台或开启待机内存清理"
                        : "内存余量充足",
                    Evidence = EvMeasuredLocal,
                    Warn = facts.UsedRatio >= 0.85
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
                        Note = onAc ? "笔记本已接电源，性能不受电池策略限制"
                            : "电池供电时厂商固件通常会限制 CPU/GPU 功耗，此时任何调度优化都补不回损失的性能，插上电源再对比",
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
                    Value = string.Join(" · ", parts.ToArray()),
                    Note = "最近 " + (topWindow / 1000.0).ToString("0.#")
                        + " 秒内最吃 CPU 的程序。压制最能从它们身上抢回资源；有想保护的就加白名单",
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }
        }

        private static void BuildPersistent(AuditReport report, Facts facts)
        {
            report.Persistent.Add(new AuditRow
            {
                Name = "HAGS 硬件加速 GPU 调度",
                Value = facts.Hags ? "开启" : "关闭",
                Note = "改动需重启生效；收益因机而异，没有普适结论",
                Evidence = EvUnverified,
                Warn = false
            });

            report.Persistent.Add(new AuditRow
            {
                Name = "VBS 基于虚拟化的安全",
                Value = !facts.Vbs.WmiOk ? "读取失败" : (facts.Vbs.VbsRunning ? "运行中" : "未运行"),
                Note = facts.Vbs.VbsRunning ? "关闭可能带来性能提升，代价是内存完整性、WSL2、Docker 和 Windows 沙盒都会受影响"
                    : "已经处于关闭状态，无需处理",
                Evidence = EvMechanism,
                Warn = false
            });

            report.Persistent.Add(new AuditRow
            {
                Name = "Windows 游戏模式",
                Value = facts.GameMode ? "开启" : "关闭",
                Note = facts.GameMode ? "系统会在游戏时抑制部分后台活动，保持即可"
                    : "被关闭状态（常见于旧优化教程），建议在系统环境页开启守护",
                Evidence = EvMechanism,
                Warn = !facts.GameMode
            });

            report.Persistent.Add(new AuditRow
            {
                Name = "MPO 多平面叠加",
                Value = facts.MpoOff ? "已禁用" : "系统默认",
                Note = "只有出现花屏、闪烁等兼容问题时才需要禁用，正常情况保持默认",
                Evidence = EvMechanism,
                Warn = false
            });

            report.Persistent.Add(new AuditRow
            {
                Name = "Game DVR 后台录制",
                Value = facts.Dvr ? "开启" : "关闭",
                Note = facts.Dvr ? "Xbox Game Bar 一直在后台悄悄录着画面，占帧数，建议在优化策略页关掉"
                    : "已关闭，无需处理",
                Evidence = EvMechanism,
                Warn = facts.Dvr
            });

            report.Persistent.Add(new AuditRow
            {
                Name = "当前电源计划",
                Value = facts.Plan,
                Note = "开启电源计划开关后，对局中会自动切到高性能/卓越性能并在结束后还原，无需手动改",
                Evidence = EvMechanism,
                Warn = false
            });
        }

        private static void BuildVerdicts(AuditReport report, Facts facts, double worstIrq, int hzCur, int hzBest)
        {
            if (hzCur > 0 && hzBest > 0 && !RefreshRateIsBest(hzCur, hzBest))
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "主屏刷新率",
                    Value = "强烈建议处理",
                    Note = "当前 " + hzCur + "Hz，可用 " + hzBest + "Hz。这一项比本页任何调度优化的收益都直接——"
                        + "帧数上限直接翻倍级别的差距，且没有任何代价",
                    Evidence = EvMechanism,
                    Warn = true
                });
            }

            report.Verdicts.Add(new AuditRow
            {
                Name = "后台压制",
                Value = facts.SuppressOn ? "已开启，无需处理" : "建议开启",
                Note = "合成台架六轮配对实测：1% 最差帧改善中位 90.7%，且免疫随时间累积的恶化",
                Evidence = EvMeasuredBench,
                Warn = false
            });

            report.Verdicts.Add(new AuditRow
            {
                Name = "Windows 游戏模式",
                Value = facts.GameMode ? "已开启，无需处理" : "建议开启",
                Note = "系统原生的游戏时段后台抑制，无兼容风险",
                Evidence = EvMechanism,
                Warn = false
            });

            report.Verdicts.Add(new AuditRow
            {
                Name = "NVIDIA 深度调优",
                Value = facts.Nv ? "可以尝试" : "本机不适用",
                Note = facts.Nv ? "先用「写入实测」确认本机驱动接受写入，再按游戏开启"
                    : "无 NVIDIA 驱动接口",
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            if (report.MeasureOk)
            {
                int tier = InterruptTier(worstIrq);
                report.Verdicts.Add(new AuditRow
                {
                    Name = "中断负载",
                    Value = tier == 2 ? "建议处理" : "无需处理",
                    Note = tier == 2
                        ? "有个核心被硬件打扰得厉害（超过 5%），建议排查它上面的设备，或用系统环境页的中断亲和把干扰引走"
                        : "当前量级（" + PercentText(worstIrq) + "）很小，感觉不出来，不用管",
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
                    Note = "后台录制一直开着就一直有开销，而多数人根本不用 Xbox Game Bar 录制",
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
                    Note = "可用仅 " + facts.AvailGb.ToString("F1") + " GB。内存不够时系统会拿硬盘顶内存，那是毫秒级的等待，"
                        + "比任何调度问题都更容易造成明显卡顿",
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
                    Note = "有代价：影响内存完整性、WSL2、Docker、沙盒；用这些功能就别关",
                    Evidence = EvMechanism,
                    Warn = false
                });
            }
        }
    }
}
