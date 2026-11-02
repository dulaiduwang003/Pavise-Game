#if PAVISE_SELFTEST && PAVISE_GAME_NV_PROFILE_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static class GameNvDeliveryChecks
    {
        private static int checks;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private static void Check(bool value, string text) { checks++; if (!value) throw new InvalidOperationException("FAIL: " + text); }
        private static void Reject(Action action, string text) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, text); }
        private static void Field(string name, object value) { typeof(CalibrationRuntime).GetField(name, Static).SetValue(null, value); }
        private static CalibrationCertificate ReadCertificate(string path)
        { return (CalibrationCertificate)typeof(CalibrationRuntime).GetMethod("LoadCertificate", Static).Invoke(null, new object[] { path }); }
        private static void Certificates(string root)
        {
            Settings.UseTransientStoreForCurrentProcess(); CalibrationRuntime.UseRootForTest(Path.Combine(root, "runtime"));
            CalibrationRuntime.AcForTest = delegate { return true; };
            string file = Path.Combine(root, "claimed-result.xml");
            Func<string, string> xml = actions => "<calibration schema='1' fingerprint='" + new string('b', 64) + "' bench='" + new string('c', 64)
                + "' evidence='" + new string('d', 64) + "' report='" + root + "' selection='optimized' apis='dx11,dx12' scenarios='smart,competitive'>" + actions + "</calibration>";
            File.WriteAllText(file, xml("<action key='" + PolicyCatalog.KeyBoost + "' value='1'/><action key='" + PolicyCatalog.KeyNvMaxPerf
                + "' value='1'/><action key='" + CalibrationProductNvExperimentPlan.NvOpenGlKey + "' value='1'/>"));
            var claims = ReadCertificate(file); Check(claims != null && claims.Approved.Count == 3, "mixed certificate shape did not preserve both NV members");
            Settings.Save(CalibrationRuntime.RequestedKey, true); Field("certificate", claims); Field("activated", true); Field("busy", false); Field("powerSuspended", false);
            Field("optionsFingerprint", CalibrationRuntime.OptionFingerprintForTest()); Field("observedGeneration", Settings.MutationGeneration);
            Check(CalibrationRuntime.Effective, "negative native-authority fixture is not actually effective");
            Check(CalibrationRuntime.ApprovedValue(PolicyCatalog.KeyBoost) == "1", "owned CPU grant compatibility changed");
            Check(CalibrationRuntime.ApprovedValue(PolicyCatalog.KeyNvMaxPerf) == null && CalibrationRuntime.ApprovedValue(CalibrationProductNvExperimentPlan.NvOpenGlKey) == null,
                "loaded claim strings manufactured original NV runtime authority");
            Reject(delegate { CalibrationRuntime.AcquireOriginalGameNvGrant(); }, "raw effective certificate issued original native grant");
            foreach (string bad in new[] { "GmGlobalTimer", "ForceP2", "NvShaderCache" }) {
                File.WriteAllText(file, xml("<action key='" + bad + "' value='1'/>")); Check(ReadCertificate(file) == null, "unrelated action entered mixed certificate: " + bad);
            }
            File.WriteAllText(file, xml("<action key='" + PolicyCatalog.KeyNvMaxPerf + "' value='0'/>")); Check(ReadCertificate(file) == null, "noncanonical NV variant accepted");
            File.WriteAllText(file, xml("<action key='" + PolicyCatalog.KeyNvMaxPerf + "' value='1'/><action key='" + PolicyCatalog.KeyNvMaxPerf + "' value='1'/>"));
            Check(ReadCertificate(file) == null, "duplicate mixed result accepted");
            Reject(delegate { GameNvNativeAuthority.ForPreparation(null, 0, null, null, null); }, "isolated factory produced native preparation authority");
            Reject(delegate { GameNvNativeAuthority.ForRecovery(null, null); }, "isolated factory produced native cleanup authority");
            Reject(delegate { GameNvRendererWitness.Capture("claim", "C:/untrusted.exe", 1, 1, null, delegate { return true; }); }, "isolated route DTO produced real witness");
            CalibrationRuntime.SetManual();
        }
        private static void SelectionAndLegacy()
        {
            Settings.UseTransientStoreForCurrentProcess(); Settings.Save(PerformanceActionCatalog.OpenGl, true);
            Check(PerformanceTuning.ActionSelected(PerformanceActionCatalog.OpenGl) && PerformanceTuning.LegacyActionSelected(PerformanceActionCatalog.OpenGl), "manual OpenGL preference was lost");
            Check(NvDrsTweaks.BuildDesired(NvGamePlan.ForAutomatic(true, false, true)).Any(x => x.Key == NvDrsTweaks.KeyPState), "manual PState route was removed");
            Settings.Save(CalibrationRuntime.RequestedKey, true);
            Check(!PerformanceTuning.ActionSelected(PerformanceActionCatalog.OpenGl) && !PerformanceTuning.LegacyActionSelected(PerformanceActionCatalog.OpenGl), "raw manual preference leaked into automatic OpenGL");
            Check(!NvDrsTweaks.BuildDesired(NvGamePlan.ForAutomatic(true, false, true)).Any(x => x.Key == NvDrsTweaks.KeyPState || x.Key == NvDrsTweaks.KeyForceP2), "old automatic planner retained transferred keys");
            Check(!NvDrsTweaks.BuildDesired(new NvGamePlan { MaxPerf = true, Automatic = false }).Any(x => x.Key == NvDrsTweaks.KeyPState || x.Key == NvDrsTweaks.KeyForceP2), "manual plan DTO bypassed actual Automatic mode");
            Check(Settings.Load(PerformanceActionCatalog.OpenGl, false), "automatic isolation rewrote saved manual preference");
            string[] debt;
            Settings.SaveStr("NvDrs_orphan.exe", "pstate=absent~1"); Check(!NvDrsTweaks.TryGameNvDebt(out debt), "nonempty orphan legacy record treated as absence");
            Settings.UseTransientStoreForCurrentProcess(); Settings.Save(CalibrationRuntime.RequestedKey, true);
            Settings.SaveStr("NvDrs_old.exe", ""); Check(NvDrsTweaks.TryGameNvDebt(out debt) && debt.Length == 0, "completed empty legacy tombstone became false debt");
            var savedDriver = NvDrsTweaks.Driver;
            try {
                var driver = new LegacyDriver(); NvDrsTweaks.Driver = driver;
                Seed("pstate=absent~1;forcep2=2~0"); driver.Values[NvApi.SettingPreferredPState] = 1; driver.Values[NvApi.SettingCudaForceP2] = 0;
                GameNvLegacyDebt.RequireRetired();
                Check(NvDrsTweaks.TryGameNvDebt(out debt) && debt.Length == 0 && !driver.Values.ContainsKey(NvApi.SettingPreferredPState)
                    && driver.Values[NvApi.SettingCudaForceP2] == 2, "legacy PState/ForceP2 did not reach durable terminal before handoff");
                Seed("pstate=absent~1"); driver.Values[NvApi.SettingPreferredPState] = 77; int writes = driver.Writes;
                GameNvLegacyDebt.RequireRetired(); Check(driver.Values[NvApi.SettingPreferredPState] == 77 && driver.Writes == writes, "legacy handoff overwrote external user value");
                Seed("pstate=3~1"); driver.Values.Remove(NvApi.SettingPreferredPState); writes = driver.Writes;
                GameNvLegacyDebt.RequireRetired(); Check(!driver.Values.ContainsKey(NvApi.SettingPreferredPState) && driver.Writes == writes, "legacy handoff recreated an override removed by the user");
                Seed("pstate=absent"); driver.Values[NvApi.SettingPreferredPState] = 1;
                Reject(GameNvLegacyDebt.RequireRetired, "scalar-only ambiguous original was silently restored");
                Check(driver.Values[NvApi.SettingPreferredPState] == 1 && NvDrsTweaks.TryGameNvDebt(out debt) && debt.Length == 1, "ambiguous legacy debt was lost");
                Seed("pstate=absent~1"); driver.SaveFails = true;
                Reject(GameNvLegacyDebt.RequireRetired, "failed durable old-owner save admitted new baseline");
                Check(NvDrsTweaks.TryGameNvDebt(out debt) && debt.Length == 1, "failed old-owner save lost original debt");
            } finally { NvDrsTweaks.Driver = savedDriver; Settings.UseTransientStoreForCurrentProcess(); }
        }
        private static void Seed(string value)
        { Settings.UseTransientStoreForCurrentProcess(); Settings.Save(CalibrationRuntime.RequestedKey, true); Settings.SaveStr(NvDrsTweaks.ListKey, "old.exe"); Settings.SaveStr("NvDrs_old.exe", value); }
        private sealed class LegacyDriver : INvDrsDriver
        {
            internal Dictionary<uint, uint> Values = new Dictionary<uint, uint>(), work; internal bool SaveFails; internal int Writes;
            public bool Available { get { return true; } }
            public bool TryOpenSession(out IntPtr s) { work = new Dictionary<uint, uint>(Values); s = new IntPtr(1); return true; }
            public void CloseSession(IntPtr s) { }
            public bool SaveSession(IntPtr s) { if (SaveFails) return false; Values = new Dictionary<uint, uint>(work); return true; }
            public bool FindOrCreateAppProfile(IntPtr s, string exe, out IntPtr p) { p = new IntPtr(2); return true; }
            public int TryGetDword(IntPtr s, IntPtr p, uint id, out uint value) { return work.TryGetValue(id, out value) ? 1 : 0; }
            public bool SetDword(IntPtr s, IntPtr p, uint id, uint value, out int status) { Writes++; work[id] = value; status = 0; return true; }
            public bool DeleteSetting(IntPtr s, IntPtr p, uint id) { Writes++; work.Remove(id); return true; }
        }
        private static void NativeReadBoundaries()
        {
            int loads = 0, saves = 0, closes = 0;
            using (var api = CalibrationFeatureNvApi.ReadBoundaryForTest(delegate { loads++; }, delegate { saves++; }, delegate { closes++; })) {
                api.Load(); Check(Monitor.IsEntered(GameNvNativeGate.SyncRoot), "native Load did not hold shared transaction gate");
                ((ICalibrationFeatureNvReadBoundary)api).FinishRead(); Check(!Monitor.IsEntered(GameNvNativeGate.SyncRoot) && saves == 0, "pure read boundary retained gate or implicitly saved");
                api.Load(); api.Write(new IntPtr(2), GameNvPrelaunchPlan.PStateId, CalibrationFeatureActionValue.Dword(1));
                Reject(((ICalibrationFeatureNvReadBoundary)api).FinishRead, "unsaved staging passed a read boundary");
                Check(!Monitor.IsEntered(GameNvNativeGate.SyncRoot) && saves == 0 && loads == 3, "unsaved staging did not discard/release without Save");
                api.Load(); api.Write(new IntPtr(2), GameNvPrelaunchPlan.PStateId, CalibrationFeatureActionValue.Dword(1)); api.Save();
                Check(saves == 1 && !Monitor.IsEntered(GameNvNativeGate.SyncRoot), "native save retained a gate into measurement");
            }
            Check(closes == 1, "native test session did not close exactly once");
            using (var api = CalibrationFeatureNvApi.ReadBoundaryForTest(delegate { loads++; }, delegate { saves++; }, delegate { closes++; }))
            using (var reader = new CalibrationNvReferenceReadBackend(api)) {
                reader.Load(); var reference = reader.Reference(new[] { "nv:1057EB71", "nv:20C1221E" });
                Check(reference != null && !Monitor.IsEntered(GameNvNativeGate.SyncRoot), "parent readonly preflight retained native gate while waiting on Guard");
            }
            Check(!Monitor.IsEntered(GameNvNativeGate.SyncRoot), "native gate escaped readonly facade disposal");
        }
        private static void WorkerAdmission()
        {
            int work = 0, complete = 0; WaitCallback late = null;
            using (var idle = new ManualResetEvent(false)) {
                bool worker = true;
                Action finish = delegate { complete++; worker = false; idle.Set(); };
                Check(!GameNvWorkerQueue.TryQueue(callback => { late = callback; return false; }, () => work++, finish), "failed queue reported accepted");
                Check(!worker && idle.WaitOne(0) && complete == 1 && work == 0, "queue rejection retained worker admission or unsignaled idle");
                late(null); Check(work == 0 && complete == 1, "late rejected callback acted or double completed");
                worker = true; idle.Reset();
                Reject(() => GameNvWorkerQueue.TryQueue(callback => { late = callback; throw new InvalidOperationException("queue failure"); }, () => work++, finish), "queue exception was hidden");
                Check(!worker && idle.WaitOne(0) && complete == 2 && work == 0, "queue exception retained worker admission or unsignaled idle");
                late(null); Check(work == 0 && complete == 2, "callback delivered after queue exception acted or double completed");
                worker = true; idle.Reset();
                Check(GameNvWorkerQueue.TryQueue(callback => { late = callback; return true; }, () => work++, finish), "accepted queue reported rejected");
                Check(worker && !idle.WaitOne(0) && complete == 2, "accepted pending callback released admission early");
                late(null); late(null);
                Check(!worker && idle.WaitOne(0) && work == 1 && complete == 3, "accepted duplicate callback ran or completed twice");
                worker = true; idle.Reset();
                Reject(() => GameNvWorkerQueue.TryQueue(callback => { callback(null); return true; }, delegate { throw new InvalidOperationException("work failure"); }, finish), "inline work failure was hidden");
                Check(!worker && idle.WaitOne(0) && complete == 4, "work failure did not release exactly one admission");
                Check(!GameNvWorkerQueue.TryQueue(callback => { callback(null); callback(null); return false; }, () => work++, finish)
                    && work == 2 && complete == 5, "inline callback followed by false double completed");
                Reject(() => GameNvWorkerQueue.TryQueue(callback => { callback(null); throw new InvalidOperationException("post-delivery failure"); }, () => work++, finish), "post-delivery queue failure was hidden");
                Check(work == 3 && complete == 6, "delivered callback followed by throw double completed");
            }
        }
        private static void Ledger(string data)
        {
            Directory.CreateDirectory(data); var store = GameNvCanonicalStore.ForTest(data, "synthetic-user", new string('f', 64)); store.RequireOwner(true);
            Check(store.PendingJournals().Length == 0, "empty canonical owner store reported debt");
            Reject(store.RequireNativeOwner, "fake canonical store minted native actor authority");
            var other = GameNvCanonicalStore.ForTest(data, "other-user", new string('f', 64)); Reject(delegate { other.RequireOwner(false); }, "another actor adopted canonical originals");
            other = GameNvCanonicalStore.ForTest(data, "synthetic-user", new string('e', 64)); Reject(delegate { other.RequireOwner(false); }, "another machine adopted canonical originals");
            string token = Guid.NewGuid().ToString("N"), name = "Original profile", path = Path.Combine(data, "Original.exe");
            var plan = GameNvPrelaunchPlan.Build("library", path, new string('d', 64), new GameNvApprovalInput(new string('a', 64), new string('b', 64), new string('c', 64), 7, true, true, true, false, true), GameNvTargetPhase.Closed);
            string key = GameNvText.Hash(System.Text.Encoding.UTF8.GetBytes(name.ToUpperInvariant() + "\n1")); string journal = Path.Combine(store.Root, key, token); Directory.CreateDirectory(journal);
            var fields = new Dictionary<string, string> { { "schema", "game-nv-original-1" }, { "lease", token }, { "profile", name }, { "predefined", "1" }, { "scope", new string('e', 64) },
                { "own", "0" }, { "target", path }, { "image", plan.ExecutableHash }, { "plan", plan.Hash }, { "plan_text", plan.Text } };
            foreach (uint id in new[] { GameNvPrelaunchPlan.PStateId, GameNvPrelaunchPlan.OpenGlId }) { fields.Add("original." + id.ToString("X8"), "1,0,1,0,0,0"); fields.Add("target." + id.ToString("X8"), id == GameNvPrelaunchPlan.PStateId ? "1" : "leave"); }
            byte[] bytes = GameNvText.Encode(fields); File.WriteAllBytes(Path.Combine(journal, "original.tsv"), bytes);
            Check(store.PendingJournals().Single() == journal, "nonterminal original not discoverable from canonical root");
            var identity = store.ReadOriginal(journal); Check(identity.ResourceBound && identity.ProfileName == name && identity.OriginalHash == GameNvText.Hash(bytes), "canonical recovery lost original resource binding");
            string relocated = Path.Combine(data, key, token);
            Reject(delegate { store.RequireReceipt(identity.WithJournal(relocated, identity.OriginalHash, name, 1, identity.ProfileScopeHash, token, false)); }, "recovery accepted another root");
            File.WriteAllText(Path.Combine(journal, "terminal.tsv"), "terminal filename only"); Reject(delegate { store.PendingJournals(); }, "fabricated terminal bypassed pending ledger");
            File.WriteAllBytes(Path.Combine(journal, "terminal.tsv"), GameNvText.Encode(new Dictionary<string, string> { { "schema", "game-nv-phase-1" }, { "lease", token }, { "original", GameNvText.Hash(bytes) }, { "operation", "terminal" } }));
            Check(store.PendingJournals().Length == 0, "verified terminal did not release pending state");
            using (var held = new FileStream(Path.Combine(store.Root, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Check(!GameNvCanonicalStore.SafeToClear(data), "live writer store lock allowed reset/delete");
            File.WriteAllText(Path.Combine(journal, "foreign.txt"), "keep");
            Reject(delegate { store.PruneTerminalHistory(0); }, "history pruning deleted an unknown phase");
            Check(File.Exists(Path.Combine(journal, "original.tsv")) && File.Exists(Path.Combine(journal, "foreign.txt")), "failed retention cleanup lost original/history bytes");
            File.Delete(Path.Combine(journal, "foreign.txt")); store.PruneTerminalHistory(0);
            Check(!Directory.Exists(journal) && store.PendingJournals().Length == 0, "verified terminal retention did not release bounded history");
        }
        private static void CleanupBoundaries(string data)
        {
            Directory.CreateDirectory(data); string root = Path.Combine(data, CriticalThreadGuardian.StoreDirectory);
            Check(!LegacyPurge.HasHardThreadRecoveryDebt(data) && !Directory.Exists(root), "absent hard-thread recovery probe created a root or false debt");
            Directory.CreateDirectory(root); string original = Path.Combine(data, "Pavise.games.txt"); File.WriteAllText(original, "keep original preferences");
            Check(LegacyPurge.HasHardThreadRecoveryDebt(data) && !File.Exists(Path.Combine(root, "hard-thread-root.lock")), "unknown hard-thread root was cleared or probe created writer state");
            var reset = AppDataReset.ClearStoppedData(data);
            Check(!reset.Completed && reset.Warnings > 0 && File.ReadAllText(original) == "keep original preferences", "reset deleted preferences while original thread recovery was unknown");
            int files, warnings; string error;
            Check(!LegacyPurge.ClearRestoredData(data, out files, out warnings) && files == 0 && warnings > 0 && File.Exists(original), "restored-data cleanup ignored hard-thread debt");
            Check(!LegacyPurge.DeleteOwnedFiles(data, out files, out error) && files == 0 && File.Exists(original), "portable owned-file cleanup ignored hard-thread debt");
            Check(!LegacyPurge.TryDeleteDataTree(data, out files, out error) && files == 0 && Directory.Exists(root), "tree cleanup deleted guardian recovery sources");
            var old = LegacyPurge.RestoreHook;
            try {
                LegacyPurge.RestoreHook = () => new List<string>(); int startup = 0;
                var interactive = UninstallMode.ExecuteInteractive(data, Path.Combine(data, "Pavise.exe"), () => true, null, () => { startup++; return true; });
                Check(!interactive.Cleared && interactive.Failure != null && startup == 0 && Directory.Exists(root), "interactive uninstall removed startup or recovery data while hard-thread debt remained");
                var unattended = UninstallMode.Execute(data, data, () => true, () => { startup++; return true; });
                Check(!unattended.Cleared && unattended.Failure != null && startup == 0 && File.Exists(original), "unattended uninstall removed startup or data while hard-thread debt remained");
            } finally { LegacyPurge.RestoreHook = old; }
        }
        private static void OwnerAndPendingState(string data)
        {
            Directory.CreateDirectory(data); Settings.UseTransientStoreForCurrentProcess(); Settings.Save(CalibrationRuntime.RequestedKey, true);
            // Initialize only the fields used by the real owner/status methods;
            // the product constructor and all process/topology probes are bypassed.
            var owner = (GameMode)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameMode));
            var instance = BindingFlags.Instance | BindingFlags.NonPublic;
            Action<string, object> field = (name, value) => {
                var target = typeof(GameMode).GetField(name, instance | BindingFlags.Static);
                if (target == null) throw new MissingFieldException(typeof(GameMode).FullName, name);
                target.SetValue(owner, value);
            };
            Action publish = () => typeof(GameMode).GetMethod("PublishGameNvDeliveryPending", instance).Invoke(owner, null);
            object ownerGate = new object(), nvGate = new object(); string exe = Path.Combine(data, "Game.exe");
            field("sync", ownerGate); field("gameNvGate", nvGate); field("dataDir", data); field("enabled", true); field("gameNvEpoch", 7L); field("gameNvPending", 1);
            field("profileStore", new GameProfileStore(data)); field("profiles", new List<GameProfile> { new GameProfile { Id = "library", ExecutablePath = exe } });
            using (var idle = new ManualResetEvent(true)) {
                field("gameNvIdle", idle);
                owner.RequireGameNvTarget("library", exe, 7); Check(true, "original owner baseline did not admit its exact selected target");
                field("calibrationLeaseActive", true);
                try { Reject(() => owner.RequireGameNvTarget("library", exe, 7), "original owner admitted stale worker during calibration lease before drain"); }
                finally { field("calibrationLeaseActive", false); }
                owner.RequireGameNvTarget("library", exe, 7); Check(true, "owner lease release did not restore the same target predicate");
                Check(owner.GameNvDeliveryPending, "initial unknown root published clean");
                using (var readDone = new ManualResetEvent(false)) {
                    bool pending = false; var thread = new Thread(delegate() { pending = owner.GameNvDeliveryPending; readDone.Set(); }); thread.IsBackground = true;
                    bool immediate;
                    lock (ownerGate) lock (nvGate) { thread.Start(); immediate = readDone.WaitOne(1000); }
                    bool joined = thread.Join(1000);
                    Check(immediate && pending, "memory-only pending property waited for an owner/native gate");
                    Check(joined, "pending property reader failed to terminate");
                }
                typeof(GameMode).GetMethod("SyncGameNvDelivery", instance).Invoke(owner, null);
                Check(!owner.GameNvDeliveryPending && !Directory.Exists(Path.Combine(data, GameNvCanonicalStore.Folder)), "isolated root initialization created state or kept confirmed absence unknown");
                field("gameNvWorker", true); lock (nvGate) publish(); Check(owner.GameNvDeliveryPending, "worker admission published settled");
                field("gameNvWorker", false); lock (nvGate) publish(); Check(!owner.GameNvDeliveryPending, "finished worker with no lease/debt remained pending");
                field("gameNvRootClear", false); lock (nvGate) publish(); Check(owner.GameNvDeliveryPending, "uncleared canonical recovery published settled");
                field("gameNvRootClear", true);
                field("gameNvLease", System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameNvProfileLease)));
                lock (nvGate) publish(); Check(owner.GameNvDeliveryPending, "held prepared lease published settled");
                field("gameNvLease", null);
                field("gameNvAuthority", Empty(typeof(GameNvNativeAuthority))); lock (nvGate) publish();
                Check(owner.GameNvDeliveryPending, "remaining cleanup authority published settled");
                field("gameNvAuthority", null);
                bool stopped = (bool)typeof(GameMode).GetMethod("StopGameNvDelivery", instance).Invoke(owner, new object[] { 0 });
                Check(stopped && !owner.GameNvDeliveryPending, "closed empty-store Stop did not publish its confirmed settled state");
                File.WriteAllText(Path.Combine(data, GameNvCanonicalStore.Folder), "unknown root file");
                stopped = (bool)typeof(GameMode).GetMethod("StopGameNvDelivery", instance).Invoke(owner, new object[] { 0 });
                Check(!stopped && owner.GameNvDeliveryPending, "failed unknown-root Stop published settled");
            }
        }
        private static void RootPresence(string data)
        {
            Directory.CreateDirectory(data); string root = Path.Combine(data, GameNvCanonicalStore.Folder);
            Check(GameNvCanonicalStore.RootConfirmedAbsent(data) && GameNvCanonicalStore.SafeToClear(data) && GameNvPersistentRecovery.Restore(data), "confirmed missing NVIDIA root was not clean");
            File.WriteAllText(root, "not a directory"); var store = GameNvCanonicalStore.ForTest(data, "synthetic-user", new string('e', 64));
            Check(!GameNvCanonicalStore.RootConfirmedAbsent(data) && !GameNvCanonicalStore.SafeToClear(data) && !GameNvPersistentRecovery.Restore(data), "root-named file became absence or deletion permission");
            Reject(() => store.PendingJournals(), "root-named file disappeared from pending ledger inventory");
            Check(File.ReadAllText(root) == "not a directory", "unknown NVIDIA root file was changed");
            Check(!GameNvCanonicalStore.RootAbsentForTest(data, path => { throw new UnauthorizedAccessException(); }), "access denied became confirmed absence");
            Check(!GameNvCanonicalStore.RootAbsentForTest(data, path => { throw new IOException(); }), "IO failure became confirmed absence");
            Check(!GameNvCanonicalStore.RootAbsentForTest(data, path => FileAttributes.Directory | FileAttributes.ReparsePoint), "redirected root became confirmed absence");
            Check(!GameNvCanonicalStore.RootAbsentForTest(data, path => path == root ? FileAttributes.Normal : FileAttributes.Directory), "file-valued root became confirmed absence");
            Check(GameNvCanonicalStore.RootAbsentForTest(data, path => { if (path == root) throw new FileNotFoundException(); return FileAttributes.Directory; }), "explicit missing root with known ancestors became false debt");
            Check(!GameNvCanonicalStore.RootAbsentForTest(data, path => { if (path == root) throw new DirectoryNotFoundException(); throw new UnauthorizedAccessException(); }), "missing leaf concealed an unreadable ancestor");
        }
        private static void PinnedTextEncoding()
        {
            var type = typeof(CalibrationRuntime).GetNestedType("NvEvidencePins", BindingFlags.NonPublic);
            var parse = type.GetMethod("Fields", Static);
            byte[] plain = new System.Text.UTF8Encoding(false, true).GetBytes("schema\tx\n00001-trial\tsha\n");
            byte[] bom = new byte[] { 0xef, 0xbb, 0xbf }.Concat(plain).ToArray();
            var a = (Dictionary<string, string>)parse.Invoke(null, new object[] { plain });
            var b = (Dictionary<string, string>)parse.Invoke(null, new object[] { bom });
            Check(a.Count == 2 && b.Count == 2 && a.ContainsKey("schema") && b.ContainsKey("schema") && b.ContainsKey("00001-trial")
                && a["schema"] == "x" && b["schema"] == "x" && b["00001-trial"] == "sha", "pinned UTF8 BOM changed the original TSV field identity");
            Check(GameNvText.Hash(plain) != GameNvText.Hash(bom) && bom[0] == 0xef, "text decoding rewrote or conflated original source byte hashes");
            Reject(() => parse.Invoke(null, new object[] { new byte[] { 0xff, 0x09, 0x78 } }), "invalid UTF8 source bytes became valid pinned fields");
        }
        private sealed class ClosingPin : FileStream
        {
            internal int Failures, Attempts; internal bool Closed;
            internal ClosingPin(string file, int failures) : base(file, FileMode.Open, FileAccess.Read, FileShare.Read) { Failures = failures; }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Attempts++; if (Failures > 0) { Failures--; throw new IOException("synthetic close fault before release"); } }
                base.Dispose(disposing); Closed = true;
            }
        }
        private static void SetInstance(object target, string name, object value)
        { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        private static object Instance(object target, string name)
        { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static object Empty(Type type)
        { return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type); }
        private static void ResourceCloseRetries(string data)
        {
            Directory.CreateDirectory(data); var owned = new List<ClosingPin>();
            Func<int, ClosingPin> pin = failures => { string file = Path.Combine(data, Guid.NewGuid().ToString("N")); File.WriteAllText(file, "own temp pin"); var result = new ClosingPin(file, failures); owned.Add(result); return result; };
            var pinType = typeof(CalibrationRuntime).GetNestedType("NvEvidencePins", BindingFlags.NonPublic);
            try {
                var sources = (IDisposable)Activator.CreateInstance(pinType, true); var files = (Dictionary<string, FileStream>)Instance(sources, "files");
                var fail = pin(1); var good = pin(0); files.Add(fail.Name, fail); files.Add(good.Name, good);
                Reject(sources.Dispose, "source pins hid a close failure");
                Check(files.Count == 1 && files.Values.Single() == fail && fail.CanRead && good.Closed && good.Attempts == 1, "source close failure lost failed ownership or skipped independent close");
                Reject(() => pinType.GetMethod("RequireCurrent", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(sources, null), "partially closed source owner retained forward validity");
                sources.Dispose(); sources.Dispose();
                Check(files.Count == 0 && fail.Closed && fail.Attempts == 2 && good.Attempts == 1, "source retry did not close only the retained failure");

                var grant = (CalibrationRuntime.GameNvRuntimeGrant)Empty(typeof(CalibrationRuntime.GameNvRuntimeGrant));
                sources = (IDisposable)Activator.CreateInstance(pinType, true); files = (Dictionary<string, FileStream>)Instance(sources, "files"); fail = pin(2); files.Add(fail.Name, fail);
                SetInstance(grant, "sourceGate", new object()); SetInstance(grant, "pins", sources);
                Reject(grant.Dispose, "grant hid first source close failure");
                Check(!grant.TryDispose(0) && files.Count == 1 && !fail.Closed, "failed grant retry declared its pins drained");
                Check(grant.TryDispose(0) && fail.Closed && fail.Attempts == 3 && files.Count == 0, "disposed grant skipped remaining source close on retry");

                var image = (GameNvTargetImage)Empty(typeof(GameNvTargetImage)); fail = pin(1); SetInstance(image, "original", fail);
                var authority = (GameNvNativeAuthority)Empty(typeof(GameNvNativeAuthority)); good = pin(0);
                SetInstance(authority, "image", image); SetInstance(authority, "ownerPin", good);
                Reject(authority.Dispose, "authority hid image close failure");
                Check(!authority.ResourcesClosed && !fail.Closed && good.Closed, "authority dropped image ownership or skipped independent owner pin close");
                Reject(() => authority.RequireForward(null), "disposing authority retained forward capability");
                authority.Dispose(); authority.Dispose();
                Check(authority.ResourcesClosed && fail.Closed && fail.Attempts == 2 && good.Attempts == 1, "authority/image close retry lost original remaining resource");

                var lease = (GameNvProfileLease)Empty(typeof(GameNvProfileLease)); fail = pin(1); good = pin(0); var storeLock = pin(0);
                SetInstance(lease, "restored", true); SetInstance(lease, "originalPin", fail); SetInstance(lease, "profileLock", good); SetInstance(lease, "storeLock", storeLock);
                Reject(lease.Dispose, "lease hid source close failure");
                Check(lease.DisposalStarted && !lease.ResourcesClosed && fail.CanRead && good.Closed && storeLock.Closed, "lease dropped pending pin or skipped independent lock release");
                lease.Dispose(); lease.Dispose();
                Check(lease.ResourcesClosed && fail.Closed && fail.Attempts == 2 && good.Attempts == 1, "lease disposal retry skipped retained pin or reclosed completed resources");

                var writerType = typeof(GameNvProfileBackend).GetNestedType("GameNvFreshProfileWriter", BindingFlags.NonPublic);
                var writer = (IDisposable)Empty(writerType); fail = pin(1); SetInstance(writer, "originalPin", fail);
                Reject(writer.Dispose, "fresh writer hid its original pin close failure");
                Check(Instance(writer, "originalPin") == fail && fail.CanRead, "fresh writer lost retry ownership before original pin closed");
                writer.Dispose(); writer.Dispose();
                Check(Instance(writer, "originalPin") == null && fail.Closed && fail.Attempts == 2, "fresh writer did not retry its original pin close");

                // This is a cleanup-only memory owner with synthetic settled
                // state, not a manufactured live restoration/native grant.
                var mode = (GameMode)Empty(typeof(GameMode));
                SetInstance(mode, "sync", new object()); SetInstance(mode, "gameNvGate", new object()); SetInstance(mode, "dataDir", data);
                SetInstance(mode, "gameNvRecoveryChecked", true); SetInstance(mode, "gameNvPending", 1);
                SetInstance(mode, "automationEvidence", new Dictionary<string, string>());
                lease = (GameNvProfileLease)Empty(typeof(GameNvProfileLease)); fail = pin(1); SetInstance(lease, "restored", true); SetInstance(lease, "originalPin", fail);
                authority = (GameNvNativeAuthority)Empty(typeof(GameNvNativeAuthority)); var authorityPin = pin(1); SetInstance(authority, "ownerPin", authorityPin);
                SetInstance(mode, "gameNvLease", lease); SetInstance(mode, "gameNvAuthority", authority);
                using (var idle = new ManualResetEvent(true)) {
                    SetInstance(mode, "gameNvIdle", idle); var stop = typeof(GameMode).GetMethod("StopGameNvDelivery", BindingFlags.Instance | BindingFlags.NonPublic);
                    Check(!(bool)stop.Invoke(mode, new object[] { 0 }) && mode.GameNvDeliveryPending && Instance(mode, "gameNvLease") == lease
                        && Instance(mode, "gameNvAuthority") == authority && authorityPin.Attempts == 0, "coordinator dropped failed lease cleanup or closed its original authority too early");
                    Check(!(bool)stop.Invoke(mode, new object[] { 0 }) && mode.GameNvDeliveryPending && Instance(mode, "gameNvLease") == null
                        && Instance(mode, "gameNvAuthority") == authority, "coordinator declared settled or dropped remaining failed authority cleanup");
                    Check((bool)stop.Invoke(mode, new object[] { 0 }) && !mode.GameNvDeliveryPending && Instance(mode, "gameNvAuthority") == null
                        && fail.Closed && authorityPin.Closed, "coordinator retry did not close exact retained resources before settled");
                }
            } finally { foreach (var item in owned) if (!item.Closed) { item.Failures = 0; item.Dispose(); } }
        }
        internal static int Run(string root)
        {
            checks = 0; string data = Path.Combine(root, "delivery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(data);
            try { Certificates(data); SelectionAndLegacy(); NativeReadBoundaries(); WorkerAdmission(); PinnedTextEncoding(); ResourceCloseRetries(Path.Combine(data, "close")); Ledger(Path.Combine(data, "ledger")); CleanupBoundaries(Path.Combine(data, "cleanup")); RootPresence(Path.Combine(data, "presence")); OwnerAndPendingState(Path.Combine(data, "owner")); }
            finally { CalibrationRuntime.SetManual(); Settings.UseTransientStoreForCurrentProcess(); }
            Console.WriteLine("PASS original-grant/legacy/canonical/read-boundary assertions=" + checks + "; isolated fake callbacks and transient settings only"); return checks;
        }
    }
}
#endif
