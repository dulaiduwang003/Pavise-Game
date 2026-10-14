#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunPowerPriorityTests()
        {
            foreach (bool laptop in new[] { false, true })
                foreach (bool aggressive in new[] { false, true })
                    foreach (bool handheld in new[] { false, true })
                        Eq(laptop && aggressive && !handheld ? 2u : 1u,
                            PowerPlan.ResolveAcPersonality(laptop, aggressive, handheld));
            Eq(true, Array.IndexOf(SystemProcessCatalog.PresetWhitelist, "ghelper") >= 0);
            using (var f = new OverlayFixture())
            {
                Eq(true, PowerOverlay.Activate()); Eq(PowerOverlay.Max, f.Actual); Eq(Guid.Empty.ToString("D"), f.Receipt);
                for (int i = 0; i < 200; i++)
                {
                    f.Actual = i % 2 == 0 ? Guid.Empty : f.Efficiency;
                    Eq(true, PowerOverlay.Activate()); Eq(PowerOverlay.Max, f.Actual);
                    Eq(Guid.Empty.ToString("D"), f.Receipt);
                }
                int writes = f.Writes, saves = f.Saves;
                for (int i = 0; i < 600; i++) Eq(true, PowerOverlay.Activate());
                Eq(writes, f.Writes); Eq(saves, f.Saves);
                f.Actual = f.Efficiency;
                Eq(true, PowerOverlay.Restore()); Eq(Guid.Empty, f.Actual); Eq("", f.Receipt);
            }
            using (var f = new OverlayFixture())
            {
                f.Actual = PowerOverlay.Max;
                Eq(true, PowerOverlay.Activate()); Eq(PowerOverlay.Max.ToString("D"), f.Receipt); Eq(0, f.Writes);
                f.Actual = f.Efficiency; Eq(true, PowerOverlay.Activate());
                Eq(true, PowerOverlay.Restore()); Eq(PowerOverlay.Max, f.Actual);
            }
            foreach (uint? personality in new uint?[] { null, 0, 1 })
                using (var f = new OverlayFixture())
                { f.Personality = personality; Eq(false, PowerOverlay.Activate()); Eq(0, f.Writes); Eq(0, f.Saves); }
            using (var f = new OverlayFixture())
            {
                f.IgnoreSave = true; Eq(false, PowerOverlay.Activate()); Eq(0, f.Writes);
                f.IgnoreSave = false; f.EffectiveRejected = true;
                Eq(false, PowerOverlay.Activate()); Eq(true, PowerOverlay.HasResidue);
                Eq(true, PowerOverlay.Restore()); Eq(Guid.Empty, f.Actual);
            }
            using (var f = new OverlayFixture())
            {
                f.Status = 5; Eq(false, PowerOverlay.Activate()); Eq(true, PowerOverlay.HasResidue);
                f.Status = 0; Eq(true, PowerOverlay.Restore()); Eq("", f.Receipt);
                f.Receipt = "bad-record"; Eq(false, PowerOverlay.Restore()); Eq("bad-record", f.Receipt);
            }
            using (var f = new OverlayFixture())
            {
                Eq(true, PowerOverlay.Activate()); f.IgnoreClear = true;
                Eq(false, PowerOverlay.Restore()); Eq(Guid.Empty, f.Actual);
                int writes = f.Writes; f.Actual = f.Efficiency;
                Eq(false, PowerOverlay.Restore()); Eq(writes, f.Writes); Eq(f.Efficiency, f.Actual);
                f.IgnoreClear = false; Eq(true, PowerOverlay.Restore()); Eq(writes, f.Writes); Eq("", f.Receipt);
            }
            using (var f = new OverlayFixture())
            {
                f.Receipt = Guid.Empty.ToString("D"); f.Actual = f.Efficiency;
                Eq(true, PowerOverlay.Restore()); Eq(0, f.Writes); Eq(f.Efficiency, f.Actual); Eq("", f.Receipt);
                f.Receipt = Guid.Empty.ToString("D"); f.Actual = PowerOverlay.Max;
                Eq(true, PowerOverlay.Restore()); Eq(Guid.Empty, f.Actual);
            }
            foreach (bool denied in new[] { false, true })
            {
                var indices = new Dictionary<Guid, uint>(); int writes = 0;
                PowerPlan.PriorityReadForTest = delegate(Guid scheme, Guid sub, Guid setting) { uint value; return indices.TryGetValue(setting, out value) ? value : 50u; };
                PowerPlan.PriorityWriteForTest = delegate(Guid scheme, Guid sub, Guid setting, uint value) { writes++; if (!denied) indices[setting] = value; return true; };
                try
                {
                    bool changed;
                    Eq(!denied, PowerPlan.EnforcePriorityForTest(Guid.NewGuid(), true, out changed));
                    if (!denied)
                    {
                        Eq(true, changed); Eq(4, writes);
                        for (int i = 0; i < 400; i++) { Eq(true, PowerPlan.EnforcePriorityForTest(Guid.Empty, true, out changed)); Eq(false, changed); }
                        Eq(4, writes);
                        indices.Clear(); Eq(true, PowerPlan.EnforcePriorityForTest(Guid.Empty, true, out changed)); Eq(8, writes);
                    }
                }
                finally { PowerPlan.PriorityReadForTest = null; PowerPlan.PriorityWriteForTest = null; }
            }
            Console.WriteLine("PASS power priority: 200 external overlay changes, 600 no-write audits, 400 no-write setting audits, durable restore and effective-mode verification");
        }

        private sealed class OverlayFixture : IDisposable
        {
            internal Guid Actual;
            internal readonly Guid Efficiency = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
            internal string Receipt = "";
            internal uint? Personality = 2;
            internal uint Status;
            internal int Writes, Saves;
            internal bool EffectiveRejected, IgnoreSave, IgnoreClear;
            internal OverlayFixture()
            {
                PowerOverlay.ResetForTest();
                PowerOverlay.PersonalityForTest = delegate { return Personality; };
                PowerOverlay.ActualForTest = delegate { return Actual; };
                PowerOverlay.EffectiveForTest = delegate { return EffectiveRejected ? Guid.Empty : Actual; };
                PowerOverlay.SetForTest = delegate(Guid value) { Writes++; if (Status == 0) Actual = value; return Status; };
                PowerOverlay.LoadForTest = delegate { return Receipt; };
                PowerOverlay.SaveForTest = delegate(string value) { Saves++; if (!IgnoreSave && !(IgnoreClear && value.Length == 0)) Receipt = value; return true; };
            }
            public void Dispose() { PowerOverlay.ResetForTest(); }
        }
    }
}
#endif
