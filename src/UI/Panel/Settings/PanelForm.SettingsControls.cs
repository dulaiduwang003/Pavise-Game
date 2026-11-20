using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm : Form
    {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal Func<bool> StartupTaskQueryForTest;
        internal Func<bool, int> StartupTaskChangeForTest;
        internal Func<long> ShaderMeasureForTest;
        internal Action<Action> UiStateWorkQueueForTest;
        internal Action<Action> UiStatePostForTest;
        internal Action<string> StartupTaskWarningForTest;
        internal Func<CacheSweep.Result> ShaderCleanForTest;
        internal Func<string, bool> SettingsConfirmationForTest;
        internal Action<bool, string> ShaderCleanupMessageForTest;
        internal Action<string> SettingsOperationWarningForTest;
#endif
        private bool QueryStartupTaskState()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (StartupTaskQueryForTest == null) throw new InvalidOperationException("Startup task query was not mocked");
            return StartupTaskQueryForTest();
#endif
            return TaskHelper.IsStartupTaskEnabled();
        }
        private int ChangeStartupTask(bool enabled)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (StartupTaskChangeForTest == null) throw new InvalidOperationException("Startup task change was not mocked");
            return StartupTaskChangeForTest(enabled);
#endif
            if (!enabled)
            {
                return TaskHelper.DeleteStartupTask();
            }
            return TaskHelper.CreateStartupTask();
        }
        private long MeasureSettingsShaderCache()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (ShaderMeasureForTest == null) throw new InvalidOperationException("Shader cache query was not mocked");
            return ShaderMeasureForTest();
#endif
            return ShaderCache.MeasureBytes();
        }
        private CacheSweep.Result CleanSettingsShaderCache()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (ShaderCleanForTest == null) throw new InvalidOperationException("Shader cache cleanup was not mocked");
            return ShaderCleanForTest();
#endif
            return ShaderCache.Clean();
        }
        private bool ConfirmSettingsAction(string key, DlgKind kind)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (SettingsConfirmationForTest == null) throw new InvalidOperationException("Settings confirmation was not mocked");
            return SettingsConfirmationForTest(key);
#endif
            return PaviseDialog.Confirm(this, "PAVISE AUTO", Lang.T(key), kind);
        }
        private void QueueUiStateWork(Action work)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (UiStateWorkQueueForTest != null) { UiStateWorkQueueForTest(work); return; }
#endif
            if (!ThreadPool.QueueUserWorkItem(delegate
            {
                work();
            }))
            {
                throw new InvalidOperationException("UI state work could not be queued");
            }
        }
        private void PostUiStateResult(Action result)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (UiStatePostForTest != null) { UiStatePostForTest(result); return; }
