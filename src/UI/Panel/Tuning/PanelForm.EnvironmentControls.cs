using System;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm : Form
    {
#if PAVISE_SELFTEST
        internal Func<EnvironmentGraphicsObservation> EnvironmentReadForTest;
        internal Action<Action> EnvironmentQueueForTest;
        internal Action<Action> EnvironmentPostForTest;
#endif
        private void ResetEnvironmentObservation()
        {
            environmentObservation = new EnvironmentObservationCycle();
            environmentPreferenceOverrides.Clear();
            environmentBundleOverride = null;
        }
        private static EnvironmentGraphicsObservation ReadEnvironmentGraphics()
        {
            EnvironmentGraphicsObservation environmentGraphicsObservation = new EnvironmentGraphicsObservation();
            environmentGraphicsObservation.GlobalTimerOn = PerformancePolicy.EnvironmentPreference("AutoGlobalTimer");
            environmentGraphicsObservation.GlobalTimerOwned = GlobalTimerResTweak.OwnsState;
            environmentGraphicsObservation.Tuning = new EnvironmentTuningObservation
            {
                BundleOn = PerformanceTuning.EnvironmentBundleEnabled,
                BundleState = PerformanceTuning.EnvironmentBundleState(),
                BundleLabel = PerformanceTuning.EnvironmentBundleLabel(),
                BundleDetail = EnvironmentBundlePerformanceDetail(PerformanceTuning.EnvironmentBundleDetail)
            };
            return environmentGraphicsObservation;
        }
        private string EnvironmentObservationConfiguration()
        {
            // MutationGeneration 用于使缓存失效 只有规范化后的偏好值决定阶段身份
            int version = Settings.MutationGeneration;
            if (environmentConfigurationSignature != null && environmentConfigurationGeneration == version) return environmentConfigurationSignature;
            StringBuilder value = new StringBuilder();
            foreach (PerformanceAction action in PerformanceActionCatalog.All)
                if (action.Environment) value.Append(action.Key).Append('=').Append(Settings.LoadCached(action.Key, false) ? '1' : '0').Append(';');
            foreach (string key in new[] { "EnvVbsDisabledV1", "EnvCpuMitigationsExpandedV1", "AutoGlobalTimer" })
                value.Append(key).Append('=').Append(Settings.LoadCached(key, false) ? '1' : '0').Append(';');
            environmentConfigurationSignature = value.ToString();
            environmentConfigurationGeneration = version;
            return environmentConfigurationSignature;
        }
        private void QueueEnvironmentObservation(bool force)
        {
            if (!UiActive || base.IsDisposed || base.Disposing || !base.IsHandleCreated)
            {
                return;
            }
#if PAVISE_SELFTEST
            if (SuppressUiWorkersForTest && EnvironmentReadForTest == null) return;
#endif
            EnvironmentObservationCycle cycle = environmentObservation;
            if (force)
            {
                cycle.Version++;
                cycle.StartedUtc = DateTime.MinValue;
            }
            if (cycle.Busy != 0 || (!force && cycle.SettingsVersion == Settings.MutationGeneration && DateTime.UtcNow - cycle.StartedUtc < TimeSpan.FromSeconds(15.0)))
            {
                return;
            }
            long observationSession = gameMode == null ? 0 : gameMode.AutomaticSessionSerial;
            string observationConfiguration = EnvironmentObservationConfiguration();
            if (!environmentObservationFailures.Allowed(0, observationSession, observationConfiguration, "environment")) return;
            cycle.Busy = 1;
            cycle.StartedUtc = DateTime.UtcNow;
            int version = cycle.Version;
            int settingsVersion = Settings.MutationGeneration;
            cycle.SettingsVersion = settingsVersion;
            Action work = delegate
            {
                EnvironmentGraphicsObservation observed = null;
                try
                {
#if PAVISE_SELFTEST
                    if (EnvironmentReadForTest != null) observed = EnvironmentReadForTest();
                    else
#endif
                    observed = ReadEnvironmentGraphics();
                }
                catch (Exception ex)
                {
                    int failures = environmentObservationFailures.Failed(0, observationSession, observationConfiguration, "environment");
                    Logger.Warn(Logger.Format("系统环境观测失败={0}/3: {1}", "Environment observation failure={0}/3: {1}", failures, ex.Message));
                }
                // 成功观测只刷新显示 不返还同一会话和配置已经消耗的失败次数
                Action apply = delegate
                {
                    cycle.Busy = 0;
                    if (!base.IsDisposed && !base.Disposing && object.ReferenceEquals(environmentObservation, cycle))
                    {
                        if (cycle.Version != version || Settings.MutationGeneration != settingsVersion)
                        {
                            cycle.StartedUtc = DateTime.MinValue;
                            QueueEnvironmentObservation(false);
                        }
                        else
                        {
                            if (observed != null)
                            {
                                cycle.Snapshot = observed;
                                environmentPreferenceOverrides.Clear();
                                environmentBundleOverride = null;
                            }
                            RefreshAutomationPage();
                        }
                    }
                };
                try
                {
#if PAVISE_SELFTEST
                    if (EnvironmentPostForTest != null) { EnvironmentPostForTest(apply); return; }
#endif
                    if (!base.IsDisposed && base.IsHandleCreated)
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            apply();
                        });
                    }
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Exchange(ref cycle.Busy, 0);
                }
                catch (Win32Exception)
                {
                    Interlocked.Exchange(ref cycle.Busy, 0);
                }
            };
            try
            {
#if PAVISE_SELFTEST
                if (EnvironmentQueueForTest != null) { EnvironmentQueueForTest(work); return; }
#endif
                if (!ThreadPool.QueueUserWorkItem(delegate
                {
                    work();
                }))
                {
                    cycle.Busy = 0;
                    int failures = environmentObservationFailures.Failed(0, observationSession, observationConfiguration, "environment");
                    Logger.Warn(Logger.Format("系统环境观测入队失败={0}/3", "Environment observation queue failure={0}/3", failures));
                }
            }
            catch
            {
                cycle.Busy = 0;
                int failures = environmentObservationFailures.Failed(0, observationSession, observationConfiguration, "environment");
                Logger.Warn(Logger.Format("系统环境观测入队异常={0}/3", "Environment observation queue exception={0}/3", failures));
            }
        }
        private bool CachedEnvironmentPreference(string key)
        {
            bool value;
            if (environmentPreferenceOverrides.TryGetValue(key, out value))
            {
                return value;
            }
            EnvironmentGraphicsObservation snapshot = environmentObservation.Snapshot;
            if (snapshot == null)
            {
                return Settings.LoadCached(key, false);
            }
            // 系统页的环境开关只有全局计时器一项
            return snapshot.GlobalTimerOn;
        }
        private void AddEnvironmentPerformanceOption(Control parent, ref int y, string key, string title, string description)
        {
            Toggle toggle = MakeSwitch(CachedEnvironmentPreference(key), delegate
            {
                ChangeEnvironmentPerformanceOption(key);
            });
            toggle.Name = "environment" + key;
            toggle.AccessibleName = Lang.T(title);
            environmentPerformanceSwitches.Add(key, toggle);
            Control host = toggle;
            string desc = PerformanceEnvironmentExplanation(key, description);
            int fullTextCardHeightResult = FullTextCardHeight(desc, ScrollContentW, host, 88);
            if (key == "AutoGlobalTimer")
            {
                string readback = Lang.F("env.gtimer.readback", "0.5000", "15.6250", "0.5000");
                string[] textEntries = new string[14]
                {
                    "ready", "timer-locked", "timer-limited", "timer-unverified", "pending-reboot", "restore-pending-reboot", "unverified", "failed", "stopped", "restore-failed",
                    "restored", "unsupported", "timer-requested", ""
                };
                foreach (string entryText in textEntries)
                {
                    fullTextCardHeightResult = Math.Max(fullTextCardHeightResult, FullTextCardHeight(GlobalTimerDescription(entryText, true, readback), ScrollContentW, host, 88));
                    fullTextCardHeightResult = Math.Max(fullTextCardHeightResult, FullTextCardHeight(GlobalTimerDescription(entryText, false, readback), ScrollContentW, host, 88));
                }
            }
            int used;
            SettingCard settingCard = MakeAutoCard(parent, 6, y, ScrollContentW, fullTextCardHeightResult, Lang.T(title), desc, host, out used);
            settingCard.Height += Theme.S(17);
            y += used + 17 + 8;
            environmentPerformanceCards.Add(key, settingCard);
        }
        private void ChangeEnvironmentPerformanceOption(string key)
        {
            Toggle toggle = environmentPerformanceSwitches[key];
            bool restoredState = PerformancePolicy.EnvironmentPreference(key);
            if (RequireElevationFor(toggle, restoredState))
            {
                bool setEnvironmentPreferenceSucceeded = gameMode.SetEnvironmentPreference(key, toggle.Checked);
                if (setEnvironmentPreferenceSucceeded)
                {
                    environmentPreferenceOverrides[key] = toggle.Checked;
                }
                QueueEnvironmentObservation(true);
                SyncEnvironmentPerformanceOptions();
                if (!setEnvironmentPreferenceSucceeded)
                {
                    PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
                }
            }
        }
        private static string PerformanceEnvironmentExplanation(string key, string fallback)
        {
            switch (key)
            {
            case "AutoGlobalTimer":
                return AutoText("适用系统的启动配置在退局后保留，开启或还原后需重启；对局中请求略高于 0.5 ms 的最细一档，退局释放。实际精度由系统决定，可能增加功耗。", "On applicable Windows versions, boot configuration persists after matches and needs a restart after enabling or restoring. The finest step just above 0.5 ms is requested only during matches, then released. Windows decides the actual resolution; power use may rise.");
            default:
                return Lang.T(fallback);
            }
        }
        private static string EnvironmentBundlePerformanceDetail(string original = null)
        {
            return ConcisePerformanceDetail(original, AutoText("关闭 VBS（含内存完整性）和适用的 CPU 软件缓解，调整音频电源管理。手动开启时，仅在支持的设备上使用扩展 CPU 范围。会降低安全防护并影响 WSL2、Hyper-V。关闭会恢复原配置，开启或恢复后需重启电脑。", "Disables VBS (including Memory Integrity) and applicable CPU software mitigations, and adjusts audio power management. Turning this on uses the expanded CPU scope on supported devices. This reduces security protections and affects WSL2 and Hyper-V. Turning it off restores the original configuration. Restart Windows after enabling or restoring."));
        }
    }
}
