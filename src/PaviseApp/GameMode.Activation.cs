using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {

        private void ApplyGameTweaks(IntPtr h, int pid, BoostPass pass, bool needTweak)
        {
            if (!needTweak) return;
            NvGamePlan plan = NvGamePlan.ForAutomatic(pass.NvMaxPerf, pass.NvShader, Native.OnAcPower(), pass.NvLowLat, pass.NvVrr, pass.NvRebar);
            string targetPath;
            lock (sync) targetPath = activeDetection == null ? pass.ActionTarget : activeDetection.RendererPath;
            NvSessionAttempt attempt = BeginNvSessionAttempt(targetPath ?? pass.RendererName, pid, pass.RendererCreation, plan.AutomaticKey);
            if (attempt == null) return;
            try
            {
                string imagePath = Native.ImagePath(h);
                if (string.IsNullOrEmpty(imagePath)) { CompleteNvSessionAttempt(attempt); return; }
                QueueNvSessionPlan(imagePath, plan, attempt);
            }
            catch (Exception error)
            {
                CompleteNvSessionAttempt(attempt);
                Logger.RepeatedError("nv-session-prepare", "NVIDIA 游戏配置准备失败： " + error.Message, -1L);
            }
        }

        private void ApplySessionNvPlan(string imagePath, NvGamePlan nvPlan, NvSessionAttempt attempt)
        {
            attempt.Failed = false;
            try
            {
                lock (driverStageGate)
                {
                    lock (sync)
                    {
                        if (gameSessionStarts != attempt.Session
                            || !NvTweakTargetCurrentLocked(attempt.Pid, attempt.Birth, attempt.Generation)) return;
                    }
                    NvDrsTweaks.BeginAutomaticSession(attempt.Session);
                    string oldReceipt;
                    if (nvPlan.Empty && Settings.TryLoadStr("NvDrs_" + Path.GetFileName(imagePath), out oldReceipt) && oldReceipt.Length == 0)
                    {
                        attempt.Processed = true;
                        return;
                    }
                    attempt.Failed = true;
                    bool retry;
                    List<string> failed = ApplyNvGamePlan(imagePath, nvPlan, out retry, -1L);
                    if (!nvPlan.Empty) HandleNvTweakOutcome(failed, nvPlan);
                    attempt.Processed = true;
                    attempt.Failed = retry || (failed != null && failed.Count > 0);
                    attempt.Satisfied = !attempt.Failed && failed != null && failed.Count == 0 && NvDrsTweaks.PlanSatisfied(imagePath, nvPlan);
                }
            }
            catch (Exception error)
            {
                attempt.Failed = true;
                Logger.RepeatedError("nv-session-apply", "NVIDIA 游戏配置应用失败： " + error.Message, -1L);
            }
        }

        private bool Deactivate(string reason, bool quiet)
        {
            if (sessionRecoveryCaptured && sessionRecoveryRoutes != null && !sessionRecoveryRoutes.Complete && !active)
                return RetrySessionRecoveryRoutes(reason);
            using (BeginSessionRecoveryCapture())
            {
                RecoveryRouteSet owner = recoveryCaptureRoutes;
                bool restored = DeactivateCore(reason, quiet);
                if (!object.ReferenceEquals(owner, sessionRecoveryRoutes) || owner.Session != Interlocked.Read(ref gameSessionStarts)) return false;
                sessionRecoveryCaptured = true;
                residueNextTryTicks = sessionRecoveryRoutes.Next;
                return restored;
            }
        }

        private bool DeactivateCore(string reason, bool quiet)
        {
            // 较慢的会话恢复开始前，先撤销排队的电源操作。
            powerSessionEnding = true;
            InvalidatePowerPlanSession();
            RevokeCriticalThreadIntent("Deactivate");
            bool runSessionRecoveryRouteSucceeded = RunSessionRecoveryRoute("critical", () => StopCriticalThreads(0, "Deactivate"));
            EndAutomationEvidence();
            ResetSessionForeground();
            InvalidateCacheWarm();
            InvalidateStandbyCleanerWork();
            EndEnglishInputSession();
            InvalidateRendererHandoff();
            bool reportClean = RunSessionRecoveryRoute("report-finish", delegate { ReportFinish(); return true; });
            RunSessionRecoveryRoute("power-overlay", () => !PowerOverlay.HasResidue || PowerOverlay.Restore());
            RunSessionRecoveryRoute("selfyield", delegate { SelfYield.Release(); return !SelfYield.HasResidue; });
            lock (sync)
            {
                active = false;
                activeGame = null;
                firstSweep = true;
            }
            SyncPerformanceAutomation();
            sessionPolicy = null;
            lock (sync)
            {
                coreSessionPolicy = null;
                criticalSessionPreference = null;
            }
            bool runSessionRecoveryRouteSucceeded2 = RunSessionRecoveryRoute("guard", delegate
            {
                if (enabled && !stopping && !ProfileStoreSaveFailed) { SyncGameDvr(); return !GuardSchedulingRecoveryPending; }
                return RestoreGuardScheduling();
            });
            SuppressionCore.GpuDemoteEnabled = bgSuppressOn;
            gameGoneSinceTicks = 0L;
            ResetBoostIdentity();
            Interlocked.Exchange(ref boostFirstStampTicks, 0L);
            Interlocked.Exchange(ref nvTweakRetryAtTicks, 0L);
            ResetNvApplyAttempts();
            if (!quiet)
            {
                preStagedNvPath = null;
            }
            ResetAdaptiveGuard();
            bool runSessionRecoveryRouteSucceeded3 = RunSessionRecoveryRoute("unboost", () => UnboostGames());
            EndAutoCores();
            slowEnvAtTicks = 0L;
            int deactivateCoreValue = 0;
            bool runSessionRecoveryRouteSucceeded4 = RunSessionRecoveryRoute("background", delegate
            {
                List<int> list = core.PidsWith(SuppressReason.Background);
                deactivateCoreValue += core.ReleaseReason(SuppressReason.Background);
                foreach (int item in list) if (core.IsThrottled(item)) return false;
                return true;
            });
            bool restoreEnvSucceeded = RestoreEnv();
            ClearEnvRetryState();
            if (runSessionRecoveryRouteSucceeded3)
            {
                CrashGuard.ClearBoost();
            }
            int deactivateCoreValueOffset = deactivateCoreValue + gracePreReleased;
            gracePreReleased = 0;
            if (!quiet || deactivateCoreValueOffset > 0)
            {
                Logger.Log(Lang.T("log.gamemodeboost.52", 0) + reason + Lang.T("log.gamemodeboost.53", 0) + deactivateCoreValueOffset + Lang.T("log.gamemodeboost.54", 0));
            }
            lock (sync)
            {
                activeDetection = null;
                transitionProbeRendererPid = 0;
                transitionProbeRendererCreation = 0L;
            }
            ClearSticky();
            NotifyExtensionSession(false);
            bool runRecoveryWaitSucceeded = RunRecoveryWait("standby-worker", () => standbyCleaner == null || standbyCleaner.Drain(0));
            bool runRecoveryWaitSucceeded2 = RunRecoveryWait("input-worker", () => DrainEnglishInput(0));
            bool runRecoveryWaitSucceeded3 = RunRecoveryWait("cache-worker", () => cacheWarm == null || cacheWarm.Drain(0));
            bool runSessionRecoveryRouteSucceeded5 = RunSessionRecoveryRoute("performance-worker", () => performanceActions.WaitForSessionRestore(0));
            bool verified = RunSessionRecoveryRoute("verify", () => EnvRestoreCompleted(true) && !SelfYield.HasResidue, "*");
            bool recoveryComplete = runSessionRecoveryRouteSucceeded3 && restoreEnvSucceeded && runSessionRecoveryRouteSucceeded4 && runRecoveryWaitSucceeded && runRecoveryWaitSucceeded2 && runRecoveryWaitSucceeded3 && runSessionRecoveryRouteSucceeded2 && runSessionRecoveryRouteSucceeded5 && runSessionRecoveryRouteSucceeded && reportClean && verified && !SelfYield.HasResidue;
            // 有界重试耗尽后才报告失败，仍在排队的重试保持恢复中状态。
            RecoveryRouteSet captured = recoveryCaptureRoutes;
            bool retrying = !recoveryComplete && CapturingSessionRecovery && captured != null && captured.RetryScheduled;
            RecordAutomation("session", recoveryComplete ? "restored" : (retrying ? "restoring" : "restore-failed"));
            return recoveryComplete;
        }

        private bool RetryDeactivate(string reason)
        {
            if (sessionRecoveryCaptured && sessionRecoveryRoutes != null && !sessionRecoveryRoutes.Complete)
            {
                RefreshSessionRecoveryIntents();
                if (DateTime.UtcNow.Ticks < residueNextTryTicks) return false;
                return RetrySessionRecoveryRoutes(reason);
            }
            return Deactivate(reason, true);
        }

        private void SyncGameNvDelivery()
        {
            InitializeGameNvDeliveryMetadata();
            if (IsActive || stopping) return;
            string key;
            lock (gameNvGate)
            {
                if (gameNvRecoveryStopped || gameNvRootClear || !BeginLegacyNvRecovery(out key)) return;
            }
            bool restored = false;
            try
            {
                GameNvWorkerQueue.TryQueue(QueueLegacyNvWorker, delegate
                {
                    try { RestoreLegacyGameNvCore(); restored = true; }
                    catch (Exception error) { Logger.Warn("旧 NVIDIA 配置等待恢复： " + error.Message); }
                }, delegate { CompleteLegacyNvRecovery(key, restored); });
            }
            catch (Exception error) { Logger.Warn("旧 NVIDIA 配置恢复入队失败： " + error.Message); }
        }

        private bool RestoreLegacyGameNv()
        {
            string key;
            lock (gameNvGate)
            {
                if (gameNvRecoveryChecked && gameNvRootClear) return true;
                if (!BeginLegacyNvRecovery(out key)) return false;
            }
            bool restored = false;
            try { RestoreLegacyGameNvCore(); restored = true; return true; }
            catch (Exception error) { Logger.Warn("旧 NVIDIA 配置等待恢复： " + error.Message); return false; }
            finally { CompleteLegacyNvRecovery(key, restored); }
        }

        internal static IDictionary<int, double> SampleRendererGpu(ProcessActionFailureBudget budget, GameDetection candidate,
            Func<bool> canceled, Func<IDictionary<int, double>> sample)
        {
            if (canceled() || !budget.Allowed(candidate.RendererPid, candidate.RendererCreation, candidate.RendererPath, "gpu")) return null;
            IDictionary<int, double> result = null;
            try { result = sample(); return result; }
            finally
            {
                if (!canceled()) RecordRendererCaptureResult(budget, candidate, "gpu", result != null, "capture");
            }
        }

        internal static PresentActivitySample FinishRendererPresent(ProcessActionFailureBudget budget, GameDetection candidate,
            PresentActivityCapture capture, string failure)
        {
            PresentActivitySample sample = capture != null ? capture.Finish() : PresentActivitySample.Unavailable(failure);
            if (failure == "budget-exhausted") return sample;
            RecordRendererCaptureResult(budget, candidate, "present", sample.Valid, sample.Failure);
            return sample;
        }

        internal static void RecordRendererCaptureResult(ProcessActionFailureBudget budget, GameDetection candidate,
            string stage, bool succeeded, string failure)
        {
            if (!budget.Allowed(candidate.RendererPid, candidate.RendererCreation, candidate.RendererPath, stage)) return;
            if (succeeded)
                budget.Succeeded(candidate.RendererPid, candidate.RendererCreation, candidate.RendererPath, stage);
            else
            {
                int count = budget.Failed(candidate.RendererPid, candidate.RendererCreation, candidate.RendererPath, stage);
                Logger.Warn("渲染进程 " + stage + " 探测失败=" + count + "/3 进程编号=" + candidate.RendererPid + " 原因=" + failure
                    + (count >= 3 ? "；该通道尝试次数已耗尽" : ""));
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);

        internal void RestorePowerOverlay()
        {
            // 仅用于恢复；新的托管来源不能叠加第二套隐式电源策略。
            if (PowerOverlay.HasResidue) PowerOverlay.Restore();
        }

    }
}