#endif
            BeginInvoke((MethodInvoker)delegate
            {
                result();
            });
        }
        private void WarnStartupTaskFailure(string reason)
        {
            string body = Lang.T("msg.taskfail") + (string.IsNullOrEmpty(reason) ? "" : ("\r\n\r\n" + reason));
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (StartupTaskWarningForTest == null) throw new InvalidOperationException("Startup task warning was not mocked");
            StartupTaskWarningForTest(body);
            return;
#endif
            PaviseDialog.Warn(this, "PAVISE AUTO", body);
        }
        internal void BeginRuntimeShutdown(string message)
        {
            GameExtension.NotifySession(null, 0, 0, false);
            if (!base.IsDisposed)
            {
                if (!runtimeShutdownBusy)
                {
                    runtimeShutdownBusy = true;
                    base.FormClosing += PreventShutdownClose;
                }

                RealExit = true;
                auditCancelRequested = true;
                Interlocked.Increment(ref slowVersion);
                uiActive = false;
                uiActivityKnown = true;
                if (uiTimer != null)
                {
                    uiTimer.Stop();
                }
                if (fitTimer != null)
                {
                    fitTimer.Stop();
                }
                CancelAutoHide();
                StopPageReveal();
                if (paviseCore != null)
                {
                    paviseCore.SetAnimationEnabled(false);
                }
                if (root != null)
                {
                    root.Enabled = false;
                }
                Cursor = Cursors.WaitCursor;
                ShowDataOperationProgress(message);
                SetResetProgress(message);
            }
        }
        private void PreventShutdownClose(object sender, FormClosingEventArgs args)
        {
            if (runtimeShutdownBusy && args.CloseReason != CloseReason.WindowsShutDown)
            {
                args.Cancel = true;
            }
        }
        internal void FinishRuntimeShutdown()
        {
            runtimeShutdownBusy = false;
            base.FormClosing -= PreventShutdownClose;
        }
        internal void SetResetProgress(string message)
        {
            if (!base.IsDisposed)
            {
                if (dataOperationPanel != null && !dataOperationPanel.IsDisposed)
                {
                    dataOperationPanel.Status.Text = message;
                }
                if (lblSub != null && !lblSub.IsDisposed)
                {
                    lblSub.Text = message;
                    lblSub.ForeColor = Theme.Warning;
                }
            }
        }
        private void SetResetActionsEnabled(bool enabled)
        {
            if (btnWipeAll != null && !btnWipeAll.IsDisposed)
            {
                btnWipeAll.Enabled = enabled;
            }
            if (btnUninstall != null && !btnUninstall.IsDisposed)
            {
                btnUninstall.Enabled = enabled;
            }
        }
        private bool CanBeginReset(string inGameMessage, string confirmation)
        {
            if (runtimeShutdownBusy || Interlocked.CompareExchange(ref wipeBusy, 1, 0) != 0)
            {
                return false;
            }
            bool resetAdmitted = false;
            try
            {
                SetResetActionsEnabled(false);
                if (ShaderCleanupBusy)
                {
                    ShowSettingsOperationWarning(PerformancePolicy.Text("着色器缓存正在清理，请完成后重试", "Shader cleanup is in progress; retry when it finishes"));
                    return false;
                }
                if (gameMode.IsActive)
                {
                    ShowSettingsOperationWarning(Lang.T(inGameMessage));
                    return false;
                }
                if (!ConfirmSettingsAction(confirmation, DlgKind.Danger))
                {
                    return false;
                }
                if (gameMode.IsActive)
                {
                    ShowSettingsOperationWarning(Lang.T(inGameMessage));
                    return false;
                }
                if (runtimeShutdownBusy || ShaderCleanupBusy)
                {
                    return false;
                }
                Cursor = Cursors.WaitCursor;
                resetAdmitted = true;
                return true;
            }
            finally
            {
                if (!resetAdmitted)
                {
                    Interlocked.Exchange(ref wipeBusy, 0);
                    if (!runtimeShutdownBusy)
                    {
                        SetResetActionsEnabled(true);
                    }
                }
            }
        }
        private void FinishSlowStateRefresh()
        {
            Interlocked.Exchange(ref slowBusy, 0);
            if (Interlocked.Exchange(ref slowPending, 0) != 0 && !base.IsDisposed && UiActive)
            {
                RefreshSlowStateAsync();
            }
        }
        private void InitializeSettingsWorkspace()
        {
            pageSettings.AutoScroll = false;
            settingsWorkspaceTabs.Clear();
            settingsWorkspaceViewport = new DBPanel
            {
                Name = "SettingsWorkspaceViewport",
                AutoScroll = false,
                BackColor = Theme.Bg
            };
            pageSettings.Controls.Add(settingsWorkspaceViewport);
            settingsWorkspaceSurface = new SettingsWorkspaceSurface
            {
                Name = "SettingsWorkspaceConsole",
                BackColor = Theme.Card
            };
            settingsWorkspaceViewport.Controls.Add(settingsWorkspaceSurface);
            settingsTabs = new TechTabs
            {
                Name = "SettingsTabState",
                Visible = false,
                TabStop = false
            };
            string[] labels = new string[4]
            {
                Lang.T("sec.app"),
                Lang.T("sec.maint"),
                Lang.T("sec.appearance"),
                Lang.T("nav.about")
            };
            settingsTabs.SetTabs(labels, null);
            pageSettings.Controls.Add(settingsTabs);
            settingsTabPanels = new DBPanel[labels.Length];
            for (int i = 0; i < settingsTabPanels.Length; i++)
            {
                settingsTabPanels[i] = i == 3 ? pageAbout : new SettingsWorkspaceCategoryPanel
                {
                    Visible = (i == 0)
                };
            }
            pageTabPanels[pageSettings] = settingsTabPanels;
            settingsTabs.IndexChanged = delegate(int index)
            {
                for (int l = 0; l < settingsTabPanels.Length; l++)
                {
                    DBPanel dBPanel2 = settingsTabPanels[l];
                    if (dBPanel2.Visible && l != index)
                    {
                        tabScrollPositions[dBPanel2] = new Point(0, -dBPanel2.AutoScrollPosition.Y);
                    }
                    dBPanel2.Visible = l == index;
                }
                Point value;
                if (tabScrollPositions.TryGetValue(settingsTabPanels[index], out value))
                {
                    settingsTabPanels[index].AutoScrollPosition = new Point(0, value.Y);
                }
            };
            for (int j = 0; j < settingsTabPanels.Length; j++)
            {
                DBPanel dBPanel = settingsTabPanels[j];
                dBPanel.Parent = settingsWorkspaceSurface;
                dBPanel.BackColor = Theme.Card;
                dBPanel.Name = "SettingsWorkspaceCategory" + j;
                dBPanel.AutoScroll = true;
                Native.Dark(dBPanel);
                int index2 = j;
                SettingsWorkspaceTab settingsWorkspaceTab = new SettingsWorkspaceTab(labels[j]);
                settingsWorkspaceTab.Name = "SettingsCategory" + j;
                settingsWorkspaceTab.Selected = j == 0;
                settingsWorkspaceTab.TabIndex = j;
                SettingsWorkspaceTab settingsWorkspaceTab2 = settingsWorkspaceTab;
                settingsWorkspaceTab2.AccessibleName = labels[j];
                settingsWorkspaceTab2.Click += delegate
                {
                    settingsTabs.Index = index2;
                };
                settingsWorkspaceTab2.NavigationRequested = delegate(Keys key)
                {
                    int indexValue = DsKeys.NavigateHorizontal(key, index2, labels.Length);
                    if (indexValue >= 0)
                    {
                        settingsTabs.Index = indexValue;
                        settingsWorkspaceTabs[indexValue].Focus();
                    }
                };
                settingsWorkspaceSurface.Controls.Add(settingsWorkspaceTab2);
                settingsWorkspaceTabs.Add(settingsWorkspaceTab2);
            }
            TechTabs techTabs = settingsTabs;
            techTabs.IndexChanged = (Action<int>)Delegate.Combine(techTabs.IndexChanged, (Action<int>)delegate(int index)
            {
                for (int k = 0; k < settingsWorkspaceTabs.Count; k++)
                {
                    settingsWorkspaceTabs[k].Selected = k == index;
                    settingsWorkspaceTabs[k].AccessibleDescription = ((k == index) ? PerformancePolicy.Text("已选中", "Selected") : "");
                    settingsWorkspaceTabs[k].Invalidate();
                }
                if (index == 3)
                {
                    RefreshAboutIcon();
                    RefreshAboutUpdate();
                }
                LayoutSettingsWorkspace();
            });
            pageSettings.SizeChanged += delegate
            {
                LayoutSettingsWorkspace();
            };
        }
        private SettingsWorkspaceTile AddSettingsTile(Control parent, string name, string title, string detail, string icon, Control host)
        {
            SettingsWorkspaceTile settingsWorkspaceTile = new SettingsWorkspaceTile();
            settingsWorkspaceTile.Name = name;
            settingsWorkspaceTile.Title = title;
            settingsWorkspaceTile.Desc = detail;
            settingsWorkspaceTile.IconKey = icon;
            settingsWorkspaceTile.CompactRow = true;
            SettingsWorkspaceTile settingsWorkspaceTile2 = settingsWorkspaceTile;
            if (host != null)
            {
                settingsWorkspaceTile2.Host(host);
                host.TabIndex = 0;
                host.AccessibleName = host.AccessibleName ?? title;
            }
            parent.Controls.Add(settingsWorkspaceTile2);
            settingsWorkspaceTile2.PresentationChanged = LayoutSettingsWorkspace;
            return settingsWorkspaceTile2;
        }
        private static SettingsWorkspaceTile SettingsTile(Control panel, string name)
        {
            return panel.Controls[name] as SettingsWorkspaceTile;
        }
        private static int SettingsContentWidth(Control panel)
        {
            int width = panel.ClientSize.Width;
            ScrollableControl scroll = panel as ScrollableControl;
            // 纵向滚动条可能在高度收紧后才出现 先预留其宽度 避免正文再次挤出横向滚动
            if (scroll != null && scroll.AutoScroll && !scroll.VerticalScroll.Visible)
            {
                width -= SystemInformation.VerticalScrollBarWidth;
            }
            return Math.Max(1, width);
        }
        private static string StandbyCleanerDetail()
        {
            return AutoText("仅对局中达双阈值、间隔约一分钟才清理待机缓存。可能卡顿和重新读盘，不联动计时器或内存压缩。", "Purges standby cache during matches only at both thresholds, at least about a minute apart. May hitch and re-read disk; does not change timers or memory compression.");
        }
