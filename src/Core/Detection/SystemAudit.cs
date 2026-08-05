// @author bdth 2074055628@qq.com
// 文件用途 系统体检 聚合本机能力 实测数据与持久设置 输出带依据等级的结论清单

using System;
using System.Collections.Generic;

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

        // 中断分层：<1% 干净，1%~5% 正常，>=5% 异常（微秒级干扰，5% 以下不构成可感知卡顿）
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

            BuildCapability(report);
            BuildMachine(report, cpuBusy, worstIrq, worstCore);
            BuildPersistent(report);
            BuildVerdicts(report, worstIrq);
            return report;
        }

        private static void BuildCapability(AuditReport report)
        {
            bool nv = false;
            try { nv = NvApi.Available; } catch { }
            report.Capability.Add(new AuditRow
            {
                Name = "NVIDIA 驱动接口",
                Value = nv ? "可用" : "不可用",
                Note = nv ? "深度调优（电源 / 帧率上限 / 预渲染）可按游戏写入驱动 Profile，可用「写入实测」按钮验证真实生效"
                    : "本机没有可用的 NVIDIA 驱动，显卡页的深度调优整体停用",
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            bool partition = false;
            try { partition = CpuTopology.HasSafeBackgroundPartition(); } catch { }
            report.Capability.Add(new AuditRow
            {
                Name = "CPU Sets 分区",
                Value = partition ? "可用" : "不可用",
                Note = partition ? "严格分区与后台核心迁移可以生效"
                    : "核心数不足或系统接口缺失，压制退化为纯优先级调整（实测优先级已占绝大部分收益）",
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            bool eco = false;
            try { eco = Native.PowerThrottlingSupported; } catch { }
            report.Capability.Add(new AuditRow
            {
                Name = "效率模式 EcoQoS",
                Value = eco ? "支持" : "不支持",
                Note = eco ? "温和档压制可将后台进程放入效率模式"
                    : "旧版 Windows 10 无此接口，温和档自动跳过该项",
                Evidence = EvMeasuredLocal,
                Warn = !eco
            });
        }

        private static void BuildMachine(AuditReport report, double cpuBusy, double worstIrq, ulong worstCore)
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
        }

        private static void BuildPersistent(AuditReport report)
        {
            bool hags = false;
            try { hags = HagsTweak.CurrentlyOn(); } catch { }
            report.Persistent.Add(new AuditRow
            {
                Name = "HAGS 硬件加速 GPU 调度",
                Value = hags ? "开启" : "关闭",
                Note = "改动需重启生效；收益因机而异，没有普适结论",
                Evidence = EvUnverified,
                Warn = false
            });

            var vbs = new VbsTweak.State();
            try { vbs = VbsTweak.Query(); } catch { }
            report.Persistent.Add(new AuditRow
            {
                Name = "VBS 基于虚拟化的安全",
                Value = !vbs.WmiOk ? "读取失败" : (vbs.VbsRunning ? "运行中" : "未运行"),
                Note = vbs.VbsRunning ? "关闭可能带来性能提升，但会影响内存完整性、WSL2、Docker 和 Windows 沙盒，取舍需要自己决定"
                    : "已经处于关闭状态，无需处理",
                Evidence = EvMechanism,
                Warn = false
            });

            bool gameMode = false;
            try { gameMode = GameModeGuard.CurrentlyOn(); } catch { }
            report.Persistent.Add(new AuditRow
            {
                Name = "Windows 游戏模式",
                Value = gameMode ? "开启" : "关闭",
                Note = gameMode ? "系统会在游戏时抑制部分后台活动，保持即可"
                    : "被关闭状态（常见于旧优化教程），建议在系统环境页开启守护",
                Evidence = EvMechanism,
                Warn = !gameMode
            });

            bool mpoOff = false;
            try { mpoOff = MpoTweak.CurrentlyDisabled(); } catch { }
            report.Persistent.Add(new AuditRow
            {
                Name = "MPO 多平面叠加",
                Value = mpoOff ? "已禁用" : "系统默认",
                Note = "只有出现花屏、闪烁等兼容问题时才需要禁用，正常情况保持默认",
                Evidence = EvMechanism,
                Warn = false
            });

            string plan = "读取失败";
            try { plan = PowerPlan.CurrentPlanLabel(); } catch { }
            report.Persistent.Add(new AuditRow
            {
                Name = "当前电源计划",
                Value = plan,
                Note = "开启电源计划开关后，对局中会自动切到高性能/卓越性能并在结束后还原，无需手动改",
                Evidence = EvMechanism,
                Warn = false
            });
        }

        private static void BuildVerdicts(AuditReport report, double worstIrq)
        {
            report.Verdicts.Add(new AuditRow
            {
                Name = "后台压制",
                Value = "建议开启",
                Note = "合成台架六轮配对实测：1% 最差帧改善中位 90.7%，且免疫随时间累积的恶化",
                Evidence = EvMeasuredBench,
                Warn = false
            });

            bool gameMode = false;
            try { gameMode = GameModeGuard.CurrentlyOn(); } catch { }
            report.Verdicts.Add(new AuditRow
            {
                Name = "Windows 游戏模式",
                Value = gameMode ? "已开启，无需处理" : "建议开启",
                Note = "系统原生的游戏时段后台抑制，无兼容风险",
                Evidence = EvMechanism,
                Warn = false
            });

            bool nv = false;
            try { nv = NvApi.Available; } catch { }
            report.Verdicts.Add(new AuditRow
            {
                Name = "NVIDIA 深度调优",
                Value = nv ? "可以尝试" : "本机不适用",
                Note = nv ? "先用「写入实测」确认本机驱动接受写入，再按游戏开启"
                    : "无 NVIDIA 驱动接口",
                Evidence = nv ? EvMeasuredLocal : EvMeasuredLocal,
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
                        ? "最脏核占用超过 5%，建议排查该核上的设备，或用系统环境页的中断亲和把设备中断引走"
                        : "中断的单次干扰是微秒级，当前量级（" + PercentText(worstIrq) + "）不足以造成可感知的卡顿",
                    Evidence = EvMeasuredLocal,
                    Warn = tier == 2
                });
            }

            var vbs = new VbsTweak.State();
            try { vbs = VbsTweak.Query(); } catch { }
            if (vbs.WmiOk && vbs.VbsRunning)
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
