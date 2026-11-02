using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace PaviseApp
{
    // Only hardware endpoints in output-directory source copies are replaced. Lifecycle code stays production.
    internal static class SharedManagedPowerFixture
    {
        internal static readonly Guid Base = new Guid("ac4fbc1c-a495-42a4-ab8c-4bd0eef433ee");
        internal static SharedManagedPlans Plans;
        internal static ManagedPowerHardwareFacts Facts;
        internal static int ForbiddenCalls;
        internal static string LastForbidden;
        internal static bool LabRestoreOk = true;
        internal static Exception Forbidden()
        { ForbiddenCalls++; LastForbidden = Environment.StackTrace; return new InvalidOperationException("A native power primitive was reached"); }
        internal static bool Read(Guid scheme, Guid sub, Guid setting, bool ac, out uint value)
        { uint? read = Plans.Read(scheme, sub, setting, ac); value = read.GetValueOrDefault(); return read.HasValue; }
    }

    internal sealed class SharedManagedPlans : IManagedPowerPresetPlans
    {
        internal static readonly Guid Cpu = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        internal static readonly Guid OemSub = new Guid("c455ca70-d980-449c-bfff-2f120a7f56ce");
        internal static readonly Guid OemSetting = new Guid("b57c5697-3127-407a-b070-4014d7cc959c");
        internal readonly List<Guid> Imports = new List<Guid>(), Deleted = new List<Guid>(), Activations = new List<Guid>();
        internal readonly List<string> Sources = new List<string>();
        internal readonly HashSet<Guid> Created = new HashSet<Guid>();
        internal readonly HashSet<Guid> LocalPlans = new HashSet<Guid>();
        internal readonly Dictionary<Guid, string> Names = new Dictionary<Guid, string>();
        internal readonly Dictionary<string, uint> Values = new Dictionary<string, uint>();
        internal readonly SortedDictionary<string, uint> Writes = new SortedDictionary<string, uint>();
        internal readonly Dictionary<Guid, HashSet<Guid>> Known = new Dictionary<Guid, HashSet<Guid>>();
        internal readonly HashSet<Guid> Unsupported = new HashSet<Guid>();
        internal readonly HashSet<string> Absent = new HashSet<string>();
        internal bool ImportFails, PartialImportFails, CorruptSource, LieOnWrite, RenameFails;
        internal Guid LieSetting;
        internal int DuplicateCalls;
        internal Guid RejectActivation;
        internal Guid Active = SharedManagedPowerFixture.Base;
        internal SharedManagedPlans()
        {
            foreach (ManagedPowerPresetValue value in ManagedPowerPreset.AllSettings) AddKnown(value.Sub, value.Setting);
            AddKnown(OemSub, OemSetting);
            foreach (var group in Known)
                foreach (Guid setting in group.Value)
                { Values[Key(SharedManagedPowerFixture.Base, group.Key, setting, true)] = 21;
                  Values[Key(SharedManagedPowerFixture.Base, group.Key, setting, false)] = 31; }
            Values[Key(SharedManagedPowerFixture.Base, OemSub, OemSetting, true)] = 66;
            Names[SharedManagedPowerFixture.Base] = "Original OEM plan";
        }
        private void AddKnown(Guid sub, Guid setting)
        { HashSet<Guid> settings; if (!Known.TryGetValue(sub, out settings)) Known[sub] = settings = new HashSet<Guid>(); settings.Add(setting); }
        internal static string Key(Guid scheme, Guid sub, Guid setting, bool ac)
        { return scheme.ToString("D") + "/" + sub.ToString("D") + "/" + setting.ToString("D") + "/" + (ac ? "AC" : "DC"); }
        public Guid? Current() { return Active; }
        public bool Exists(Guid scheme) { return scheme == SharedManagedPowerFixture.Base || Created.Contains(scheme) || LocalPlans.Contains(scheme); }
        public bool IsOwned(Guid scheme) { return Created.Contains(scheme); }
        public bool Duplicate(Guid source, out Guid created)
        { DuplicateCalls++; created = Guid.Empty; throw SharedManagedPowerFixture.Forbidden(); }
        public bool ImportPreset(ManagedPowerPreset preset, Guid destination)
        {
            string expected = "1|" + destination.ToString("D") + "|" + SharedManagedPowerFixture.Base.ToString("D");
            if (Settings.LoadStr("ManagedPresetCreationV1", "") != expected)
                throw new InvalidOperationException("Import began before its durable candidate/base receipt");
            if (destination == Guid.Empty || Exists(destination)) throw new InvalidOperationException("Import reused a plan GUID");
            Imports.Add(destination); Sources.Add(preset.Id);
            if (ImportFails) return false;
            Created.Add(destination); Names[destination] = preset.DisplayName;
            foreach (ManagedPowerPresetValue value in preset.Entries)
            {
                uint? ac = Index(value.Ac, value.AcRaw), dc = Index(value.Dc, value.DcRaw);
                if (ac.HasValue) Values[Key(destination, value.Sub, value.Setting, true)] = ac.Value;
                if (dc.HasValue) Values[Key(destination, value.Sub, value.Setting, false)] = dc.Value;
            }
            if (CorruptSource) Values[Key(destination, Cpu, new Guid("4d2b0152-7d5c-498b-88e2-34345392a2c5"), true)] = 9;
            return !PartialImportFails;
        }
        private static uint? Index(uint? value, string raw)
        {
            if (value.HasValue || raw == null || raw.Length != 8) return value;
            uint result = 0;
            for (int i = 0; i < 4; i++) result |= Convert.ToUInt32(raw.Substring(i * 2, 2), 16) << (8 * i);
            return result;
        }
        public bool Activate(Guid scheme)
        { if (!Exists(scheme) || scheme == RejectActivation) return false; Active = scheme; Activations.Add(scheme); return true; }
        public bool Delete(Guid scheme)
        { if (Active == scheme || scheme == SharedManagedPowerFixture.Base) return false; Deleted.Add(scheme); return Created.Remove(scheme); }
        public bool Rename(Guid scheme, string title, string note)
        { if (RenameFails) return false; Names[scheme] = title; return true; }
        public string Name(Guid scheme) { string name; return Names.TryGetValue(scheme, out name) ? name : ""; }
        public Guid[] Subgroups(Guid scheme) { return Exists(scheme) ? Known.Keys.ToArray() : null; }
        public Guid[] ProcessorSettings(Guid scheme, Guid sub)
        { HashSet<Guid> settings; return Exists(scheme) && Known.TryGetValue(sub, out settings) ? settings.Where(v => !Unsupported.Contains(v)).ToArray() : new Guid[0]; }
        public uint? Read(Guid scheme, Guid sub, Guid setting, bool ac)
        {
            HashSet<Guid> settings;
            if (!Exists(scheme) || Unsupported.Contains(setting) || !Known.TryGetValue(sub, out settings) || !settings.Contains(setting)
                || Absent.Contains(Key(scheme, sub, setting, ac))) return null;
            uint value; return Values.TryGetValue(Key(scheme, sub, setting, ac), out value) ? value : ac ? 2u : 3u;
        }
        public bool RemoveOverride(Guid scheme, Guid sub, Guid setting, bool ac)
        {
            if (!Created.Contains(scheme) || Active == scheme) throw new InvalidOperationException("Removed a non-owned or active plan value");
            string key = Key(scheme, sub, setting, ac); Values.Remove(key); Absent.Add(key); return true;
        }
        public bool CopyBaseSetting(Guid source, Guid target, Guid sub, Guid setting, bool ac)
        {
            uint? original = Read(source, sub, setting, ac), current = Read(target, sub, setting, ac);
            if (!original.HasValue) return !current.HasValue || RemoveOverride(target, sub, setting, ac);
            return current == original || Write(target, sub, setting, ac, original.Value)
                && Read(target, sub, setting, ac) == original;
        }
        public bool Write(Guid scheme, Guid sub, Guid setting, bool ac, uint value)
        {
            if (scheme == SharedManagedPowerFixture.Base || LocalPlans.Contains(scheme)) throw new InvalidOperationException("Original/local plan was modified");
            if (Unsupported.Contains(setting)) throw new InvalidOperationException("Unsupported setting was modified");
            string key = Key(scheme, sub, setting, ac); Writes[key] = value; Absent.Remove(key);
            if (!LieOnWrite && setting != LieSetting) Values[key] = value;
            return true;
        }
    }

    internal static class SharedManagedPowerChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private static int checks;
        private static readonly List<string> output = new List<string> { "source,plan,setting,value" };
        private static void Check(bool value, string message)
        { checks++; if (!value) throw new InvalidOperationException(message); }
        private static object Call(object target, string name, params object[] args)
        { try { return target.GetType().GetMethod(name, Hidden).Invoke(target, args); } catch (TargetInvocationException error) { throw error.InnerException; } }
        private static object Power(string name, params object[] args)
        { try { return typeof(PowerPlan).GetMethod(name, Static).Invoke(null, args); } catch (TargetInvocationException error) { throw error.InnerException; } }
        private static GameMode Mode(PolicySnapshot policy)
        {
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            typeof(GameMode).GetField("sessionPolicy", Hidden).SetValue(mode, policy); return mode;
        }
        private static PolicySnapshot Policy(PerformancePreset preset, bool suppress, bool off)
        {
            Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr("PerformancePreset", ((int)preset).ToString());
            Settings.Save("GmSuppress", suppress); Settings.SaveStr("PowerPlanChoice", off ? "off" : "managed"); return PolicyResolver.Global();
        }
        private static void ModeContracts()
        {
            foreach (PerformancePreset preset in new[] { PerformancePreset.Standard, PerformancePreset.Competitive })
            foreach (bool suppress in new[] { false, true })
            foreach (bool off in new[] { false, true })
            {
                PolicySnapshot policy = Policy(preset, suppress, off);
                Check(policy.ProcessorTuning == (!off && preset == PerformancePreset.Competitive), "Mode processor-tuning admission changed");
                Check(policy.WideBackgroundScope == (suppress && preset == PerformancePreset.Competitive), "the shared power policy widened Smart immediately");
                Check(policy.AdaptiveEscalation == (suppress && preset == PerformancePreset.Standard), "the shared power policy changed adaptive background admission");
                GameMode mode = Mode(policy);
                foreach (bool escalated in new[] { false, true })
                {
                    typeof(GameMode).GetField("adaptiveEscalated", Hidden).SetValue(mode, escalated);
                    bool expectedWide = suppress && (preset == PerformancePreset.Competitive || escalated);
                    Check((bool)Call(mode, "WideBackgroundScope", policy) == expectedWide, "adaptive scope was not independent from managed power");
                    foreach (bool switchedOut in new[] { false, true })
                    {
                        typeof(GameMode).GetField("smartSwitchedOut", Hidden).SetValue(mode, switchedOut);
                        Check((bool)Call(mode, "ProcessorTuningRequested", policy) == (!off && (preset == PerformancePreset.Competitive || escalated)), "Adaptive processor-tuning admission changed");
                    }
                }
            }
            foreach (string retired in new[] { "Activate", "ShouldActivate", "Supported", "Personality", "TryReadActive" })
                Check(typeof(PowerOverlay).GetMethod(retired, Static) == null,
                    "Retired overlay activation entry point remains: " + retired);
            Check(typeof(GameMode).GetMethod("MaybeActivatePowerOverlay", Hidden) == null,
                "Game mode still has an implicit performance-overlay activation path");
            foreach (string retired in new[] { "overlayAttempts", "nextOverlayAuditTicks", "overlayRaised" })
                Check(typeof(GameMode).GetField(retired, Hidden) == null, "Retired overlay activation state remains: " + retired);
            foreach (int retired in new[] { -1, 2, 3, 4, 5, 6, 999 })
                Check(!PerformancePolicy.SceneTunesProcessor((PerformancePreset)retired), "a retired/unknown mode requested managed processor tuning");

            var foreground = new SessionForegroundTracker();
            Check(foreground.Observe(true, true, 1), "foreground tracker did not enter the game");
            Check(!foreground.Observe(false, true, 2), "one foreground miss switched out");
            Check(foreground.Observe(false, true, 3) && foreground.State == SessionForegroundState.SwitchedOut, "two misses did not confirm switch-out");
            Check(foreground.Observe(true, true, 4) && foreground.State == SessionForegroundState.Game, "foreground tracker did not recover");
            var cpu = new CpuSaturation(); var guard = new AdaptiveGuard();
            Check(!cpu.Update(0.95, 1), "one high CPU sample escalated immediately");
            Check(cpu.Update(0.95, 100000001), "sustained whole-machine pressure did not saturate");
            Check(guard.Step(cpu.Saturated, 100000001) == 1, "the retained Smart guard did not escalate");
            Check(cpu.Update(0.20, 110000001), "one calm sample cleared saturation before the hold elapsed");
            Check(!cpu.Update(0.20, 160000001), "the retained CPU sensor did not clear");
            guard.Step(false, 160000001);
            Check(guard.Step(false, 1360000001) == -1, "the retained guard did not relax after 120 seconds");
        }

        private static void ResetField(string name, object value)
        { FieldInfo field = typeof(PowerPlan).GetField(name, Static); if (field != null) field.SetValue(null, value); }
        private static SharedManagedPlans Fresh(string source = "auto", bool asymmetric = false)
        {
            Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr("PowerPlanChoice", "v2:managed"); Settings.SaveStr("PowerPlanSourceV1", source);
            foreach (string name in new[] { "saved", "target", "pendingManagedCleanup" }) ResetField(name, Guid.Empty);
            foreach (string name in new[] { "active", "resolved", "targetOwned", "managedCreationBlocked", "requestedAggressive" }) ResetField(name, false);
            ResetField("tuneState", -1); ResetField("managedFailureRetryGeneration", 0); ResetField("machineSig", "TEST");
            ResetField("cpuIdleReceipt", null); ResetField("settledRestoreTarget", null);
            SharedManagedPowerFixture.LabRestoreOk = true;
            SharedManagedPowerFixture.Facts = new ManagedPowerHardwareFacts { CpuName = asymmetric ? "AMD Ryzen 9 7950X3D" : "AMD Ryzen 9 7950X", Vendor = "AuthenticAMD", AsymmetricCache = asymmetric };
            return SharedManagedPowerFixture.Plans = new SharedManagedPlans();
        }
        private static void SourceIsolationAndRestoration()
        {
            foreach (ManagedPowerPreset source in ManagedPowerPreset.AllSources.Where(s => s.Id != "excellent"))
            {
                var plans = Fresh("excellent");
                bool aggressive = source.Id != "amd-generic" && source.Id != "universal";
                if (source.Id == "amd-x3d") SharedManagedPowerFixture.Facts.CpuName = "AMD Ryzen 7 7800X3D";
                if (source.Id == "powerx-v3") SharedManagedPowerFixture.Facts = new ManagedPowerHardwareFacts { Vendor="GenuineIntel", CpuName="Intel Core i5-12400F" };
                if (source.Id == "intel-kf") SharedManagedPowerFixture.Facts = new ManagedPowerHardwareFacts { Vendor="GenuineIntel", CpuName="Intel Core i9-13900KF" };
                if (source.Id == "universal") SharedManagedPowerFixture.Facts = new ManagedPowerHardwareFacts { Vendor="GenuineIntel", CpuName="Intel Core i7-9750H" };
                Check(PowerPlan.Enforce(aggressive), "Source could not become a managed session: " + source.Id);
                Guid active = plans.Active;
                Check(active != SharedManagedPowerFixture.Base && plans.Imports.Count == 1 && plans.Sources[0] == source.Id,
                    "Automatic source was overridden by a retired preference or baseline was used as the mutable target");
                string[] record = Settings.LoadStr("ManagedPlanPolicyV3", "").Split('|');
                Check(record.Length == 6 && record[0] == "presets2" && record[4] == source.Id && record[5].Contains(source.Hash),
                    "Managed record lost original source identity");
                Check(Settings.LoadStr("ManagedPresetCreationV1", "!") == "", "Committed import retained its creation debt");
                foreach (ManagedPowerPresetValue value in source.Entries)
                    if (value.Sub == SharedManagedPlans.Cpu && value.Ac.HasValue && !plans.Unsupported.Contains(value.Setting))
                        Check(plans.Read(active, value.Sub, value.Setting, true) == value.Ac,
                            "Source AC processor policy was silently rewritten: " + source.Id + "/" + value.Setting);
                foreach (var group in plans.Known)
                    foreach (Guid setting in group.Value)
                        Check(plans.Read(active, group.Key, setting, false) == plans.Read(SharedManagedPowerFixture.Base, group.Key, setting, false),
                            "Managed battery policy did not preserve the original plan");
                Check(plans.Read(active, SharedManagedPlans.OemSub, SharedManagedPlans.OemSetting, true) == 66,
                    "OEM daily setting absent from the original source was not inherited");
                int imports = plans.Imports.Count, writes = plans.Writes.Count;
                Check(PowerPlan.EnforceSettled(aggressive) && PowerPlan.Enforce(aggressive)
                    && plans.Imports.Count == imports && plans.Writes.Count == writes, "Settled enforcement rewrote or reimported a plan");
                foreach (var value in plans.Writes) output.Add(source.Id + "," + active + "," + value.Key + "," + value.Value);
                Check(PowerPlan.Restore() && plans.Active == SharedManagedPowerFixture.Base && Settings.LoadStr("PrevPowerPlan", "!") == "",
                    "Session restoration did not return to the exact original plan");
                Check(PowerPlan.Enforce(aggressive) && plans.Active != active && plans.Imports.Count == imports + 1,
                    "A subsequent session reused a previously mutated imported plan");
                Check(PowerPlan.Restore(), "Second session did not restore");
                Check(plans.DuplicateCalls == 0, "Preset lifecycle fell back to legacy template duplication");
            }
        }
        private static void ModeAndTopologyTransitions()
        {
            var plans = Fresh();
            Check(PowerPlan.Enforce(false) && plans.Sources.Last() == "amd-generic", "AMD Smart source selection changed");
            Guid smart = plans.Active;
            Check(PowerPlan.Enforce(true) && plans.Active != smart && plans.Sources.Last() == "amd-lowlat",
                "Escalation failed to create a separate competitive source plan");
            Guid competitive = plans.Active;
            Check(PowerPlan.Enforce(false) && plans.Active != competitive && plans.Active != smart && plans.Sources.Last() == "amd-generic",
                "Relaxation reused an altered source instead of importing a new Smart plan");
            Check(PowerPlan.Restore() && plans.Active == SharedManagedPowerFixture.Base, "Mode transition lost the original return plan");

            plans = Fresh("amd-x3d", true);
            Check(PowerPlan.Enforce(true), "Asymmetric X3D source did not apply");
            foreach (string id in new[] { "0cc5b647-c1df-4637-891a-dec35c318583", "616cdaa5-695e-4545-97ad-97dc2d1bdd88", "93b8b6dc-0698-4d1c-9ee4-0644e900c85d", "7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5" })
                Check(plans.Read(plans.Active, SharedManagedPlans.Cpu, new Guid(id), true) == 21,
                    "Asymmetric cache topology lost the original parking/scheduling policy");
            Check(plans.Read(plans.Active, SharedManagedPlans.Cpu, new Guid("4d2b0152-7d5c-498b-88e2-34345392a2c5"), true) == 5000,
                "Topology adaptation silently changed an unrelated source performance interval");
            Check(PowerPlan.Restore(), "Asymmetric source did not restore");

            plans = Fresh(); SharedManagedPowerFixture.Facts = new ManagedPowerHardwareFacts { CpuName = "Unknown CPU", Vendor = "Unknown" };
            Check(PowerPlan.Enforce(true) && plans.Active == SharedManagedPowerFixture.Base && plans.Imports.Count == 0 && plans.Writes.Count == 0,
                "Unknown hardware was assigned guessed preset parameters");
            Check(PowerPlan.Restore(), "Unmodified OEM fallback did not settle");
        }
        private static void UniversalPlatformPlans()
        {
            Guid high = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            var platforms = new[] {
                new ManagedPowerHardwareFacts { CpuName="AMD Ryzen Threadripper PRO 7995WX", Vendor="AuthenticAMD" },
                new ManagedPowerHardwareFacts { CpuName="Intel Xeon W-2495X", Vendor="GenuineIntel" },
                new ManagedPowerHardwareFacts { CpuName="AMD EPYC 9654", Vendor="AuthenticAMD" },
                new ManagedPowerHardwareFacts { CpuName="Intel Core i9-13900K", Vendor="GenuineIntel", ProcessorGroupCount=2 },
                new ManagedPowerHardwareFacts { CpuName="Intel Core i9-13900K", Vendor="GenuineIntel", PackageCount=2 },
                new ManagedPowerHardwareFacts { CpuName="Workstation", Workstation=true },
                new ManagedPowerHardwareFacts { CpuName="AMD Ryzen Z1 Extreme", Vendor="AuthenticAMD" },
                new ManagedPowerHardwareFacts { CpuName="AMD Ryzen AI Z2 Extreme", Vendor="AuthenticAMD", Hybrid=true, EfficiencyClassCount=2 }
            };
            foreach (var platform in platforms)
                foreach (bool aggressive in new[] { false, true })
                    foreach (string deviceChoice in new[] { "auto", "on", "off" })
                    {
                        var plans = Fresh("amd-lowlat"); SharedManagedPowerFixture.Facts = platform;
                        Settings.SaveStr("HandheldDeviceMode", deviceChoice);
                        plans.LocalPlans.Add(high); plans.Names[high] = "Existing High performance";
                        string expected = platform.CpuName == "AMD Ryzen Z1 Extreme" && aggressive ? "amd-lowlat" : "universal";
                        Check(PowerPlan.Enforce(aggressive) && plans.Active != high && plans.Imports.Count == 1 && plans.Sources.Last() == expected,
                            "CPU-based route was overridden by a legacy device choice or an existing performance plan: " + platform.CpuName);
                        Check(Settings.LoadStr("PrevPowerPlan", "") == SharedManagedPowerFixture.Base.ToString(), "Managed route lost the exact return plan");
                        Check(PowerPlan.EnforceSettled(aggressive), "Universal/mobile plan did not settle");
                        Check(PowerPlan.Restore() && plans.Active == SharedManagedPowerFixture.Base && Settings.LoadStr("PrevPowerPlan", "!") == "",
                            "Universal/mobile plan did not restore exactly");
                    }
            var explicitPlan = Fresh(); SharedManagedPowerFixture.Facts = platforms[1]; explicitPlan.LocalPlans.Add(high);
            Settings.SaveStr("PowerPlanChoice", "v2:" + high);
            Check(PowerPlan.Enforce(true) && explicitPlan.Active == high && explicitPlan.Imports.Count == 0 && PowerPlan.Restore(),
                "Explicit existing plan lost precedence");
            explicitPlan = Fresh(); SharedManagedPowerFixture.Facts = platforms[1];
            Settings.SaveStr("PowerPlanChoice", "v2:off");
            Check(!PowerPlan.Enforce(true) && explicitPlan.Active == SharedManagedPowerFixture.Base && explicitPlan.Imports.Count == 0,
                "No-switch choice was bypassed");
        }

        private static void FailureAndLegacyRecovery()
        {
            foreach (string legacy in new[] { "3", "2223", "preset1" })
            {
                var plans = Fresh(); Guid old = new Guid("c55bfb5c-dccd-4d58-8e2c-3a2774777835");
                plans.Created.Add(old); plans.Names[old] = "由软件调度 TEST"; plans.Active = old;
                Settings.SaveStr("ManagedPlanPolicyV3", legacy + "|" + old + "|" + SharedManagedPowerFixture.Base + "|" + Guid.Empty);
                Settings.SaveStr("PrevPowerPlan", SharedManagedPowerFixture.Base.ToString("D"));
                Check(PowerPlan.Restore() && plans.Active == SharedManagedPowerFixture.Base,
                    "Legacy record no longer authorizes exact return-plan restoration: " + legacy);
            }
            var failed = Fresh(); failed.ImportFails = true;
            Check(!PowerPlan.Enforce(true) && failed.Active == SharedManagedPowerFixture.Base, "Failed import activated a partial plan");
            var partial = Fresh(); partial.PartialImportFails = true;
            Check(!PowerPlan.Enforce(true) && partial.Active == SharedManagedPowerFixture.Base,
                "Partially imported source escaped before preparation");
            Check(partial.Created.Count == 0 || Settings.LoadStr("ManagedPresetCreationV1", "").Length > 0,
                "Failed import left an untracked orphan plan");
            var corrupt = Fresh("amd-generic"); corrupt.CorruptSource = true;
            Check(!PowerPlan.Enforce(true) && corrupt.Active == SharedManagedPowerFixture.Base,
                "Source readback mismatch was accepted as a valid import");
            var readback = Fresh("amd-generic"); readback.LieOnWrite = true;
            Check(!PowerPlan.Enforce(true) && readback.Active == SharedManagedPowerFixture.Base,
                "Failed DC/daily readback was activated");
            var unsupported = Fresh("amd-generic"); Guid missing = new Guid("4d2b0152-7d5c-498b-88e2-34345392a2c5"); unsupported.Unsupported.Add(missing);
            Check(PowerPlan.Enforce(true), "A setting unavailable on this platform blocked the entire supported source");
            Check(!unsupported.Writes.Keys.Any(v => v.Contains(missing.ToString("D"))), "Unsupported source setting was synthesized");
            Check(PowerPlan.Restore(), "Supported subset could not restore");
            var sparse = Fresh("amd-generic");
            Guid sleepSub = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20"), sleepSetting = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
            sparse.Absent.Add(SharedManagedPlans.Key(SharedManagedPowerFixture.Base, SharedManagedPlans.Cpu, missing, false));
            sparse.Absent.Add(SharedManagedPlans.Key(SharedManagedPowerFixture.Base, sleepSub, sleepSetting, true));
            sparse.Absent.Add(SharedManagedPlans.Key(SharedManagedPowerFixture.Base, sleepSub, sleepSetting, false));
            Check(PowerPlan.Enforce(true) && !sparse.Read(sparse.Active, SharedManagedPlans.Cpu, missing, false).HasValue
                && !sparse.Read(sparse.Active, sleepSub, sleepSetting, true).HasValue
                && !sparse.Read(sparse.Active, sleepSub, sleepSetting, false).HasValue,
                "Source-only battery or daily overrides were not removed to preserve base absence");
            Check(sparse.Read(sparse.Active, SharedManagedPlans.Cpu, missing, true) == 5000,
                "Removing a source-only DC override erased the AC performance setting");
            Check(PowerPlan.Restore(), "Sparse baseline source did not restore");
            var blocked = Fresh(); SharedManagedPowerFixture.LabRestoreOk = false;
            Check(!PowerPlan.Enforce(true) && blocked.Imports.Count == 0 && blocked.Active == SharedManagedPowerFixture.Base,
                "Unrestored laboratory debt permitted a new plan import");
            SharedManagedPowerFixture.LabRestoreOk = true;
        }
        private static void CreationDebtAndPreferences()
        {
            var plans = Fresh(); Guid candidate = new Guid("74acbc6e-92a6-47a2-b017-47187cc80789");
            plans.Created.Add(candidate); plans.Names[candidate] = "Uncommitted imported plan"; plans.Active = candidate;
            Settings.SaveStr("ManagedPresetCreationV1", "1|" + candidate + "|" + SharedManagedPowerFixture.Base);
            Check((bool)Power("RecoverPresetCreation", plans) && plans.Active == SharedManagedPowerFixture.Base
                && plans.Deleted.Contains(candidate) && Settings.LoadStr("ManagedPresetCreationV1", "!") == "",
                "A crash between import and ownership commit left an active orphan");
            plans = Fresh("amd-generic"); Check(PowerPlan.Enforce(true), "Committed creation-debt fixture could not activate");
            candidate = plans.Active;
            Settings.SaveStr("ManagedPresetCreationV1", "1|" + candidate + "|" + SharedManagedPowerFixture.Base);
            int activations = plans.Activations.Count;
            Check((bool)Power("RecoverPresetCreation", plans) && plans.Exists(candidate) && plans.Active == candidate
                && plans.Activations.Count == activations && Settings.LoadStr("ManagedPresetCreationV1", "!") == "",
                "Committed plan was discarded while clearing a stale creation receipt");
            Check(PowerPlan.Restore(), "Committed creation-debt fixture could not restore");
            plans = Fresh(); Settings.SaveStr("ManagedPresetCreationV1", "unparseable");
            Check(!PowerPlan.Enforce(true) && plans.Imports.Count == 0 && plans.Deleted.Count == 0
                && Settings.LoadStr("ManagedPresetCreationV1", "") == "unparseable",
                "Malformed creation evidence was discarded or permitted another import");
            foreach (string retired in new[] { "intel-kf", "universal", "excellent", "not-a-source" })
            {
                plans = Fresh(retired);
                Check(PowerPlan.Enforce(true) && plans.Sources.Last() == "amd-lowlat",
                    "Retired source preference still overrides automatic routing: " + retired);
                Check(PowerPlan.Restore(), "Retired source preference fixture could not restore");
            }
            plans = Fresh("intel-kf"); PowerPlan.HealFromCrash();
            Check(Settings.LoadStr("PowerPlanSourceV1", "removed") == "removed"
                && PowerPlan.EffectivePlanId == "managed", "Retired preference cleanup lost the main power choice");
            plans = Fresh(); Settings.SaveStr("PowerPlanChoice", "v2:off");
            Check(!PowerPlan.Enforce(true) && plans.Imports.Count == 0 && plans.Writes.Count == 0,
                "No-switch selection allowed a source import or parameter mutation");
        }
        private static int Main(string[] args)
        {
            try
            {
                Logger.WritesEnabled = false; ModeContracts(); checks += ManagedPowerPresetChecks.Run();
                SourceIsolationAndRestoration(); ModeAndTopologyTransitions(); UniversalPlatformPlans(); FailureAndLegacyRecovery(); CreationDebtAndPreferences();
                Check(SharedManagedPowerFixture.ForbiddenCalls == 0, "Native power primitives were attempted: " + SharedManagedPowerFixture.LastForbidden);
                if (args.Length == 1) File.WriteAllLines(Path.Combine(args[0], "plan-output.csv"), output);
                Console.WriteLine("PASS shared managed power checks=" + checks + "; seven sources; isolated session imports and exact baseline restoration; memory backend; no Pavise startup");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
