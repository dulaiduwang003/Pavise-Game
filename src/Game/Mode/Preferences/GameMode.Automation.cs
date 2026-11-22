using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode : IExeLaunchSettingHost
    {
        private ExeLaunchSettingCoordinator exeLaunchSettings;

        private AutomationLogDedup automationLog;

        // 两个组件正常由构造函数创建 这里的兜底只给测试里不走构造函数造出来的 GameMode 用
        internal ExeLaunchSettingCoordinator ExeLaunchSettings
        {
            get { return LazyInitializer.EnsureInitialized(ref exeLaunchSettings, () => new ExeLaunchSettingCoordinator(this)); }
        }

        internal AutomationLogDedup AutomationLog
        {
            get { return LazyInitializer.EnsureInitialized(ref automationLog, () => new AutomationLogDedup()); }
        }

        object IExeLaunchSettingHost.SyncRoot { get { return sync; } }
        bool IExeLaunchSettingHost.Stopping { get { return stopping; } }
        bool IExeLaunchSettingHost.ProfileStoreSaveFailed { get { return ProfileStoreSaveFailed; } }
        bool IExeLaunchSettingHost.LibraryRecoveryPending
        {
            get { return autoIgnoreLoadFailed || (libraryIgnoreTransaction != null && libraryIgnoreTransaction.RecoveryPending); }
        }
        GameDetection IExeLaunchSettingHost.ActiveDetection { get { return activeDetection; } }
        IEnumerable<GameProfile> IExeLaunchSettingHost.ProfilesLocked { get { return profiles; } }
        GameProfile IExeLaunchSettingHost.FindProfileLocked(string profileId) { return FindProfileLocked(profileId); }
        void IExeLaunchSettingHost.RecordAutomation(string key, string state) { RecordAutomation(key, state); }

        internal void InitializeAutomation()
        {
            Settings.Remove("AutomaticPolicyV1");
            Settings.Remove("AutoFrameCoordinationV1");
            MigrateLegacyPreset();
            RetireSceneRulePreferences();
            SyncAutomationPolicy();
        }
        private void SyncAutomationPolicy()
        {
            PolicySnapshot policySnapshot = sessionPolicy ?? PolicyResolver.Global();
            bgSuppressOn = policySnapshot.SuppressBackground;
            boostOn = policySnapshot.BoostGame;
            pauseUpdateOn = policySnapshot.PauseUpdate;
            pauseServicesOn = policySnapshot.PauseServices;
            planSwitch = policySnapshot.PowerPlanOn;
            renderLaneOn = policySnapshot.RenderLane;
            nvMaxPerf = policySnapshot.NvMaxPerf;
            nvShaderCacheMax = policySnapshot.NvShaderCacheMax;
            nvRebarOn = false;
            nvDlssMode = "off";
            amdAfmf = false;
            rsrOn = false;
            lock (sync)
            {
                if (!autoAddPreferenceUnsaved)
                {
                    autoAddOn = Settings.LoadCached("GmAutoAdd", false);
                }
            }
            killGameDvr = Settings.LoadCached("GameDvrOff", true);
        }
        internal void RefreshAutomaticPreferences()
        {
            lock (familyPolicyGate)
            {
                InvalidateFamilyPolicy();
                SyncAutomationPolicy();
            }
            RequestPolicyApply("RefreshAutomaticPreferences");
        }
        private void RecordAutomation(string key, string state)
        { RecordAutomationSnapshot(key, state, ""); }

        private void RecordAutomationSnapshot(string key, string state, string powerDetail)
        {
            lock (sync)
            {
                string value;
                automationEvidence.TryGetValue(key, out value);
                bool finalAwakeRestore = key == "awake" && state == "restored" && automationSessionEnding && !DisplayAwakeLog.FinalRestoreLogged;
                string powerSignature = powerDetail.Length == 0 ? "" : state + "|" + powerDetail;
                bool powerFailureChanged = AutomationLog.PowerFailureChanged(powerSignature);
                string timerDetail = key == "AutoGlobalTimer" ? GlobalTimerFailureDetail(state) : "";
                string timerSignature = timerDetail.Length == 0 ? "" : state + "|" + timerDetail;
                bool timerFailureChanged = AutomationLog.TimerFailureChanged(timerSignature);
                if (value == state && !finalAwakeRestore && !powerFailureChanged && !timerFailureChanged)
                {
                    return;
                }
                if (key == "PowerPlanOn") AutomationLog.RememberPowerFailure(powerSignature);
                if (key == "AutoGlobalTimer") AutomationLog.RememberTimerFailure(timerSignature);
                automationEvidence[key] = state;
                if ((key == "awake" && !DisplayAwakeLog.ShouldLog(state, automationSessionEnding)) || string.IsNullOrEmpty(state) || state == "unchanged")
                {
                    return;
                }
                if (key == "session" && !string.IsNullOrEmpty(automationSession))
                {
                    string stateSnapshot = ((state == "started") ? "start" : state);
                    string automationSessionNameOffset = Logger.Format("会话 [{0}] {1} | {2}", "SESSION [{0}] {1} | {2}", automationSession, stateSnapshot, AutomationLog.SessionName);
                    if (state == "restore-failed")
                    {
                        Logger.Warn(automationSessionNameOffset);
                    }
                    else if (state == "restored")
                    {
                        Logger.LogFormat("成功 {0}", "PASS {0}", automationSessionNameOffset);
                    }
                    else
                    {
                        Logger.Info(automationSessionNameOffset);
                    }
                    if (state == "started") Logger.RuntimeIdentity();
                    return;
                }
                string stateOffset = Logger.Format("自动 [{0}] {1} | {2}", "AUTO [{0}] {1} | {2}", automationSession, key, state);
                if (key == "PowerPlanOn")
                {
                    if (powerDetail.Length > 0) stateOffset += " | " + powerDetail;
                    if (state == "stopped")
                    {
                        Logger.Warn(stateOffset);
                        return;
                    }
                }
                if (key == "AutoGlobalTimer")
                {
                    bool verified; uint actual, finest;
                    bool leaseActive = timerLease.Readback(out verified, out actual, out finest);
                    stateOffset += Logger.Format(" | requested100ns={0} 活动={1} 已核验={2} actual100ns={3} finest100ns={4} 避开0.5ms边界={5} 实测Sleep1中位数ms={6}",
                        " | requested100ns={0} active={1} verified={2} actual100ns={3} finest100ns={4} avoids0.5msBoundary={5} measuredSleep1MedianMs={6}",
                        timerLease.Requested, leaseActive, verified, actual, finest,
                        verified && actual > TimerResolutionLease.Boundary && actual <= TimerResolutionLease.Ceiling,
                        timerLease.ChosenMedianMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                    if (timerDetail.Length > 0) stateOffset += " | " + timerDetail;
                    if (state == "stopped") { Logger.Warn(stateOffset); return; }
                }
                switch (state)
                {
                case "failed":
                case "restore-failed":
                    Logger.Warn(stateOffset);
                    break;
                case "applied":
                case "restored":
                case "started":
                case "timer-locked":
                    Logger.LogFormat("成功 {0}", "PASS {0}", stateOffset);
                    break;
                default:
                    Logger.Info(stateOffset);
                    break;
                }
            }
        }
        internal static string AutomationActionLabel(string key)
        {
            if (key == "session")
            {
                return Lang.T("auto.log.session");
            }
            if (key == "AutoDisableFullscreenOptimizations")
            {
                return Lang.T("auto.fso");
            }
            if (key == AutomaticCfgKey)
            {
                return Lang.T("lib.cfg");
            }
            PolicyItem policyItem = PolicyCatalog.ItemOf(key);
            if (policyItem == null)
            {
                return EnvLabel(key);
            }
            return Lang.T(policyItem.LangKey);
        }
        internal static string AutomationStateLabel(string state)
        {
            switch (state ?? "")
            {
            case "": return Lang.T("auto.log.empty");
            case "started": return Lang.T("auto.log.started");
            case "applied": return Lang.T("auto.log.applied");
            case "configured": return Lang.T("auto.log.configured");
            case "fso-configured": return Lang.T("auto.fso.configured");
            case "cfg-configured": return Lang.T("auto.cfg.configured");
            case "restored": return Lang.T("auto.log.restored");
            case "failed": return Lang.T("auto.log.failed");
            case "restore-failed": return Lang.T("auto.log.restore-failed");
            case "pending-reboot": return Lang.T("auto.log.pending-reboot");
            case "restore-pending-reboot": return Lang.T("auto.log.restore-pending-reboot");
            case "ready": return Lang.T("auto.log.ready");
            case "unverified": return Lang.T("env.performance.unverified");
            case "timer-requested": return Lang.T("auto.log.timer-requested");
            case "timer-locked": return Lang.T("auto.log.timer-locked");
            case "timer-limited": return Lang.T("auto.log.timer-limited");
            case "timer-unverified": return Lang.T("auto.log.timer-unverified");
            case "unsupported": return Lang.T("auto.log.unsupported");
            case "unchanged": return Lang.T("auto.log.unchanged");
            case "stopped": return Lang.T("auto.log.stopped");
            case "restoring": return Lang.T("auto.log.restoring");
            default: return state;
            }
        }
        internal string AutomaticExecutionState(string key)
        {
            bool servicesStillPaused = key == "GmPauseServices" && OptionalServicePause.Active;
            int pendingStopCount = (servicesStillPaused ? OptionalServicePause.PendingStopCount : 0);
            lock (sync)
            {
                switch (key)
                {
                case "PowerPlanOn":
                    return PowerPlanExecutionState;
                case "AutoDisableFullscreenOptimizations":
                case "AutoDisableControlFlowGuard":
                    return ExeLaunchSettings.State(key);
                case "AutoTimerTick":
                    return timerMode.TickState;
                case "AutoGlobalTimer":
                    return GlobalTimerExecutionState();
                default:
                {
                    string message = null;
                    string[] envKeys = EnvKeys;
                    foreach (string entryKey in envKeys)
                    {
                        if (EnvPolicyKey(entryKey) == key)
                        {
                            message = entryKey;
                            break;
                        }
                    }
                    if (message != null && envRestorePending.Contains(message))
                    {
                        return "restore-failed";
                    }
                    if (message != null && envFused.Contains(message))
                    {
                        return "stopped";
                    }
                    if (servicesStillPaused)
                    {
                        return (pendingStopCount > 0) ? "unverified" : "applied";
                    }
                    string value;
                    if (automationEvidence.TryGetValue(message ?? key, out value))
                    {
                        return value;
                    }
                    return "";
                }
                }
            }
        }
        private void BeginAutomationEvidence()
        {
            lock (sync)
            {
                EndAutomationEvidence();
                automationEvidence.Clear();
                automationSession = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                AutomationLog.SessionName = activeGame ?? ((sessionPolicy == null) ? "" : sessionPolicy.ProfileName) ?? "";
                automationSessionEnding = false;
                DisplayAwakeLog.Reset();
            }
            RecordAutomation("session", "started");
            PolicySnapshot automationPolicySnapshot = AutomationPolicySnapshot;
            foreach (PolicyItem item in PolicyCatalog.All)
            {
                if (!PerformancePolicy.IsIntent(item.Key) && !PerformancePolicy.IsRetired(item.Key))
                {
                    string valueOfResult = automationPolicySnapshot.ValueOf(item.Key);
                    if (valueOfResult == "0" || valueOfResult == "off")
                    {
                        RecordAutomation(item.Key, "unchanged");
                    }
                }
            }
        }
        private void EndAutomationEvidence()
        {
            lock (sync)
            {
                if (!string.IsNullOrEmpty(automationSession) && !automationSessionEnding)
                {
                    automationSessionEnding = true;
                    Logger.Info(Logger.Format("会话 [{0}] 结束 | {1}", "SESSION [{0}] end | {1}", automationSession, AutomationLog.SessionName));
                }
            }
        }
        private void SyncGameDvr()
        {
            SyncGuardScheduling();
        }
        private void RecordNvAutomation(string key, string state, bool preStage)
        {
            if (!preStage)
            {
                RecordAutomation(key, state);
                return;
            }
            string msg = "PRESTAGE " + Lang.T("auto.log.prestage") + " | " + AutomationActionLabel(key) + " | " + AutomationStateLabel(state);
            if (state == "failed")
            {
                Logger.Warn(msg);
            }
            else
            {
                Logger.Info(msg);
            }
        }
        private void SyncPerformanceAutomation()
        {
            Laboratory.Sync();
            bool automationAllowed;
            bool gameActive;
            bool visualMatch;
            lock (sync)
            {
                automationAllowed = enabled && !stopping && !panicReq && !ProfileStoreSaveFailed && !autoIgnoreLoadFailed && (libraryIgnoreTransaction == null || !libraryIgnoreTransaction.RecoveryPending);
                gameActive = automationAllowed && active;
                visualMatch = false; // 桌面视觉简化功能已退役 只恢复旧记录
            }
            performanceActions.Queue(automationAllowed, gameActive, visualMatch);
        }
        internal bool SetVisualEffectsReduction(bool value)
        {
            lock (sync)
            {
                if (value || stopping || !Settings.Save("MatchVisualEffectsV1", false))
                {
                    return false;
                }
            }
            SyncPerformanceAutomation();
            RequestPolicyApply("SetVisualEffectsReduction");
            return true;
        }
        internal static NvGamePlan AutomaticPreStagePlan(PolicySnapshot policy, bool onAc)
        {
            return NvGamePlan.ForAutomatic(policy.NvMaxPerf, policy.NvShaderCacheMax, onAc, policy.NvRebar);
        }
        private bool RunPreStagedMutation(Action mutation)
        {
            return RunPreStagedWork(mutation, false);
        }
        private bool RunPreStagedWork(Action mutation, bool restoring)
        {
            lock (driverStageGate)
            {
                if (stopping || IsActive || mutation == null || (!restoring && sessionRecoveryCaptured && sessionRecoveryRoutes != null && !sessionRecoveryRoutes.Complete))
                {
                    return false;
                }
                mutation();
                return true;
            }
        }
        private void ReleasePreStagedTuning()
        {
            if (stopping || preStagedNvPath == null)
            {
                return;
            }
            preStagedNvPath = null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    RunPreStagedWork(delegate
                    {
                        NvDrsTweaks.RestoreAllGames();
                    }, true);
                }
                catch
                {
                }
            });
        }
    }
}
