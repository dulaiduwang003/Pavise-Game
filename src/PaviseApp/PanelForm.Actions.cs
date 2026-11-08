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
		private void ShowReleaseNotes()
		{
			if (releaseNotesShowing || base.IsDisposed || base.Disposing) return;
			ReleaseNote current = ReleaseNotes.Current;
			if (current != null)
			{
				releaseNotesShowing = true;
				try
				{
					StringBuilder stringBuilder = new StringBuilder();
					stringBuilder.Append(Lang.F("notes.sub", current.Tag)).Append("  |  ").Append(current.Date)
						.AppendLine()
						.AppendLine();
					for (int i = 0; i < current.Count; i++)
					{
						stringBuilder.Append(i + 1).Append(". ").Append(current.Item(i))
							.AppendLine()
							.AppendLine();
					}
					PaviseDialog.Show(this, Lang.T("notes.title") + "  " + current.Tag, stringBuilder.ToString().TrimEnd(), null, 640);
					// A failed acknowledgement retries next launch, not every tray activation.
					releaseNotesAutoHandled = true;
					if (!ReleaseNotes.MarkSeen()) Logger.Warn("Release notes shown; version acknowledgement could not be saved");
				}
				finally { releaseNotesShowing = false; }
			}
		}
		private void ShowAcHelp()
		{
			ShowAcInformation(Lang.T("v14.anticheat"), Lang.T("v14.anticheat.groups.sub") + "\r\n\r\n" + Lang.T("ac.mode." + AntiCheatModes.Token(tamer.Mode) + ".d") + "\r\n\r\n" + Lang.T("ac.mode.disclaimer"));
		}
		private void ShowAcInformation(string title, string detail)
		{
			PaviseDialog.Info(this, title, detail);
		}
		private void OpenAcDrawer(string kind)
		{
			if (kind != "roster" && kind != "records")
			{
				return;
			}
			if (acDrawerKind != kind)
			{
				acDrawerBody.AutoScrollPosition = Point.Empty;
			}
			acDrawerKind = kind;
			acOtherOpen = kind == "records";
			if (kind == "roster")
			{
				SyncAcRoster();
			}
			acDrawerTitle.Text = AutoText((kind == "roster") ? "兼容名单" : "兼容与记录", (kind == "roster") ? "Compatibility list" : "Compatibility & records");
			acDrawer.Visible = true;
			acList.Visible = false;
			acClear.Visible = kind == "roster";
			lblAcRoster.Visible = kind == "roster";
			foreach (Control acReadOnlyCard in acReadOnlyCards)
			{
				acReadOnlyCard.Visible = kind == "records";
			}
			foreach (Control acRosterRow in acRosterRows)
			{
				acRosterRow.Visible = kind == "roster";
			}
			LayoutAntiCheatViewport();
			acDrawer.BringToFront();
			acDrawerClose.Focus();
		}
		private void OnClearRoster()
		{
			if (ProtectedGameRoster.Names().Length != 0 && PaviseDialog.Confirm(this, Lang.T("ac.roster"), Lang.T("ac.roster.confirm"), DlgKind.Warn))
			{
				ProtectedGameRoster.Clear();
				SyncAcRoster();
			}
		}
		private void ShowAuditIdle()
		{
			auditScan.SetIdle(Lang.T("audit.idle.title"), Lang.F("audit.idle.hint", 120));
			auditScan.Visible = true;
			auditScroll.Visible = false;
			SetToolbarVisible(false);
			int width = Theme.S(184);
			int height = Theme.S(38);
			btnAuditStart.SetBounds((auditScan.Width - width) / 2, auditScan.ContentBottom + Theme.S(20), width, height);
			btnAuditStart.Visible = true;
			btnAuditStart.BringToFront();
			LayoutAuditViewport();
		}
		private void StartAudit()
		{
			if (runtimeShutdownBusy || base.IsDisposed || Interlocked.CompareExchange(ref auditBusy, 1, 0) != 0)
			{
				return;
			}
			int version = Interlocked.Increment(ref auditRunVersion);
			auditCancelRequested = false;
			ShowAuditRunning();
			try
			{
				QueueUiStateWork(delegate
				{
					AuditReport report = null;
					try
					{
						Func<bool> cancelIrq = () => auditCancelRequested || base.IsDisposed || runtimeShutdownBusy || (gameMode != null && gameMode.IsActive);
						Action<float, string> progress = delegate(float ratio, string caption)
						{
							ReportAuditProgressForRun(version, ratio, caption);
						};
						report = SystemAudit.Collect(120000, cancelIrq, progress);
					}
					catch
					{
					}
					try
					{
						PostUiStateResult(delegate
						{
							CompleteAudit(version, report);
						});
					}
					catch
					{
						if (version == Volatile.Read(ref auditRunVersion))
						{
							Interlocked.Exchange(ref auditBusy, 0);
						}
					}
				});
			}
			catch
			{
				CompleteAudit(version, null);
			}
		}
		private void ShowAuditRunning()
		{
			lblAuditStatus.Visible = true;
			SetAuditButtons(false);
			btnAuditFixAll.Visible = false;
			btnAuditCancel.Visible = true;
			btnAuditCancel.Enabled = !auditCancelRequested;
			btnAuditStart.Visible = false;
			auditScroll.Visible = false;
			auditScan.Visible = true;
			auditScan.BeginScan(auditCancelRequested ? Lang.T("irqcheck.dialog.stopping") : Lang.F("audit.phase.sampling", 120));
			lblAuditStatus.Text = "";
			LayoutAuditViewport();
		}
		private void OnAuditFixClick(AuditFix fix)
		{
			if (auditBusy != 0)
			{
				return;
			}
			if (fix.Open != null && !fix.CanFix() && !fix.CanRevert())
			{
				fix.Open();
				return;
			}
			if (!elevated)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.needadmin"));
				return;
			}
			bool canFix = fix.CanFix();
			bool matchesCanRevert = !canFix && fix.CanRevert();
			if (!canFix && !matchesCanRevert)
			{
				RefreshAuditAfterFix();
			}
			else
			{
				if (!matchesCanRevert && fix.ConfirmKey != null && !PaviseDialog.Confirm(this, "PAVISE AUTO", Lang.T(fix.ConfirmKey), DlgKind.Warn))
				{
					return;
				}
				try
				{
					if (matchesCanRevert)
					{
						fix.Revert();
					}
					else
					{
						fix.Fix();
					}
				}
				catch
				{
				}
				Logger.Log(matchesCanRevert ? Lang.T("log.panelformauditpage.1") : Lang.T("log.panelformauditpage.2"));
				RefreshAuditAfterFix();
			}
		}
		private void OnAuditFixAllClick()
		{
			if (auditBusy != 0)
			{
				return;
			}
			if (!elevated)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.needadmin"));
				return;
			}
			int processedCount = 0;
			foreach (AuditFix value in BuildAuditFixes().Values)
			{
				try
				{
					if (value.CanFix() && (value.ConfirmKey == null || PaviseDialog.Confirm(this, "PAVISE AUTO", Lang.T(value.ConfirmKey), DlgKind.Warn)))
					{
						value.Fix();
						processedCount++;
					}
				}
				catch
				{
				}
			}
			Logger.Log((processedCount > 0) ? (Lang.T("log.panelformauditpage.3") + processedCount + Lang.T("log.panelformauditpage.4")) : Lang.T("log.panelformauditpage.5"));
			RefreshAuditAfterFix();
		}
		private void ShowAutoIgnoreDialog()
		{
			if (autoIgnoreDialogOpen || base.IsDisposed || base.Disposing)
			{
				return;
			}
			autoIgnoreDialogOpen = true;
			try
			{
				using (AutoIgnoreDialog dlg = new AutoIgnoreDialog(gameMode))
				{
					ShowDim(dlg);
				}
			}
			finally
			{
				autoIgnoreDialogOpen = false;
			}
		}
		private void ShowPerformanceSystemTab()
		{
			if (performanceTabs != null)
			{
				performanceTabs.Index = 3;
				performanceTabs.SnapToSelection();
			}
		}
		private void ShowManualPerformanceSaveFailure()
		{
			PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
		}
		private void OpenGameCoreScheduling(string profileId)
		{
			coreProfileId = profileId;
			if (CorePageProfile() == null)
			{
				coreProfileId = null;
				return;
			}
			nav.Select(12);
			coreScrollPanel.AutoScrollPosition = Point.Empty;
			SyncCorePage();
		}
		private void ShowCoreInformation(string title, string body)
		{
			PaviseDialog.Info(this, title, body);
		}
		private void ShowDataOperationProgress(string message)
		{
			if (root == null || base.IsDisposed)
			{
				return;
			}
			if (dataOperationPanel == null)
			{
				dataOperationPanel = new DataOperationProgressCard();
				base.Controls.Add(dataOperationPanel);
				base.Resize += delegate
				{
					PositionDataOperationProgress();
				};
			}
			dataOperationPanel.Status.Text = message;
			PositionDataOperationProgress();
			dataOperationPanel.BringToFront();
		}
		private void OnGameLowLatencyToggle(object sender, EventArgs e)
		{
			bool setGameLowLatencySucceeded = gameMode.SetGameLowLatency(swGameLowLatency.Checked);
			SyncGameLowLatencyControl();
			if (!setGameLowLatencySucceeded)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("set.lowlatency.savefail"));
			}
		}
		private void OnAmdSamToggle(object s, EventArgs e)
		{
			EnvironmentGraphicsObservation snapshot = environmentObservation.Snapshot;
            if (RequireElevationFor(swAmdSam, snapshot != null && snapshot.AmdReadable && snapshot.AmdOn))
			{
				bool success = swAmdSam.Checked ? AmdSamTweak.Enable() : AmdSamTweak.Disable();
				QueueEnvironmentObservation(true);
				SyncEnvStatus();
                if (!success) PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.graphics.failed"));
			}
		}
		private void OnHagsToggle(object s, EventArgs e)
		{
			if (RequireElevationFor(swHags, CachedHagsPresentation().Checked))
			{
				if (swHags.Checked ? HagsTweak.Enable() : HagsTweak.Disable())
				{
					PaviseDialog.Info(this, "PAVISE AUTO", Lang.T("hags.reboot"));
				}
                else PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.graphics.failed"));
				QueueEnvironmentObservation(true);
				SyncHagsPresentation();
			}
		}
		private void OnEnvironmentGameDvrToggle(object sender, EventArgs e)
		{
			bool setGameDvrSuppressionSucceeded = gameMode.SetGameDvrSuppression(swEnvironmentGameDvr.Checked);
			SyncEnvironmentGameDvr();
			if (!setGameDvrSuppressionSucceeded)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
			}
		}
		private void ShowQuantumProfileSetting()
		{
			ShowPage(5);
			SettingCard settingCard = cardPerformanceEnvironmentQuantum;
			if (settingCard != null)
			{
				ScrollableControl scrollableControl = settingCard.Parent as ScrollableControl;
				PerformanceFeatureGrid performanceFeatureGrid = scrollableControl as PerformanceFeatureGrid;
				if (performanceFeatureGrid != null)
				{
					performanceFeatureGrid.Reveal(settingCard);
				}
				else if (scrollableControl != null)
				{
					scrollableControl.ScrollControlIntoView(settingCard);
				}
				if (swPerformanceEnvironmentQuantum != null && swPerformanceEnvironmentQuantum.CanFocus)
				{
					swPerformanceEnvironmentQuantum.Focus();
				}
			}
		}
		private void OnIrqCheck(object sender, EventArgs e)
		{
			RunIrqCheck();
		}
		private void OnIrqRestore(object sender, EventArgs e)
		{
			if (!Native.IsElevated())
			{
				return;
			}
			if (gameMode.IsActive)
			{
				PaviseDialog.Warn(this, Lang.T("nav.audit"), Lang.T("irqcheck.ingame"));
			}
			else
			{
				if (!PaviseDialog.Confirm(this, Lang.T("irqcheck.restore.btn"), Lang.T("irqcheck.restore.confirm"), DlgKind.Warn))
				{
					return;
				}
				bool onIrqRestoreReady = true;
				try
				{
					if (GpuIrqCheck.HasResidue)
					{
						onIrqRestoreReady &= GpuIrqCheck.RestoreAll();
					}
				}
				catch
				{
					onIrqRestoreReady = false;
				}
				try
				{
					if (IrqRelocate.HasResidue || IrqPriorityTweak.HasResidue)
					{
						IrqRestoreResult irqRestoreResult = IrqRelocate.RestoreDevices(null);
						onIrqRestoreReady &= irqRestoreResult != null && irqRestoreResult.Success;
					}
				}
				catch
				{
					onIrqRestoreReady = false;
				}
				if (onIrqRestoreReady)
				{
					PaviseDialog.Success(this, Lang.T("irqcheck.restore.btn"), Lang.T("irqcheck.restore.done"));
				}
				else
				{
					PaviseDialog.Warn(this, Lang.T("irqcheck.restore.btn"), Lang.T("irqcheck.restore.failed"));
				}
			}
		}
		private void ShowLibraryMore(GameLibraryItem selected, Control anchor)
		{
			GameCardMenuButton gameCardMenuButton = anchor as GameCardMenuButton;
			if (selected != null && selected.Profile != null && gameCardMenuButton != null && !gameCardMenuButton.IsDisposed)
			{
				gameCardMenuButton.ShowMenu(BuildLibraryMoreMenu(selected));
			}
		}
		private void ShowAddGameDialog()
		{
			ShowAddGameDialog(null);
		}
		private void ShowAddGameDialog(IEnumerable<string> seeds)
		{
			List<string> list = new List<string>();
			foreach (GameProfile profile in gameMode.GetProfiles())
			{
				if (!string.IsNullOrEmpty(profile.ExecutablePath))
				{
					list.Add(profile.ExecutablePath);
				}
				if (!string.IsNullOrEmpty(profile.LearnedExecutablePath))
				{
					list.Add(profile.LearnedExecutablePath);
				}
			}
			using (AddGameDialog addGameDialog = new AddGameDialog(list, gameMode.ActiveGame == null, seeds))
			{
				if (ShowDim(addGameDialog) != DialogResult.OK || addGameDialog.Selected.Count == 0)
				{
					return;
				}
				string lastError;
				int addScannedGamesResult = gameMode.AddScannedGames(addGameDialog.Selected, out lastError);
				if (!gameMode.ProfileStoreSaveFailed)
				{
					RefreshGames();
					if (addScannedGamesResult > 0)
					{
						Logger.Log(Lang.F("scan.added", addScannedGamesResult));
					}
					else if (lastError != null)
					{
						PaviseDialog.Warn(this, "PAVISE AUTO", lastError);
					}
				}
			}
		}
		private void OpenLibraryConfiguration(GameLibraryItem requested, Control anchor)
		{
			if (requested == null || requested.Profile == null)
			{
				return;
			}
			if (libraryConfigurationDialog != null && !libraryConfigurationDialog.IsDisposed)
			{
				libraryConfigurationDialog.Activate();
				return;
			}
			string id = requested.Profile.Id;
			GameLibraryItem gameLibraryItem = ConfigurationItem(id);
			if (gameLibraryItem == null)
			{
				RefreshGames();
				return;
			}
			libraryConfigurationCoreTarget = null;
			using (GameConfigurationDialog dlg = new GameConfigurationDialog(gameLibraryItem, new LibraryGameConfiguration(this, gameLibraryItem), GameIcon(gameLibraryItem.Profile.ExecutablePath)))
			{
				libraryConfigurationDialog = dlg;
				try
				{
					ShowDim(dlg);
				}
				finally
				{
					libraryConfigurationDialog = null;
				}
			}
			string libraryConfigurationCoreTargetSnapshot = libraryConfigurationCoreTarget;
			libraryConfigurationCoreTarget = null;
			if (base.IsDisposed || base.Disposing)
			{
				return;
			}
			if (!string.IsNullOrEmpty(libraryConfigurationCoreTargetSnapshot))
			{
				OpenGameCoreScheduling(libraryConfigurationCoreTargetSnapshot);
			}
			else if (curPage == pageLibrary)
			{
				SelectProfile(id);
				if (anchor != null && !anchor.IsDisposed && anchor.CanFocus)
				{
					anchor.Focus();
				}
				else if (lstGames != null && !lstGames.IsDisposed)
				{
					lstGames.Focus();
				}
			}
		}
		private void OpenLibraryCoreScheduling(string id)
		{
			if (libraryConfigurationDialog != null && !libraryConfigurationDialog.IsDisposed && libraryConfigurationDialog.Visible)
			{
				libraryConfigurationCoreTarget = id;
				libraryConfigurationDialog.Close();
			}
			else
			{
				OpenGameCoreScheduling(id);
			}
		}
		private void OnLogWritesToggle(object s, EventArgs e)
		{
			if (!base.IsDisposed && swLogWrites != null && !swLogWrites.IsDisposed)
			{
				bool @checked = swLogWrites.Checked;
				if (!@checked)
				{
					Logger.Log(Lang.T("log.logwrites.off"));
				}
				Logger.WritesEnabled = @checked;
				Settings.Save("LogWritesEnabled", @checked);
				if (@checked)
				{
					Logger.Log(Lang.T("log.logwrites.on"));
				}
				swLogWrites.SetSilently(Settings.Load("LogWritesEnabled", true));
				RefreshLog(true);
			}
		}
		private void StartManualMemoryCleanup()
		{
			if (!elevated || manualMemoryDisposed || base.IsDisposed || base.Disposing || !base.IsHandleCreated || Interlocked.CompareExchange(ref manualMemoryPending, 1, 0) != 0)
			{
				return;
			}
			int gameProtection = gameMode == null || gameMode.IsActive ? 1 : 0;
			Func<bool> gamePresent = delegate
			{
				if (Volatile.Read(ref gameProtection) != 0) return true;
				bool protect;
				try { protect = gameMode == null || gameMode.MemoryCleanupGamePresent(); }
				catch { protect = true; }
				if (protect) Interlocked.Exchange(ref gameProtection, 1);
				return protect;
			};
			IManualMemoryNative native = new WindowsManualMemoryNative(gamePresent);
			Action<int> wait = null;
			manualMemoryReport = null;
			manualMemoryProgressNumber = 1;
			manualMemoryProgressStep = gameMode.IsActive ? ManualMemoryStep.PaviseMemory : ManualMemoryCleaner.OrderedSteps[0];
			overviewMemoryCleanup.ShowProgress(1, manualMemoryProgressStep);
			Action work = delegate
			{
				try
				{
					ManualMemoryReport report = new ManualMemoryCleaner(native, wait, gamePresent).Run(() => !manualMemoryDisposed, delegate(int number, ManualMemoryStep step)
					{
						PostManualMemoryUi(delegate
						{
							manualMemoryProgressNumber = number;
							manualMemoryProgressStep = step;
							overviewMemoryCleanup.ShowProgress(number, step);
						});
					});
					PostManualMemoryUi(delegate
					{
						manualMemoryReport = report;
						overviewMemoryCleanup.ShowReport(report);
						Interlocked.Exchange(ref manualMemoryPending, 0);
						overviewNextTelemetry = 0L;
						QueueOverviewTelemetry(true);
					});
				}
				catch
				{
					PostManualMemoryUi(delegate
					{
						overviewMemoryCleanup.ShowError(ManualMemoryText.T("清理未完成 · 可重试", "Cleanup failed · retry available", "未完了 · 再試行できます"));
						Interlocked.Exchange(ref manualMemoryPending, 0);
					});
				}
			};
			try
			{
				if (!ThreadPool.QueueUserWorkItem(delegate
				{
					work();
				}))
				{
					throw new InvalidOperationException("Cannot queue manual memory cleanup");
				}
			}
			catch
			{
				Interlocked.Exchange(ref manualMemoryPending, 0);
				overviewMemoryCleanup.ShowError(ManualMemoryText.T("无法启动清理 · 可重试", "Cannot start cleanup · retry available", "開始できません · 再試行できます"));
			}
		}
		private void ShowDonate()
		{
			if (donateLoading || base.IsDisposed)
			{
				return;
			}
			DonateInfo effective = DonateCache.Effective;
			bool hasImage = DonateCache.HasImage;
			if (hasImage && !DonateCache.NeedsRefresh(DonateCache.CachedId, effective.Id, true))
			{
				OpenDonate(DonateCache.LoadCached(), false);
				return;
			}
			donateLoading = true;
			RefreshDonateButton();
			try
			{
				FetchDonateImage(delegate(byte[] bytes, DonateInfo info)
				{
					try
					{
						PostUiStateResult(delegate
						{
							CompleteDonate(bytes, info);
						});
					}
					catch
					{
						donateLoading = false;
					}
				});
			}
			catch
			{
				CompleteDonate(null, null);
				return;
			}
			OpenDonate(hasImage ? DonateCache.LoadCached() : null, true);
		}
		private void OpenDonate(Bitmap qr, bool loading)
		{
			using (DonateDialog donateDialog = new DonateDialog(qr, loading))
			{
				this.donateDialog = donateDialog;
				try
				{
					donateDialog.ShowDialog(this);
				}
				finally
				{
					this.donateDialog = null;
				}
			}
		}
		private void OpenExternal(string url)
		{
			if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			try
			{
				using (Process.Start(url))
				{
				}
			}
			catch
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("v211.link.failed"));
			}
		}
		private void ShowShaderCleanupMessage(bool failed, string message)
		{
			if (failed)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", message);
			}
			else
			{
				PaviseDialog.Info(this, "PAVISE AUTO", message);
			}
		}
		private void OnBackdropPick()
		{
			if (Backdrop.Choose(this))
			{
				SyncBackdropAppearance();
				RefreshBackdrop();
			}
		}
		private void OnBackdropReset()
		{
			if (Backdrop.Active)
			{
				Backdrop.Clear();
				SyncBackdropAppearance();
				RefreshBackdrop();
			}
		}
		private void OnBackdropDim(int index)
		{
			if (index <= 0)
			{
				Backdrop.Clear();
			}
			else
			{
				Backdrop.Dim = index - 1;
			}
			SyncBackdropAppearance();
			RefreshBackdrop();
		}
		private void OnModeColorPick(PerformancePreset mode, Color color)
		{
			if (color.IsEmpty)
			{
				Theme.ClearModeColorOverride(mode);
				Settings.SaveStr("ModeAccent" + (int)mode, "");
			}
			else
			{
				Theme.SetModeColorOverride(mode, color);
				Settings.SaveStr("ModeAccent" + (int)mode, Col.ToHex(color));
			}
			bool hasModeColorOverride = Theme.HasModeColorOverride(mode);
			Color b = Theme.ModeColor(mode);
			foreach (ColorSwatch item in modeSwatches[(int)mode])
			{
				item.Selected = (item.IsDefault ? (!hasModeColorOverride) : (hasModeColorOverride && SameRgb(item.Swatch, b)));
				item.Invalidate();
			}
			RefreshModeAccentLabels();
			if (motionAppearance != null && !motionAppearance.IsDisposed) motionAppearance.RefreshPalette();
			if (modeVisualInitialized && mode == visualMode)
			{
				using (Icon runtimeIcon = IconArt.MakeMultiIcon(visualMode, visualEnabled))
				{
					SetRuntimeIcon(runtimeIcon);
				}
				if (nav != null)
				{
					nav.RefreshLogo();
				}
				if (tuningNav != null)
				{
					tuningNav.RefreshLogo();
				}
				if (paviseCore != null)
				{
					paviseCore.RefreshVisual();
				}
			}
			if (UiActive)
			{
				UiClock.Wake();
			}
		}
		private void OnAutoToggle(object s, EventArgs e)
		{
			if (!base.IsDisposed && swAuto != null && !swAuto.IsDisposed)
			{
				Interlocked.Increment(ref slowVersion);
				if (ChangeStartupTask(swAuto.Checked) != 0)
				{
					string lastSchtasksError = TaskHelper.LastSchtasksError;
					swAuto.SetSilently(QueryStartupTaskState());
					WarnStartupTaskFailure(lastSchtasksError);
				}
			}
		}
		private void OnShaderClean(PillButton btn)
		{
			if (runtimeShutdownBusy || base.IsDisposed || btn == null || btn.IsDisposed)
			{
				return;
			}
			if (ShaderCleanupBusy)
			{
				if (cardShader != null)
				{
					cardShader.Value = Lang.T("shader.busy");
				}
			}
			else if (gameMode != null && gameMode.IsActive)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("shader.ingame"));
			}
			else
			{
				if (!ConfirmSettingsAction("shader.confirm", DlgKind.Warn))
				{
					return;
				}
				if (gameMode != null && gameMode.IsActive)
				{
					PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("shader.ingame"));
				}
				else
				{
					if (runtimeShutdownBusy || base.IsDisposed || btn.IsDisposed || Interlocked.CompareExchange(ref shaderCleaning, 1, 0) != 0)
					{
						return;
					}
					int version = Interlocked.Increment(ref shaderCleanupVersion);
					btn.Enabled = false;
					SyncShaderCleanupPresentation();
					Action<string, CacheSweep.Result, long> complete = delegate(string failure, CacheSweep.Result cr, long left)
					{
						try
						{
							PostUiStateResult(delegate
							{
								if (!base.IsDisposed && !runtimeShutdownBusy && version == Volatile.Read(ref shaderCleanupVersion))
								{
									if (!btn.IsDisposed)
									{
										btn.Enabled = true;
									}
									SyncShaderCleanupPresentation();
									if (cardShader != null && !cardShader.IsDisposed && left >= 0)
									{
										cardShader.Value = CacheSweep.FmtBytes(left);
									}
									else if (cardShader != null && !cardShader.IsDisposed)
									{
										cardShader.Value = ((failure == null) ? "" : PerformancePolicy.Text("清理未完成", "Cleanup incomplete"));
									}
									string message = ((failure != null) ? (PerformancePolicy.Text("着色器缓存清理未完成", "Shader cache cleanup incomplete") + "\r\n\r\n" + failure) : (Lang.F("shader.freed", CacheSweep.FmtBytes(cr.FreedBytes)) + ((cr.FailedFiles > 0) ? ("\r\n" + Lang.F("shader.skip", cr.FailedFiles)) : "") + "\r\n\r\n" + Lang.T("shader.note")));
									ShowShaderCleanupMessage(failure != null, message);
								}
							});
						}
						catch
						{
						}
					};
					try
					{
						QueueUiStateWork(delegate
						{
							CacheSweep.Result result = null;
							long arg = -1L;
							string arg2 = null;
							try
							{
								result = CleanSettingsShaderCache();
								if (result == null)
								{
									throw new InvalidOperationException("Shader cleanup returned no result");
								}
								arg = MeasureSettingsShaderCache();
								Logger.Log(Lang.T("log.panelformsettingspage.1") + CacheSweep.FmtBytes(result.FreedBytes) + ((result.FailedFiles > 0) ? (" " + result.FailedFiles + Lang.T("log.panelformsettingspage.2")) : ""));
							}
							catch (Exception ex2)
							{
								arg2 = ex2.Message;
							}
							finally
							{
								Interlocked.Exchange(ref shaderCleaning, 0);
							}
							complete(arg2, result, arg);
						});
					}
					catch (Exception ex)
					{
						Interlocked.Exchange(ref shaderCleaning, 0);
						btn.Enabled = true;
						SyncShaderCleanupPresentation();
						ShowShaderCleanupMessage(true, PerformancePolicy.Text("着色器缓存清理未完成", "Shader cache cleanup incomplete") + "\r\n\r\n" + ex.Message);
					}
				}
			}
		}
		private void ShowSettingsOperationWarning(string message)
		{
			PaviseDialog.Warn(this, "PAVISE AUTO", message);
		}
		private void OnWipeAll()
		{
			Action resetApp = ResetApp;
			if (resetApp != null && CanBeginReset("wipe.ingame", "wipe.confirm"))
			{
				resetApp();
			}
		}
		private void OnUninstall()
		{
			Action uninstallApp = UninstallApp;
			if (uninstallApp != null && CanBeginReset("uninstall.ingame", "uninstall.confirm"))
			{
				uninstallApp();
			}
		}
		private void OnStandbyCleanerToggle(bool on)
		{
			if (on && !ConfirmStandbyCleanerEnable())
			{
				if (swPolicyStandbyCleaner != null)
				{
					swPolicyStandbyCleaner.SetSilently(gameMode.StandbyCleanerEnabled);
				}
				return;
			}
			bool trySetStandbyCleanerEnabled = gameMode.TrySetStandbyCleanerEnabled(on);
			RefreshStandbyCleanerPresentation();
			if (!trySetStandbyCleanerEnabled)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("standbycleaner.switch.savefailed"));
			}
		}
		private void OnWhiteDragEnter(object sender, DragEventArgs e)
		{
			e.Effect = (e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None);
		}
		private void OnWhiteDragDrop(object sender, DragEventArgs e)
		{
			string[] getDataOffset = e.Data.GetData(DataFormats.FileDrop) as string[];
			if (getDataOffset != null && getDataOffset.Length != 0)
			{
				AddWhitelistFiles(getDataOffset);
			}
		}
		private bool ShowSelectedAutomaticExemption()
		{
			WhitelistItem whitelistItem = lstWhite.SelectedItem as WhitelistItem;
			if (whitelistItem == null || !whitelistItem.Automatic || string.IsNullOrEmpty(whitelistItem.Details))
			{
				return false;
			}
			TextBox textBox = new TextBox();
			textBox.Multiline = true;
			textBox.ReadOnly = true;
			textBox.WordWrap = true;
			textBox.ScrollBars = ScrollBars.Vertical;
			textBox.BackColor = Theme.Card;
			textBox.ForeColor = Theme.Fg;
			textBox.BorderStyle = BorderStyle.FixedSingle;
			textBox.Font = Theme.UI(9.2f, false);
			textBox.Text = whitelistItem.Details;
			textBox.Size = new Size(Theme.S(668), Theme.S(390));
			textBox.SelectionStart = 0;
			textBox.SelectionLength = 0;
			Native.Dark(textBox);
			PaviseDialog.Show(this, whitelistItem.Title, Lang.T("white.auto.details.body"), textBox, 720);
			return true;
		}
		private void OnWhiteMenuOpening(object sender, CancelEventArgs e)
		{
			whiteMenu.Items.Clear();
			if (!CanRemoveSelectedWhitelist())
			{
				e.Cancel = true;
				return;
			}
			whiteMenu.Items.Add(Lang.T("btn.remove"), null, delegate
			{
				RemoveSelectedWhitelist();
			});
			if (whiteMenu.Items.Count == 0)
			{
				e.Cancel = true;
			}
		}
		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			RegisterPowerSchemeNotifications();
			Native.EnableElevatedFileDrop(base.Handle);
			AttachFormFrame();
			Native.RoundCorners(base.Handle);
			uiActivityKnown = false;
			CenterRoot();
			ScheduleFit();
			SyncUiActivity();
			if (UiActive)
			{
				RefreshSlowStateAsync();
			}
		}
		internal void ShowPageForShot(PageId id)
		{
			nav.Select((int)id);
			StopPageReveal();
			nav.SnapToSelection();
			tuningNav.SnapToSelection();
			if (curPage != null)
			{
				curPage.Left = pageBaseLeft;
			}
		}
		private void ShowPage(int index)
		{
			bool matchesIndex = index == 5;
			if (matchesIndex)
			{
				index = 2;
				nav.SelectSilently(index);
			}
			if (index == 11)
			{
				index = 6;
			}
			if (index < 0 || index >= pages.Length || pages[index] == null)
			{
				return;
			}
			if (index != 8)
			{

			}
			SetModeFlyout(false);
			SetPowerFlyout(false);
			DBPanel dBPanel = pages[index];
			if (curPage == dBPanel && dBPanel.Visible)
			{
				if (matchesIndex)
				{
					ShowPerformanceSystemTab();
				}
				return;
			}
			SavePagePosition(curPage);
			bool visibleValue = index == 12;
			bool visibleValue2 = index == 7;
			pageBaseLeft = ((!visibleValue && !visibleValue2) ? Theme.S(RailW) : 0);
			dBPanel.Left = pageBaseLeft;
			bool matchesPreparePageReveal = curPage != null && !curPage.IsDisposed && PreparePageReveal(dBPanel);
			bool showPageReady = false;
			root.SuspendLayout();
			dBPanel.SuspendLayout();
			try
			{
				DBPanel[] pagesSnapshot = pages;
				foreach (DBPanel dBPanel2 in pagesSnapshot)
				{
					dBPanel2.Visible = dBPanel2 == dBPanel;
				}
				curPage = dBPanel;
				LayoutWorkspaceViewport();
				bool isAdvancedPage = IsAdvancedPage(index);
				nav.Visible = true;
				nav.BringToFront();
				tuningNav.Visible = false;
				coreBackBar.Visible = visibleValue;
				if (visibleValue)
				{
					coreBackBar.BringToFront();
				}
				overviewBackBar.Visible = visibleValue2;
				if (visibleValue2)
				{
					overviewBackBar.BringToFront();
				}
				if (isAdvancedPage)
				{
					tuningNav.SelectSilently(index);
					nav.SelectSilently(2);
					if (lastAdvancedPage != index)
					{
						lastAdvancedPage = index;
						Settings.SaveStr("DeepTuningLastPage", index.ToString(CultureInfo.InvariantCulture));
					}
				}
				else
				{
					mainReturnPage = (visibleValue ? 1 : (visibleValue2 ? 8 : index));
					if (visibleValue)
					{
						nav.SelectSilently(1);
					}
					else if (visibleValue2)
					{
						nav.SelectSilently(8);
					}
				}
				LayoutWorkspaceViewport();
				if (matchesIndex)
				{
					ShowPerformanceSystemTab();
				}
				if (UiActive)
				{
					UiClock.Wake();
				}
				NotifyPageActivation();
				showPageReady = true;
			}
			finally
			{
				try
				{
					try
					{
						dBPanel.ResumeLayout(true);
					}
					finally
					{
						root.ResumeLayout(true);
					}
					if (showPageReady)
					{
						LayoutPerformanceViewport();
						RestorePagePosition(index);
					}
					if (matchesPreparePageReveal)
					{
						if (showPageReady)
						{
							if (nav.Visible)
							{
								nav.Update();
							}
							else if (tuningNav.Visible)
							{
								tuningNav.Update();
							}
							StartPageReveal();
						}
						else
						{
							StopPageReveal();
						}
					}
				}
				catch
				{
					StopPageReveal();
					throw;
				}
			}
		}
		protected override void OnHandleDestroyed(EventArgs e)
		{
			SetOverviewTelemetryActive(false);

			UnregisterPowerSchemeNotifications();
			DisposePageReveal();
			CancelAutoHide();
			outroActive = false;
			outroWatch.Reset();
			if (outroTimer != null)
			{
				outroTimer.Stop();
				outroTimer.Dispose();
				outroTimer = null;
			}
			if (uiTimer != null)
			{
				uiTimer.Stop();
			}
			if (fitTimer != null)
			{
				fitTimer.Stop();
				fitTimer.Dispose();
				fitTimer = null;
			}
			uiActive = false;
			uiActivityKnown = true;
			UiClock.Suspended = true;
			if (paviseCore != null)
			{
				paviseCore.SetAnimationEnabled(false);
			}
			DetachFormFrame();
			foreach (Bitmap value in gameIconCache.Values)
			{
				try
				{
					value.Dispose();
				}
				catch
				{
				}
			}
			gameIconCache.Clear();
			foreach (Bitmap value2 in whiteIconCache.Values)
			{
				if (value2 != null)
				{
					try
					{
						value2.Dispose();
					}
					catch
					{
					}
				}
			}
			whiteIconCache.Clear();
			whiteNameCache.Clear();
			if (appIcon != null)
			{
				appIcon.Dispose();
				appIcon = null;
			}
			base.OnHandleDestroyed(e);
		}
		public void ShowPanel()
		{
			if (base.InvokeRequired)
			{
				BeginInvoke(new MethodInvoker(ShowPanel));
			}
			else if (!base.IsDisposed)
			{
				CancelOutro();
				ApplyPendingDpiRebuild();
				bool matchesVisible = base.Visible && base.WindowState != FormWindowState.Minimized;
				if (!matchesVisible)
				{
					BeginIntro();
				}
				Show();
				base.WindowState = FormWindowState.Normal;
				Activate();
				StartIntro();
				SyncUiActivity();
				if (matchesVisible)
				{
					SyncAllToggles();
				}
				releaseNotesAutoRequested = true;
				QueueUnseenReleaseNotes();
			}
		}
		private void StartPageReveal()
		{
			if (pageReveal != null && !pageReveal.IsDisposed)
			{
				pageReveal.Reveal();
			}
		}
		private void HandlePowerSchemeNotification(Message message)
		{
			if (message.Msg != 536 || message.WParam.ToInt64() != 32787 || message.LParam == IntPtr.Zero)
			{
				return;
			}
			try
			{
				if (((PowerBroadcastSetting)Marshal.PtrToStructure(message.LParam, typeof(PowerBroadcastSetting))).PowerSetting == ActivePowerSchemeGuid)
				{
					gameMode.NotifyPowerSchemeChanged();
				}
			}
			catch
			{
			}
		}
		private void OnThemeToggled(bool light)
		{
			if (Theme.LightMode != light)
			{
				StopPageReveal();
				UiPalette previous = new UiPalette();
				Settings.Save("UiLight", light);
				Theme.SetLight(light);
				ApplyUiPalette(previous);
				Logger.Log(Lang.T("log.panelform.8") + (light ? Lang.T("log.panelform.9") : Lang.T("log.panelform.10")));
			}
		}
		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			if (SelfUpdateGate.IsBusy && e.CloseReason != CloseReason.WindowsShutDown)
			{
				e.Cancel = true;
				UpdateDialog.ActivateCurrent();
				return;
			}
			if (!RealExit && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.ApplicationExitCall)
			{
				e.Cancel = true;
				BeginOutro();
			}
			else
			{
				CancelConfigurationPresetWork();
				base.OnFormClosing(e);
			}
		}
		private DialogResult ShowDim(Form dlg)
		{
			StopPageReveal();
			return dlg.ShowDialog(this);
		}
		protected override void OnVisibleChanged(EventArgs e)
		{
			if (!base.Visible)
			{
				SetModeFlyout(false);
			}
			if (!base.Visible)
			{
			}
			base.OnVisibleChanged(e);
			if (base.Visible)
			{
				CenterRoot();
				ScheduleFit();
			}
			else if (introActive)
			{
				FinishIntro();
			}
			SyncUiActivity();
		}
		private void OnFitTick(object sender, EventArgs e)
		{
			if (fitTimer != null) fitTimer.Stop();
			if (fitting || base.IsDisposed || !base.IsHandleCreated) return;
			if (moveSizeLoop) { fitDeferredByDrag = true; return; }
			if (base.WindowState == FormWindowState.Minimized) return;
			int w = base.ClientSize.Width, h = base.ClientSize.Height;
			if (!Dpi.FitDiffers(w, h)) return;
			float target = Dpi.FitScale(w, h);
			fitting = true;
			try
			{
				Dpi.Scale = target;
				Theme.DropFontCache();
				Logger.Log(Lang.T("log.panelform.6") + w + "x" + h
					+ Lang.T("log.panelform.7") + target.ToString("F2"));
				RebuildUi();
			}
			finally { fitting = false; }
			CenterRoot();
		}
		protected override void OnActivated(EventArgs e)
		{
			base.OnActivated(e);
			SyncUiForeground(true);
			QueueUnseenReleaseNotes();
		}
		protected override void OnDeactivate(EventArgs e)
		{
			if (powerButton == null || !powerButton.DropDownVisible)
			{
				SetModeFlyout(false);
			}
			base.OnDeactivate(e);
			SyncUiForeground(false);
		}
		private void OnFormFrame(object s, EventArgs e)
		{
			if (Theme.StepTheme())
			{
				RefreshAccentLabels();
				InvalidateVisibleTheme();
			}
			StepIntro();
			if (!introActive && !introPending && !outroActive && base.AllowTransparency)
			{
				DropLayeredStyle();
			}
		}
		private void StartIntro()
		{
			if (!introPending)
			{
				if (!introActive)
				{
					DropLayeredStyle();
				}
				return;
			}
			introPending = false;
			introMotion.Speed = 0.24f;
			introMotion.Set(1f);
			introMotion.To(0f);
			introActive = true;
			PaintTree(this);
			UiClock.Wake(90);
			if (!UiClock.Running)
			{
				FinishIntro();
			}
		}
		private void OnUiTick(object s, EventArgs e)
		{
			if (UiActive && !moveSizeLoop)
			{
				if (overviewMemoryCleanup != null) overviewMemoryCleanup.SetGamePolicy(gameMode.IsActive);
				UpdateAutoHide(gameMode.Enabled && gameMode.IsActive);
				RefreshLightweightUiState();
				UpdateModePresentation(true);
				PageHook pageHook = CurrentPageHook();
				if (pageHook != null && pageHook.OnTick != null)
				{
					pageHook.OnTick();
				}
			}
		}
		private void OnAutoHideTick(object s, EventArgs e)
		{
			if (!moveSizeLoop)
			{
				CancelAutoHide();
				if (!base.IsDisposed && UiActive && !AnyDialogOpen())
				{
					BeginOutro();
				}
			}
		}
		private void OnOutroTick(object s, EventArgs e)
		{
			if (!outroActive || base.IsDisposed)
			{
				FinishOutro(false);
				return;
			}
			double totalMillisecondsRatio = outroWatch.Elapsed.TotalMilliseconds / 220.0;
			if (totalMillisecondsRatio >= 1.0)
			{
				FinishOutro(true);
				return;
			}
			double scaledTotalMillisecondsRatio = totalMillisecondsRatio * totalMillisecondsRatio;
			base.Opacity = 1.0 - scaledTotalMillisecondsRatio;
		}
		private void OnAutoHideToggle(object s, EventArgs e)
		{
			Settings.Save("AutoHideOnGame", swAutoHide.Checked);
			if (!swAutoHide.Checked)
			{
				CancelAutoHide();
			}
			swAutoHide.SetSilently(Settings.Load("AutoHideOnGame", true));
		}
		private void OnEscHide(object s, KeyEventArgs e)
		{
			if (e.KeyCode != Keys.Escape)
			{
				return;
			}
			if (powerButton != null && powerButton.DropDownVisible)
			{
				SetPowerFlyout(false);
			}
			else if (modeFlyout != null && modeFlyout.Visible)
			{
				SetModeFlyout(false);
			}
			else if ((curPage != pageAntiCheat || !CloseAcDrawer()) && (curPage != pageLibrary || lstGames == null || !lstGames.CollapseExpandedConfiguration()))
			{
				if (curPage == pageCoreScheduling)
				{
					nav.Select(1);
				}
				else if (IsInDeepTuning)
				{
					ReturnFromDeepTuning();
				}
				else
				{
					BeginOutro();
				}
			}
			e.Handled = true;
			e.SuppressKeyPress = true;
		}
	}
}
