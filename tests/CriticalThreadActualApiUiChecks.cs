#if PAVISE_SELFTEST && PAVISE_CRITICAL_THREAD_ACTUAL_UI_CHECKS
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
namespace PaviseApp
{
    // Real GameMode preference APIs + real temporary profile store.
    // No fake GameMode API implementation, active game, thread operator or Main.
    internal static class CriticalThreadActualApiUiChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks, fixtures;
        private static void Check(bool value, string text) { checks++; if (!value) throw new InvalidOperationException("FAIL actual API UI: " + text); }
        private static T Field<T>(object owner, string name)
        { return (T)(owner is PanelForm ? typeof(PanelForm) : owner.GetType()).GetField(name, Hidden).GetValue(owner); }
        private static object Call(PanelForm form, string name, params object[] args) { return typeof(PanelForm).GetMethod(name, Hidden).Invoke(form, args); }
        private static GameProfile Profile(GameMode mode, string id) { return mode.GetProfiles().Single(x => x.Id == id); }
        private static void Set(GameMode mode, string name, object value) { typeof(GameMode).GetField(name, Hidden).SetValue(mode, value); }
        private static void RejectedPauseOrigins()
        {
            // Memory-only source facts exercise the actual rejection consumer.
            // No constructor, runtime loop, native lease or process exists here.
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            var profile = new GameProfile { Id = "isolated-old-origin", Name = "Isolated origin" };
            string raw = new CriticalThreadRule(ManualCorePlan.Stamp(255, new ulong[] { 3, 12, 48, 192 }), 1).Encode();
            profile.Overrides[CriticalThreadRule.Key] = raw;
            Set(mode, "sync", new object()); Set(mode, "profiles", new System.Collections.Generic.List<GameProfile> { profile });
            Set(mode, "enabled", true); Set(mode, "active", true); Set(mode, "gameSessionStarts", 7L); Set(mode, "criticalIntentEpoch", 20L);
            Set(mode, "activeDetection", new GameDetection { Profile = profile.Clone(), RendererPid = 42, RendererCreation = 9000 });
            Guid oldToken = Guid.NewGuid();
            var view = new CriticalThreadUiState(profile.Id, "observing", "", true, sessionActive: true, canPause: true,
                sessionToken: oldToken, gameSerial: 7, intentEpoch: 20, targetPid: 42, targetBirth: 9000, ruleSignature: raw);
            Set(mode, "criticalThreadUi", view);
            Check(ReferenceEquals(mode.CriticalThreadView(profile.Id), view), "origin rejection fixture is not a current actual API snapshot");
            Check(!mode.SetCriticalThreadPaused(profile.Id, Guid.Empty, true), "missing pause token was accepted by the actual API");
            Check(!mode.SetCriticalThreadPaused(profile.Id, Guid.NewGuid(), true), "unpublished pause token was accepted by the actual API");
            Set(mode, "gameSessionStarts", 8L);
            Check(!mode.SetCriticalThreadPaused(profile.Id, oldToken, true), "old displayed token paused a newly opened same-profile session");
            Check(!Field<bool>(mode, "criticalThreadPaused") && Field<long>(mode, "criticalThreadPauseSerial") == 0
                && Field<long>(mode, "criticalIntentEpoch") == 20, "rejected origins mutated pause or revocation state");
            Check(mode.CriticalThreadView(profile.Id).State == "waiting-game", "old cached observation remained current after the game serial changed");
        }
        private static void Run(string output, int language, bool failDisk)
        {
            string folder = Path.Combine(output, "actual-api-" + language + "-" + failDisk); Directory.CreateDirectory(folder);
            string gameRoot = Path.Combine(folder, "game"); Directory.CreateDirectory(gameRoot);
            var profile = new GameProfile { Id = Guid.NewGuid().ToString("N"), Name = "UI only profile", Root = gameRoot };
            profile.Entries.Add("ui-only-game");
            var seed = new GameProfileStore(folder); Check(seed.Save(new[] { profile }), "temporary real profile seed failed");
            CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[0], 0, 0, 0, 0, false, false);
            AutoCores.SupportedForTest = true; AutoCores.AllocatedForTest = delegate { return 0; };
            PrivacyControl.ResetForTest(); PrivacyControl.AdmissionAllowed = delegate { return false; };
            PrivacyControl.QueueForTest = delegate { throw new InvalidOperationException("No privacy worker in actual preference UI check"); };
            CalibrationCoverage.BeginRun(); PanelForm.CpuNameOverride = "UI ONLY";
            EnvironmentEnhancementEligibility.SampleForTest = delegate { return new EnvironmentEnhancementEligibility.MemorySnapshot {
                Installed = 32 * EnvironmentEnhancementEligibility.GiB, Total = 31 * EnvironmentEnhancementEligibility.GiB, Available = 8 * EnvironmentEnhancementEligibility.GiB }; };
            PanelForm.WorkspaceAreaForTest = delegate { return new Rectangle(0, 0, 1280, 760); }; PanelForm.WorkspaceDpiForTest = delegate { return 96; };
            Dpi.NoFit = false; Dpi.Scale = 1; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(true);
            GameMode mode;
            using (PanelForm form = PerformanceWorkspacePageChecks.CreateFixtureForTest(folder, out mode)) {
                Check(mode.GetProfiles().Any(x => x.Id == profile.Id), "actual GameMode did not load the seeded real store");
                Check(form.CriticalThreadReadForTest == null && form.CriticalThreadSaveForTest == null && form.CriticalThreadPauseForTest == null,
                    "real API UI check unexpectedly installed action/observation fakes");
                form.Show(); Call(form, "OpenGameCoreScheduling", profile.Id); Application.DoEvents();
                Check(!Field<Toggle>(form, "criticalThreadEnabled").Checked && CriticalThreadRule.Raw(Profile(mode, profile.Id)) == null, "default preference not off");
                Check(!Field<PillButton>(form, "criticalThreadPause").Enabled
                    && !mode.SetCriticalThreadPaused(profile.Id, Guid.Empty, true), "waiting-game UI or actual API accepted a missing pause token");
                var store = Field<GameProfileStore>(mode, "profileStore");
                string storePath = Path.Combine(folder, GameProfileStore.FileName); byte[] before = File.ReadAllBytes(storePath);
                if (failDisk) store.BeforeReplaceForTest = delegate { throw new IOException("UI_ONLY_ATOMIC_SAVE_FAULT"); };
                Call(form, "SelectCriticalThreadCore", 3UL);
                Check(CriticalThreadRule.Raw(Profile(mode, profile.Id)) == null, "target draft wrote the real preference store");
                Field<PillButton>(form, "criticalThreadSave").PerformClick(); Application.DoEvents();
                if (failDisk) {
                    Check(CriticalThreadRule.Raw(Profile(mode, profile.Id)) == null && !Field<Toggle>(form, "criticalThreadEnabled").Checked,
                        "actual failed profile commit published an enabled rule");
                    Check(before.SequenceEqual(File.ReadAllBytes(storePath)), "actual failed atomic commit changed prior profile bytes");
                } else {
                    string raw = CriticalThreadRule.Raw(Profile(mode, profile.Id)); var saved = CriticalThreadRule.Parse(raw);
                    Check(saved != null && !saved.WorkerAvoidance && saved.Targets.SequenceEqual(new[] { 1UL }), "actual save/Canonical lost the default candidate rule");
                    Check(CriticalThreadRule.Raw(new GameProfileStore(folder).LoadProfiles(false).Single(x => x.Id == profile.Id)) == raw,
                        "actual saved rule did not round trip through the on-disk profile store");
                    byte[] after = File.ReadAllBytes(storePath);
                    Check(mode.SaveCriticalThreadRule(profile.Id, null, saved) != null && raw == CriticalThreadRule.Raw(Profile(mode, profile.Id))
                        && after.SequenceEqual(File.ReadAllBytes(storePath)), "stale actual CAS overwrote the current rule");
                    Field<Toggle>(form, "criticalThreadWorkerAvoid").Checked = true;
                    Check(!CriticalThreadRule.Parse(CriticalThreadRule.Raw(Profile(mode, profile.Id))).WorkerAvoidance, "worker draft auto-saved in actual backend");
                    Field<PillButton>(form, "criticalThreadSave").PerformClick(); Application.DoEvents();
                    Check(CriticalThreadRule.Parse(CriticalThreadRule.Raw(Profile(mode, profile.Id))).WorkerAvoidance
                        && mode.CriticalThreadView(profile.Id).RequestedWorkerAvoid, "saved worker flag disagrees with actual waiting-game view");
                    Field<Toggle>(form, "criticalThreadEnabled").Checked = false; Application.DoEvents();
                    Check(CriticalThreadRule.Raw(Profile(mode, profile.Id)) == null
                        && CriticalThreadRule.Raw(new GameProfileStore(folder).LoadProfiles(false).Single(x => x.Id == profile.Id)) == null,
                        "actual disable did not clear only the stored rule");
                }
                Check(!mode.Enabled && !form.UiActive, "actual preference fixture activated runtime workers"); fixtures++;
            }
        }
        [STAThread] private static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional actual API UI negative control"); return 3; }
            return PerformanceWorkspaceLayout.PrivateDesktop(delegate {
                Settings.UseTransientStoreForCurrentProcess(); Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                PanelForm.SuppressUiWorkersForTest = true; Dpi.Init(); Lang.Init();
                RejectedPauseOrigins();
                foreach (int language in new[] { 0, 1 }) foreach (bool fail in new[] { false, true }) Run(args[0], language, fail);
                Console.WriteLine("PASS critical-thread actual API UI checks=" + checks + " fixtures=" + fixtures
                    + "; real temporary profile store and GameMode APIs; no active game/native thread/guardian/benchmark execution");
            });
        }
    }
}
#endif
