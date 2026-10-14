// 作者：bdth 2074055628@qq.com
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private const string GpuIrqDisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        private const string GpuIrqDevice = "PCI\\VEN_10DE&DEV_2684\\4&1A2B3C&0&0008";

        internal static void RunGpuIrqCheckTests()
        {
            Action[] tests = { GpuIrqAttributionFold, GpuIrqRegionAndTarget, GpuIrqEvaluateBranches, GpuIrqVerifyRules,
                GpuIrqStateRoundTrip, GpuIrqSlowDriverReport, GpuIrqPickGpu, GpuIrqScheduleVerifyAndRollback, GpuIrqGuardsAndRestore,
                GpuIrqAuditReadOnly, GpuIrqFullWindowAndCaptureGuards, AuditDoesNotInspectVbs };
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS gpu-irq-check tests=" + tests.Length + " system_writes=mocked capture=mocked");
        }

        private static void GpuIrqAttributionFold()
        {
            long[] buckets;
            DriverInterrupt driver = InterruptAttribution.SyntheticDpcEvents(out buckets);
            Eq(3L, driver.Dpc); Eq(5UL, driver.CpuMask); Eq(false, driver.CpuMaskTruncated);
            Eq(1200.0, driver.DpcMaxUs); Eq(1900.0, driver.DpcTotalUs); Eq(0L, driver.BadDuration);
            Eq(2L, InterruptAttribution.SumFrom(buckets, 9)); Eq(1L, InterruptAttribution.SumFrom(buckets, 10));
            Eq(1000.0, IrqPercentile.ApproxUs(buckets, driver.DpcMaxUs, 0.5));
            Eq(1200.0, IrqPercentile.ApproxUs(buckets, driver.DpcMaxUs, 0.99));
            Eq(true, InterruptAttribution.CaptureComplete(true, true, true, false));
            Eq(false, InterruptAttribution.CaptureComplete(true, true, true, true));
            Eq(false, InterruptAttribution.HasMappedEvents(3, 0, 0));
            Eq(true, InterruptAttribution.HasMappedEvents(3, 0, 1));
        }

        private static void GpuIrqRegionAndTarget()
        {
            ulong[] cores = { 3, 12, 48, 192 };
            Eq(0UL, GpuIrqCheck.GameRegion(null, 255, cores));
            Eq(0UL, GpuIrqCheck.GameRegion(new AutoCorePlan { Blocked = "schedule.auto.uniform", Exclusive = 0xF0 }, 255, cores));
            Eq(0UL, GpuIrqCheck.GameRegion(new AutoCorePlan { Exclusive = 255, Cores = 4 }, 255, cores));
            Eq(0UL, GpuIrqCheck.GameRegion(new AutoCorePlan { Exclusive = 0 }, 255, cores));
            Eq(0xF0UL, GpuIrqCheck.GameRegion(new AutoCorePlan { Exclusive = 0x50, Cores = 2 }, 255, cores));
            Eq(0xC0UL, GpuIrqCheck.GameRegion(new AutoCorePlan { Exclusive = 0xC0, Cores = 1 }, 255, cores));
            Eq(0xFCUL, GpuIrqCheck.GameRegion(new AutoCorePlan { Exclusive = 0xFC, Cores = 3 }, 255, cores));
            Eq(0UL, GpuIrqCheck.GameRegion(new AutoCorePlan { Exclusive = 0xAA, Cores = 4 }, 255, cores));
            Eq(0x0FUL, GpuIrqCheck.TargetFor(0xF0, 255, 0, false));
            Eq(0x0CUL, GpuIrqCheck.TargetFor(0xF0, 255, 0x0C | 0xF0, true));
            Eq(0UL, GpuIrqCheck.TargetFor(0xF0, 255, 0xF0, true));
            Eq(0UL, GpuIrqCheck.TargetFor(0, 255, 0, false));
        }

        private static GpuIrqSample GpuIrqSampleOn(ulong seen, long events)
        {
            return new GpuIrqSample { DeviceId = GpuIrqDevice, Name = "GeForce", Driver = "nvlddmkm.sys",
                DriverVersion = "32.0.15.6094", Seen = seen, Events = events, P99Us = 40, MaxUs = 90 };
        }

        private static void GpuIrqEvaluateBranches()
        {
            string reason;
            Eq(GpuIrqOutcome.NoEvidence, GpuIrqCheck.Evaluate(null, 0xF0, 0x0F, out reason));
            Eq(GpuIrqOutcome.NoEvidence, GpuIrqCheck.Evaluate(GpuIrqSampleOn(0x10, GpuIrqCheck.MinEvents - 1), 0xF0, 0x0F, out reason));
            Eq(GpuIrqOutcome.NoEvidence, GpuIrqCheck.Evaluate(GpuIrqSampleOn(0, 5000), 0xF0, 0x0F, out reason));
            Eq(GpuIrqOutcome.NoRegion, GpuIrqCheck.Evaluate(GpuIrqSampleOn(0x10, 5000), 0, 0, out reason));
            Eq(GpuIrqOutcome.Clear, GpuIrqCheck.Evaluate(GpuIrqSampleOn(0x03, 5000), 0xF0, 0x0F, out reason));
            Eq(GpuIrqOutcome.NoTarget, GpuIrqCheck.Evaluate(GpuIrqSampleOn(0x13, 5000), 0xF0, 0, out reason));
            Eq(GpuIrqOutcome.Scheduled, GpuIrqCheck.Evaluate(GpuIrqSampleOn(0x13, 5000), 0xF0, 0x0F, out reason));
            GpuIrqSample multi = GpuIrqSampleOn(0x10, 5000); multi.MultiMessage = true;
            Eq(GpuIrqOutcome.Skipped, GpuIrqCheck.Evaluate(multi, 0xF0, 0x0F, out reason)); Eq("irqcheck.reason.multimessage", reason);
            GpuIrqSample pinned = GpuIrqSampleOn(0x10, 5000); pinned.ExternalPin = true;
            Eq(GpuIrqOutcome.Skipped, GpuIrqCheck.Evaluate(pinned, 0xF0, 0x0F, out reason)); Eq("irqcheck.reason.externalpin", reason);
            GpuIrqSample unknown = GpuIrqSampleOn(0x10, 5000); unknown.ConfigurationKnown = false;
            Eq(GpuIrqOutcome.Skipped, GpuIrqCheck.Evaluate(unknown, 0xF0, 0x0F, out reason)); Eq("irqcheck.reason.config", reason);
        }

        private static void GpuIrqVerifyRules()
        {
            var state = new GpuIrqState { Phase = GpuIrqPhase.Pending, Target = 0x0F, BaselineP99 = 40 };
            string reason;
            Eq(true, GpuIrqCheck.Verify(GpuIrqSampleOn(0x04, 5000), state, false, out reason));
            Eq(false, GpuIrqCheck.Verify(GpuIrqSampleOn(0x04, 5000), state, true, out reason)); Eq("irqcheck.reason.reset", reason);
            Eq(false, GpuIrqCheck.Verify(GpuIrqSampleOn(0x14, 5000), state, false, out reason)); Eq("irqcheck.reason.ignored", reason);
            GpuIrqSample slower = GpuIrqSampleOn(0x04, 5000); slower.P99Us = 141;
            Eq(false, GpuIrqCheck.Verify(slower, state, false, out reason)); Eq("irqcheck.reason.slower", reason);
            slower.P99Us = 140;
            Eq(true, GpuIrqCheck.Verify(slower, state, false, out reason));
            state.BaselineP99 = 0; slower.P99Us = 5000;
            Eq(true, GpuIrqCheck.Verify(slower, state, false, out reason));
        }

        private static void GpuIrqStateRoundTrip()
        {
            var state = new GpuIrqState { Phase = GpuIrqPhase.Applied, DeviceId = GpuIrqDevice, DeviceName = "RTX | 4090 %",
                DriverVersion = "32.0.15.6094", Reason = "irqcheck.reason.fused", Target = 0x0F, Region = 0xF0, Seen = 0x02,
                BaselineP99 = 42.5, CheckedTicks = 638000000000000000 };
            GpuIrqState back = GpuIrqState.Parse(state.Encode());
            Eq(state.Encode(), back.Encode());
            Eq("RTX | 4090 %", back.DeviceName);
            foreach (string bad in new[] { "", "garbage", "2|2|a|b|c|d|F|F0|2|1|1", "1|9|a|b|c|d|F|F0|2|1|1", "1|2|a|b|c|d|ZZ|F0|2|1|1" })
            {
                GpuIrqState parsed = GpuIrqState.Parse(bad);
                Eq(GpuIrqPhase.None, parsed.Phase);
                Eq("", parsed.DeviceId);
            }
        }

        private static DriverInterrupt GpuIrqDriver(string driver, long dpc, ulong cpu, double maxUs, int bucket, long over1ms)
        {
            var buckets = new long[InterruptAttribution.BucketCount];
            buckets[bucket] = dpc;
            return new DriverInterrupt { Driver = driver, Dpc = dpc, CpuMask = cpu, DpcMaxUs = maxUs,
                DpcBuckets = buckets, DpcOver1Ms = over1ms };
        }

        private static void GpuIrqSlowDriverReport()
        {
            var drivers = new List<DriverInterrupt>
            {
                GpuIrqDriver("nvlddmkm.sys", 5000, 0x10, 90, 5, 0),
                GpuIrqDriver("rtwlane.sys", 400, 0x01, 2100, 10, 3),
                GpuIrqDriver("ntoskrnl.exe", 9000, 0xFF, 3000, 10, 9),
                GpuIrqDriver("usbxhci.sys", 150, 0x01, 700, 8, 0),
                GpuIrqDriver("few.sys", 20, 0x01, 700, 8, 0)
            };
            var devices = new List<IrqDevice> { new IrqDevice { Service = "rtwlane", Name = "Realtek Wi-Fi" } };
            List<SlowDriver> slow = GpuIrqCheck.SlowDrivers(drivers, devices);
            Eq(2, slow.Count);
            Eq("rtwlane.sys", slow[0].Driver); Eq("Realtek Wi-Fi", slow[0].Device); Eq(3L, slow[0].Over1Ms);
            Eq("usbxhci.sys", slow[1].Driver);
            long ticks;
            List<SlowDriver> back = GpuIrqCheck.DecodeSlow(GpuIrqCheck.EncodeSlow(slow, 12345), out ticks);
            Eq(12345L, ticks); Eq(2, back.Count); Eq("Realtek Wi-Fi", back[0].Device); Eq(2100.0, back[0].MaxUs);
            Eq(0, GpuIrqCheck.DecodeSlow("bad", out ticks).Count); Eq(0L, ticks);
            var many = new List<DriverInterrupt>();
            for (int i = 0; i < 9; i++) many.Add(GpuIrqDriver("slow" + i + ".sys", 200, 1, 1000 + i, 10, 1));
            List<SlowDriver> capped = GpuIrqCheck.SlowDrivers(many, null);
            Eq(GpuIrqCheck.SlowLimit, capped.Count); Eq("slow8.sys", capped[0].Driver);
        }

        private static IrqDevice GpuIrqDisplayDevice(string id, string service)
        {
            return new IrqDevice { InstanceId = id, Name = "GeForce RTX", Service = service, Bus = "PCI",
                ClassGuid = GpuIrqDisplayClass, ConfigurationKnown = true, DriverVersion = "32.0.15.6094" };
        }

        private static void GpuIrqPickGpu()
        {
            var drivers = new List<DriverInterrupt> { GpuIrqDriver("nvlddmkm.sys", 5000, 0x10, 90, 5, 0), GpuIrqDriver("igdkmdn64.sys", 800, 0x01, 60, 5, 0) };
            var nvidia = GpuIrqDisplayDevice(GpuIrqDevice, "nvlddmkm");
            var intel = GpuIrqDisplayDevice("PCI\\VEN_8086&DEV_A780\\3&11", "igdkmdn64");
            var audio = new IrqDevice { InstanceId = "HDAUDIO\\X", Service = "nvlddmkm", Bus = "HDAUDIO", ClassGuid = "{4d36e96c-e325-11ce-bfc1-08002be10318}" };
            GpuIrqSample pick = GpuIrqCheck.PickGpu(new List<IrqDevice> { intel, audio, nvidia }, drivers, null);
            Eq(GpuIrqDevice, pick.DeviceId); Eq(5000L, pick.Events); Eq(0x10UL, pick.Seen); Eq(false, pick.ExternalPin);
            nvidia.Policy = 4; nvidia.Mask = 0x0F;
            Eq(true, GpuIrqCheck.PickGpu(new List<IrqDevice> { nvidia }, drivers, null).ExternalPin);
            Eq(false, GpuIrqCheck.PickGpu(new List<IrqDevice> { nvidia }, drivers, GpuIrqDevice).ExternalPin);
            drivers[0].CpuMaskTruncated = true;
            Eq(0UL, GpuIrqCheck.PickGpu(new List<IrqDevice> { nvidia }, drivers, GpuIrqDevice).Seen);
            Eq(null, GpuIrqCheck.PickGpu(new List<IrqDevice> { audio }, drivers, null));
        }

        private sealed class GpuIrqFixture : IDisposable
        {
            private readonly CpuTopology.TopologySnapshot topology = CpuTopology.CaptureTopologyForTest();
            internal ulong CaptureCpu = 0x10;
            internal long CaptureEvents = 5000;
            internal bool Lossy, Reset, Elevated = true, CancelCapture, CancelAfterCapture, Invalid, Incomplete;
            internal int Captures, Writes, Restores, LastWindowMs;
            internal ulong LastWrite;
            internal IrqRebootState Reboot = IrqRebootState.Unknown;
            internal AutoCorePlan Plan = new AutoCorePlan { Exclusive = 0xF0, Cores = 2, Kind = "hybrid" };
            internal readonly List<IrqDevice> Devices = new List<IrqDevice>();

            internal GpuIrqFixture()
            {
                CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 255 }, 0, 0, 0, 0, false, false);
                Settings.SaveStr(GpuIrqCheck.StateKey, "");
                Settings.SaveStr(GpuIrqCheck.SlowKey, "");
                Settings.SaveStr(GpuIrqCheck.FuseKey, "");
                Devices.Add(GpuIrqDisplayDevice(GpuIrqDevice, "nvlddmkm"));
                GpuIrqCheck.ElevatedForTest = delegate { return Elevated; };
                GpuIrqCheck.DevicesForTest = delegate { return Devices; };
                GpuIrqCheck.PlanForTest = delegate { return Plan; };
                GpuIrqCheck.RebootStateForTest = delegate { return Reboot; };
                GpuIrqCheck.DisplayResetForTest = delegate { return Reset; };
                GpuIrqCheck.CaptureForTest = delegate(Func<bool> cancelled, Action<float> progress, out string error, int windowMs)
                {
                    Captures++;
                    LastWindowMs = windowMs;
                    error = "";
                    if (progress != null) progress(1f);
                    if (CancelCapture) return null;
                    var result = new InterruptAttributionResult { Ok = !Invalid, Error = Invalid ? "invalid fixture" : "",
                        Incomplete = Incomplete, EventsLost = Lossy ? 3u : 0u };
                    result.Drivers.Add(GpuIrqDriver("nvlddmkm.sys", CaptureEvents, CaptureCpu, 90, 5, 0));
                    result.Drivers.Add(GpuIrqDriver("rtwlane.sys", 400, 0x01, 2100, 10, 3));
                    return result;
                };
                GpuIrqCheck.WriteForTest = delegate(string id, ulong mask, out string failure)
                {
                    Writes++; LastWrite = mask; failure = "";
                    Eq(GpuIrqDevice, id);
                    return true;
                };
                GpuIrqCheck.RestoreForTest = delegate { Restores++; return true; };
            }

            internal GpuIrqResult Run()
            {
                return GpuIrqCheck.Run(delegate { return CancelCapture || (CancelAfterCapture && Captures > 0); }, null);
            }

            public void Dispose()
            {
                GpuIrqCheck.ElevatedForTest = null; GpuIrqCheck.DevicesForTest = null; GpuIrqCheck.PlanForTest = null;
                GpuIrqCheck.RebootStateForTest = null; GpuIrqCheck.DisplayResetForTest = null; GpuIrqCheck.CaptureForTest = null;
                GpuIrqCheck.WriteForTest = null; GpuIrqCheck.RestoreForTest = null;
                Settings.SaveStr(GpuIrqCheck.StateKey, "");
                Settings.SaveStr(GpuIrqCheck.SlowKey, "");
                Settings.SaveStr(GpuIrqCheck.FuseKey, "");
                CpuTopology.RestoreTopologyForTest(topology);
            }
        }

        private static void GpuIrqScheduleVerifyAndRollback()
        {
            using (var f = new GpuIrqFixture())
            {
                GpuIrqResult r = f.Run();
                Eq(GpuIrqOutcome.Scheduled, r.Outcome);
                Eq(1, f.Writes); Eq(0x0FUL, f.LastWrite);
                GpuIrqState state = GpuIrqCheck.LoadState();
                Eq(GpuIrqPhase.Pending, state.Phase); Eq(0x0FUL, state.Target); Eq(0xF0UL, state.Region); Eq(0x10UL, state.Seen);
                long ticks;
                Eq(1, GpuIrqCheck.LoadSlow(out ticks).Count);

                f.Reboot = IrqRebootState.AwaitingReboot;
                Eq(GpuIrqOutcome.AwaitingReboot, f.Run().Outcome);
                Eq(1, f.Captures);

                f.Reboot = IrqRebootState.Rebooted;
                f.CaptureEvents = 10;
                Eq(GpuIrqOutcome.NoEvidence, f.Run().Outcome);
                Eq(GpuIrqPhase.Pending, GpuIrqCheck.LoadState().Phase);

                f.CaptureEvents = 5000; f.CaptureCpu = 0x04;
                Eq(GpuIrqOutcome.Verified, f.Run().Outcome);
                Eq(GpuIrqPhase.Applied, GpuIrqCheck.LoadState().Phase);
                Eq(0, f.Restores);

                Eq(GpuIrqOutcome.Clear, f.Run().Outcome);
                Eq(GpuIrqPhase.Applied, GpuIrqCheck.LoadState().Phase);
                Eq(1, f.Writes);
            }
            using (var f = new GpuIrqFixture())
            {
                Eq(GpuIrqOutcome.Scheduled, f.Run().Outcome);
                f.Reboot = IrqRebootState.Rebooted;
                GpuIrqResult r = f.Run();
                Eq(GpuIrqOutcome.RolledBack, r.Outcome);
                Eq(1, f.Restores);
                GpuIrqState state = GpuIrqCheck.LoadState();
                Eq(GpuIrqPhase.RolledBack, state.Phase); Eq("irqcheck.reason.ignored", state.Reason);
                Eq(true, GpuIrqCheck.IsFused(GpuIrqDevice, "32.0.15.6094"));

                r = f.Run();
                Eq(GpuIrqOutcome.Skipped, r.Outcome);
                Eq(1, f.Writes);
                Eq("irqcheck.reason.fused", GpuIrqCheck.LoadState().Reason);

                f.Devices[0].DriverVersion = "32.0.15.7000";
                Eq(GpuIrqOutcome.Scheduled, f.Run().Outcome);
                Eq(2, f.Writes);
            }
            using (var f = new GpuIrqFixture())
            {
                Eq(GpuIrqOutcome.Scheduled, f.Run().Outcome);
                f.Reboot = IrqRebootState.Rebooted; f.CaptureCpu = 0x02; f.Reset = true;
                Eq(GpuIrqOutcome.RolledBack, f.Run().Outcome);
                Eq("irqcheck.reason.reset", GpuIrqCheck.LoadState().Reason);
            }
        }

        private static void GpuIrqGuardsAndRestore()
        {
            using (var f = new GpuIrqFixture())
            {
                f.Elevated = false;
                Eq(GpuIrqOutcome.Failed, f.Run().Outcome);
                Eq(0, f.Captures);
                f.Elevated = true;

                f.Lossy = true;
                Eq(GpuIrqOutcome.Failed, f.Run().Outcome);
                Eq(GpuIrqPhase.None, GpuIrqCheck.LoadState().Phase);
                f.Lossy = false;

                f.CancelCapture = true;
                Eq(GpuIrqOutcome.Cancelled, f.Run().Outcome);
                f.CancelCapture = false;

                f.Plan = new AutoCorePlan { Blocked = "schedule.auto.uniform" };
                Eq(GpuIrqOutcome.NoRegion, f.Run().Outcome);
                Eq(GpuIrqPhase.NoRegion, GpuIrqCheck.LoadState().Phase);
                Eq(0, f.Writes); Eq(0, f.Restores);

                f.Plan = new AutoCorePlan { Exclusive = 0xF0, Cores = 2, Kind = "hybrid" };
                f.CaptureCpu = 0x01;
                Eq(GpuIrqOutcome.Clear, f.Run().Outcome);
                Eq(GpuIrqPhase.Clear, GpuIrqCheck.LoadState().Phase);

                f.CaptureCpu = 0x10;
                Eq(GpuIrqOutcome.Scheduled, f.Run().Outcome);
                f.Reboot = IrqRebootState.Rebooted; f.CaptureCpu = 0x01;
                Eq(GpuIrqOutcome.Verified, f.Run().Outcome);

                f.Plan = new AutoCorePlan { Blocked = "schedule.auto.handheld" };
                Eq(GpuIrqOutcome.Restored, f.Run().Outcome);
                Eq(1, f.Restores);
                Eq(GpuIrqPhase.NoRegion, GpuIrqCheck.LoadState().Phase);

                f.Plan = new AutoCorePlan { Exclusive = 0xF0, Cores = 2, Kind = "hybrid" };
                f.CaptureCpu = 0x10;
                f.Devices[0].MessageCount = 4; f.Devices[0].MultiMessageRisk = true;
                Eq(GpuIrqOutcome.Skipped, f.Run().Outcome);
                Eq("irqcheck.reason.multimessage", GpuIrqCheck.LoadState().Reason);

                Eq(true, GpuIrqCheck.RestoreAll());
                Eq(GpuIrqPhase.None, GpuIrqCheck.LoadState().Phase);
            }
        }

        private static void GpuIrqAuditReadOnly()
        {
            using (var f = new GpuIrqFixture())
            {
                foreach (long events in new[] { 5000L, 10L })
                {
                    f.CaptureEvents = events;
                    GpuIrqResult r = GpuIrqCheck.Inspect(3000, null);
                    Eq(events == 5000 ? GpuIrqOutcome.NeedsPlacement : GpuIrqOutcome.NoEvidence, r.Outcome);
                    Eq(0, f.Writes); Eq(0, f.Restores);
                    Eq("", Settings.LoadStr(GpuIrqCheck.StateKey, ""));
                    Eq("", Settings.LoadStr(GpuIrqCheck.SlowKey, ""));
                    var report = new AuditReport { GpuIrq = r };
                    SystemAudit.BuildGpuInterrupt(report);
                    Eq("gpuirq", report.Machine[0].FixKey);
                    Eq(events == 5000 ? SystemAudit.EvMeasuredLocal : SystemAudit.EvUnverified, report.Machine[0].Evidence);
                    Eq(2, report.Machine.Count);
                }
                f.CaptureEvents = 5000; f.Lossy = true;
                Eq(GpuIrqOutcome.Failed, GpuIrqCheck.Inspect(3000, null).Outcome);
                f.Lossy = false; f.Elevated = false;
                Eq(GpuIrqOutcome.Failed, GpuIrqCheck.Inspect(3000, null).Outcome);
                f.Elevated = true;
                int captures = f.Captures;
                Eq(GpuIrqOutcome.Cancelled, GpuIrqCheck.Inspect(3000, delegate { return true; }).Outcome);
                Eq(captures, f.Captures);

                Eq(GpuIrqOutcome.Scheduled, f.Run().Outcome);
                string saved = Settings.LoadStr(GpuIrqCheck.StateKey, "");
                f.Reboot = IrqRebootState.AwaitingReboot;
                Eq(GpuIrqOutcome.AwaitingReboot, GpuIrqCheck.Inspect(3000, null).Outcome);
                f.Reboot = IrqRebootState.Rebooted; f.Reset = true;
                Eq(GpuIrqOutcome.NeedsVerification, GpuIrqCheck.Inspect(3000, null).Outcome);
                Eq(saved, Settings.LoadStr(GpuIrqCheck.StateKey, ""));
                Eq(1, f.Writes); Eq(0, f.Restores);
                Eq(GpuIrqOutcome.RolledBack, f.Run().Outcome);
                Eq(GpuIrqOutcome.Skipped, GpuIrqCheck.Inspect(3000, null).Outcome);
                Eq(1, f.Writes); Eq(1, f.Restores);
            }
        }

        private static void GpuIrqFullWindowAndCaptureGuards()
        {
            using (var f = new GpuIrqFixture())
            {
                float progress = -1;
                GpuIrqResult result = GpuIrqCheck.Inspect(120000, null, delegate(float value) { progress = value; });
                Eq(120000, f.LastWindowMs); Eq(1f, progress);
                Eq(GpuIrqOutcome.NeedsPlacement, result.Outcome);
                Eq(0, f.Writes); Eq(0, f.Restores);
                Eq("", Settings.LoadStr(GpuIrqCheck.StateKey, ""));
                Eq("", Settings.LoadStr(GpuIrqCheck.SlowKey, ""));
                GpuIrqCheck.Inspect(3000, null); Eq(3000, f.LastWindowMs);
                GpuIrqCheck.Inspect(int.MaxValue, null); Eq(120000, f.LastWindowMs);
                GpuIrqCheck.Inspect(0, null); Eq(1000, f.LastWindowMs);
                Eq(GpuIrqOutcome.Scheduled, f.Run().Outcome); Eq(120000, f.LastWindowMs);
                f.Reboot = IrqRebootState.Rebooted; f.CaptureCpu = 0x04;
                Eq(GpuIrqOutcome.Verified, f.Run().Outcome); Eq(120000, f.LastWindowMs);
                var report = new AuditReport { GpuIrq = result };
                report.Machine.Add(new AuditRow { Name = "retained CPU row" });
                SystemAudit.BuildGpuInterrupt(report); Eq(3, report.Machine.Count);
                result.Slow.Clear(); SystemAudit.BuildGpuInterrupt(report);
                Eq(2, report.Machine.Count); Eq("retained CPU row", report.Machine[0].Name);
            }
            using (var f = new GpuIrqFixture())
            {
                f.Invalid = true;
                Eq(GpuIrqOutcome.Failed, f.Run().Outcome);
                f.Invalid = false; f.Incomplete = true;
                Eq(GpuIrqOutcome.Failed, f.Run().Outcome);
                Eq(0, f.Writes); Eq(0, f.Restores);
                Eq("", Settings.LoadStr(GpuIrqCheck.StateKey, ""));
                Eq("", Settings.LoadStr(GpuIrqCheck.SlowKey, ""));
            }
            using (var f = new GpuIrqFixture())
            {
                f.CancelAfterCapture = true;
                Eq(GpuIrqOutcome.Cancelled, f.Run().Outcome);
                Eq(1, f.Captures); Eq(0, f.Writes); Eq(0, f.Restores);
                Eq("", Settings.LoadStr(GpuIrqCheck.StateKey, ""));
                Eq("", Settings.LoadStr(GpuIrqCheck.SlowKey, ""));
            }
            using (var f = new GpuIrqFixture())
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Eq(null, SystemAudit.Collect(120000, delegate { return f.Captures > 0; }));
                Eq(true, clock.ElapsedMilliseconds < 3000);
                Eq(0, f.Writes); Eq(0, f.Restores);
                Eq("", Settings.LoadStr(GpuIrqCheck.StateKey, ""));
                double busy;
                Eq(null, DpcSampler.MeasureLoad(120000, out busy, delegate { return true; }));
                Eq(0.0, busy);
            }
        }

        private static void AuditDoesNotInspectVbs()
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            Eq(true, typeof(AuditReport).GetField("VbsCanDisable") == null);
            Eq(true, typeof(SystemAudit).GetMethod("BuildVbs", flags) == null);
            var codes = new Dictionary<short, System.Reflection.Emit.OpCode>();
            foreach (var field in typeof(System.Reflection.Emit.OpCodes).GetFields())
                if (field.FieldType == typeof(System.Reflection.Emit.OpCode))
                {
                    var code = (System.Reflection.Emit.OpCode)field.GetValue(null); codes[code.Value] = code;
                }
            foreach (var method in typeof(SystemAudit).GetMethods(flags))
            {
                var body = method.GetMethodBody(); if (body == null) continue;
                byte[] il = body.GetILAsByteArray();
                for (int pos = 0; pos < il.Length;)
                {
                    byte first = il[pos++];
                    short value = first == 0xfe ? (short)(0xfe00 | il[pos++]) : (short)first;
                    var op = codes[value];
                    if (op == System.Reflection.Emit.OpCodes.Call || op == System.Reflection.Emit.OpCodes.Callvirt)
                    {
                        var target = method.Module.ResolveMethod(BitConverter.ToInt32(il, pos));
                        Eq(false, target.DeclaringType == typeof(VbsTweak));
                    }
                    switch (op.OperandType)
                    {
                        case System.Reflection.Emit.OperandType.InlineNone: break;
                        case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                        case System.Reflection.Emit.OperandType.ShortInlineI:
                        case System.Reflection.Emit.OperandType.ShortInlineVar: pos++; break;
                        case System.Reflection.Emit.OperandType.InlineVar: pos += 2; break;
                        case System.Reflection.Emit.OperandType.InlineI8:
                        case System.Reflection.Emit.OperandType.InlineR: pos += 8; break;
                        case System.Reflection.Emit.OperandType.InlineSwitch: pos += 4 + 4 * BitConverter.ToInt32(il, pos); break;
                        default: pos += 4; break;
                    }
                }
            }
        }
    }

    internal sealed partial class InterruptAttribution
    {
        internal static DriverInterrupt SyntheticDpcEvents(out long[] buckets)
        {
            var probe = new InterruptAttribution { usPerTick = 1, sanityMaxTicks = 1000000 };
            probe.modules.Add(new Module { Base = 1000, End = 2000, Name = "fixture.sys" });
            IntPtr payload = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
            try
            {
                int[] durations = { 600, 100, 1200 };
                ushort[] cpus = { 0, 0, 2 };
                for (int i = 0; i < durations.Length; i++)
                {
                    System.Runtime.InteropServices.Marshal.WriteInt64(payload, 10000);
                    System.Runtime.InteropServices.Marshal.WriteInt64(payload, 8, 1000);
                    var record = new EventRecord { UserData = payload, UserDataLength = 16,
                        EventHeader = new EventHeader { ProviderId = PerfInfoGuid, Flags = HeaderFlag64Bit,
                            TimeStamp = 10000 + durations[i], EventDescriptor = new EventDescriptor { Opcode = 66 } },
                        BufferContext = new EtwBufferContext { ProcessorIndex = cpus[i] } };
                    probe.OnEvent(ref record);
                }
                var drivers = new Dictionary<string, DriverInterrupt>();
                var folded = new Dictionary<string, long[]>();
                probe.Fold(drivers, folded, probe.dpcHits, true);
                buckets = folded["fixture.sys"];
                return drivers["fixture.sys"];
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(payload); }
        }
    }
}
#endif
