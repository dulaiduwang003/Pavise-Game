using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swMonitoring;
        private SettingCard cardMonitoring;
        private MonitoringPreview monitoringPreview;
        private readonly Dictionary<MonitoringMetrics, Toggle> monitoringMetricSwitches = new Dictionary<MonitoringMetrics, Toggle>();
        private PillButton btnMonitoringInstall, btnMonitoringDetect, btnMonitoringUninstall;
        private Label lblMonitoringComponent, lblMonitoringProgress, lblMonitoringMessage, lblMonitoringFpsLegend, lblMonitoringAdapter;
        private Control monitoringSettingsPanel, monitoringAppearancePanel, monitoringMetricsPanel;
        private RtssComponentInfo monitoringComponent;
        private CancellationTokenSource monitoringInstallCancellation;
        private bool monitoringBusy, monitoringInstalling, monitoringUninstalling, monitoringAwaitingUninstall, monitoringCanCancel, monitoringDetected, monitoringDisposeHooked;
        private volatile bool monitoringComponentLaunched;
        private string monitoringProgress = "", monitoringMessage = "", monitoringInstallPhase = "";
        private bool monitoringMessageError, monitoringProgressError;
        private int monitoringLastDetectionTick;
        private bool MonitoringComponentInstallPending
        {
            get { return monitoringInstalling || monitoringUninstalling || (monitoringComponent != null
                && (monitoringComponent.InstallationInProgress || monitoringComponent.UninstallationInProgress)); }
        }
        private bool MonitoringControlsAvailable
        {
            get { return !runtimeShutdownBusy && !MonitoringComponentInstallPending && monitoringComponent != null
                && monitoringComponent.Installed && monitoringComponent.SupportsOverlay; }
        }
#if PAVISE_SELFTEST
        internal Func<RtssComponentInfo> MonitoringDetectForTest;
        internal Func<Action<RtssInstallProgress>, CancellationToken, RtssComponentInfo> MonitoringInstallForTest;
        internal Func<Action<RtssInstallProgress>, CancellationToken, RtssComponentInfo> MonitoringUninstallForTest;
        internal Action<Action> MonitoringWorkForTest;
