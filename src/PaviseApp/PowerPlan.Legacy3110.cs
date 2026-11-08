using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
    internal interface IManagedPowerLegacy3110Hardware
    {
        PowerPlanProfile Profile { get; }
        bool Hybrid { get; }
        bool HasBattery { get; }
        void ReadAutonomousScaling(Guid scheme, out bool? ac, out bool? dc);
        uint Clamp(Guid sub, Guid setting, uint value);
    }

    internal interface IManagedPowerLegacy3110WriteEvidence
    {
        uint WriteLegacy3110(Guid scheme, Guid sub, Guid setting, bool ac, uint value);
    }

    internal static partial class PowerPlan
    {
        // Legacy parameter tables; creation, failure budgets and restore ownership use the current controllers.
		private struct Knob
		{
			public readonly Guid Sub;

			public readonly Guid Setting;

			public readonly uint ArenaAc;

			public readonly uint ArenaDc;

			public readonly uint CalmAc;

			public readonly uint CalmDc;

			private readonly string labelKey;

			public string Label
			{
				get
				{
					return Lang.T(labelKey);
				}
			}

			public Knob(Guid sub, Guid setting, uint arenaAc, uint arenaDc, uint calmAc, uint calmDc, string label)
			{
				Sub = sub;
				Setting = setting;
				ArenaAc = arenaAc;
				ArenaDc = arenaDc;
				CalmAc = calmAc;
				CalmDc = calmDc;
				labelKey = label;
			}
		}
        // Initialize after the enclosing partial type's GUIDs, independent of file order.
        private static class Legacy3110Tables
        {
            static Legacy3110Tables() { }
		internal static readonly Knob[] CoreKnobs = new Knob[11]
		{
			new Knob(SubProcessor, ProcThrottleMin, 100u, 100u, 20u, 10u, "t.powerplanschemes.1"),
			new Knob(SubProcessor, ProcThrottleMax, 100u, 100u, 100u, 100u, "t.powerplanschemes.2"),
			new Knob(SubProcessor, CpMinCores, 100u, 100u, 50u, 20u, "t.powerplanschemes.3"),
			new Knob(SubProcessor, CpMaxCores, 100u, 100u, 100u, 100u, "t.powerplanschemes.4"),
			new Knob(SubProcessor, PerfBoostMode, 2u, 2u, 2u, 3u, "t.powerplanschemes.5"),
			new Knob(SubProcessor, Throttling, 0u, 0u, 2u, 2u, "t.powerplanschemes.6"),
			new Knob(SubProcessor, SysCoolPol, 1u, 1u, 1u, 1u, "t.powerplanschemes.7"),
			new Knob(SubPcie, PcieAspm, 0u, 0u, 1u, 2u, "t.powerplanschemes.8"),
			new Knob(SubUsb, UsbSelSuspend, 0u, 0u, 0u, 0u, "t.powerplanschemes.9"),
			new Knob(SubDisk, DiskIdle, 0u, 0u, 1200u, 600u, "t.powerplanschemes.10"),
			new Knob(SubNone, Personality, 1u, 1u, 1u, 1u, "t.powerplanschemes.11")
		};

		internal static readonly Knob[] OptionalKnobs = new Knob[19]
		{
			new Knob(SubProcessor, PerfEpp, 0u, 0u, 32u, 70u, "t.powerplanschemes.12"),
			new Knob(SubProcessor, PerfBoostPol, 100u, 100u, 60u, 40u, "t.powerplanschemes.13"),
			new Knob(SubProcessor, PerfIncPol, 2u, 2u, 1u, 1u, "t.powerplanschemes.14"),
			new Knob(SubProcessor, PerfDecPol, 1u, 1u, 2u, 2u, "t.powerplanschemes.15"),
			new Knob(SubProcessor, PerfIncTime, 1u, 1u, 3u, 3u, "t.powerplanschemes.16"),
			new Knob(SubProcessor, PerfDecTime, 10u, 10u, 5u, 5u, "t.powerplanschemes.17"),
			new Knob(SubProcessor, PerfIncThreshold, 10u, 10u, 30u, 40u, "t.powerplanschemes.18"),
			new Knob(SubProcessor, PerfDecThreshold, 8u, 8u, 20u, 30u, "t.powerplanschemes.19"),
			new Knob(SubProcessor, LatencyHintPerf, 100u, 100u, 75u, 50u, "t.powerplanschemes.20"),
			new Knob(SubProcessor, LatencyHintUnpark, 100u, 100u, 50u, 50u, "t.powerplanschemes.21"),
			new Knob(SubProcessor, PerfDutyCycling, 0u, 0u, 0u, 1u, "t.powerplanschemes.22"),
			new Knob(SubProcessor, ProcFreqMax, 0u, 0u, 0u, 0u, "t.powerplanschemes.23"),
			new Knob(SubWireless, WirelessPowerSave, 0u, 0u, 1u, 2u, "t.powerplanschemes.24"),
			new Knob(SubDisk, NvmeLatTolPrimary, 0u, 0u, 15u, 50u, "t.powerplanschemes.35"),
			new Knob(SubDisk, NvmeLatTolSecondary, 0u, 0u, 100u, 100u, "t.powerplanschemes.36"),
			new Knob(SubDisk, AhciLpm, 0u, 0u, 0u, 1u, "t.powerplanschemes.37"),
			new Knob(SubUsb, Usb3Lpm, 0u, 0u, 2u, 3u, "t.powerplanschemes.43"),
			new Knob(SubIntelGfx, IntelGfxPlan, 1u, 1u, 1u, 1u, "t.powerplanschemes.44"),
			new Knob(SubSwitchGfx, SwitchGfxPolicy, 3u, 3u, 1u, 1u, "t.powerplanschemes.54")
		};

		internal static readonly Knob[] HybridKnobs = new Knob[16]
		{
			new Knob(SubProcessor, ProcThrottleMin1, 100u, 100u, 20u, 10u, "t.powerplanschemes.25"),
			new Knob(SubProcessor, ProcThrottleMax1, 100u, 100u, 100u, 100u, "t.powerplanschemes.26"),
			new Knob(SubProcessor, CpMinCores1, 100u, 100u, 50u, 20u, "t.powerplanschemes.27"),
			new Knob(SubProcessor, CpMaxCores1, 100u, 100u, 100u, 100u, "t.powerplanschemes.28"),
			new Knob(SubProcessor, PerfEpp1, 0u, 0u, 32u, 70u, "t.powerplanschemes.29"),
			new Knob(SubProcessor, SchedPolicy, 2u, 2u, 5u, 5u, "t.powerplanschemes.30"),
			new Knob(SubProcessor, ShortSchedPolicy, 2u, 2u, 5u, 5u, "t.powerplanschemes.31"),
			new Knob(SubProcessor, PerfBoostPol1, 100u, 100u, 60u, 40u, "t.powerplanschemes.45"),
			new Knob(SubProcessor, PerfIncPol1, 2u, 2u, 1u, 1u, "t.powerplanschemes.46"),
			new Knob(SubProcessor, PerfDecPol1, 1u, 1u, 2u, 2u, "t.powerplanschemes.47"),
			new Knob(SubProcessor, PerfIncTime1, 1u, 1u, 3u, 3u, "t.powerplanschemes.48"),
			new Knob(SubProcessor, PerfDecTime1, 10u, 10u, 5u, 5u, "t.powerplanschemes.49"),
			new Knob(SubProcessor, PerfIncThreshold1, 10u, 10u, 30u, 40u, "t.powerplanschemes.50"),
			new Knob(SubProcessor, PerfDecThreshold1, 8u, 8u, 20u, 30u, "t.powerplanschemes.51"),
			new Knob(SubProcessor, LatencyHintPerf1, 100u, 100u, 75u, 50u, "t.powerplanschemes.52"),
			new Knob(SubProcessor, LatencyHintUnpark1, 100u, 100u, 50u, 50u, "t.powerplanschemes.53")
		};
		internal static readonly Guid[] ArenaDcRelaxOnLaptop = new Guid[9] { ProcThrottleMin, ProcThrottleMin1, CpMinCores, CpMinCores1, PerfDutyCycling, DiskIdle, WirelessPowerSave, NvmeLatTolPrimary, NvmeLatTolSecondary };

		internal static readonly Guid[] ArenaAcRelaxOnLaptop = new Guid[2] { CpMinCores, CpMinCores1 };
		internal static readonly Guid[] CalmArenaAcOnDesktop =
        {
            ProcThrottleMin, ProcThrottleMin1, CpMinCores, CpMinCores1,
            PerfEpp, PerfEpp1, PerfBoostPol, PerfIncPol, PerfIncTime,
            PerfIncThreshold, LatencyHintPerf, LatencyHintUnpark,
        };

        }

        private static PowerPlanProfile legacy3110Profile;

        private sealed class Legacy3110Context
        {
            internal IManagedPowerPlans Plans;
            internal IManagedPowerLegacy3110Hardware Hardware;
            internal PowerPlanProfile Profile;
            internal bool Hybrid, HasBattery;
            internal bool? AutonomousAc, AutonomousDc;
        }

        private static PowerPlanProfile CurrentLegacy3110Profile()
        {
            if (legacy3110Profile != null) return legacy3110Profile;
            bool amd = false;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"))
                    if (key != null)
                        amd = ((key.GetValue("ProcessorNameString") as string) ?? "").IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }
            return legacy3110Profile = PowerPlanProfile.Resolve(amd, CpuTopology.Hybrid, CpuTopology.AsymCache, CpuTopology.PartitionTag);
        }

        private static Legacy3110Context Legacy3110ContextFor(Guid scheme, bool readAutonomous)
        {
            IManagedPowerPlans plans = ManagedPlans;
            var hardware = plans as IManagedPowerLegacy3110Hardware;
            var context = new Legacy3110Context
            {
                Plans = plans, Hardware = hardware,
                Profile = hardware == null ? CurrentLegacy3110Profile() : hardware.Profile,
                Hybrid = hardware == null ? CpuTopology.Hybrid : hardware.Hybrid,
                HasBattery = hardware == null ? Native.HasSystemBattery() : hardware.HasBattery
            };
            if (readAutonomous)
            {
                if (hardware != null) hardware.ReadAutonomousScaling(scheme, out context.AutonomousAc, out context.AutonomousDc);
                else
                {
                    ProcessorPowerPlatform.Interface platform = ProcessorPowerPlatform.Current;
                    context.AutonomousAc = ProcessorPowerPlatform.AutonomousMinimum(platform, plans.Read(scheme, SubProcessor, PerfAutonomous, true));
                    context.AutonomousDc = ProcessorPowerPlatform.AutonomousMinimum(platform, plans.Read(scheme, SubProcessor, PerfAutonomous, false));
                }
            }
            return context;
        }

        internal static string Legacy3110Fingerprint(bool aggressive)
        {
            Legacy3110Context context = Legacy3110ContextFor(Guid.Empty, false);
            PowerPlanProfile profile = context.Profile;
            // Localized profile labels and observation timestamps are not policy identity.
            return "legacy3110:1:" + (aggressive ? "arena" : "calm")
                + ":battery=" + (context.HasBattery ? "1" : "0")
                + ":hybrid=" + (context.Hybrid ? "1" : "0")
                + ":hetero=" + (profile.WriteHetero ? "1" : "0")
                + ":sched=" + profile.HeteroSched.ToString(CultureInfo.InvariantCulture)
                + ":parking=" + (profile.PreserveCoreParking ? "1" : "0");
        }

        private static ManagedPowerReadState ReadLegacy3110Value(IManagedPowerPlans plans,
            Guid scheme, Guid sub, Guid setting, bool ac, out uint value, bool recordFailure = false)
        {
            var evidence = plans as IManagedPowerReadEvidence;
            if (evidence != null)
            {
                ManagedPowerReadEvidence read = evidence.ReadEvidence(scheme, sub, setting, ac);
                value = read.Value;
                if (recordFailure && read.State == ManagedPowerReadState.Unavailable)
                    PresetPreparationFailed("legacy3110 setting read failed " + setting.ToString("D"), read.NativeCode, read.ResolutionCode);
                return read.State;
            }
            uint? actual = plans.Read(scheme, sub, setting, ac);
            value = actual.GetValueOrDefault();
            return actual.HasValue ? ManagedPowerReadState.Value : ManagedPowerReadState.Unavailable;
        }

        private static bool IsProcessorMinimum(Guid setting)
        { return setting == ProcThrottleMin || setting == ProcThrottleMin1; }

        private static bool WriteLegacy3110Verified(IManagedPowerPlans plans, Guid scheme,
            Guid sub, Guid setting, bool ac, uint expected)
        {
            Guid? current = plans.Current();
            if (!current.HasValue || current.Value == scheme || !plans.IsOwned(scheme)) return false;
            uint value;
            if (ReadLegacy3110Value(plans, scheme, sub, setting, ac, out value, true) != ManagedPowerReadState.Value) return false;
            if (value == expected) return true;
            var writer = plans as IManagedPowerLegacy3110WriteEvidence;
            if (writer != null)
            {
                uint code = writer.WriteLegacy3110(scheme, sub, setting, ac, expected);
                if (code != 0) return PresetPreparationFailed("legacy3110 setting write failed " + setting.ToString("D"), code);
            }
            else if (!plans.Write(scheme, sub, setting, ac, expected)) return false;
            return ReadLegacy3110Value(plans, scheme, sub, setting, ac, out value, true) == ManagedPowerReadState.Value
                && value == expected;
        }

        internal static bool ArenaAcRelaxed(Guid setting)
        { return Array.IndexOf(Legacy3110Tables.ArenaAcRelaxOnLaptop, setting) >= 0; }

        internal static bool ArenaDcRelaxed(Guid setting)
        { return Array.IndexOf(Legacy3110Tables.ArenaDcRelaxOnLaptop, setting) >= 0; }

        internal static uint ResolveArenaAc(bool hasBattery, bool handheld, bool relaxedOnAc,
            bool relaxedOnDc, uint arenaAc, uint calmAc)
        {
            if (!hasBattery) return relaxedOnAc ? calmAc : arenaAc;
            if (handheld && relaxedOnDc) return calmAc;
            return relaxedOnAc ? calmAc : arenaAc;
        }

        private static bool ResolveLegacy3110Knob(Guid scheme, Knob knob, bool aggressive,
            Legacy3110Context context, out uint ac, out uint dc)
        {
            // Only primary parking uses UseArenaCoreParking; secondary parking follows the mode.
            bool parking = knob.Setting == CpMinCores || knob.Setting == CpMaxCores;
            bool arena = parking ? context.Profile.UseArenaCoreParking(aggressive) : aggressive;
            ac = arena ? knob.ArenaAc : knob.CalmAc;
            dc = arena ? knob.ArenaDc : knob.CalmDc;
            if (arena)
            {
                if (IsProcessorMinimum(knob.Setting))
                {
                    if (context.AutonomousAc == true) ac = knob.CalmAc;
                    if (context.AutonomousDc == true) dc = knob.CalmDc;
                }
                ac = ResolveArenaAc(context.HasBattery, false, ArenaAcRelaxed(knob.Setting),
                    ArenaDcRelaxed(knob.Setting), ac, knob.CalmAc);
                if (context.HasBattery && ArenaDcRelaxed(knob.Setting)) dc = knob.CalmDc;
            }
            else if (!context.HasBattery
                && !(context.Profile.PreserveCoreParking && (knob.Setting == CpMinCores || knob.Setting == CpMinCores1))
                && Array.IndexOf(Legacy3110Tables.CalmArenaAcOnDesktop, knob.Setting) >= 0)
                ac = knob.ArenaAc;

            if (arena && IsProcessorMinimum(knob.Setting)
                && !ProcessorPowerPlatform.TryResolveMinimumIndices(context.AutonomousAc, context.AutonomousDc, ac, dc,
                    delegate(bool onAc) { return context.Plans.Read(scheme, knob.Sub, knob.Setting, onAc); }, out ac, out dc))
                return false;
            ac = context.Hardware == null ? Clamp(knob.Sub, knob.Setting, ac) : context.Hardware.Clamp(knob.Sub, knob.Setting, ac);
            dc = context.Hardware == null ? Clamp(knob.Sub, knob.Setting, dc) : context.Hardware.Clamp(knob.Sub, knob.Setting, dc);
            return true;
        }

        private static IEnumerable<Knob> Legacy3110Knobs(Legacy3110Context context)
        {
            foreach (Knob knob in Legacy3110Tables.CoreKnobs) yield return knob;
            // Handheld bypass leaves the legacy Intel-GPU override inactive.
            foreach (Knob knob in Legacy3110Tables.OptionalKnobs) yield return knob;
            if (context.Hybrid && context.Profile.WriteHetero)
                foreach (Knob knob in Legacy3110Tables.HybridKnobs)
                    yield return knob.Setting == SchedPolicy || knob.Setting == ShortSchedPolicy
                        ? new Knob(knob.Sub, knob.Setting, context.Profile.HeteroSched, context.Profile.HeteroSched,
                            knob.CalmAc, knob.CalmDc, knob.Label) : knob;
        }

        private static bool ApplyOrVerifyLegacy3110(Guid scheme, bool aggressive, bool write,
            HashSet<Guid> laboratory)
        {
            Legacy3110Context context = Legacy3110ContextFor(scheme, true);
            IManagedPowerPlans plans = context.Plans;
            if (scheme == Guid.Empty || context.Profile == null || !plans.Exists(scheme) || !plans.IsOwned(scheme))
                return write ? false : NoteIntactMismatch("legacy-plan-missing-or-not-owned");
            if (write)
            {
                Guid? current = plans.Current();
                if (!current.HasValue || current.Value == scheme) return false;
            }
            int applied = 0, skipped = 0;
            foreach (Knob knob in Legacy3110Knobs(context))
            {
                if (laboratory != null && knob.Sub == SubProcessor && laboratory.Contains(knob.Setting)) continue;
                if (aggressive && IsProcessorMinimum(knob.Setting)
                    && !context.AutonomousAc.HasValue && !context.AutonomousDc.HasValue) { skipped++; continue; }
                uint actualAc, actualDc;
                ManagedPowerReadState acState = ReadLegacy3110Value(plans, scheme, knob.Sub, knob.Setting, true, out actualAc, write);
                ManagedPowerReadState dcState = ReadLegacy3110Value(plans, scheme, knob.Sub, knob.Setting, false, out actualDc, write);
                if (acState == ManagedPowerReadState.ConfirmedUnregistered && dcState == ManagedPowerReadState.ConfirmedUnregistered)
                { skipped++; continue; }
                if (acState != ManagedPowerReadState.Value || dcState != ManagedPowerReadState.Value)
                    return write ? PresetPreparationFailed("legacy3110 setting read failed " + knob.Setting.ToString("D"))
                        : NoteIntactMismatch(Legacy3110KnobName(knob) + " read ac=" + acState + " dc=" + dcState);
                uint ac, dc;
                if (!ResolveLegacy3110Knob(scheme, knob, aggressive, context, out ac, out dc))
                    return write ? PresetPreparationFailed("legacy3110 setting resolution failed " + knob.Setting.ToString("D"))
                        : NoteIntactMismatch(Legacy3110KnobName(knob) + " resolution failed");
                if (!write)
                {
                    if (actualAc != ac || actualDc != dc)
                        return NoteIntactMismatch(Legacy3110KnobName(knob) + " expected ac/dc=" + ac + "/" + dc
                            + " actual ac/dc=" + actualAc + "/" + actualDc);
                }
                else
                {
                    bool acOk = WriteLegacy3110Verified(plans, scheme, knob.Sub, knob.Setting, true, ac);
                    bool dcOk = WriteLegacy3110Verified(plans, scheme, knob.Sub, knob.Setting, false, dc);
                    if (!acOk || !dcOk) return PresetPreparationFailed("legacy3110 setting write/readback failed " + knob.Setting.ToString("D"));
                    applied++;
                }
            }
            if (write) Logger.Info("POWER source=" + Legacy3110Source + " mode=" + (aggressive ? "arena" : "calm")
                + " profile=" + context.Profile.Tag + " settings=" + applied + " skipped=" + skipped);
            return true;
        }

        private static string Legacy3110KnobName(Knob knob)
        {
            string label;
            try { label = knob.Label; } catch { label = ""; }
            return "setting=" + (string.IsNullOrEmpty(label) ? "" : label + " ") + knob.Setting.ToString("D");
        }

        internal static bool TuneLegacy3110Target(Guid scheme, bool aggressive)
        {
            try { return ApplyOrVerifyLegacy3110(scheme, aggressive, true, null); }
            catch (Exception error) { return PresetPreparationFailed("legacy3110 " + error.GetType().Name); }
        }

        internal static bool Legacy3110ManagedIntact(Guid scheme, bool aggressive)
        {
            try
            {
                HashSet<Guid> laboratory = LaboratoryPower.OwnedSettings(scheme);
                if (laboratory == null) return NoteIntactMismatch("laboratory-ownership-unreadable");
                return ApplyOrVerifyLegacy3110(scheme, aggressive, false, laboratory);
            }
            catch (Exception error) { return NoteIntactMismatch("legacy-verify " + error.GetType().Name); }
        }

        internal static bool SyncLegacy3110DisplayFeel(Guid source, Guid destination)
        {
            try
            {
                IManagedPowerPlans plans = ManagedPlans;
                Guid? current = plans.Current();
                if (source == Guid.Empty || source == destination || !plans.Exists(source) || !plans.Exists(destination)
                    || !plans.IsOwned(destination) || !current.HasValue || current.Value == destination) return false;
                foreach (Guid setting in new[] { VideoBrightness, VideoDimBrightness, VideoAdaptive })
                    foreach (bool ac in new[] { true, false })
                    {
                        uint value;
                        ManagedPowerReadState state = ReadLegacy3110Value(plans, source, SubVideo, setting, ac, out value, true);
                        if (state == ManagedPowerReadState.ConfirmedUnregistered) continue;
                        if (state != ManagedPowerReadState.Value
                            || !WriteLegacy3110Verified(plans, destination, SubVideo, setting, ac, value))
                            return PresetPreparationFailed("legacy3110 display preference read/write failed " + setting.ToString("D"));
                    }
                return true;
            }
            catch (Exception error) { return PresetPreparationFailed("legacy3110 display " + error.GetType().Name); }
        }
    }
}
