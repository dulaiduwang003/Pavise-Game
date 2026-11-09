using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {
        private struct MemoryStatusEx
        {
            public uint Length;

            public uint MemoryLoad;

            public ulong TotalPhys;

            public ulong AvailPhys;

            public ulong TotalPageFile;

            public ulong AvailPageFile;

            public ulong TotalVirtual;

            public ulong AvailVirtual;

            public ulong AvailExtendedVirtual;
        }

        private struct SystemPowerStatus
        {
            public byte AcLineStatus;

            public byte BatteryFlag;

            public byte BatteryLifePercent;

            public byte SystemStatusFlag;

            public int BatteryLifeTime;

            public int BatteryFullLifeTime;
        }

        public const string EvMeasuredLocal = "本机实测";

        public const string EvMeasuredBench = "台架实测";

        public const string EvMechanism = "机制明确";

        public const string EvUnverified = "未验证";

        public const byte EnergySaverOn = 1;

        public const int EcoQosFullBuild = 22000;

        private const string X3dServiceKey = "SYSTEM\\CurrentControlSet\\Services\\amd3dvcache";

        public static Func<List<string>> LibraryPaths;

        public static string EvidenceText(string evidence)
        {
            switch (evidence)
            {
            case "本机实测":
                return Lang.T("audit.evidence.local");
            case "台架实测":
                return Lang.T("audit.evidence.bench");
            case "机制明确":
                return Lang.T("audit.evidence.mechanism");
            default:
                return Lang.T("audit.evidence.unverified");
            }
        }

        public static string PercentText(double rate)
        {
            return (rate * 100.0).ToString("F2") + "%";
        }

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        public static bool TryEnergySaver(out bool on)
        {
            on = false;
            try
            {
                SystemPowerStatus status;
                if (!GetSystemPowerStatus(out status))
                {
                    return false;
                }
                if (status.SystemStatusFlag > 1)
                {
                    return false;
                }
                on = status.SystemStatusFlag == 1;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryMemory(out double usedRatio, out double totalGb, out double availGb)
        {
            usedRatio = 0.0;
            totalGb = 0.0;
            availGb = 0.0;
            try
            {
                MemoryStatusEx buffer = default(MemoryStatusEx);
                buffer.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                if (!GlobalMemoryStatusEx(ref buffer) || buffer.TotalPhys == 0)
                {
                    return false;
                }
                totalGb = (double)buffer.TotalPhys / 1073741824.0;
                availGb = (double)buffer.AvailPhys / 1073741824.0;
                usedRatio = 1.0 - (double)buffer.AvailPhys / (double)buffer.TotalPhys;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool RefreshRateIsBest(int current, int best)
        {
            if (current > 0 && best > 0)
            {
                return current >= best - 1;
            }
            return true;
        }

        private static string WindowsText()
        {
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"))
                {
                    if (registryKey == null)
                    {
                        return Environment.OSVersion.VersionString;
                    }
                    string getValueOffset = registryKey.GetValue("ProductName") as string;
                    string getValueOffset2 = registryKey.GetValue("DisplayVersion") as string;
                    object value = registryKey.GetValue("CurrentBuildNumber");
                    object value2 = registryKey.GetValue("UBR");
                    string windowsTextResult = (string.IsNullOrEmpty(getValueOffset) ? "Windows" : getValueOffset);
                    int result;
                    if (value != null && int.TryParse(value.ToString(), out result) && result >= 22000)
                    {
                        windowsTextResult = windowsTextResult.Replace("Windows 10", "Windows 11");
                    }
                    if (!string.IsNullOrEmpty(getValueOffset2))
                    {
                        windowsTextResult = windowsTextResult + " " + getValueOffset2;
                    }
                    if (value != null)
                    {
                        object instance = windowsTextResult;
                        windowsTextResult = string.Concat(instance, " ", value, (value2 != null) ? ("." + value2) : "");
                    }
                    return windowsTextResult;
                }
            }
            catch
            {
                return Environment.OSVersion.VersionString;
            }
        }

        public static int WindowsBuild()
        {
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"))
                {
                    if (registryKey != null)
                    {
                        object value = registryKey.GetValue("CurrentBuildNumber");
                        int result;
                        if (value != null && int.TryParse(value.ToString(), out result))
                        {
                            return result;
                        }
                    }
                }
            }
            catch
            {
            }
            try
            {
                return Environment.OSVersion.Version.Build;
            }
            catch
            {
                return 0;
            }
        }

        private static bool GameDvrOn()
        {
            try
            {
                using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("System\\GameConfigStore"))
                {
                    if (registryKey == null)
                    {
                        return false;
                    }
                    object value = registryKey.GetValue("GameDVR_Enabled");
                    return value == null || Convert.ToInt32(value) != 0;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool PageFileLooksDisabled(double pageFileGb)
        {
            return pageFileGb < 0.5;
        }

        public static AuditReport Collect(int measureWindowMs, Func<bool> cancelIrq = null, Action<float, string> progress = null)
        {
            if (cancelIrq != null && cancelIrq())
            {
                return null;
            }
            AuditReport auditReport = new AuditReport();
            auditReport.MeasureWindowMs = measureWindowMs;
            object progressGate = new object();
            float cpuProgress = 0f;
            float irqProgress = 0f;
            Action reportSampling = delegate
            {
                if (progress != null)
                {
                    float irqProgressLimit = Math.Min(cpuProgress, irqProgress);
                    int roundedValue = (int)Math.Ceiling((double)measureWindowMs * (1.0 - (double)irqProgressLimit) / 1000.0);
                    progress(irqProgressLimit * 0.9f, Lang.F("audit.phase.sampling", roundedValue));
                }
            };
            Task<GpuIrqResult> task = Task.Factory.StartNew(delegate
            {
                try
                {
                    return GpuIrqCheck.Inspect(measureWindowMs, cancelIrq, delegate(float value)
                    {
                        lock (progressGate)
                        {
                            irqProgress = value;
                            reportSampling();
                        }
                    });
                }
                catch (Exception ex)
                {
                    return new GpuIrqResult
                    {
                        Outcome = GpuIrqOutcome.Failed,
                        Detail = ex.GetType().Name
                    };
                }
                finally
                {
                    lock (progressGate)
                    {
                        irqProgress = 1f;
                        reportSampling();
                    }
                }
            });
            double cpuBusy = 0.0;
            double[] measureLoadResult = null;
            try
            {
                measureLoadResult = DpcSampler.MeasureLoad(measureWindowMs, out cpuBusy, cancelIrq, delegate(float value)
                {
                    lock (progressGate)
                    {
                        cpuProgress = value;
                        reportSampling();
                    }
                });
            }
            catch
            {
            }
            auditReport.GpuIrq = task.Result;
            if (cancelIrq != null && cancelIrq())
            {
                return null;
            }
            auditReport.MeasureOk = measureLoadResult != null;
            auditReport.CpuBusy = cpuBusy;
            if (progress != null)
            {
                progress(0.91f, Lang.T("audit.phase.capability"));
            }
            if (!Assemble(auditReport, cancelIrq, progress))
            {
                return null;
            }
            return auditReport;
        }

        public static AuditReport Refresh(AuditReport previous, GpuIrqResult irqFix)
        {
            if (previous == null)
            {
                return null;
            }
            AuditReport auditReport = new AuditReport();
            auditReport.MeasureWindowMs = previous.MeasureWindowMs;
            auditReport.MeasureOk = previous.MeasureOk;
            auditReport.CpuBusy = previous.CpuBusy;
            auditReport.GpuIrq = GpuIrqCheck.AfterFix(previous.GpuIrq, irqFix);
            if (!Assemble(auditReport, null, null))
            {
                return null;
            }
            return auditReport;
        }

        private static bool Assemble(AuditReport report, Func<bool> cancelled, Action<float, string> progress)
        {
            int current;
            int best;
            DisplayGuard.QueryRefreshRates(out current, out best);
            Facts facts = Gather();
            BuildCapability(report, facts);
            BuildMachine(report, facts, report.CpuBusy, current, best);
            BuildBypassIo(report, facts);
            BuildHardwareHealth(report, facts);
            BuildInputChain(report, facts);
            if (cancelled != null && cancelled())
            {
                return false;
            }
            if (progress != null)
            {
                progress(0.96f, Lang.T("audit.phase.persistent"));
            }
            BuildPersistent(report, facts);
            BuildGpuInterrupt(report);
            if (GpuIrqCheck.HasResidue || IrqRelocate.HasResidue || IrqPriorityTweak.HasResidue)
            {
                report.Persistent.Add(new AuditRow
                {
                    Name = Lang.T("audit.irq.saved"),
                    Value = Lang.T("audit.irq.owned"),
                    Note = Lang.T("irqcheck.restore.hint"),
                    Evidence = "机制明确",
                    FixKey = "gpuirqrestore"
                });
            }
            if (cancelled != null && cancelled())
            {
                return false;
            }
            if (progress != null)
            {
                progress(0.99f, Lang.T("audit.phase.verdict"));
            }
            BuildVerdicts(report, facts, current, best);
            return true;
        }
    }
}