#endif

        private static string MonitoringText(string zh, string en) { return AutomaticPolicy.Text(zh, en); }
        private void BuildPerformanceMonitoringSettings(Control parent)
        {
            monitoringSettingsPanel = parent;
            monitoringMetricSwitches.Clear();
            int height;
            if (monitoringTips != null) monitoringTips.Dispose();
            monitoringTips = new ToolTip { InitialDelay = 400, AutoPopDelay = 18000, ReshowDelay = 100 };
            BuildMonitoringComponentCard(parent);
            swMonitoring = MakeSwitch(MonitoringPreferences.Enabled, delegate {
                if (!AllowMonitoringInteraction()) return;
                SetMonitoringResult(MonitoringPreferences.TrySetEnabled(swMonitoring.Checked));
            });
            swMonitoring.Name = "monitoringEnabled";
            swMonitoring.AccessibleName = MonitoringText("游戏内性能监测", "In-game performance monitoring");
            string detail = MonitoringText("对局期间采集所选指标，退局结束监测。快捷键仅切换悬浮栏显示。", "Collect selected metrics during matches and stop after exiting. The shortcut only toggles overlay visibility.");
            cardMonitoring = MakeAutoCard(parent, 6, 2, ScrollContentW, 61,
                swMonitoring.AccessibleName, "", swMonitoring, out height);
            cardMonitoring.AccessibleDescription = detail;
            monitoringTips.SetToolTip(cardMonitoring, detail);
            lblMonitoringAdapter = new Label { Name = "monitoringGpuAdapter", Visible = false };
            parent.Controls.Add(lblMonitoringAdapter);
            BuildMonitoringHotkey(parent);
            BuildMonitoringAppearanceEditor(parent, 71);
            BuildMonitoringMetricsCard(parent, 417);
            lblMonitoringMessage = CardLabel(parent, "", 18, 490, ScrollContentW - 36, 40, 8.5f, false, Theme.Warning);
            lblMonitoringMessage.Name = "monitoringMessage";
            if (!monitoringDisposeHooked)
            {
                monitoringDisposeHooked = true;
                Disposed += delegate {
                    CloseMonitoringDesktopPreview();
                    if (monitoringTips != null) { monitoringTips.Dispose(); monitoringTips = null; }
                    CancellationTokenSource cancellation = monitoringInstallCancellation;
                    if (cancellation != null && !monitoringComponentLaunched)
                        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
                };
            }
            parent.VisibleChanged += delegate {
                if (parent == monitoringSettingsPanel && !parent.Visible) CloseMonitoringDesktopPreview();
                if (parent == monitoringSettingsPanel && parent.Visible && UiActive) RefreshMonitoringPresentation();
            };
            SyncMonitoringSettings();
        }

        private void SetMonitoringResult(bool saved)
        {
            monitoringMessage = saved ? "" : MonitoringText("设置未能保存，请重试。", "Could not save the setting. Try again.");
            monitoringMessageError = !saved;
            SyncMonitoringSettings();
        }

        private bool AllowMonitoringInteraction()
        {
            if (MonitoringControlsAvailable) return true;
            SyncMonitoringSettings();
            return false;
        }

        private string MonitoringControlsUnavailableText()
        {
            if (runtimeShutdownBusy) return MonitoringText("程序正在退出", "Application is shutting down");
            if (MonitoringComponentInstallPending) return MonitoringText("RTSS 正在安装或卸载，暂不可用", "RTSS installation or removal is in progress");
            if (monitoringComponent == null) return MonitoringText("等待检测 RTSS 组件", "Waiting for RTSS detection");
            if (!monitoringComponent.Installed) return MonitoringText("请先安装 RTSS 组件", "Install the RTSS component first");
            return MonitoringText("当前 RTSS 版本不支持，请更新组件", "This RTSS version is unsupported; update the component");
        }

        private void SyncMonitoringSettings()
        {
            if (swMonitoring == null || swMonitoring.IsDisposed) return;
            if (lblMonitoringFpsLegend != null && !lblMonitoringFpsLegend.IsDisposed)
                lblMonitoringFpsLegend.ForeColor = Theme.ModeColor(Theme.CurrentMode);
            bool available = MonitoringControlsAvailable;
            if (!available) CloseMonitoringDesktopPreview();
            cardMonitoring.Enabled = available;
            monitoringAppearancePanel.Enabled = available;
            monitoringMetricsPanel.Enabled = available;
            SyncMonitoringHotkey(available);
            swMonitoring.SetSilently(MonitoringPreferences.Enabled);
            swMonitoring.Enabled = available;
            string status = !available ? MonitoringControlsUnavailableText()
                : MonitoringPreferences.Enabled ? PerformanceMonitoring.Status : MonitoringText("已关闭", "Off");
            cardMonitoring.SetStatus((available ? "RTSS " + monitoringComponent.Version + " · " : "") + status,
                available && MonitoringPreferences.Enabled ? Theme.Accent : Theme.Dim);
            string gpuName = PerformanceMonitoring.Current.GpuAdapterName;
            lblMonitoringAdapter.Text = MonitoringText("监测显卡：", "Monitored GPU: ") +
                (!MonitoringPreferences.Enabled ? MonitoringText("监测已关闭", "Monitoring is off")
                : (MonitoringPreferences.Metrics & MonitoringNativeSensors.GpuMetrics) == 0 ? MonitoringText("未选择 GPU 指标", "No GPU metrics selected")
                : gpuName.Length > 0 ? gpuName : MonitoringText("进入游戏后自动识别", "Detected automatically in-game"));
            lblMonitoringAdapter.AccessibleDescription = lblMonitoringAdapter.Text;
            pickMonitoringLayout.SetSilently((int)MonitoringPreferences.Layout);
            foreach (var pair in monitoringMetricSwitches) pair.Value.SetSilently((MonitoringPreferences.Metrics & pair.Key) != 0);
            SyncMonitoringMetricsPresentation();
            SyncMonitoringAppearance();
            lblMonitoringComponent.Text = monitoringComponent == null ? MonitoringText("尚未检测", "Not checked")
                : monitoringComponent.InstallationInProgress ? MonitoringText("RTSS 正在安装", "Installing RTSS")
                : monitoringComponent.UninstallationInProgress ? MonitoringText("RTSS 正在卸载", "Uninstalling RTSS")
                : monitoringComponent.Installed ? "RTSS " + monitoringComponent.Version + " · "
                    + (monitoringComponent.Running ? MonitoringText("正在运行", "Running") : MonitoringText("已安装", "Installed"))
                : MonitoringText("未安装 RTSS", "RTSS is not installed");
            if (monitoringComponent != null && monitoringComponent.Installed && monitoringComponent.SupportsOverlay
                && !monitoringComponent.SupportsFontOverride)
                lblMonitoringComponent.Text += MonitoringText(" · 大小调节需要更新至 RTSS 7.3.7 或更新版本", " · Size adjustment requires RTSS 7.3.7 or newer");
            lblMonitoringComponent.AccessibleDescription = lblMonitoringComponent.Text + (monitoringComponent == null ? "" : "\n" + monitoringComponent.Path);
            monitoringTips.SetToolTip(lblMonitoringComponent, lblMonitoringComponent.AccessibleDescription);
            lblMonitoringProgress.Text = monitoringProgress;
            lblMonitoringProgress.ForeColor = monitoringProgressError ? Theme.Warning : Theme.Dim;
            bool installed = monitoringComponent != null && monitoringComponent.Installed;
            btnMonitoringInstall.Text = monitoringInstalling
                ? monitoringCanCancel ? MonitoringText("取消下载", "Cancel download")
                    : monitoringInstallPhase == "Installing" ? MonitoringText("正在安装…", "Installing…") : MonitoringText("处理中…", "Working…")
                : monitoringComponent != null && monitoringComponent.InstallationInProgress ? MonitoringText("正在安装…", "Installing…")
                : installed ? MonitoringText("组件已安装", "Installed") : MonitoringText("下载并安装", "Download and install");
            btnMonitoringInstall.Enabled = monitoringInstalling ? monitoringCanCancel : !monitoringBusy && !installed && !MonitoringComponentInstallPending;
            btnMonitoringDetect.Enabled = !monitoringBusy;
            btnMonitoringUninstall.Text = monitoringUninstalling
                ? monitoringCanCancel ? MonitoringText("取消准备", "Cancel preparation")
                    : MonitoringText("正在卸载…", "Uninstalling…")
                : monitoringComponent != null && monitoringComponent.UninstallationInProgress ? MonitoringText("正在卸载…", "Uninstalling…")
                : MonitoringText("卸载 RTSS", "Uninstall RTSS");
            btnMonitoringUninstall.Enabled = monitoringUninstalling ? monitoringCanCancel
                : !monitoringBusy && installed && !MonitoringComponentInstallPending;
            lblMonitoringMessage.Text = monitoringMessage;
            lblMonitoringMessage.ForeColor = monitoringMessageError ? Theme.Warning : Theme.Dim;
            SyncMonitoringComponentPresentation();
            ReflowMonitoringSettings();
        }

        private void RefreshMonitoringPresentation()
        {
            if (runtimeShutdownBusy || !UiActive || monitoringSettingsPanel == null
                || monitoringSettingsPanel.IsDisposed || !monitoringSettingsPanel.Visible) return;
            SyncMonitoringSettings();
            if (!monitoringBusy && MonitoringDetectionDue(Environment.TickCount)) BeginMonitoringDetection();
        }

        private bool MonitoringDetectionDue(int now)
        {
            return !monitoringDetected || (monitoringComponent != null
                && (monitoringComponent.InstallationInProgress || monitoringComponent.UninstallationInProgress)
                && unchecked((uint)(now - monitoringLastDetectionTick)) >= 5000);
        }

        private void RunMonitoringWork(Action work)
        {
#if PAVISE_SELFTEST
            if (MonitoringWorkForTest == null) throw new InvalidOperationException("Monitoring UI work queue was not mocked");
            MonitoringWorkForTest(work);
#else
            if (!ThreadPool.QueueUserWorkItem(delegate { work(); })) throw new InvalidOperationException("Could not queue component operation");
#endif
        }

        private RtssComponentInfo DetectMonitoringComponent()
        {
#if PAVISE_SELFTEST
            if (MonitoringDetectForTest == null) throw new InvalidOperationException("RTSS detection was not mocked");
            return MonitoringDetectForTest();
#else
            return PrepareMonitoringBackgroundComponent(RtssComponent.Detect());
#endif
        }

        private RtssComponentInfo InstallMonitoringComponent(Action<RtssInstallProgress> progress, CancellationToken cancellation)
        {
#if PAVISE_SELFTEST
            if (MonitoringInstallForTest == null) throw new InvalidOperationException("RTSS installation was not mocked");
            return MonitoringInstallForTest(progress, cancellation);
#else
            return PrepareMonitoringBackgroundComponent(RtssComponent.Install(progress, cancellation));
#endif
        }

        private static RtssComponentInfo PrepareMonitoringBackgroundComponent(RtssComponentInfo component)
        {
            if (component != null && component.Installed && !component.InstallationInProgress
                && !component.UninstallationInProgress && String.IsNullOrEmpty(component.Error))
            {
                string reason;
                if (!RtssStartup.PrepareBackgroundConfiguration(component.Path, out reason))
                    component.Error = reason;
            }
            return component;
        }

        private RtssComponentInfo UninstallMonitoringComponent(Action<RtssInstallProgress> progress, CancellationToken cancellation)
        {
#if PAVISE_SELFTEST
            if (MonitoringUninstallForTest == null) throw new InvalidOperationException("RTSS uninstallation was not mocked");
            return MonitoringUninstallForTest(progress, cancellation);
#else
            return RtssComponent.Uninstall(progress, cancellation);
#endif
        }

        private void PostMonitoringResult(Action result)
        {
            if (runtimeShutdownBusy || IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!runtimeShutdownBusy && !IsDisposed && !Disposing) result(); }); }
            catch (InvalidOperationException) { }
        }

        private bool RejectDataOperationDuringMonitoringInstall()
        {
            if (!monitoringBusy && !MonitoringComponentInstallPending) return false;
            monitoringProgress = MonitoringText("RTSS 组件操作正在进行，请完成或取消后再重试。",
                "An RTSS component operation is in progress. Finish or cancel it before retrying.");
            monitoringProgressError = true;
            if (settingsTabs != null) settingsTabs.Index = 3;
            SyncMonitoringSettings();
            return true;
        }

        private void CancelMonitoringForShutdown()
        {
            CloseMonitoringDesktopPreview();
            // 安装器已运行时等待完成，仅准备和下载可取消。
            if (monitoringInstallCancellation != null && !monitoringComponentLaunched)
                try { monitoringInstallCancellation.Cancel(); } catch (ObjectDisposedException) { }
            monitoringCanCancel = false;
        }

        private void BeginMonitoringDetection()
        {
            if (runtimeShutdownBusy || IsDisposed || Disposing || monitoringBusy || wipeBusy != 0) return;
            monitoringBusy = true;
            monitoringLastDetectionTick = Environment.TickCount;
            monitoringProgressError = false;
            monitoringProgress = MonitoringText("正在检测组件…", "Checking component…");
            SyncMonitoringSettings();
            try
            {
                RunMonitoringWork(delegate {
                    RtssComponentInfo result = null; string error = "";
                    try { result = DetectMonitoringComponent(); } catch (Exception e) { error = e.Message; }
                    PostMonitoringResult(delegate { FinishMonitoringComponent(result, error, false); });
                });
            }
            catch (Exception e) { FinishMonitoringComponent(null, e.Message, false); }
        }

        private void BeginMonitoringInstall()
        {
            if (runtimeShutdownBusy || IsDisposed || Disposing) return;
            if (monitoringInstalling)
            {
                if (monitoringCanCancel && !monitoringComponentLaunched && monitoringInstallCancellation != null)
                {
                    try { monitoringInstallCancellation.Cancel(); } catch (ObjectDisposedException) { }
                    monitoringCanCancel = false;
                    monitoringProgress = MonitoringText("正在取消下载…", "Cancelling download…"); SyncMonitoringSettings();
                }
                return;
            }
            if (monitoringBusy || wipeBusy != 0 || MonitoringComponentInstallPending || (monitoringComponent != null && monitoringComponent.Installed)) return;
            monitoringBusy = monitoringInstalling = true; monitoringCanCancel = false;
            monitoringComponentLaunched = false;
            monitoringProgressError = false; monitoringInstallPhase = "Checking";
            monitoringInstallCancellation = new CancellationTokenSource();
            CancellationTokenSource cancellation = monitoringInstallCancellation;
            monitoringProgress = MonitoringText("准备安装 RTSS…", "Preparing RTSS installation…");
            SyncMonitoringSettings();
            try
            {
                RunMonitoringWork(delegate {
                    RtssComponentInfo result = null; string error = "";
                    try
                    {
                        result = InstallMonitoringComponent(delegate(RtssInstallProgress progress) {
                            if (progress.Phase == "Installing") monitoringComponentLaunched = true;
                            PostMonitoringResult(delegate {
                                if (!monitoringInstalling || monitoringInstallCancellation != cancellation) return;
                                monitoringInstallPhase = progress.Phase;
                                monitoringCanCancel = !monitoringComponentLaunched && !cancellation.IsCancellationRequested
                                    && (progress.Phase == "Downloading" || progress.Phase == "Verifying");
                                monitoringProgress = progress.Detail + (progress.Percent >= 0 ? "  " + Math.Max(0, Math.Min(100, progress.Percent)) + "%" : "");
                                SyncMonitoringSettings();
                            });
                        }, cancellation.Token);
                    }
                    catch (OperationCanceledException) { error = MonitoringText("下载已取消。", "Download cancelled."); }
                    catch (Exception e) { error = e.Message; }
                    PostMonitoringResult(delegate { FinishMonitoringComponent(result, error, true); });
                    cancellation.Dispose();
                });
            }
            catch (Exception e)
            {
                cancellation.Dispose(); FinishMonitoringComponent(null, e.Message, true);
            }
        }

        private void BeginMonitoringUninstall()
        {
            if (runtimeShutdownBusy || IsDisposed || Disposing) return;
            if (monitoringUninstalling)
            {
                if (monitoringCanCancel && !monitoringComponentLaunched && monitoringInstallCancellation != null)
                {
                    try { monitoringInstallCancellation.Cancel(); } catch (ObjectDisposedException) { }
                    monitoringCanCancel = false;
                    monitoringProgress = MonitoringText("正在取消卸载准备…", "Cancelling uninstallation preparation…"); SyncMonitoringSettings();
                }
                return;
            }
            if (monitoringBusy || wipeBusy != 0 || MonitoringComponentInstallPending || monitoringComponent == null || !monitoringComponent.Installed) return;
            if (!MonitoringPreferences.TrySetEnabled(false))
            {
                SetMonitoringResult(false); return;
            }
            monitoringBusy = monitoringUninstalling = monitoringCanCancel = true;
            monitoringComponentLaunched = false;
            monitoringProgressError = false; monitoringInstallPhase = "Checking";
            monitoringMessage = ""; monitoringMessageError = false;
            monitoringInstallCancellation = new CancellationTokenSource();
            CancellationTokenSource cancellation = monitoringInstallCancellation;
            monitoringProgress = MonitoringText("监测已停止，正在准备卸载…", "Monitoring stopped. Preparing removal…");
            SyncMonitoringSettings();
            try
            {
                RunMonitoringWork(delegate {
                    RtssComponentInfo result = null; string error = "";
                    try
                    {
                        result = UninstallMonitoringComponent(delegate(RtssInstallProgress progress) {
                            if (progress.Phase == "Uninstalling") monitoringComponentLaunched = true;
                            PostMonitoringResult(delegate {
                                if (!monitoringUninstalling || monitoringInstallCancellation != cancellation) return;
                                monitoringInstallPhase = progress.Phase;
                                monitoringCanCancel = !monitoringComponentLaunched && !cancellation.IsCancellationRequested
                                    && (progress.Phase == "Checking" || progress.Phase == "Stopping");
                                monitoringProgress = progress.Detail + (progress.Percent >= 0 ? "  " + Math.Max(0, Math.Min(100, progress.Percent)) + "%" : "");
                                SyncMonitoringSettings();
                            });
                        }, cancellation.Token);
                    }
                    catch (OperationCanceledException) { error = MonitoringText("卸载准备已取消。", "Uninstallation preparation cancelled."); }
                    catch (Exception e) { error = e.Message; }
                    PostMonitoringResult(delegate { FinishMonitoringComponent(result, error, false, true); });
                    cancellation.Dispose();
                });
            }
            catch (Exception e)
            {
                cancellation.Dispose(); FinishMonitoringComponent(null, e.Message, false, true);
            }
        }

        private void FinishMonitoringComponent(RtssComponentInfo result, string error, bool installation, bool uninstallation = false)
        {
            bool checkingUninstall = uninstallation || monitoringAwaitingUninstall || (result != null && result.UninstallationInProgress);
            monitoringBusy = monitoringInstalling = monitoringUninstalling = monitoringCanCancel = false; monitoringDetected = true;
            monitoringInstallCancellation = null;
            if (result != null) monitoringComponent = result;
            else if (!installation && !uninstallation) monitoringComponent = null;
            if (result != null && (result.InstallationInProgress || result.UninstallationInProgress))
                monitoringLastDetectionTick = Environment.TickCount;
            if (string.IsNullOrEmpty(error) && result != null) error = result.Error;
            if (checkingUninstall && result != null)
                monitoringAwaitingUninstall = result.UninstallationInProgress || !string.IsNullOrEmpty(error);
            bool uninstalled = checkingUninstall && result != null && !result.Installed
                && !result.InstallationInProgress && !result.UninstallationInProgress && string.IsNullOrEmpty(error);
            // 无关准备失败不能使 RTSS 窗口重新显示。
            if (result != null && result.Installed && !result.InstallationInProgress && !result.UninstallationInProgress)
                RtssTrayIcon.Hide(result);
            if (uninstalled)
            {
                RtssTrayIcon.Forget();
                RtssOverlayClient.ComponentChanged();
                if (MonitoringPreferences.Enabled) MonitoringPreferences.TrySetEnabled(false);
            }
            else if (result != null && result.Installed && !result.InstallationInProgress
                && !result.UninstallationInProgress && string.IsNullOrEmpty(error))
            {
                RtssOverlayClient.ComponentChanged();
                if (MonitoringPreferences.Enabled) PerformanceMonitoring.RefreshConfiguration();
            }
            monitoringProgressError = !string.IsNullOrEmpty(error);
            monitoringProgress = !string.IsNullOrEmpty(error) ? error
                : result != null && result.InstallationInProgress ? MonitoringText("安装仍在进行，将稍后重新检测。", "Installation is in progress. Checking again shortly.")
                : result != null && result.UninstallationInProgress ? MonitoringText("卸载仍在进行，将稍后重新检测。", "Removal is in progress. Checking again shortly.")
                : uninstalled ? (result.RestartRequired
                    ? MonitoringText("RTSS 已卸载，性能监测已关闭；请重启电脑完成剩余清理。", "RTSS uninstalled and monitoring is off; restart your PC to finish cleanup.")
                    : MonitoringText("RTSS 已卸载，性能监测已关闭。", "RTSS uninstalled. Performance monitoring is off."))
                : result != null && result.Installed ? (result.RestartRequired ? MonitoringText("组件安装完成，需要重启电脑。", "Component installed; restart your PC.")
                    : installation ? MonitoringText("组件已就绪，可开启性能监测。", "Component ready. You can enable monitoring.")
                    : result.Path)
                : MonitoringText("尚无可用组件，可点击「下载并安装」。", "No component found. Select Download and install to set it up.");
            SyncMonitoringSettings();
        }
    }
}
