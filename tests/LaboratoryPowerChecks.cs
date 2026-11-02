#if PAVISE_LAB_POWER_CHECKS
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class LaboratoryPowerChecks
    {
        private static int checks;
        private static void Check(bool condition, string name)
        { checks++; if (!condition) throw new Exception(name); }
        private sealed class MemoryPlatform : ILaboratoryPowerPlatform
        {
            internal readonly Guid Plan = new Guid("10000000-0000-0000-0000-000000000001");
            internal Dictionary<Guid, uint> Values = new Dictionary<Guid, uint>();
            internal string Receipt = "";
            internal bool Ac = true, Battery = true, Managed = true, Supported = true, SaveOk = true, FlushOk = true;
            internal Guid FailWrite;
            internal int Writes, Samples, Owner = 1;
            internal long Clock = TimeSpan.TicksPerSecond;
            internal double Watts = 50, Gpu = 95, Cpu = 30;
            internal bool Identity = true, Missing;
            internal uint Adapter = 1;
            internal List<string> Events = new List<string>();
            internal List<string> Changes = new List<string>();
            internal MemoryPlatform()
            {
                Values[LaboratoryPower.Promote] = 60; Values[LaboratoryPower.Scaling] = 0;
                Values[LaboratoryPower.Disable] = 0; Values[LaboratoryPower.Epp] = 0; Values[LaboratoryPower.Epp1] = 0;
            }
            public long Now { get { return Clock; } }
            public bool OnAc { get { return Ac; } }
            public bool HasBattery { get { return Battery; } }
            public Guid ActiveManagedPlan() { return Managed ? Plan : Guid.Empty; }
            public bool EppSupported(Guid value) { return Supported; }
            public int OwnerStatus(Guid value) { return Owner; }
            public uint? Read(Guid value, Guid setting) { uint found; return Values.TryGetValue(setting, out found) ? (uint?)found : null; }
            public bool Write(Guid value, Guid setting, uint index)
            {
                Events.Add("write"); Writes++;
                Changes.Add(setting.ToString("N") + ":" + index);
                if (FailWrite == setting) return false;
                Values[setting] = index; return true;
            }
            public bool Flush(Guid value) { Events.Add("flush"); return FlushOk; }
            public bool Load(out string receipt) { receipt = Receipt; return true; }
            public bool Save(string receipt)
            { Events.Add("save"); if (!SaveOk) return false; Receipt = receipt; return true; }
            public LaboratoryPowerSample Sample(int pid, long creation)
            {
                Samples++;
                return new LaboratoryPowerSample { IdentityValid = Identity, AdapterKnown = !Missing,
                    AdapterLow = Adapter, GpuPercent = Gpu, CpuPercent = Cpu, CpuWatts = Watts };
            }
            public void ResetSampling() { }
        }
        private static LaboratoryPowerController New(MemoryPlatform p)
        { return new LaboratoryPowerController(p, new object()); }
        private static void Tick(LaboratoryPowerController c, MemoryPlatform p, int n, string idle = "default")
        { for (int i = 0; i < n; i++) { p.Clock += TimeSpan.TicksPerSecond * 5; c.Update(true, idle, 42, 99); } }
        private static void Engage(LaboratoryPowerController c, MemoryPlatform p, string idle = "default")
        { c.Update(true, idle, 42, 99); Tick(c, p, 4, idle); }
        private static void IdleStrategies()
        {
            var p = new MemoryPlatform(); var c = New(p);
            c.Update(false, "threshold", 0, 0);
            Check(p.Values[LaboratoryPower.Promote] == 100, "threshold promotes at 100");
            Check(p.Values[LaboratoryPower.Disable] == 0, "threshold does not disable idle");
            Check(p.Events.IndexOf("save") < p.Events.IndexOf("write"), "durable snapshot precedes first mutation");
            int writes = p.Writes; c.Update(false, "threshold", 0, 0);
            Check(p.Writes == writes, "steady-state update performs no writes");
            c.Update(false, "disabled", 0, 0);
            Check(p.Values[LaboratoryPower.Promote] == 60 && p.Values[LaboratoryPower.Disable] == 1,
                "switching restores threshold before disabling idle");
            c.Update(false, "default", 0, 0);
            Check(p.Values[LaboratoryPower.Disable] == 0 && p.Receipt == "", "default restores exact originals");
            p.Values[LaboratoryPower.Disable] = 1;
            c.Update(false, "threshold", 0, 0); c.Restore();
            Check(p.Values[LaboratoryPower.Disable] == 1, "inherited idle-disable original is restored");
        }
        private static void SafetyAndRecovery()
        {
            var p = new MemoryPlatform(); var c = New(p); p.SaveOk = false;
            c.Update(false, "disabled", 0, 0);
            Check(p.Writes == 0, "failed persistence prevents mutation");
            p = new MemoryPlatform(); c = New(p); c.Update(false, "threshold", 0, 0);
            p.Values[LaboratoryPower.Promote] = 73;
            Check(c.Restore() && p.Values[LaboratoryPower.Promote] == 73, "external edits win on restore");
            p = new MemoryPlatform(); c = New(p); c.Update(false, "disabled", 0, 0);
            p.FailWrite = LaboratoryPower.Disable;
            Check(!c.Restore() && p.Receipt.Length > 0, "failed restore retains durable debt");
            p.FailWrite = Guid.Empty;
            Check(New(p).Restore() && p.Values[LaboratoryPower.Disable] == 0 && p.Receipt == "", "new controller recovers crash receipt");
            p = new MemoryPlatform(); c = New(p); c.Update(false, "disabled", 0, 0); p.FlushOk = false;
            Check(!c.Restore() && p.Receipt.Length > 0, "failed activation keeps debt even after readback restored");
            p.FlushOk = true;
            Check(c.Restore() && p.Receipt == "", "activation debt can be retried");
            p = new MemoryPlatform(); c = New(p); c.Update(false, "disabled", 0, 0); p.Owner = -1;
            int writes = p.Writes;
            Check(!c.Restore() && p.Writes == writes && p.Receipt.Length > 0, "foreign or unknown plan is never edited");
            p.Owner = 0;
            Check(c.Restore() && p.Receipt == "", "proven deleted owner clears obsolete receipt");
            p = new MemoryPlatform(); c = New(p); p.Ac = false;
            c.Update(true, "disabled", 42, 99);
            Check(p.Writes == 0 && p.Samples == 0, "battery power does not write or sample");
            p.Ac = true; p.Managed = false; c.Update(true, "disabled", 42, 99);
            Check(p.Writes == 0 && p.Samples == 0, "user plan is not modified");
            p = new MemoryPlatform(); p.Values.Remove(LaboratoryPower.Promote); c = New(p);
            c.Update(false, "threshold", 0, 0);
            Check(p.Writes == 0 && p.Receipt == "", "missing required idle setting fails atomically");
            p = new MemoryPlatform(); p.Values[LaboratoryPower.Promote] = 101; c = New(p);
            c.Update(false, "threshold", 0, 0);
            Check(p.Writes == 0 && p.Receipt == "", "invalid original range is rejected before snapshot");
            p = new MemoryPlatform(); p.Values[LaboratoryPower.Disable] = 1; p.FailWrite = LaboratoryPower.Promote; c = New(p);
            c.Update(false, "threshold", 0, 0);
            Check(p.Values[LaboratoryPower.Disable] == 1 && p.Receipt == "", "partial idle apply is rolled back");
            p = new MemoryPlatform(); p.Receipt = "unparseable"; c = New(p);
            c.Update(true, "disabled", 42, 99);
            Check(p.Writes == 0 && p.Receipt == "unparseable", "malformed journal blocks mutation and remains recoverable evidence");
        }
        private static void PresetIdleAndRestoration()
        {
            var p = new MemoryPlatform(); p.Values[LaboratoryPower.Disable] = 1;
            p.Values[LaboratoryPower.Promote] = 73; p.Values[LaboratoryPower.Scaling] = 1;
            var c = New(p); c.Update(false, "default", 0, 0);
            Check(p.Writes == 0 && p.Receipt == "" && p.Values[LaboratoryPower.Disable] == 1
                && p.Values[LaboratoryPower.Promote] == 73 && p.Values[LaboratoryPower.Scaling] == 1,
                "follow leaves the chosen preset intact without an idle override");

            p = new MemoryPlatform(); p.Values[LaboratoryPower.Promote] = 100; c = New(p);
            c.Update(false, "threshold", 0, 0);
            Check(p.Writes == 0 && p.Receipt.Length > 0
                && c.State(LaboratoryPower.IdleKey) == "电源方案已包含提高深层空闲门槛",
                "matching preset threshold is reported and journaled without value writes");
            c.Update(false, "threshold", 0, 0);
            Check(p.Writes == 0 && c.State(LaboratoryPower.IdleKey).Contains("电源方案已包含"),
                "matching preset status remains visible on subsequent updates");
            p.Values[LaboratoryPower.Disable] = 1; c.Update(false, "threshold", 0, 0);
            Check(p.Writes == 0 && p.Values[LaboratoryPower.Disable] == 1 && p.Receipt == ""
                && c.State(LaboratoryPower.IdleKey).Contains("让出"),
                "an external edit to an initially matching idle field is preserved");

            p = new MemoryPlatform(); p.Values[LaboratoryPower.Disable] = 1; c = New(p);
            c.Update(false, "disabled", 0, 0);
            Check(p.Writes == 0 && p.Receipt.Length > 0
                && c.State(LaboratoryPower.IdleKey) == "电源方案已包含禁止 CPU 空闲",
                "matching preset idle-disable is explicitly reported and journaled");
            c.Update(false, "default", 0, 0);
            Check(p.Writes == 0 && p.Values[LaboratoryPower.Disable] == 1 && p.Receipt == "",
                "turning the override off does not undo idle-disable inherited from the preset");

            p = new MemoryPlatform(); p.Values[LaboratoryPower.Disable] = 1;
            p.Values[LaboratoryPower.Promote] = 100; p.Values[LaboratoryPower.Scaling] = 1; c = New(p);
            c.Update(false, "threshold", 0, 0);
            Check(p.Values[LaboratoryPower.Disable] == 0 && p.Values[LaboratoryPower.Scaling] == 0
                && c.State(LaboratoryPower.IdleKey) == "已提高深层空闲门槛",
                "a partially matching preset is reported as an applied override");
            c.Update(false, "default", 0, 0);
            Check(p.Values[LaboratoryPower.Disable] == 1 && p.Values[LaboratoryPower.Promote] == 100
                && p.Values[LaboratoryPower.Scaling] == 1 && p.Receipt == "",
                "follow restores the actual preset values rather than legacy Pavise defaults");

            p = new MemoryPlatform(); p.Values[LaboratoryPower.Epp] = 12; p.Values[LaboratoryPower.Epp1] = 24;
            c = New(p); Engage(c, p);
            Check(p.Values[LaboratoryPower.Epp] == 48 && p.Values[LaboratoryPower.Epp1] == 48,
                "nonzero preset EPP values can enter an eligible yield trial");
            c.Update(false, "default", 42, 99);
            Check(p.Values[LaboratoryPower.Epp] == 12 && p.Values[LaboratoryPower.Epp1] == 24 && p.Receipt == "",
                "turning EPP yield off restores both actual preset EPP values");

            var translate = typeof(LaboratoryPowerController).GetMethod("EnglishState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check((string)translate.Invoke(null, new object[] { "电源方案已包含提高深层空闲门槛" })
                == "The power plan already includes raised deep-idle thresholds", "included threshold status has English text");
            Check((string)translate.Invoke(null, new object[] { "电源方案已包含禁止 CPU 空闲" })
                == "The power plan already disables CPU idle", "included idle-disable status has English text");
        }
        private static void IdleChangesRestartEpp()
        {
            // The observation window before an idle change cannot count toward the next trial.
            var p = new MemoryPlatform(); var c = New(p);
            c.Update(true, "default", 42, 99); Tick(c, p, 3);
            c.Update(true, "threshold", 42, 99); Tick(c, p, 3, "threshold");
            Check(p.Values[LaboratoryPower.Epp] == 0, "idle change discards the preceding EPP observation window");
            Tick(c, p, 1, "threshold");
            Check(p.Values[LaboratoryPower.Epp] == 48, "a complete fresh idle-specific baseline permits a new trial");

            // An active trial must drain EPP before the first write for the new idle mode.
            p = new MemoryPlatform(); c = New(p); Engage(c, p); p.Changes.Clear();
            c.Update(true, "threshold", 42, 99);
            int eppRestore = p.Changes.IndexOf(LaboratoryPower.Epp.ToString("N") + ":0");
            int idleApply = p.Changes.IndexOf(LaboratoryPower.Promote.ToString("N") + ":100");
            Check(eppRestore >= 0 && idleApply > eppRestore && p.Values[LaboratoryPower.Epp] == 0,
                "EPP restoration precedes a new idle override while verification is active");
            Tick(c, p, 3, "threshold");
            Check(p.Values[LaboratoryPower.Epp] == 0 && !c.State(LaboratoryPower.EppKey).Contains("持续监测"),
                "old EPP verification samples cannot finish after changing idle mode");

            // A held yield is also reset, and explicit idle-disable plus yield remains supported.
            p = new MemoryPlatform(); c = New(p); Engage(c, p); p.Watts = 46; Tick(c, p, 4);
            c.Update(true, "disabled", 42, 99);
            Check(p.Values[LaboratoryPower.Epp] == 0 && p.Values[LaboratoryPower.Disable] == 1,
                "idle-disable withdraws a held EPP yield before starting new observations");
            Tick(c, p, 4, "disabled");
            Check(p.Values[LaboratoryPower.Epp] == 48 && p.Values[LaboratoryPower.Disable] == 1,
                "idle-disable and EPP yield can coexist after a fresh baseline");
            c.Update(false, "disabled", 42, 99);
            Check(p.Values[LaboratoryPower.Epp] == 0 && p.Values[LaboratoryPower.Disable] == 1,
                "turning EPP off retains the independently selected idle strategy");

            p = new MemoryPlatform(); c = New(p); Engage(c, p, "threshold");
            p.Watts = 46; Tick(c, p, 4, "threshold");
            p.Values[LaboratoryPower.Promote] = 73; c.Update(true, "threshold", 42, 99);
            Check(p.Values[LaboratoryPower.Epp] == 0 && p.Values[LaboratoryPower.Promote] == 73
                && c.State(LaboratoryPower.IdleKey).Contains("让出"),
                "a detected external idle edit preserves the edit and invalidates a held EPP baseline");

            p = new MemoryPlatform(); c = New(p); Engage(c, p); p.FailWrite = LaboratoryPower.Epp;
            int samples = p.Samples; c.Update(true, "disabled", 42, 99);
            Check(p.Values[LaboratoryPower.Disable] == 0 && p.Samples == samples && p.Receipt.Length > 0
                && c.State(LaboratoryPower.IdleKey).Contains("恢复未完成"),
                "failed EPP recovery blocks the new idle configuration and all sampling");
            p.FailWrite = Guid.Empty; Check(c.Restore() && p.Receipt == "", "idle-change EPP recovery debt remains recoverable");

            p = new MemoryPlatform(); c = New(p); Engage(c, p, "threshold"); p.FailWrite = LaboratoryPower.Promote;
            samples = p.Samples; c.Update(true, "disabled", 42, 99);
            Check(p.Values[LaboratoryPower.Epp] == 0 && p.Values[LaboratoryPower.Disable] == 0
                && p.Samples == samples && p.Receipt.Length > 0,
                "failed old idle restoration blocks the next mode after restoring EPP first");
            p.FailWrite = Guid.Empty; Check(c.Restore() && p.Receipt == "", "idle-change idle recovery debt remains recoverable");
        }
        private static void EppAdmissionAndVerification()
        {
            var p = new MemoryPlatform(); var c = New(p); p.Battery = false;
            c.Update(true, "default", 42, 99);
            Check(p.Samples == 0 && p.Writes == 0, "desktop EPP trial is inapplicable");
            p.Battery = true; c.Update(true, "default", 0, 0);
            Check(p.Samples == 0, "missing renderer identity never samples");
            p = new MemoryPlatform(); c = New(p); Engage(c, p);
            Check(p.Values[LaboratoryPower.Epp] == 48 && p.Values[LaboratoryPower.Epp1] == 48, "eligible sustained baseline starts EPP 48 verification");
            p.Watts = 46; Tick(c, p, 4);
            Check(c.State(LaboratoryPower.EppKey).Contains("持续监测"), "power and utilization verification keeps monitoring");
            p.Cpu = 90; Tick(c, p, 2);
            Check(p.Values[LaboratoryPower.Epp] == 0 && p.Receipt == "", "sustained CPU pressure gives EPP back");
            p = new MemoryPlatform(); c = New(p); Engage(c, p); Tick(c, p, 4);
            Check(p.Values[LaboratoryPower.Epp] == 0 && c.State(LaboratoryPower.EppKey).Contains("未通过"), "no measured power decrease rejects experiment");
            int blockedWrites = p.Writes, blockedSamples = p.Samples;
            Tick(c, p, 12);
            Check(p.Writes == blockedWrites && p.Samples == blockedSamples, "failed verification stays blocked for the same renderer session");
            p = new MemoryPlatform(); c = New(p); Engage(c, p); p.Adapter = 2; Tick(c, p, 1);
            Check(p.Values[LaboratoryPower.Epp] == 0 && c.State(LaboratoryPower.EppKey).Contains("显卡"), "adapter migration invalidates the baseline");
            p = new MemoryPlatform(); c = New(p); Engage(c, p); p.Identity = false; Tick(c, p, 1);
            Check(p.Values[LaboratoryPower.Epp] == 0, "PID reuse or renderer exit restores EPP");
            p = new MemoryPlatform(); c = New(p); Engage(c, p); p.Missing = true; Tick(c, p, 2);
            Check(p.Values[LaboratoryPower.Epp] == 0, "missing data for ten seconds restores EPP");
            p = new MemoryPlatform(); c = New(p); Engage(c, p); p.Values[LaboratoryPower.Epp] = 70; Tick(c, p, 1);
            Check(p.Values[LaboratoryPower.Epp] == 70 && p.Values[LaboratoryPower.Epp1] == 0, "external EPP edit wins and other owned class is restored");
            p = new MemoryPlatform(); c = New(p); Engage(c, p); c.Update(true, "default", 43, 100);
            Check(p.Values[LaboratoryPower.Epp] == 0, "renderer identity change starts a fresh baseline after restore");
            p = new MemoryPlatform(); p.Cpu = 90; c = New(p); Engage(c, p);
            Check(p.Writes == 0, "CPU-heavy baseline never yields");
            p = new MemoryPlatform(); c = New(p); Engage(c, p); p.Ac = false; c.Update(true, "default", 42, 99);
            Check(p.Values[LaboratoryPower.Epp] == 0, "AC disconnect restores EPP");
            p = new MemoryPlatform(); c = New(p); Engage(c, p); p.Missing = true; p.FlushOk = false; Tick(c, p, 2);
            Check(p.Receipt.Length > 0 && c.State(LaboratoryPower.EppKey).Contains("恢复未完成"), "failed EPP flush retains recovery debt");
            p.Missing = false; p.FlushOk = true; p.Watts = 46; Tick(c, p, 1);
            Check(p.Receipt == "" && p.Values[LaboratoryPower.Epp] == 0 && !c.State(LaboratoryPower.EppKey).Contains("持续监测"),
                "successful debt recovery clears active verification state");
            blockedWrites = p.Writes; blockedSamples = p.Samples; Tick(c, p, 12);
            Check(p.Writes == blockedWrites && p.Samples == blockedSamples, "debt recovery does not restart a stopped session");
        }
#if PAVISE_LAB_POWER_CORE_AUDIT
        private static void SettledCoreAudit()
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Cur = 0;
            PowerPlan.ResetPlanRestoreForTest();
            var p = new MemoryPlatform(); var c = New(p); Engage(c, p);
            Guid previous = new Guid("20000000-0000-0000-0000-000000000002");
            int switches = 0, settingsWrites = 0;
            try
            {
                Check(PowerPlan.SelectPlan(PowerPlan.ManagedChoice), "managed plan choice saved in transient store");
                PowerPlan.ActivateTargetPlanForTest = delegate { return p.Plan; };
                PowerPlan.RestoreCurrentPlanForTest = delegate { return p.Plan; };
                PowerPlan.RestoreSetPlanForTest = delegate(Guid value) { switches++; return true; };
                var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                typeof(PowerPlan).GetField("active", flags).SetValue(null, true);
                typeof(PowerPlan).GetField("targetOwned", flags).SetValue(null, true);
                typeof(PowerPlan).GetField("saved", flags).SetValue(null, previous);
                typeof(PowerPlan).GetField("tuneState", flags).SetValue(null, 1);
                typeof(PowerPlan).GetField("resolved", flags).SetValue(null, true);
                typeof(PowerPlan).GetField("target", flags).SetValue(null, p.Plan);
                Settings.SaveStrDurable(LaboratoryPower.ReceiptKey, p.Receipt);
                string receipt = p.Receipt; int writes = p.Writes;
                Settings.BeforeStringWriteForTest = delegate(string key) { settingsWrites++; };
                Settings.BeforeValueWriteForTest = delegate(string key) { settingsWrites++; };
                for (int i = 0; i < 30; i++)
                {
                    Check(PowerPlan.EnforceSettled(true), "legal lab EPP override remains settled");
                    Check(PowerPlan.Enforce(true), "steady managed enforcement remains successful");
                }
                Check(switches == 0 && settingsWrites == 0 && p.Writes == writes
                    && p.Receipt == receipt && Settings.LoadStr(LaboratoryPower.ReceiptKey, "") == receipt,
                    "thirty core audits neither restore lab receipt nor switch or rewrite policy");
                PowerPlan.LaboratoryRestoreForTest = delegate { return false; };
                Check(!PowerPlan.Restore() && switches == 0
                    && (Guid)typeof(PowerPlan).GetField("saved", flags).GetValue(null) == previous,
                    "failed lab recovery prevents releasing the managed-plan lease");
                Check(!PowerPlan.RemoveManagedPlan() && !PowerPlan.RemoveCreatedPlan() && switches == 0,
                    "failed lab recovery prevents plan deletion");
                Check((Guid)typeof(PowerPlan).GetMethod("EnsureManagedPolicyPlan", flags).Invoke(null, null) == Guid.Empty,
                    "failed lab recovery prevents managed-plan replacement");
                Lang.Cur = 1;
                Check(c.State(LaboratoryPower.EppKey) == "Verifying EPP 48", "English state is translated");
                Lang.Cur = 0;
                Check(c.State(LaboratoryPower.EppKey) == "正在验证 EPP 48", "Chinese state remains available");
            }
            finally
            {
                Settings.BeforeStringWriteForTest = null; Settings.BeforeValueWriteForTest = null;
                PowerPlan.ResetPlanRestoreForTest(); Settings.UseTransientStoreForCurrentProcess(); Lang.Cur = 0;
            }
        }
