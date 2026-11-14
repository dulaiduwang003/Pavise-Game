using System;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private ConfigurationPresetStrip configurationPresetStrip;
        private ConfigurationPresetRuntime configurationPresetRuntime;
        private int configurationPresetUiGeneration;
        private bool configurationPresetDisposeHook;
        private string configurationPresetLastResult = "";
        private string configurationPresetLastIssue = "";
        private ConfigurationPresetGuideDialog configurationPresetGuide;
        private bool configurationPresetRestartPending, configurationPresetRestartPartial, configurationPresetRestartUnverified;
#if PAVISE_SELFTEST
        internal Action<ConfigurationPresetGuideDialog> ConfigurationPresetGuideForTest;
        internal Action<string, string> ConfigurationPresetRestartReminderForTest;
#endif

        private void BuildConfigurationPresetControls()
        {
            if (configurationPresetRuntime == null) configurationPresetRuntime = new ConfigurationPresetRuntime(gameMode);
            if (!configurationPresetDisposeHook)
            {
                configurationPresetDisposeHook = true;
                Disposed += delegate { CancelConfigurationPresetWork(); };
            }
            configurationPresetStrip = new ConfigurationPresetStrip { TabIndex = 1 };
            configurationPresetStrip.GuideRequested += OpenConfigurationPresetGuide;
            pagePolicy.Controls.Add(configurationPresetStrip);
            SyncConfigurationPresetControls();
        }

        private void CancelConfigurationPresetWork()
        {
            Interlocked.Increment(ref configurationPresetUiGeneration);
            configurationPresetRestartPending = false;
            if (configurationPresetRuntime != null) configurationPresetRuntime.Cancel();
        }

        // 仅退出工作线程调用，界面线程不能调用。
        internal bool WaitForConfigurationPresetWork(int timeoutMilliseconds)
        {
            ConfigurationPresetRuntime work = configurationPresetRuntime;
            if (work == null) return true;
            work.Cancel();
            return work.WaitForCompletion(timeoutMilliseconds);
        }

        private void LayoutConfigurationPresetControls(int left, int top, int right)
        {
            if (performanceCategories == null || configurationPresetStrip == null || configurationPresetStrip.IsDisposed) return;
            int gap = Theme.S(12);
            int width = Math.Max(1, right - left);
            int stripWidth = Math.Min(configurationPresetStrip.PreferredStripWidth,
                Math.Max(configurationPresetStrip.MinimumStripWidth, width - Theme.S(470) - gap));
            stripWidth = Math.Min(stripWidth, Math.Max(1, width - gap - Theme.S(250)));
            int tabsWidth = Math.Max(1, width - stripWidth - gap);
            performanceCategories.SetBounds(left, top, tabsWidth, Theme.S(44));
            configurationPresetStrip.SetBounds(left + tabsWidth + gap, top + Theme.S(6), stripWidth, Theme.S(32));
        }

        private void SyncConfigurationPresetControls()
        {
            if (configurationPresetStrip == null || configurationPresetStrip.IsDisposed || configurationPresetRuntime == null) return;
            bool allowed = !runtimeShutdownBusy && wipeBusy == 0 && !IsDisposed && !Disposing;
            bool busy = configurationPresetRuntime.Busy;
            configurationPresetStrip.SetState(configurationPresetRuntime.CurrentPreset, busy, allowed, configurationPresetLastResult);
            if (performanceTabPanels != null)
                foreach (DBPanel panel in performanceTabPanels)
                    if (panel != null && !panel.IsDisposed) panel.Enabled = allowed && !busy;
        }

        private void OpenConfigurationPresetGuide()
        {
            if (configurationPresetRuntime == null || configurationPresetRuntime.Busy || runtimeShutdownBusy || wipeBusy != 0 || IsDisposed || Disposing) return;
            if (configurationPresetGuide != null && !configurationPresetGuide.IsDisposed) { configurationPresetGuide.Activate(); return; }
            configurationPresetRestartPending = false;
            using (var guide = new ConfigurationPresetGuideDialog(configurationPresetRuntime.CurrentPreset))
            {
                configurationPresetGuide = guide;
                guide.ApplyRequested += ApplyConfigurationPreset;
                if (configurationPresetLastIssue.Length > 0) guide.ShowIssue(configurationPresetLastIssue);
                try {
#if PAVISE_SELFTEST
                    if (ConfigurationPresetGuideForTest == null) throw new InvalidOperationException("Preset guide presentation was not mocked");
                    ConfigurationPresetGuideForTest(guide);
#else
                    guide.ShowDialog(this);
#endif
                }
                finally { configurationPresetGuide = null; }
            }
            if (configurationPresetRestartPending && !IsDisposed && !Disposing && !runtimeShutdownBusy)
            {
                configurationPresetRestartPending = false;
                ShowConfigurationPresetRestartReminder(configurationPresetRestartPartial, configurationPresetRestartUnverified);
            }
        }

        private void ApplyConfigurationPreset(ConfigurationPresetKind selected)
        {
            if (configurationPresetRuntime == null || configurationPresetStrip == null || configurationPresetStrip.IsDisposed ||
                configurationPresetRuntime.Busy || runtimeShutdownBusy || wipeBusy != 0 || IsDisposed || Disposing) return;
            int generation = Interlocked.Increment(ref configurationPresetUiGeneration);
            if (configurationPresetGuide != null && !configurationPresetGuide.IsDisposed) configurationPresetGuide.SetBusy(true);
            bool started = configurationPresetRuntime.TryApply(selected,
                delegate { return IsDisposed || Disposing || runtimeShutdownBusy || wipeBusy != 0 ||
                    generation != Volatile.Read(ref configurationPresetUiGeneration); },
                delegate(ConfigurationPresetResult result) {
                    if (IsDisposed || Disposing || !IsHandleCreated || generation != Volatile.Read(ref configurationPresetUiGeneration)) return;
                    try {
                        BeginInvoke(new MethodInvoker(delegate {
                            if (IsDisposed || Disposing || runtimeShutdownBusy || wipeBusy != 0 ||
                                generation != Volatile.Read(ref configurationPresetUiGeneration)) return;
                            configurationPresetLastResult = ConfigurationPresetReport(result);
                            configurationPresetLastIssue = result.Cancelled ? PerformancePolicy.Text("设置已停止，可重新选择。", "Setup stopped. Choose a setup to try again.")
                                : result.Failed > 0 ? PerformancePolicy.Text("部分设置未能应用，可重试。", "Some settings could not be applied. Try again.")
                                : result.Pending > 0 ? PerformancePolicy.Text("部分配置已记录，仍待重启或状态验证。", "Some preferences were saved and await a restart or state verification.") : "";
                            RefreshEnvironmentStateAsync();
                            RefreshAutomationPage();
                            SyncConfigurationPresetControls();
                            if (configurationPresetGuide != null && !configurationPresetGuide.IsDisposed)
                            {
                                if (result.Cancelled || (result.Failed > 0 && result.Applied == 0 && result.Unchanged == 0))
                                    configurationPresetGuide.ShowIssue(configurationPresetLastIssue);
                                else
                                {
                                    configurationPresetRestartPending = true;
                                    configurationPresetRestartPartial = result.Failed > 0;
                                    configurationPresetRestartUnverified = result.Pending > 0;
                                    configurationPresetGuide.Close();
                                }
                            }
                        }));
                    } catch (InvalidOperationException) { }
                });
            SyncConfigurationPresetControls();
            if (!started && configurationPresetGuide != null && !configurationPresetGuide.IsDisposed)
                configurationPresetGuide.ShowIssue(Lang.T("configpreset.busy"));
        }

        private static string ConfigurationPresetReport(ConfigurationPresetResult result)
        {
            // 执行原因只进入诊断，不放进引导、提示气泡或普通界面。
            return result.Summary;
        }

        private void ShowConfigurationPresetRestartReminder(bool partial, bool pending = false)
        {
            string title = Lang.Cur == 2 ? "PCの再起動が必要です" : PerformancePolicy.Text("请重启电脑", "Restart your PC");
            string body = Lang.Cur == 2 ? "適用した設定を有効にするには、PCを再起動してください。"
                : PerformancePolicy.Text("请重启电脑，使已应用的设置生效。", "Restart your PC for the applied settings to take effect.");
            if (pending) body = PerformancePolicy.Text("部分配置已记录，状态仍待验证。请重启电脑，使需重启的设置生效；待验证项仍需后续确认。",
                "Some preferences were saved and still await verification. Restart your PC for changes that require it; pending items remain unconfirmed.");
            if (partial) body = (Lang.Cur == 2 ? "一部の設定は適用できませんでした。" : PerformancePolicy.Text("部分设置未能应用。", "Some settings could not be applied. ")) + body;
#if PAVISE_SELFTEST
            if (ConfigurationPresetRestartReminderForTest == null) throw new InvalidOperationException("Preset restart reminder must be mocked");
            ConfigurationPresetRestartReminderForTest(title, body);
#else
            PaviseDialog.Info(this, title, body);
#endif
        }
    }
}
