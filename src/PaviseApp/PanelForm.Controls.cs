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
		private Control[] AddAboutMetric(Control parent, string title, string value, int x, int y, int width, bool good)
		{
			Label label = CardLabel(parent, title.ToUpperInvariant(), x, y, width, 18, 6.7f, true, Theme.Faint);
			Label label2 = CardLabel(parent, value, x, y + 24, width, 27, 9.2f, true, good ? Theme.Green : Theme.Fg);
			label2.TextAlign = ContentAlignment.MiddleLeft;
			AccentLine accentLine = new AccentLine();
			accentLine.SetBounds(Theme.S(x), Theme.S(y + 60), Theme.S(width - 14), Theme.S(1));
			parent.Controls.Add(accentLine);
			return new Control[3] { label, label2, accentLine };
		}
		private Label AddAboutRow(Control parent, string title, string value, int y, int width, bool accent)
		{
			CardLabel(parent, title, 20, y, 112, 20, 7.2f, true, Theme.Faint);
			Label label = (accent ? AccentLabel(parent, value, 136, y - 2, width - 156, 25, 8.8f, true) : CardLabel(parent, value, 136, y - 2, width - 156, 25, 8.8f, false, Theme.Fg));
			label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			Panel panel = new Panel();
			panel.BackColor = Theme.Stroke;
			panel.SetBounds(Theme.S(20), Theme.S(y + 31), Theme.S(width - 40), 1);
			panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			parent.Controls.Add(panel);
			return label;
		}
		private static void OwnedImage(PictureBox pb, Image img)
		{
			pb.Image = img;
			pb.Disposed += delegate
			{
				try
				{
					if (pb.Image != null)
					{
						pb.Image.Dispose();
					}
				}
				catch
				{
				}
			};
		}
		private AcViewState CaptureAcViewState()
		{
			if (curPage != pageAntiCheat || acDrawerBody == null || acViewport == null)
			{
				return null;
			}
			float acLayoutScaleLimit = Math.Max(0.01f, acLayoutScale);
			int lowerBound = Math.Max(0, -acViewport.AutoScrollPosition.Y);
			AcViewState acViewState = new AcViewState();
			acViewState.Drawer = acDrawerKind;
			acViewState.BodyScroll = acDrawerBody.AutoScroll;
			acViewState.InnerY = (float)Math.Max(0, -acDrawerBody.AutoScrollPosition.Y) / acLayoutScaleLimit;
			acViewState.OuterY = (float)lowerBound / acLayoutScaleLimit;
			acViewState.BodyOrigin = (float)(acRosterBar.Top + lowerBound + acDrawer.Top + acDrawerBody.Top) / acLayoutScaleLimit;
			return acViewState;
		}
		private void RestoreAcViewState(AcViewState state)
		{
			if (state != null && curPage == pageAntiCheat)
			{
				if (state.Drawer.Length > 0)
				{
					OpenAcDrawer(state.Drawer);
				}
				RestoreAcScrollPosition(state);
			}
		}
		private bool CloseAcDrawer()
		{
			if (acDrawerKind.Length == 0 || acDrawer == null)
			{
				return false;
			}
			string acDrawerKindSnapshot = acDrawerKind;
			acDrawerKind = "";
			acOtherOpen = false;
			acDrawer.Visible = false;
			acList.Visible = true;
			LayoutAntiCheatViewport();
			((acDrawerKindSnapshot == "roster") ? acRosterEntry : acOtherToggle).Focus();
			return true;
		}
		private void AddAcReadOnly(string name, string description, string[] processes, bool recordOnly)
		{
			string tag = Lang.T(recordOnly ? "ac.recordonly" : "ac.protectiononly");
			if (tag == "ac.recordonly")
			{
				tag = AutoText("仅记录", "Record only");
			}
			AcReadOnlyButton acReadOnlyButton = new AcReadOnlyButton(name, tag, recordOnly);
			acReadOnlyButton.Name = "AntiCheatReadOnly";
			acReadOnlyButton.Visible = false;
			acReadOnlyButton.AccessibleName = name + " · " + tag;
			AcReadOnlyButton acReadOnlyButton2 = acReadOnlyButton;
			acReadOnlyButton2.Click += delegate
			{
				ShowAcInformation(name, tag + "\r\n\r\n" + description + "\r\n\r\n" + string.Join(" · ", processes));
			};
			acDrawerBody.Controls.Add(acReadOnlyButton2);
			acReadOnlyCards.Add(acReadOnlyButton2);
		}
		private static Color StatusInk(bool needsAction, bool doneByPavise)
		{
			if (doneByPavise)
			{
				return Theme.Green;
			}
			if (!needsAction)
			{
				return Theme.Faint;
			}
			return Theme.Accent;
		}
		private static void LockEnvCard(SettingCard card, Toggle toggle, bool externalOn)
		{
			if (card != null && toggle != null)
			{
				card.SetLock(externalOn ? Lang.T("lock.external") : ((!toggle.Enabled) ? Lang.T("lock.na") : ""), externalOn);
			}
		}
		private bool RequireElevationFor(Toggle sw, bool restoredState)
		{
			if (elevated)
			{
				return true;
			}
			PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.needadmin"));
			sw.SetSilently(restoredState);
			return false;
		}
		private static string GlobalTimerDescription(string state, bool on, string readback)
		{
			string message;
			switch (state)
			{
			case "ready":
				message = "ready";
				break;
			case "timer-locked":
				message = "locked";
				break;
			case "timer-limited":
				message = "limited";
				break;
			case "timer-unverified":
				message = "query-unverified";
				break;
			case "pending-reboot":
			case "restore-pending-reboot":
			case "unverified":
			case "failed":
			case "stopped":
			case "restore-failed":
			case "restored":
			case "unsupported":
				message = state;
				break;
			case "timer-requested":
				message = "requested";
				break;
			default:
				message = (on ? "pending" : "off");
				break;
			}
			return PerformanceEnvironmentExplanation("AutoGlobalTimer", "env.gtimer.manual") + "\n" + Lang.T("env.gtimer.state." + message) + ((!on && !string.IsNullOrEmpty(readback)) ? ("\n" + Lang.T("env.gtimer.state.release-pending")) : "") + (string.IsNullOrEmpty(readback) ? "" : ("\n" + readback));
		}
		private void MonoLabel(Control parent, string text, int x, int y, int w)
		{
			Label label = new Label();
			label.Text = text;
			label.ForeColor = Theme.Faint;
			label.BackColor = Color.Transparent;
			label.Font = Theme.Mono(6.2f);
			label.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(14));
			parent.Controls.Add(label);
		}
		private void ToggleForceTrigger(string keepId)
		{
			GameProfile gameProfile = FindLibraryProfile(keepId);
			if (gameProfile == null)
			{
				return;
			}
			bool isNotForceTrigger = !gameProfile.ForceTrigger;
			if (isNotForceTrigger && !PaviseDialog.Confirm(this, Lang.T("v15.library.force"), Lang.F("v15.library.force.confirm", gameProfile.Name), DlgKind.Warn))
			{
				return;
			}
			if (!gameMode.SetProfileForceTrigger(keepId, isNotForceTrigger))
			{
				if (!gameMode.ProfileStoreSaveFailed)
				{
					PaviseDialog.Warn(this, Lang.T("v15.library.force"), Lang.T("v15.library.force.noexe"));
				}
			}
			else
			{
				RefreshGames();
				SelectProfile(keepId);
			}
		}
		private GameLibraryItem ConfigurationItem(string id)
		{
			GameProfile gameProfile = FindLibraryProfile(id);
			if (gameProfile == null)
			{
				return null;
			}
			bool running = false;
			if (lstGames != null)
			{
				foreach (GameLibraryItem item in lstGames.Items)
				{
					if (item.Profile != null && item.Profile.Id == id)
					{
						running = item.Running;
						break;
					}
				}
			}
			return new GameLibraryItem(gameProfile, running, gameMode.HasRendererObservation(gameProfile));
		}
		private void ConfigureLogView()
		{
			if (logStream != null)
			{
				logStream.ActionEvents = !logDiagnostics;
				logStream.TechnicalEvents = logDiagnostics;
				logStream.Filter = 4;
			}
			if (logActionsButton != null)
			{
				logActionsButton.Kind = logDiagnostics ? BtnKind.Normal : BtnKind.Primary;
				logActionsButton.AccessibleDescription = Lang.T("log.actions.hint");
				logActionsButton.Invalidate();
			}
			if (logDiagnosticsButton != null)
			{
				logDiagnosticsButton.Kind = logDiagnostics ? BtnKind.Primary : BtnKind.Normal;
				logDiagnosticsButton.AccessibleDescription = Lang.T("log.diagnostics.hint");
				logDiagnosticsButton.Invalidate();
			}
			if (logExportButton != null)
				logExportButton.AccessibleDescription = Lang.T(logDiagnostics ? "log.export.diagnostics.detail" : "log.export.actions.detail");
		}
		private void SelectLogCategory(bool diagnostics)
		{
			if (logDiagnostics == diagnostics) return;
			logDiagnostics = diagnostics;
			ConfigureLogView();
			SyncLogHint();
		}
		private void PostManualMemoryUi(Action action)
		{
			if (manualMemoryDisposed || base.IsDisposed || base.Disposing || !base.IsHandleCreated)
			{
				return;
			}
			try
			{
				BeginInvoke((MethodInvoker)delegate
				{
					if (!manualMemoryDisposed && !base.IsDisposed && !base.Disposing)
					{
						action();
					}
				});
			}
			catch (InvalidOperationException)
			{
			}
		}
		public void NotifyLastSession(string brief)
		{
			try
			{
				if (!base.IsHandleCreated || base.IsDisposed || string.IsNullOrEmpty(brief))
				{
					return;
				}
				BeginInvoke((MethodInvoker)delegate
				{
					if (!base.IsDisposed)
					{
						RefreshOverviewAttention();
					}
				});
			}
			catch
			{
			}
		}
		public void NotifyUpdate(UpdateResult result)
		{
			try
			{
				PostUiStateResult(delegate
				{
					ApplyUpdateResult(result);
				});
			}
			catch
			{
			}
		}
		private void ApplyUpdateResult(UpdateResult result)
		{
			if (!base.IsDisposed && result != null && result.Ok && !string.IsNullOrEmpty(result.Latest))
			{
				if (latestUpdate == null || !UpdateChecker.IsNewer(latestUpdate.Latest, result.Latest))
				{
					latestUpdate = result;
				}
				RefreshUpdatePresentation();
			}
		}
		public void NotifyDonate(DonateInfo d)
		{
			if (d != null)
			{
				DonateCache.Latest = d;
			}
		}
		private static void FetchDonateImage(Action<byte[], DonateInfo> done)
		{
			Action<DonateInfo> pull = delegate(DonateInfo i)
			{
				byte[] arg = UpdateChecker.FetchBytes(i.Url, 2097152);
				done(arg, i);
			};
			DonateInfo known = DonateCache.Latest;
			if (known != null)
			{
				ThreadPool.QueueUserWorkItem(delegate
				{
					pull(known);
				});
				return;
			}
			UpdateChecker.CheckAsync(delegate(UpdateResult r)
			{
				DonateInfo donateInfo = ((r != null && r.Ok) ? r.Donate : null);
				if (donateInfo != null)
				{
					DonateCache.Latest = donateInfo;
				}
				pull(donateInfo ?? DonateCache.Default);
			});
		}
		private void CompleteDonate(byte[] bytes, DonateInfo info)
		{
			donateLoading = false;
			if (base.IsDisposed)
			{
				return;
			}
			Bitmap bitmap = null;
			if (bytes != null && info != null && DonateCache.Store(bytes, info.Id))
			{
				bitmap = DonateCache.Decode(bytes);
			}
			RefreshDonateButton();
			if (donateDialog != null && !donateDialog.IsDisposed)
			{
				if (bitmap != null)
				{
					donateDialog.SetImage(bitmap);
				}
				else
				{
					donateDialog.MarkFailed();
				}
			}
			else if (bitmap != null)
			{
				bitmap.Dispose();
			}
		}
		private void EnableCardCollapse(Control panel, params SettingCard[] keepOpen)
		{
			List<Control> list = new List<Control>();
			foreach (Control key in stackBase.Keys)
			{
				if (key.IsDisposed)
				{
					list.Add(key);
				}
			}
			foreach (Control item in list)
			{
				stackBase.Remove(item);
			}
			Dictionary<Control, int> dictionary = new Dictionary<Control, int>();
			foreach (Control control3 in panel.Controls)
			{
				dictionary[control3] = control3.Top;
			}
			stackBase[panel] = dictionary;
			List<SettingCard> list2 = new List<SettingCard>(keepOpen ?? new SettingCard[0]);
			foreach (Control control4 in panel.Controls)
			{
				SettingCard settingCard = control4 as SettingCard;
				if (settingCard == null || settingCard.Desc.Length == 0)
				{
					continue;
				}
				settingCard.ExpandedHeight = settingCard.Height;
				settingCard.CollapsedHeight = Theme.S(52) + (settingCard.HasStatus ? Theme.S(17) : 0);
				if (settingCard.CollapsedHeight < settingCard.ExpandedHeight)
				{
					settingCard.Collapsible = true;
					settingCard.SnapExpanded(list2.Contains(settingCard));
					Control owner = panel;
					settingCard.ExpandedChanged = delegate
					{
						RestackCards(owner);
					};
					settingCard.Height = (settingCard.Expanded ? settingCard.ExpandedHeight : settingCard.CollapsedHeight);
				}
			}
			RestackCards(panel);
		}
		private static string ModeName(PerformancePreset m)
		{
			return ModeButton.ModeName(m);
		}
		private static string DetailSection(string titleKey, string content)
		{
			return Lang.T(titleKey) + "\r\n" + content;
		}
		private WhitelistItem Decorate(WhitelistItem item)
		{
			WhitelistRule rule = item.View.Rule;
			if (item.View.PlatformId != null)
			{
				item.Title = PlatformRuntimePolicy.DisplayName(item.View.PlatformId);
				item.Subtitle = PlatformRuntimePolicy.ProcessNames(item.View.PlatformId);
				item.Badge = Lang.T("white.platform.badge");
				item.StateText = ((item.View.CurrentMatches > 0) ? Lang.F("white.state.running", item.View.CurrentMatches) : ((item.View.CurrentMatches < 0) ? Lang.T("white.matches.pending") : Lang.T("white.state.idle")));
				return item;
			}
			item.Title = WhitelistTitle(rule);
			item.Subtitle = ((rule.Kind == WhitelistRuleKind.LegacyName) ? Lang.T("white.badge.name.sub") : rule.Value);
			item.Badge = (item.View.Required ? Lang.T("white.required.badge") : Lang.T("white.badge.family"));
			item.StateText = (item.View.Required ? "" : ((item.View.CurrentMatches < 0) ? Lang.T("white.matches.pending") : ((item.View.CurrentMatches > 0) ? Lang.F("white.state.running", item.View.CurrentMatches) : Lang.T("white.state.idle"))));
			return item;
		}
		private static int DockScrollLimit(ScrollableControl viewport)
		{
			if (viewport != null && viewport.VerticalScroll.Visible)
			{
				return Math.Max(0, viewport.VerticalScroll.Maximum - viewport.VerticalScroll.LargeChange + 1);
			}
			return 0;
		}
		private void ReturnFromDeepTuning()
		{
			if (IsInDeepTuning)
			{
				int mainReturnPageSnapshot = mainReturnPage;
				if (mainReturnPageSnapshot < 0 || mainReturnPageSnapshot >= (int)PageId.Count || IsAdvancedPage(mainReturnPageSnapshot))
				{
					mainReturnPageSnapshot = 0;
				}
				nav.Select(mainReturnPageSnapshot);
				nav.Focus();
			}
		}
		private void RegisterPowerSchemeNotifications()
		{
			UnregisterPowerSchemeNotifications();
			if (!base.IsHandleCreated)
			{
				return;
			}
			Guid powerSettingGuid = ActivePowerSchemeGuid;
			try
			{
				powerSchemeNotification = RegisterPowerSettingNotification(base.Handle, ref powerSettingGuid, 0u);
			}
			catch
			{
				powerSchemeNotification = IntPtr.Zero;
			}
		}
		private void UnregisterPowerSchemeNotifications()
		{
			IntPtr intPtr = powerSchemeNotification;
			powerSchemeNotification = IntPtr.Zero;
			if (intPtr == IntPtr.Zero)
			{
				return;
			}
			try
			{
				UnregisterPowerSettingNotification(intPtr);
			}
			catch
			{
			}
		}
		internal static string ModeSourceText(string sourceName, bool compact)
		{
			if (!string.IsNullOrEmpty(sourceName))
			{
				return Lang.F(compact ? "mode.source.game.short" : "mode.source.game", sourceName);
			}
			return Lang.T("mode.source.global");
		}
		private void RebuildUi()
		{
			CancelConfigurationPresetWork();
			SetOverviewTelemetryActive(false);
			if (libraryConfigurationDialog != null && !libraryConfigurationDialog.IsDisposed)
			{
				libraryConfigurationDialog.Close();
			}

			DisposePageReveal();
			if (tuningNav != null && tuningNav.Parent == null)
			{
				tuningNav.Dispose();
			}
			CancelOutro();
			if (uiTimer != null)
			{
				uiTimer.Stop();
				uiTimer.Dispose();
				uiTimer = null;
			}
			uiActive = false;
			uiActivityKnown = false;
			UiClock.Suspended = true;
			if (paviseCore != null)
			{
				paviseCore.SetAnimationEnabled(false);
			}
			DetachFormFrame();
			List<Control> list = new List<Control>();
			int matchIndex = ((pages != null && curPage != null) ? Array.IndexOf(pages, curPage) : ((nav != null) ? nav.Selected : 0));
			if (matchIndex < 0)
			{
				matchIndex = 0;
			}
			int mainReturnPageSnapshot = mainReturnPage;
			AcViewState acViewState = CaptureAcViewState();
			CoreViewState coreViewState = CaptureCoreViewState();
			SavePagePosition(curPage);
			foreach (Control control in base.Controls)
			{
				list.Add(control);
			}
			base.Controls.Clear();
			foreach (Control item in list)
			{
				item.Dispose();
			}
			acGroups.Clear();
			acCards.Clear();
			acToggles.Clear();
			accentLabels.Clear();
			policySync.Clear();
			stackBase.Clear();
			for (int i = 0; i < modeAccentLabels.Length; i++)
			{
				modeAccentLabels[i] = null;
				modeSwatches[i].Clear();
			}
			BuildUi(appIcon);
			mainReturnPage = mainReturnPageSnapshot;
			nav.Select(matchIndex);
			RestoreAcViewState(acViewState);
			RestoreCoreViewState(coreViewState);
			if (UiActive)
			{
				RefreshSlowStateAsync();
			}
		}
		public void RenderTo(string path, int pageIndex, bool showAntiCheat = false, bool showModePicker = false, string previewMode = null)
		{
			shotMode = true;
			base.StartPosition = FormStartPosition.Manual;
			base.Location = new Point(-20000, -20000);
			Show();
			if (pageIndex >= 0 && pageIndex < pages.Length)
			{
				nav.Select(pageIndex);
				nav.SnapToSelection();
				tuningNav.SnapToSelection();
				if (curPage != null)
				{
					curPage.Left = pageBaseLeft;
				}
			}
			OnUiTick(null, EventArgs.Empty);
			if (showAntiCheat)
			{
				nav.Select(3);
				nav.SnapToSelection();
				if (curPage != null)
				{
					curPage.Left = pageBaseLeft;
				}
			}
			PerformancePreset? standard;
			switch (previewMode)
			{
			default:
				standard = null;
				break;
			case "standard":
				standard = PerformancePreset.Standard;
				break;
			case "handheld":
				standard = PerformancePreset.Handheld;
				break;
			case "competitive":
				standard = PerformancePreset.Competitive;
				break;
			case "extreme":
				standard = PerformancePreset.Extreme;
				break;
			}
			PerformancePreset? performancePreset = standard;
			if (performancePreset.HasValue)
			{
				Theme.SetMode(performancePreset.Value, false);
				modeButton.SetMode(performancePreset.Value);
				nav.SetMode(performancePreset.Value, true);
				tuningNav.SetMode(performancePreset.Value, true);
				if (lblHeroMode != null)
				{
					lblHeroMode.Text = ModeButton.ModeName(performancePreset.Value);
					lblHeroMode.ForeColor = Theme.Accent;
				}
				if (paviseCore != null)
				{
					paviseCore.SetState(performancePreset.Value, true, false);
				}
			}
			if (showModePicker && modeButton != null)
			{
				modeButton.PerformClick();
			}
			if (previewMode == "power" && powerButton != null)
			{
				SetPowerFlyout(true);
			}
			if (previewMode == "advanced")
			{
				nav.Select(lastAdvancedPage);
				tuningNav.SnapToSelection();
				if (curPage != null)
				{
					curPage.Left = pageBaseLeft;
				}
			}
			if (previewMode == "audit" && pageIndex == 6)
			{
				try
				{
					RenderAudit(SystemAudit.Collect(400));
				}
				catch
				{
				}
				if (lblAuditStatus != null)
				{
					lblAuditStatus.Text = "";
				}
			}
			if (previewMode == "settings-appearance" && pageIndex == 8 && settingsTabs != null)
			{
				settingsTabs.Index = 2;
			}
			if (previewMode == "settings-maint" && pageIndex == 8 && settingsTabs != null)
			{
				settingsTabs.Index = 1;
			}
			Application.DoEvents();
			StopPageReveal();
			if (lstGames != null)
			{
				bool renderToReady = false;
				foreach (GameLibraryItem item in lstGames.Items)
				{
					if (item.Profile != null && item.Profile.ExecutablePath != null && item.Profile.ExecutablePath.IndexOf("PaviseShot_", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						gameIconCache["D:\\Games\\NebulaStrike\\Binaries\\Win64\\NebulaStrike-Win64-Shipping.exe"] = GameIcon(item.Profile.ExecutablePath);
						item.Profile.ExecutablePath = "D:\\Games\\NebulaStrike\\Binaries\\Win64\\NebulaStrike-Win64-Shipping.exe";
						renderToReady = true;
					}
				}
				if (renderToReady)
				{
					lstGames.Invalidate(true);
					Application.DoEvents();
				}
			}
			using (Bitmap bitmap = new Bitmap(base.ClientSize.Width, base.ClientSize.Height))
			{
				DrawToBitmap(bitmap, new Rectangle(0, 0, base.ClientSize.Width, base.ClientSize.Height));
				if (showModePicker && modeFlyout != null && modeFlyout.Visible)
				{
					using (Bitmap bitmap2 = new Bitmap(modeFlyout.Width, modeFlyout.Height))
					{
						using (Graphics graphics = Graphics.FromImage(bitmap))
						{
							modeFlyout.DrawToBitmap(bitmap2, new Rectangle(0, 0, bitmap2.Width, bitmap2.Height));
							graphics.DrawImageUnscaled(bitmap2, modeFlyout.Left, modeFlyout.Top);
						}
					}
				}
				bitmap.Save(path, ImageFormat.Png);
			}
			Hide();
			shotMode = false;
		}
		private Label Section(Control parent, string text, int x, int y)
		{
			AccentLine accentLine = new AccentLine();
			accentLine.SetBounds(Theme.S(x + 4), Theme.S(y + 5), Theme.S(3), Theme.S(8));
			parent.Controls.Add(accentLine);
			Label label = new Label();
			label.Text = text;
			label.ForeColor = Theme.Faint;
			label.BackColor = Theme.Bg;
			label.Font = Theme.UI(8.25f, true);
			label.UseCompatibleTextRendering = false;
			label.SetBounds(Theme.S(x + 14), Theme.S(y), Theme.S(400), Theme.S(18));
			parent.Controls.Add(label);
			return label;
		}
		private Toggle MakeSwitch(bool on, EventHandler handler)
		{
			Toggle toggle = new Toggle();
			toggle.Size = new Size(Theme.S(46), Theme.S(24));
			toggle.Bg = Theme.Card;
			toggle.SetSilently(on);
			if (handler != null)
			{
				toggle.CheckedChanged += handler;
			}
			return toggle;
		}
		private SettingCard MakeAutoCard(Control parent, int x, int y, int w, int minH, string title, string desc, Control host, out int used)
		{
			used = AutoCardHeight(desc, w, host, minH);
			return MakeCard(parent, x, y, w, used, title, desc, host);
		}
		private SettingCard MakeCard(Control parent, int x, int y, int w, int h, string title, string desc, Control host)
		{
			SettingCard settingCard = ((parent is PerformanceFeatureGrid) ? new PerformanceFeatureCard() : new SettingCard());
			int channelValue = 1;
			foreach (Control control in parent.Controls)
			{
				if (control is SettingCard)
				{
					channelValue++;
				}
			}
			settingCard.Channel = channelValue;
			settingCard.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
			settingCard.Title = title;
			settingCard.Desc = desc ?? "";
			if (host != null)
			{
				settingCard.Host(host);
			}
			parent.Controls.Add(settingCard);
			return settingCard;
		}
		private RoundPanel MakeConsolePanel(Control parent, int x, int y, int width, int height, bool accent)
		{
			RoundPanel roundPanel = new RoundPanel();
			roundPanel.SetBounds(Theme.S(x), Theme.S(y), Theme.S(width), Theme.S(height));
			roundPanel.BackColor = Theme.Bg;
			roundPanel.Fill = Theme.Card;
			roundPanel.Border = Theme.Stroke;
			roundPanel.Radius = Theme.S(14);
			roundPanel.AccentEdge = accent;
			parent.Controls.Add(roundPanel);
			return roundPanel;
		}
		private Label CardLabel(Control parent, string text, int x, int y, int w, int h, float size, bool bold, Color color)
		{
			Label label = new Label();
			label.Text = text;
			label.ForeColor = color;
			label.BackColor = Color.Transparent;
			label.Font = Theme.UI(size, bold);
			label.AutoEllipsis = true;
			label.UseCompatibleTextRendering = false;
			label.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
			parent.Controls.Add(label);
			return label;
		}
		private Label AccentLabel(Control parent, string text, int x, int y, int w, int h, float size, bool bold)
		{
			Label label = CardLabel(parent, text, x, y, w, h, size, bold, Theme.Accent);
			accentLabels.Add(label);
			return label;
		}
		private DBPanel[] MakeTabPanels(Control page, TechTabs tabs, int count, int y)
		{
			DBPanel[] panels = new DBPanel[count];
			for (int i = 0; i < panels.Length; i++)
			{
				WorkspacePanel workspacePanel = new WorkspacePanel();
				workspacePanel.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
				workspacePanel.BackColor = Theme.Bg;
				workspacePanel.AutoScroll = true;
				Native.Dark(workspacePanel);
				workspacePanel.Visible = i == 0;
				page.Controls.Add(workspacePanel);
				panels[i] = workspacePanel;
			}
			pageTabPanels[page] = panels;
			tabs.IndexChanged = delegate(int index)
			{
				for (int j = 0; j < panels.Length; j++)
				{
					if (j != index)
					{
						if (panels[j].Visible)
						{
							tabScrollPositions[panels[j]] = new Point(-panels[j].AutoScrollPosition.X, -panels[j].AutoScrollPosition.Y);
						}
						panels[j].Visible = false;
					}
				}
				panels[index].Visible = true;
				Point value;
				if (tabScrollPositions.TryGetValue(panels[index], out value))
				{
					panels[index].AutoScrollPosition = value;
				}
			};
			return panels;
		}
		internal static bool ShouldRunUi(bool visible, FormWindowState windowState)
		{
			if (visible)
			{
				return windowState != FormWindowState.Minimized;
			}
			return false;
		}
		private static void PaintTree(Control c)
		{
			if (c.IsHandleCreated && c.Visible)
			{
				c.Update();
				for (int i = 0; i < c.Controls.Count; i++)
				{
					PaintTree(c.Controls[i]);
				}
			}
		}
		private bool AnyDialogOpen()
		{
			try
			{
				if (base.IsHandleCreated && !IsWindowEnabled(base.Handle))
				{
					return true;
				}
				foreach (Form openForm in Application.OpenForms)
				{
					if (!object.ReferenceEquals(openForm, this) && !(openForm is PageReveal) && openForm.Visible)
					{
						return true;
					}
				}
			}
			catch
			{
			}
			return false;
		}
	}
}
