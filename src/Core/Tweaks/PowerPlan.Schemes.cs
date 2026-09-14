// @author bdth 2074055628@qq.com
// File purpose powrprof mechanical layer for power schemes: enumeration, name read/write, create/delete, and per-item writes to the managed scheme
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        private const uint AccessScheme = 16;

        [DllImport("powrprof.dll")] private static extern uint PowerReadValueMin(IntPtr root, ref Guid sub, ref Guid setting, out uint min);
        [DllImport("powrprof.dll")] private static extern uint PowerReadValueMax(IntPtr root, ref Guid sub, ref Guid setting, out uint max);
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);
        [DllImport("powrprof.dll")] private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid src, ref IntPtr dest);
        [DllImport("powrprof.dll")] private static extern uint PowerDeleteScheme(IntPtr root, ref Guid guid);
        [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(
            IntPtr root, IntPtr scheme, IntPtr subgroup, uint accessFlags, uint index, byte[] buffer, ref uint size);
        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, IntPtr buffer, ref uint size);
        [DllImport("powrprof.dll", EntryPoint = "PowerReadFriendlyName")]
        private static extern uint PowerReadFriendlyNameBuf(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, ref uint size);
        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);
        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteDescription(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

        private static readonly Guid SubVideo     = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        private static readonly Guid VideoBrightness    = new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb");
        private static readonly Guid VideoDimBrightness = new Guid("f1fbfde2-a960-4165-9f88-50667911ce96");
        private static readonly Guid VideoAdaptive      = new Guid("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");

        private static readonly Guid SubNone      = new Guid("fea3413e-7e05-4911-9a71-700331f1c294");
        private static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        private static readonly Guid SubPcie      = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
        private static readonly Guid SubUsb       = new Guid("2a737441-1930-4402-8d77-b2bebba308a3");
        private static readonly Guid SubDisk      = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
        private static readonly Guid SubWireless  = new Guid("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");

        private static readonly Guid Personality       = new Guid("245d8541-3943-4422-b025-13a784f679b7");
        private static readonly Guid ProcThrottleMin   = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
        private static readonly Guid ProcThrottleMax   = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
        private static readonly Guid CpMinCores        = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
        private static readonly Guid CpMaxCores        = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028");
        private static readonly Guid PerfBoostMode     = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        private static readonly Guid Throttling        = new Guid("3b04d4fd-1cc7-4f23-ab1c-d1337819c4bb");
        private static readonly Guid SysCoolPol        = new Guid("94d3a615-a899-4ac5-ae2b-e4d8f634367f");
        private static readonly Guid PcieAspm          = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");
        private static readonly Guid UsbSelSuspend     = new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        private static readonly Guid DiskIdle          = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");
        // Waking NVMe from non-operational power states is millisecond-scale tail latency; the proper lever against demotion is latency tolerance, not the timeout
        //   After the timeout expires the driver only picks states whose ENLAT+EXLAT fits within the tolerance; tolerance 0 means no state qualifies
        //   Microsoft's own High performance scheme is written this way; timeout 0 semantics are undocumented, leave it alone
        //   AHCI LPM wake stutter is confirmed by Event 129; keep the link Active during the match
        private static readonly Guid NvmeLatTolPrimary   = new Guid("fc95af4d-40e7-4b6d-835a-56d131dbc80e");
        private static readonly Guid NvmeLatTolSecondary = new Guid("dbc9e238-6de9-49e3-92cd-8c2b4946b472");
        private static readonly Guid AhciLpm             = new Guid("0b2d69d7-a2a1-449c-9680-f91c70521c60");

        // USB3 link U1/U2 low-power exit is microsecond-to-millisecond scale, same path as selective suspend; off during the match
        private static readonly Guid Usb3Lpm           = new Guid("d4e98f31-5ffe-4ce1-be31-1b38b384c009");
        // Vendor-injected graphics subgroup in the scheme, present only on machines with the matching driver; skip the whole item when SettingPresent is unreadable
        //   Intel iGPU: 0 max battery, 1 balanced, 2 max performance; all devices and tiers use balanced for both AC/DC
        //   Switchable graphics: 0 force power-saving GPU, 1 optimize power, 2 optimize performance, 3 max performance
        private static readonly Guid SubIntelGfx       = new Guid("44f3beca-a7c0-460e-9df2-bb8b99e0cba6");
        private static readonly Guid IntelGfxPlan      = new Guid("3619c3f2-afb2-4afc-b0e9-e7fef372de36");
        private static readonly Guid SubSwitchGfx      = new Guid("e276e160-7cb0-43c6-b20b-73f5dce39954");
        private static readonly Guid SwitchGfxPolicy   = new Guid("a1662ab2-9d34-4e53-ba8b-2639b9e20857");

        // Exclusive to the idle policy toggle, not the same as hardware wake latency or disabling idle
        //   These items are unexposed on most schemes; skip the whole item when SettingPresent is unreadable, do not corrupt the scheme
        private static readonly Guid IdlePromote      = new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c");
        private static readonly Guid IdleDemote       = new Guid("4b92d758-5a24-4851-a470-815d78aee119");
        private static readonly Guid IdleScaling      = new Guid("6c2993b0-8f48-481f-bcc6-00dd2742aa06");

        private static readonly Guid PerfEpp           = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        private static readonly Guid PerfAutonomous    = new Guid("8baa4a8a-14c6-4451-8e8b-14bdbd197537");
        private static readonly Guid PerfBoostPol      = new Guid("45bcc044-d885-43e2-8605-ee0ec6e96b59");
        private static readonly Guid PerfIncPol        = new Guid("465e1f50-b610-473a-ab58-00d1077dc418");
        private static readonly Guid PerfDecPol        = new Guid("40fbefc7-2e9d-4d25-a185-0cfd8574bac6");
        private static readonly Guid PerfIncTime       = new Guid("984cf492-3bed-4488-a8f9-4286c97bf5aa");
        private static readonly Guid PerfDecTime       = new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c8");
        private static readonly Guid PerfIncThreshold  = new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35d");
        private static readonly Guid PerfDecThreshold  = new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6");
        private static readonly Guid LatencyHintPerf   = new Guid("619b7505-003b-4e82-b7a6-4dd29c300971");
        private static readonly Guid LatencyHintUnpark = new Guid("616cdaa5-695e-4545-97ad-97dc2d1bdd88");
        private static readonly Guid PerfDutyCycling   = new Guid("4e4450b3-6179-4e91-b8f1-5bb9938f81a1");
        private static readonly Guid ProcFreqMax       = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        private static readonly Guid WirelessPowerSave = new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a");

        private static readonly Guid ProcThrottleMin1  = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964d");
        private static readonly Guid ProcThrottleMax1  = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed");
        private static readonly Guid CpMinCores1       = new Guid("0cc5b647-c1df-4637-891a-dec35c318584");
        private static readonly Guid CpMaxCores1       = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334029");
        private static readonly Guid PerfEpp1          = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");
        private static readonly Guid SchedPolicy       = new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d");
        private static readonly Guid ShortSchedPolicy  = new Guid("bae08b81-2d5e-4688-ad6a-13243356654b");
        // Efficiency class 1 half of the perf increase/decrease and latency-sensitivity items, GUID last digit one higher than class 0
        //   Writing P-cores aggressive while E-cores keep the system default means game helper threads landing on E-cores ramp under Balanced criteria
        //   Under HWP autonomous mode the six increase/decrease policy, time and threshold items are no-ops; the real delta is boost policy and the two latency-sensitivity items
        private static readonly Guid PerfBoostPol1      = new Guid("45bcc044-d885-43e2-8605-ee0ec6e96b5a");
        private static readonly Guid PerfIncPol1        = new Guid("465e1f50-b610-473a-ab58-00d1077dc419");
        private static readonly Guid PerfDecPol1        = new Guid("40fbefc7-2e9d-4d25-a185-0cfd8574bac7");
        private static readonly Guid PerfIncTime1       = new Guid("984cf492-3bed-4488-a8f9-4286c97bf5ab");
        private static readonly Guid PerfDecTime1       = new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c9");
        private static readonly Guid PerfIncThreshold1  = new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35e");
        private static readonly Guid PerfDecThreshold1  = new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a7");
        private static readonly Guid LatencyHintPerf1   = new Guid("619b7505-003b-4e82-b7a6-4dd29c300972");
        private static readonly Guid LatencyHintUnpark1 = new Guid("616cdaa5-695e-4545-97ad-97dc2d1bdd89");

        private static readonly Guid IdleDisableSet    = new Guid("5d76a2ca-e8c0-402f-a133-2158492d58ad");

        private struct Knob
        {
            public readonly Guid Sub;
            public readonly Guid Setting;
            public readonly uint ArenaAc, ArenaDc, CalmAc, CalmDc;
            private readonly string labelKey;
            public string Label { get { return Lang.T(labelKey); } }
            public Knob(Guid sub, Guid setting, uint arenaAc, uint arenaDc, uint calmAc, uint calmDc, string label)
            {
                Sub = sub; Setting = setting;
                ArenaAc = arenaAc; ArenaDc = arenaDc; CalmAc = calmAc; CalmDc = calmDc;
                labelKey = label;
            }
        }

        private static readonly Knob[] CoreKnobs = new Knob[]
        {
            new Knob(SubProcessor, ProcThrottleMin, 100, 100,  20, 10, "t.powerplanschemes.1"),
            new Knob(SubProcessor, ProcThrottleMax, 100, 100, 100, 100, "t.powerplanschemes.2"),
            new Knob(SubProcessor, CpMinCores,      100, 100,  50, 20, "t.powerplanschemes.3"),
            new Knob(SubProcessor, CpMaxCores,      100, 100, 100, 100, "t.powerplanschemes.4"),
            new Knob(SubProcessor, PerfBoostMode,     2,   2,   2,  3, "t.powerplanschemes.5"),
            new Knob(SubProcessor, Throttling,        0,   0,   2,  2, "t.powerplanschemes.6"),
            new Knob(SubProcessor, SysCoolPol,        1,   1,   1,  1, "t.powerplanschemes.7"),
            new Knob(SubPcie,      PcieAspm,          0,   0,   1,  2, "t.powerplanschemes.8"),
            new Knob(SubUsb,       UsbSelSuspend,     0,   0,   0,  0, "t.powerplanschemes.9"),
            new Knob(SubDisk,      DiskIdle,          0,   0, 1200, 600, "t.powerplanschemes.10"),
            new Knob(SubNone,      Personality,       1,   1,   1,  1, "t.powerplanschemes.11"),
        };

        private static readonly Knob[] OptionalKnobs = new Knob[]
        {
            new Knob(SubProcessor, PerfEpp,           0,   0,  32, 70, "t.powerplanschemes.12"),
            new Knob(SubProcessor, PerfBoostPol,    100, 100,  60, 40, "t.powerplanschemes.13"),
            new Knob(SubProcessor, PerfIncPol,        2,   2,   1,  1, "t.powerplanschemes.14"),
            new Knob(SubProcessor, PerfDecPol,        1,   1,   2,  2, "t.powerplanschemes.15"),
            new Knob(SubProcessor, PerfIncTime,       1,   1,   3,  3, "t.powerplanschemes.16"),
            new Knob(SubProcessor, PerfDecTime,      10,  10,   5,  5, "t.powerplanschemes.17"),
            new Knob(SubProcessor, PerfIncThreshold, 10,  10,  30, 40, "t.powerplanschemes.18"),
            new Knob(SubProcessor, PerfDecThreshold,  8,   8,  20, 30, "t.powerplanschemes.19"),
            new Knob(SubProcessor, LatencyHintPerf, 100, 100,  75, 50, "t.powerplanschemes.20"),
            new Knob(SubProcessor, LatencyHintUnpark,100,100,  50, 50, "t.powerplanschemes.21"),
            new Knob(SubProcessor, PerfDutyCycling,   0,   0,   0,  1, "t.powerplanschemes.22"),
            new Knob(SubProcessor, ProcFreqMax,       0,   0,   0,  0, "t.powerplanschemes.23"),
            new Knob(SubWireless,  WirelessPowerSave, 0,   0,   1,  2, "t.powerplanschemes.24"),
            new Knob(SubDisk,      NvmeLatTolPrimary,   0, 0,  15,  50, "t.powerplanschemes.35"),
            new Knob(SubDisk,      NvmeLatTolSecondary, 0, 0, 100, 100, "t.powerplanschemes.36"),
            new Knob(SubDisk,      AhciLpm,             0, 0,   0,   1, "t.powerplanschemes.37"),
            new Knob(SubUsb,       Usb3Lpm,             0, 0,   2,   3, "t.powerplanschemes.43"),
            new Knob(SubIntelGfx,  IntelGfxPlan,        1, 1,   1,   1, "t.powerplanschemes.44"),
            new Knob(SubSwitchGfx, SwitchGfxPolicy,     3, 3,   1,   1, "t.powerplanschemes.54"),
        };

        private static readonly Knob[] HybridKnobs = new Knob[]
        {
            new Knob(SubProcessor, ProcThrottleMin1, 100, 100,  20, 10, "t.powerplanschemes.25"),
            new Knob(SubProcessor, ProcThrottleMax1, 100, 100, 100, 100, "t.powerplanschemes.26"),
            new Knob(SubProcessor, CpMinCores1,      100, 100,  50, 20, "t.powerplanschemes.27"),
            new Knob(SubProcessor, CpMaxCores1,      100, 100, 100, 100, "t.powerplanschemes.28"),
            new Knob(SubProcessor, PerfEpp1,           0,   0,  32, 70, "t.powerplanschemes.29"),
            new Knob(SubProcessor, SchedPolicy,        2,   2,   5,  5, "t.powerplanschemes.30"),
            new Knob(SubProcessor, ShortSchedPolicy,   2,   2,   5,  5, "t.powerplanschemes.31"),
            new Knob(SubProcessor, PerfBoostPol1,    100, 100,  60, 40, "t.powerplanschemes.45"),
            new Knob(SubProcessor, PerfIncPol1,        2,   2,   1,  1, "t.powerplanschemes.46"),
            new Knob(SubProcessor, PerfDecPol1,        1,   1,   2,  2, "t.powerplanschemes.47"),
            new Knob(SubProcessor, PerfIncTime1,       1,   1,   3,  3, "t.powerplanschemes.48"),
            new Knob(SubProcessor, PerfDecTime1,      10,  10,   5,  5, "t.powerplanschemes.49"),
            new Knob(SubProcessor, PerfIncThreshold1, 10,  10,  30, 40, "t.powerplanschemes.50"),
            new Knob(SubProcessor, PerfDecThreshold1,  8,   8,  20, 30, "t.powerplanschemes.51"),
            new Knob(SubProcessor, LatencyHintPerf1, 100, 100,  75, 50, "t.powerplanschemes.52"),
            new Knob(SubProcessor, LatencyHintUnpark1,100,100,  50, 50, "t.powerplanschemes.53"),
        };

        // Extra set added by the idle policy toggle; written only while it is on, restored from snapshot when turned off
        //   Two kinds: pushing existing knobs to the end of their range, and idle behaviors the Esports column never touched
        //   Units are system-defined; every write goes through Clamp to this machine's allowed range
        private static readonly Knob[] ExtremeKnobs = new Knob[]
        {
            // Raise the threshold for promoting to deeper idle states; leave the system's original demotion threshold alone
            // IdleDemote demotes only when idle ratio drops below the threshold; 0 does not mean faster exit
            // Do not flip it to another untested extreme value; old writes are restored by snapshot migration
            new Knob(SubProcessor, IdlePromote,     100, 100, 100, 100, "t.powerplanschemes.39"),
            // Idle check period: c4581c31 once wrote 0 here hoping Clamp would floor it, this machine's range is 1~200000 microseconds
            //   The write never succeeded, one item failed every match; the kernel check period follows the timer tick, so 1 vs default 50000 makes no measurable difference
            //   Nothing supports it, item removed; if an old snapshot has it, RestoreExtremeKnobs still writes it back by GUID
            // Disable scaling the idle threshold by current performance state; hardware C-state exit latency is untouched
            new Knob(SubProcessor, IdleScaling,       0,   0,   0,   0, "t.powerplanschemes.42"),
        };

#if PAVISE_SELFTEST
        // Members and values of the idle policy group, for regression checks, triggers no writes
        internal static int ExtremeKnobCountForTest { get { return ExtremeKnobs.Length; } }

        internal static bool ExtremeOnlyGuidForTest(Guid setting)
        {
            foreach (Knob k in CoreKnobs) if (k.Setting == setting) return false;
            foreach (Knob k in OptionalKnobs) if (k.Setting == setting) return false;
            foreach (Knob k in HybridKnobs) if (k.Setting == setting) return false;
            foreach (Knob k in ExtremeKnobs) if (k.Setting == setting) return true;
            return false;
        }

        internal static Guid[] ExtremeKnobGuidsForTest()
        {
            var list = new List<Guid>();
            foreach (Knob k in ExtremeKnobs) list.Add(k.Setting);
            return list.ToArray();
        }
        internal static List<string> DescribeKnobs()
        {
            var lines = new List<string>();
            foreach (Knob k in CoreKnobs) lines.Add(DescribeKnob(k));
            foreach (Knob k in OptionalKnobs) lines.Add(DescribeKnob(k));
            foreach (Knob k in HybridKnobs) lines.Add(DescribeKnob(k));
            foreach (Knob k in ExtremeKnobs) lines.Add(DescribeKnob(k));
            return lines;
        }

        private static string DescribeKnob(Knob k)
        {
            return k.Label + " arenaAc=" + k.ArenaAc + " arenaDc=" + k.ArenaDc
                + " calmAc=" + k.CalmAc + " calmDc=" + k.CalmDc;
        }
#endif

        private static PowerPlanProfile cachedProfile;

        private static PowerPlanProfile CurrentProfile()
        {
            if (cachedProfile != null) return cachedProfile;
            bool amd = false;
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (k != null)
                        amd = ((k.GetValue("ProcessorNameString") as string) ?? "")
                            .IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }
            cachedProfile = PowerPlanProfile.Resolve(amd, CpuTopology.Hybrid,
                CpuTopology.AsymCache, CpuTopology.PartitionTag);
            return cachedProfile;
        }

        // Set when the idle policy group is not fully configured; callers then skip caching tuneState and finish on the next configure
        private static bool extremeTunePending;

        internal static bool ExtremeTunePending { get { return extremeTunePending; } }

        private static bool TuneTarget(Guid g, bool aggressive, bool handheld, bool extreme)
        {
            try
            {
                PowerPlanProfile profile = CurrentProfile();
                bool? autonomousAc, autonomousDc;
                ReadAutonomousScaling(g, out autonomousAc, out autonomousDc);
                int written = 0;
                int failed = 0;
                var skipped = new List<string>();

                foreach (Knob k in CoreKnobs)
                {
                    if (aggressive && IsProcessorMinimum(k.Setting)
                        && !autonomousAc.HasValue && !autonomousDc.HasValue) { skipped.Add(k.Label); continue; }
                    if (!SettingPresent(g, k.Sub, k.Setting)) { skipped.Add(k.Label); continue; }
                    if (WriteKnob(g, k, aggressive, handheld, profile,
                        autonomousAc, autonomousDc)) written++;
                    else { failed++; LogKnobFailure(k); }
                }
                foreach (Knob k in OptionalKnobs)
                {
                    if (!SettingPresent(g, k.Sub, k.Setting)) { skipped.Add(k.Label); continue; }
                    Knob effective = k;
                    if (k.Setting == IntelGfxPlan)
                    {
                        GpuAdapter[] adapters = null;
                        if (aggressive && handheld)
                            try { adapters = GpuInventory.Adapters(); } catch { }
                        uint value = ResolveIntelGraphicsPlan(aggressive, handheld, adapters);
                        effective = new Knob(k.Sub, k.Setting, value, value, value, value, k.Label);
                    }
                    if (WriteKnob(g, effective, aggressive, handheld, profile,
                        autonomousAc, autonomousDc)) written++; else failed++;
                }
                // Idle policy group: unexposed items are skipped as usual and do not affect the write results of the other knobs
                //   Snapshot current values before writing; when the toggle is turned off and the scheme rewritten, write back from the snapshot
                //   otherwise the idle policy would stay on the managed scheme forever
                // Legacy retired items are restored the same way while on; on failure keep the receipt and retry on next configure
                //   This group is an add-on; an unfinished backup or restore skips only this group, other knobs and the scheme switch proceed as usual
                //   Previously this returned false, so one incomplete receipt blocked the whole power scheme configure, seen by the user as the scheme not taking effect
                List<ExtremeSavedValue> snapshot;
                bool extremeReady = PrepareExtremeKnobs(g, extreme, out snapshot);
                if (!extremeReady)
                {
                    foreach (Knob k in ExtremeKnobs) skipped.Add(k.Label);
                }
                if (extreme && extremeReady)
                {
                    foreach (Knob k in ExtremeKnobs)
                    {
                        // After a transient read failure SettingPresent may succeed again, but items without a backup still must not be written
                        if (!snapshot.Exists(delegate(ExtremeSavedValue v) { return v.Setting == k.Setting; }))
                        { skipped.Add(k.Label); continue; }
                        if (!SettingPresent(g, k.Sub, k.Setting))
                        { extremeTunePending = true; skipped.Add(k.Label); continue; }
                        if (WriteKnob(g, k, aggressive, handheld, profile,
                            autonomousAc, autonomousDc)) written++;
                        else { extremeTunePending = true; failed++; }
                    }
                }
                if (extremeTunePending) Logger.Warn(Lang.T("log.powerplanschemes.extremePending"));
                if (CpuTopology.Hybrid && profile.WriteHetero)
                {
                    foreach (Knob k in HybridKnobs)
                    {
                        if (aggressive && IsProcessorMinimum(k.Setting)
                            && !autonomousAc.HasValue && !autonomousDc.HasValue) { skipped.Add(k.Label); continue; }
                        if (!SettingPresent(g, k.Sub, k.Setting)) { skipped.Add(k.Label); continue; }
                        Knob eff = k;
                        if (k.Setting == SchedPolicy || k.Setting == ShortSchedPolicy)
                            eff = new Knob(k.Sub, k.Setting, profile.HeteroSched, profile.HeteroSched,
                                k.CalmAc, k.CalmDc, k.Label);
                        if (WriteKnob(g, eff, aggressive, handheld, profile,
                            autonomousAc, autonomousDc)) written++; else failed++;
                    }
                }

                if (failed > 0)
                {
                    if (written > 0)
                        Logger.Warn(Lang.T("log.powerplanschemes.35") + failed + Lang.T("log.powerplanschemes.45"));
                    else
                    {
                        Logger.Log(Lang.T("log.powerplanschemes.35") + failed + Lang.T("log.powerplanschemes.36"));
                        return false;
                    }
                }

                Logger.Log(Lang.T("log.powerplanschemes.37")
                    + (!aggressive ? Lang.T("log.powerplanschemes.39")
                        : handheld ? Lang.T("log.powerplanschemes.46") : Lang.T("log.powerplanschemes.38"))
                    + " " + ManagedPlanTitle + Lang.T("log.powerplanschemes.40") + profile.Tag + Lang.T("log.powerplanschemes.41") + written + Lang.T("t.gamemodeenv.30")
                    + (profile.PreserveCoreParking ? Lang.T("log.powerplanschemes.42") : "")
                    + (skipped.Count > 0 ? Lang.T("log.powerplanschemes.43") + skipped.Count + Lang.T("log.powerplanschemes.44") + string.Join(" ", skipped.ToArray()) : ""));
                return true;
            }
            catch { return false; }
        }

        // This is the iGPU driver's power policy, distinct from CPU P-core/E-core scheduling and the Windows Balanced scheme
        // Handheld tier requests max performance only when the driver confirms the sole GPU is an Intel iGPU
        // With a dGPU present or unknown topology stay on balanced, preserving the existing desktop and laptop policy
        internal static uint ResolveIntelGraphicsPlan(bool aggressive, bool handheld, GpuAdapter[] adapters)
        {
            if (!aggressive || !handheld || adapters == null || adapters.Length != 1) return 1;
            GpuAdapter adapter = adapters[0];
            return adapter != null && adapter.Vendor == GpuVendor.Intel
                && adapter.IntegratedKnown && adapter.Integrated ? 2u : 1u;
        }

        private static void LogKnobFailure(Knob k)
        {
            // Do not log the parameters again here, that would bypass the preserve branch for unknown platforms
            Logger.Warn(Lang.T("log.powerplanschemes.32") + Lang.T(k.Label)
                + Lang.T("log.powerplanschemes.33"));
        }

        private static bool WriteKnob(Guid scheme, Knob k, bool aggressive,
            bool handheld, PowerPlanProfile profile, bool? autonomousAc, bool? autonomousDc)
        {
            bool coreParking = k.Setting == CpMinCores || k.Setting == CpMaxCores;
            bool useArena = coreParking ? profile.UseArenaCoreParking(aggressive) : aggressive;
            uint ac = useArena ? ArenaAcFor(k, k.ArenaAc, handheld, autonomousAc)
                : CalmAcFor(k, k.CalmAc, profile);
            uint dc = useArena ? ArenaDcFor(k, k.ArenaDc, autonomousDc) : k.CalmDc;
            if (useArena && IsProcessorMinimum(k.Setting))
            {
                // AC and DC are judged separately; whichever side cannot be confirmed keeps its original value; a read failure skips the whole item
                if (!ProcessorPowerPlatform.TryResolveMinimumIndices(autonomousAc, autonomousDc, ac, dc,
                    delegate(bool onAc)
                    {
                        uint original;
                        return (onAc ? ReadAc(scheme, k.Sub, k.Setting, out original)
                            : ReadDc(scheme, k.Sub, k.Setting, out original)) ? (uint?)original : null;
                    }, out ac, out dc)) return false;
            }
            return WritePair(scheme, k.Sub, k.Setting, ac, dc);
        }

        private static void ReadAutonomousScaling(Guid scheme, out bool? ac, out bool? dc)
        {
            ac = null; dc = null;
            try
            {
                ProcessorPowerPlatform.Interface platform = ProcessorPowerPlatform.Current;
                uint value;
                uint? requestedAc = ReadAc(scheme, SubProcessor, PerfAutonomous, out value) ? (uint?)value : null;
                uint? requestedDc = ReadDc(scheme, SubProcessor, PerfAutonomous, out value) ? (uint?)value : null;
                ac = ProcessorPowerPlatform.AutonomousMinimum(platform, requestedAc);
                dc = ProcessorPowerPlatform.AutonomousMinimum(platform, requestedDc);
            }
            catch { }
        }

        private static bool IsProcessorMinimum(Guid setting)
        {
            return setting == ProcThrottleMin || setting == ProcThrottleMin1;
        }

        // When autonomous frequency control is confirmed on, minimum processor state goes to a gentle value and the hardware picks the frequency
        //   09-06 A/B on an i7-9750H laptop: locked at 100 gave 230 to 250 fps, relaxed gave 270 to 280 fps
        //   On a six-core laptop power and thermals are one budget; pinning all cores at max frequency starves the busy cores of boost
        private static uint AutonomousArenaValue(Knob k, uint value, bool ac, bool? autonomous)
        {
            return autonomous == true && IsProcessorMinimum(k.Setting)
                ? (ac ? k.CalmAc : k.CalmDc) : value;
        }

        // Esports tier on laptops: the battery side relaxes pure power-saving items, symmetric to CalmArenaAcOnDesktop in the opposite direction
        //   For a long time the Esports tier's 31 knobs wrote the same values on AC and battery; the desktop split only served desktops
        //   Unplugged still wrote the AC criteria: minimum performance state 100, no core parking, all power saving off, nobody benefited
        //
        // Relax only pure power-saving items, leave anything that affects frames and input alone
        //   Do not touch ProcThrottleMax PerfEpp PerfBoostPol, the ones that decide frequency under load
        //     Measured: a full-range EPP sweep did not move frequency at all, PL1 is the real ceiling on laptops
        //     Also this area belongs to Dynamic Boost and Intel DTT, fighting them for the wheel only makes it worse
        //   Do not touch PcieAspm, it independently chokes GPU bandwidth, one of the few power items that really affect games
        //   Do not touch UsbSelSuspend, that one fixes the first jittery input after keyboard/mouse idle
        // Relaxed values borrow the Smart tier battery column directly, avoiding another set of magic numbers
        // NVMe latency tolerance is relaxed together with DiskIdle; AHCI LPM is not, its wake hits frames directly, same path as PcieAspm
        private static readonly Guid[] ArenaDcRelaxOnLaptop =
        {
            ProcThrottleMin, ProcThrottleMin1, CpMinCores, CpMinCores1,
            PerfDutyCycling, DiskIdle, WirelessPowerSave,
            NvmeLatTolPrimary, NvmeLatTolSecondary,
        };

        // Laptops on AC should not force zero core parking either
        //   Local bench: 12 logical cores, 3-thread steady-state load, two independent runs on the same machine
        //   Unparked minimum cores %: 100 50 20 5
        //   Run 1 actual frequency %: 153.8 157.8 164.0 157.8
        //   Run 2 actual frequency %: 153.8 162.1 156.3 163.6
        //   Both 100 runs landed exactly on 153.8; all six relaxed arms sit in 156.3~164.0, zero overlap
        //   So forcing no parking actually makes the working cores run slower, the package budget is spread across more active cores
        //   the exact opposite of what the Esports tier intends
        // Desktops relax by the same table; the mechanism is the same on power-limited small cases and air-cooled high-core-count CPUs
        //   Desktops have no constraint data, and the only measured set points the other way; without evidence do not write 100
        //   Relaxed writes use the Smart tier column; keeping parking on asymmetric-cache machines is a separate path
        // The power draw side is inconclusive, the two baselines drifted 6W on their own, noise exceeds effect, do not use it as evidence
        private static readonly Guid[] ArenaAcRelaxOnLaptop =
        {
            CpMinCores, CpMinCores1,
        };

        internal static bool ArenaAcRelaxed(Guid setting)
        {
            for (int i = 0; i < ArenaAcRelaxOnLaptop.Length; i++)
                if (setting == ArenaAcRelaxOnLaptop[i]) return true;
            return false;
        }

        // Handheld tier on AC relaxes by the battery column's criteria, borrowing the ArenaDcRelaxOnLaptop table
        //   Laptops on AC relax only core parking because that budget is still enough for CPU and dGPU to each take their share
        //   A handheld is a dozen-odd watts total, CPU and iGPU fight over the same budget; locking minimum performance state at 100 hands the budget to the CPU first
        //   Still only pure power-saving items are relaxed; EPP PerfBoostPol ProcThrottleMax keep their aggressive values, frames and input untouched
        private static uint ArenaAcFor(Knob k, uint ac, bool handheld, bool? autonomous)
        {
            ac = AutonomousArenaValue(k, ac, true, autonomous);
            return ResolveArenaAc(Native.HasSystemBattery(), handheld,
                ArenaAcRelaxed(k.Setting), ArenaDcRelaxed(k.Setting), ac, k.CalmAc);
        }

        // Pure decision: what the AC column finally writes
        //   Desktop and laptop on AC relax only core parking; handheld on AC relaxes pure power-saving items per the battery table
        internal static uint ResolveArenaAc(bool hasBattery, bool handheld,
            bool relaxedOnAc, bool relaxedOnDc, uint arenaAc, uint calmAc)
        {
            if (!hasBattery) return relaxedOnAc ? calmAc : arenaAc;
            if (handheld && relaxedOnDc) return calmAc;
            return relaxedOnAc ? calmAc : arenaAc;
        }

        internal static bool ArenaDcRelaxed(Guid setting)
        {
            for (int i = 0; i < ArenaDcRelaxOnLaptop.Length; i++)
                if (setting == ArenaDcRelaxOnLaptop[i]) return true;
            return false;
        }

        private static uint ArenaDcFor(Knob k, uint dc, bool? autonomous)
        {
            dc = AutonomousArenaValue(k, dc, false, autonomous);
            if (!Native.HasSystemBattery()) return dc;   // Desktops never use the battery column
            return ArenaDcRelaxed(k.Setting) ? k.CalmDc : dc;
        }

