using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class CurrentPlanIdleChecks
    {
        private static int checks;
        private static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        private static readonly Guid Second = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        private const string Machine = "60d4bfb17f3548deade415bba4c2535e";
        private static void Check(bool condition, string name)
        { checks++; if (!condition) throw new Exception(name); }

        private sealed class Memory : ILaboratoryPowerPlatform, ILaboratoryCurrentPowerPlatform
        {
            internal readonly Dictionary<Guid, Dictionary<Guid, uint>> Plans = new Dictionary<Guid, Dictionary<Guid, uint>>();
            internal readonly List<string> Events = new List<string>();
            internal Guid Active = Balanced, Managed;
            internal string Device = Machine, Receipt = "";
            internal bool Ac = true, Battery = true, SaveOk = true, LoadOk = true, FlushOk = true, Enumerate = true;
            internal Guid FailRestore;
            internal bool FailRead;
            internal int Writes, Samples;
            internal long Clock = TimeSpan.TicksPerSecond;
            internal Action AfterSave;
            internal Action<Guid, Guid, uint> AfterWrite;
            internal Memory() { Add(Balanced); Add(Second); }
            internal void Add(Guid p)
            {
                Plans[p] = new Dictionary<Guid, uint> {
                    { LaboratoryPower.Disable, 0 }, { LaboratoryPower.Promote, 60 },
                    { LaboratoryPower.Scaling, 1 }, { LaboratoryPower.Epp, 0 }, { LaboratoryPower.Epp1, 0 } };
            }
            public long Now { get { return Clock; } }
            public bool OnAc { get { return Ac; } }
            public bool HasBattery { get { return Battery; } }
            public string DeviceIdentity { get { return Device; } }
            public Guid ActivePlan() { return Active; }
            public Guid ActiveManagedPlan() { return Active == Managed ? Managed : Guid.Empty; }
            public int PlanStatus(Guid p) { return !Enumerate ? -1 : Plans.ContainsKey(p) ? 1 : 0; }
            public int OwnerStatus(Guid p) { return PlanStatus(p) == 1 ? p == Managed ? 1 : -1 : PlanStatus(p); }
            public bool EppSupported(Guid p) { return true; }
            public uint? Read(Guid p, Guid setting)
            {
                Dictionary<Guid, uint> values; uint v;
                return !FailRead && Plans.TryGetValue(p, out values) && values.TryGetValue(setting, out v) ? (uint?)v : null;
            }
            public bool Write(Guid p, Guid setting, uint value)
            {
                Writes++; Events.Add("write:" + p.ToString("N") + ":" + setting.ToString("N") + ":" + value);
                if (p == FailRestore && value != 100 && value != 48 && !(setting == LaboratoryPower.Disable && value == 1)) return false;
                if (!Plans.ContainsKey(p)) throw new Exception("write to absent plan");
                Plans[p][setting] = value;
                if (AfterWrite != null) AfterWrite(p, setting, value);
                return true;
            }
            public bool Flush(Guid p) { Events.Add("flush:" + p.ToString("N")); return FlushOk; }
            public bool Load(out string receipt) { receipt = Receipt; return LoadOk; }
            public bool Save(string receipt)
            {
                Events.Add("save:" + receipt); if (!SaveOk) return false;
                Receipt = receipt; if (AfterSave != null) AfterSave(); return true;
            }
            public LaboratoryPowerSample Sample(int pid, long creation)
            { Samples++; return new LaboratoryPowerSample { IdentityValid=true, AdapterKnown=true, GpuPercent=95, CpuPercent=30, CpuWatts=50 }; }
            public void ResetSampling() { }
        }
        private static LaboratoryPowerController New(Memory p) { return new LaboratoryPowerController(p, new object()); }
        private static string Receipt(Guid p, Guid setting, uint original, uint wanted, string scope, string device)
        { return "2|" + device + "|" + p.ToString("N") + "|" + setting.ToString("N") + ":" + original + ":" + wanted + ":" + scope; }
        private static void AdmissionAndIndependence()
        {
            var p = new Memory(); var c = New(p);
            c.Update(true, "threshold", 42, 99);
            Check(p.Plans[Balanced][LaboratoryPower.Promote] == 100 && p.Plans[Balanced][LaboratoryPower.Scaling] == 0,
                "unmanaged current plan receives threshold strategy");
            Check(p.Receipt.StartsWith("2|" + Machine + "|" + Balanced.ToString("N") + "|"), "receipt binds full machine and actual current plan");
            Check(p.Receipt.Contains(":c") && !p.Receipt.Contains(":m"), "idle authorization is explicitly current-plan scope");
            Check(p.Events[0].StartsWith("save:") && p.Events[1].StartsWith("write:"), "durable receipt precedes mutation");
            Check(c.State(LaboratoryPower.IdleKey).Contains("已提高"), "EPP ineligibility preserves successful idle state");
            Check(c.State(LaboratoryPower.EppKey).Contains("托管方案") && p.Samples == 0, "unmanaged EPP remains inapplicable and never samples");
            int writes = p.Writes;
            for (int i = 0; i < 30; i++) c.Update(true, "threshold", 42, 99);
            Check(p.Writes == writes && p.Samples == 0, "inapplicable EPP does not undo or rewrite stable idle");
            c.Update(false, "disabled", 0, 0);
            Check(p.Plans[Balanced][LaboratoryPower.Promote] == 60 && p.Plans[Balanced][LaboratoryPower.Scaling] == 1
                && p.Plans[Balanced][LaboratoryPower.Disable] == 1, "strategy switch restores old idle fields before applying new mode");
            c.Update(false, "default", 0, 0);
            Check(p.Plans[Balanced][LaboratoryPower.Disable] == 0 && p.Receipt == "", "default restores exact original and clears receipt");
            p = new Memory(); p.Battery = false; c = New(p); c.Update(true, "disabled", 42, 99);
            Check(p.Plans[Balanced][LaboratoryPower.Disable] == 1 && p.Samples == 0, "desktop idle does not require laptop EPP qualification");
            p = new Memory(); p.Managed = Balanced; p.Battery = false; c = New(p); c.Update(true, "disabled", 42, 99);
            Check(p.Plans[Balanced][LaboratoryPower.Disable] == 1 && c.State(LaboratoryPower.EppKey).Contains("笔记本"),
                "managed desktop EPP rejection leaves legitimate idle active");
            p = new Memory(); p.Ac = false; c = New(p); c.Update(true, "disabled", 42, 99);
            Check(p.Writes == 0 && p.Receipt == "" && p.Samples == 0, "DC never starts a mutation or trial");
            p.Ac = true; c.Update(false, "disabled", 0, 0); p.Ac = false; c.Update(false, "disabled", 0, 0);
            Check(p.Plans[Balanced][LaboratoryPower.Disable] == 0 && p.Receipt == "", "unplugging restores AC index without DC mutation");
        }
        private static void IdentityAndLegacy()
        {
            var p = new Memory(); p.Device = null; var c = New(p); c.Update(false, "disabled", 0, 0);
            Check(p.Writes == 0 && p.Receipt == "", "missing device identity fails closed");
            p = new Memory(); p.Device = "PAVISE"; c = New(p); c.Update(false, "disabled", 0, 0);
            Check(p.Writes == 0, "weak machine-name identity is rejected");
            p = new Memory(); p.Enumerate = false; c = New(p); c.Update(false, "disabled", 0, 0);
            Check(p.Writes == 0 && p.Receipt == "", "unknown enumeration prevents new writes");
            string old = "1|" + Balanced.ToString("N") + "|" + LaboratoryPower.Disable.ToString("N") + ":0:1";
            p = new Memory(); p.Receipt = old; p.Plans[Balanced][LaboratoryPower.Disable] = 1;
            Check(!New(p).Restore() && p.Writes == 0 && p.Receipt == old,
                "V1 standard GUID does not authorize restoration of a foreign current plan");
            p.Managed = Balanced;
            Check(New(p).Restore() && p.Plans[Balanced][LaboratoryPower.Disable] == 0 && p.Receipt == "", "V1 owned plan remains recoverable");
            p = new Memory(); p.Receipt = Receipt(Balanced, LaboratoryPower.Disable, 0, 1, "c", "bdbd0ab0318d432e88658c03941319f7");
            p.Plans[Balanced][LaboratoryPower.Disable] = 1; string wrong = p.Receipt;
            Check(!New(p).Restore() && p.Writes == 0 && p.Receipt == wrong,
                "same standard plan GUID on a different PC is not restoration authority");
            p.Device = null;
            Check(!New(p).Restore() && p.Writes == 0 && p.Receipt == wrong, "unknown machine retains debt and never restores");
            p = new Memory(); p.Receipt = Receipt(Balanced, LaboratoryPower.Epp, 0, 48, "c", Machine);
            Check(!New(p).Restore() && p.Writes == 0 && p.Receipt.Length > 0, "EPP current-plan scope is invalid even in a syntactically valid V2 journal");
            p.Receipt = Receipt(Balanced, LaboratoryPower.Epp, 0, 48, "m", Machine); p.Plans[Balanced][LaboratoryPower.Epp] = 48;
            Check(!New(p).Restore() && p.Writes == 0 && p.Receipt.Length > 0, "V2 EPP still requires managed ownership");
            p.Managed = Balanced;
            Check(New(p).Restore() && p.Plans[Balanced][LaboratoryPower.Epp] == 0 && p.Receipt == "", "V2 managed EPP can recover");
            p = new Memory(); p.Receipt = Receipt(Balanced, LaboratoryPower.Disable, 0, 1, "c", Machine); p.Plans[Balanced][LaboratoryPower.Disable] = 1;
            Check(New(p).Restore() && p.Plans[Balanced][LaboratoryPower.Disable] == 0 && p.Receipt == "", "V2 unmanaged idle recovers after controller restart");
        }
        private static void SwitchingAndFailure()
        {
            var p = new Memory(); var c = New(p); c.Update(false, "threshold", 0, 0);
            p.Active = Second; int before = p.Events.Count; c.Update(false, "disabled", 0, 0);
            Check(p.Plans[Balanced][LaboratoryPower.Promote] == 60 && p.Plans[Balanced][LaboratoryPower.Scaling] == 1, "switch restores former plan");
            Check(p.Plans[Second][LaboratoryPower.Disable] == 1 && p.Active == Second, "strategy follows current plan without reactivating previous plan");
            int oldLast=-1, newFirst=-1;
            for (int i=before;i<p.Events.Count;i++)
            {
                if (p.Events[i].StartsWith("write:" + Balanced.ToString("N"))) oldLast=i;
                if (newFirst < 0 && p.Events[i].StartsWith("write:" + Second.ToString("N"))) newFirst=i;
            }
            Check(oldLast >= before && newFirst > oldLast, "old plan recovery completes before any write to new plan");
            Check(c.Restore() && p.Active == Second && p.Plans[Second][LaboratoryPower.Disable] == 0, "stop restores current plan without changing selection");
            p = new Memory(); c = New(p); c.Update(false, "threshold", 0, 0); p.Active = Second; p.FailRestore = Balanced;
            c.Update(false, "disabled", 0, 0);
            Check(p.Plans[Second][LaboratoryPower.Disable] == 0 && p.Receipt.Length > 0, "failed former-plan restore blocks all mutations of next plan");
            p.FailRestore = Guid.Empty;
            Check(c.Restore() && p.Receipt == "", "failed restore debt retries successfully");
            p = new Memory(); c = New(p); c.Update(false, "disabled", 0, 0); p.Enumerate = false; int writes = p.Writes;
            Check(!c.Restore() && p.Writes == writes && p.Receipt.Length > 0, "enumeration ambiguity retains original debt with no mutation");
            p.Enumerate = true; p.Plans.Remove(Balanced);
            Check(c.Restore() && p.Receipt == "" && p.Writes == writes, "only proven plan deletion clears obsolete debt");
            p = new Memory(); p.AfterSave = delegate { if (p.Receipt.Length > 0) p.Active = Second; }; c = New(p);
            c.Update(false, "threshold", 0, 0);
            Check(p.Writes == 0 && p.Active == Second && p.Plans[Second][LaboratoryPower.Promote] == 60,
                "switch after receipt save prevents all apply writes and never steals active selection");
            p = new Memory(); p.Plans[Balanced][LaboratoryPower.Disable] = 1;
            p.AfterWrite = delegate(Guid owner, Guid setting, uint value) { if (owner == Balanced && setting == LaboratoryPower.Disable && value == 0) p.Active = Second; };
            c = New(p); c.Update(false, "threshold", 0, 0);
            Check(p.Active == Second && p.Plans[Balanced][LaboratoryPower.Disable] == 1 && p.Plans[Balanced][LaboratoryPower.Promote] == 60,
                "mid-apply plan switch rolls back only already-owned AC values");
            Check(p.Plans[Second][LaboratoryPower.Promote] == 60 && p.Receipt == "", "mid-apply switch never writes newly active plan");
            p = new Memory(); c = New(p); c.Update(false, "disabled", 0, 0); p.FlushOk = false;
            Check(!c.Restore() && p.Receipt.Length > 0, "flush failure keeps full receipt even after original readback");
            p.FlushOk = true; Check(c.Restore() && p.Receipt == "", "flush debt remains retryable");
            p = new Memory(); p.SaveOk = false; c = New(p); c.Update(false, "disabled", 0, 0);
            Check(p.Writes == 0, "failed durable receipt prevents any write");
            p = new Memory(); p.LoadOk = false; c = New(p); c.Update(false, "disabled", 0, 0);
            Check(p.Writes == 0, "failed receipt read fails closed");
            p = new Memory(); c = New(p); c.Update(false, "disabled", 0, 0); p.FailRead = true; writes = p.Writes;
            Check(!c.Restore() && p.Writes == writes && p.Receipt.Length > 0, "unreadable original values retain debt without writing");
        }
        private static void ExternalAndSharedJournal()
        {
            var p = new Memory(); var c = New(p); c.Update(false, "threshold", 0, 0);
            p.Plans[Balanced][LaboratoryPower.Promote] = 73; c.Update(false, "threshold", 0, 0);
            Check(p.Plans[Balanced][LaboratoryPower.Promote] == 73 && p.Plans[Balanced][LaboratoryPower.Scaling] == 1,
                "external threshold edit wins while untouched owned knobs are restored");
            Check(p.Receipt == "" && c.State(LaboratoryPower.IdleKey).Contains("让出"), "external edit relinquishes idle for current session");
            int writes = p.Writes; for(int i=0;i<30;i++) c.Update(false, "threshold", 0, 0);
            Check(p.Writes == writes && p.Plans[Balanced][LaboratoryPower.Promote] == 73, "same session never fights external user setting");
            p = new Memory(); p.Plans[Balanced][LaboratoryPower.Promote] = 100; p.Plans[Balanced][LaboratoryPower.Scaling] = 0;
            c = New(p); c.Update(false, "threshold", 0, 0);
            Check(p.Writes == 0 && p.Receipt.Length > 0, "already-correct idle strategy records intent without redundant writes");
            p.Plans[Balanced][LaboratoryPower.Disable] = 1; c.Update(false, "threshold", 0, 0); writes = p.Writes;
            for(int i=0;i<4;i++) c.Update(false, "threshold", 0, 0);
            Check(p.Writes == writes && p.Plans[Balanced][LaboratoryPower.Disable] == 1 && c.State(LaboratoryPower.IdleKey).Contains("让出"),
                "external change of an initially-correct idle knob is never reclaimed");
            p = new Memory(); p.Managed = Balanced; c = New(p);
            for(int i=0;i<5;i++) { c.Update(true, "disabled", 42, 99); p.Clock += 5*TimeSpan.TicksPerSecond; }
            Check(p.Plans[Balanced][LaboratoryPower.Disable] == 1 && p.Plans[Balanced][LaboratoryPower.Epp] == 48,
                "managed EPP trial can coexist with current-plan idle");
            Check(p.Receipt.Contains(":c") && p.Receipt.Contains(":m"), "shared receipt keeps per-setting authority separate");
            c.Update(false, "disabled", 42, 99);
            Check(p.Plans[Balanced][LaboratoryPower.Disable] == 1 && p.Plans[Balanced][LaboratoryPower.Epp] == 0,
                "turning EPP off restores only EPP group");
            Check(p.Receipt.Contains(LaboratoryPower.Disable.ToString("N")) && !p.Receipt.Contains(LaboratoryPower.Epp.ToString("N")),
                "partial recovery retains only idle debt");
            p.Active = Second; c.Update(true, "threshold", 42, 99); int samples=p.Samples;
            for(int i=0;i<8;i++) { p.Clock += 5*TimeSpan.TicksPerSecond; c.Update(true,"threshold",42,99); }
            Check(p.Samples == samples && p.Plans[Balanced][LaboratoryPower.Disable] == 0 && p.Plans[Second][LaboratoryPower.Promote] == 100,
                "switch to foreign plan restores previous groups, applies idle, and stops EPP sampling");
        }
        public static int Main()
        {
            try
            {
                AdmissionAndIndependence(); IdentityAndLegacy(); SwitchingAndFailure(); ExternalAndSharedJournal();
                Console.WriteLine("PASS CurrentPlanIdle " + checks + " checks; memory-only power, identity, receipt, active-selection primitives"); return 0;
            }
            catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
