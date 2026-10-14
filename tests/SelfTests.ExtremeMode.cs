#if PAVISE_SELFTEST
using System;
namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static string TimerFixtureBoot(uint sequence)
        { return BootSessionIdentity.Create(new Guid("12345678-1234-1234-1234-1234567890ab"), sequence); }

        private static void CheckRebootIdentityRegression()
        {
            Settings.UseTransientStoreForCurrentProcess();
            string first = TimerFixtureBoot(20), next = TimerFixtureBoot(21);
            Eq(IrqRebootState.AwaitingReboot, IrqAffinityEngine.RebootStateFromStamps(first, first));
            Eq(IrqRebootState.Rebooted, IrqAffinityEngine.RebootStateFromStamps(first, next));
            Eq(IrqRebootState.Unknown, IrqAffinityEngine.RebootStateFromStamps("63894500000", next));
            Eq(IrqRebootState.Unknown, IrqAffinityEngine.RebootStateFromStamps(first, null));
            Eq(false, BootSessionIdentity.Valid("boot2:123456781234123412341234567890ab:0"));
            const string slot = "reboot-regression";
            bool configured = false, owned = false; int writes = 0;
            Func<bool> enable = delegate { writes++; configured = owned = true; return true; };
            Func<bool> restore = delegate { writes++; configured = owned = false; return true; };
            Func<string, string> step = delegate(string boot) {
                return TimerModeController.Step(slot, true, boot, () => configured, () => owned, enable, restore);
            };
            Eq("pending-reboot", step(first)); Eq(1, writes);
            string original = Settings.LoadStr(slot, "");
            Eq("pending-reboot", step(first)); Eq(original, Settings.LoadStr(slot, ""));
            Eq("ready", step(next)); Eq(1, writes); Eq(original, Settings.LoadStr(slot, ""));
            Eq("ready", step(next)); Eq(1, writes);
            Eq("unverified", step(null)); Eq(original, Settings.LoadStr(slot, ""));
            Settings.SaveStr(slot, "on|123456781234123412341234567890ab");
            Eq("unverified", step(next)); Eq(1, writes);
            Eq("unverified", step(next)); Eq(1, writes);
            Eq("ready", step(TimerFixtureBoot(22))); Eq(1, writes);
            Eq("restore-pending-reboot", TimerModeController.Step(slot, false, next, () => configured, () => owned, enable, restore));
            Eq(2, writes);
            Eq("restored", TimerModeController.Step(slot, false, TimerFixtureBoot(22), () => configured, () => owned, enable, restore));
            Eq(2, writes);
            bool attempted; string irqStamp = first;
            Eq(true, IrqAffinityEngine.ApplyWithBootStamp(true, next, value => { irqStamp = value; return true; },
                () => irqStamp, () => { throw new Exception("No-op rewrote IRQ configuration"); }, out attempted));
            Eq(false, attempted); Eq(first, irqStamp);
            Eq(true, IrqAffinityEngine.ApplyWithBootStamp(false, next, value => { irqStamp = value; return true; },
                () => irqStamp, () => true, out attempted));
            Eq(true, attempted); Eq(next, irqStamp);
            Eq(false, IrqAffinityEngine.ApplyWithBootStamp(false, next, value => false,
                () => irqStamp, () => { throw new Exception("Wrote without durable boot marker"); }, out attempted));
            Eq(false, attempted);
            Console.WriteLine("PASS reboot identity: same-loader new-boot, process restart, missing/legacy evidence, restore, IRQ write ownership");
        }

        private static void RunExtremeModeTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.Save(AutomaticPolicy.VersionKey, true);
            Eq(PerformancePreset.Extreme, PresetValue.From(6));
            Eq(true, PresetValue.SupportsCoreScheduling(PerformancePreset.Extreme));
            Eq(true, GameMode.IsAggressive(PerformancePreset.Extreme, false));
            foreach (string mode in new[] { "0", "6", "4", "6", "1", "0" })
            {
                Settings.SaveStr(PolicyCatalog.KeyPreset, mode);
                var policy = PolicyResolver.Global();
                Eq(mode == "6", policy.DwmBoost);
                Eq(mode == "6", policy.IdlePolicy);
                Eq(true, policy.PauseUpdate);
                Eq(mode == "6", policy.IntelEnduranceOff);
                Eq("off", policy.NvLowLatMode);
                Eq(false, policy.AmdAntiLag);
                Eq(false, policy.GpuPowerMax);
                Eq("0", policy.ValueOf(PolicyCatalog.KeyTimerTick));
                Eq("0", policy.ValueOf(PolicyCatalog.KeyGlobalTimer));
                Eq(false, policy.AutoCoresAllowed);
                Eq(false, policy.StandbyCleaner);
            }
            Settings.Save(AutomaticPolicy.VersionKey, false);
            Settings.Save(PolicyCatalog.KeyGpuPowerMax, true);
            var oldPowerProfile = new GameProfile { Id = "old-power" };
            oldPowerProfile.Overrides[PolicyCatalog.KeyGpuPowerMax] = "1";
            Eq(false, PolicyResolver.For(oldPowerProfile).GpuPowerMax);
            Eq("0", PolicyResolver.Read(oldPowerProfile, PolicyCatalog.KeyGpuPowerMax));
            var oldPowerMode = new GameMode(System.IO.Path.GetTempPath(), new SuppressionCore());
            Eq(false, oldPowerMode.GpuPowerLift);
            oldPowerMode.GpuPowerLift = true;
            Eq(false, oldPowerMode.GpuPowerLift); Eq(false, Settings.Load(PolicyCatalog.KeyGpuPowerMax, true));
            Settings.Save(AutomaticPolicy.VersionKey, true);
            var modeSwitch = new GameMode(System.IO.Path.GetTempPath(), new SuppressionCore());
            modeSwitch.Preset = PerformancePreset.Extreme;
            Eq(true, modeSwitch.AutomationPolicySnapshot.IntelEnduranceOff);
            modeSwitch.Preset = PerformancePreset.Standard;
            Eq(false, modeSwitch.AutomationPolicySnapshot.IntelEnduranceOff);
            Settings.SaveStr(PolicyCatalog.KeyPreset, "6");
            Eq(true, PowerPlan.SelectPlan(PowerPlan.BalancedGuid.ToString()));
            Eq(false, PolicyResolver.Global().IdlePolicy);
            Eq(true, PowerPlan.SelectPlan(PowerPlan.ManagedChoice));
            Eq(true, PolicyResolver.Global().IdlePolicy);

            int dwmCalls = 0; bool dwmFail = false;
            DwmBoost.RequestForTest = delegate(bool enabled) { dwmCalls++; return dwmFail ? -1 : 0; };
            try
            {
                Eq(true, DwmBoost.Activate()); Eq(true, DwmBoost.Activate()); Eq(1, dwmCalls);
                dwmFail = true; Eq(false, DwmBoost.Restore());
                dwmFail = false; Eq(true, DwmBoost.Restore()); Eq(3, dwmCalls);
                Eq(true, DwmBoost.Restore()); Eq(3, dwmCalls);
            }
            finally { DwmBoost.RequestForTest = null; }
            int control = 2, nativeMode = 1, writes = 0; bool readable = true, failReadAfterWrite = false;
            IntelEndurance.ReadForTest = delegate(out int c, out int m) { c = control; m = nativeMode; return readable; };
            IntelEndurance.WriteForTest = delegate(int c, int m)
            { writes++; control = c; nativeMode = m; if (failReadAfterWrite) readable = false; return true; };
            try
            {
                Eq(true, IntelEndurance.Activate()); Eq(0, control); Eq(true, IntelEndurance.HasResidue);
                Eq(true, IntelEndurance.Restore()); Eq(2, control); Eq(false, IntelEndurance.HasResidue);
                failReadAfterWrite = true;
                Eq(false, IntelEndurance.Activate()); Eq(true, IntelEndurance.HasResidue);
                int before = writes;
                Eq(false, IntelEndurance.Restore()); Eq(before, writes);
                readable = true; failReadAfterWrite = false;
                Eq(true, IntelEndurance.Restore()); Eq(2, control);
                Eq(true, IntelEndurance.Activate()); control = 1;
                before = writes;
                Eq(true, IntelEndurance.Restore()); Eq(before, writes); Eq(1, control);
                Settings.SaveStr(IntelEndurance.SnapKey, "-1|1");
                before = writes; Eq(false, IntelEndurance.Restore()); Eq(before, writes);
                Settings.SaveStr(IntelEndurance.SnapKey, "bad");
                Eq(false, IntelEndurance.Restore()); Eq(true, IntelEndurance.HasResidue);
            }
            finally
            {
                IntelEndurance.ReadForTest = null; IntelEndurance.WriteForTest = null;
                Settings.SaveStr(IntelEndurance.SnapKey, "");
                Settings.UseTransientStoreForCurrentProcess();
            }
            RunExtremePowerRecoveryTests();
            RunExtremeTimerTests();
            RunEnvironmentPreferenceTests();
            Console.WriteLine("PASS Extreme mode transitions / owned power / Endurance recovery; system writes mocked");
        }

        private static void RunEnvironmentPreferenceTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.Save(AutomaticPolicy.VersionKey, true);
            var mode = new GameMode(System.IO.Path.GetTempPath(), new SuppressionCore());
            var profile = new GameProfile { Id = "environment-is-global" };
            string[] keys = { PolicyCatalog.KeyTimerTick, PolicyCatalog.KeyGlobalTimer, PolicyCatalog.KeyCacheWarm };
            foreach (string key in keys)
            {
                profile.Overrides[key] = "1";
                Eq("0", PolicyResolver.For(profile).ValueOf(key));
                Eq(false, PolicyResolver.SetOverride(profile, key, "1"));
                Eq(true, mode.SetEnvironmentPreference(key, true));
                foreach (PerformancePreset preset in new[] { PerformancePreset.Standard, PerformancePreset.Competitive, PerformancePreset.Handheld, PerformancePreset.Extreme })
                {
                    mode.Preset = preset;
                    Eq("1", PolicyResolver.For(profile).ValueOf(key));
                    Eq(true, AutomaticPolicy.EnvironmentPreference(key));
                }
                Eq(true, mode.SetEnvironmentPreference(key, false));
                Eq("0", PolicyResolver.For(profile).ValueOf(key));
            }
            Eq(true, mode.SetEnvironmentPreference(PolicyCatalog.KeyCacheWarm, true));
            Settings.SuspendWritesForReset();
            Eq(false, mode.SetEnvironmentPreference(PolicyCatalog.KeyCacheWarm, false));
            Eq(true, mode.CacheWarmOn); Eq(true, AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyCacheWarm));
            Settings.UseTransientStoreForCurrentProcess();
            bool tick = false, global = false; int writes = 0;
            var timer = new TimerModeController {
                Boot = delegate { return TimerFixtureBoot(1); }, TickOn = delegate { return tick; }, GlobalOn = delegate { return global; },
                TickOwned = delegate { return tick; }, GlobalOwned = delegate { return global; },
                TickEnable = delegate { writes++; tick = true; return true; }, TickRestore = delegate { writes++; tick = false; return true; },
                GlobalEnable = delegate { writes++; global = true; return true; }, GlobalRestore = delegate { writes++; global = false; return true; }
            };
            long now = DateTime.UtcNow.Ticks;
            timer.Sync(true, false, 26100, now); Eq(true, tick); Eq(false, global); Eq(1, writes);
            timer.Sync(false, true, 26100, ++now); Eq(false, tick); Eq(true, global); Eq(3, writes);
            Eq("restore-pending-reboot", timer.TickState); Eq("pending-reboot", timer.GlobalState);
            timer.Sync(false, true, 26100, ++now); Eq(3, writes);
            timer.Sync(false, false, 26100, ++now); Eq(false, tick); Eq(false, global); Eq(4, writes);
            Settings.UseTransientStoreForCurrentProcess();
            Console.WriteLine("PASS independent Environment switches: all modes, profile isolation, failed saves and independent timer restoration");
        }

        private static void RunExtremeTimerTests()
        {
            CheckRebootIdentityRegression();
            Settings.UseTransientStoreForCurrentProcess();
            bool tick = false, global = false, tickOwned = false, globalOwned = false;
            bool fail = false; string boot = TimerFixtureBoot(1); int writes = 0;
            var timer = new TimerModeController {
                Boot = delegate { return boot; }, TickOn = delegate { return tick; },
                GlobalOn = delegate { return global; }, TickOwned = delegate { return tickOwned; },
                GlobalOwned = delegate { return globalOwned; },
                TickEnable = delegate { writes++; if (fail) return false; tick = tickOwned = true; return true; },
                GlobalEnable = delegate { writes++; if (fail) return false; global = globalOwned = true; return true; },
                TickRestore = delegate { writes++; if (fail) return false; tick = tickOwned = false; return true; },
                GlobalRestore = delegate { writes++; if (fail) return false; global = globalOwned = false; return true; }
            };
            long now = DateTime.UtcNow.Ticks;
            timer.Sync(true, true, 26100, now); Eq(2, writes);
            Eq("pending-reboot", timer.TickState); Eq(false, timer.GlobalReady);
            timer.Sync(true, true, 26100, now + 1); Eq(2, writes);
            now += TimeSpan.FromMinutes(6).Ticks;
            timer.Sync(true, true, 26100, now); Eq(false, timer.GlobalReady); Eq(2, writes);
            boot = TimerFixtureBoot(2); now += TimeSpan.FromMinutes(6).Ticks;
            timer.Sync(true, true, 26100, now); Eq("ready", timer.TickState); Eq(true, timer.GlobalReady);
            fail = true; timer.Sync(false, false, 26100, ++now);
            Eq("restore-failed", timer.TickState); Eq("restore-failed", timer.GlobalState);
            fail = false; now += TimeSpan.FromSeconds(31).Ticks;
            timer.Sync(false, false, 26100, now); Eq("restore-pending-reboot", timer.TickState);
            Eq(false, tickOwned); Eq(false, globalOwned);
            boot = TimerFixtureBoot(3); now += TimeSpan.FromMinutes(6).Ticks;
            timer.Sync(false, false, 26100, now); Eq("restored", timer.TickState);
            timer.Sync(true, true, 19045, ++now); Eq("unsupported", timer.GlobalState); Eq(false, global);
            timer.Sync(false, false, 19045, ++now); fail = true;
            timer.Sync(true, true, 26100, ++now); Eq("stopped", timer.TickState); Eq("stopped", timer.GlobalState);
            int before = writes; timer.Sync(true, true, 26100, ++now); Eq(before, writes);
            boot = null; now += TimeSpan.FromMinutes(6).Ticks;
            timer.Sync(true, true, 26100, now); Eq(before, writes); Eq(false, timer.GlobalReady);

            int requests = 0, releases = 0; bool requestFail = true, releaseFail = true;
            var lease = new TimerResolutionLease();
            lease.Query = delegate(out uint coarsest, out uint finest, out uint current) {
                coarsest = 156250; finest = current = 10000; return 0;
            };
            lease.Set = delegate(uint desired, bool acquire, out uint actual) {
                Eq((uint)5000, desired); actual = 10000;
                if (acquire) { requests++; return requestFail ? -1 : 0; }
                releases++; return releaseFail ? -1 : 0;
            };
            Eq(false, lease.Update(true, now)); Eq(false, lease.Active);
            Eq(false, lease.Update(true, ++now)); Eq(1, requests);
            requestFail = false; now += TimeSpan.FromSeconds(31).Ticks;
            Eq(false, lease.Update(true, now)); Eq(1, requests);
            Eq(true, ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(TimerResolutionLease.FailureAction, "system")));
            Eq(true, lease.Update(true, now)); Eq(true, lease.Active); Eq((uint)10000, lease.Actual);
            Eq(false, lease.TargetReached); Eq("timer-limited", lease.State);
            Eq(true, lease.Update(true, ++now)); Eq(2, requests);
            Eq(false, lease.Update(false, ++now)); Eq(true, lease.Active);
            releaseFail = false; now += TimeSpan.FromSeconds(31).Ticks; Eq(true, lease.Update(false, now)); Eq(false, lease.Active);
            Eq(true, lease.Update(false, ++now)); Eq(2, releases);
            RunTimerResolutionVerificationTests();

            Settings.UseTransientStoreForCurrentProcess();
            var values = new[] { ReversibleReg.Absent, "yes", ReversibleReg.Absent };
            bool dropWrite = false;
            TimerTickTweak.BcdForTest = delegate(string args, out int code) {
                code = 0;
                if (args.StartsWith("/enum", StringComparison.Ordinal)) {
                    string result = "Windows Boot Loader\n";
                    for (int i = 0; i < values.Length; i++)
                        if (values[i] != ReversibleReg.Absent) result += TimerTickTweak.Names[i] + " " + values[i] + "\n";
                    return result;
                }
                string[] parts = args.Split(' ');
                Eq("{current}", parts[1]);
                int index = Array.IndexOf(TimerTickTweak.Names, parts[2]);
                if (!dropWrite) values[index] = parts[0] == "/set" ? parts[3] : ReversibleReg.Absent;
                return "ok";
            };
            try {
                dropWrite = true; Eq(false, TimerTickTweak.Enable()); Eq(true, TimerTickTweak.OwnsState);
                dropWrite = false; Eq(true, TimerTickTweak.Enable());
                values[0] = "yes";
                Eq(true, TimerTickTweak.Restore()); Eq("yes", values[0]); Eq("yes", values[1]);
                Eq(ReversibleReg.Absent, values[2]); Eq(false, TimerTickTweak.OwnsState);
            }
            finally { TimerTickTweak.BcdForTest = null; Settings.UseTransientStoreForCurrentProcess(); }
            Console.WriteLine("PASS Extreme timers: mode/reboot/denial/backoff/restore/readback/paired requests; all writes mocked");
        }

        private static void RunTimerResolutionVerificationTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            long now = DateTime.UtcNow.Ticks;
            int acquired = 0, released = 0, references = 0, queries = 0;
            uint current = 156250, finest = 5000;
            bool queryWorks = true, releaseWorks = true, acquireWorks = true, honorRequest = true;
            var lease = new TimerResolutionLease();
            lease.Query = delegate(out uint coarse, out uint fine, out uint actual) {
                queries++; coarse = 156250; fine = finest; actual = current; return queryWorks ? 0 : -1;
            };
            lease.Set = delegate(uint desired, bool acquire, out uint actual) {
                Eq((uint)5000, desired);
                if (acquire) {
                    acquired++;
                    if (!acquireWorks) { actual = current; return -1; }
                    references++; Eq(1, references); if (honorRequest) current = 5000;
                } else {
                    released++;
                    if (!releaseWorks) { actual = current; return -1; }
                    references--; Eq(0, references); current = 156250;
                }
                actual = current; return 0;
            };
            Func<bool> retry = delegate { return ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(TimerResolutionLease.FailureAction, "system")); };
            Eq(5, TimerResolutionLease.AuditSeconds);
            Eq(true, lease.Update(true, now)); Eq("timer-locked", lease.State); Eq(true, lease.TargetReached); Eq(1, queries);
            Eq(true, lease.Update(true, ++now)); Eq(1, acquired); Eq(1, queries);
            current = 10000; now += TimeSpan.FromSeconds(6).Ticks;
            Eq(true, lease.Update(true, now)); Eq(2, acquired); Eq(1, released); Eq(1, references);
            Eq(true, lease.Update(false, ++now)); Eq(0, references);

            finest = current = 10000; honorRequest = false;
            Eq(true, lease.Update(true, ++now)); Eq("timer-limited", lease.State);
            int before = acquired, beforeReleased = released;
            for (int i = 0; i < 100; i++) {
                now += TimeSpan.FromMinutes(1).Ticks; Eq(true, lease.Update(true, now));
                Eq(before, acquired); Eq(beforeReleased, released); Eq(1, references);
            }
            Eq(0, ActionFailureStore.Records().Count); Eq(true, lease.Update(false, ++now)); Eq(0, references);

            finest = 5000; honorRequest = true; queryWorks = false;
            Eq(false, lease.Update(true, ++now)); Eq("stopped", lease.State); Eq(0, references);
            before = acquired; beforeReleased = released; int beforeQueries = queries;
            for (int i = 0; i < 100; i++) {
                now += TimeSpan.FromMinutes(1).Ticks; Eq(false, lease.Update(true, now));
                Eq(before, acquired); Eq(beforeReleased, released); Eq(beforeQueries, queries);
            }
            queryWorks = true; lease.ResetAcquireAttempts(); Eq(false, lease.Update(true, ++now));
            Eq(true, retry()); Eq(true, lease.Update(true, ++now)); Eq("timer-locked", lease.State);
            Eq(true, lease.Update(false, ++now)); Eq(0, references);

            honorRequest = false; current = 10000;
            Eq(false, lease.Update(true, ++now)); Eq("stopped", lease.State); Eq(0, references);
            before = acquired;
            for (int i = 0; i < 100; i++) { now += TimeSpan.FromMinutes(10).Ticks; Eq(false, lease.Update(true, now)); Eq(before, acquired); }
            honorRequest = true; Eq(true, retry()); Eq(true, lease.Update(true, ++now)); Eq(true, lease.Update(false, ++now)); Eq(0, references);

            acquireWorks = false;
            Eq(false, lease.Update(true, ++now)); Eq(0, references); before = acquired;
            for (int i = 0; i < 100; i++) { now += TimeSpan.FromMinutes(10).Ticks; Eq(false, lease.Update(true, now)); Eq(before, acquired); }
            acquireWorks = true; Eq(true, retry()); Eq(true, lease.Update(true, ++now)); Eq(true, lease.Update(false, ++now)); Eq(0, references);

            Eq(true, lease.Update(true, ++now)); current = 10000; releaseWorks = false;
            before = acquired; now += TimeSpan.FromSeconds(6).Ticks;
            Eq(false, lease.Update(true, now)); Eq("restore-failed", lease.State); Eq(before, acquired); Eq(1, references);
            foreach (int seconds in new[] { 30, 60, 120, 240, 300, 300 }) {
                int expectedRelease = released;
                for (int elapsed = 5; elapsed < seconds; elapsed += 5) {
                    Eq(false, lease.Update(true, now + TimeSpan.FromSeconds(elapsed).Ticks));
                    Eq(expectedRelease, released); Eq(before, acquired); Eq(1, references);
                }
                now += TimeSpan.FromSeconds(seconds).Ticks;
                Eq(false, lease.Update(true, now)); Eq(expectedRelease + 1, released); Eq(before, acquired); Eq(1, references);
            }
            releaseWorks = true; now += TimeSpan.FromSeconds(300).Ticks;
            Eq(false, lease.Update(true, now)); Eq(0, references); Eq(before, acquired);
            Eq(true, lease.Update(false, ++now)); Eq(false, lease.HasResidue);
            Eq(true, retry()); Eq(true, lease.Update(true, ++now)); Eq(before + 1, acquired);
            Eq(true, lease.Update(false, ++now)); Eq(0, references);

            int pairs = 0; var informational = new TimerResolutionLease();
            informational.Set = delegate(uint desired, bool acquire, out uint actual) { pairs += acquire ? 1 : -1; actual = 4992; return 1; };
            informational.Query = delegate(out uint coarse, out uint fine, out uint actual) { coarse = 156250; fine = actual = 4992; return 1; };
            Eq(true, informational.Update(true, now)); Eq(true, informational.TargetReached); Eq((uint)4992, informational.Actual);
            Eq(true, informational.Update(false, ++now)); Eq(0, pairs);
            Console.WriteLine("PASS timer resolution: first-failure isolation, explicit retry, integer readback, system limits, one paired drift repair, bounded restoration and balanced mocked NT requests");
        }
        private static void RunExtremePowerRecoveryTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            foreach (string vendor in new[] { "nv", "amd" })
            {
                long original = vendor == "nv" ? 100000 : 0, maximum = vendor == "nv" ? 110000 : 15;
                long current = original; int writes = 0; bool readable = true, failWrite = false;
                string id = "fixture";
                GpuPowerMax.ReadForTest = delegate(string v) { return !readable || v != vendor ? null
                    : new GpuPowerMax.State { Vendor = v, Id = id, Current = current, Maximum = maximum }; };
                GpuPowerMax.WriteForTest = delegate(GpuPowerMax.State state, long value)
                { Eq(true, GpuPowerMax.HasResidue()); writes++; current = value; return !failWrite; };
                try
                {
                    string receipt = vendor + ":fixture:" + original + ":" + maximum;
                    Settings.SaveStr(GpuPowerMax.SnapKey, receipt); current = maximum;
                    readable = false; int before = writes;
                    Eq(false, GpuPowerMax.Restore()); Eq(before, writes); Eq(true, GpuPowerMax.HasResidue());
                    readable = true; Eq(true, GpuPowerMax.Restore()); Eq(original, current);
                    Settings.SaveStr(GpuPowerMax.SnapKey, receipt); current = maximum;
                    failWrite = true; Eq(false, GpuPowerMax.Restore()); Eq(true, GpuPowerMax.HasResidue());
                    failWrite = false; Eq(true, GpuPowerMax.Restore()); Eq(original, current);
                    Settings.SaveStr(GpuPowerMax.SnapKey, receipt); current = maximum;
                    id = "replacement"; before = writes;
                    Eq(false, GpuPowerMax.Restore()); Eq(before, writes); Eq(true, GpuPowerMax.HasResidue());
                    id = "fixture"; current = original + 1;
                    Eq(true, GpuPowerMax.Restore()); Eq(before, writes); Eq(original + 1, current);
                    current = maximum; before = writes;
                    Eq(true, GpuPowerMax.Restore()); Eq(before, writes); Eq(maximum, current);
                    Settings.SaveStr(GpuPowerMax.SnapKey, receipt);
                    GpuPowerMax.DenySaveForTest = true;
                    Eq(false, GpuPowerMax.Restore()); Eq(original, current); Eq(true, GpuPowerMax.HasResidue());
                    GpuPowerMax.DenySaveForTest = false;
                    Eq(true, GpuPowerMax.Restore()); Eq(false, GpuPowerMax.HasResidue());
                    Settings.SaveStr(GpuPowerMax.SnapKey, "malformed");
                    Eq(false, GpuPowerMax.Restore()); Eq(true, GpuPowerMax.HasResidue());
                }
                finally
                {
                    GpuPowerMax.DenySaveForTest = false; GpuPowerMax.ReadForTest = null; GpuPowerMax.WriteForTest = null;
                    Settings.UseTransientStoreForCurrentProcess();
                }
            }
        }
    }
}
#endif