#endif
        public static int Main()
        {
            try
            {
                IdleStrategies(); SafetyAndRecovery(); PresetIdleAndRestoration(); EppAdmissionAndVerification(); IdleChangesRestartEpp();
#if PAVISE_LAB_POWER_CORE_AUDIT
                SettledCoreAudit();
#endif
                Console.WriteLine("PASS LaboratoryPower " + checks + " checks; memory-only platform, no native writes"); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
#if PAVISE_LAB_POWER_ISOLATED
    internal static class Settings
    { internal static bool TryLoadStr(string key, out string value) { value = null; throw new Exception("Unexpected Settings access"); } }
    internal static class PowerPlan
    {
        internal static object LaboratoryGate = new object();
        internal sealed class NativeLaboratoryPower : ILaboratoryPowerPlatform
        {
            public long Now { get { throw new Exception("Native platform forbidden"); } }
            public bool OnAc { get { throw new Exception("Native platform forbidden"); } }
            public bool HasBattery { get { throw new Exception("Native platform forbidden"); } }
            public Guid ActiveManagedPlan() { throw new Exception("Native platform forbidden"); }
            public bool EppSupported(Guid plan) { throw new Exception("Native platform forbidden"); }
            public int OwnerStatus(Guid plan) { throw new Exception("Native platform forbidden"); }
            public uint? Read(Guid plan, Guid setting) { throw new Exception("Native platform forbidden"); }
            public bool Write(Guid plan, Guid setting, uint value) { throw new Exception("Native platform forbidden"); }
            public bool Flush(Guid plan) { throw new Exception("Native platform forbidden"); }
            public bool Load(out string receipt) { receipt = null; throw new Exception("Native platform forbidden"); }
            public bool Save(string receipt) { throw new Exception("Native platform forbidden"); }
            public LaboratoryPowerSample Sample(int pid, long creation) { throw new Exception("Native platform forbidden"); }
            public void ResetSampling() { throw new Exception("Native platform forbidden"); }
        }
    }
#endif
#if PAVISE_LAB_POWER_CORE_AUDIT
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new Exception("Product runtime forbidden in laboratory power checks"); } }
#endif
}
#endif
