#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunPowerYieldRuntimeRegressionTests()
        {
            PowerYieldStartRevokesDynamicAdmission();
            PowerYieldSampleRevokesDynamicAdmission();
            PowerYieldAdmissionIsCheckedAtWrite();
            PowerYieldFailedRevocationRetainsReceipt();
            PowerYieldLivePolicyRespectsOverrides();
            PowerYieldAdmissionReplacementCannotAuthorizeOldGeneration();
            PowerYieldHealthyStartKeepsBoundAdmission();
            return 7;
        }

        private static int RuntimeYieldWorker(PowerBudgetYield state)
        {
            using (var ready = new ManualResetEvent(false))
            {
                Exception failure = null;
                int mine = PowerBudgetYieldRunner.StartShutdownWorkerForTest(delegate(int generation)
                {
                    try { PowerBudgetYieldRunner.SetSampleStateForTest(generation, state); }
                    catch (Exception e) { failure = e; }
                    finally { ready.Set(); }
                });
                Eq(true, mine > 0); Eq(true, ready.WaitOne(3000));
                if (failure != null) throw new InvalidOperationException("runtime yield fixture", failure);
                return mine;
            }
        }

        private static PowerBudgetYield RuntimeYieldState(int phase)
        {
            if (phase != 0) return RelaxedYield(false, phase == 2);
            var state = new PowerBudgetYield(); state.Begin(0, true); return state;
        }

        private static void RuntimeYieldFinish()
        {
            PowerBudgetYieldRunner.ConfigureMutationBoundary(null, null);
            Eq(true, PowerBudgetYieldRunner.CloseForShutdown(3000));
            PowerBudgetYieldRunner.ResetShutdownForTest();
        }

        private static void PowerYieldStartRevokesDynamicAdmission()
        {
            for (int reason = 0; reason < 4; reason++)
                for (int phase = 0; phase < 3; phase++)
                    using (var fixture = new ResetFlowEppFixture())
                    {
                        PowerBudgetYieldRunner.ResetShutdownForTest();
                        try
                        {
                            if (phase != 0) Eq(true, PowerPlan.TryYieldEpp(PowerBudgetYield.YieldEpp));
                            int mine = RuntimeYieldWorker(RuntimeYieldState(phase));
                            bool environment = reason != 2;
                            PowerBudgetYieldRunner.RuntimeEnvironmentForTest = delegate { return environment; };
                            // All four paths fail before Start can create any real sampling worker
                            PowerBudgetYieldRunner.Start(reason != 0, reason != 1, 123, 456,
                                delegate { return reason != 3; });
                            Eq(YieldStage.Idle, PowerBudgetYieldRunner.Stage);
                            Eq(false, PowerPlan.EppYielded);
                            Eq((uint)10, fixture.Value(fixture.FirstScheme, false));
                            Eq(false, PowerBudgetYieldRunner.RunShutdownMutationForTest(mine,
                                delegate { throw new Exception("revoked generation wrote"); }));
                        }
                        finally { RuntimeYieldFinish(); }
                    }
        }

        private static void PowerYieldSampleRevokesDynamicAdmission()
        {
            foreach (bool policyLoss in new[] { false, true })
                for (int phase = 0; phase < 3; phase++)
                    using (var fixture = new ResetFlowEppFixture())
                    {
                        PowerBudgetYieldRunner.ResetShutdownForTest();
                        try
                        {
                            if (phase != 0) Eq(true, PowerPlan.TryYieldEpp(PowerBudgetYield.YieldEpp));
                            int mine = RuntimeYieldWorker(RuntimeYieldState(phase));
                            PowerBudgetYieldRunner.RuntimeEnvironmentForTest = delegate { return policyLoss; };
                            PowerBudgetYieldRunner.SetRuntimeAdmissionForTest(mine, delegate { return !policyLoss; });
                            YieldAction action; YieldVerdict verdict;
                            Eq(false, PowerBudgetYieldRunner.ProcessSample(mine, 40 * TimeSpan.TicksPerSecond,
                                98, 40, 45, 150, delegate { throw new Exception("ineligible apply"); },
                                PowerPlan.RestoreEpp, out action, out verdict));
                            Eq(YieldStage.Idle, PowerBudgetYieldRunner.Stage); Eq(false, PowerPlan.EppYielded);
                            Eq(false, PowerBudgetYieldRunner.RunShutdownMutationForTest(mine,
                                delegate { throw new Exception("late admission mutation"); }));
                        }
                        finally { RuntimeYieldFinish(); }
                    }
        }

        private static void PowerYieldAdmissionIsCheckedAtWrite()
        {
            using (var fixture = new ResetFlowEppFixture())
            {
                PowerBudgetYieldRunner.ResetShutdownForTest();
                try
                {
                    var state = new PowerBudgetYield(); state.Begin(0, true);
                    for (int second = 2; second < 20; second += 2)
                        state.Advance(second * TimeSpan.TicksPerSecond, 98, 40, 45, 150);
                    int mine = RuntimeYieldWorker(state);
                    bool allowed = true;
                    PowerBudgetYieldRunner.RuntimeEnvironmentForTest = delegate { return allowed; };
                    PowerBudgetYieldRunner.ConfigureMutationBoundary(delegate { allowed = false; }, null);
                    int writes = 0;
                    YieldAction action; YieldVerdict verdict;
                    Eq(false, PowerBudgetYieldRunner.ProcessSample(mine, 20 * TimeSpan.TicksPerSecond,
                        98, 40, 45, 150, delegate { writes++; return true; },
                        PowerPlan.RestoreEpp, out action, out verdict));
                    Eq(0, writes); Eq(false, PowerPlan.EppYielded);
                    Eq(YieldStage.Idle, PowerBudgetYieldRunner.Stage);
                }
                finally { RuntimeYieldFinish(); }
            }
        }

        private static void PowerYieldFailedRevocationRetainsReceipt()
        {
            using (var fixture = new ResetFlowEppFixture())
            {
                PowerBudgetYieldRunner.ResetShutdownForTest();
                try
                {
                    Eq(true, PowerPlan.TryYieldEpp(PowerBudgetYield.YieldEpp));
                    int mine = RuntimeYieldWorker(RuntimeYieldState(2));
                    fixture.IgnoreWrites = true;
                    PowerBudgetYieldRunner.RuntimeEnvironmentForTest = delegate { return false; };
                    YieldAction action; YieldVerdict verdict;
                    Eq(false, PowerBudgetYieldRunner.ProcessSample(mine, 40 * TimeSpan.TicksPerSecond,
                        98, 40, 45, 150, delegate { throw new Exception("ineligible apply"); },
                        PowerPlan.RestoreEpp, out action, out verdict));
                    Eq(true, PowerPlan.EppYielded);
                    Eq(false, PowerBudgetYieldRunner.RunShutdownMutationForTest(mine, delegate { return true; }));
                    Eq(false, PowerBudgetYieldRunner.CloseForShutdown(3000));
                    Eq(true, PowerPlan.EppYielded);
                    fixture.IgnoreWrites = false;
                    Eq(true, PowerBudgetYieldRunner.CloseForShutdown(3000));
                    Eq(false, PowerPlan.EppYielded);
                    Eq((uint)10, fixture.Value(fixture.FirstScheme, false));
                }
                finally { fixture.IgnoreWrites = false; RuntimeYieldFinish(); }
            }
        }

        private static void PowerYieldLivePolicyRespectsOverrides()
        {
            SafetyFolder(delegate(string folder)
            {
                var mode = new GameMode(folder, new SuppressionCore());
                var profile = GameProfileStore.NewProfile("runtime-yield", folder, Path.Combine(folder, "Game.exe"));
                ((List<GameProfile>)BoundaryField(mode, "profiles")).Add(profile);
                BoundarySet(mode, "active", true); BoundarySet(mode, "enabled", true);
                BoundarySet(mode, "activeDetection", new GameDetection {
                    Profile = profile, RendererPid = 123, RendererCreation = 456 });
                profile.Overrides[PolicyCatalog.KeyPreset] = ((int)PerformancePreset.Handheld).ToString();
                mode.ProbeSessionPolicyApply(profile);
                Settings.Save(PolicyCatalog.KeyPowerYield, true);
                Func<bool> admission = (Func<bool>)BoundaryCall(mode, "CapturePowerYieldAdmission", 123, 456L);
                Eq(true, admission());
                Settings.Save(PolicyCatalog.KeyPowerYield, false); Eq(false, admission());
                profile.Overrides[PolicyCatalog.KeyPowerYield] = "1";
                admission = (Func<bool>)BoundaryCall(mode, "CapturePowerYieldAdmission", 123, 456L);
                Eq(true, admission());
                BoundaryCall(mode, "InvalidateOverrideWorkLocked", PolicyCatalog.KeyPowerYield);
                profile.Overrides[PolicyCatalog.KeyPowerYield] = "0";
                Eq(false, admission());
                admission = (Func<bool>)BoundaryCall(mode, "CapturePowerYieldAdmission", 123, 456L);
                Settings.Save(PolicyCatalog.KeyPowerYield, true); Eq(false, admission());
                // Per-game off overrides global on, with Extreme retired no tier can flip a per-game override
                //   switching to Esports keeps it off, only changing the override itself to on lets it through
                profile.Overrides[PolicyCatalog.KeyPreset] = ((int)PerformancePreset.Competitive).ToString();
                mode.ProbeSessionPolicyApply(profile);
                admission = (Func<bool>)BoundaryCall(mode, "CapturePowerYieldAdmission", 123, 456L);
                Eq(false, admission());
                BoundaryCall(mode, "InvalidateOverrideWorkLocked", PolicyCatalog.KeyPowerYield);
                profile.Overrides[PolicyCatalog.KeyPowerYield] = "1";
                mode.ProbeSessionPolicyApply(profile);
                admission = (Func<bool>)BoundaryCall(mode, "CapturePowerYieldAdmission", 123, 456L);
                Eq(true, admission());
                BoundarySet(mode, "stickyGraceOnly", true); Eq(false, admission());
                BoundarySet(mode, "stickyGraceOnly", false);
                BoundarySet(mode, "gameGoneSinceTicks", 1L); Eq(false, admission());
                BoundarySet(mode, "gameGoneSinceTicks", 0L);
                BoundarySet(mode, "profileSaveFailureSignaled", 1); Eq(false, admission());
            });
        }

        private static void PowerYieldAdmissionReplacementCannotAuthorizeOldGeneration()
        {
            foreach (bool retarget in new[] { false, true })
                using (var fixture = new ResetFlowEppFixture())
                using (var nextChecked = new ManualResetEvent(false))
                using (var releaseNext = new ManualResetEvent(false))
                {
                    PowerBudgetYieldRunner.ResetShutdownForTest();
                    Thread starter = null;
                    Exception startFailure = null;
                    try
                    {
                        var state = new PowerBudgetYield(); state.Begin(0, true);
                        for (int second = 2; second < 20; second += 2)
                            state.Advance(second * TimeSpan.TicksPerSecond, 98, 40, 45, 150);
                        int mine = RuntimeYieldWorker(state);
                        // Same PID an override generation invalidated the previous closure
                        // Retarget the old closure itself still returns true the target change must revoke it
                        PowerBudgetYieldRunner.SetRuntimeAdmissionForTest(mine, delegate { return retarget; }, 123, 456);
                        int nextCalls = 0;
                        Func<bool> nextAdmission = delegate
                        {
                            if (Interlocked.Increment(ref nextCalls) != 1) return true;
                            nextChecked.Set();
                            if (!releaseNext.WaitOne(3000)) throw new TimeoutException("replacement admission held");
                            // End the fake Start without ever creating a real sampling worker
                            return false;
                        };
                        starter = new Thread(delegate()
                        {
                            try { PowerBudgetYieldRunner.Start(true, true, retarget ? 789 : 123,
                                retarget ? 987 : 456, nextAdmission); }
                            catch (Exception e) { startFailure = e; }
                        });
                        starter.IsBackground = true;
                        starter.Start();
                        Eq(true, nextChecked.WaitOne(3000));
                        int oldWrites = 0;
                        YieldAction action; YieldVerdict verdict;
                        Eq(false, PowerBudgetYieldRunner.ProcessSample(mine, 20 * TimeSpan.TicksPerSecond,
                            98, 40, 45, 150, delegate { oldWrites++; return true; },
                            PowerPlan.RestoreEpp, out action, out verdict));
                        Eq(0, oldWrites); Eq(1, nextCalls);
                        Eq(false, PowerBudgetYieldRunner.RunShutdownMutationForTest(mine, delegate { return true; }));
                    }
                    finally
                    {
                        releaseNext.Set();
                        if (starter != null) Eq(true, starter.Join(3000));
                        RuntimeYieldFinish();
                    }
                    if (startFailure != null) throw new InvalidOperationException("replacement Start failed", startFailure);
                }
        }

        private static void PowerYieldHealthyStartKeepsBoundAdmission()
        {
            using (var fixture = new ResetFlowEppFixture())
            {
                PowerBudgetYieldRunner.ResetShutdownForTest();
                try
                {
                    var state = new PowerBudgetYield(); state.Begin(0, true);
                    for (int second = 2; second < 20; second += 2)
                        state.Advance(second * TimeSpan.TicksPerSecond, 98, 40, 45, 150);
                    int mine = RuntimeYieldWorker(state);
                    PowerBudgetYieldRunner.SetRuntimeAdmissionForTest(mine, delegate { return true; }, 123, 456);
                    int newCalls = 0;
                    PowerBudgetYieldRunner.Start(true, true, 123, 456,
                        delegate { return Interlocked.Increment(ref newCalls) == 1; });
                    int writes = 0;
                    YieldAction action; YieldVerdict verdict;
                    Eq(true, PowerBudgetYieldRunner.ProcessSample(mine, 20 * TimeSpan.TicksPerSecond,
                        98, 40, 45, 150, delegate { writes++; return true; },
                        PowerPlan.RestoreEpp, out action, out verdict));
                    Eq(YieldAction.Engage, action); Eq(1, writes); Eq(1, newCalls);
                }
                finally { RuntimeYieldFinish(); }
            }
        }
    }
}
#endif