#if PAVISE_SELFTEST
        internal static uint AutonomousMinimumForTest(bool secondary, bool ac, bool autonomous)
        {
            Knob k = secondary ? HybridKnobs[0] : CoreKnobs[0];
            return AutonomousArenaValue(k, ac ? k.ArenaAc : k.ArenaDc, ac, autonomous);
        }
#endif

        // Clear once the 1 written before retirement, regardless of takeover state; on success set a flag so it never runs again
        //   No managed scheme, or the item missing from the scheme, both count as cleared; without write access keep the flag unset and retry next time
        internal static bool ClearLegacyIdleDisableOnce()
        {
            lock (lk)
            {
                if (!RestoreCpuIdle() || CpuIdleHasResidue) return false;
                return ClearLegacyIdleDisableOnceCore();
            }
        }

        private static bool ClearLegacyIdleDisableOnceCore()
        {
            if (Settings.Load(IdleDisableClearedKey, false)) return true;
            try
            {
                Guid g = ManagedPlanGuid();
                if (g == Guid.Empty || !SettingPresent(g, SubProcessor, IdleDisableSet))
                {
                    Settings.Save(IdleDisableClearedKey, true);
                    return true;
                }
                if (!WritePair(g, SubProcessor, IdleDisableSet, 0u, 0u)) return false;
                // When the managed scheme is the active one, it must be re-activated for the kernel to re-read this value
                //   If activation fails do not set the flag, clear again on next launch; once the flag is set there is no second chance
                Guid? cur = Current();
                if (cur.HasValue && cur.Value == g && !Set(g)) return false;
                Settings.Save(IdleDisableClearedKey, true);
                Logger.Log(Lang.T("log.powerplanschemes.47"));
                return true;
            }
            catch { return false; }
        }

        private static readonly Guid[] CalmArenaAcOnDesktop =
        {
            ProcThrottleMin, ProcThrottleMin1, CpMinCores, CpMinCores1,
            PerfEpp, PerfEpp1, PerfBoostPol, PerfIncPol, PerfIncTime,
            PerfIncThreshold, LatencyHintPerf, LatencyHintUnpark,
        };

        private static uint CalmAcFor(Knob k, uint ac, PowerPlanProfile profile)
        {
            if (Native.HasSystemBattery()) return ac;
            if (profile.PreserveCoreParking
                && (k.Setting == CpMinCores || k.Setting == CpMinCores1)) return ac;
            for (int i = 0; i < CalmArenaAcOnDesktop.Length; i++)
                if (k.Setting == CalmArenaAcOnDesktop[i]) return k.ArenaAc;
            return ac;
        }

        // Temporarily raise EPP during the match to yield shared power budget; touches only the managed scheme's AC value, always restored on exit
        //   EPP is the knob that really controls the power/responsiveness trade-off on HWP platforms
        //   ProcThrottleMin only decides whether it can go down; with the floor relaxed but EPP still 0 it never will
        //   On hybrid architectures the E-core PerfEpp1 must move together, otherwise only one efficiency class changes
        // The yield write comes from the sampling thread; restore may come from both the sampling thread and the match-exit path
        //   Stop does Join before checking EppYielded so normally they never collide, but a Join timeout will
        //   A collision means a duplicate write or reading a half-written snapshot; a lock is cheaper than reasoning about it
        private static readonly object eppLk = new object();
        private static bool eppYielded;
        private static bool eppApplied;
        private static Guid eppSavedScheme;
        private static uint eppSavedAc, eppSavedAc1;
        private static bool eppSaved, eppSaved1;

        internal static bool EppYielded { get { lock (eppLk) return eppYielded; } }

        internal static bool TryYieldEpp(uint epp)
        {
            lock (eppLk)
            {
                // Pending restore does not count as a successful yield, and its original value must not be
                // clobbered by a second attempt's snapshot
                if (eppYielded) return eppApplied;
                try
                {
                    Guid g = EppManagedScheme();
                    if (g == Guid.Empty) return false;
                    uint savedAc, savedAc1;
                    if (!EppReadAc(g, false, out savedAc)) return false;
                    // Some systems have no second efficiency class; settings whose original value cannot be read
                    // are never written
                    bool hasSecondary = EppReadAc(g, true, out savedAc1);
                    eppSavedScheme = g;
                    eppSavedAc = savedAc;
                    eppSavedAc1 = savedAc1;
                    eppSaved = true;
                    eppSaved1 = false;
                    eppApplied = false;
                    eppYielded = true;
                    bool ok = EppWriteVerified(g, false, epp);
                    if (ok && hasSecondary)
                    {
                        // Record what this write will touch before entering native code
                        // because it may fail halfway through changing the settings
                        eppSaved1 = true;
                        ok = EppWriteVerified(g, true, epp);
                    }
                    if (ok) ok = EppReapplyVerified(g);
                    if (!ok) { RestoreEpp(); return false; }
                    eppApplied = true;
                    return true;
                }
                catch
                {
                    if (eppYielded) RestoreEpp();
                    return false;
                }
            }
        }

        internal static bool RestoreEpp()
        {
            lock (eppLk)
            {
                if (!eppYielded) return true;
                eppApplied = false;
                // The managed scheme reference may have changed since the write
                // These original values belong only to the scheme captured at the time
                Guid g = eppSavedScheme;
                if (g == Guid.Empty || !eppSaved) return false;
                try
                {
                    bool ok = EppWriteVerified(g, false, eppSavedAc);
                    if (eppSaved1) ok = EppWriteVerified(g, true, eppSavedAc1) && ok;
                    if (!ok || !EppReapplyVerified(g)) return false;
                    ClearEppSnapshot();
                    return true;
                }
                catch { return false; }
            }
        }

        // Changing values on the active scheme needs another SetActive, otherwise the kernel will not re-read
        private static bool EppReapplyVerified(Guid scheme)
        {
            Guid? cur = EppCurrentScheme();
            if (!cur.HasValue || cur.Value == Guid.Empty) return false;
            if (cur.Value != scheme) return true;
            if (!EppSetActive(scheme)) return false;
            Guid? after = EppCurrentScheme();
            return after.HasValue && after.Value == scheme;
        }

        private static bool EppWriteVerified(Guid scheme, bool secondary, uint value)
        {
            uint actual;
            return EppWriteAc(scheme, secondary, value)
                && EppReadAc(scheme, secondary, out actual) && actual == value;
        }

        private static void ClearEppSnapshot()
        {
            eppYielded = eppApplied = eppSaved = eppSaved1 = false;
            eppSavedScheme = Guid.Empty;
            eppSavedAc = eppSavedAc1 = 0;
        }

        private static Guid EppManagedScheme()
        {
#if PAVISE_SELFTEST
            if (EppManagedSchemeForTest != null) return EppManagedSchemeForTest();
#endif
            return ManagedPlanGuid();
        }

        private static bool EppReadAc(Guid scheme, bool secondary, out uint value)
        {
#if PAVISE_SELFTEST
            if (EppReadAcForTest != null) return EppReadAcForTest(scheme, secondary, out value);
#endif
            return ReadAc(scheme, SubProcessor, secondary ? PerfEpp1 : PerfEpp, out value);
        }

        private static bool EppWriteAc(Guid scheme, bool secondary, uint value)
        {
#if PAVISE_SELFTEST
            if (EppWriteAcForTest != null) return EppWriteAcForTest(scheme, secondary, value);
#endif
            return WriteAc(scheme, SubProcessor, secondary ? PerfEpp1 : PerfEpp, value);
        }

        private static Guid? EppCurrentScheme()
        {
#if PAVISE_SELFTEST
            if (EppCurrentSchemeForTest != null) return EppCurrentSchemeForTest();
#endif
            return Current();
        }

        private static bool EppSetActive(Guid scheme)
        {
#if PAVISE_SELFTEST
            if (EppSetActiveForTest != null) return EppSetActiveForTest(scheme);
#endif
            return Set(scheme);
        }

