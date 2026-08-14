// @author bdth 2074055628@qq.com
// 文件用途 系统体检硬件健康分部 内存模块 灯效常驻 降频事件 机械盘寻道检测

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {
        private static void BuildHardwareHealth(AuditReport report, Facts facts)
        {
            if (facts.MemModules > 0)
            {
                bool xmpSuspect = facts.MemConfiguredMhz > 0 && facts.MemRatedMhz > 0
                    && facts.MemConfiguredMhz * 10 < facts.MemRatedMhz * 9;
                report.Machine.Add(new AuditRow
                {
                    Name = "内存频率",
                    Value = facts.MemConfiguredMhz > 0
                        ? facts.MemConfiguredMhz + " MHz"
                            + (facts.MemRatedMhz > facts.MemConfiguredMhz ? " 标称 " + facts.MemRatedMhz : "")
                        : "读取失败",
                    Note = xmpSuspect
                        ? "疑似跑在 JEDEC 默认频率 没吃满内存条标称速度 去 BIOS 开 XMP 或 EXPO 台架实测 1% low 能高一成以上 板载内存机型不适用此判断"
                        : "已接近标称频率 或本机读数不足以判断 不用处理",
                    Evidence = xmpSuspect ? EvMeasuredBench : EvMechanism,
                    Warn = xmpSuspect
                });
                report.Machine.Add(new AuditRow
                {
                    Name = "内存通道",
                    Value = facts.MemModules + " 条内存",
                    Note = facts.MemModules == 1
                        ? "单条内存只有单通道带宽 1% low 受影响明显 加装一条同规格内存组双通道是性价比最高的升级之一 板载内存机型不适用"
                        : "两条及以上 一般已组成双通道 不用处理",
                    Evidence = EvMechanism,
                    Warn = facts.MemModules == 1
                });
            }

            if (facts.AllHz != null && facts.AllHz.Count > 1)
            {
                int min = int.MaxValue, max = 0;
                var parts = new List<string>();
                foreach (int hz in facts.AllHz)
                {
                    if (hz < min) min = hz;
                    if (hz > max) max = hz;
                    parts.Add(hz + "Hz");
                }
                bool mixed = max - min > 5;
                bool oldOs = facts.OsBuild > 0 && facts.OsBuild < 26100;
                report.Machine.Add(new AuditRow
                {
                    Name = "多屏刷新率",
                    Value = string.Join(" / ", parts.ToArray()),
                    Note = mixed && oldOs
                        ? "多台显示器刷新率不一致 该版本系统的桌面合成器会因此掉帧 副屏播视频时主屏最明显 24H2 已改进 建议更新系统或把副屏设为相同刷新率"
                        : mixed ? "刷新率不一致 当前系统已含混合刷新率改进 一般无碍" : "各屏一致 不用处理",
                    Evidence = EvMechanism,
                    Warn = mixed && oldOs
                });
            }

            report.Machine.Add(new AuditRow
            {
                Name = "CPU 降频事件",
                Value = facts.ThrottleEvents7d > 0 ? "近 7 天 " + facts.ThrottleEvents7d + " 次" : "近 7 天无记录",
                Note = facts.ThrottleEvents7d > 0
                    ? "系统日志里有内核处理器降频记录 多为散热或供电不足 越玩越卡的常见真因 清灰改善风道或检查电源适配器后再看这项"
                    : "系统日志无处理器降频记录 散热和供电目前没拖后腿",
                Evidence = EvMeasuredLocal,
                Warn = facts.ThrottleEvents7d > 0
            });

            if (facts.HddRoots != null && facts.HddRoots.Count > 0)
                report.Machine.Add(new AuditRow
                {
                    Name = "游戏在机械硬盘",
                    Value = facts.HddRoots.Count + " 个游戏目录",
                    Note = "所在磁盘有寻道惩罚 开放世界类游戏的流式加载会持续卡顿 建议把常玩的游戏移到固态盘",
                    Evidence = EvMechanism,
                    Warn = true
                });

            if (facts.RgbSuites != null && facts.RgbSuites.Count > 0)
                report.Machine.Add(new AuditRow
                {
                    Name = "灯效常驻软件",
                    Value = string.Join(" ", facts.RgbSuites.ToArray()),
                    Note = "社区多有此类软件引发帧时间尖峰的成案 遇到无法归因的卡顿时 建议先退出它们对比一局再下结论",
                    Evidence = EvMechanism,
                    Warn = false
                });

            if (facts.RyzenCpu && facts.OsBuild > 0)
            {
                bool old = facts.OsBuild < 26100;
                report.Persistent.Add(new AuditRow
                {
                    Name = "Ryzen 分支预测优化",
                    Value = old ? "系统版本较旧" : "24H2 已含",
                    Note = old
                        ? "Windows 11 24H2 为 Ryzen 加入分支预测优化 台架实测游戏平均快一成 个别标题更多 23H2 装齐 2024 年 8 月之后的累积更新也已回移 建议更新系统"
                        : "当前系统已包含 AMD 分支预测优化 不用处理",
                    Evidence = EvMeasuredBench,
                    Warn = old
                });
            }

            if (facts.OsBuild >= 22000)
                report.Persistent.Add(new AuditRow
                {
                    Name = "窗口化游戏优化",
                    Value = facts.WindowedOptOn ? "开启" : "未开启",
                    Note = facts.WindowedOptOn
                        ? "旧 DX10 DX11 游戏的窗口化呈现已走升级路径 保持就行"
                        : "微软官方背书的降延迟机制 旧 DX10 DX11 游戏窗口化时改走翻转模型 还解锁自动 HDR 与可变刷新率 做法 系统环境页拨开 窗口化游戏优化 开关 重启游戏后生效",
                    Evidence = EvMechanism,
                    Warn = !facts.WindowedOptOn
                });

            report.Persistent.Add(new AuditRow
            {
                Name = "显卡 MSI 中断",
                Value = facts.MsiOffCount > 0 ? facts.MsiOffCount + " 个设备被关闭" : "正常",
                Note = facts.MsiOffCount > 0
                    ? "MSISupported 被写成 0 多半是旧优化工具留下的 中断退回传统线模式会抬高 DPC 延迟 点右侧一键修复写回 重启生效 可还原"
                    : "消息信号中断未被干预 现代驱动默认即为 MSI 不用处理",
                Evidence = EvMechanism,
                Warn = facts.MsiOffCount > 0,
                FixKey = "msi"
            });
        }

        private static void GatherMemoryModules(Facts f)
        {
            using (var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory"))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(10);
                using (var results = searcher.Get())
                    foreach (System.Management.ManagementBaseObject mo in results)
                        using (mo)
                        {
                            f.MemModules++;
                            int rated = ToMhz(mo["Speed"]);
                            int cfg = ToMhz(mo["ConfiguredClockSpeed"]);
                            if (rated > f.MemRatedMhz) f.MemRatedMhz = rated;
                            if (cfg > f.MemConfiguredMhz) f.MemConfiguredMhz = cfg;
                        }
            }
        }

        private static int ToMhz(object value)
        {
            try { return value == null ? 0 : Convert.ToInt32(value); }
            catch { return 0; }
        }

        private static List<string> ScanRgbSuites()
        {
            var hits = new List<string>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string n = p.ProcessName.ToLowerInvariant();
                    string label = null;
                    if (n.Contains("icue")) label = "Corsair iCUE";
                    else if (n.Contains("lightingservice")) label = "ASUS Aura";
                    else if (n.Contains("armourycrate") || n.Contains("armouryswagent")) label = "Armoury Crate";
                    else if (n.Contains("razerappengine") || n.Contains("rzsynapse")
                        || n.Contains("razer synapse")) label = "Razer Synapse";
                    else if (n.Contains("mysticlight")) label = "MSI Mystic Light";
                    else if (n.Contains("signalrgb")) label = "SignalRGB";
                    if (label != null && !hits.Contains(label)) hits.Add(label);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return hits;
        }

        private static int CountCpuThrottleEvents()
        {
            string q = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power']"
                + " and (EventID=37) and TimeCreated[timediff(@SystemTime) <= 604800000]]]";
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                "System", System.Diagnostics.Eventing.Reader.PathType.LogName, q);
            int n = 0;
            using (var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query))
                for (System.Diagnostics.Eventing.Reader.EventRecord r = reader.ReadEvent();
                    r != null && n < 200; r = reader.ReadEvent())
                    using (r) n++;
            return n;
        }

        private static List<string> SeekPenaltyRoots(List<string> roots)
        {
            var hits = new List<string>();
            if (roots == null) return hits;
            var drives = new Dictionary<char, bool>();
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') continue;
                char drive = char.ToUpperInvariant(root[0]);
                bool penalty;
                if (!drives.TryGetValue(drive, out penalty))
                {
                    penalty = DriveHasSeekPenalty(drive);
                    drives[drive] = penalty;
                }
                if (penalty) hits.Add(root);
            }
            return hits;
        }

        private static bool DriveHasSeekPenalty(char drive)
        {
            IntPtr h = CreateFileW(@"\\.\" + drive + ":", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
            try
            {
                var query = new StoragePropertyQuery { PropertyId = 7, QueryType = 0 };
                DeviceSeekPenaltyDescriptor descriptor;
                uint got;
                if (!DeviceIoControl(h, 0x2D1400, ref query,
                        (uint)Marshal.SizeOf(typeof(StoragePropertyQuery)),
                        out descriptor, (uint)Marshal.SizeOf(typeof(DeviceSeekPenaltyDescriptor)),
                        out got, IntPtr.Zero))
                    return false;
                return descriptor.IncursSeekPenalty != 0;
            }
            catch { return false; }
            finally { CloseHandle(h); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StoragePropertyQuery
        {
            public uint PropertyId;
            public uint QueryType;
            public byte AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceSeekPenaltyDescriptor
        {
            public uint Version;
            public uint Size;
            public byte IncursSeekPenalty;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string fileName, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint code,
            ref StoragePropertyQuery input, uint inputSize,
            out DeviceSeekPenaltyDescriptor output, uint outputSize,
            out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
