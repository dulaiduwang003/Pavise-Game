// @author bdth 2074055628@qq.com
// 文件用途 量出哪个驱动的中断最长 并把设备的中断钉到指定的几个核上

using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed class IrqCandidate
    {
        public string Driver = "";
        public long Dpc;
        public double TotalUs;
        public double MaxUs;
        public long Over500Us;
        public long Over1Ms;
        public ulong CpuMask;
        public long[] Buckets;
        public double ApproxPercentileUs(double q) { return IrqPercentile.ApproxUs(Buckets, MaxUs, q); }
        public double PercentileLowerUs(double q) { return IrqPercentile.LowerUs(Buckets, q); }
        public readonly List<string> DeviceIds = new List<string>();
        public string Why = "";
        public bool Actionable { get { return DeviceIds.Count > 0; } }
        public bool Moved;
    }

    internal sealed class IrqScanResult
    {
        public bool Ok;
        public string Error = "";
        public int Seconds;
        public uint EventsLost;
        public uint BuffersLost;
        public bool Lossy { get { return EventsLost > 0 || BuffersLost > 0; } }
        public readonly List<IrqCandidate> Candidates = new List<IrqCandidate>();
    }

    internal static class IrqRelocate
    {
        private static readonly IrqAffinityEngine engine =
            new IrqAffinityEngine("IrqRelocateOn", "IrqReloc_", Lang.T("irqmove.logprefix"));

        internal const double MinMaxUsToOffer = 200.0;

        public static bool Applied { get { return engine.EnabledByPavise; } }
        public static bool HasResidue { get { return engine.HasResidue; } }
        public static bool RebootedSinceWrite(string deviceId) { return engine.RebootedSinceWrite(deviceId); }
        public static IrqRebootState GetRebootState(string deviceId) { return engine.GetRebootState(deviceId); }

        public static ulong AutoMask()
        {
            ulong m = CpuTopology.InterruptMask;
            if (m == 0 || m == CpuTopology.AllMask) m = CpuTopology.ThrottleMask;
            return m;
        }

        internal static ulong Sanitize(ulong mask)
        {
            ulong all = CpuTopology.AllMask;
            if (all != 0) mask &= all;
            if (mask == 0 || mask == all) return 0;
            return mask;
        }

        public static bool ApplyDevice(string deviceId, ulong mask)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;
            ulong keep = Sanitize(mask);
            if (keep == 0) return false;
            if (!Native.IsElevated()) { Logger.Log(Lang.T("irqmove.needadmin")); return false; }
            foreach (string g in OwnedByOtherTweaks())
                if (string.Equals(g, deviceId, StringComparison.OrdinalIgnoreCase))
                { Logger.Log(Lang.T("irqmove.ownedelsewhere")); return false; }
            return engine.Enable(new List<string>(new string[] { deviceId }), keep);
        }

        public static IrqScanResult Scan(int seconds)
        {
            var r = new IrqScanResult();
            if (seconds < 5) seconds = 15;
            if (seconds > 60) seconds = 60;
            r.Seconds = seconds;

            var ia = new InterruptAttribution();
            InterruptAttributionResult raw = null;
            try
            {
                if (!ia.Start()) { r.Error = InterruptAttribution.StartFailureText(ia); return r; }
                Thread.Sleep(seconds * 1000);
                raw = ia.Stop();
            }
            catch (Exception ex) { r.Error = ex.GetType().Name; }
            finally { try { ia.Stop(); } catch { } }

            if (raw != null) { r.EventsLost = raw.EventsLost; r.BuffersLost = raw.BuffersLost; }
            if (raw == null || raw.Drivers == null || raw.Drivers.Count == 0)
            {
                if (r.Error.Length == 0) r.Error = Lang.T("irqmove.nodata");
                return r;
            }

            List<string> touched = engine.TouchedDevices();
            foreach (DriverInterrupt d in raw.Drivers)
            {
                var c = new IrqCandidate();
                c.Driver = d.Driver ?? "?";
                c.Dpc = d.Dpc;
                c.TotalUs = d.DpcTotalUs;
                c.MaxUs = d.DpcMaxUs;
                c.Over500Us = d.DpcOver500Us;
                c.Over1Ms = d.DpcOver1Ms;
                c.CpuMask = d.CpuMask;
                c.Buckets = d.DpcBuckets;

                if (d.DpcMaxUs < MinMaxUsToOffer)
                    c.Why = Lang.F("irqmove.tooshort", d.DpcMaxUs.ToString("F0"), MinMaxUsToOffer.ToString("F0"));
                else
                {
                    DriverDeviceMatch m = DriverDeviceResolver.Resolve(c.Driver);
                    if (!m.Actionable) c.Why = m.Why;
                    else
                    {
                        List<string> owned = OwnedByOtherTweaks();
                        foreach (string id in m.DeviceIds)
                        {
                            bool taken = false;
                            foreach (string g in owned)
                                if (string.Equals(g, id, StringComparison.OrdinalIgnoreCase)) { taken = true; break; }
                            if (!taken) c.DeviceIds.Add(id);
                            else c.Why = Lang.T("irqmove.ownedelsewhere");
                        }
                        foreach (string id in c.DeviceIds)
                            foreach (string t in touched)
                                if (string.Equals(t, id, StringComparison.OrdinalIgnoreCase)) c.Moved = true;
                    }
                }
                r.Candidates.Add(c);
            }
            r.Candidates.Sort(delegate (IrqCandidate a, IrqCandidate b)
            { return b.MaxUs.CompareTo(a.MaxUs); });
            r.Ok = true;
            return r;
        }

        public static bool Apply(IrqCandidate c)
        {
            if (c == null || !c.Actionable) return false;
            ulong keep = Sanitize(AutoMask());
            if (keep == 0) return false;
            if (!Native.IsElevated()) { Logger.Log(Lang.T("irqmove.needadmin")); return false; }
            List<string> owned = OwnedByOtherTweaks();
            foreach (string id in c.DeviceIds)
                foreach (string g in owned)
                    if (string.Equals(g, id, StringComparison.OrdinalIgnoreCase))
                    { Logger.Log(Lang.T("irqmove.ownedelsewhere")); return false; }
            return engine.Enable(c.DeviceIds, keep);
        }

        public static bool Revert() { return engine.Disable(null); }

        public static List<string> OwnedElsewhere() { return OwnedByOtherTweaks(); }

        public static void HealFromCrash()
        {
            try { if (engine.HasResidue && !engine.EnabledByPavise) Revert(); } catch { }
        }

        // 挪核重启回来的用户十有八九不知道还要打一局才能看到实测效果 启动时主动说一声
        //   只在「已重启 且 重启后一局都没打过」时提示 打过局说明观测已经在路上 不用催
        public static void NotifyPendingVerification()
        {
            try
            {
                if (!engine.EnabledByPavise) return;
                List<string> touched = engine.TouchedDevices();
                if (touched.Count == 0) return;
                bool rebooted = false;
                foreach (string id in touched)
                    if (engine.RebootedSinceWrite(id)) { rebooted = true; break; }
                if (!rebooted) return;
                string boot = IrqAffinityEngine.BootStamp();
                foreach (IrqSessionRecord rec in IrqSessionLedger.Load())
                    if (rec != null && IrqAffinityEngine.SameBoot(rec.BootStamp, boot))
                        return;
                Logger.Log(Lang.T(IrqSessionProbe.EnabledSetting
                    ? "log.irqrelocate.1" : "log.irqrelocate.2"));
            }
            catch { }
        }

        private static List<string> OwnedByOtherTweaks()
        {
            var owned = new List<string>();
            try
            {
                if (NetworkAffinityTweak.EnabledByPavise)
                    foreach (string id in NetworkAffinityTweak.EnumerateNicDeviceIds())
                        if (!Has(owned, id)) owned.Add(id);
            }
            catch { }
            return owned;
        }

        private static bool Has(List<string> list, string id)
        {
            if (id == null || id.Length == 0) return true;
            foreach (string had in list)
                if (string.Equals(had, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static string MaskText(ulong mask)
        {
            if (mask == 0) return "?";
            var sb = new System.Text.StringBuilder();
            int run = -1;
            for (int i = 0; i <= 64; i++)
            {
                bool on = i < 64 && (mask & (1UL << i)) != 0;
                if (on && run < 0) run = i;
                else if (!on && run >= 0)
                {
                    if (sb.Length > 0) sb.Append(',');
                    if (i - 1 == run) sb.Append(run);
                    else sb.Append(run).Append('-').Append(i - 1);
                    run = -1;
                }
            }
            return sb.ToString();
        }
    }
}