#if PAVISE_SELFTEST
        internal delegate bool EppReadAcProbe(Guid scheme, bool secondary, out uint value);
        internal static Func<Guid> EppManagedSchemeForTest;
        internal static EppReadAcProbe EppReadAcForTest;
        internal static Func<Guid, bool, uint, bool> EppWriteAcForTest;
        internal static Func<Guid?> EppCurrentSchemeForTest;
        internal static Func<Guid, bool> EppSetActiveForTest;

        internal static void ResetEppForTest()
        {
            lock (eppLk)
            {
                ClearEppSnapshot();
                EppManagedSchemeForTest = null;
                EppReadAcForTest = null;
                EppWriteAcForTest = null;
                EppCurrentSchemeForTest = null;
                EppSetActiveForTest = null;
            }
        }
#endif

        internal static bool ReadAc(Guid scheme, Guid sub, Guid setting, out uint value)
        {
            Guid sb = sub, st = setting;
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref st, out value) == 0;
        }

        internal static bool ReadDc(Guid scheme, Guid sub, Guid setting, out uint value)
        {
            Guid sb = sub, st = setting;
            return PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sb, ref st, out value) == 0;
        }

        internal static bool WriteDc(Guid scheme, Guid sub, Guid setting, uint value)
        {
            Guid sb = sub, st = setting;
            return PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sb, ref st, value) == 0;
        }

        internal static bool WriteAc(Guid scheme, Guid sub, Guid setting, uint value)
        {
            Guid sb = sub, st = setting;
            return PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref st, value) == 0;
        }

        internal static bool ManagedPlanIsActive
        {
            get
            {
                try
                {
                    Guid g = ManagedPlanGuid();
                    if (g == Guid.Empty) return false;
                    Guid? cur = Current();
                    return cur.HasValue && cur.Value == g;
                }
                catch { return false; }
            }
        }

        private static bool SettingPresent(Guid scheme, Guid sub, Guid setting)
        {
            Guid sb = sub, set = setting;
            uint value;
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, out value) == 0;
        }

        private static uint Clamp(Guid sub, Guid setting, uint v)
        {
            Guid sb = sub, set = setting;
            uint lo, hi;
            if (PowerReadValueMin(IntPtr.Zero, ref sb, ref set, out lo) != 0) return v;
            sb = sub; set = setting;
            if (PowerReadValueMax(IntPtr.Zero, ref sb, ref set, out hi) != 0) return v;
            if (lo > hi) return v;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static bool WritePair(Guid scheme, Guid sub, Guid setting, uint ac, uint dc)
        {
            uint code;
            return WritePair(scheme, sub, setting, ac, dc, out code);
        }

        private static bool WritePair(Guid scheme, Guid sub, Guid setting, uint ac, uint dc, out uint code)
        {
            Guid sb = sub, set = setting;
            code = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, ac);
            if (code == ErrorInvalidParameter)
            {
                uint fixedAc = Clamp(sub, setting, ac);
                if (fixedAc != ac)
                {
                    sb = sub; set = setting;
                    code = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, fixedAc);
                }
            }
            if (code != 0) return false;

            sb = sub; set = setting;
            code = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, dc);
            if (code == ErrorInvalidParameter)
            {
                uint fixedDc = Clamp(sub, setting, dc);
                if (fixedDc != dc)
                {
                    sb = sub; set = setting;
                    code = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, fixedDc);
                }
            }
            return code == 0;
        }

        private const uint ErrorInvalidParameter = 87;

        // On uninstall remove schemes left by older versions by name; the RemoveManagedPlan in the receipt already deleted its own, this only settles old debts
        internal static bool IsManagedPlanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string prefix in new[] { "PG ", "由软件调度", "Scheduled by Pavise", "Aegis", "Pavise" })
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static int DeleteManagedPlansByName()
        {
            int deleted = 0;
            lock (lk)
            {
                Guid? cur = Current();
                foreach (Guid g in EnumerateSchemes())
                {
                    if (!IsManagedPlanName(ReadName(g))) continue;
                    if (cur.HasValue && cur.Value == g)
                    {
                        if (!SwitchAwayFrom(g)) continue;
                        cur = Current();
                    }
                    Guid t = g;
                    if (PowerDeleteScheme(IntPtr.Zero, ref t) == 0) deleted++;
                }
            }
            return deleted;
        }

        private static Guid? Current()
        {
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0 || p == IntPtr.Zero) return null;
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            catch { return null; }
            finally { LocalFree(p); }
        }

        private static bool Set(Guid g)
        {
            if (PowerSetActiveScheme(IntPtr.Zero, ref g) != 0) return false;
            Guid? actual = Current();
            return actual.HasValue && actual.Value == g;
        }

        private static bool SchemeUsable(Guid g)
        {
            uint size = 0;
            uint r = PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            return r == 0 || r == 234;
        }

        private static string ReadName(Guid g)
        {
            try
            {
                uint size = 0;
                uint probe = PowerReadFriendlyNameBuf(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size);
                if ((probe != 0 && probe != 234) || size == 0 || size > 4096) return "";
                byte[] buf = new byte[size];
                if (PowerReadFriendlyNameBuf(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return "";
                return Encoding.Unicode.GetString(buf).TrimEnd('\0');
            }
            catch { return ""; }
        }

        private static bool WriteName(Guid g, string title, string note)
        {
            try
            {
                byte[] nameBuf = Encoding.Unicode.GetBytes(title + "\0");
                Guid s = g;
                if (PowerWriteFriendlyName(IntPtr.Zero, ref s, IntPtr.Zero, IntPtr.Zero, nameBuf, (uint)nameBuf.Length) != 0)
                    return false;
                byte[] noteBuf = Encoding.Unicode.GetBytes(note + "\0");
                s = g;
                PowerWriteDescription(IntPtr.Zero, ref s, IntPtr.Zero, IntPtr.Zero, noteBuf, (uint)noteBuf.Length);
                return true;
            }
            catch { return false; }
        }

        private static List<Guid> EnumerateSchemes()
        {
            List<Guid> list;
            TryEnumerateSchemes(out list);
            return list;
        }

        // Callers may use absence as proof a scheme does not exist only after a normal end of enumeration
        private static bool TryEnumerateSchemes(out List<Guid> list)
        {
            list = new List<Guid>();
            try
            {
                for (uint i = 0; i < 128; i++)
                {
                    uint size = 16;
                    byte[] buf = new byte[16];
                    uint result;
#if PAVISE_SELFTEST
                    if (EnumerateSchemeForTest != null) result = EnumerateSchemeForTest(i, buf, ref size);
                    else
#endif
                        result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buf, ref size);
                    if (result == 259) return true; // ERROR_NO_MORE_ITEMS
                    if (result != 0 || size != 16) return false;
                    Guid scheme = new Guid(buf);
                    if (scheme == Guid.Empty || list.Contains(scheme)) return false;
                    list.Add(scheme);
                }
            }
            catch { }
            return false; // An exception or hitting the count cap is not a complete enumeration
        }

#if PAVISE_SELFTEST
        internal delegate uint SchemeEnumeration(uint index, byte[] buffer, ref uint size);
        internal static SchemeEnumeration EnumerateSchemeForTest;
#endif

        internal static void SyncDisplayFeel(Guid src, Guid dst)
        {
            Guid[] settings = { VideoBrightness, VideoDimBrightness, VideoAdaptive };
            foreach (Guid setting in settings)
            {
                Guid s = src, d = dst, sub = SubVideo, st = setting;
                uint value;
                if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref st, out value) == 0)
                    PowerWriteACValueIndex(IntPtr.Zero, ref d, ref sub, ref st, value);
                if (PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref st, out value) == 0)
                    PowerWriteDCValueIndex(IntPtr.Zero, ref d, ref sub, ref st, value);
            }
        }

        private static bool Duplicate(Guid src, out Guid created)
        {
            created = Guid.Empty;
            IntPtr dest = IntPtr.Zero;
            Guid s = src;
            uint r = PowerDuplicateScheme(IntPtr.Zero, ref s, ref dest);
            if (r != 0 || dest == IntPtr.Zero) return false;
            try { created = (Guid)Marshal.PtrToStructure(dest, typeof(Guid)); }
            catch { return false; }
            finally { LocalFree(dest); }
            return created != Guid.Empty;
        }

