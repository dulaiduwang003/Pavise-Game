// @author bdth 2074055628@qq.com
// 文件用途 一键中断体检 自己造负载 当场扫一次 只做初筛 不下结论
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed class IrqCheckupDevice
    {
        public string Name = "";
        public string InstanceId = "";
        public string StatsDriver = "";
        public double P99Us;
        public double P99LoUs;
        public long Dpc;
        public ulong CpuMask;
        public bool OnGameCores;
        public bool OverGate;
        public bool MultiMessageRisk;
        public bool CompletionFollowsIssuer;

        public ulong SuggestMask;
        public string NoSuggestReason = "";
        public bool SuggestSinglePhysical;
        public bool SuggestSharesBackground;
    }

    internal sealed class IrqCheckupResult
    {
        public bool Ok;
        public string Error = "";
        public int Seconds;
        public uint EventsLost;
        public ulong GameMask;
        public readonly List<IrqCheckupDevice> Devices = new List<IrqCheckupDevice>();
        public int OverGateCount;
        public int OnGameCoreCount;
        public int SuggestCount;
        public bool Cancelled;
        public int Elapsed;
    }

    internal static class IrqCheckup
    {
        internal const int DefaultSeconds = 300;
        internal static double GateUs { get { return IrqVerdict.MinMaxUs; } }

        internal static int ClampSeconds(int seconds)
        {
            if (seconds < 10) return DefaultSeconds;
            if (seconds > DefaultSeconds) return DefaultSeconds;
            return seconds;
        }

        private static int busy;
        public static bool Busy { get { return Thread.VolatileRead(ref busy) != 0; } }

        private static volatile bool cancel;
        public static void Cancel() { cancel = true; }

        public static IrqCheckupResult Run(int seconds, Action<int> progress)
        {
            var r = new IrqCheckupResult();
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            { r.Error = Lang.T("irqcheck.busy"); return r; }
            try
            {
                seconds = ClampSeconds(seconds);
                r.Seconds = seconds;
                if (!Native.IsElevated()) { r.Error = Lang.T("irqmove.needadmin"); return r; }
                if (InterruptAttribution.ProbeOwnedElsewhere())
                { r.Error = Lang.T("irqcheck.probebusy"); return r; }

                cancel = false;
                var load = new LoadGen();
                var ia = new InterruptAttribution();
                InterruptAttributionResult raw = null;
                try
                {
                    load.Start();
                    Thread.Sleep(3000);
                    if (!ia.Start()) { r.Error = InterruptAttribution.StartFailureText(ia); return r; }
                    for (int i = 0; i < seconds && !cancel; i++)
                    {
                        Thread.Sleep(1000);
                        r.Elapsed = i + 1;
                        if (progress != null) try { progress(i + 1); } catch { }
                    }
                    r.Cancelled = cancel;
                    raw = ia.Stop();
                }
                catch (Exception ex) { r.Error = ex.GetType().Name; }
                finally
                {
                    try { ia.Stop(); } catch { }
                    try { load.Stop(); } catch { }
                }

                if (raw != null) r.EventsLost = raw.EventsLost;
                if (raw == null || !raw.Ok || raw.Drivers.Count == 0)
                {
                    if (r.Error.Length == 0)
                        r.Error = raw != null && raw.Error.Length > 0 ? raw.Error : Lang.T("irqmove.nodata");
                    return r;
                }

                Build(r, raw);
                r.Ok = true;
                Logger.Log(Lang.F("log.irqcheckup.1", r.Seconds, r.Devices.Count,
                    r.OnGameCoreCount, r.OverGateCount));
                return r;
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        private static void Build(IrqCheckupResult r, InterruptAttributionResult raw)
        {
            r.GameMask = CpuTopology.StrictBoostMask;
            if (r.GameMask == 0) r.GameMask = CpuTopology.AllMask;

            var scan = new IrqScanResult();
            scan.Ok = true;
            scan.Seconds = r.Seconds;
            scan.EventsLost = raw.EventsLost;
            scan.BuffersLost = raw.BuffersLost;
            foreach (DriverInterrupt d in raw.Drivers)
            {
                if (d == null || d.Dpc <= 0) continue;
                var c = new IrqCandidate();
                c.Driver = d.Driver ?? "?";
                c.Dpc = d.Dpc;
                c.TotalUs = d.DpcTotalUs;
                c.MaxUs = d.DpcMaxUs;
                c.Over500Us = d.DpcOver500Us;
                c.Over1Ms = d.DpcOver1Ms;
                c.CpuMask = d.CpuMask;
                c.Buckets = d.DpcBuckets;
                scan.Candidates.Add(c);
            }

            List<IrqDevice> devices = IrqDeviceInventory.Enumerate();
            IrqDeviceInventory.AttachScan(devices, scan);
            IrqDeviceInventory.MarkOwnership(devices);

            foreach (IrqDevice d in devices)
            {
                if (d.Dpc <= 0) continue;
                double p99 = PercentileFor(scan, d);
                double p99lo = PercentileLowerFor(scan, d);
                var e = new IrqCheckupDevice();
                e.Name = d.Name;
                e.InstanceId = d.InstanceId;
                e.StatsDriver = d.FrameworkStats ? d.StatsDriver : d.Service;
                e.P99Us = p99;
                e.P99LoUs = p99lo;
                e.Dpc = d.Dpc;
                e.CpuMask = d.SeenOnCpus;
                e.OnGameCores = d.SeenOnCpus != 0 && (d.SeenOnCpus & r.GameMask) != 0;
                e.OverGate = p99lo >= GateUs;
                e.MultiMessageRisk = d.MultiMessageRisk;
                e.CompletionFollowsIssuer = d.CompletionFollowsIssuer;
                Suggest(e, r.GameMask);
                if (e.OnGameCores) r.OnGameCoreCount++;
                if (e.OverGate) r.OverGateCount++;
                if (e.SuggestMask != 0) r.SuggestCount++;
                r.Devices.Add(e);
            }

            r.Devices.Sort(delegate (IrqCheckupDevice a, IrqCheckupDevice b)
            {
                if (a.OnGameCores != b.OnGameCores) return b.OnGameCores.CompareTo(a.OnGameCores);
                return b.P99Us.CompareTo(a.P99Us);
            });
        }

        private static void Suggest(IrqCheckupDevice e, ulong gameMask)
        {
            ulong all = CpuTopology.AllMask;
            if (all == 0) { e.NoSuggestReason = Lang.T("irqcheck.nosug.topology"); return; }

            if (!e.OnGameCores) { e.NoSuggestReason = Lang.T("irqcheck.nosug.nooverlap"); return; }
            if (e.CompletionFollowsIssuer) { e.NoSuggestReason = Lang.T("irqcheck.nosug.storage"); return; }

            ulong away = all & ~gameMask;
            if (away == 0) { e.NoSuggestReason = Lang.T("irqcheck.nosug.nospare"); return; }

            ulong spread = CpuTopology.OnePerPhysicalIn(away);
            if (CpuTopology.PhysicalCoresIn(away) >= 2 && spread != 0) away = spread;

            ulong keep = IrqRelocate.Sanitize(away);
            if (keep == 0) { e.NoSuggestReason = Lang.T("irqcheck.nosug.nospare"); return; }
            e.SuggestMask = keep;
            e.SuggestSinglePhysical = CpuTopology.PhysicalCoresIn(keep) <= 1;
            e.SuggestSharesBackground = (keep & CpuTopology.ThrottleMask) != 0;
        }

        private static IrqCandidate SourceOf(IrqScanResult scan, IrqDevice d)
        {
            foreach (IrqCandidate c in scan.Candidates)
                if (Math.Abs(c.MaxUs - d.MaxUs) <= 0.001 && c.Dpc == d.Dpc) return c;
            return null;
        }

        private static double PercentileFor(IrqScanResult scan, IrqDevice d)
        {
            IrqCandidate c = SourceOf(scan, d);
            return c != null ? c.ApproxPercentileUs(0.99) : d.MaxUs;
        }

        private static double PercentileLowerFor(IrqScanResult scan, IrqDevice d)
        {
            IrqCandidate c = SourceOf(scan, d);
            return c != null ? c.PercentileLowerUs(0.99) : 0;
        }
    }
}
