using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        // All laboratory processor mutations share the normal plan owner's lock.
        internal static object LaboratoryGate { get { return lk; } }
#if PAVISE_SELFTEST
        internal static Func<bool> LaboratoryRestoreForTest;
#endif
        private static bool RestoreLaboratoryBeforeCoreChange()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            // 隔离测试未注入实验室后端时，跳过原生电源访问。
#if PAVISE_SELFTEST
            if (LaboratoryRestoreForTest != null) return LaboratoryRestoreForTest();
#endif
            return true;
#else
            return LaboratoryPower.Restore();
#endif
        }
        private static bool LaboratoryHasResidue
        {
            get
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return false;
#else
                return LaboratoryPower.HasResidue;
#endif
            }
        }

        internal sealed class NativeLaboratoryPower : ILaboratoryPowerPlatform, ILaboratoryCurrentPowerPlatform
        {
            private CpuSaturation cpu = new CpuSaturation();
            private GpuEvidence.GpuEngineSampler gpu;
            private EnergyMeter.Sample previousEnergy;
            private int samplePid;
            private int drySamples;
            public long Now { get { return checked((long)Native.GetTickCount64() * TimeSpan.TicksPerMillisecond); } }
            public bool OnAc { get { return Native.ConfirmedAcPower(); } }
            public bool HasBattery { get { return LaboratoryEppEligibility.VerifiedLaptop && Native.HasSystemBattery(); } }
            public Guid ActivePlan() { return Current() ?? Guid.Empty; }
            public string DeviceIdentity
            {
                get
                {
                    // Standard Windows plan GUIDs repeat on other PCs. Never use the short display signature.
                    try
                    {
                        using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        using (RegistryKey key = machine.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography"))
                        {
                            Guid value;
                            return key != null && Guid.TryParse(key.GetValue("MachineGuid", null) as string, out value)
                                && value != Guid.Empty ? value.ToString("N") : null;
                        }
                    }
                    catch { return null; }
                }
            }
            public int PlanStatus(Guid scheme)
            {
                List<Guid> schemes;
                if (!TryEnumerateSchemes(out schemes)) return -1;
                return schemes.Contains(scheme) ? 1 : 0;
            }
            public Guid ActiveManagedPlan()
            {
                ManagedPolicyRecord record;
                if (!AutomaticSelection || !active || !ReadManagedPolicy(out record) || record == null
                    || Current() != record.Plan || !ManagedPlans.IsOwned(record.Plan)) return Guid.Empty;
                return record.Plan;
            }
            public bool EppSupported(Guid scheme)
            {
                bool? ac, dc; ReadAutonomousScaling(scheme, out ac, out dc);
                uint? epp = Read(scheme, LaboratoryPower.Epp);
                return ac == true && epp.HasValue && epp.Value <= 100;
            }
            public int OwnerStatus(Guid scheme)
            {
                int status = PlanStatus(scheme);
                if (status != 1) return status;
                return ManagedPlans.IsOwned(scheme) ? 1 : -1;
            }
            public uint? Read(Guid scheme, Guid setting)
            { uint value; return ReadAc(scheme, SubProcessor, setting, out value) ? (uint?)value : null; }
            public bool Write(Guid scheme, Guid setting, uint value)
            { return WriteAc(scheme, SubProcessor, setting, value); }
            public bool Flush(Guid scheme)
            {
                Guid? current = Current();
                if (!current.HasValue) return false;
                if (current.Value != scheme) return true;
                // Never intentionally switch back to a scheme the user has already left.
                Guid? confirmed = Current();
                return confirmed.HasValue && (confirmed.Value != scheme || Set(scheme) && Current() == scheme);
            }
            public bool Load(out string receipt) { return Settings.TryLoadStr(LaboratoryPower.ReceiptKey, out receipt); }
            public bool Save(string receipt) { return Settings.SaveStrDurable(LaboratoryPower.ReceiptKey, receipt); }
            public LaboratoryPowerSample Sample(int rendererPid, long creation)
            {
                var result = new LaboratoryPowerSample();
                IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, rendererPid);
                if (process == IntPtr.Zero) return result;
                try
                {
                    long actual, ticks; ulong io;
                    result.IdentityValid = Native.QueryProcessSample(process, out actual, out ticks, out io) && actual == creation;
                }
                finally { Native.CloseHandle(process); }
                if (!result.IdentityValid) return result;
                if (gpu == null || samplePid != rendererPid)
                {
                    ResetSampling(); samplePid = rendererPid;
                    gpu = new GpuEvidence.GpuEngineSampler(); gpu.Open(rendererPid);
                }
                RenderAdapter adapter = gpu.Resolve(rendererPid, true);
                if (adapter == null)
                {
                    // Wildcard tables may miss a renderer's late-created device. Reopen sparingly.
                    if (++drySamples >= 6) { drySamples = 0; gpu.Open(rendererPid); }
                }
                else drySamples = 0;
                if (adapter != null)
                {
                    result.AdapterKnown = true; result.Ambiguous = adapter.Ambiguous;
                    result.AdapterHigh = adapter.LuidHigh; result.AdapterLow = adapter.LuidLow; result.AdapterPhysical = adapter.PhysIndex;
                    result.GpuPercent = Math.Min(100, adapter.Util);
                }
                result.CpuPercent = cpu.Sample() * 100;
                EnergyMeter.Sample energy = EnergyMeter.Take();
                double watts = EnergyMeter.Watts(previousEnergy, energy, EnergyRail.Package);
                if (energy != null) previousEnergy = energy;
                if (watts >= 0) result.CpuWatts = watts;
                return result;
            }
            public void ResetSampling()
            {
                if (gpu != null) gpu.Dispose(); gpu = null;
                previousEnergy = null; samplePid = drySamples = 0; cpu.Reset();
            }
        }
    }
}
