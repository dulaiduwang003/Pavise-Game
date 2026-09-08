// 文件用途 渲染交接的集成回归 临时游戏库加上合成的身份 前台 恢复和 GPU 接缝
// 从不启动 GameMode.Loop 和 Program 也不改任何进程
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunRendererCoordinatorRegressionTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Lang.Cur = 0;
            string previousLog = Logger.LogPath;
            Logger.LogPath = null;
            string root = Path.Combine(Path.GetTempPath(), "PaviseRendererCoordinator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Action<string>[] tests =
            {
                RendererCoordinatorRecoversBeforeAsyncProof,
                RendererCoordinatorForceSafetyOnly,
                RendererCoordinatorAmbiguityDoesNotFallBack,
                RendererCoordinatorInvalidateDuringCapture,
                RendererCoordinatorInvalidateDuringRelease,
                RendererCoordinatorInvalidateDuringCommit,
                RendererCoordinatorFocusChangesDuringRecovery,
                RendererCoordinatorLateIdentityResult,
                RendererCoordinatorRemovedProfileResult,
                RendererCoordinatorRemovedOtherProfile,
                RendererCoordinatorLifecycleResult,
                RendererCoordinatorStaleTrackingStaysProtected,
                RendererCoordinatorSaveFailureIsTransactional,
                RendererCoordinatorRetryRevalidates,
                RendererCoordinatorExpiredProofCannotCommit,
                RendererCoordinatorProofMustStillBeHeld,
                RendererCoordinatorForegroundRecheckedAtCommit,
                RendererCoordinatorHardEvidenceAndAltTab,
                RendererCoordinatorSeparateForcedRecovery,
                RendererCoordinatorStickyRejectsOlderBackground,
                RendererCoordinatorStickyCannotRebindPid,
                RendererCoordinatorLegacyRootKeepsSibling
            };
            try
            {
                foreach (Action<string> test in tests)
                {
                    test(root);
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                return tests.Length;
            }
            finally
            {
                Logger.LogPath = previousLog;
                // root 就是上面刚建的那个独立目录 绝不是用户的应用数据
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void RendererCoordinatorRecoversBeforeAsyncProof(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                f.ReleaseState = BackgroundReleaseState.Pending;
                int epoch;
                GameDetection before = f.Resolve(f.Old, out epoch);
                Eq(f.Old.RendererPid, before.RendererPid);
                Eq(true, f.Protected(f.Candidate));
                Eq(0, f.GpuCalls);
                Eq(false, f.Protected(RendererCoordinatorDifferentCreation(f.Candidate)));
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);

                f.ReleaseState = BackgroundReleaseState.Ready;
                f.GpuGate.Reset();
                var time = Stopwatch.StartNew();
                f.Resolve(f.Old, out epoch);
                if (time.ElapsedMilliseconds >= 1500) throw new Exception("GPU worker blocked the resolving thread");
                Eq(true, f.GpuStarted.WaitOne(3000));
                Eq(1, f.GpuCalls);
                Eq(true, f.Tracker.HasProbe);
                Eq(true, f.Protected(f.Candidate));
                f.GpuGate.Set();
                f.Drain();
                GameDetection chosen = f.Resolve(f.Old, out epoch);
                Eq(f.Candidate.RendererPid, chosen.RendererPid);
                Eq(true, chosen.RendererGpuProofExpiresMs > 0);
                Eq(true, f.Commit(chosen, epoch));
                GameProfile saved = f.Mode.GetProfiles()[0];
                Eq(f.Profile.Id, saved.Id);
                Eq(f.Profile.Name, saved.Name);
                Eq(f.Candidate.RendererPath, saved.ExecutablePath);
                Eq(null, saved.LearnedExecutablePath);
                Eq(1, saved.Entries.Count);
                Eq("0", saved.Overrides[PolicyCatalog.KeyBoost]);
                Eq(false, GameSessionDetector.IsProfileEntryName(saved, f.Old.RendererName));
                Eq(true, File.Exists(f.Old.RendererPath));
                RendererCoordinatorInvoke(f.Mode, "CompleteRendererSelection", chosen);
                Eq(false, f.Protected(f.Candidate));
            }
        }

        private static void RendererCoordinatorForceSafetyOnly(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false, true))
            {
                f.Candidate.RendererSafetyOnly = true;
                f.Candidate.RequiresGpuConfirm = false;
                f.Candidate.RendererLearnable = false;
                int epoch;
                GameDetection selected = f.Resolve(f.Old, out epoch);
                Eq(f.Old.RendererPid, selected.RendererPid);
                Eq(true, f.Protected(f.Candidate));
                Eq(0, f.GpuCalls);
                Eq(false, f.Commit(f.Candidate, epoch));
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);
            }
        }

        private static void RendererCoordinatorAmbiguityDoesNotFallBack(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                f.Mode.RendererTestCandidate = delegate { return null; };
                int epoch;
                Eq(null, f.Resolve(f.Candidate, out epoch));
                Eq(false, f.Protected(f.Candidate));
                Eq(0, f.GpuCalls);
                GameDetection hard = RendererHandoffTracker.Copy(f.Candidate);
                hard.RequiresGpuConfirm = false;
                hard.RendererCandidateSelected = true;
                Eq(null, f.Resolve(hard, out epoch));
            }
        }

        private static void RendererCoordinatorInvalidateDuringCapture(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                f.Mode.RendererTestCandidate = delegate
                {
                    f.Invalidate();
                    return RendererHandoffTracker.Copy(f.Candidate);
                };
                int epoch;
                Eq(null, f.Resolve(f.Old, out epoch));
                Eq(false, f.Protected(f.Candidate));
                Eq(false, f.Tracker.HasProbe);
                Eq(0, f.GpuCalls);
            }
        }

        private static void RendererCoordinatorInvalidateDuringRelease(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                f.Mode.RendererTestRelease = delegate
                {
                    f.Invalidate();
                    return BackgroundReleaseState.Ready;
                };
                int epoch;
                Eq(null, f.Resolve(f.Old, out epoch));
                Eq(false, f.Tracker.HasProbe);
                Eq(false, f.Protected(f.Candidate));
            }
        }

        private static void RendererCoordinatorInvalidateDuringCommit(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                int epoch;
                GameDetection selected = f.Confirm(out epoch);
                f.Mode.RendererTestIdentity = delegate(GameDetection ignored)
                {
                    f.Invalidate();
                    return true;
                };
                Eq(false, f.Commit(selected, epoch));
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);
            }
        }

        private static void RendererCoordinatorFocusChangesDuringRecovery(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                f.Mode.RendererTestRelease = delegate
                {
                    f.Foreground = 999999;
                    return BackgroundReleaseState.Ready;
                };
                int epoch;
                GameDetection result = f.Resolve(f.Old, out epoch);
                Eq(f.Old.RendererPid, result.RendererPid);
                Eq(0, f.GpuCalls);
                Eq(true, f.Protected(f.Candidate)); // short focus-loss grace, not an election
                Eq(null, f.Tracker.Confirmed(RendererCoordinatorNow()));
            }
        }

        private static void RendererCoordinatorLateIdentityResult(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                f.GpuGate.Reset();
                int epoch;
                f.Resolve(f.Old, out epoch);
                Eq(true, f.GpuStarted.WaitOne(3000));
                f.IdentityValid = false;
                f.GpuGate.Set();
                f.Drain();
                f.Resolve(f.Old, out epoch);
                Eq(false, f.Protected(f.Candidate));
                Eq(null, f.Tracker.Confirmed(RendererCoordinatorNow()));
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);
            }
        }

        private static void RendererCoordinatorRemovedProfileResult(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                f.GpuGate.Reset();
                int epoch;
                f.Resolve(f.Old, out epoch);
                Eq(true, f.GpuStarted.WaitOne(3000));
                f.Mode.RemoveProfile(f.Profile.Id); // temporary fixture store only
                f.GpuGate.Set();
                f.Drain();
                Eq(null, f.Resolve(null, out epoch));
                Eq(0, f.Mode.GetProfiles().Count);
                Eq(false, f.Protected(f.Candidate));
            }
        }

        private static void RendererCoordinatorLifecycleResult(string root)
        {
            foreach (string flag in new[] { "enabled", "stopping", "panicReq" })
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                f.GpuGate.Reset();
                int epoch;
                f.Resolve(f.Old, out epoch);
                Eq(true, f.GpuStarted.WaitOne(3000));
                // 把取消标志原样跑一遍 不去触发真正的全局还原
                RendererCoordinatorSet(f.Mode, flag, flag != "enabled");
                f.Invalidate();
                f.GpuGate.Set();
                f.Drain();
                Eq(null, f.Resolve(null, out epoch));
                Eq(false, f.Protected(f.Candidate));
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);
            }
        }

        private static void RendererCoordinatorRemovedOtherProfile(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                GameProfile other = GameProfileStore.NewProfile("另一款游戏", f.Directory,
                    Path.Combine(f.Directory, "OtherLauncher.exe"));
                var profiles = RendererCoordinatorField<List<GameProfile>>(f.Mode, "profiles");
                profiles.Add(other.Clone());
                Eq(true, new GameProfileStore(f.Directory).Save(profiles));
                f.Candidate.Profile = other.Clone();
                int epoch;
                GameDetection selected = f.Confirm(out epoch);
                f.Mode.RemoveProfile(other.Id);
                Eq(false, RendererCoordinatorField<bool>(f.Mode, "panicReq"));
                Eq(true, epoch != f.Epoch);
                Eq(false, f.Commit(selected, epoch));
                Eq(1, f.Mode.GetProfiles().Count);
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);
            }
        }

        private static void RendererCoordinatorStaleTrackingStaysProtected(string root)
        {
            foreach (BackgroundReleaseState state in new[]
                { BackgroundReleaseState.IdentityMismatch, BackgroundReleaseState.OtherReasonActive })
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                f.ReleaseState = state;
                int epoch;
                f.Resolve(f.Old, out epoch);
                Eq(true, f.Protected(f.Candidate));
                Eq(0, f.GpuCalls);
                Eq(null, f.Tracker.Confirmed(RendererCoordinatorNow()));
                f.IdentityValid = false;
                f.Resolve(f.Old, out epoch);
                Eq(false, f.Protected(f.Candidate));
            }
        }

        private static void RendererCoordinatorSaveFailureIsTransactional(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                int epoch;
                GameDetection selected = f.Confirm(out epoch);
                string path = Path.Combine(f.Directory, GameProfileStore.FileName);
                string before = File.ReadAllText(path);
                int failures = 0;
                f.Mode.ProfileStoreSaveFailure += delegate { failures++; };
                using (var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(false, f.Commit(selected, epoch));
                Eq(false, f.Mode.ProfileStoreSaveFailed);
                Eq(0, failures);
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);
                Eq(before, File.ReadAllText(path));
                Eq(epoch, f.Epoch);
                Eq(true, f.Protected(f.Candidate));
                Eq(true, f.Commit(selected, epoch));
                Eq(selected.RendererPath, new GameProfileStore(f.Directory).LoadProfiles()[0].ExecutablePath);
            }
        }

        private static void RendererCoordinatorRetryRevalidates(string root)
        {
            foreach (string change in new[] { "control", "foreground", "identity", "proof", "expiry", "epoch" })
            for (int repeat = 0; repeat < 3; repeat++)
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                int epoch;
                GameDetection selected = f.Confirm(out epoch);
                string file = Path.Combine(f.Directory, GameProfileStore.FileName), before = File.ReadAllText(file);
                var store = RendererCoordinatorField<GameProfileStore>(f.Mode, "profileStore");
                int retries = 0, changes = 0, failures = 0;
                f.Mode.LibraryChanged += delegate { changes++; };
                f.Mode.ProfileStoreSaveFailure += delegate { failures++; };
                bool committed;
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    store.RetryWaitForTest = delegate
                    {
                        retries++;
                        if (change == "foreground") f.Foreground = 999999;
                        else if (change == "identity") f.IdentityValid = false;
                        else if (change == "proof") f.Tracker.Clear();
                        else if (change == "expiry") selected.RendererGpuProofExpiresMs = RendererCoordinatorNow() - 1;
                        else if (change == "epoch") f.Invalidate();
                        lease.Dispose();
                    };
                    committed = f.Commit(selected, epoch);
                }
                Eq(1, retries); Eq(0, failures); Eq(false, f.Mode.ProfileStoreSaveFailed);
                Eq(change == "control", committed);
                Eq(change != "control", store.SaveCanceled);
                Eq(change == "control" ? 1 : 0, changes);
                Eq(change == "control", before != File.ReadAllText(file));
                Eq(change == "control" ? f.Candidate.RendererPath : f.Profile.ExecutablePath,
                    f.Mode.GetProfiles()[0].ExecutablePath);
                Eq(true, RendererCoordinatorField<RendererObservationStore>(f.Mode, "rendererObservations").Close(3000));
                Eq(0, Directory.GetFiles(f.Directory, "*.tmp").Length);
            }
        }

        private static void RendererCoordinatorExpiredProofCannotCommit(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                int epoch;
                GameDetection selected = f.Confirm(out epoch);
                selected.RendererGpuProofExpiresMs = RendererCoordinatorNow() - 1;
                Eq(false, f.Commit(selected, epoch));
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);
            }
        }

        private static void RendererCoordinatorHardEvidenceAndAltTab(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                f.Mode.RendererTestCandidate = delegate { return null; };
                f.Foreground = 999999;
                Eq(true, f.Commit(RendererHandoffTracker.Copy(f.Old), f.Epoch));
                GameDetection hard = RendererHandoffTracker.Copy(f.Candidate);
                hard.RendererForeground = false;
                hard.RendererCandidateSelected = true;
                hard.RendererUserSelected = true;
                hard.RequiresGpuConfirm = false;
                hard.RendererLearnable = false;
                Eq(0L, hard.RendererGpuProofExpiresMs);
                Eq(true, f.Commit(hard, f.Epoch));
                Eq(0, f.GpuCalls);
            }
        }

        private static void RendererCoordinatorProofMustStillBeHeld(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                int epoch;
                GameDetection selected = f.Confirm(out epoch);
                f.Tracker.Clear(); // invalidate only the candidate generation, not the whole session
                Eq(epoch, f.Epoch);
                Eq(false, f.Commit(selected, epoch));
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);
            }
        }

        private static void RendererCoordinatorForegroundRecheckedAtCommit(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                int epoch;
                GameDetection selected = f.Confirm(out epoch);
                f.Foreground = 999999;
                Eq(false, f.Commit(selected, epoch));
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);
            }
        }

        private static void RendererCoordinatorSeparateForcedRecovery(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false, true))
            {
                RendererCoordinatorSet(f.Mode, "stickyDetection", null);
                f.Candidate.RendererSafetyOnly = true;
                f.Candidate.RequiresGpuConfirm = false;
                f.Candidate.RendererLearnable = false;
                GameDetection direct = RendererHandoffTracker.Copy(f.Old);
                direct.RendererForeground = false;
                bool recovered = false;
                f.Mode.RendererTestRelease = delegate(int pid, long creation, string name)
                { return pid == direct.RendererPid && !recovered ? BackgroundReleaseState.Pending : BackgroundReleaseState.Ready; };
                int epoch;
                Eq(null, f.Resolve(direct, out epoch));
                Eq(true, f.Protected(direct));
                Eq(true, f.Protected(f.Candidate));
                Eq(0, f.GpuCalls);
                recovered = true;
                GameDetection selected = f.Resolve(null, out epoch);
                Eq(direct.RendererPid, selected.RendererPid);
                Eq(true, f.Commit(selected, epoch));
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);
            }
        }

        private static void RendererCoordinatorStickyRejectsOlderBackground(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, true))
            {
                GameDetection selected = RendererHandoffTracker.Copy(f.Candidate);
                selected.RendererForeground = false;
                selected.RendererCandidateSelected = true;
                selected.RequiresGpuConfirm = false;
                selected.RendererCreation = f.Old.RendererCreation - 1;
                f.Mode.RendererTestIdentity = delegate { return true; };
                Eq(false, f.Commit(selected, f.Epoch));
                Eq(f.Profile.ExecutablePath, f.Mode.GetProfiles()[0].ExecutablePath);
            }
        }

        private static void RendererCoordinatorStickyCannotRebindPid(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, false))
            {
                GameDetection selected = RendererHandoffTracker.Copy(f.Candidate);
                selected.RendererCandidateSelected = true;
                selected.RequiresGpuConfirm = false;
                f.Mode.RendererTestStickyIdentity = delegate(int pid)
                {
                    return new GameProcessSnapshot { Pid = pid, Name = selected.RendererName,
                        Creation = selected.RendererCreation + 1, Path = selected.RendererPath };
                };
                Eq(false, (bool)RendererCoordinatorInvoke(f.Mode, "RememberSticky", selected));
                Eq(f.Candidate.RendererCreation, selected.RendererCreation);
                Eq(f.Old.RendererCreation,
                    RendererCoordinatorField<GameDetection>(f.Mode, "stickyDetection").RendererCreation);
                Eq(false, GameMode.FreshRendererMayReplaceSticky(selected, selected.RendererName,
                    selected.RendererCreation + 1, f.Old.RendererCreation));
            }
        }

        private static void RendererCoordinatorLegacyRootKeepsSibling(string root)
        {
            using (var f = new RendererCoordinatorFixture(root, true))
            {
                f.Mode.RendererTestCandidate = delegate { return null; };
                f.Foreground = f.Old.RendererPid;
                Eq(true, f.Commit(RendererHandoffTracker.Copy(f.Old), f.Epoch));
                GameProfile updated = f.Mode.GetProfiles()[0];
                Eq(f.Old.RendererPath, updated.ExecutablePath);
                Eq(null, updated.LearnedExecutablePath);
                Eq(f.Profile.Root, updated.Root);
                var child = new GameProcessSnapshot { Pid = f.Candidate.RendererPid,
                    Creation = f.Candidate.RendererCreation, Name = f.Candidate.RendererName,
                    Path = f.Candidate.RendererPath, Foreground = true, Visible = true };
                GameDetection candidate = GameSessionDetector.FindForegroundCandidateSnapshot(
                    new[] { child }, new[] { updated }, f.Old);
                Eq(true, candidate != null);
                Eq(true, candidate.RequiresGpuConfirm);
            }
        }

        private static GameDetection RendererCoordinatorDifferentCreation(GameDetection source)
        {
            GameDetection result = RendererHandoffTracker.Copy(source);
            result.RendererCreation++;
            return result;
        }

        private const BindingFlags RendererCoordinatorFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static T RendererCoordinatorField<T>(object instance, string field)
        { return (T)instance.GetType().GetField(field, RendererCoordinatorFlags).GetValue(instance); }
        private static void RendererCoordinatorSet(object instance, string field, object value)
        { instance.GetType().GetField(field, RendererCoordinatorFlags).SetValue(instance, value); }
        private static object RendererCoordinatorInvoke(object instance, string method, params object[] args)
        {
            try { return instance.GetType().GetMethod(method, RendererCoordinatorFlags).Invoke(instance, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
        private static long RendererCoordinatorNow()
        {
            long ticks = Stopwatch.GetTimestamp();
            return ticks / Stopwatch.Frequency * 1000L + ticks % Stopwatch.Frequency * 1000L / Stopwatch.Frequency;
        }

        private sealed class RendererCoordinatorFixture : IDisposable
        {
            internal readonly string Directory;
            internal readonly GameMode Mode;
            internal readonly GameProfile Profile;
            internal readonly GameDetection Old;
            internal readonly GameDetection Candidate;
            internal readonly ManualResetEvent GpuGate = new ManualResetEvent(true);
            internal readonly ManualResetEvent GpuStarted = new ManualResetEvent(false);
            internal volatile bool IdentityValid = true;
            internal int Foreground;
            internal int GpuCalls;
            internal BackgroundReleaseState ReleaseState = BackgroundReleaseState.Ready;

            internal RendererCoordinatorFixture(string root, bool legacy) : this(root, legacy, false) { }
            internal RendererCoordinatorFixture(string root, bool legacy, bool force)
            {
                Directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(Directory);
                string launcher = Path.Combine(Directory, "Menu", "GameLauncher.exe");
                string oldRenderer = Path.Combine(Directory, "MenuUi", "PriorRenderer.exe");
                string renderer = Path.Combine(Directory, "Gameplay", "GameRenderer.exe");
                foreach (string path in new[] { launcher, oldRenderer, renderer })
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, new byte[] { 77, 90, 0, 0 });
                }
                Profile = GameProfileStore.NewProfile("独立配置保留", Directory, launcher);
                Profile.ForceTrigger = force;
                Profile.LearnedExecutablePath = legacy ? oldRenderer : null;
                PolicyResolver.SetOverride(Profile, PolicyCatalog.KeyBoost, "0");
                PolicyResolver.SetOverride(Profile, PolicyCatalog.KeySuppressFamily, "1");
                Eq(true, new GameProfileStore(Directory).Save(new[] { Profile }));
                Mode = new GameMode(Directory, new SuppressionCore(delegate { throw new Exception("Unexpected real core recovery"); }, true));
                Old = Detection(Profile, 71001, 132500000001234560L, legacy ? oldRenderer : launcher, true);
                Candidate = Detection(Profile, 71002, 132500000001234570L, renderer, false);
                Foreground = Candidate.RendererPid;
                RendererCoordinatorSet(Mode, "enabled", true);
                RendererCoordinatorSet(Mode, "active", true);
                RendererCoordinatorSet(Mode, "boostOn", false);
                RendererCoordinatorSet(Mode, "stickyDetection", RendererHandoffTracker.Copy(Old));
                RendererCoordinatorSet(Mode, "activeDetection", RendererHandoffTracker.Copy(Old));
                Mode.RendererTestForeground = delegate { return Foreground; };
                Mode.RendererTestCandidate = delegate { return RendererHandoffTracker.Copy(Candidate); };
                Mode.RendererTestIdentity = delegate(GameDetection value)
                { return value != null && (value.RendererPid == Old.RendererPid || IdentityValid && RendererHandoffTracker.SameIdentity(value, Candidate)); };
                Mode.RendererTestRelease = delegate { return ReleaseState; };
                Mode.RendererTestGpu = delegate(RendererProbeTicket ticket, Func<bool> canceled)
                {
                    Interlocked.Increment(ref GpuCalls);
                    GpuStarted.Set();
                    if (!GpuGate.WaitOne(3000)) throw new Exception("Fake GPU gate timed out");
                    return new Dictionary<int, double> { { ticket.Detection.RendererPid, 42.0 } };
                };
            }

            private static GameDetection Detection(GameProfile profile, int pid, long creation, string path, bool selected)
            {
                var value = new GameDetection { Profile = profile.Clone(), RendererPid = pid,
                    RendererCreation = creation, RendererName = Path.GetFileNameWithoutExtension(path),
                    RendererPath = path, RendererForeground = true, RendererCandidateSelected = selected,
                    RendererUserSelected = selected, RequiresGpuConfirm = !selected, RendererLearnable = !selected };
                value.FamilyPids.Add(pid);
                value.FamilyNames.Add(value.RendererName);
                return value;
            }

            internal int Epoch { get { return RendererCoordinatorField<int>(Mode, "rendererHandoffEpoch"); } }
            internal RendererHandoffTracker Tracker
            { get { return (RendererHandoffTracker)typeof(GameMode).GetProperty("Handoff", RendererCoordinatorFlags).GetValue(Mode, null); } }
            internal GameDetection Resolve(GameDetection raw, out int epoch)
            {
                object[] args = { null, Mode.GetProfiles(), raw, 0 };
                GameDetection value = (GameDetection)RendererCoordinatorInvoke(Mode, "ResolveRendererHandoff", args);
                epoch = (int)args[3];
                return value;
            }
            internal bool Commit(GameDetection value, int epoch)
            { return (bool)RendererCoordinatorInvoke(Mode, "FinalizeRendererSelection", value, epoch); }
            internal bool Protected(GameDetection value)
            { return (bool)RendererCoordinatorInvoke(Mode, "IsRendererHandoffProtected", value.RendererPid, value.RendererCreation, value.RendererPath); }
            internal void Invalidate() { RendererCoordinatorInvoke(Mode, "InvalidateRendererHandoff"); }
            internal void Drain()
            { if (!SpinWait.SpinUntil(delegate { return !Tracker.HasProbe; }, 4000)) throw new Exception("Probe did not finish"); }
            internal GameDetection Confirm(out int epoch)
            {
                Resolve(Old, out epoch);
                Drain();
                GameDetection value = Resolve(Old, out epoch);
                if (value == null || value.RendererPid != Candidate.RendererPid) throw new Exception("Candidate did not confirm");
                return value;
            }
            public void Dispose()
            {
                Invalidate();
                GpuGate.Set();
                Drain();
                GpuGate.Dispose();
                GpuStarted.Dispose();
            }
        }
    }
}
#endif
