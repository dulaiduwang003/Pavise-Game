// @author bdth 2074055628@qq.com
// File purpose Renderer target replacement after confirmation, test directory only, no game launch, does not go through the normal Program
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
#if PAVISE_RENDERER_LEARNING_SELFTEST
        // Standalone entry runs only the three isolated groups below, never enters Program or the full --selftest
        private static int Main()
        {
            string testRoot = Path.Combine(Path.GetTempPath(),
                "PaviseRendererLearning-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            int failed = 0;
            try
            {
                Action<string>[] checks =
                {
                    TestConfirmedRendererReplacement,
                    TestConfirmedRendererLearningGuards,
                    TestConfirmedRendererSaveFailure
                };
                foreach (Action<string> check in checks)
                {
                    try
                    {
                        check(testRoot);
                        Console.WriteLine("PASS " + check.Method.Name);
                    }
                    catch (Exception error)
                    {
                        failed++;
                        Console.Error.WriteLine("FAIL " + check.Method.Name + " " + error);
                    }
                }
                Console.WriteLine("RendererLearning: " + (checks.Length - failed) + "/" + checks.Length);
                return failed == 0 ? 0 : 1;
            }
            finally { try { Directory.Delete(testRoot, true); } catch { } }
        }
#endif

        private static void TestConfirmedRendererReplacement(string root)
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.Save(PolicyCatalog.KeyBoost, false);
            Lang.Init();
            string dir = Path.Combine(root, "rendererReplace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string oldExe = Path.Combine(dir, "Menu", "GameMenu.exe");
                string oldLearned = Path.Combine(dir, "Prior", "OldRender.exe");
                string renderer = Path.Combine(dir, "Client", "GameRender.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(oldExe));
                Directory.CreateDirectory(Path.GetDirectoryName(oldLearned));
                Directory.CreateDirectory(Path.GetDirectoryName(renderer));
                File.WriteAllBytes(oldExe, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(oldLearned, new byte[] { 4, 5, 6 });
                File.WriteAllBytes(renderer, new byte[] { 7, 8, 9 });

                GameProfile original = GameProfileStore.NewProfile("用户命名不改", dir, oldExe);
                original.LearnedExecutablePath = oldLearned;
                original.Entries.Clear();
                original.Entries.Add("GameMenu");
                original.Entries.Add("OldRender");
                original.Entries.Add("HistoricalAlias");
                Eq(true, PolicyResolver.SetOverride(original, PolicyCatalog.KeyBoost, "0"));
                Eq(true, PolicyResolver.SetOverride(original, PolicyCatalog.KeyPreset, "2"));
                Eq(true, new GameProfileStore(dir).Save(new[] { original }));
                var mode = new GameMode(dir, new SuppressionCore());
                int changed = 0;
                mode.LibraryChanged += delegate { changed++; };
                GameDetection hit = RendererLearningHit(original, renderer);

                Eq(true, mode.TryLearnConfirmedRenderer(hit));
                GameProfile actual = mode.GetProfiles()[0];
                Eq(original.Id, actual.Id);
                Eq(original.Name, actual.Name);
                Eq(original.ForceTrigger, actual.ForceTrigger);
                Eq(renderer, actual.ExecutablePath);
                Eq(null, actual.LearnedExecutablePath);
                Eq(dir, actual.Root); // keep the declared game scope, do not save the old entry alias
                Eq(1, actual.Entries.Count);
                Eq(true, actual.Entries.Contains("GameRender"));
                Eq(2, actual.Overrides.Count);
                Eq("0", actual.Overrides[PolicyCatalog.KeyBoost]);
                Eq("2", actual.Overrides[PolicyCatalog.KeyPreset]);
                Eq(false, GameSessionDetector.IsProfileEntryName(actual, "GameMenu"));
                Eq(false, GameSessionDetector.IsProfileEntryName(actual, "OldRender"));
                Eq(1, changed);
                Eq(true, File.Exists(oldExe));
                Eq(true, File.Exists(oldLearned));
                Eq(true, File.Exists(renderer));
                AssertRendererLearningProfile(actual, new GameProfileStore(dir).LoadProfiles()[0]);

                string file = Path.Combine(dir, GameProfileStore.FileName);
                string saved = File.ReadAllText(file);
                // If the idempotent confirm saved again, this read-only shared lock would make Replace fail
                using (var readLease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Eq(true, mode.TryLearnConfirmedRenderer(hit));
                    hit.RendererPath = Path.Combine(Path.GetDirectoryName(renderer), ".", Path.GetFileName(renderer));
                    Eq(true, mode.TryLearnConfirmedRenderer(hit));
                }
                Eq(false, mode.ProfileStoreSaveFailed);
                Eq(1, changed);
                Eq(saved, File.ReadAllText(file));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static void TestConfirmedRendererLearningGuards(string root)
        {
            Settings.UseTransientStoreForCurrentProcess();
            Lang.Init();
            string dir = Path.Combine(root, "rendererGuards-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                GameProfile original = GameProfileStore.NewProfile("目标", dir,
                    Path.Combine(dir, "GameMenu.exe"));
                GameProfile claimed = GameProfileStore.NewProfile("他档", dir,
                    Path.Combine(dir, "ClaimedRender.exe"));
                claimed.LearnedExecutablePath = Path.Combine(dir, "ClaimedAlias.exe");
                GameProfile forced = GameProfileStore.NewProfile("强制", dir,
                    Path.Combine(dir, "ForcedGame.exe"));
                forced.ForceTrigger = true;
                var before = new[] { original, claimed, forced };
                Eq(true, new GameProfileStore(dir).Save(before));
                var mode = new GameMode(dir, new SuppressionCore());
                int changed = 0;
                mode.LibraryChanged += delegate { changed++; };
                string file = Path.Combine(dir, GameProfileStore.FileName);
                string saved = File.ReadAllText(file);
                string renderer = Path.Combine(dir, "GameRender.exe");

                Eq(false, mode.TryLearnConfirmedRenderer(null));
                Action<GameDetection>[] invalid =
                {
                    delegate(GameDetection hit) { hit.Profile = null; },
                    delegate(GameDetection hit) { hit.RendererCandidateSelected = false; },
                    delegate(GameDetection hit) { hit.RendererLearnable = false; },
                    delegate(GameDetection hit) { hit.RendererSafetyOnly = true; },
                    delegate(GameDetection hit) { hit.RequiresGpuConfirm = true; },
                    delegate(GameDetection hit) { hit.RendererPid = 0; },
                    delegate(GameDetection hit) { hit.RendererCreation = 0; },
                    delegate(GameDetection hit) { hit.Profile.ForceTrigger = true; },
                    delegate(GameDetection hit) { hit.Profile.Id = "removed-profile"; },
                    delegate(GameDetection hit) { hit.Profile.ExecutablePath = Path.Combine(dir, "StaleEntry.exe"); },
                    delegate(GameDetection hit) { hit.Profile.LearnedExecutablePath = Path.Combine(dir, "StaleAlias.exe"); },
                    delegate(GameDetection hit) { hit.Profile.Root = Path.Combine(dir, "StaleRoot"); },
                    delegate(GameDetection hit) { hit.RendererName = null; },
                    delegate(GameDetection hit) { hit.RendererName = ""; },
                    delegate(GameDetection hit) { hit.RendererName = "WrongImage"; },
                    delegate(GameDetection hit) { hit.RendererPath = "GameRender.exe"; },
                    delegate(GameDetection hit) { hit.RendererPath = "C:GameRender.exe"; },
                    delegate(GameDetection hit) { hit.RendererPath = "\\GameRender.exe"; },
                    delegate(GameDetection hit) { hit.RendererPath = "\0"; }
                };
                foreach (Action<GameDetection> invalidate in invalid)
                {
                    GameDetection hit = RendererLearningHit(original, renderer);
                    invalidate(hit);
                    Eq(false, mode.TryLearnConfirmedRenderer(hit));
                }
                Eq(false, mode.TryLearnConfirmedRenderer(RendererLearningHit(original, claimed.ExecutablePath)));
                Eq(false, mode.TryLearnConfirmedRenderer(RendererLearningHit(original, claimed.LearnedExecutablePath)));
                // Unsafe OS targets are never eligible, client and game names are not subject to this rule
                Eq(false, mode.TryLearnConfirmedRenderer(RendererLearningHit(original, @"C:\Windows\System32\svchost.exe")));
                GameDetection staleForce = RendererLearningHit(forced, renderer);
                staleForce.Profile.ForceTrigger = false;
                Eq(false, mode.TryLearnConfirmedRenderer(staleForce));

                // The test hook stays compatible, but likewise may not bypass another tier's claim via Learned
                mode.ProbeLearnRenderer(original.Id, claimed.ExecutablePath, "ClaimedRender");
                Eq(false, mode.ProfileStoreSaveFailed);
                Eq(0, changed);
                Eq(saved, File.ReadAllText(file));
                List<GameProfile> after = mode.GetProfiles();
                Eq(before.Length, after.Count);
                for (int i = 0; i < before.Length; i++) AssertRendererLearningProfile(before[i], after[i]);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static void TestConfirmedRendererSaveFailure(string root)
        {
            Settings.UseTransientStoreForCurrentProcess();
            Lang.Init();
            string dir = Path.Combine(root, "rendererSaveFailure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                GameProfile original = GameProfileStore.NewProfile("保存失败原样保留", dir,
                    Path.Combine(dir, "GameMenu.exe"));
                original.LearnedExecutablePath = Path.Combine(dir, "OldRender.exe");
                original.Entries.Add("OldRender");
                Eq(true, PolicyResolver.SetOverride(original, PolicyCatalog.KeyBoost, "0"));
                Eq(true, new GameProfileStore(dir).Save(new[] { original }));
                var mode = new GameMode(dir, new SuppressionCore());
                int changed = 0, failures = 0;
                GameProfile observedAtFailure = null;
                mode.LibraryChanged += delegate { changed++; };
                // Does not subscribe to Program's fatal handler, the fault lands only on the store instance this test created
                mode.ProfileStoreSaveFailure += delegate
                {
                    failures++;
                    observedAtFailure = mode.GetProfiles()[0];
                };
                string file = Path.Combine(dir, GameProfileStore.FileName);
                string saved = File.ReadAllText(file);
                GameDetection hit = RendererLearningHit(original, Path.Combine(dir, "GameRender.exe"));
                using (var readLease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(false, mode.TryLearnConfirmedRenderer(hit));

                Console.WriteLine("RENDERER_BUSY fatal=" + mode.ProfileStoreSaveFailed + " failures=" + failures + " changes=" + changed);
                Eq(false, mode.ProfileStoreSaveFailed);
                Eq(0, failures);
                Eq(0, changed);
                Eq(null, observedAtFailure);
                AssertRendererLearningProfile(original, mode.GetProfiles()[0]);
                Eq(saved, File.ReadAllText(file));
                // A brief lock is not a fatal fault, once the write lock is released the same instance can still commit
                Eq(true, mode.TryLearnConfirmedRenderer(hit));
                Eq(0, failures);
                Eq(1, changed);
                Eq(hit.RendererPath, new GameProfileStore(dir).LoadProfiles()[0].ExecutablePath);
                var observations = (RendererObservationStore)typeof(GameMode).GetField("rendererObservations",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(mode);
                Eq(true, observations.Close(3000)); // Drain optional asynchronous history before checking owned files
                string[] remainingTemps = Directory.GetFiles(dir, "*.tmp");
                if (remainingTemps.Length != 0)
                    throw new InvalidOperationException("Profile retry left temporary files: " + string.Join(",", remainingTemps));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static GameDetection RendererLearningHit(GameProfile profile, string renderer)
        {
            return new GameDetection
            {
                Profile = profile.Clone(),
                RendererPid = 7101,
                RendererCreation = 123456789,
                RendererPath = renderer,
                RendererName = Path.GetFileNameWithoutExtension(renderer),
                RendererCandidateSelected = true,
                RendererLearnable = true
            };
        }

        private static void AssertRendererLearningProfile(GameProfile expected, GameProfile actual)
        {
            if (actual == null) throw new Exception("renderer learning profile missing");
            Eq(expected.Id, actual.Id);
            Eq(expected.Name, actual.Name);
            Eq(expected.Root, actual.Root);
            Eq(expected.ExecutablePath, actual.ExecutablePath);
            Eq(expected.LearnedExecutablePath, actual.LearnedExecutablePath);
            Eq(expected.ForceTrigger, actual.ForceTrigger);
            Eq(true, expected.Entries.SetEquals(actual.Entries));
            Eq(expected.Overrides.Count, actual.Overrides.Count);
            foreach (KeyValuePair<string, string> item in expected.Overrides)
            {
                string value;
                Eq(true, actual.Overrides.TryGetValue(item.Key, out value));
                Eq(item.Value, value);
            }
        }
    }
}
