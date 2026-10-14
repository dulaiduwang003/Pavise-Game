#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunAutomaticFullscreenOptimizationTests()
        {
            FsoDefaultOffAndLegacyRecovery();
            FsoOptInPersistenceAndModeIndependence();
            FsoRendererOptInAndRemoval();
            FsoRetargetRestoresObsoletePaths();
            FsoOptInApplyRetry();
            FsoOwnedRestoreRetry();
            FsoProfileSaveFailureIsTransactional();
            Settings.UseTransientStoreForCurrentProcess();
        }

        private static void FsoDefaultOffAndLegacyRecovery()
        {
            using (var f = new AutomaticFsoFixture())
            {
                string userSetting = "~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE";
                f.Layers[f.Second] = userSetting;
                f.Layers[f.First] = "~ RUNASADMIN HIGHDPIAWARE";
                foreach (GameProfile p in f.Mode.GetProfiles())
                    Eq(false, p.DisableFullscreenOptimizations);
                Settings.Save(GameMode.AutomaticFsoKey, true);
                f.Mode.SyncAutomaticFullscreenOptimizations();
                Eq(false, FsoTweak.IsDisabledForExe(f.First));
                Eq(userSetting, f.Layers[f.Second]);
                Eq(0, f.Writes);

                Eq(true, FsoTweak.SetForExe(f.First, true));
                Eq(1, FsoTweak.TrackedExes().Length);
                f.SetField("enabled", false);
                Settings.Save(AutomaticPolicy.VersionKey, false);
                f.Mode.SyncAutomaticFullscreenOptimizations(nowTicks: DateTime.UtcNow.Ticks + TimeSpan.TicksPerMinute * 5);
                Eq("~ RUNASADMIN HIGHDPIAWARE", f.Layers[f.First]);
                Eq(userSetting, f.Layers[f.Second]);
                Eq(0, FsoTweak.TrackedExes().Length);
                Eq(2, f.Writes);
                GameProfile userProfile = f.Mode.GetProfiles()[1];
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(userProfile.Id, true));
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(userProfile.Id, false));
                Eq(userSetting, f.Layers[f.Second]);
                Eq(0, FsoTweak.TrackedExes().Length);
                Eq(2, f.Writes);
            }
        }

        private static void FsoOptInPersistenceAndModeIndependence()
        {
            using (var f = new AutomaticFsoFixture(true))
            {
                string userSetting = "~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE";
                f.Layers[f.Second] = userSetting;
                f.Layers[f.First] = "~ RUNASADMIN HIGHDPIAWARE";
                GameProfile profile = f.Mode.GetProfiles()[0];
                Eq(false, f.Mode.SetProfileFullscreenOptimizations("missing-profile", true));
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(profile.Id, true));
                Eq(true, f.Current(profile.Id).DisableFullscreenOptimizations);
                Eq(true, new GameProfileStore(f.DirectoryPath).LoadProfiles()[0].DisableFullscreenOptimizations);
                Eq(true, FsoTweak.IsDisabledForExe(f.First));
                Eq(true, FsoTweak.IsDisabledForExe(f.Learned));
                Eq(userSetting, f.Layers[f.Second]);
                Eq(2, FsoTweak.TrackedExes().Length);
                Eq("fso-configured", f.Mode.AutomaticExecutionState(GameMode.AutomaticFsoKey));
                int writes = f.Writes;
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(profile.Id, true));
                Eq(writes, f.Writes);
                GameProfile view = f.Current(profile.Id);
                view.Overrides.Clear();
                Eq(true, f.Current(profile.Id).DisableFullscreenOptimizations);
                foreach (string mode in new[] { "0", "1", "6", "4" })
                {
                    Settings.SaveStr(PolicyCatalog.KeyPreset, mode);
                    f.Mode.SyncAutomaticFullscreenOptimizations(true, DateTime.UtcNow.Ticks + TimeSpan.TicksPerMinute * 5);
                    Eq(true, FsoTweak.IsDisabledForExe(f.First));
                    Eq(true, FsoTweak.IsDisabledForExe(f.Learned));
                    Eq(writes, f.Writes);
                }
                typeof(GameMode).GetMethod("BeginAutomationEvidence", FsoHidden).Invoke(f.Mode, null);
                Eq("fso-configured", f.Mode.AutomaticExecutionState(GameMode.AutomaticFsoKey));
                f.SetField("enabled", false);
                f.Mode.SyncAutomaticFullscreenOptimizations(nowTicks: DateTime.UtcNow.Ticks + TimeSpan.TicksPerMinute * 10);
                Eq(writes, f.Writes);
                Eq(true, FsoTweak.IsDisabledForExe(f.First));
                Eq(true, f.Mode.Stop());
                Eq(true, FsoTweak.IsDisabledForExe(f.First));
                var reloaded = new GameMode(f.DirectoryPath, new SuppressionCore());
                try
                {
                    Eq(true, reloaded.GetProfiles()[0].DisableFullscreenOptimizations);
                    Eq(true, reloaded.SetProfileFullscreenOptimizations(profile.Id, false));
                    Eq(false, reloaded.GetProfiles()[0].DisableFullscreenOptimizations);
                    Eq(false, FsoTweak.IsDisabledForExe(f.First));
                    Eq(false, FsoTweak.IsDisabledForExe(f.Learned));
                }
                finally { Eq(true, reloaded.Stop()); }
                Eq(userSetting, f.Layers[f.Second]);
                Eq("~ RUNASADMIN HIGHDPIAWARE", f.Layers[f.First]);
                Eq(0, FsoTweak.TrackedExes().Length);
                Eq(false, new GameProfileStore(f.DirectoryPath).LoadProfiles()[0].DisableFullscreenOptimizations);
            }
        }

        private static void FsoRendererOptInAndRemoval()
        {
            using (var f = new AutomaticFsoFixture())
            {
                string added = f.NewExecutable("NewGame");
                Eq(true, f.Mode.AddGameExecutable("New game", added));
                f.Mode.SyncAutomaticFullscreenOptimizations();
                Eq(false, FsoTweak.IsDisabledForExe(added));
                string renderer = f.NewExecutable("GameRenderer");
                GameProfile profile = f.Mode.GetProfiles()[0];
                var detection = new GameDetection { Profile = profile, RendererPath = renderer,
                    RendererCandidateSelected = true, RequiresGpuConfirm = true };
                f.SetField("activeDetection", detection);
                long now = DateTime.UtcNow.Ticks + TimeSpan.TicksPerMinute * 5;
                detection.RequiresGpuConfirm = false;
                f.Mode.SyncAutomaticFullscreenOptimizations(true, now);
                Eq(false, f.Layers.ContainsKey(renderer));
                detection.RequiresGpuConfirm = true;
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(profile.Id, true));
                Eq(true, FsoTweak.IsDisabledForExe(f.First));
                f.Mode.SyncAutomaticFullscreenOptimizations(true, now + 1);
                Eq(false, f.Layers.ContainsKey(renderer));
                detection.RequiresGpuConfirm = false; detection.RendererSafetyOnly = true;
                f.Mode.SyncAutomaticFullscreenOptimizations(true, now + 2);
                Eq(false, f.Layers.ContainsKey(renderer));
                detection.RendererSafetyOnly = false;
                f.Mode.SyncAutomaticFullscreenOptimizations(true, now + 3);
                Eq(true, FsoTweak.IsDisabledForExe(renderer));
                Eq(false, f.Layers.ContainsKey(f.NewExecutable("UnrelatedApp")));
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(profile.Id, false));
                Eq(false, FsoTweak.IsDisabledForExe(f.First));
                Eq(false, FsoTweak.IsDisabledForExe(renderer));
                Eq(0, FsoTweak.TrackedExes().Length);
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(profile.Id, true));
                Eq(true, FsoTweak.IsDisabledForExe(renderer));
                f.Mode.RemoveProfile(profile.Id);
                Eq(false, FsoTweak.IsDisabledForExe(f.First));
                Eq(false, FsoTweak.IsDisabledForExe(renderer));
                Eq(0, FsoTweak.TrackedExes().Length);
                f.Mode.SyncAutomaticFullscreenOptimizations(true, now + TimeSpan.TicksPerMinute * 5);
                Eq(false, FsoTweak.IsDisabledForExe(renderer));
                Eq(false, FsoTweak.IsDisabledForExe(added));
            }
        }

        private static void FsoOptInApplyRetry()
        {
            using (var f = new AutomaticFsoFixture(false, true))
            {
                long now = DateTime.UtcNow.Ticks;
                f.DeniedPath = f.First;
                f.Mode.SyncAutomaticFullscreenOptimizations(nowTicks: now);
                Eq("failed", f.Mode.AutomaticExecutionState(GameMode.AutomaticFsoKey));
                Eq(true, FsoTweak.IsDisabledForExe(f.Second));
                int writes = f.Writes;
                for (int i = 1; i < 30; i++)
                    f.Mode.SyncAutomaticFullscreenOptimizations(true, now + TimeSpan.TicksPerSecond * i);
                Eq(writes, f.Writes);
                f.DeniedPath = null;
                f.Mode.SyncAutomaticFullscreenOptimizations(nowTicks: now + TimeSpan.TicksPerMinute * 10);
                Eq(true, FsoTweak.IsDisabledForExe(f.First));
                Eq("fso-configured", f.Mode.AutomaticExecutionState(GameMode.AutomaticFsoKey));
            }
            using (var f = new AutomaticFsoFixture())
            {
                GameProfile p = f.Mode.GetProfiles()[0];
                f.DenyLedger = true;
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(p.Id, true));
                Eq(true, f.Current(p.Id).DisableFullscreenOptimizations);
                Eq(true, new GameProfileStore(f.DirectoryPath).LoadProfiles()[0].DisableFullscreenOptimizations);
                Eq(0, f.Writes);
                Eq(0, FsoTweak.TrackedExes().Length);
                Eq("failed", f.Mode.AutomaticExecutionState(GameMode.AutomaticFsoKey));
                f.DenyLedger = false;
                f.Mode.SyncAutomaticFullscreenOptimizations(nowTicks: DateTime.UtcNow.Ticks + TimeSpan.TicksPerMinute * 10);
                Eq(true, FsoTweak.IsDisabledForExe(f.First));
            }
        }

        private static void FsoRetargetRestoresObsoletePaths()
        {
            using (var f = new AutomaticFsoFixture(true))
            {
                f.Layers[f.First] = "~ RUNASADMIN HIGHDPIAWARE";
                GameProfile profile = f.Mode.GetProfiles()[0];
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(profile.Id, true));
                Eq(2, FsoTweak.TrackedExes().Length);
                string replacement = f.NewExecutable("ReplacementRenderer");
                var hit = new GameDetection
                {
                    Profile = f.Current(profile.Id), RendererPath = replacement,
                    RendererName = "ReplacementRenderer", RendererPid = 7101,
                    RendererCreation = 123456789, RendererCandidateSelected = true,
                    RendererLearnable = true
                };
                Eq(true, f.Mode.TryLearnConfirmedRenderer(hit));
                f.Mode.SyncAutomaticFullscreenOptimizations(true);
                Eq(replacement, f.Current(profile.Id).ExecutablePath);
                Eq(true, f.Current(profile.Id).DisableFullscreenOptimizations);
                Eq(true, FsoTweak.IsDisabledForExe(replacement));
                Eq(false, FsoTweak.IsDisabledForExe(f.First));
                Eq(false, FsoTweak.IsDisabledForExe(f.Learned));
                Eq("~ RUNASADMIN HIGHDPIAWARE", f.Layers[f.First]);
                Eq(1, FsoTweak.TrackedExes().Length);
                Eq(replacement, FsoTweak.TrackedExes()[0]);
                Eq(true, new GameProfileStore(f.DirectoryPath).LoadProfiles()[0].DisableFullscreenOptimizations);
            }
        }

        private static void FsoOwnedRestoreRetry()
        {
            using (var f = new AutomaticFsoFixture())
            {
                string userSetting = "~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE";
                f.Layers[f.Second] = userSetting;
                GameProfile profile = f.Mode.GetProfiles()[0];
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(profile.Id, true));
                f.DeniedPath = f.First;
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(profile.Id, false));
                Eq(false, f.Current(profile.Id).DisableFullscreenOptimizations);
                Eq(1, FsoTweak.TrackedExes().Length);
                Eq("failed", f.Mode.AutomaticExecutionState(GameMode.AutomaticFsoKey));
                f.DeniedPath = null;
                f.Mode.SyncAutomaticFullscreenOptimizations(nowTicks: DateTime.UtcNow.Ticks + TimeSpan.TicksPerMinute * 10);
                Eq(false, FsoTweak.IsDisabledForExe(f.First));
                Eq(0, FsoTweak.TrackedExes().Length);
                Eq(userSetting, f.Layers[f.Second]);
            }
        }

        private static void FsoProfileSaveFailureIsTransactional()
        {
            using (var f = new AutomaticFsoFixture())
            {
                long now = DateTime.UtcNow.Ticks;
                f.SetField("profileSaveFailureSignaled", 1);
                f.Mode.SyncAutomaticFullscreenOptimizations(nowTicks: now);
                Eq(0, f.Reads); Eq(0, f.Writes);
                f.SetField("profileSaveFailureSignaled", 0);
                GameProfile profile = f.Mode.GetProfiles()[0];
                string file = Path.Combine(f.DirectoryPath, GameProfileStore.FileName);
                string before = File.ReadAllText(file);
                int changes = 0;
                f.Mode.LibraryChanged += delegate { changes++; };
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(false, f.Mode.SetProfileFullscreenOptimizations(profile.Id, true));
                Eq(false, f.Mode.ProfileStoreSaveFailed);
                Eq(false, f.Current(profile.Id).DisableFullscreenOptimizations);
                Eq(0, changes); Eq(0, f.Writes);
                Eq(before, File.ReadAllText(file));
                Eq(0, FsoTweak.TrackedExes().Length);
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(profile.Id, true));
                Eq(1, changes);
                Eq(true, FsoTweak.IsDisabledForExe(f.First));
                before = File.ReadAllText(file);
                int writes = f.Writes;
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(false, f.Mode.SetProfileFullscreenOptimizations(profile.Id, false));
                Eq(true, f.Current(profile.Id).DisableFullscreenOptimizations);
                Eq(1, changes); Eq(writes, f.Writes);
                Eq(before, File.ReadAllText(file));
                Eq(1, FsoTweak.TrackedExes().Length);
                Eq(true, f.Mode.SetProfileFullscreenOptimizations(profile.Id, false));
                Eq(2, changes);
                Eq(false, FsoTweak.IsDisabledForExe(f.First));
                Eq(0, FsoTweak.TrackedExes().Length);
            }
        }

        private const BindingFlags FsoHidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private sealed class AutomaticFsoFixture : IDisposable
        {
            internal readonly string DirectoryPath, First, Second, Learned;
            internal readonly GameMode Mode;
            internal readonly Dictionary<string, string> Layers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal int Reads, Writes;
            internal string DeniedPath;
            internal bool DenyLedger;
            private readonly Func<string, string> oldRead = FsoTweak.ReadLayerForTest;
            private readonly Action<string, string> oldWrite = FsoTweak.WriteLayerForTest;
            private readonly Func<string, bool> oldSave = FsoTweak.SaveTrackedForTest;

            internal AutomaticFsoFixture(bool seedLearned = false, bool seedOptIn = false)
            {
                Settings.UseTransientStoreForCurrentProcess();
                Settings.Save(AutomaticPolicy.VersionKey, true);
                DirectoryPath = Path.Combine(Path.GetTempPath(), "PaviseFsoTest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(DirectoryPath);
                First = NewExecutable("GameOne"); Second = NewExecutable("GameTwo");
                GameProfile first = GameProfileStore.NewProfile("One", DirectoryPath, First);
                GameProfile second = GameProfileStore.NewProfile("Two", DirectoryPath, Second);
                if (seedLearned) { Learned = NewExecutable("LearnedOne"); first.LearnedExecutablePath = Learned; }
                if (seedOptIn)
                {
                    first.Overrides[PolicyCatalog.KeyGameFullscreenOptimizations] = "1";
                    second.Overrides[PolicyCatalog.KeyGameFullscreenOptimizations] = "1";
                }
                Eq(true, new GameProfileStore(DirectoryPath).Save(new[] { first, second }));
                Mode = new GameMode(DirectoryPath, new SuppressionCore());
                SetField("enabled", true);
                FsoTweak.ReadLayerForTest = delegate(string path) {
                    Reads++;
                    if (path == DeniedPath) throw new IOException("Injected compatibility read failure");
                    string value; return Layers.TryGetValue(path, out value) ? value : null;
                };
                FsoTweak.WriteLayerForTest = delegate(string path, string value) { Writes++; Layers[path] = value; };
                FsoTweak.SaveTrackedForTest = delegate(string value) { return !DenyLedger && Settings.SaveStr("FsoExeList", value); };
            }
            internal GameProfile Current(string id)
            {
                foreach (GameProfile profile in Mode.GetProfiles()) if (profile.Id == id) return profile;
                throw new InvalidOperationException("Missing FSO fixture profile");
            }
            internal void SetField(string name, object value) { typeof(GameMode).GetField(name, FsoHidden).SetValue(Mode, value); }
            internal string NewExecutable(string name)
            {
                string path = Path.Combine(DirectoryPath, name + ".exe");
                File.Copy(typeof(GameMode).Assembly.Location, path);
                return path;
            }
            public void Dispose()
            {
                Eq(true, Mode.Stop());
                FsoTweak.ReadLayerForTest = oldRead; FsoTweak.WriteLayerForTest = oldWrite; FsoTweak.SaveTrackedForTest = oldSave;
                Directory.Delete(DirectoryPath, true);
            }
        }
    }
}
#endif
