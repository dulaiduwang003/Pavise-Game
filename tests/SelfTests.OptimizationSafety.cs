// Retirement and safety regressions. All files belong to temporary test folders.
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunOptimizationSafetyTests()
        {
            Action[] tests = { CacheWarmIsRetired, CacheWarmLegacyLibraryMigrates,
                LaneDefaultsOnAndSaturationWins, WorkingSetTrimRequiresBothPressureSignals,
                ProfileCommitRetriesOnlySafeErrors, ProfileCommitRealLockRecovers,
                ProfileCommitPermanentErrorsRemainFatal, ProfileBusyEditsRollBack,
                ProfileBusyAddsRollBack, SaturationReleasesLaneAndRejectsLateWork,
                ProfileCommitCancellationIsNotFatal, LibraryIgnoreBusyPairRollsBack,
                LibraryIgnoreSecondaryFailurePreservesBoth, LibraryIgnoreRecoveryBlocksThenRetries,
                LibraryIgnoreRestartRecoversBothOutcomes, LibraryIgnoreUnknownStateIsPreserved,
                LibraryIgnoreInvalidInputsDoNotCreateReceipt, LibraryIgnorePendingDoesNotSeedPrimary,
                RestrictedCpuDomainCannotPromote, PowerYieldRejectsInvalidTelemetry,
                PowerYieldMissingTelemetryRestoresThroughRunner, AutoGpuProtectsVisibleImages,
                VramProbeIsSingleFlightAndSessionBound, NormalPriorityDoesNotPollLaneReceipt,
                AutoGpuRevalidatesUnderCommitLock, AutoGpuSharedHostsAreNotFamilies,
                AutoGpuCommitSkipsContendedLocks, AutoGpuCommitDrainsAcrossBoundaries,
                BoostDomainEvidenceIsSessionAndIdentityBound };
            foreach (Action test in tests)
            {
                Settings.UseTransientStoreForCurrentProcess(); Lang.Init(); test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            return tests.Length;
        }

        private static void RestrictedCpuDomainCannotPromote()
        {
            foreach (bool lane in new[] { false, true })
            {
                Eq(Native.NORMAL_PRIORITY_CLASS, GameMode.BoostPriorityTarget(false, lane, 4, 255, false));
                Eq(Native.NORMAL_PRIORITY_CLASS, GameMode.BoostPriorityTarget(false, lane, 255, 255, true));
                Eq(Native.NORMAL_PRIORITY_CLASS, GameMode.BoostPriorityTarget(false, lane, 0, 0, false));
                Eq(Native.NORMAL_PRIORITY_CLASS, GameMode.BoostPriorityTarget(true, lane, 255, 255, false));
                Eq(Native.HIGH_PRIORITY_CLASS, GameMode.BoostPriorityTarget(false, lane, 255, 255, false));
            }
        }

        private static void NormalPriorityDoesNotPollLaneReceipt()
        {
            SafetyFolder(delegate(string folder)
            {
                RenderLane.ResetShutdownForTest();
                var mode = new GameMode(folder, new SuppressionCore());
                Type passType = typeof(GameMode).GetNestedType("BoostPass", BindingFlags.NonPublic);
                object pass = Activator.CreateInstance(passType, true);
                MethodInfo target = typeof(GameMode).GetMethod("SetBoostPriorityTarget", BindingFlags.Instance | BindingFlags.NonPublic);
                Action<string> oldRead = Settings.BeforeStrictStringReadForTest;
                int reads = 0;
                try
                {
                    Settings.BeforeStrictStringReadForTest = delegate(string key) { if (key == "RenderLane") reads++; };
                    for (int i = 0; i < 10; i++) target.Invoke(mode, new[] { pass, (object)Native.NORMAL_PRIORITY_CLASS });
                    Eq(1, reads);
                    target.Invoke(mode, new[] { pass, (object)Native.HIGH_PRIORITY_CLASS });
                    target.Invoke(mode, new[] { pass, (object)Native.NORMAL_PRIORITY_CLASS });
                    Eq(2, reads); // 再次进入保护状态必须重新取消。
                    target.Invoke(mode, new[] { pass, (object)Native.HIGH_PRIORITY_CLASS });
                    Settings.SaveStr("RenderLane", "invalid-test-receipt");
                    target.Invoke(mode, new[] { pass, (object)Native.NORMAL_PRIORITY_CLASS });
                    target.Invoke(mode, new[] { pass, (object)Native.NORMAL_PRIORITY_CLASS });
                    Eq(4, reads); // 恢复失败不得被成功缓存掩盖。
                    Settings.SaveStr("RenderLane", "");
                    target.Invoke(mode, new[] { pass, (object)Native.NORMAL_PRIORITY_CLASS });
                    target.Invoke(mode, new[] { pass, (object)Native.NORMAL_PRIORITY_CLASS });
                    Eq(5, reads);
                }
                finally
                {
                    Settings.BeforeStrictStringReadForTest = oldRead;
                    Settings.SaveStr("RenderLane", ""); RenderLane.ResetShutdownForTest();
                }
            });
        }

        private static PowerBudgetYield SafetyYield(bool proxy, bool held)
        {
            var state = new PowerBudgetYield(); state.Begin(0, true, proxy);
            for (int s = 2; s <= 20; s += 2)
                state.Advance(s * TimeSpan.TicksPerSecond, 98, 40, 45, 150);
            Eq(YieldStage.Engaged, state.Stage);
            if (held)
            {
                for (int s = 22; s <= 36; s += 2)
                    state.Advance(s * TimeSpan.TicksPerSecond, 97, 40, 41, 140);
                Eq(YieldStage.Held, state.Stage);
            }
            return state;
        }

        private static void PowerYieldRejectsInvalidTelemetry()
        {
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity,
                double.NegativeInfinity, -1.0, 101.0 })
            {
                Eq(false, PowerBudgetYield.WorthYielding(98, invalid));
                Eq(false, PowerBudgetYield.WorthYielding(invalid, 40));
                Eq(false, PowerBudgetYield.VerifyHold(45, 41, invalid, 97));
                foreach (bool proxy in new[] { false, true })
                {
                    var cold = new PowerBudgetYield(); cold.Begin(0, true, proxy);
                    cold.Advance(2 * TimeSpan.TicksPerSecond, 98, invalid, 45, 150);
                    for (int s = 4; s <= 20; s += 2)
                        cold.Advance(s * TimeSpan.TicksPerSecond, 98, 40, 45, 150);
                    Eq(YieldStage.Engaged, cold.Stage);
                    Eq(98.0, cold.BaselineGpuUtil);
                    var missing = new PowerBudgetYield(); missing.Begin(0, true, proxy);
                    for (int s = 2; s <= 60; s += 2)
                        Eq(YieldAction.None, missing.Advance(s * TimeSpan.TicksPerSecond, invalid, 40, 45, 150));
                    Eq(YieldStage.Skipped, missing.Stage);
                    foreach (bool held in new[] { false, true })
                    {
                        var state = SafetyYield(proxy, held);
                        int start = held ? 36 : 20;
                        for (int delta = 2; delta <= 10; delta += 2)
                            Eq(delta == 10 ? YieldAction.Revert : YieldAction.None,
                                state.Advance((start + delta) * TimeSpan.TicksPerSecond, 97, invalid, 41, 140));
                        Eq(YieldStage.Reverted, state.Stage);
                        Eq(YieldVerdict.Inconclusive, state.Verdict);
                        Eq(false, PowerBudgetYield.Fused); Eq(false, PowerBudgetYield.FreqFused);
                    }
                }
            }
            foreach (bool proxy in new[] { false, true })
            {
                var state = SafetyYield(proxy, false);
                for (int s = 22; s <= 30; s += 2)
                    state.Advance(s * TimeSpan.TicksPerSecond, 97, 40, double.NaN, double.PositiveInfinity);
                Eq(YieldStage.Reverted, state.Stage);
                Eq(YieldVerdict.Inconclusive, state.Verdict);
                state = SafetyYield(proxy, false);
                Eq(YieldAction.Revert, state.Advance(31 * TimeSpan.TicksPerSecond, 97, 40, 41, 140));
                // 一次短缺测不会把之后完整有效的验证样本判坏。
                state = SafetyYield(proxy, false);
                state.Advance(22 * TimeSpan.TicksPerSecond, -1, double.NaN, -1, -1);
                for (int s = 24; s <= 36; s += 2)
                    state.Advance(s * TimeSpan.TicksPerSecond, 97, 40, 41, 140);
                Eq(YieldStage.Held, state.Stage);
            }
            bool known = false, changed; int high = 0; uint low = 0;
            Eq(-1.0, PowerBudgetYieldRunner.AcceptTargetAdapterSample(new RenderAdapter { Util = double.NaN },
                ref known, ref high, ref low, out changed));
            Eq(false, known);
        }

        private static void PowerYieldMissingTelemetryRestoresThroughRunner()
        {
            foreach (bool restoreWorks in new[] { true, false })
            {
                PowerBudgetYieldRunner.ResetShutdownForTest();
                Exception failure = null;
                int applied = 0, restored = 0, mine = -1; bool receipt = false;
                using (var done = new ManualResetEvent(false))
                {
                    try
                    {
                        mine = PowerBudgetYieldRunner.StartShutdownWorkerForTest(delegate(int generation)
                        {
                            try
                            {
                                var state = new PowerBudgetYield(); state.Begin(0, true);
                                PowerBudgetYieldRunner.SetSampleStateForTest(generation, state);
                                YieldAction action; YieldVerdict verdict;
                                Func<bool> apply = delegate { applied++; receipt = true; return true; };
                                Func<bool> restore = delegate { restored++; if (restoreWorks) receipt = false; return restoreWorks; };
                                for (int s = 2; s <= 28; s += 2)
                                    Eq(true, PowerBudgetYieldRunner.ProcessSample(generation,
                                        s * TimeSpan.TicksPerSecond, s <= 20 ? 98 : -1, 40, 45, -1,
                                        apply, restore, out action, out verdict));
                                Eq(false, PowerBudgetYieldRunner.ProcessSample(generation, 30 * TimeSpan.TicksPerSecond,
                                    -1, 40, 41, -1, apply, restore, out action, out verdict));
                                Eq(YieldAction.Revert, action); Eq(YieldVerdict.Inconclusive, verdict);
                                Eq(1, applied); Eq(1, restored); Eq(!restoreWorks, receipt);
                            }
                            catch (Exception ex) { failure = ex; }
                            finally { done.Set(); }
                        });
                        Eq(true, done.WaitOne(3000));
                        if (failure != null) throw new InvalidOperationException("runner sample dispatch", failure);
                        Eq(true, PowerBudgetYieldRunner.CloseForShutdown(3000));
                        YieldAction late; YieldVerdict ignored;
                        Eq(false, PowerBudgetYieldRunner.ProcessSample(mine, 32 * TimeSpan.TicksPerSecond,
                            98, 40, 41, -1, delegate { throw new Exception("late apply"); },
                            delegate { throw new Exception("late restore"); }, out late, out ignored));
                    }
                    finally
                    {
                        PowerBudgetYieldRunner.CloseForShutdown(3000);
                        PowerBudgetYieldRunner.ResetShutdownForTest();
                    }
                }
            }
        }

        private static void AutoGpuProtectsVisibleImages()
        {
            const string visiblePath = @"C:\Fixture\visible.exe", helperPath = @"C:\Fixture\worker.exe";
            var visible = new HashSet<int> { 10 };
            var entries = new[] {
                new ProcEntry { Pid = 10, Creation = 100, Session = 1, Path = visiblePath },
                new ProcEntry { Pid = 20, ParentPid = 10, Creation = 200, Session = 1, Path = visiblePath },
                new ProcEntry { Pid = 30, ParentPid = 20, Creation = 300, Session = 1, Path = helperPath },
                new ProcEntry { Pid = 40, Creation = 400, Session = 1, Path = AppGpuPath } };
            var snapshot = new ProcessSnapshot(entries);
            foreach (ProcEntry entry in entries)
            {
                var candidate = new GameProcessSnapshot { Pid = entry.Pid, Creation = entry.Creation, Path = entry.Path };
                Eq(entry.Pid == 40, GameMode.AutoGpuVisibilityAllows(candidate, snapshot, visible, 1));
                candidate.Creation++;
                Eq(false, GameMode.AutoGpuVisibilityAllows(candidate, snapshot, visible, 1));
            }
            var background = new GameProcessSnapshot { Pid = 40, Creation = 400, Path = AppGpuPath };
            Eq(false, GameMode.AutoGpuVisibilityAllows(background, snapshot, null, 1));
            Eq(null, GameMode.VisibleWindowResult(false, visible));
            Eq(true, object.ReferenceEquals(visible, GameMode.VisibleWindowResult(true, visible)));
            visible.Add(999); Eq(false, GameMode.AutoGpuVisibilityAllows(background, snapshot, visible, 1));
            visible.Remove(999);
            entries[0].Path = null;
            Eq(false, GameMode.AutoGpuVisibilityAllows(background, snapshot, visible, 1));
            entries[0].Path = visiblePath;
            using (var fixture = new AppGpuFixture())
            {
                bool admittedAfterPrepare = false;
                Eq(AppGpuPreferenceResult.Changed, GameMode.AutoGpuEnroll(fixture.Manager, AppGpuPath, delegate
                {
                    admittedAfterPrepare = fixture.Control.Reads > 0;
                    visible.Add(40); // 模拟 Prepare 后窗口刚刚出现。
                    return GameMode.AutoGpuVisibilityAllows(background, snapshot, visible, 1);
                }));
                Eq(true, admittedAfterPrepare); Eq(0, fixture.Control.Writes); Eq(0, fixture.Ledger.Writes);
                visible.Remove(40);
                Eq(AppGpuPreferenceResult.Success, GameMode.AutoGpuEnroll(fixture.Manager, AppGpuPath,
                    delegate { return GameMode.AutoGpuVisibilityAllows(background, snapshot, visible, 1); }));
                Eq(1, fixture.Control.Writes);
            }
        }

        private static void VramProbeIsSingleFlightAndSessionBound()
        {
            VramSpillProbe.ResetForTest();
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                int reads = 0;
                try
                {
                    VramSpillProbe.ReadForTest = delegate
                    {
                        Interlocked.Increment(ref reads); entered.Set();
                        if (!release.WaitOne(3000)) throw new Exception("fixture timed out");
                        return new Dictionary<int, double> { { 7, VramSpillProbe.WarnBytes * 2 } };
                    };
                    var pids = new List<int> { 7 };
                    VramSpillProbe.SampleIfDueAt(pids, 0); // 必须在 release 之前返回。
                    Eq(true, entered.WaitOne(3000)); pids.Clear();
                    VramSpillProbe.SampleIfDueAt(new[] { 7 }, 25 * TimeSpan.TicksPerSecond);
                    Eq(1, reads); Eq(false, VramSpillProbe.WaitForIdle(0));
                    VramSpillProbe.Reset(); // 在旧查询尚未返回时换局。
                    release.Set(); Eq(true, VramSpillProbe.WaitForIdle(3000));
                    Eq(0, VramSpillProbe.SamplesForTest); Eq(null, VramSpillProbe.Summarize());
                    for (int s = 0; s <= 20; s += 20)
                    {
                        VramSpillProbe.SampleIfDueAt(new[] { 7 }, s * TimeSpan.TicksPerSecond);
                        Eq(true, VramSpillProbe.WaitForIdle(3000));
                    }
                    Eq(2, VramSpillProbe.SamplesForTest); Eq(true, VramSpillProbe.Summarize() != null);
                    VramSpillProbe.SampleIfDueAt(new[] { 7 }, 21 * TimeSpan.TicksPerSecond);
                    Eq(3, reads); // 20 秒限频仍在。
                    entered.Reset(); release.Reset();
                    VramSpillProbe.SampleIfDueAt(new[] { 7 }, 40 * TimeSpan.TicksPerSecond);
                    Eq(true, entered.WaitOne(3000)); VramSpillProbe.Seal();
                    release.Set(); Eq(true, VramSpillProbe.WaitForIdle(3000));
                    Eq(2, VramSpillProbe.SamplesForTest);
                    VramSpillProbe.Reset();
                    VramSpillProbe.ReadForTest = delegate { throw new IOException("PDH unavailable"); };
                    VramSpillProbe.SampleIfDueAt(new[] { 7 }, 0);
                    Eq(true, VramSpillProbe.WaitForIdle(3000)); Eq(0, VramSpillProbe.SamplesForTest);
                    VramSpillProbe.ReadForTest = delegate { return new Dictionary<int, double> { { 7, double.NaN } }; };
                    VramSpillProbe.SampleIfDueAt(new[] { 7 }, 20 * TimeSpan.TicksPerSecond);
                    Eq(true, VramSpillProbe.WaitForIdle(3000)); Eq(1, VramSpillProbe.SamplesForTest);
                    Eq(null, VramSpillProbe.Summarize());
                    Eq(true, VramSpillProbe.CloseForShutdown(3000)); VramSpillProbe.Reset();
                    VramSpillProbe.SampleIfDueAt(new[] { 7 }, 0);
                    Eq(0, VramSpillProbe.SamplesForTest);
                }
                finally
                {
                    release.Set(); Eq(true, VramSpillProbe.WaitForIdle(3000)); VramSpillProbe.ResetForTest();
                }
            }
        }

        private static void SafetyFolder(Action<string> test)
        {
            string folder = Path.Combine(Path.GetTempPath(), "Pavise-SafetyTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try { test(folder); }
            finally { Directory.Delete(folder, true); }
        }

        private static GameProfile SafetyProfile(string folder)
        {
            return GameProfileStore.NewProfile("safety-fixture", folder, Path.Combine(folder, "Game.exe"));
        }

        private static void CacheWarmIsRetired()
        {
            Eq(null, PolicyCatalog.ItemOf("GmCacheWarm"));
            Eq(null, PolicyCatalog.Canonical("GmCacheWarm", "1"));
            Eq(true, GameProfile.IsRetiredOverrideKey("GmCacheWarm"));
            foreach (string key in ExtremeMode.SessionPolicyKeys) Eq(false, key == "GmCacheWarm");
            Eq(null, typeof(GameMode).Assembly.GetType("PaviseApp.CacheWarm"));
            Eq(null, typeof(GameMode).GetProperty("CacheWarmOn"));
            SafetyFolder(delegate(string folder)
            {
                Settings.Save("GmCacheWarm", true);
                new GameMode(folder, new SuppressionCore());
                string value;
                Eq(true, Settings.TryLoadStr("GmCacheWarm", out value)); Eq("", value);
            });
        }

        private static void CacheWarmLegacyLibraryMigrates()
        {
            SafetyFolder(delegate(string folder)
            {
                GameProfile p = SafetyProfile(folder);
                Eq(true, PolicyResolver.SetOverride(p, PolicyCatalog.KeyBoost, "0"));
                var store = new GameProfileStore(folder);
                Eq(true, store.Save(new[] { p }));
                string file = Path.Combine(folder, GameProfileStore.FileName);
                Func<string, string> b64 = delegate(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); };
                File.AppendAllText(file, "O|" + b64(p.Id) + "|" + b64("GmCacheWarm") + "|" + b64("1") + Environment.NewLine);
                List<GameProfile> loaded = store.LoadProfiles();
                Eq(false, store.LoadFailed); Eq(false, store.SaveFailed); Eq(1, loaded.Count);
                Eq(p.Id, loaded[0].Id); Eq(false, loaded[0].Overrides.ContainsKey("GmCacheWarm"));
                Eq("0", loaded[0].Overrides[PolicyCatalog.KeyBoost]);
                Eq(true, store.Save(loaded));
                Eq(false, File.ReadAllText(file).Contains(b64("GmCacheWarm")));
            });
        }

        private static void LaneDefaultsOnAndSaturationWins()
        {
            Eq("1", PolicyCatalog.ItemOf(PolicyCatalog.KeyRenderLane).Fallback);
            Eq(true, PolicyResolver.Global().RenderLane);
            Eq(Native.NORMAL_PRIORITY_CLASS, GameMode.BoostPriorityTarget(true, false));
            Eq(Native.NORMAL_PRIORITY_CLASS, GameMode.BoostPriorityTarget(true, true));
            Eq(Native.HIGH_PRIORITY_CLASS, GameMode.BoostPriorityTarget(false, false));
            Eq(Native.HIGH_PRIORITY_CLASS, GameMode.BoostPriorityTarget(false, true));
            bool laneForcedByExtreme = false;
            foreach (string key in ExtremeMode.SessionPolicyKeys) if (key == PolicyCatalog.KeyRenderLane) laneForcedByExtreme = true;
            Eq(true, laneForcedByExtreme);
            SafetyFolder(delegate(string folder)
            {
                Eq(true, new GameMode(folder, new SuppressionCore()).RenderLaneOn);
                Settings.Save(PolicyCatalog.KeyRenderLane, false);
                Eq(false, PolicyResolver.Global().RenderLane);
                Eq(false, new GameMode(folder, new SuppressionCore()).RenderLaneOn);
            });
        }

        private static void WorkingSetTrimRequiresBothPressureSignals()
        {
            const ulong gib = 1024UL * 1024UL * 1024UL;
            Eq(false, WsTrim.ShouldTrim(64 * gib, 12 * gib));
            Eq(false, WsTrim.ShouldTrim(64 * gib, 6 * gib));
            Eq(false, WsTrim.ShouldTrim(8 * gib, 2 * gib));
            Eq(false, WsTrim.ShouldTrim(8 * gib, gib));
            Eq(false, WsTrim.ShouldTrim(64 * gib, 4 * gib));
            Eq(true, WsTrim.ShouldTrim(32 * gib, 3 * gib));
            Eq(true, WsTrim.ShouldTrim(8 * gib, gib / 2));
            Eq(false, WsTrim.ShouldTrim(0, 0));
            Eq(false, WsTrim.ShouldTrim(gib, 2 * gib));
        }

        private static IOException ReplaceError(int code)
        {
            return new IOException("injected replacement failure", unchecked((int)(0x80070000U | (uint)code)));
        }

        private static void ProfileCommitRetriesOnlySafeErrors()
        {
            foreach (int code in new[] { 32, 33, 1175 })
                SafetyFolder(delegate(string folder)
                {
                    var store = new GameProfileStore(folder);
                    GameProfile p = SafetyProfile(folder);
                    Eq(true, store.Save(new[] { p }));
                    int attempts = 0, waits = 0;
                    store.BeforeReplaceForTest = delegate(int attempt)
                    { attempts++; if (attempt < 2) throw ReplaceError(code); };
                    store.RetryWaitForTest = delegate { waits++; };
                    p.Name = "committed-after-retry";
                    Eq(true, store.Save(new[] { p })); Eq(3, attempts); Eq(2, waits);
                    Eq(false, store.SaveFailed); Eq(false, store.RetryableSaveFailure);
                    Eq(p.Name, new GameProfileStore(folder).LoadProfiles()[0].Name);
                    Eq(0, Directory.GetFiles(folder, "*.tmp").Length);
                });
        }

        private static void ProfileCommitRealLockRecovers()
        {
            SafetyFolder(delegate(string folder)
            {
                var store = new GameProfileStore(folder);
                GameProfile p = SafetyProfile(folder);
                Eq(true, store.Save(new[] { p }));
                string file = Path.Combine(folder, GameProfileStore.FileName), before = File.ReadAllText(file);
                p.Name = "retry-after-unlock";
                int waits = 0;
                store.RetryWaitForTest = delegate { waits++; };
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(false, store.Save(new[] { p }));
                Eq(4, waits); Eq(before, File.ReadAllText(file));
                Eq(false, store.SaveFailed); Eq(true, store.RetryableSaveFailure);
                Eq(0, Directory.GetFiles(folder, "*.tmp").Length);
                Eq(true, store.Save(new[] { p })); Eq(false, store.RetryableSaveFailure);
                Eq(p.Name, new GameProfileStore(folder).LoadProfiles()[0].Name);
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    store.RetryWaitForTest = delegate { lease.Dispose(); };
                    p.Name = "unlocked-within-same-save";
                    Eq(true, store.Save(new[] { p }));
                }
                Eq(p.Name, new GameProfileStore(folder).LoadProfiles()[0].Name);
                Eq(0, Directory.GetFiles(folder, "*.tmp").Length);
            });
        }

        private static void ProfileCommitPermanentErrorsRemainFatal()
        {
            Eq(false, GameProfileStore.IsRetryableReplaceError(new IOException("non-win32", 32)));
            foreach (int code in new[] { 5, 112, 1176, 1177 })
                SafetyFolder(delegate(string folder)
                {
                    var store = new GameProfileStore(folder);
                    GameProfile p = SafetyProfile(folder);
                    Eq(true, store.Save(new[] { p }));
                    string file = Path.Combine(folder, GameProfileStore.FileName), before = File.ReadAllText(file);
                    int attempts = 0, waits = 0;
                    store.BeforeReplaceForTest = delegate { attempts++; throw ReplaceError(code); };
                    store.RetryWaitForTest = delegate { waits++; };
                    Eq(false, store.Save(new[] { p }));
                    Eq(true, store.SaveFailed); Eq(false, store.RetryableSaveFailure);
                    Eq(1, attempts); Eq(0, waits); Eq(before, File.ReadAllText(file));
                    store.BeforeReplaceForTest = null;
                    Eq(false, store.Save(new[] { p }));
                    Eq(0, Directory.GetFiles(folder, "*.tmp").Length);
                });
            SafetyFolder(delegate(string folder)
            {
                var store = new GameProfileStore(folder);
                Eq(false, store.Save(null)); Eq(true, store.SaveFailed);
                Eq(false, store.RetryableSaveFailure);
                Eq(false, File.Exists(Path.Combine(folder, GameProfileStore.FileName)));
            });
        }

        private static void SaturationReleasesLaneAndRejectsLateWork()
        {
            SafetyFolder(delegate(string folder)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var mode = new GameMode(folder, new SuppressionCore());
                mode.RenderLaneOn = true;
                RenderLane.ResetShutdownForTest();
                RenderLane.ConfigureMutationBoundary(null, null);
                try
                {
                    const int pid = 2000000001, tid = 2000000002;
                    const long creation = 1234;
                    int priority = 0, restores = 0;
                    bool canRestore = false;
                    RenderLane.RestoreThreadForTest = delegate(int p, long c, int t, int original)
                    {
                        Eq(pid, p); Eq(creation, c); Eq(tid, t); restores++;
                        if (!canRestore) return false;
                        priority = original; return true;
                    };
                    int generation = RenderLane.ShutdownGenerationForTest;
                    Eq(RenderLane.PinOutcome.Pinned, RenderLane.TryPinForTest(pid, creation, tid, generation,
                        delegate { return priority; }, delegate(int value) { priority = value; return true; }));
                    var saturation = (CpuSaturation)typeof(GameMode).GetField("cpuSaturation", flags).GetValue(mode);
                    saturation.Update(1, 0); Eq(true, saturation.Update(1, CpuSaturation.EnterHoldTicks));
                    Type passType = typeof(GameMode).GetNestedType("BoostPass", BindingFlags.NonPublic);
                    object pass = Activator.CreateInstance(passType, true);
                    passType.GetField("RendererPid").SetValue(pass, pid);
                    passType.GetField("RendererCreation").SetValue(pass, creation);
                    MethodInfo resolve = typeof(GameMode).GetMethod("ResolvePriorityTarget", flags);
                    resolve.Invoke(mode, new[] { pass });
                    Eq(Native.NORMAL_PRIORITY_CLASS, (uint)passType.GetField("PriorityTarget").GetValue(pass));
                    Eq(1, restores); Eq(true, RenderLane.HasResidue());
                    int lateWrites = 0;
                    Eq(false, RenderLane.RunShutdownMutationForTest(generation, delegate { lateWrites++; return true; }));
                    Eq(0, lateWrites);
                    canRestore = true;
                    resolve.Invoke(mode, new[] { pass });
                    Eq(2, restores); Eq(0, priority); Eq(false, RenderLane.HasResidue());
                    Eq(false, RenderLane.IsActiveFor(pid, creation));
                    typeof(GameMode).GetMethod("EngageLaneAndReport", flags).Invoke(mode,
                        new object[] { IntPtr.Zero, null, pid, creation, pass, false, false, false, false, "" });
                    Eq(LaneState.Idle, RenderLane.StateFor(pid, creation));
                }
                finally { RenderLane.ResetShutdownForTest(); RenderLane.ConfigureMutationBoundary(null, null); }
            });
        }

        private static void ProfileBusyAddsRollBack()
        {
            SafetyFolder(delegate(string folder)
            {
                string upgrade = Path.Combine(folder, "UpgradeGame.exe"), fresh = Path.Combine(folder, "FreshGame.exe");
                File.Copy(typeof(GameMode).Assembly.Location, upgrade);
                File.Copy(typeof(GameMode).Assembly.Location, fresh);
                GameProfile legacy = GameProfileStore.NewProfile("legacy-name", folder, null);
                legacy.Entries.Add("UpgradeGame");
                Eq(true, new GameProfileStore(folder).Save(new[] { legacy }));
                var mode = new GameMode(folder, new SuppressionCore());
                var store = (GameProfileStore)typeof(GameMode).GetField("profileStore", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode);
                store.RetryWaitForTest = delegate { };
                string file = Path.Combine(folder, GameProfileStore.FileName), before = File.ReadAllText(file), error;
                var hits = new[] { new ScanHit { Name = "upgrade", Exe = upgrade, Root = folder },
                    new ScanHit { Name = "fresh", Exe = fresh, Root = folder } };
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Eq(false, mode.AddGameExecutable("upgrade", upgrade));
                    Eq(legacy.Name, mode.GetProfiles()[0].Name); Eq(null, mode.GetProfiles()[0].ExecutablePath);
                    Eq(false, mode.AddGameExecutable("fresh", fresh)); Eq(1, mode.GetProfiles().Count);
                    Eq(0, mode.AddScannedGames(hits, out error)); Eq(1, mode.GetProfiles().Count);
                    Eq(null, mode.GetProfiles()[0].ExecutablePath);
                }
                Eq(before, File.ReadAllText(file)); Eq(false, mode.ProfileStoreSaveFailed);
                Eq(2, mode.AddScannedGames(hits, out error));
                Eq(2, mode.GetProfiles().Count); Eq(upgrade, mode.GetProfiles()[0].ExecutablePath);
                Eq(2, new GameProfileStore(folder).LoadProfiles().Count);
            });
        }

        private static T TxnField<T>(GameMode mode, string name)
        {
            return (T)typeof(GameMode).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode);
        }

        private static GameMode TxnMode(string folder, out GameProfile p)
        {
            p = SafetyProfile(folder);
            byte[] pe = new byte[68]; pe[0] = 77; pe[1] = 90; pe[60] = 64; pe[64] = 80; pe[65] = 69;
            File.WriteAllBytes(p.ExecutablePath, pe);
            Eq(true, new GameProfileStore(folder).Save(new[] { p }));
            var mode = new GameMode(folder, new SuppressionCore());
            TxnField<GameProfileStore>(mode, "profileStore").RetryWaitForTest = delegate { };
            return mode;
        }

        private static string TxnIgnore(string folder)
        {
            string file = Path.Combine(folder, LibraryIgnoreTransaction.IgnoreFileName);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }

        private static void TxnDrain(GameMode mode)
        {
            Eq(true, TxnField<RendererObservationStore>(mode, "rendererObservations").Close(3000));
        }

        private static void ProfileCommitCancellationIsNotFatal()
        {
            foreach (bool existing in new[] { false, true })
            SafetyFolder(delegate(string folder)
            {
                var store = new GameProfileStore(folder);
                GameProfile p = SafetyProfile(folder);
                if (existing) Eq(true, store.Save(new[] { p }));
                string file = Path.Combine(folder, GameProfileStore.FileName);
                string before = existing ? File.ReadAllText(file) : null;
                Eq(false, store.Save(new[] { p }, delegate { return false; }));
                Eq(true, store.SaveCanceled); Eq(false, store.SaveFailed); Eq(false, store.RetryableSaveFailure);
                Eq(before, File.Exists(file) ? File.ReadAllText(file) : null);
                int checks = 0;
                Eq(false, store.Save(new[] { p }, delegate { return ++checks == 1; }));
                Eq(2, checks); Eq(before, File.Exists(file) ? File.ReadAllText(file) : null);
                Eq(false, store.Save(new[] { p }, delegate { throw new IOException("eligibility unavailable"); }));
                Eq(true, store.SaveCanceled); Eq(false, store.SaveFailed);
                Eq(true, store.Save(new[] { p })); Eq(false, store.SaveCanceled);
                Eq(0, Directory.GetFiles(folder, "*.tmp").Length);
            });
        }

        private static void LibraryIgnoreBusyPairRollsBack()
        {
            foreach (string action in new[] { "remove", "add", "batch" })
            for (int repeat = 0; repeat < 3; repeat++)
            SafetyFolder(delegate(string folder)
            {
                GameProfile p; GameMode mode = TxnMode(folder, out p);
                if (action != "remove") mode.RemoveProfile(p.Id);
                string file = Path.Combine(folder, GameProfileStore.FileName), before = File.ReadAllText(file);
                string ignore = TxnIgnore(folder);
                bool wasIgnored = TxnField<HashSet<string>>(mode, "autoAddIgnore").Contains(p.ExecutablePath);
                int count = mode.GetProfiles().Count, changes = 0, failures = 0;
                mode.LibraryChanged += delegate { changes++; };
                mode.ProfileStoreSaveFailure += delegate { failures++; };
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (action == "remove") mode.RemoveProfile(p.Id);
                    else if (action == "add") Eq(false, mode.AddGameExecutable(p.Name, p.ExecutablePath));
                    else
                    {
                        string error;
                        Eq(0, mode.AddScannedGames(new[] { new ScanHit { Name = p.Name, Exe = p.ExecutablePath, Root = folder } }, out error));
                    }
                }
                Eq(before, File.ReadAllText(file)); Eq(ignore, TxnIgnore(folder));
                Eq(wasIgnored, TxnField<HashSet<string>>(mode, "autoAddIgnore").Contains(p.ExecutablePath));
                Eq(count, mode.GetProfiles().Count); Eq(0, changes); Eq(0, failures);
                Eq(false, TxnField<LibraryIgnoreTransaction>(mode, "libraryIgnoreTransaction").RecoveryPending);
                if (action == "remove") { mode.RemoveProfile(p.Id); Eq(0, mode.GetProfiles().Count); }
                else { Eq(true, mode.AddGameExecutable(p.Name, p.ExecutablePath)); Eq(1, mode.GetProfiles().Count); }
                TxnDrain(mode); Eq(0, Directory.GetFiles(folder, "*.tmp").Length);
            });
        }

        private static void LibraryIgnoreSecondaryFailurePreservesBoth()
        {
            foreach (string phase in new[] { "prepare", "apply-ignore" })
            SafetyFolder(delegate(string folder)
            {
                GameProfile p; GameMode mode = TxnMode(folder, out p);
                string before = File.ReadAllText(Path.Combine(folder, GameProfileStore.FileName));
                var txn = TxnField<LibraryIgnoreTransaction>(mode, "libraryIgnoreTransaction");
                txn.BeforeWriteForTest = delegate(string step) { if (step == phase) throw new IOException("owned injected failure"); };
                mode.RemoveProfile(p.Id);
                Eq(1, mode.GetProfiles().Count); Eq(before, File.ReadAllText(Path.Combine(folder, GameProfileStore.FileName)));
                Eq(null, TxnIgnore(folder)); Eq(false, txn.RecoveryPending); Eq(false, mode.ProfileStoreSaveFailed);
                txn.BeforeWriteForTest = null;
                mode.RemoveProfile(p.Id); Eq(0, mode.GetProfiles().Count); TxnDrain(mode);
            });
            SafetyFolder(delegate(string folder)
            {
                GameProfile p; GameMode mode = TxnMode(folder, out p);
                mode.RemoveProfile(p.Id);
                string before = TxnIgnore(folder);
                using (var lease = new FileStream(Path.Combine(folder, LibraryIgnoreTransaction.IgnoreFileName),
                    FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(false, mode.AddGameExecutable(p.Name, p.ExecutablePath));
                Eq(before, TxnIgnore(folder)); Eq(0, mode.GetProfiles().Count); Eq(false, mode.ProfileStoreSaveFailed);
                Eq(true, mode.AddGameExecutable(p.Name, p.ExecutablePath)); TxnDrain(mode);
            });
        }

        private static void LibraryIgnoreRecoveryBlocksThenRetries()
        {
            SafetyFolder(delegate(string folder)
            {
                GameProfile p; GameMode mode = TxnMode(folder, out p);
                var txn = TxnField<LibraryIgnoreTransaction>(mode, "libraryIgnoreTransaction");
                txn.BeforeWriteForTest = delegate(string step) { if (step == "restore-ignore") throw new IOException("owned restore blocked"); };
                string file = Path.Combine(folder, GameProfileStore.FileName), before = File.ReadAllText(file);
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) mode.RemoveProfile(p.Id);
                Eq(true, txn.RecoveryPending); Eq(before, File.ReadAllText(file)); Eq(false, mode.ProfileStoreSaveFailed);
                Eq(false, TxnField<HashSet<string>>(mode, "autoAddIgnore").Contains(p.ExecutablePath));
                Eq(false, mode.RenameProfile(p.Id, "blocked")); Eq(p.Name, mode.GetProfiles()[0].Name);
                Eq(false, mode.SetProfileFamilySuppression(p.Id, true));
                Eq(false, mode.SetProfileCorePlacement(p.Id, "", "0", null));
                Eq(true, File.Exists(Path.Combine(folder, LibraryIgnoreTransaction.FileName)));
                txn.BeforeWriteForTest = null;
                Eq(true, mode.RenameProfile(p.Id, "recovered"));
                Eq(false, txn.RecoveryPending); Eq(null, TxnIgnore(folder));
                Eq(false, File.Exists(Path.Combine(folder, LibraryIgnoreTransaction.FileName))); TxnDrain(mode);
            });
        }

        private static void LibraryIgnoreRestartRecoversBothOutcomes()
        {
            foreach (bool committed in new[] { false, true })
            SafetyFolder(delegate(string folder)
            {
                GameProfile p; GameMode mode = TxnMode(folder, out p);
                var txn = TxnField<LibraryIgnoreTransaction>(mode, "libraryIgnoreTransaction");
                txn.BeforeWriteForTest = delegate(string step)
                { if (step == (committed ? "clear-receipt" : "restore-ignore")) throw new IOException("keep owned receipt"); };
                string file = Path.Combine(folder, GameProfileStore.FileName);
                if (committed) mode.RemoveProfile(p.Id);
                else using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) mode.RemoveProfile(p.Id);
                Eq(true, txn.RecoveryPending); TxnDrain(mode);
                var reloaded = new GameMode(folder, new SuppressionCore());
                Eq(committed ? 0 : 1, reloaded.GetProfiles().Count);
                Eq(committed, TxnField<HashSet<string>>(reloaded, "autoAddIgnore").Contains(p.ExecutablePath));
                Eq(false, TxnField<LibraryIgnoreTransaction>(reloaded, "libraryIgnoreTransaction").RecoveryPending);
                Eq(false, File.Exists(Path.Combine(folder, LibraryIgnoreTransaction.FileName))); TxnDrain(reloaded);
            });
        }

        private static void LibraryIgnoreUnknownStateIsPreserved()
        {
            foreach (bool corruptReceipt in new[] { false, true })
            SafetyFolder(delegate(string folder)
            {
                GameProfile p; GameMode mode = TxnMode(folder, out p);
                var txn = TxnField<LibraryIgnoreTransaction>(mode, "libraryIgnoreTransaction");
                txn.BeforeWriteForTest = delegate(string step) { if (step == "restore-ignore") throw new IOException("keep receipt"); };
                string file = Path.Combine(folder, GameProfileStore.FileName);
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) mode.RemoveProfile(p.Id);
                TxnDrain(mode);
                string receipt = Path.Combine(folder, LibraryIgnoreTransaction.FileName);
                if (corruptReceipt) File.WriteAllText(receipt, "invalid owned receipt");
                else { p.Name = "external-edit"; Eq(true, new GameProfileStore(folder).Save(new[] { p })); }
                string before = File.ReadAllText(file), ignore = TxnIgnore(folder), pending = File.ReadAllText(receipt);
                var reloaded = new GameMode(folder, new SuppressionCore());
                Eq(true, TxnField<LibraryIgnoreTransaction>(reloaded, "libraryIgnoreTransaction").RecoveryPending);
                Eq(false, reloaded.RenameProfile(p.Id, "must-not-write"));
                Eq(false, reloaded.ProfileStoreSaveFailed);
                Eq(before, File.ReadAllText(file)); Eq(ignore, TxnIgnore(folder)); Eq(pending, File.ReadAllText(receipt));
                TxnDrain(reloaded);
            });
        }

        private static void LibraryIgnoreInvalidInputsDoNotCreateReceipt()
        {
            SafetyFolder(delegate(string folder)
            {
                GameProfile p; GameMode mode = TxnMode(folder, out p);
                var txn = TxnField<LibraryIgnoreTransaction>(mode, "libraryIgnoreTransaction");
                var store = TxnField<GameProfileStore>(mode, "profileStore");
                string file = Path.Combine(folder, GameProfileStore.FileName), before = File.ReadAllText(file);
                Eq(false, txn.Commit(new GameProfile[0], null, null));
                Eq(false, txn.Commit(new GameProfile[0], new byte[6 * 1024 * 1024], null));
                Eq(false, txn.RecoveryPending); Eq(false, store.SaveFailed);
                Eq(before, File.ReadAllText(file)); Eq(null, TxnIgnore(folder));
                Eq(false, File.Exists(Path.Combine(folder, LibraryIgnoreTransaction.FileName)));
                Eq(0, Directory.GetFiles(folder, "*.tmp").Length); TxnDrain(mode);
            });
        }

        private static void LibraryIgnorePendingDoesNotSeedPrimary()
        {
            SafetyFolder(delegate(string folder)
            {
                string receipt = Path.Combine(folder, LibraryIgnoreTransaction.FileName);
                File.WriteAllText(receipt, "unresolved owned receipt");
                var mode = new GameMode(folder, new SuppressionCore());
                Eq(0, mode.GetProfiles().Count); Eq(false, mode.ProfileStoreSaveFailed);
                Eq(true, TxnField<LibraryIgnoreTransaction>(mode, "libraryIgnoreTransaction").RecoveryPending);
                Eq(false, File.Exists(Path.Combine(folder, GameProfileStore.FileName)));
                Eq("unresolved owned receipt", File.ReadAllText(receipt)); TxnDrain(mode);
            });
        }

        private static void ProfileBusyEditsRollBack()
        {
            SafetyFolder(delegate(string folder)
            {
                GameProfile p = SafetyProfile(folder);
                p.Overrides[PolicyCatalog.KeyBoost] = "0";
                Eq(true, new GameProfileStore(folder).Save(new[] { p }));
                var mode = new GameMode(folder, new SuppressionCore());
                var store = (GameProfileStore)typeof(GameMode).GetField("profileStore", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode);
                store.RetryWaitForTest = delegate { };
                string file = Path.Combine(folder, GameProfileStore.FileName), before = File.ReadAllText(file);
                int failures = 0, changes = 0;
                mode.ProfileStoreSaveFailure += delegate { failures++; };
                mode.LibraryChanged += delegate { changes++; };
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Eq(false, mode.RenameProfile(p.Id, "not-committed"));
                    Eq(p.Name, mode.GetProfiles()[0].Name);
                    Eq(false, mode.SetProfileForceTrigger(p.Id, true)); Eq(false, mode.GetProfiles()[0].ForceTrigger);
                    Eq(false, mode.SetProfileOverride(p.Id, PolicyCatalog.KeyBoost, "1"));
                    Eq("0", mode.GetProfiles()[0].Overrides[PolicyCatalog.KeyBoost]);
                    Eq(false, mode.ClearProfileOverride(p.Id, PolicyCatalog.KeyBoost));
                    Eq("0", mode.GetProfiles()[0].Overrides[PolicyCatalog.KeyBoost]);
                    Eq(false, mode.SetProfileCorePlacement(p.Id, "", "0", "1"));
                    Eq(1, mode.GetProfiles()[0].Overrides.Count);
                    mode.RemoveProfile(p.Id); Eq(1, mode.GetProfiles().Count);
                }
                Eq(before, File.ReadAllText(file));
                Eq(false, mode.ProfileStoreSaveFailed); Eq(0, failures); Eq(0, changes);
                Eq(true, mode.RenameProfile(p.Id, "retry-committed"));
                Eq("retry-committed", new GameProfileStore(folder).LoadProfiles()[0].Name);
                Eq(true, mode.SetProfileCorePlacement(p.Id, "", "0", "1"));
                GameProfile saved = new GameProfileStore(folder).LoadProfiles()[0];
                Eq("", saved.Overrides[PolicyCatalog.KeyCoreMask]);
                Eq("0", saved.Overrides[PolicyCatalog.KeyStrictCores]);
                Eq("1", saved.Overrides[PolicyCatalog.KeyCoreDomainAlt]);
                Eq(1, changes); Eq(0, Directory.GetFiles(folder, "*.tmp").Length);
            });
        }
    }
}
#endif
