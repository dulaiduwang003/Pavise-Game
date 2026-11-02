#if PAVISE_SELFTEST && PAVISE_GAME_NV_PROFILE_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PaviseApp
{
    internal static class GameNvFreshWriterChecks
    {
        private static int checks;
        private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("FAIL: " + message); }
        private static void Reject(Action work, string message) { bool failed = false; try { work(); } catch { failed = true; } Check(failed, message); }
        private sealed class Row
        {
            internal string Name, Path;
            internal bool ExtraApp, Complete = true;
            internal Dictionary<uint, uint> Values = new Dictionary<uint, uint>();
            internal Row Copy() { return new Row { Name = Name, Path = Path, ExtraApp = ExtraApp, Complete = Complete, Values = new Dictionary<uint, uint>(Values) }; }
            internal GameNvProfile Snapshot()
            {
                var apps = new List<GameNvApplication>(); if (Path != null) apps.Add(new GameNvApplication(Path));
                if (ExtraApp) apps.Add(new GameNvApplication(Path + ".other.exe"));
                return new GameNvProfile(Name, 0, (uint)Values.Keys.Union(new[] { GameNvPrelaunchPlan.PStateId, GameNvPrelaunchPlan.OpenGlId }).Count(),
                    apps, true, (uint)Values.Count, Complete);
            }
        }
        private sealed class Db
        {
            internal Row Saved;
            internal int Opened, Closed, Saves, Stages, GateDepth, Enters, Forward, Recovery;
            internal bool ForwardAllowed = true, RecoveryAllowed = true, SkipPersist;
            internal Action AfterStage, ConcurrentSave;
            internal readonly List<int> SaveSessions = new List<int>(), ReadSessions = new List<int>();
            internal IGameNvProfileWriter Open() { Check(GateDepth == 1, "session opened outside shared short gate"); return new Session(this, ++Opened); }
            internal IDisposable Enter() { Check(GateDepth == 0, "short native gate recursively entered"); GateDepth++; Enters++; return new Exit(this); }
            internal void RequireForward() { Forward++; if (!ForwardAllowed) throw new IOException("original source revoked"); }
            internal void RequireRecovery() { Recovery++; if (!RecoveryAllowed) throw new GameNvPreparationPending(GameNvPending.Recovery); }
            private sealed class Exit : IDisposable { private readonly Db db; internal Exit(Db value) { db = value; } public void Dispose() { db.GateDepth--; } }
        }
        private sealed class Session : IGameNvProfileWriter
        {
            private readonly Db db; private readonly int id; private Row work; private bool disposed;
            internal Session(Db database, int instance) { db = database; id = instance; }
            public void Reload() { work = db.Saved == null ? null : db.Saved.Copy(); }
            public GameNvProfile FindProfile(string name) { db.ReadSessions.Add(id); return work != null && work.Name == name ? work.Snapshot() : null; }
            public GameNvProfile FindApplication(string path) { return work != null && work.Path == path ? work.Snapshot() : null; }
            public GameNvSetting Reference(uint key) { return new GameNvSetting(true, 0, 1, 0, 0, 0); }
            public GameNvSetting Read(string name, uint key)
            { GameNvPrelaunchPlan.RequireId(key); uint value; return work.Values.TryGetValue(key, out value) ? new GameNvSetting(true, value, 0, 0, 0, 0) : Reference(key); }
            private void Stage() { db.Stages++; if (db.AfterStage != null) db.AfterStage(); }
            public void CreateProfile(string name) { if (work != null) throw new IOException("duplicate"); work = new Row { Name = name }; Stage(); }
            public void Bind(string name, string path) { work.Path = path; Stage(); }
            public void SetUserOverride(string name, uint key, uint? value) { if (value.HasValue) work.Values[key] = value.Value; else work.Values.Remove(key); Stage(); }
            public void Unbind(string name, string path) { work.Path = null; Stage(); }
            public void DeleteEmptyProfile(string name) { Check(work.Values.Count == 0 && work.Path == null, "foreign owned-profile data deleted"); work = null; Stage(); }
            public void Save()
            {
                db.Saves++; db.SaveSessions.Add(id); if (db.ConcurrentSave != null) db.ConcurrentSave();
                if (!db.SkipPersist) db.Saved = work == null ? null : work.Copy();
            }
            public void Dispose() { Check(!disposed, "native session double disposed"); disposed = true; db.Closed++; }
        }
        private static IGameNvProfileWriter Writer(Db db, string path, bool own, out GameNvRecoveryIdentity identity)
        {
            var plan = GameNvPrelaunchPlan.Build("fake-game", path, new string('d', 64),
                new GameNvApprovalInput(new string('a', 64), new string('b', 64), new string('c', 64), 7, true, true, true, false, true), GameNvTargetPhase.Closed);
            string token = Guid.NewGuid().ToString("N"), name = own ? "Pavise.Game.fake." + token : db.Saved.Name;
            string scope = own ? "absent" : db.Saved.Snapshot().ScopeHash;
            var originals = new Dictionary<uint, GameNvSetting> { { GameNvPrelaunchPlan.PStateId, new GameNvSetting(true, 0, 1, 0, 0, 0) },
                { GameNvPrelaunchPlan.OpenGlId, new GameNvSetting(true, 0, 1, 0, 0, 0) } };
            string journal = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), token); Directory.CreateDirectory(journal);
            var fields = new Dictionary<string, string> { { "schema", "game-nv-original-1" }, { "lease", token }, { "profile", name }, { "predefined", "0" },
                { "scope", scope }, { "own", own ? "1" : "0" }, { "target", path }, { "image", plan.ExecutableHash }, { "plan", plan.Hash }, { "plan_text", plan.Text } };
            foreach (var p in originals) { string key = p.Key.ToString("X8"); fields.Add("original." + key, p.Value.Text); fields.Add("target." + key, p.Key == GameNvPrelaunchPlan.PStateId ? "1" : "leave"); }
            byte[] bytes = GameNvText.Encode(fields); File.WriteAllBytes(System.IO.Path.Combine(journal, "original.tsv"), bytes);
            string hash = GameNvText.Hash(bytes);
            identity = new GameNvRecoveryIdentity(plan.Text, plan.Hash, path, plan.ExecutableHash).WithJournal(journal, hash, name, 0, scope, token, own);
            // Standalone provider tests use their own canonical issued phases;
            // the lease suite separately checks write-ahead creation ordering.
            foreach (string operation in new[] { "create", "bind", "set-1057EB71", "restore-1057EB71", "unbind", "delete" })
                File.WriteAllBytes(System.IO.Path.Combine(journal, "issued-" + operation + ".tsv"), GameNvText.Encode(new Dictionary<string, string> {
                    { "schema", "game-nv-phase-1" }, { "lease", token }, { "original", hash }, { "operation", "issued-" + operation } }));
            return GameNvProfileBackend.FreshWriterForTest(db.Open, db.Enter, db.RequireForward, db.RequireRecovery, identity,
                name, path, own, 0, scope, originals, new Dictionary<uint, uint> { { GameNvPrelaunchPlan.PStateId, 1 } });
        }
        internal static int Run(string root)
        {
            checks = 0; uint ps = GameNvPrelaunchPlan.PStateId; string path = System.IO.Path.Combine(root, "FreshRenderer.exe");
            GameNvRecoveryIdentity identity;
            var db = new Db { Saved = new Row { Name = "Exact profile", Path = path } };
            using (var writer = Writer(db, path, false, out identity)) {
                Reject(delegate { writer.DeleteEmptyProfile(db.Saved.Name); }, "borrowed profile obtained deletion authority");
                Reject(delegate { writer.SetUserOverride(db.Saved.Name, GameNvPrelaunchPlan.OpenGlId, 1); }, "unselected key obtained writable authority");
                writer.SetUserOverride(db.Saved.Name, ps, 1); Check(db.Saves == 0 && db.Stages == 0, "staging wrapper committed before explicit Save");
                int before = db.Opened; writer.Save();
                Check(db.Saves == 1 && db.Saved.Values[ps] == 1 && db.Opened >= before + 2, "commit did not use independent verification session");
                Check(db.ReadSessions.Last() != db.SaveSessions.Last() && db.Forward >= 6, "cached readback or missing phase authority checks");
                ((IGameNvProfileRecoveryContext)writer).BeginRecovery(identity); writer.SetUserOverride(db.Saved.Name, ps, null); writer.Save();
                Check(!db.Saved.Values.ContainsKey(ps) && db.Recovery >= 6, "recovery did not restore inherited provenance with its own admission");
            }
            Check(db.Opened == db.Closed && db.GateDepth == 0, "sessions/gate leaked after normal commit");

            db = new Db { Saved = new Row { Name = "Exact profile", Path = path } };
            using (var writer = Writer(db, path, false, out identity)) {
                Reject(delegate { File.AppendAllText(System.IO.Path.Combine(identity.JournalDirectory, "original.tsv"), "tamper"); }, "provider did not independently pin original bytes");
                File.Delete(System.IO.Path.Combine(identity.JournalDirectory, "issued-set-1057EB71.tsv"));
                Reject(delegate { writer.SetUserOverride(db.Saved.Name, ps, 1); }, "provider accepted a missing issued phase");
                Check(db.Opened == 0 && db.Saves == 0, "missing WAL reached driver session");
            }
            db = new Db { Saved = new Row { Name = "Exact profile", Path = path } };
            using (var writer = Writer(db, path, false, out identity)) {
                string issued = System.IO.Path.Combine(identity.JournalDirectory, "issued-set-1057EB71.tsv");
                writer.SetUserOverride(db.Saved.Name, ps, 1); db.AfterStage = delegate { File.Delete(issued); };
                Reject(writer.Save, "provider committed after issued phase disappeared");
                Check(db.Saves == 0 && db.Saved.Values.Count == 0, "WAL phase disappearance reached durable Save");
            }

            db = new Db { Saved = new Row { Name = "Exact profile", Path = path } };
            using (var writer = Writer(db, path, false, out identity)) {
                writer.SetUserOverride(db.Saved.Name, ps, 1); db.ForwardAllowed = false;
                Reject(writer.Save, "revoked original authority committed"); Check(db.Stages == 0 && db.Saves == 0, "revocation did not precede native mutation");
                db.ForwardAllowed = true; Reject(writer.Save, "failed save retained a replayable pending operation");
            }
            db = new Db { Saved = new Row { Name = "Exact profile", Path = path } };
            using (var writer = Writer(db, path, false, out identity)) {
                writer.SetUserOverride(db.Saved.Name, ps, 1); db.AfterStage = delegate { db.ForwardAllowed = false; };
                Reject(writer.Save, "revocation after native staging reached Save");
                Check(db.Stages == 1 && db.Saves == 0 && db.Saved.Values.Count == 0 && db.Opened == db.Closed, "discarded native session escaped to persistence");
            }
            db = new Db { Saved = new Row { Name = "Exact profile", Path = path } };
            using (var writer = Writer(db, path, false, out identity)) {
                writer.SetUserOverride(db.Saved.Name, ps, 1); db.Saved.Values[ps] = 77;
                Reject(writer.Save, "new local user override was overwritten"); Check(db.Saved.Values[ps] == 77 && db.Stages == 0, "fresh commit ignored source drift");
            }
            db = new Db { Saved = new Row { Name = "Exact profile", Path = path }, SkipPersist = true };
            using (var writer = Writer(db, path, false, out identity)) {
                writer.SetUserOverride(db.Saved.Name, ps, 1); Reject(writer.Save, "uncached native persistence failure reported success");
                Check(db.Saved.Values.Count == 0 && db.Opened == db.Closed, "persistence failure leaked session");
            }
            db = new Db { Saved = new Row { Name = "Exact profile", Path = path } };
            using (var writer = Writer(db, path, false, out identity)) {
                writer.SetUserOverride(db.Saved.Name, ps, 1); db.Saved.ExtraApp = true;
                Reject(writer.Save, "borrowed multi-application scope admitted"); Check(db.Stages == 0, "foreign app scope reached mutation");
            }
            db = new Db { Saved = new Row { Name = "Exact profile", Path = path, Complete = false } };
            using (var writer = Writer(db, path, false, out identity)) {
                Reject(delegate { writer.SetUserOverride(db.Saved.Name, ps, 1); }, "incomplete local settings admitted");
                Check(db.Stages == 0, "incomplete local settings reached mutation");
            }
            db = new Db();
            using (var writer = Writer(db, path, true, out identity)) {
                string name = identity.ProfileName;
                writer.CreateProfile(name); writer.Save(); writer.Bind(name, path); writer.Save(); writer.SetUserOverride(name, ps, 1); writer.Save();
                Check(db.Saved.Snapshot().SettingCount == 2 && db.Saved.Snapshot().LocalSettingCount == 1, "fake does not distinguish inherited metadata count");
                ((IGameNvProfileRecoveryContext)writer).BeginRecovery(identity);
                writer.SetUserOverride(name, ps, null); writer.Save();
                db.Saved.Values[0x88888888] = 9;
                writer.Unbind(name, path); Reject(writer.Save, "external local setting allowed owned unbind");
                Check(db.Saved.Path == path && db.Saved.Values[0x88888888] == 9, "foreign owned-profile data lost");
                db.Saved.Values.Remove(0x88888888);
                writer.Unbind(name, path); writer.Save(); writer.DeleteEmptyProfile(name); writer.Save();
                Check(db.Saved == null && db.Opened == db.Closed, "inherited-only entries prevented actual owned empty deletion");
            }
            db = new Db { Saved = new Row { Name = "Exact profile", Path = path } };
            using (var writer = Writer(db, path, false, out identity)) {
                writer.SetUserOverride(db.Saved.Name, ps, 1); writer.Save(); ((IGameNvProfileRecoveryContext)writer).BeginRecovery(identity);
                writer.SetUserOverride(db.Saved.Name, ps, null); db.AfterStage = delegate { db.RecoveryAllowed = false; };
                int saves = db.Saves; Reject(writer.Save, "running/unknown recovery phase reached Save");
                Check(db.Saves == saves && db.Saved.Values[ps] == 1, "blocked recovery lost owned override before retry");
            }
            // Explicit counterexample: another tool ignores our gate. DRS saves
            // its full loaded session; no test should claim this is atomic CAS.
            db = new Db { Saved = new Row { Name = "Exact profile", Path = path } };
            using (var writer = Writer(db, path, false, out identity)) {
                writer.SetUserOverride(db.Saved.Name, ps, 1); db.ConcurrentSave = delegate { db.Saved.Values[0x88888888] = 9; };
                writer.Save(); Check(!db.Saved.Values.ContainsKey(0x88888888), "no-merge counterexample unexpectedly claims external CAS");
            }
            Reject(delegate { GameNvProfileLease.PrepareNative(null, null); }, "production prepare accepted no original authority");
            Check(!typeof(IGameNvProfileWriter).IsAssignableFrom(typeof(GameNvProfileBackend)), "reader acquired default native write authority");
            Console.WriteLine("PASS fresh-session native-provider seam assertions=" + checks + "; fake only, includes no-merge counterexample");
            return checks;
        }
    }
}
#endif