#if PAVISE_SELFTEST
        internal static Guid SelfTestCurrent()
        {
            Guid? cur = Current();
            return cur.HasValue ? cur.Value : Guid.Empty;
        }

        internal static bool SelfTestSetActive(Guid g) { return Set(g); }

        internal static bool SelfTestDuplicate(out Guid created) { return Duplicate(HighPerf, out created); }

        internal static bool SelfTestEppRange(Guid scheme, out uint lo, out uint hi)
        {
            Guid sb = SubProcessor, st = PerfEpp;
            lo = 0; hi = 0;
            return PowerReadValueMin(IntPtr.Zero, ref sb, ref st, out lo) == 0
                && PowerReadValueMax(IntPtr.Zero, ref sb, ref st, out hi) == 0;
        }

        internal static Guid SelfTestProcSub { get { return SubProcessor; } }
        internal static Guid SelfTestMinState { get { return ProcThrottleMin; } }
        internal static Guid SelfTestParkMin { get { return CpMinCores; } }
        internal static Guid SelfTestMaxState { get { return ProcThrottleMax; } }
        internal static Guid SelfTestBoostPol { get { return PerfBoostPol; } }
        internal static Guid SelfTestPcieAspm { get { return PcieAspm; } }
        internal static Guid SelfTestUsbSuspend { get { return UsbSelSuspend; } }
        internal static Guid SelfTestDutyCycling { get { return PerfDutyCycling; } }
        internal static Guid SelfTestWirelessSave { get { return WirelessPowerSave; } }
        internal static Guid SelfTestEppSetting { get { return PerfEpp; } }

        internal static bool SelfTestReadBrightnessAc(Guid scheme, out uint value)
        {
            Guid s = scheme, sub = SubVideo, st = VideoBrightness;
            return PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref st, out value) == 0;
        }

        internal static bool SelfTestWriteBrightnessAc(Guid scheme, uint value)
        {
            Guid s = scheme, sub = SubVideo, st = VideoBrightness;
            return PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref st, value) == 0;
        }

        internal static int SelfTestSchemeCount() { return EnumerateSchemes().Count; }

        internal static bool SelfTestDelete(Guid scheme)
        {
            Guid t = scheme;
            return PowerDeleteScheme(IntPtr.Zero, ref t) == 0;
        }

        internal static string SelfTestName(Guid scheme) { return ReadName(scheme); }

        internal static bool SelfTestWriteName(Guid scheme, string title) { return WriteName(scheme, title, "selftest"); }

        internal static bool SelfTestReadProcMinAc(Guid scheme, out uint value)
        {
            Guid s = scheme;
            Guid sub = new Guid("54533251-82be-4824-96c1-47b60b740d00");
            Guid st = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
            return PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref st, out value) == 0;
        }
#endif
    }
}
