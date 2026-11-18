using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class AiTuningHardwareReport
    {
        private static string Unknown { get { return Lang.TranslateChinese("未知／未读取"); } }
        private const string DisplayClass = "SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}";
        private static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        private static readonly Guid HighPerformance = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        private static readonly Guid PowerSaver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        private static readonly Guid Ultimate = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");

        internal static string Read(string displayDeviceName)
        {
            var stringBuilder = new StringBuilder();
            Append(stringBuilder, Lang.TranslateChinese("系统"), ReadWindows);

            OverviewHardwareInfo hardware = null;
            try
            {
                using (var reader = new OverviewTelemetryReader())
                    hardware = reader.ReadHardware(displayDeviceName);
            }
            catch { }

            stringBuilder.AppendLine(Lang.TranslateChinese("CPU 型号：") + Safe(hardware == null ? null : hardware.CpuName));
            stringBuilder.AppendLine(Lang.TranslateChinese("物理核心／逻辑线程：") + Number(hardware == null ? 0 : hardware.PhysicalCores)
                + Lang.TranslateChinese(" 核／") + Number(hardware == null ? 0 : hardware.LogicalProcessors) + Lang.TranslateChinese(" 线程"));
            Append(stringBuilder, Lang.TranslateChinese("CPU 拓扑"), ReadTopology);
            Append(stringBuilder, "GPU", ReadGpus);
            stringBuilder.AppendLine(Lang.TranslateChinese("物理内存：") + Capacity(hardware == null ? 0UL : hardware.InstalledMemoryBytes)
                + Lang.TranslateChinese("；类型 ") + Safe(hardware == null ? null : hardware.MemoryType)
                + Lang.TranslateChinese("；配置速度 ") + (hardware != null && hardware.MemorySpeedMts > 0
                    ? hardware.MemorySpeedMts.ToString(CultureInfo.InvariantCulture) + " MT/s" : Unknown));
            stringBuilder.AppendLine(Lang.TranslateChinese("桌面显示模式（不是游戏分辨率/限帧设置）：") + (hardware != null && hardware.DisplayWidth > 0 && hardware.DisplayHeight > 0
                ? hardware.DisplayWidth.ToString(CultureInfo.InvariantCulture) + " × " + hardware.DisplayHeight.ToString(CultureInfo.InvariantCulture)
                    + "；" + (hardware.DisplayRefreshRate > 0 ? hardware.DisplayRefreshRate.ToString(CultureInfo.InvariantCulture) + " Hz" : Lang.TranslateChinese("刷新率") + Unknown)
                : Unknown));
            Append(stringBuilder, Lang.TranslateChinese("供电"), ReadPowerStatus);
            Append(stringBuilder, Lang.TranslateChinese("当前活动电源计划"), ReadActivePlan);
            Append(stringBuilder, Lang.TranslateChinese("Pavise 电源选择策略"), ReadPlanChoice);
            Append(stringBuilder, Lang.TranslateChinese("EPP 功耗让路适用性"), delegate
            {
                LaboratoryEppAvailability value = LaboratoryEppEligibility.Current;
                return value.Available ? Lang.TranslateChinese("当前核验允许使用（笔记本、可核验电池及交流供电）")
                    : Safe(Lang.Cur == 3 ? value.Detail : value.Chinese);
            });
            stringBuilder.AppendLine(Lang.TranslateChinese("来源：现有缓存与Windows只读接口；缓存域不等于CCD，核显显存不等于独立显存。游戏设置、磁盘类型、温度、负载瓶颈和帧时间未采集，未知不代表关闭/不支持；未导出设备标识和路径。"));
            return stringBuilder.ToString();
        }

        private static void Append(StringBuilder text, string label, Func<string> read)
        {
            string value;
            try { value = read(); }
            catch { value = Unknown; }
            text.AppendLine(label + "：" + (string.IsNullOrWhiteSpace(value) ? Unknown : value));
        }

        private static string ReadWindows()
        {
            int build = Native.OsBuild();
            string product = "Windows", release = "";
            uint? revision = null;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"))
                {
                    if (key != null)
                    {
                        string installation = key.GetValue("InstallationType") as string;
                        if (!string.IsNullOrWhiteSpace(installation) && installation.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0)
                            product = "Windows Server";
                        else if (string.Equals(installation, "Client", StringComparison.OrdinalIgnoreCase))
                            product = build >= 22000 ? "Windows 11" : build >= 10240 ? "Windows 10" : "Windows";
                        release = SafeOptional(key.GetValue("DisplayVersion") as string);
                        uint parsed;
                        if (uint.TryParse(Convert.ToString(key.GetValue("UBR"), CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                            revision = parsed;
                    }
                }
            }
            catch { }
            return product + (release.Length == 0 ? "" : " " + release) + Lang.TranslateChinese("；原生 Build ")
                + (build > 0 ? build.ToString(CultureInfo.InvariantCulture) + (revision.HasValue ? "." + revision.Value.ToString(CultureInfo.InvariantCulture) : "") : Unknown)
                + Lang.TranslateChinese("；系统 ") + (Environment.Is64BitOperatingSystem ? Lang.TranslateChinese("64 位") : Lang.TranslateChinese("32 位"))
                + Lang.TranslateChinese("；Pavise 进程 ") + (Environment.Is64BitProcess ? Lang.TranslateChinese("64 位") : Lang.TranslateChinese("32 位"));
        }

        private static string ReadTopology()
        {
            bool multiGroup = CpuTopology.MultiGroup;
            var parts = new List<string>();
            parts.Add(Lang.TranslateChinese("多处理器组=") + (multiGroup ? Lang.TranslateChinese("是") : Lang.TranslateChinese("未检测到")));
            parts.Add(Lang.TranslateChinese("拓扑来源一致性=") + (CpuTopology.TopologySourcesAgree ? Lang.TranslateChinese("通过") : Lang.TranslateChinese("未通过／未确认")));
            if (CpuTopology.PhysicalCoreCount <= 0 || CpuTopology.AllMask == 0)
            {
                parts.Add(Lang.TranslateChinese("核心类别、SMT 与缓存域未确认"));
                return string.Join("；", parts.ToArray());
            }
            parts.Add(Lang.TranslateChinese("混合核心=") + (CpuTopology.CoreTypesConfirmed ? (CpuTopology.Hybrid ? Lang.TranslateChinese("已确认") : Lang.TranslateChinese("仅确认一种核心类型")) : Lang.TranslateChinese("未确认")));
            parts.Add(Lang.TranslateChinese("核心类型证据=") + CpuTopology.CoreTypeDetail);
            parts.Add(Lang.TranslateChinese("非对称缓存=") + (CpuTopology.AsymCache ? Lang.TranslateChinese("已检测到") : Lang.TranslateChinese("未检测到")));
            if (multiGroup)
            {
                parts.Add(Lang.TranslateChinese("现有位掩码仅覆盖有限处理器组，不导出为全机效率核、SMT 或 CCD 结论"));
                return string.Join("；", parts.ToArray());
            }
            if (!CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
            {
                parts.Add(Lang.TranslateChinese("物理核数、SMT 与核心类别映射未确认，不按型号或线程数推断"));
                return string.Join("；", parts.ToArray());
            }
            ulong[] cores = CpuTopology.PhysicalCoreMasks();
            int smt = 0, efficient = 0, performance = 0;
            foreach (ulong core in cores)
            {
                if (CpuTopology.CountSetBits(core) > 1) smt++;
                if (CpuTopology.EffMask != 0 && (core & CpuTopology.EffMask) == core) efficient++;
                if (CpuTopology.PerfMask != 0 && (core & CpuTopology.PerfMask) == core) performance++;
            }
            parts.Add(Lang.TranslateChinese("观测到含 SMT 同胞线程的物理核=") + smt.ToString(CultureInfo.InvariantCulture));
            if (CpuTopology.Hybrid && CpuTopology.CoreTypesConfirmed && CpuTopology.PerfMask != 0 && CpuTopology.EffMask != 0)
                parts.Add(Lang.TranslateChinese("性能类／效率类物理核=") + performance.ToString(CultureInfo.InvariantCulture) + "／" + efficient.ToString(CultureInfo.InvariantCulture));
            else
                parts.Add(Lang.TranslateChinese("性能类／效率类物理核=") + (CpuTopology.CoreTypesConfirmed ? Lang.TranslateChinese("仅确认一种核心类型") : Lang.TranslateChinese("未确认")));
            ulong[] l3 = CpuTopology.L3Masks(), domains = CpuTopology.DieMasks();
            parts.Add(Lang.TranslateChinese("L3 缓存域数量=") + Number(l3.Length));
            parts.Add(Lang.TranslateChinese("最大单个 L3 域容量=") + (CpuTopology.L3CacheMb() > 0 ? CpuTopology.L3CacheMb().ToString(CultureInfo.InvariantCulture) + " MiB" : Unknown));
            parts.Add(Lang.TranslateChinese("已区分的处理器 Die／回退缓存分区域=") + (domains.Length >= 2 ? domains.Length.ToString(CultureInfo.InvariantCulture) : Lang.TranslateChinese("未区分出多个域")));
            return string.Join("；", parts.ToArray());
        }

        private static string ReadGpus()
        {
            GpuAdapter[] adapters = GpuInventory.Adapters();
            if (adapters == null || adapters.Length == 0) return Unknown;
            Dictionary<string, HashSet<string>> versions = ReadDriverVersions();
            var rows = new List<string>();
            for (int i = 0; i < adapters.Length; i++)
            {
                GpuAdapter gpu = adapters[i];
                if (gpu == null) continue;
                string kind = gpu.IntegratedKnown ? (gpu.Integrated ? Lang.TranslateChinese("核显／集成显卡") : Lang.TranslateChinese("独立显卡")) : Lang.TranslateChinese("集成／独立属性未确认");
                string memory = gpu.VideoMemoryBytes > 0 ? Capacity((ulong)gpu.VideoMemoryBytes) : Unknown;
                string driver = Unknown, key = GpuInventory.VenDevKey(gpu.HardwareId);
                HashSet<string> matches;
                if (key != null && versions.TryGetValue(key, out matches) && matches.Count == 1)
                    foreach (string match in matches) driver = match;
                rows.Add("GPU " + (i + 1).ToString(CultureInfo.InvariantCulture) + " " + Safe(gpu.Name)
                    + "（" + kind + Lang.TranslateChinese("；报告显存 ") + memory + Lang.TranslateChinese("；驱动 ") + driver + "）");
            }
            return rows.Count == 0 ? Unknown : string.Join("；", rows.ToArray());
        }

        private static Dictionary<string, HashSet<string>> ReadDriverVersions()
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(DisplayClass))
                {
                    if (root == null) return result;
                    foreach (string child in root.GetSubKeyNames())
                    {
                        if (child.Length != 4) continue;
                        using (RegistryKey key = root.OpenSubKey(child))
                        {
                            if (key == null) continue;
                            string id = GpuInventory.VenDevKey(key.GetValue("MatchingDeviceId") as string);
                            string version = SafeOptional(key.GetValue("DriverVersion") as string);
                            if (id == null || version.Length == 0) continue;
                            Version parsed;
                            if (!Version.TryParse(version, out parsed)) continue;
                            HashSet<string> versions;
                            if (!result.TryGetValue(id, out versions)) result.Add(id, versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                            versions.Add(parsed.ToString());
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private static string ReadPowerStatus()
        {
            PowerStatus status = SystemInformation.PowerStatus;
            string ac = status.PowerLineStatus == PowerLineStatus.Online ? Lang.TranslateChinese("交流供电")
                : status.PowerLineStatus == PowerLineStatus.Offline ? Lang.TranslateChinese("未接交流供电") : Unknown;
            BatteryChargeStatus battery = status.BatteryChargeStatus;
            string present = battery == BatteryChargeStatus.Unknown ? Unknown
                : (battery & BatteryChargeStatus.NoSystemBattery) != 0 ? Lang.TranslateChinese("系统未报告电池") : Lang.TranslateChinese("系统报告存在电池");
            return ac + "；" + present + Lang.TranslateChinese("（电池报告本身不作为笔记本类型的充分证据）");
        }

        private static string ReadActivePlan()
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out buffer) != 0 || buffer == IntPtr.Zero) return Unknown;
                Guid id = (Guid)Marshal.PtrToStructure(buffer, typeof(Guid));
                string standard = StandardPlan(id);
                if (standard != null) return standard;
                return PowerPlan.ManagedPlanIsActive ? Lang.TranslateChinese("Pavise 托管计划") : Lang.TranslateChinese("其他现有／OEM／自定义计划（不导出名称和标识）");
            }
            finally { if (buffer != IntPtr.Zero) LocalFree(buffer); }
        }

        private static string ReadPlanChoice()
        {
            if (PowerPlan.HandheldBypass) return Lang.TranslateChinese("已识别为掌机：不切换、不创建或维护托管电源计划，交给系统和厂商软件管理");
            if (PowerPlan.NoSwitchSelected) return Lang.TranslateChinese("不切换电源计划");
            if (PowerPlan.AutomaticSelection)
                return Lang.TranslateChinese("由 Pavise 调度；按 CPU 架构和智能/竞技场景选择托管计划，工作站/多处理器组/多插槽使用通用计划，电池沿用本机 DC 基准");
            Guid id;
            string standard = Guid.TryParse(PowerPlan.EffectivePlanId, out id) ? StandardPlan(id) : null;
            return Lang.TranslateChinese("用户指定：") + (standard ?? Lang.TranslateChinese("现有计划（不导出自定义名称和标识）"));
        }

        private static string StandardPlan(Guid id)
        {
            if (id == Balanced) return Lang.TranslateChinese("Windows 平衡");
            if (id == HighPerformance) return Lang.TranslateChinese("Windows 高性能");
            if (id == PowerSaver) return Lang.TranslateChinese("Windows 节能");
            if (id == Ultimate) return Lang.TranslateChinese("Windows 卓越性能");
            return null;
        }

        private static string Number(int value) { return value > 0 ? value.ToString(CultureInfo.InvariantCulture) : Unknown; }
        private static string Capacity(ulong bytes)
        { return bytes > 0 ? (bytes / 1073741824.0).ToString("0.##", CultureInfo.InvariantCulture) + " GiB" : Unknown; }
        private static string Safe(string value)
        { string clean = SafeOptional(value); return clean.Length == 0 ? Unknown : clean; }
        private static string SafeOptional(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var clean = new StringBuilder();
            foreach (char c in value)
            {
                if (clean.Length >= 180) break;
                clean.Append(char.IsControl(c) ? ' ' : c);
            }
            return clean.ToString().Trim();
        }

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
