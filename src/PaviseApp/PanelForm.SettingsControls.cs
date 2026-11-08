using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
	internal partial class PanelForm : Form
	{
		private bool QueryStartupTaskState()
		{
			return TaskHelper.IsStartupTaskEnabled();
		}
		private int ChangeStartupTask(bool enabled)
		{
			if (!enabled)
			{
				return TaskHelper.DeleteStartupTask();
			}
			return TaskHelper.CreateStartupTask();
		}
		private long MeasureSettingsShaderCache()
		{
			return ShaderCache.MeasureBytes();
		}
		private CacheSweep.Result CleanSettingsShaderCache()
		{
			return ShaderCache.Clean();
		}
		private bool ConfirmSettingsAction(string key, DlgKind kind)
		{
			return PaviseDialog.Confirm(this, "PAVISE AUTO", Lang.T(key), kind);
		}
		private void QueueUiStateWork(Action work)
		{
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
			BeginInvoke((MethodInvoker)delegate
			{
				result();
			});
		}
		private void WarnStartupTaskFailure(string reason)
		{
			string body = Lang.T("msg.taskfail") + (string.IsNullOrEmpty(reason) ? "" : ("\r\n\r\n" + reason));
			PaviseDialog.Warn(this, "PAVISE AUTO", body);
		}
		internal void BeginRuntimeShutdown(string message)
		{
			CancelConfigurationPresetWork();
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
			bool flag = false;
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
				flag = true;
				return true;
			}
			finally
			{
				if (!flag)
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
			string[] labels = new string[3]
			{
				Lang.T("sec.app"),
				Lang.T("sec.maint"),
				Lang.T("sec.appearance")
			};
			settingsTabs.SetTabs(labels, null);
			pageSettings.Controls.Add(settingsTabs);
			settingsTabPanels = new DBPanel[3];
			for (int i = 0; i < settingsTabPanels.Length; i++)
			{
				settingsTabPanels[i] = new SettingsWorkspaceCategoryPanel
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
				SyncMotionAppearanceActivity();
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
					int num = DsKeys.NavigateHorizontal(key, index2, labels.Length);
					if (num >= 0)
					{
						settingsTabs.Index = num;
						settingsWorkspaceTabs[num].Focus();
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
				if (index == 0)
				{
					RefreshStandbyCleanerPresentation();
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
			return settingsWorkspaceTile2;
		}
		private static SettingsWorkspaceTile SettingsTile(Control panel, string name)
		{
			return panel.Controls[name] as SettingsWorkspaceTile;
		}
		private static int SettingsContentWidth(Control panel)
		{
			return Math.Max(1, panel.Width - SystemInformation.VerticalScrollBarWidth);
		}
		private static string SettingsStandbyCleanerDetail()
		{
			return AutoText("仅对局中达双阈值、间隔约一分钟才清理待机缓存。可能卡顿和重新读盘，不联动计时器或内存压缩。", "Purges standby cache during matches only at both thresholds, at least about a minute apart. May hitch and re-read disk; does not change timers or memory compression.");
		}
		private int StandbyCleanerCardHeight(int width, Control host, int minimum, bool perGame)
		{
			int val = FullTextCardHeight(perGame ? Lang.T("gm.standbycleaner.cfgsub") : SettingsStandbyCleanerDetail(), width, host, minimum);
			val = Math.Max(val, FullTextCardHeight(Lang.T("gm.standbycleaner.needadmin"), width, host, minimum));
			return Math.Max(val, FullTextCardHeight(Lang.T("standbycleaner.config.invalid"), width, host, minimum));
		}
		private bool ConfirmStandbyCleanerEnable()
		{
			if (!elevated)
			{
				return false;
			}
			if (!gameMode.StandbyCleaningOptionsValid)
			{
				PaviseDialog.Warn(this, Lang.T("gm.standbycleaner"), Lang.T("standbycleaner.config.invalid"));
				return false;
			}
			return PaviseDialog.Confirm(this, Lang.T("gm.standbycleaner"), Lang.T("standbycleaner.warn"), DlgKind.Warn);
		}
		private bool EditStandbyCleanerOptions()
		{
			StandbyCleanerOptions standbyCleaningOptions = gameMode.StandbyCleaningOptions;
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