#if PAVISE_SELFTEST
        internal Func<bool> StandbyCleanerConfirmationForTest;
        internal Action StandbyCleanerInvalidOptionsForTest;
        internal Func<StandbyCleanerOptions, StandbyCleanerOptions> StandbyCleanerOptionsEditorForTest;
        internal Action StandbyCleanerSaveFailureForTest;
#endif
        private bool ConfirmStandbyCleanerEnable()
        {
            if (!elevated)
            {
                return false;
            }
            if (!gameMode.StandbyCleaningOptionsValid)
            {
#if PAVISE_SELFTEST
                if (StandbyCleanerInvalidOptionsForTest != null) StandbyCleanerInvalidOptionsForTest();
                else
#endif
                PaviseDialog.Warn(this, Lang.T("gm.standbycleaner"), Lang.T("standbycleaner.config.invalid"));
                return false;
            }
#if PAVISE_SELFTEST
            if (StandbyCleanerConfirmationForTest != null) return StandbyCleanerConfirmationForTest();
#endif
            return PaviseDialog.Confirm(this, Lang.T("gm.standbycleaner"), Lang.T("standbycleaner.warn"), DlgKind.Warn);
        }
        private bool EditStandbyCleanerOptions()
        {
            StandbyCleanerOptions standbyCleaningOptions = gameMode.StandbyCleaningOptions;
#if PAVISE_SELFTEST
            if (StandbyCleanerOptionsEditorForTest != null)
                return ApplyStandbyCleanerOptions(StandbyCleanerOptionsEditorForTest(standbyCleaningOptions));
#endif
            using (StandbyCleanerOptionsDialog dlg = new StandbyCleanerOptionsDialog(standbyCleaningOptions, ApplyStandbyCleanerOptions, !gameMode.StandbyCleaningOptionsValid))
            {
                return ShowDim(dlg) == DialogResult.OK;
            }
        }
        private bool ApplyStandbyCleanerOptions(StandbyCleanerOptions value)
        {
            if (value == null || !value.IsValid || !gameMode.TrySetStandbyCleanerOptions(value))
            {
                return false;
            }
            RefreshStandbyCleanerPresentation();
            return true;
        }
    }
}
