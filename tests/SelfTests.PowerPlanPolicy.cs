#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunPowerPlanPolicyRegressionTests()
        {
            Action[] tests = { IntelGraphicsPlanOnlyRelaxesConfirmedIntegratedHandheld, PowerPlatformRequiresCompleteEvidence, PowerPlatformParsesNamedFields, PowerBootBoundaryToleratesClockCorrection,
                PowerAutonomousPolicyIsNotHardwareState, PowerUnknownMinimumPreservesEachSide, PowerIdlePolicyIsRestoreOnly, ExtremeIdleApplyAndRestore, ExtremeIdleFailureAndReceipt,
                PowerExtremeRestoresEveryRetiredKnob, PowerExtremePartialRestoreRetainsReceipt,
                PowerExtremeReadbackAndJournalFailuresRetainReceipt, PowerExtremeRejectsWrongOwnerAndMalformedReceipt,
                PowerExtremeDeletedPlanClearsOnlyItsReceipt, PowerExtremePreparationWithoutReceiptWritesNothing,
                PowerExtremePreparationRetainsFailedRestore, PowerExtremeOrphanRequiresCompleteEnumeration,
                PowerExtremeOrphanKeepsExistingAndUnknownOwners, PowerExtremeOrphanClearsOnlyAfterDurableSave };
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS power-plan-policy tests=" + tests.Length + " system_power_writes=mocked");
        }

        private static void IntelGraphicsPlanOnlyRelaxesConfirmedIntegratedHandheld()
        {
            var intel = new GpuAdapter { Vendor = GpuVendor.Intel, Integrated = true, IntegratedKnown = true };
            Eq(2u, PowerPlan.ResolveIntelGraphicsPlan(true, true, new[] { intel }));
            foreach (bool aggressive in new[] { false, true })
                foreach (bool handheld in new[] { false, true })
                    if (!aggressive || !handheld)
                        Eq(1u, PowerPlan.ResolveIntelGraphicsPlan(aggressive, handheld, new[] { intel }));
            foreach (GpuAdapter[] topology in new[] {
                null, new GpuAdapter[0], new GpuAdapter[] { null },
                new[] { new GpuAdapter { Vendor = GpuVendor.Intel, Integrated = true } },
                new[] { new GpuAdapter { Vendor = GpuVendor.Intel, IntegratedKnown = true } },
                new[] { new GpuAdapter { Vendor = GpuVendor.Amd, Integrated = true, IntegratedKnown = true } },
                new[] { new GpuAdapter { Vendor = GpuVendor.Unknown, Integrated = true, IntegratedKnown = true } },
                new[] { intel, intel },
                new[] { intel, new GpuAdapter { Vendor = GpuVendor.Nvidia, IntegratedKnown = true } },
                new[] { intel, new GpuAdapter { Vendor = GpuVendor.Intel, IntegratedKnown = true } },
                new[] { intel, new GpuAdapter { Vendor = GpuVendor.Unknown } } })
                Eq(1u, PowerPlan.ResolveIntelGraphicsPlan(true, true, topology));
            Eq(1u, PowerPlan.ResolveIntelGraphicsPlan(true, false, new[] { intel }));
            Eq(5u, PowerPlanProfile.Resolve(false, true, false, null).HeteroSched);
        }

        private static void PowerPlatformRequiresCompleteEvidence()
        {
            Eq(ProcessorPowerPlatform.Interface.Cppc, ProcessorPowerPlatform.Classify(new uint[] { 3, 3 }, 2));
            Eq(ProcessorPowerPlatform.Interface.AcpiPState, ProcessorPowerPlatform.Classify(new uint[] { 1, 1 }, 2));
            foreach (uint[] values in new[] { new uint[0], new uint[] { 3 }, new uint[] { 3, 1 },
                new uint[] { 0, 0 }, new uint[] { 2, 2 }, new uint[] { 4, 4 }, new uint[] { 3, 3, 3 } })
                Eq(ProcessorPowerPlatform.Interface.Unknown, ProcessorPowerPlatform.Classify(values, 2));
            Eq(ProcessorPowerPlatform.Interface.Unknown, ProcessorPowerPlatform.Classify(new uint[] { 3 }, 0));
        }

        private static string PowerCapabilityXml()
        {
            return "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>"
                + "<Provider Name='Microsoft-Windows-Kernel-Processor-Power'/><EventID>55</EventID><Version>0</Version>"
                + "</System><EventData><Data Name='PerformanceImplementation'>3</Data>"
                + "<Data Name='Number'>12</Data><Data Name='Group'>1</Data></EventData></Event>";
        }

        private static void PowerPlatformParsesNamedFields()
        {
            string processor; uint implementation;
            string good = PowerCapabilityXml();
            Eq(true, ProcessorPowerPlatform.TryReadCapability(good, out processor, out implementation));
            Eq("1:12", processor); Eq(3u, implementation);
            foreach (string bad in new[] { "", "<broken", good.Replace("<Version>0", "<Version>1"),
                good.Replace("<EventID>55", "<EventID>56"), good.Replace("Kernel-Processor-Power", "Other-Provider"),
                good.Replace("Name='Group'", "Name='Missing'"), good.Replace(">12<", ">64<"),
                good.Replace(">3<", ">no<"), good.Replace("</EventData>", "<Data Name='Group'>0</Data></EventData>") })
                Eq(false, ProcessorPowerPlatform.TryReadCapability(bad, out processor, out implementation));
        }

        private static void PowerBootBoundaryToleratesClockCorrection()
        {
            DateTime boot = new DateTime(2026, 8, 18, 6, 47, 35, DateTimeKind.Utc);
            DateTime now = boot.AddDays(18);
            Eq(true, ProcessorPowerPlatform.BootRecordMatchesUptime(boot, boot.AddMinutes(1), now));
            Eq(true, ProcessorPowerPlatform.BootRecordMatchesUptime(boot, boot.AddMinutes(-1), now));
            Eq(false, ProcessorPowerPlatform.BootRecordMatchesUptime(boot, boot.AddDays(1), now));
            Eq(false, ProcessorPowerPlatform.BootRecordMatchesUptime(now.AddMinutes(1), now, now));
        }

        private static void PowerAutonomousPolicyIsNotHardwareState()
        {
            Eq((bool?)true, ProcessorPowerPlatform.AutonomousMinimum(ProcessorPowerPlatform.Interface.Cppc, 1));
            foreach (uint? value in new uint?[] { null, 0, 1, 2, uint.MaxValue })
            {
                Eq((bool?)null, ProcessorPowerPlatform.AutonomousMinimum(ProcessorPowerPlatform.Interface.Unknown, value));
                Eq((bool?)false, ProcessorPowerPlatform.AutonomousMinimum(ProcessorPowerPlatform.Interface.AcpiPState, value));
                if (value != 1)
                    Eq((bool?)null, ProcessorPowerPlatform.AutonomousMinimum(ProcessorPowerPlatform.Interface.Cppc, value));
            }
            Eq((bool?)true, ProcessorPowerPlatform.AutonomousMinimum(ProcessorPowerPlatform.Interface.Cppc, 1));
            Eq((bool?)null, ProcessorPowerPlatform.AutonomousMinimum(ProcessorPowerPlatform.Interface.Cppc, 0));
        }

        private static void PowerUnknownMinimumPreservesEachSide()
        {
            uint ac, dc;
            Eq(true, ProcessorPowerPlatform.TryResolveMinimumIndices(true, false, 20, 100,
                delegate { throw new InvalidOperationException("Known policy must not need original values"); }, out ac, out dc));
            Eq(20u, ac); Eq(100u, dc);
            Eq(true, ProcessorPowerPlatform.TryResolveMinimumIndices(null, true, 100, 10,
                delegate(bool onAc) { Eq(true, onAc); return 37; }, out ac, out dc));
            Eq(37u, ac); Eq(10u, dc);
            Eq(true, ProcessorPowerPlatform.TryResolveMinimumIndices(true, null, 20, 100,
                delegate(bool onAc) { Eq(false, onAc); return 53; }, out ac, out dc));
            Eq(20u, ac); Eq(53u, dc);
            Eq(false, ProcessorPowerPlatform.TryResolveMinimumIndices(null, true, 100, 10,
                delegate { return null; }, out ac, out dc));
            Eq(false, ProcessorPowerPlatform.TryResolveMinimumIndices(null, true, 100, 10,
                delegate { return 101; }, out ac, out dc));
        }

        private static readonly Guid powerDemote = new Guid("4b92d758-5a24-4851-a470-815d78aee119");
        private static readonly Guid powerPromote = new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c");
        private static readonly Guid powerScaling = new Guid("6c2993b0-8f48-481f-bcc6-00dd2742aa06");
        private static readonly Guid powerIdleCheck = new Guid("c4581c31-89ab-4597-8e2b-9c9cab440e6b");

        private static void PowerIdlePolicyIsRestoreOnly()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Eq(null, typeof(PowerPlan).GetField("ExtremeKnobs", flags));
            Eq(null, typeof(PowerPlan).GetMethod("SnapshotExtremeKnobs", flags));
            Eq(3, typeof(PowerPlan).GetMethod("Enforce", flags).GetParameters().Length);
            Eq(4, typeof(PowerPlan).GetMethod("TuneTarget", flags).GetParameters().Length);
        }

        private static void ExtremeIdleApplyAndRestore()
        {
            using (var f = new PowerPolicyFixture())
            {
                Guid promote = new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c"), scaling = new Guid("6c2993b0-8f48-481f-bcc6-00dd2742aa06");
                f.Set(promote, 60, 70); f.Set(scaling, 1, 1);
                Eq(true, PowerPlan.ApplyExtremeIdleForTest(f.Scheme, true));
                Eq(100u, f.Get(promote, true)); Eq(0u, f.Get(scaling, false));
                string original = f.Receipt(); int writes = f.Writes;
                Eq(true, PowerPlan.ApplyExtremeIdleForTest(f.Scheme, true));
                Eq(original, f.Receipt()); Eq(writes, f.Writes);
                Eq(true, PowerPlan.ApplyExtremeIdleForTest(f.Scheme, false));
                Eq(60u, f.Get(promote, true)); Eq(70u, f.Get(promote, false));
                Eq(1u, f.Get(scaling, true)); Eq("", f.Receipt());
            }
        }
        private static void ExtremeIdleFailureAndReceipt()
        {
            using (var f = new PowerPolicyFixture())
            {
                Guid promote = new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c"), scaling = new Guid("6c2993b0-8f48-481f-bcc6-00dd2742aa06");
                f.Set(promote, 60, 70); f.Set(scaling, 1, 1);
                PowerPlan.ExtremeSaveSnapshotForTest = delegate { return false; };
                Eq(false, PowerPlan.ApplyExtremeIdleForTest(f.Scheme, true)); Eq(0, f.Writes);
                PowerPlan.ExtremeSaveSnapshotForTest = null;
                f.FailDc = true;
                Eq(false, PowerPlan.ApplyExtremeIdleForTest(f.Scheme, true));
                Eq(60u, f.Get(promote, true)); Eq(1u, f.Get(scaling, true));
                f.FailDc = false;
                Eq(true, PowerPlan.ApplyExtremeIdleForTest(f.Scheme, true));
                f.RemoveDc(promote);
                Eq(false, PowerPlan.ApplyExtremeIdleForTest(f.Scheme, false));
                Eq(true, f.Receipt().Length > 0);
                f.Set(promote, 60, 100);
                Eq(true, PowerPlan.ApplyExtremeIdleForTest(f.Scheme, false)); Eq("", f.Receipt());
            }
        }

        private sealed class PowerPolicyFixture : IDisposable
        {
            internal readonly Guid Scheme = new Guid("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
            internal readonly Dictionary<string, uint> Values = new Dictionary<string, uint>();
            internal int Writes;
            internal bool FailDc, LieAboutWrite;
            private readonly string saved = Settings.LoadStr(PowerPlan.ExtremeSnapKey, "");
            internal PowerPolicyFixture()
            {
                Settings.SaveStr(PowerPlan.ExtremeSnapKey, "");
                PowerPlan.ExtremeReadIndexForTest = delegate(Guid scheme, Guid setting, bool ac)
                {
                    Eq(Scheme, scheme);
                    uint value;
                    return Values.TryGetValue(Index(setting, ac), out value) ? (uint?)value : null;
                };
                PowerPlan.ExtremeWriteIndexForTest = delegate(Guid scheme, Guid setting, bool ac, uint value)
                {
                    Eq(Scheme, scheme); Writes++;
                    if (!ac && FailDc) return false;
                    if (!LieAboutWrite) Values[Index(setting, ac)] = value;
                    return true;
                };
                PowerPlan.EnumerateSchemeForTest = delegate { throw new InvalidOperationException("Unexpected scheme enumeration"); };
            }
            private static string Index(Guid setting, bool ac) { return setting.ToString("N") + (ac ? "AC" : "DC"); }
            internal void Set(Guid setting, uint ac, uint dc) { Values[Index(setting, true)] = ac; Values[Index(setting, false)] = dc; }
            internal uint Get(Guid setting, bool ac) { return Values[Index(setting, ac)]; }
            internal void RemoveDc(Guid setting) { Values.Remove(Index(setting, false)); }
            internal void Receipt(string value) { Settings.SaveStr(PowerPlan.ExtremeSnapKey, value); }
            internal string Receipt() { return Settings.LoadStr(PowerPlan.ExtremeSnapKey, ""); }
            public void Dispose()
            {
                PowerPlan.ExtremeReadIndexForTest = null; PowerPlan.ExtremeWriteIndexForTest = null;
                PowerPlan.ExtremeSaveSnapshotForTest = null;
                PowerPlan.EnumerateSchemeForTest = null;
                Settings.SaveStr(PowerPlan.ExtremeSnapKey, saved);
            }
        }

        private static string PowerSaved(Guid setting, uint ac, uint dc) { return setting.ToString("N") + "=" + ac + "," + dc; }

        private static void PowerExtremeRestoresEveryRetiredKnob()
        {
            using (var f = new PowerPolicyFixture())
            {
                f.Set(powerDemote, 0, 0); f.Set(powerPromote, 100, 100); f.Set(powerScaling, 0, 0); f.Set(powerIdleCheck, 1, 1);
                f.Receipt(PowerSaved(powerDemote, 40, 30) + ";" + PowerSaved(powerPromote, 60, 50)
                    + ";" + PowerSaved(powerScaling, 1, 1) + ";" + PowerSaved(powerIdleCheck, 50000, 50000));
                Eq(true, PowerPlan.RestoreExtremeForTest(f.Scheme));
                Eq(40u, f.Get(powerDemote, true)); Eq(30u, f.Get(powerDemote, false));
                Eq(60u, f.Get(powerPromote, true)); Eq(50u, f.Get(powerPromote, false));
                Eq(1u, f.Get(powerScaling, true)); Eq(1u, f.Get(powerScaling, false));
                Eq(50000u, f.Get(powerIdleCheck, true)); Eq(50000u, f.Get(powerIdleCheck, false));
                Eq(8, f.Writes); Eq("", f.Receipt());
                Eq(true, PowerPlan.RestoreExtremeForTest(f.Scheme)); Eq(8, f.Writes);
            }
        }

        private static void PowerExtremePartialRestoreRetainsReceipt()
        {
            using (var f = new PowerPolicyFixture())
            {
                f.Set(powerDemote, 0, 0); f.Receipt(PowerSaved(powerDemote, 40, 30)); f.FailDc = true;
                Eq(false, PowerPlan.RestoreExtremeForTest(f.Scheme));
                Eq(40u, f.Get(powerDemote, true)); Eq(0u, f.Get(powerDemote, false));
                Eq(true, f.Receipt().Contains(PowerSaved(powerDemote, 40, 30)));
                int before = f.Writes; f.FailDc = false;
                Eq(true, PowerPlan.RestoreExtremeForTest(f.Scheme));
                Eq(before + 1, f.Writes); Eq(30u, f.Get(powerDemote, false)); Eq("", f.Receipt());
            }
        }

        private static void PowerExtremeReadbackAndJournalFailuresRetainReceipt()
        {
            using (var f = new PowerPolicyFixture())
            {
                f.Set(powerDemote, 0, 0); f.Receipt(PowerSaved(powerDemote, 40, 30)); f.LieAboutWrite = true;
                Eq(false, PowerPlan.RestoreExtremeForTest(f.Scheme));
                Eq(true, f.Receipt().Contains(PowerSaved(powerDemote, 40, 30)));
                f.LieAboutWrite = false;
                PowerPlan.ExtremeSaveSnapshotForTest = delegate { return false; };
                Eq(false, PowerPlan.RestoreExtremeForTest(f.Scheme));
                Eq(true, f.Receipt().Contains(PowerSaved(powerDemote, 40, 30)));
                int writes = f.Writes;
                PowerPlan.ExtremeSaveSnapshotForTest = null;
                Eq(true, PowerPlan.RestoreExtremeForTest(f.Scheme)); Eq(writes, f.Writes); Eq("", f.Receipt());
            }
        }

        private static void PowerExtremeRejectsWrongOwnerAndMalformedReceipt()
        {
            using (var f = new PowerPolicyFixture())
            {
                f.Set(powerDemote, 0, 0);
                string part = PowerSaved(powerDemote, 40, 30);
                foreach (string bad in new[] { "invalid", part + ";invalid", part + ";" + part,
                    PowerSaved(Guid.Empty, 1, 1), powerDemote.ToString("N") + "=40,not-a-number",
                    PowerSaved(new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), 0, 0),
                    "2|cccccccc-1111-2222-3333-bbbbbbbbbbbb|" + part, "3|" + f.Scheme + "|" + part })
                {
                    f.Receipt(bad);
                    Eq(false, PowerPlan.RestoreExtremeForTest(f.Scheme)); Eq(0, f.Writes); Eq(bad, f.Receipt());
                }
                f.Receipt(part); f.RemoveDc(powerDemote);
                Eq(false, PowerPlan.RestoreExtremeForTest(f.Scheme));
                Eq(true, f.Receipt().Contains(part));
            }
        }

        private static void PowerExtremeDeletedPlanClearsOnlyItsReceipt()
        {
            using (var f = new PowerPolicyFixture())
            {
                f.Receipt("2|" + f.Scheme.ToString("N") + "|" + PowerSaved(powerPromote, 60, 50));
                string before = f.Receipt();
                Eq(false, PowerPlan.ForgetDeletedExtremeForTest(Guid.NewGuid())); Eq(before, f.Receipt());
                Eq(true, PowerPlan.ForgetDeletedExtremeForTest(f.Scheme)); Eq("", f.Receipt()); Eq(0, f.Writes);
                f.Receipt(PowerSaved(powerPromote, 60, 50));
                Eq(true, PowerPlan.ForgetDeletedExtremeForTest(f.Scheme)); Eq("", f.Receipt());
            }
        }

        private static void PowerExtremePreparationWithoutReceiptWritesNothing()
        {
            using (var f = new PowerPolicyFixture())
            {
                f.Set(powerPromote, 60, 50); f.Set(powerScaling, 1, 1);
                Eq(true, PowerPlan.PrepareExtremeForTest(f.Scheme));
                Eq(false, PowerPlan.ExtremeTunePending); Eq("", f.Receipt()); Eq(0, f.Writes);
                Eq(60u, f.Get(powerPromote, true)); Eq(50u, f.Get(powerPromote, false));
                Eq(1u, f.Get(powerScaling, true)); Eq(1u, f.Get(powerScaling, false));
            }
            using (var f = new PowerPolicyFixture())
            {
                Eq(true, PowerPlan.PrepareExtremeForTest(f.Scheme));
                Eq(false, PowerPlan.ExtremeTunePending); Eq("", f.Receipt()); Eq(0, f.Writes);
            }
        }

        private static void PowerExtremePreparationRetainsFailedRestore()
        {
            using (var f = new PowerPolicyFixture())
            {
                string original = PowerSaved(powerDemote, 40, 30);
                f.Receipt("2|" + f.Scheme.ToString("N") + "|" + original);
                f.Set(powerDemote, 0, 0); f.FailDc = true;
                Eq(false, PowerPlan.PrepareExtremeForTest(f.Scheme));
                Eq(true, PowerPlan.ExtremeTunePending); Eq(true, f.Receipt().Contains(original));
                Eq(40u, f.Get(powerDemote, true)); Eq(0u, f.Get(powerDemote, false));
                f.FailDc = false;
                Eq(true, PowerPlan.PrepareExtremeForTest(f.Scheme));
                Eq(false, PowerPlan.ExtremeTunePending); Eq("", f.Receipt());
                Eq(30u, f.Get(powerDemote, false));
            }
        }

        private static void PowerEnumerateForTest(Guid[] schemes, uint terminalCode)
        {
            PowerPlan.EnumerateSchemeForTest = delegate(uint index, byte[] buffer, ref uint size)
            {
                if (index >= schemes.Length) return terminalCode;
                Array.Copy(schemes[index].ToByteArray(), buffer, 16); size = 16;
                return 0;
            };
        }

        private static void PowerExtremeOrphanRequiresCompleteEnumeration()
        {
            using (var f = new PowerPolicyFixture())
            {
                Guid owner = Guid.NewGuid();
                string receipt = "2|" + owner.ToString("N") + "|" + PowerSaved(powerPromote, 60, 50);
                f.Receipt(receipt);
                foreach (uint error in new uint[] { 5, 87, 234 })
                    foreach (Guid[] prefix in new[] { new Guid[0], new[] { f.Scheme } })
                    {
                        PowerEnumerateForTest(prefix, error);
                        Eq(false, PowerPlan.DropOrphanExtremeForTest(f.Scheme)); Eq(receipt, f.Receipt());
                    }
                PowerPlan.EnumerateSchemeForTest = delegate { throw new InvalidOperationException("Enumeration failed"); };
                Eq(false, PowerPlan.DropOrphanExtremeForTest(f.Scheme)); Eq(receipt, f.Receipt());
                var many = new Guid[129];
                for (int i = 0; i < many.Length; i++) many[i] = Guid.NewGuid();
                many[128] = owner;
                PowerEnumerateForTest(many, 259);
                Eq(false, PowerPlan.DropOrphanExtremeForTest(f.Scheme)); Eq(receipt, f.Receipt());
                PowerPlan.EnumerateSchemeForTest = delegate(uint index, byte[] buffer, ref uint size)
                { size = 8; return 0; };
                Eq(false, PowerPlan.DropOrphanExtremeForTest(f.Scheme)); Eq(receipt, f.Receipt());
                Eq(0, f.Writes);
            }
        }

        private static void PowerExtremeOrphanKeepsExistingAndUnknownOwners()
        {
            using (var f = new PowerPolicyFixture())
            {
                string part = PowerSaved(powerPromote, 60, 50);
                f.Receipt("2|" + f.Scheme.ToString("N") + "|" + part);
                string before = f.Receipt();
                Eq(false, PowerPlan.DropOrphanExtremeForTest(f.Scheme)); Eq(before, f.Receipt());
                Guid owner = Guid.NewGuid();
                f.Receipt("2|" + owner.ToString("N") + "|" + part); before = f.Receipt();
                PowerEnumerateForTest(new[] { f.Scheme, owner }, 259);
                Eq(false, PowerPlan.DropOrphanExtremeForTest(f.Scheme)); Eq(before, f.Receipt());
                PowerEnumerateForTest(new[] { f.Scheme }, 259);
                foreach (string malformed in new[] { "invalid", part, "2|invalid|" + part,
                    "3|" + owner + "|" + part, "2|" + Guid.Empty + "|" + part, "2|" + owner + "|" + part + "|extra" })
                {
                    f.Receipt(malformed);
                    Eq(false, PowerPlan.DropOrphanExtremeForTest(f.Scheme)); Eq(malformed, f.Receipt());
                }
                Eq(0, f.Writes);
            }
        }

        private static void PowerExtremeOrphanClearsOnlyAfterDurableSave()
        {
            using (var f = new PowerPolicyFixture())
            {
                string old = "2|" + Guid.NewGuid().ToString("N") + "|" + PowerSaved(powerPromote, 20, 30);
                f.Receipt(old); f.Set(powerPromote, 100, 100);
                PowerEnumerateForTest(new[] { f.Scheme }, 259);
                PowerPlan.ExtremeSaveSnapshotForTest = delegate { return false; };
                Eq(false, PowerPlan.PrepareExtremeForTest(f.Scheme));
                Eq(true, PowerPlan.ExtremeTunePending); Eq(old, f.Receipt());
                PowerPlan.ExtremeSaveSnapshotForTest = null;
                Eq(true, PowerPlan.PrepareExtremeForTest(f.Scheme));
                Eq(false, PowerPlan.ExtremeTunePending); Eq("", f.Receipt());
                Eq(100u, f.Get(powerPromote, true)); Eq(0, f.Writes);
            }
        }
    }
}
#endif
