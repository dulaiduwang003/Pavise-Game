// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal enum GpuIrqPhase { None = 0, Pending = 1, Applied = 2, Clear = 3, NoRegion = 4, RolledBack = 5, Skipped = 6 }

    internal enum GpuIrqOutcome
    {
        Failed, Cancelled, AwaitingReboot, NoEvidence, NoRegion, NoTarget, Clear, Scheduled, Verified, RolledBack, Skipped, Restored,
        NeedsPlacement, NeedsVerification
    }

    internal sealed class GpuIrqSample
    {
        public string DeviceId = "", Name = "", Driver = "", DriverVersion = "";
        public ulong Seen;
        public long Events;
        public double P99Us, MaxUs;
        public bool MultiMessage, ExternalPin, ConfigurationKnown = true;
    }

    internal sealed class SlowDriver
    {
        public string Driver = "", Device = "";
        public double MaxUs, P99Us;
        public long Over1Ms, Events;
    }

    internal sealed class GpuIrqState
    {
        public GpuIrqPhase Phase;
        public string DeviceId = "", DeviceName = "", DriverVersion = "", Reason = "";
        public ulong Target, Region, Seen;
        public double BaselineP99;
        public long CheckedTicks;

        internal string Encode()
        {
            return "1|" + ((int)Phase).ToString(CultureInfo.InvariantCulture)
                + "|" + Uri.EscapeDataString(DeviceId ?? "") + "|" + Uri.EscapeDataString(DeviceName ?? "")
                + "|" + Uri.EscapeDataString(DriverVersion ?? "") + "|" + Uri.EscapeDataString(Reason ?? "")
                + "|" + Target.ToString("X") + "|" + Region.ToString("X") + "|" + Seen.ToString("X")
                + "|" + BaselineP99.ToString("R", CultureInfo.InvariantCulture)
                + "|" + CheckedTicks.ToString(CultureInfo.InvariantCulture);
        }

        internal static GpuIrqState Parse(string raw)
        {
            var s = new GpuIrqState();
            if (string.IsNullOrEmpty(raw)) return s;
            string[] p = raw.Split('|');
            int phase;
            ulong target, region, seen;
            double baseline;
            long ticks;
            if (p.Length != 11 || p[0] != "1"
                || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out phase)
                || phase < 0 || phase > (int)GpuIrqPhase.Skipped
                || !ulong.TryParse(p[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out target)
                || !ulong.TryParse(p[7], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out region)
                || !ulong.TryParse(p[8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out seen)
                || !double.TryParse(p[9], NumberStyles.Float, CultureInfo.InvariantCulture, out baseline)
                || !long.TryParse(p[10], NumberStyles.None, CultureInfo.InvariantCulture, out ticks)) return s;
            try
            {
                s.DeviceId = Uri.UnescapeDataString(p[2]);
                s.DeviceName = Uri.UnescapeDataString(p[3]);
                s.DriverVersion = Uri.UnescapeDataString(p[4]);
                s.Reason = Uri.UnescapeDataString(p[5]);
            }
            catch { return new GpuIrqState(); }
            s.Phase = (GpuIrqPhase)phase;
            s.Target = target; s.Region = region; s.Seen = seen;
            s.BaselineP99 = baseline; s.CheckedTicks = ticks;
            return s;
        }
    }

    internal sealed class GpuIrqResult
    {
        public GpuIrqOutcome Outcome;
        public string Detail = "";
        public GpuIrqSample Gpu;
        public ulong Region, Target;
        public List<SlowDriver> Slow = new List<SlowDriver>();
    }

    internal static class GpuIrqCheck
    {
        internal const int CheckSeconds = 120;
        internal const long MinEvents = 300;
        internal const double SlowP99Us = 500;
        internal const int SlowLimit = 5;
        internal const string StateKey = "GpuIrqCheckV1";
        internal const string SlowKey = "GpuIrqSlowV1";
        internal const string FuseKey = "GpuIrqFuseV1";
        private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static readonly object lk = new object();
        private static readonly IrqAffinityEngine engine =
            new IrqAffinityEngine("GpuIrqAutoOn", "GpuIrqAuto_", "GPU IRQ ");

        public static bool HasResidue { get { return engine.HasResidue; } }
        public static List<string> TouchedDevices() { return engine.TouchedDevices(); }

        public static GpuIrqState LoadState() { return GpuIrqState.Parse(Settings.LoadStr(StateKey, "")); }

        public static bool AwaitingReboot(GpuIrqState state)
        {
            return state != null && state.Phase == GpuIrqPhase.Pending && RebootState(state.DeviceId) == IrqRebootState.AwaitingReboot;
        }

        internal static ulong WholeCores(ulong mask, ulong[] physical)
        {
            if (mask == 0 || physical == null) return 0;
            ulong result = 0;
            foreach (ulong core in physical) if ((core & mask) != 0) result |= core;
            return result;
        }

        internal static ulong GameRegion(AutoCorePlan plan, ulong all, ulong[] physical)
        {
            if (plan == null || !plan.Available || all == 0) return 0;
            ulong raw = plan.Exclusive & all;
            if (raw == 0 || raw == all) return 0;
            ulong region = WholeCores(raw, physical) & all;
            return region == all ? 0 : region;
        }

        internal static ulong TargetFor(ulong region, ulong all, ulong perf, bool hybrid)
        {
            if (region == 0) return 0;
            ulong outside = all & ~region;
            if (hybrid && perf != 0) outside &= perf;
            return outside;
        }

        internal static GpuIrqOutcome Evaluate(GpuIrqSample gpu, ulong region, ulong target, out string reason)
        {
            reason = "";
            if (gpu == null || gpu.Events < MinEvents || gpu.Seen == 0) return GpuIrqOutcome.NoEvidence;
            if (region == 0) return GpuIrqOutcome.NoRegion;
            if ((gpu.Seen & region) == 0) return GpuIrqOutcome.Clear;
            if (target == 0) return GpuIrqOutcome.NoTarget;
            if (!gpu.ConfigurationKnown) { reason = "irqcheck.reason.config"; return GpuIrqOutcome.Skipped; }
            if (gpu.MultiMessage) { reason = "irqcheck.reason.multimessage"; return GpuIrqOutcome.Skipped; }
            if (gpu.ExternalPin) { reason = "irqcheck.reason.externalpin"; return GpuIrqOutcome.Skipped; }
            return GpuIrqOutcome.Scheduled;
        }

        internal static bool Verify(GpuIrqSample gpu, GpuIrqState state, bool displayReset, out string reason)
        {
            if (displayReset) { reason = "irqcheck.reason.reset"; return false; }
            if ((gpu.Seen & ~state.Target) != 0) { reason = "irqcheck.reason.ignored"; return false; }
            double limit = Math.Max(state.BaselineP99 * 2, state.BaselineP99 + 100);
            if (state.BaselineP99 > 0 && gpu.P99Us > limit) { reason = "irqcheck.reason.slower"; return false; }
            reason = "";
            return true;
        }

        private static string ServiceOf(string driver)
        {
            string svc = driver ?? "";
            int dot = svc.LastIndexOf('.');
            return dot > 0 ? svc.Substring(0, dot) : svc;
        }

        internal static GpuIrqSample PickGpu(List<IrqDevice> devices, List<DriverInterrupt> drivers, string ownedDevice)
        {
            GpuIrqSample best = null;
            if (devices == null || drivers == null) return null;
            foreach (IrqDevice d in devices)
            {
                if (d == null || !string.Equals(d.ClassGuid, DisplayClass, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(d.Bus, "PCI", StringComparison.OrdinalIgnoreCase) || d.Service.Length == 0) continue;
                DriverInterrupt hit = null;
                foreach (DriverInterrupt di in drivers)
                    if (di != null && string.Equals(ServiceOf(di.Driver), d.Service, StringComparison.OrdinalIgnoreCase)) { hit = di; break; }
                if (hit == null) continue;
                var sample = new GpuIrqSample
                {
                    DeviceId = d.InstanceId, Name = d.Name, Driver = hit.Driver ?? "", DriverVersion = d.DriverVersion ?? "",
                    Seen = hit.CpuMaskTruncated ? 0 : hit.CpuMask, Events = hit.Dpc,
                    P99Us = IrqPercentile.ApproxUs(hit.DpcBuckets, hit.DpcMaxUs, 0.99), MaxUs = hit.DpcMaxUs,
                    MultiMessage = d.MultiMessageRisk, ConfigurationKnown = d.ConfigurationKnown,
                    ExternalPin = d.IsPinned && !string.Equals(d.InstanceId, ownedDevice ?? "", StringComparison.OrdinalIgnoreCase)
                };
                if (best == null || sample.Events > best.Events) best = sample;
            }
            return best;
        }

        internal static List<SlowDriver> SlowDrivers(List<DriverInterrupt> drivers, List<IrqDevice> devices)
        {
            var list = new List<SlowDriver>();
            if (drivers == null) return list;
            foreach (DriverInterrupt d in drivers)
            {
                if (d == null || string.IsNullOrEmpty(d.Driver) || DriverDeviceResolver.IsFramework(d.Driver)) continue;
                double p99 = IrqPercentile.ApproxUs(d.DpcBuckets, d.DpcMaxUs, 0.99);
                if (d.DpcOver1Ms <= 0 && (d.Dpc < 100 || p99 < SlowP99Us)) continue;
                string device = "";
                if (devices != null)
                    foreach (IrqDevice dev in devices)
                        if (dev != null && string.Equals(dev.Service, ServiceOf(d.Driver), StringComparison.OrdinalIgnoreCase))
                        { device = dev.Name; break; }
                list.Add(new SlowDriver { Driver = d.Driver, Device = device, MaxUs = d.DpcMaxUs, P99Us = p99,
                    Over1Ms = d.DpcOver1Ms, Events = d.Dpc });
            }
            list.Sort(delegate(SlowDriver a, SlowDriver b) { return b.MaxUs.CompareTo(a.MaxUs); });
            if (list.Count > SlowLimit) list.RemoveRange(SlowLimit, list.Count - SlowLimit);
            return list;
        }

        internal static string EncodeSlow(List<SlowDriver> slow, long ticks)
        {
            var parts = new List<string> { "1", ticks.ToString(CultureInfo.InvariantCulture) };
            foreach (SlowDriver s in slow)
                parts.Add(Uri.EscapeDataString(s.Driver) + "," + Uri.EscapeDataString(s.Device)
                    + "," + s.MaxUs.ToString("F0", CultureInfo.InvariantCulture)
                    + "," + s.P99Us.ToString("F0", CultureInfo.InvariantCulture)
                    + "," + s.Over1Ms.ToString(CultureInfo.InvariantCulture)
                    + "," + s.Events.ToString(CultureInfo.InvariantCulture));
            return string.Join("|", parts.ToArray());
        }

        internal static List<SlowDriver> DecodeSlow(string raw, out long ticks)
        {
            ticks = 0;
            var list = new List<SlowDriver>();
            if (string.IsNullOrEmpty(raw)) return list;
            string[] parts = raw.Split('|');
            if (parts.Length < 2 || parts[0] != "1"
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks)) { ticks = 0; return list; }
            for (int i = 2; i < parts.Length; i++)
            {
                string[] f = parts[i].Split(',');
                double max, p99; long over, events;
                if (f.Length != 6
                    || !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out max)
                    || !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out p99)
                    || !long.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out over)
                    || !long.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out events)) continue;
                try
                {
                    list.Add(new SlowDriver { Driver = Uri.UnescapeDataString(f[0]), Device = Uri.UnescapeDataString(f[1]),
                        MaxUs = max, P99Us = p99, Over1Ms = over, Events = events });
                }
                catch { }
            }
            return list;
        }

        public static List<SlowDriver> LoadSlow(out long ticks) { return DecodeSlow(Settings.LoadStr(SlowKey, ""), out ticks); }

        private static string FuseToken(string deviceId, string version)
        {
            return (deviceId ?? "").ToUpperInvariant() + "#" + (version ?? "");
        }

        internal static bool IsFused(string deviceId, string version)
        {
            string token = FuseToken(deviceId, version);
            foreach (string line in Settings.LoadStr(FuseKey, "").Split('\n'))
                if (string.Equals(line.Trim(), token, StringComparison.Ordinal)) return true;
            return false;
        }

        private static void AddFuse(string deviceId, string version)
        {
            if (IsFused(deviceId, version)) return;
            string raw = Settings.LoadStr(FuseKey, "");
            Settings.SaveStr(FuseKey, raw.Length == 0 ? FuseToken(deviceId, version) : raw + "\n" + FuseToken(deviceId, version));
        }

        private static bool SameDevice(GpuIrqSample gpu, GpuIrqState state)
        {
            return gpu != null && string.Equals(gpu.DeviceId, state.DeviceId, StringComparison.OrdinalIgnoreCase);
        }

        // 体检只采集证据，不执行写入或回滚。
        public static GpuIrqResult Inspect(int windowMs, Func<bool> cancelled, Action<float> progress = null)
        {
            lock (lk)
            {
                var r = new GpuIrqResult();
                if (cancelled != null && cancelled()) { r.Outcome = GpuIrqOutcome.Cancelled; return r; }
                if (!Elevated()) { r.Detail = Lang.T("irqcheck.needadmin"); return r; }
                if (CpuTopology.MultiGroup) { r.Detail = Lang.T("irq.exact.unsupported"); return r; }
                GpuIrqState state = LoadState();
                if (state.Phase == GpuIrqPhase.Pending)
                {
                    r.Outcome = AwaitingReboot(state) ? GpuIrqOutcome.AwaitingReboot : GpuIrqOutcome.NeedsVerification;
                    return r;
                }
                string error;
                InterruptAttributionResult raw = Capture(cancelled, progress, out error,
                    Math.Max(1000, Math.Min(CheckSeconds * 1000, windowMs)));
                if (cancelled != null && cancelled()) { r.Outcome = GpuIrqOutcome.Cancelled; return r; }
                if (raw == null || !raw.Ok || raw.Lossy || raw.Incomplete)
                {
                    r.Outcome = cancelled != null && cancelled() ? GpuIrqOutcome.Cancelled : GpuIrqOutcome.Failed;
                    r.Detail = raw == null ? error : Lang.T("irqcheck.lossy");
                    return r;
                }
                List<IrqDevice> devices = Devices();
                r.Slow = SlowDrivers(raw.Drivers, devices);
                r.Gpu = PickGpu(devices, raw.Drivers, state.Phase == GpuIrqPhase.Applied ? state.DeviceId : null);
                ulong all = CpuTopology.AllMask;
                r.Region = GameRegion(Plan(), all, CpuTopology.PhysicalCoreMasks());
                r.Target = TargetFor(r.Region, all, CpuTopology.PerfMask, CpuTopology.Hybrid);
                string why;
                r.Outcome = Evaluate(r.Gpu, r.Region, r.Target, out why);
                if (r.Outcome == GpuIrqOutcome.Scheduled)
                {
                    if (IsFused(r.Gpu.DeviceId, r.Gpu.DriverVersion))
                    { r.Outcome = GpuIrqOutcome.Skipped; why = "irqcheck.reason.fused"; }
                    else r.Outcome = GpuIrqOutcome.NeedsPlacement;
                }
                r.Detail = string.IsNullOrEmpty(why) ? "" : Lang.T(why);
                return r;
            }
        }

        public static GpuIrqResult Run(Func<bool> cancelled, Action<float> progress)
        {
            lock (lk)
            {
                var result = new GpuIrqResult();
                if (cancelled != null && cancelled()) { result.Outcome = GpuIrqOutcome.Cancelled; return result; }
                if (!Elevated()) { result.Outcome = GpuIrqOutcome.Failed; result.Detail = Lang.T("irqcheck.needadmin"); return result; }
                if (CpuTopology.MultiGroup) { result.Outcome = GpuIrqOutcome.Failed; result.Detail = Lang.T("irq.exact.unsupported"); return result; }
                GpuIrqState state = LoadState();
                if (AwaitingReboot(state)) { result.Outcome = GpuIrqOutcome.AwaitingReboot; return result; }

                string error;
                InterruptAttributionResult raw = Capture(cancelled, progress, out error);
                if (cancelled != null && cancelled()) { result.Outcome = GpuIrqOutcome.Cancelled; return result; }
                if (raw == null)
                {
                    bool stopped = cancelled != null && cancelled();
                    result.Outcome = stopped ? GpuIrqOutcome.Cancelled : GpuIrqOutcome.Failed;
                    result.Detail = error;
                    return result;
                }
                if (!raw.Ok || raw.Lossy || raw.Incomplete)
                { result.Outcome = GpuIrqOutcome.Failed; result.Detail = raw.Lossy || raw.Incomplete
                    ? Lang.T("irqcheck.lossy") : raw.Error; return result; }

                List<IrqDevice> devices = Devices();
                long now = DateTime.UtcNow.Ticks;
                result.Slow = SlowDrivers(raw.Drivers, devices);
                Settings.SaveStr(SlowKey, EncodeSlow(result.Slow, now));
                bool owned = state.Phase == GpuIrqPhase.Pending || state.Phase == GpuIrqPhase.Applied;
                GpuIrqSample gpu = PickGpu(devices, raw.Drivers, owned ? state.DeviceId : null);
                result.Gpu = gpu;
                ulong all = CpuTopology.AllMask;
                result.Region = GameRegion(Plan(), all, CpuTopology.PhysicalCoreMasks());
                result.Target = TargetFor(result.Region, all, CpuTopology.PerfMask, CpuTopology.Hybrid);
                state.CheckedTicks = now;

                if (state.Phase == GpuIrqPhase.Pending)
                {
                    if (!SameDevice(gpu, state) || gpu.Events < MinEvents || gpu.Seen == 0)
                    {
                        Save(state);
                        result.Outcome = GpuIrqOutcome.NoEvidence;
                        return result;
                    }
                    string reason;
                    state.Seen = gpu.Seen;
                    if (Verify(gpu, state, DisplayResetSinceBoot(), out reason))
                    {
                        state.Phase = GpuIrqPhase.Applied;
                        state.Reason = "";
                        result.Outcome = GpuIrqOutcome.Verified;
                        Logger.Log(Lang.F("log.irqcheck.verified", state.DeviceName, IrqRelocate.MaskText(state.Target)));
                    }
                    else
                    {
                        bool restored = Restore(state.DeviceId);
                        AddFuse(state.DeviceId, state.DriverVersion);
                        state.Phase = GpuIrqPhase.RolledBack;
                        state.Reason = reason;
                        result.Outcome = GpuIrqOutcome.RolledBack;
                        result.Detail = Lang.T(reason) + (restored ? "" : " · " + Lang.T("irqcheck.restorefailed"));
                        Logger.Warn(Lang.F("log.irqcheck.rolledback", state.DeviceName, Lang.T(reason)));
                    }
                    Save(state);
                    return result;
                }

                string why;
                GpuIrqOutcome outcome = Evaluate(gpu, result.Region, result.Target, out why);
                if (gpu != null)
                {
                    state.Seen = gpu.Seen;
                    if (!owned) { state.DeviceId = gpu.DeviceId; state.DriverVersion = gpu.DriverVersion; }
                    state.DeviceName = gpu.Name;
                }
                state.Region = result.Region;
                switch (outcome)
                {
                    case GpuIrqOutcome.Scheduled:
                        if (IsFused(gpu.DeviceId, gpu.DriverVersion))
                        {
                            outcome = GpuIrqOutcome.Skipped;
                            state.Phase = GpuIrqPhase.Skipped;
                            state.Reason = "irqcheck.reason.fused";
                            break;
                        }
                        string failure;
                        if (!Write(gpu.DeviceId, result.Target, out failure))
                        {
                            outcome = GpuIrqOutcome.Failed;
                            result.Detail = failure;
                            break;
                        }
                        state.Phase = GpuIrqPhase.Pending;
                        state.DeviceId = gpu.DeviceId;
                        state.DriverVersion = gpu.DriverVersion;
                        state.Target = result.Target;
                        state.BaselineP99 = gpu.P99Us;
                        state.Reason = "";
                        Logger.Log(Lang.F("log.irqcheck.scheduled", gpu.Name, IrqRelocate.MaskText(gpu.Seen), IrqRelocate.MaskText(result.Target)));
                        break;
                    case GpuIrqOutcome.NoRegion:
                        if (owned)
                        {
                            bool restored = Restore(state.DeviceId);
                            outcome = GpuIrqOutcome.Restored;
                            if (!restored) result.Detail = Lang.T("irqcheck.restorefailed");
                        }
                        state.Phase = GpuIrqPhase.NoRegion;
                        state.Reason = "";
                        break;
                    case GpuIrqOutcome.Clear:
                        state.Phase = owned && state.Phase == GpuIrqPhase.Applied ? GpuIrqPhase.Applied : GpuIrqPhase.Clear;
                        state.Reason = "";
                        break;
                    case GpuIrqOutcome.Skipped:
                        state.Phase = GpuIrqPhase.Skipped;
                        state.Reason = why;
                        break;
                    case GpuIrqOutcome.NoTarget:
                        state.Phase = GpuIrqPhase.Skipped;
                        state.Reason = "irqcheck.reason.notarget";
                        break;
                }
                result.Outcome = outcome;
                if (outcome != GpuIrqOutcome.Failed) Save(state);
                return result;
            }
        }

        public static bool RestoreAll()
        {
            lock (lk)
            {
                bool ok = HooksRestoreAll();
                if (ok) Settings.SaveStr(StateKey, "");
                return ok;
            }
        }

        private static void Save(GpuIrqState state) { Settings.SaveStr(StateKey, state.Encode()); }

        private static InterruptAttributionResult Capture(Func<bool> cancelled, Action<float> progress, out string error, int windowMs = CheckSeconds * 1000)
        {
            error = "";
#if PAVISE_SELFTEST
            if (CaptureForTest != null) return CaptureForTest(cancelled, progress, out error, windowMs);
#endif
            var ia = new InterruptAttribution();
            try
            {
                if (!ia.Start()) { error = InterruptAttribution.StartFailureText(ia); return null; }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    long elapsed = clock.ElapsedMilliseconds;
                    if (progress != null) progress(Math.Min(1f, elapsed / (float)windowMs));
                    if (cancelled != null && cancelled()) { ia.Stop(); return null; }
                    if (elapsed >= windowMs) break;
                    Thread.Sleep(200);
                }
                InterruptAttributionResult r = ia.Stop();
                if (!string.IsNullOrEmpty(r.Error)) { error = r.Error; return null; }
                return r;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                try { ia.Stop(); } catch { }
                return null;
            }
        }

        internal static bool DisplayResetSinceBoot()
        {
#if PAVISE_SELFTEST
            if (DisplayResetForTest != null) return DisplayResetForTest();
#endif
            try
            {
                DateTime boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Native.GetTickCount64());
                string query = "*[System[Provider[@Name='Display'] and (EventID=4101) and TimeCreated[@SystemTime>='"
                    + boot.ToString("yyyy-MM-ddTHH:mm:ss.000Z", CultureInfo.InvariantCulture) + "']]]";
                using (var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query)))
                using (EventRecord record = reader.ReadEvent())
                    return record != null;
            }
            catch { return false; }
        }

        private static bool Elevated()
        {
#if PAVISE_SELFTEST
            if (ElevatedForTest != null) return ElevatedForTest();
#endif
            return Native.IsElevated();
        }

        private static List<IrqDevice> Devices()
        {
#if PAVISE_SELFTEST
            if (DevicesForTest != null) return DevicesForTest();
#endif
            try { return IrqDeviceInventory.Enumerate(); }
            catch { return new List<IrqDevice>(); }
        }

        private static AutoCorePlan Plan()
        {
#if PAVISE_SELFTEST
            if (PlanForTest != null) return PlanForTest();
#endif
            AutoCoreStatus status = AutoCores.Status;
            return status.Manual && status.Engaged && !status.Pending ? status.Plan : null;
        }

        private static IrqRebootState RebootState(string deviceId)
        {
#if PAVISE_SELFTEST
            if (RebootStateForTest != null) return RebootStateForTest(deviceId);
#endif
            return engine.GetRebootState(deviceId);
        }

        private static bool Write(string deviceId, ulong mask, out string failure)
        {
            failure = "";
#if PAVISE_SELFTEST
            if (WriteForTest != null) return WriteForTest(deviceId, mask, out failure);
#endif
            bool ok = engine.Enable(new List<string> { deviceId }, mask);
            if (!ok) failure = engine.LastResult.Describe();
            return ok;
        }

        private static bool Restore(string deviceId)
        {
#if PAVISE_SELFTEST
            if (RestoreForTest != null) return RestoreForTest(deviceId);
#endif
            return engine.RestoreOnly(new List<string> { deviceId });
        }

        private static bool HooksRestoreAll()
        {
#if PAVISE_SELFTEST
            if (RestoreForTest != null) return RestoreForTest(null);
#endif
            return engine.Disable(null);
        }

#if PAVISE_SELFTEST
        internal delegate InterruptAttributionResult CaptureHook(Func<bool> cancelled, Action<float> progress, out string error, int windowMs);
        internal delegate bool WriteHook(string deviceId, ulong mask, out string failure);
        internal static CaptureHook CaptureForTest;
        internal static WriteHook WriteForTest;
        internal static Func<string, bool> RestoreForTest;
        internal static Func<bool> DisplayResetForTest, ElevatedForTest;
        internal static Func<List<IrqDevice>> DevicesForTest;
        internal static Func<AutoCorePlan> PlanForTest;
        internal static Func<string, IrqRebootState> RebootStateForTest;
#endif
    }
}
