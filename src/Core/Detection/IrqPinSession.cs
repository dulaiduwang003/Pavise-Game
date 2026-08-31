// 文件用途 一次已完成的观测 绝不和当前桌面负载或者预设混在一起
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal sealed class IrqPinSession
    {
        internal string GameName = "";
        internal long StartUtcTicks;
        internal int DurationSeconds;
        internal bool Available;
        internal IrqSessionExclusion Exclusion;
        internal ulong GameMask, SeenMask;
        internal IrqDriverRecord Driver;
        internal readonly Dictionary<int, double> Loads = new Dictionary<int, double>();
        internal double MinimumCoverage, MaximumCoverage;

        internal static IrqPinSession FromLatest(IList<IrqSessionRecord> records,
            IrqDevice device, string boot, string topology, Func<string, string> driverVersion)
        {
            var view = new IrqPinSession();
            if (records == null || records.Count == 0) return view;
            IrqSessionRecord record = records[records.Count - 1];
            if (record == null) return view;
            view.GameName = record.GameName;
            view.StartUtcTicks = record.StartUtcTicks;
            view.DurationSeconds = record.DurationSeconds;
            view.Exclusion = record.DisplayExclusion(boot, topology);
            // 没有拓扑身份 老的 CPU 编号没法安全映射
            if (string.IsNullOrEmpty(record.TopologyStamp))
                view.Exclusion = IrqSessionExclusion.DifferentTopology;
            if (view.Exclusion != IrqSessionExclusion.None) return view;

            view.Available = true;
            view.GameMask = (record.GameMask & ~record.SystemMask) == 0 ? record.GameMask : 0;
            if (record.CoreLoadWindowTicks > 0 && IrqSessionLedger.ValidCoreLoads(record))
                foreach (IrqCoreLoadRecord load in record.CoreLoads)
                {
                    double coverage = 100.0 * load.ObservedTicks / record.CoreLoadWindowTicks;
                    if (view.Loads.Count == 0) view.MinimumCoverage = coverage;
                    view.MinimumCoverage = Math.Min(view.MinimumCoverage, coverage);
                    view.MaximumCoverage = Math.Max(view.MaximumCoverage, coverage);
                    view.Loads.Add(load.Cpu, load.AveragePercent);
                }
            if (device == null) return view;

            string expected = device.FrameworkStats ? device.StatsDriver
                : device.Verdict == null ? null : device.Verdict.Driver;
            foreach (IrqDriverRecord driver in record.Drivers)
            {
                if (driver == null || driver.Dpc <= 0) continue;
                bool match = !string.IsNullOrEmpty(expected)
                    ? string.Equals(expected, driver.Driver, StringComparison.OrdinalIgnoreCase)
                    : !string.IsNullOrEmpty(device.Service) && string.Equals(device.Service,
                        Path.GetFileNameWithoutExtension(driver.Driver), StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                string currentVersion = null;
                try { if (driverVersion != null) currentVersion = driverVersion(driver.Driver); } catch { }
                if (string.IsNullOrEmpty(currentVersion) || !string.Equals(currentVersion,
                    driver.DriverVersion, StringComparison.Ordinal)) continue;
                if (view.Driver != null)
                {
                    view.Driver = null;
                    view.SeenMask = 0;
                    return view; // Ambiguous driver identities: do not guess placement.
                }
                view.Driver = driver;
                view.SeenMask = driver.MaskTruncated ? 0 : driver.CpuMask & record.SystemMask;
            }
            return view;
        }
    }
}
