#if PAVISE_SELFTEST && PAVISE_GAME_NV_PROFILE_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Product startup forbidden in game NVIDIA checks"); } }
    internal static class GameNvProfileChecks
    {
        private static string root;
        private static int checks;
        private sealed class Failure : Exception { internal Failure(string text) : base("FAIL: " + text) { } }
        private static void Check(bool value, string text) { checks++; if (!value) throw new Failure(text); }
        private static void Reject(Action action, string text)
        { bool refused = false; try { action(); } catch (Failure) { throw; } catch { refused = true; } Check(refused, text); }
        private static void Succeed(Action action, string text)
        { try { action(); } catch (Failure) { throw; } catch (Exception error) { throw new Failure(text + ": " + error.GetType().Name); } Check(true, text); }
        private sealed class Evidence : IGameNvApiEvidenceProvider
        {
            internal GameNvApi Api = GameNvApi.OpenGl;
            internal bool Fail;
            internal string Hash;
            internal bool Nvidia = true;
            internal string Physical = new string('c', 64);
            public GameNvApiEvidence Observe(string path, string imageHash) { if (Fail) throw new IOException("fake API evidence unavailable"); return new GameNvApiEvidence(Api, GameNvApiEvidenceKind.ObservedContext, Hash ?? imageHash, "fake-original-context-v1", Physical, Nvidia); }
        }
        private sealed class Row
        {
            internal string Name;
            internal uint Predefined;
            internal readonly List<GameNvApplication> Apps = new List<GameNvApplication>();
            internal readonly Dictionary<uint, uint> Values = new Dictionary<uint, uint>(), Defaults = new Dictionary<uint, uint>();
            internal Row Copy() { var r = new Row { Name = Name, Predefined = Predefined }; r.Apps.AddRange(Apps); foreach (var p in Values) r.Values.Add(p.Key, p.Value); foreach (var p in Defaults) r.Defaults.Add(p.Key, p.Value); return r; }
        }
        private sealed class Driver : IGameNvProfileWriter
        {
            internal Dictionary<string, Row> Saved = new Dictionary<string, Row>(StringComparer.Ordinal), Working;
            internal readonly Dictionary<uint, uint> Globals = new Dictionary<uint, uint> { { GameNvPrelaunchPlan.PStateId, 0 }, { GameNvPrelaunchPlan.OpenGlId, 0 } };
            internal readonly Dictionary<uint, GameNvSetting> Inherited = new Dictionary<uint, GameNvSetting>();
            internal string Store;
            internal int Mutations, AttemptedMutations, Loads, Saves;
            internal bool Recovering, FailRead, SkipPersistence;
            internal string FailSaveAfter;
            internal Action<string> AfterSave;
            internal Action<string> AfterFindProfile;
            internal Func<GameNvTargetPhase> TargetPhase;
            private string last;
            internal Driver() { Reload(); }
            public void Reload() { Loads++; Working = Saved.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.Ordinal); }
            private GameNvProfile Snapshot(Row row) { return new GameNvProfile(row.Name, row.Predefined, (uint)row.Values.Keys.Union(row.Defaults.Keys).Count(), row.Apps,
                localSettingCount: (uint)row.Values.Keys.Union(row.Defaults.Keys).Count()); }
            public GameNvProfile FindApplication(string query)
            {
                var found = Working.Values.Where(x => x.Apps.Any(a => string.Equals(a.Name, query, StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(a.Name) && string.Equals(a.Name, Path.GetFileName(query), StringComparison.OrdinalIgnoreCase))).ToArray();
                if (found.Length > 1) throw new IOException("fake ambiguous application"); return found.Length == 0 ? null : Snapshot(found[0]);
            }
            public GameNvProfile FindProfile(string name) { Row r; var result = Working.TryGetValue(name, out r) ? Snapshot(r) : null; if (AfterFindProfile != null) AfterFindProfile(name); return result; }
            public GameNvSetting Reference(uint id) { GameNvPrelaunchPlan.RequireId(id); if (FailRead) throw new IOException("fake original read failed"); return new GameNvSetting(true, Globals[id], 0, 0, 0, 0); }
            public GameNvSetting Read(string name, uint id)
            {
                GameNvPrelaunchPlan.RequireId(id); if (FailRead) throw new IOException("fake original read failed"); var r = Working[name]; uint value, predefined; bool hasDefault = r.Defaults.TryGetValue(id, out predefined);
                if (r.Values.TryGetValue(id, out value)) return new GameNvSetting(true, value, 0, 0, hasDefault ? 1u : 0u, predefined);
                if (hasDefault) return new GameNvSetting(true, predefined, 0, 1, 1, predefined);
                GameNvSetting inherited; if (Inherited.TryGetValue(id, out inherited)) return inherited;
                return new GameNvSetting(true, Globals[id], 1, 0, 0, 0);
            }
            private void Mutation(string op)
            {
                AttemptedMutations++;
                if (TargetPhase != null) Check(TargetPhase() == GameNvTargetPhase.Closed, "driver mutation requires closed exact game target");
                Mutations++; last = op;
                var candidates = Directory.GetDirectories(Store, "*", SearchOption.AllDirectories).Where(d => File.Exists(Path.Combine(d, "original.tsv")) && !File.Exists(Path.Combine(d, "terminal.tsv"))).ToArray();
                Check(candidates.Length == 1 && File.Exists(Path.Combine(candidates[0], "issued-" + op + ".tsv")), "durable exact issued phase precedes fake mutation: " + op);
            }
            public void CreateProfile(string name) { Mutation("create"); if (Working.ContainsKey(name)) throw new IOException("duplicate profile"); Working.Add(name, new Row { Name = name }); }
            public void Bind(string name, string exactPath) { Mutation("bind"); Working[name].Apps.Add(new GameNvApplication(exactPath)); }
            public void SetUserOverride(string name, uint id, uint? value)
            { GameNvPrelaunchPlan.RequireId(id); Mutation((Recovering ? "restore-" : "set-") + id.ToString("X8")); if (value.HasValue) Working[name].Values[id] = value.Value; else Working[name].Values.Remove(id); }
            public void Unbind(string name, string exactPath) { Mutation("unbind"); Working[name].Apps.RemoveAll(x => x.Name == exactPath); }
            public void DeleteEmptyProfile(string name) { Mutation("delete"); Check(Working[name].Apps.Count == 0 && Working[name].Values.Count == 0 && Working[name].Defaults.Count == 0, "only empty own profile is deleted"); Working.Remove(name); }
            public void Save()
            {
                Saves++; if (!SkipPersistence) Saved = Working.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.Ordinal);
                if (AfterSave != null) AfterSave(last);
                if (FailSaveAfter == last) { FailSaveAfter = null; throw new IOException("fake saved-but-result-uncertain: " + last); }
            }
            public void Dispose() { }
        }
        private sealed class Gate : IGameNvRuntimeAdmission
        {
            internal GameNvTargetPhase Phase = GameNvTargetPhase.Closed;
            internal bool Active = true, RecoveryAllowed = true;
            internal Driver Driver;
            internal Action<int> OnCheck;
            internal Action<int> OnRecoveryCheck;
            private int recoveryChecks;
            private int checkCount;
            public GameNvTargetPhase Recheck(GameNvPrelaunchPlan plan) { if (!Active) throw new InvalidOperationException("fake original policy changed"); if (OnCheck != null) OnCheck(++checkCount); Driver.Recovering = false; return Phase; }
            public void RequireRecovery(GameNvRecoveryIdentity original) { if (OnRecoveryCheck != null) OnRecoveryCheck(++recoveryChecks); if (!RecoveryAllowed) throw new IOException("fake recovery temporarily unavailable");
                if (Phase != GameNvTargetPhase.Closed) throw new GameNvPreparationPending(GameNvPending.Recovery);
                Check(original.ResourceBound && File.Exists(Path.Combine(original.JournalDirectory, "original.tsv"))
                    && GameNvText.Hash(File.ReadAllBytes(Path.Combine(original.JournalDirectory, "original.tsv"))) == original.OriginalHash,
                    "recovery carries bound original resource receipt");
                Check(Path.IsPathRooted(original.Executable) && GameNvText.IsHash(original.ImageHash) && GameNvText.IsHash(original.PlanHash) && original.PhysicalToken == new string('c', 64) && original.MachineHash == new string('b', 64) && original.Generation == 7, "recovery receives exact original typed plan, machine, physical and target identity"); Driver.Recovering = true; }
        }
        private sealed class Fixture
        {
            internal readonly string Root, Store, PathName, Image;
            internal readonly Driver Driver = new Driver();
            internal readonly Gate Gate;
            internal Fixture(bool existing = true, bool predefined = true)
            {
                Root = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); Store = Path.Combine(Root, "journals"); Directory.CreateDirectory(Store);
                PathName = Path.Combine(Root, "ArenaRenderer.exe"); File.WriteAllText(PathName, "own fake game image"); Image = GameNvText.Hash(File.ReadAllBytes(PathName)); Driver.Store = Store;
                Gate = new Gate { Driver = Driver };
                Driver.TargetPhase = delegate { return Gate.Phase; };
                if (existing) { var r = new Row { Name = "Vendor single application", Predefined = predefined ? 1u : 0u }; r.Apps.Add(new GameNvApplication(PathName, predefined: predefined ? 1u : 0u)); r.Defaults[GameNvPrelaunchPlan.PStateId] = 2; r.Defaults[GameNvPrelaunchPlan.OpenGlId] = 2; Driver.Saved.Add(r.Name, r); Driver.Reload(); }
            }
            internal GameNvPrelaunchPlan Plan(bool pstate = true, bool gl = true, bool ac = true, Evidence api = null, GameNvTargetPhase phase = GameNvTargetPhase.Closed)
            { return GameNvPrelaunchPlan.Build("library-game", PathName, Image, new GameNvApprovalInput(new string('a', 64), new string('b', 64), new string('c', 64), 7, true, true, pstate, gl, ac), phase, api); }
            // These old cases exercise transaction mechanisms, including values
            // which the separate product policy deliberately does not replace.
            internal GameNvProfileLease Prepare(GameNvPrelaunchPlan plan) { return GameNvProfileLease.PrepareMechanismForTest(plan, Store, Gate, Driver); }
            internal GameNvProfileLease PrepareProduct(GameNvPrelaunchPlan plan) { return GameNvProfileLease.PrepareProductPolicyForTest(plan, Store, Gate, Driver); }
            internal string PendingJournal() { return Directory.GetDirectories(Store, "*", SearchOption.AllDirectories).Single(x => File.Exists(Path.Combine(x, "original.tsv")) && !File.Exists(Path.Combine(x, "terminal.tsv"))); }
        }
        private static void PlansAndReadonly()
        {
            var f = new Fixture(); var partial = f.Plan();
            Check(partial.Members[GameNvMember.OpenGl] == GameNvPending.ApiEvidence && partial.Targets.Count == 1 && partial.Targets[GameNvPrelaunchPlan.PStateId] == 1, "unknown GL evidence leaves a separately approved PState target usable");
            Check(f.Plan(api: new Evidence()).Targets.Count == 2 && f.Plan(api: new Evidence { Api = GameNvApi.DirectX }).Targets.Count == 1 && f.Plan(api: new Evidence { Fail = true }).Targets.Count == 1, "GL suitability and provider failure affect only the GL member");
            Check(f.Plan(api: new Evidence { Hash = new string('d', 64) }).Members[GameNvMember.OpenGl] == GameNvPending.ApiEvidence, "another executable's API observation cannot qualify GL");
            Check(f.Plan(api: new Evidence { Physical = new string('d', 64) }).Members[GameNvMember.OpenGl] == GameNvPending.ApiEvidence
                && f.Plan(api: new Evidence { Nvidia = false }).Targets.Count == 1, "OpenGL on another or non-NVIDIA physical renderer does not qualify this GL member");
            var battery = f.Plan(ac: false, api: new Evidence()); Check(battery.Members[GameNvMember.PState] == GameNvPending.AcPower && battery.Targets.Count == 1 && battery.Targets.ContainsKey(GameNvPrelaunchPlan.OpenGlId), "AC gating of PState does not discard independently qualified GL");
            Check(f.Plan(phase: GameNvTargetPhase.Running).Targets.Count == 0, "already-running games remain next-launch without mutation");
            Check(GameNvProfileLease.Inspect(f.Plan(phase: GameNvTargetPhase.Running), f.Driver).State == GameNvPending.NextLaunch
                && GameNvProfileLease.Inspect(f.Plan(pstate: false), f.Driver).State == GameNvPending.ApiEvidence, "aggregate inspection preserves next-launch or API pending instead of reporting off");
            var inspected = GameNvProfileLease.Inspect(partial, f.Driver); Check(inspected.State == GameNvPending.Integration && !inspected.OwnResources && !inspected.NativeMutationAvailable && f.Driver.Mutations == 0, "existing exact single application profile is inspected without native write authority");
            f.Driver.FailRead = true; Reject(() => GameNvProfileLease.Inspect(partial, f.Driver), "read failures are not absence or a fabricated original"); Check(f.Driver.Mutations == 0, "readonly failure does not mutate driver state");
            GameNvProfileBackend.ValidateAbi(); Check(GameNvProfileBackend.FoundStatus(0, -160, "test") && !GameNvProfileBackend.FoundStatus(-160, -160, "test"), "exact documented missing status differs from successful read");
            foreach (int status in new[] { -1, -163, -166, -174 }) Reject(() => GameNvProfileBackend.FoundStatus(status, -160, "setting"), "other native errors never become missing setting: " + status);
            Reject(() => new GameNvProfileBackend(), "native default backend is fenced in isolated checks");
            Reject(() => GameNvPrelaunchPlan.RequireId(0x50166c5e), "ForceP2 is outside every automatic mutation address");
            foreach (string scope in new[] { "basename", "shared", "folder", "launcher", "command" })
            {
                var g = new Fixture(); var row = g.Driver.Saved.Values.Single(); row.Apps.Clear();
                row.Apps.Add(new GameNvApplication(scope == "basename" ? System.IO.Path.GetFileName(g.PathName) : g.PathName, launcher: scope == "launcher" ? "launcher.exe" : "", fileInFolder: scope == "folder" ? "marker.dat" : "", flags: scope == "command" ? 2u : 0u, commandLine: scope == "command" ? "--mode" : ""));
                if (scope == "shared") row.Apps.Add(new GameNvApplication(System.IO.Path.Combine(g.Root, "another.exe"))); g.Driver.Reload();
                Check(GameNvProfileLease.Inspect(g.Plan(), g.Driver).State == GameNvPending.Scope && g.Driver.Mutations == 0, "unaccepted actual driver scope stays pending: " + scope);
            }
        }
        private static void BorrowAndPartialOwnership()
        {
            foreach (bool predefined in new[] { false, true })
            {
                var f = new Fixture(predefined: predefined); var row = f.Driver.Saved.Values.Single(); row.Values[GameNvPrelaunchPlan.OpenGlId] = 7; f.Driver.Reload();
                Succeed(delegate { using (var lease = f.Prepare(f.Plan()))
                {
                    Check(lease.ConfiguredForNextLaunch && !lease.ActivationObserved && !lease.BenefitEvaluated && !lease.HasNativeMutationAuthority, "real journal/fake configuration is never reported as context adoption, benefit or native authority");
                    var current = f.Driver.Saved.Values.Single(); Check(current.Values[GameNvPrelaunchPlan.PStateId] == 1 && current.Values[GameNvPrelaunchPlan.OpenGlId] == 7, "pending GL preserves original application override while PState is prepared");
                    Reject(() => { using (var writer = new FileStream(System.IO.Path.Combine(lease.JournalDirectory, "original.tsv"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }, "actual immutable original journal remains pinned against writers");
                    Reject(() => { using (f.Prepare(f.Plan(pstate: false, api: new Evidence()))) { } }, "second member owner cannot acquire the same actual profile concurrently");
                } }, "borrowed profile restoration preserves original inheritance without a new override");
                var after = f.Driver.Saved.Values.Single(); Check(!after.Values.ContainsKey(GameNvPrelaunchPlan.PStateId) && after.Values[GameNvPrelaunchPlan.OpenGlId] == 7 && after.Predefined == (predefined ? 1u : 0u), "restore removes introduced override and preserves predefined/user profile, app and unrelated setting");
                using (f.Prepare(f.Plan(pstate: false, api: new Evidence()))) { } Check(f.Driver.Saved.Values.Single().Values[GameNvPrelaunchPlan.OpenGlId] == 7, "a later lease starts only after prior terminal and restores its own user value");
            }
            {
                var f = new Fixture(); f.Driver.Saved.Values.Single().Values[GameNvPrelaunchPlan.PStateId] = 3; f.Driver.Reload(); using (f.Prepare(f.Plan())) { }
                Check(f.Driver.Saved.Values.Single().Values[GameNvPrelaunchPlan.PStateId] == 3, "an original explicit user override is restored as an override");
            }
            {
                var f = new Fixture(false); f.Driver.Globals[GameNvPrelaunchPlan.PStateId] = 1; var info = GameNvProfileLease.Inspect(f.Plan(), f.Driver);
                Check(info.State == GameNvPending.ApiEvidence && info.Decisions[GameNvPrelaunchPlan.PStateId].State == GameNvPending.AlreadyMatches && f.Driver.Mutations == 0 && f.Driver.Saved.Count == 0, "matching inherited PState creates no redundant profile while missing GL evidence remains pending");
            }
        }
        private static void OwnResourcesAndRecovery()
        {
            {
                var f = new Fixture(); var lease = f.Prepare(f.Plan()); string journal = lease.JournalDirectory;
                lease.SimulateOwnerCrashForTest(); int before = f.Driver.Mutations;
                f.Gate.OnRecoveryCheck = delegate(int check) { if (check == 2) f.Gate.Phase = GameNvTargetPhase.Running; };
                Reject(() => GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver), "recovery rechecks exact target before each mutation");
                Check(f.Driver.Mutations == before && !File.Exists(Path.Combine(journal, "terminal.tsv")), "phase change after recovery entry did not preserve debt");
                f.Gate.OnRecoveryCheck = null; f.Gate.Phase = GameNvTargetPhase.Closed;
                GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver);
                Check(File.Exists(Path.Combine(journal, "terminal.tsv")), "closed independent recovery retry did not finish");
            }
            {
                var f = new Fixture(false); f.Driver.AfterFindProfile = delegate(string name) {
                    f.Driver.AfterFindProfile = null; var external = new Row { Name = name }; f.Driver.Saved.Add(name, external); f.Driver.Reload();
                };
                Reject(() => { using (f.Prepare(f.Plan())) { } }, "an external profile appearing after readonly inspection is not an owned create");
                Check(f.Driver.Saved.Count == 1 && f.Driver.Mutations == 0, "cleanup never deletes a profile without this lease's issued creation phase");
            }
            {
                var f = new Fixture(false); using (f.Prepare(f.Plan(api: new Evidence()))) { Check(f.Driver.Saved.Count == 1 && f.Driver.Saved.Values.Single().Values.Count == 2, "own absent profile and application are prepared via separate original resource phases"); }
                Check(f.Driver.Saved.Count == 0, "own settings are removed before exact app unbind and empty profile deletion");
            }
            {
                var f = new Fixture(); var lease = f.Prepare(f.Plan()); string journal = lease.JournalDirectory;
                byte[] original = File.ReadAllBytes(Path.Combine(journal, "original.tsv")); int mutations = f.Driver.Mutations;
                f.Gate.Phase = GameNvTargetPhase.Running;
                Reject(lease.Dispose, "running target disposal did not defer recovery");
                Check(File.ReadAllBytes(Path.Combine(journal, "original.tsv")).SequenceEqual(original) && !File.Exists(Path.Combine(journal, "terminal.tsv"))
                    && f.Driver.Mutations == mutations, "failed live lease disposal lost WAL or mutated a running target");
                f.Gate.Phase = GameNvTargetPhase.Unknown;
                Reject(() => GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver), "independent reopened recovery accepted unknown target");
                Check(File.ReadAllBytes(Path.Combine(journal, "original.tsv")).SequenceEqual(original) && f.Driver.Mutations == mutations, "unknown reopened recovery changed original or settings");
                f.Gate.Phase = GameNvTargetPhase.Closed;
                GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver);
                Check(File.Exists(Path.Combine(journal, "terminal.tsv")) && f.Driver.Saved.Values.Single().Values.Count == 0,
                    "closed reopened original recovery did not finish after failed owner disposal");
            }
            foreach (string fail in new[] { "create", "bind", "set-1057EB71", "set-20C1221E" })
            {
                var f = new Fixture(false); f.Driver.FailSaveAfter = fail; f.Gate.RecoveryAllowed = false;
                Reject(() => { using (f.Prepare(f.Plan(api: new Evidence()))) { } }, "uncertain persisted operation retains original recovery debt: " + fail);
                string journal = f.PendingJournal(); f.Gate.RecoveryAllowed = true;
                GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver);
                Check(f.Driver.Saved.Count == 0 && File.Exists(System.IO.Path.Combine(journal, "terminal.tsv")), "fresh original-phase recovery closes partial resources: " + fail);
            }
            {
                var f = new Fixture(); var lease = f.Prepare(f.Plan(api: new Evidence())); string journal = lease.JournalDirectory; lease.SimulateOwnerCrashForTest();
                Reject(() => { using (f.Prepare(f.Plan())) { } }, "unrestored original receipt cannot be treated as a new baseline");
                GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver);
                Check(f.Driver.Saved.Values.Single().Values.Count == 0, "prepared-owner crash recovery removes only introduced borrowed overrides");
            }
            {
                var f = new Fixture(false); int attemptsAtBind = 0;
                f.Driver.AfterSave = delegate(string op) { if (op == "bind") { attemptsAtBind = f.Driver.AttemptedMutations; f.Gate.Phase = GameNvTargetPhase.Running; } };
                Reject(() => { using (f.Prepare(f.Plan())) { } }, "game starting during staging prevents configured success");
                string journal = f.PendingJournal(); int mutations = f.Driver.Mutations;
                Check(f.Driver.AttemptedMutations == attemptsAtBind, "closed admission prevents even attempted native mutation after startup race");
                Check(f.Driver.Saved.Count == 1 && !File.Exists(Path.Combine(journal, "terminal.tsv")), "startup race preserves recoverable ownership while target is running");
                foreach (var phase in new[] { GameNvTargetPhase.Running, GameNvTargetPhase.Unknown }) {
                    f.Gate.Phase = phase;
                    Reject(() => GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver), "nonclosed recovery remains pending: " + phase);
                    Check(f.Driver.Mutations == mutations && !File.Exists(Path.Combine(journal, "terminal.tsv")), "pending recovery mutated or lost receipt");
                }
                f.Gate.Phase = GameNvTargetPhase.Closed; GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver);
                Check(f.Driver.Saved.Count == 0, "closed retry cleans owned resources without killing or restarting a game");
            }
            {
                var f = new Fixture(); f.Driver.SkipPersistence = true; Reject(() => { using (f.Prepare(f.Plan())) { } }, "fresh reload detects a save that did not persist override"); Check(f.Driver.Saved.Values.Single().Values.Count == 0, "persistence failure cannot produce configured success");
            }
            {
                var f = new Fixture(); var lease = f.Prepare(f.Plan()); string journal = lease.JournalDirectory; lease.SimulateOwnerCrashForTest();
                f.Driver.Saved.Values.Single().Values[GameNvPrelaunchPlan.PStateId] = 77;
                Reject(() => GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver), "external changed override cannot be overwritten as owned recovery");
                Check(f.Driver.Saved.Values.Single().Values[GameNvPrelaunchPlan.PStateId] == 77 && !File.Exists(System.IO.Path.Combine(journal, "terminal.tsv")), "external conflict remains intact with explicit original debt");
            }
            {
                var f = new Fixture(); var lease = f.Prepare(f.Plan(api: new Evidence())); string journal = lease.JournalDirectory; lease.SimulateOwnerCrashForTest();
                f.Driver.Saved.Values.Single().Values[GameNvPrelaunchPlan.PStateId] = 77;
                Reject(() => GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver), "one externally changed member keeps the journal incomplete");
                Check(f.Driver.Saved.Values.Single().Values[GameNvPrelaunchPlan.PStateId] == 77 && !f.Driver.Saved.Values.Single().Values.ContainsKey(GameNvPrelaunchPlan.OpenGlId), "recovery still attempts independent safe GL restoration after a PState conflict");
            }
            {
                var f = new Fixture(false); var lease = f.Prepare(f.Plan()); string journal = lease.JournalDirectory; lease.SimulateOwnerCrashForTest();
                f.Driver.Saved.Values.Single().Values[0x88888888] = 9;
                Reject(() => GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver), "external unrelated setting prevents own profile deletion");
                Check(f.Driver.Saved.Values.Single().Values[0x88888888] == 9 && f.Driver.Saved.Values.Single().Apps.Count == 1, "another owner's data and application association survive recovery conflict");
            }
            {
                var f = new Fixture(); var lease = f.Prepare(f.Plan()); string journal = lease.JournalDirectory; lease.SimulateOwnerCrashForTest();
                var row = f.Driver.Saved.Values.Single(); f.Driver.Saved.Remove(row.Name); row.Name = "renamed external profile"; f.Driver.Saved.Add(row.Name, row); f.Driver.Reload(); int before = f.Driver.Mutations;
                Reject(() => { using (f.Prepare(f.Plan(pstate: false, api: new Evidence()))) { } }, "profile rename cannot evade another outstanding original target journal");
                Check(f.Driver.Mutations == before && !File.Exists(System.IO.Path.Combine(journal, "terminal.tsv")), "outstanding store debt blocks a new baseline under a renamed profile");
            }
            {
                var f = new Fixture(); var lease = f.Prepare(f.Plan()); string journal = lease.JournalDirectory; lease.SimulateOwnerCrashForTest(); File.WriteAllText(System.IO.Path.Combine(journal, "terminal.tsv"), "not a terminal proof");
                Reject(() => { using (f.Prepare(f.Plan(pstate: false, api: new Evidence()))) { } }, "mere terminal filename cannot bypass original recovery debt");
            }
            {
                var f = new Fixture(); var lease = f.Prepare(f.Plan()); string journal = lease.JournalDirectory; lease.SimulateOwnerCrashForTest();
                File.AppendAllText(System.IO.Path.Combine(journal, "issued-set-1057EB71.tsv"), "tamper\n"); int before = f.Driver.Mutations;
                Reject(() => GameNvProfileLease.RecoverForTest(journal, f.Gate, f.Driver), "changed issued phase cannot authorize recovery"); Check(f.Driver.Mutations == before, "invalid phase is rejected before any recovery mutation");
            }
        }
        private static void PolicySourceCases()
        {
            uint gl = GameNvPrelaunchPlan.OpenGlId, ps = GameNvPrelaunchPlan.PStateId;
            Check(typeof(IGameNvProfileReader).IsAssignableFrom(typeof(GameNvProfileBackend)) && !typeof(IGameNvProfileWriter).IsAssignableFrom(typeof(GameNvProfileBackend)), "native backend remains readonly with no writable implementation");
            var origins = new[] { GameNvValueSource.ProfileUser, GameNvValueSource.ProfilePredefined,
                GameNvValueSource.GlobalUser, GameNvValueSource.GlobalPredefined, GameNvValueSource.BaseUser,
                GameNvValueSource.BasePredefined, GameNvValueSource.DefaultUser, GameNvValueSource.DefaultPredefined };
            for (uint location = 0; location < 4; location++)
                for (uint predefined = 0; predefined < 2; predefined++)
                    for (uint valid = 0; valid < 2; valid++)
                    {
                        var value = new GameNvSetting(true, 2, location, predefined, valid, 0);
                        var source = origins[(int)(location * 2 + predefined)];
                        Check(GameNvProfilePolicy.Source(value) == source, "current value source ignores predefined union validity: " + source + "/" + valid);
                        var expectedGl = location != 0 ? GameNvPending.Ready : predefined == 0 ? GameNvPending.UserPreference : GameNvPending.ProfileRule;
                        Check(GameNvProfilePolicy.Evaluate(gl, 1, value).State == expectedGl, "GL source-specific decision: " + source + "/" + valid);
                        Check(GameNvProfilePolicy.Evaluate(ps, 1, value).State == (source == GameNvValueSource.ProfileUser ? GameNvPending.UserPreference : GameNvPending.Ready), "PState independent of local vendor GL policy: " + source + "/" + valid);
                        Check(GameNvProfilePolicy.Evaluate(gl, 1, new GameNvSetting(true, 1, location, predefined, valid, 0)).State == GameNvPending.AlreadyMatches, "already-on preserves every current source: " + source);
                    }
            Check(GameNvProfilePolicy.Evaluate(gl, 1, null).State == GameNvPending.Observation
                && GameNvProfilePolicy.Evaluate(gl, 1, new GameNvSetting(false, 0, 0, 0, 0, 0)).Source == GameNvValueSource.Unavailable, "missing or unresolved setting remains observation pending");
            Check(GameNvProfilePolicy.Evaluate(gl, 1, new GameNvSetting(true, 3, 0, 1, 1, 3)).State == GameNvPending.Observation, "unknown local predefined GL value is not assumed off");
            Reject(() => GameNvProfilePolicy.Evaluate(gl, 2, new GameNvSetting(true, 0, 1, 0, 0, 0)), "policy supports only approved target one");
            Reject(() => GameNvProfilePolicy.Evaluate(0x50166c5e, 1, null), "policy rejects out-of-contract setting address");
            foreach (bool profilePredefined in new[] { false, true })
                foreach (uint originalGl in new uint[] { 0, 2, 1 })
                {
                    var f = new Fixture(predefined: profilePredefined); var row = f.Driver.Saved.Values.Single(); row.Defaults[gl] = originalGl;
                    var plan = f.Plan(api: new Evidence()); var info = GameNvProfileLease.Inspect(plan, f.Driver);
                    Check(info.ProductPolicyEvaluated && info.PolicyRule == GameNvProfilePolicy.Rule && !info.NativeMutationAvailable, "product inspection explicitly records pure policy without native authority");
                    Check(info.Decisions[gl].Source == GameNvValueSource.ProfilePredefined && info.Decisions[gl].State == (originalGl == 1 ? GameNvPending.AlreadyMatches : GameNvPending.ProfileRule)
                        && info.EligibleTargets.Count == 1 && info.EligibleTargets.ContainsKey(ps), "local predefined GL preserved independently of profile-level flag; PState remains eligible");
                    info.RequireCurrentProductPolicy(plan);
                    Reject(() => info.RequireCurrentProductPolicy(f.Plan(api: null)), "inspection cannot be reused for another original plan");
                    using (var lease = f.PrepareProduct(plan))
                    {
                        Check(f.Driver.Saved.Values.Single().Values.ContainsKey(ps) && !f.Driver.Saved.Values.Single().Values.ContainsKey(gl), "product fake lease changes only eligible PState, without redundant GL override");
                        Check(!File.Exists(Path.Combine(lease.JournalDirectory, "issued-set-" + gl.ToString("X8") + ".tsv")) && !lease.ActivationObserved && !lease.BenefitEvaluated, "preserved or already-on GL has no issued write or activation/benefit claim");
                    }
                    Check(f.Driver.Saved.Values.Single().Values.Count == 0 && f.Driver.Saved.Values.Single().Defaults[gl] == originalGl, "partial product lease restores PState and retains original predefined GL");
                }
            foreach (uint id in new[] { ps, gl })
            {
                var f = new Fixture(); f.Driver.Saved.Values.Single().Values[id] = 2;
                var plan = f.Plan(api: new Evidence()); var info = GameNvProfileLease.Inspect(plan, f.Driver);
                Check(info.Decisions[id].State == GameNvPending.UserPreference && info.Decisions[id].Source == GameNvValueSource.ProfileUser && !info.EligibleTargets.ContainsKey(id), "local user choice preserved even with valid predefined union: " + id);
                if (id == gl)
                {
                    using (f.PrepareProduct(plan)) { Check(f.Driver.Saved.Values.Single().Values[gl] == 2, "partial PState product lease leaves local GL user override intact"); }
                    Check(f.Driver.Saved.Values.Single().Values.Count == 1 && f.Driver.Saved.Values.Single().Values[gl] == 2, "rollback preserves user's GL override presence");
                }
                else
                {
                    Check(info.State == GameNvPending.ProfileRule && info.EligibleTargets.Count == 0, "two independent blocked members do not produce an eligible vector");
                    Reject(() => { using (f.PrepareProduct(plan)) { } }, "product lease cannot bypass blocked member decisions");
                    Check(f.Driver.Mutations == 0, "blocked vector has no fake writes");
                }
                f.Driver.Saved.Values.Single().Values[id] = 1; info = GameNvProfileLease.Inspect(plan, f.Driver);
                Check(info.Decisions[id].State == GameNvPending.AlreadyMatches && !info.EligibleTargets.ContainsKey(id), "matching user override needs no redundant write: " + id);
            }
            for (uint location = 1; location < 4; location++)
                foreach (uint predefined in new uint[] { 0, 1 })
                {
                    var f = new Fixture(predefined: true); f.Driver.Saved.Values.Single().Defaults.Remove(gl);
                    f.Driver.Inherited[gl] = new GameNvSetting(true, 2, location, predefined, 1, 2);
                    var plan = f.Plan(api: new Evidence()); var info = GameNvProfileLease.Inspect(plan, f.Driver);
                    Check(info.EligibleTargets.Count == 2 && info.Decisions[gl].Source == origins[(int)(location * 2 + predefined)], "predefined profile does not imply a local GL rule; inherited source remains distinct: " + location + "/" + predefined);
                    info.RequireCurrentProductPolicy(plan);
                }
            {
                var f = new Fixture(); f.Driver.Saved.Values.Single().Defaults.Remove(gl); f.Driver.Inherited[gl] = new GameNvSetting(false, 0, 0, 0, 0, 0);
                var plan = f.Plan(api: new Evidence()); var info = GameNvProfileLease.Inspect(plan, f.Driver);
                Check(info.Decisions[gl].State == GameNvPending.Observation && !info.EligibleTargets.ContainsKey(gl) && info.EligibleTargets.ContainsKey(ps), "unresolved GL does not become fabricated zero or block eligible PState");
                using (f.PrepareProduct(plan)) { }
                var partial = GameNvProfileLease.Inspect(f.Plan(api: new Evidence { Fail = true }), f.Driver);
                Check(partial.Decisions[gl].State == GameNvPending.ApiEvidence && partial.EligibleTargets.ContainsKey(ps), "API pending remains distinct from source observation pending");
                var originals = new Dictionary<uint, GameNvSetting>(info.Originals); var decisions = new Dictionary<uint, GameNvProfileDecision>(info.Decisions);
                var targets = new Dictionary<uint, uint>(info.EligibleTargets); targets[gl] = 1;
                var altered = new GameNvInspection(info.State, info.Profile, info.ProfileName, info.OwnResources, targets, originals, decisions, true, plan.Hash);
                Reject(() => altered.RequireCurrentProductPolicy(plan), "semantic guard rejects added eligibility for pending GL");
                var mechanism = new GameNvInspection(info.State, info.Profile, info.ProfileName, info.OwnResources, info.EligibleTargets, originals, decisions, false, plan.Hash);
                Reject(() => mechanism.RequireCurrentProductPolicy(plan), "mechanism-only observation cannot serve as product policy evaluation");
                targets = new Dictionary<uint, uint>(info.EligibleTargets); targets[0x50166c5e] = 1;
                var extra = new GameNvInspection(info.State, info.Profile, info.ProfileName, info.OwnResources, targets, originals, decisions, true, plan.Hash);
                Reject(() => extra.RequireCurrentProductPolicy(plan), "semantic guard rejects any extra mutation address");
            }
            {
                var f = new Fixture(); var plan = f.Plan(api: new Evidence());
                f.Gate.OnCheck = delegate(int count) { if (count == 2) f.Driver.Saved.Values.Single().Values[ps] = 2; };
                Reject(() => { using (f.PrepareProduct(plan)) { } }, "new local user preference after inspection prevents stale product write");
                Check(f.Driver.Mutations == 0 && f.Driver.Saved.Values.Single().Values[ps] == 2, "source drift preserves new user value before any fake write");
            }
        }
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional game NVIDIA negative control"); return 3; }
                root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                GamePerformanceTargetPolicy.MetadataForTest = delegate { return null; };
                if (args.Contains("--policy-only")) PolicySourceCases();
                else { PlansAndReadonly(); BorrowAndPartialOwnership(); OwnResourcesAndRecovery(); checks += GameNvFreshWriterChecks.Run(root); checks += GameNvDeliveryChecks.Run(root); }
                Console.WriteLine("PASS " + checks + " game NVIDIA checks; real lease/journals plus fresh-session provider seam with fake driver only, no real profile calls or game process"); return 0;
            }
            catch (Exception error) { Console.WriteLine(error); return 1; }
        }
    }
}
#endif
