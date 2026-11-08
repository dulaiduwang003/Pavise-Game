using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class LolCardPanel : GameCardExtension
	{
		private static int fileOpBusy;

		private readonly LolOptimizationService service;

		private readonly PillButton btnClean;

		private readonly PillButton btnRestore;

		private readonly PillButton btnDelete;

		private readonly Toggle swCleanup;

		private readonly Toggle swHeadless;

		private readonly System.Windows.Forms.Timer tick;

		private readonly ToolTip tips = new ToolTip();

		private Color surface = Theme.Card;

		private LolOptimizationSnapshot last;

		private int uiBusy;

		private int inspectBusy;

		private string inspectRoot = "";

		private string inspectError = "";

		private DateTime inspectUtc = DateTime.MinValue;

		private int inspectSignature = -1;

		private int candidateCount;

		private bool canDelete;

		private bool inspectBlocked;

		private bool inspectCompleted;

		private readonly List<string> blockers = new List<string>();

		public override int PreferredHeight
		{
			get
			{
				return Theme.S(98);
			}
		}

		private bool CompactLayout
		{
			get
			{
				return base.ClientSize.Width < Theme.S(560);
			}
		}

		public LolCardPanel(LolOptimizationService svc)
		{
			service = svc;
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			base.TabStop = false;
			BackColor = surface;
			base.AccessibleRole = AccessibleRole.Grouping;
			base.AccessibleName = "英雄联盟增强";
			tips.InitialDelay = 500;
			tips.ReshowDelay = 120;
			tips.AutoPopDelay = 18000;
			tips.ShowAlways = true;
			btnClean = MakeButton(Lang.T("col.btn.clean"), BtnKind.Normal, delegate
			{
				RunAction(() => service.CleanNow());
			});
			btnRestore = MakeButton(Lang.T("col.btn.restore"), BtnKind.Normal, delegate
			{
				RunAction(() => service.RestoreNow());
			});
			btnDelete = MakeButton(Lang.T("col.btn.delete"), BtnKind.Danger, delegate
			{
				OnDelete();
			});
			tips.SetToolTip(btnDelete, Lang.T("col.addon.tip"));
			swCleanup = MakeToggle(service.CleanupEnabled, delegate
			{
				service.CleanupEnabled = swCleanup.Checked;
			});
			swHeadless = MakeToggle(service.HeadlessEnabled, delegate
			{
				service.HeadlessEnabled = swHeadless.Checked;
			});
			swCleanup.AccessibleName = Lang.T("col.cleanup");
			swHeadless.AccessibleName = Lang.T("col.headless");
			tips.SetToolTip(swCleanup, Lang.T("col.cleanup.tip"));
			tips.SetToolTip(swHeadless, Lang.T("col.headless.tip"));
			tick = new System.Windows.Forms.Timer();
			tick.Interval = 2000;
			tick.Tick += delegate
			{
				if (base.Visible && FindForm() != null)
				{
					RefreshState(true);
				}
			};
			service.Changed += OnServiceChanged;
			base.Disposed += delegate
			{
				service.Changed -= OnServiceChanged;
				try
				{
					tick.Stop();
					tick.Dispose();
				}
				catch
				{
				}
				try
				{
					tips.Dispose();
				}
				catch
				{
				}
			};
			RefreshState(false);
		}

		public override int PreferredHeightForWidth(int width)
		{
			return Theme.S((width < Theme.S(560)) ? 208 : 98);
		}

		public override void SetSurface(Color value)
		{
			surface = value;
			BackColor = value;
			foreach (Control control in base.Controls)
			{
				FxControl fxControl = control as FxControl;
				if (fxControl != null)
				{
					fxControl.Bg = value;
					fxControl.Invalidate();
				}
			}
			Invalidate();
		}

		private PillButton MakeButton(string text, BtnKind kind, EventHandler onClick)
		{
			PillButton pillButton = new PillButton(text, kind);
			pillButton.Bg = surface;
			pillButton.Font = Theme.UI(8.4f, false);
			pillButton.Height = Theme.S(30);
			pillButton.Width = TextRenderer.MeasureText(text, pillButton.Font, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(30);
			pillButton.Click += onClick;
			base.Controls.Add(pillButton);
			return pillButton;
		}

		private void SetButtonText(PillButton b, string text)
		{
			if (!string.Equals(b.Text, text, StringComparison.Ordinal))
			{
				b.Text = text;
				int widthValue = TextRenderer.MeasureText(text, b.Font, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(30);
				if (b.Width != widthValue)
				{
					b.Width = widthValue;
					PerformLayout();
				}
				b.Invalidate();
			}
		}

		private Toggle MakeToggle(bool on, EventHandler handler)
		{
			Toggle toggle = new Toggle();
			toggle.Size = new Size(Theme.S(42), Theme.S(22));
			toggle.Bg = surface;
			toggle.SetSilently(on);
			toggle.CheckedChanged += handler;
			base.Controls.Add(toggle);
			return toggle;
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			base.OnVisibleChanged(e);
			if (base.Visible)
			{
				tick.Start();
				RefreshState(true);
			}
			else
			{
				tick.Stop();
			}
		}

		protected override void OnLayout(LayoutEventArgs levent)
		{
			base.OnLayout(levent);
			if (swHeadless == null || btnDelete == null)
			{
				return;
			}
			int width = base.ClientSize.Width;
			int scaledDimension = Theme.S(8);
			if (CompactLayout)
			{
				int width2 = Math.Max(1, (width - scaledDimension) / 2);
				btnClean.SetBounds(0, Theme.S(46), width2, Theme.S(30));
				btnRestore.SetBounds(width2 + scaledDimension, Theme.S(46), Math.Max(1, width - width2 - scaledDimension), Theme.S(30));
				swCleanup.Location = new Point(Math.Max(0, width - swCleanup.Width), Theme.S(90));
				swHeadless.Location = new Point(Math.Max(0, width - swHeadless.Width), Theme.S(124));
				btnDelete.SetBounds(0, Theme.S(160), width, Theme.S(30));
				return;
			}
			PillButton[] pillButtonItems = new PillButton[3] { btnClean, btnRestore, btnDelete };
			foreach (PillButton pillButton in pillButtonItems)
			{
				pillButton.Width = TextRenderer.MeasureText(pillButton.Text, pillButton.Font, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(30);
			}
			int scaledDimension2 = Theme.S(10);
			int scaledDimension3 = Theme.S(58);
			int onLayoutValue = width;
			PillButton[] pillButtonItems2 = new PillButton[2] { btnRestore, btnClean };
			foreach (PillButton pillButton2 in pillButtonItems2)
			{
				if (pillButton2.Visible)
				{
					onLayoutValue -= pillButton2.Width;
					pillButton2.Location = new Point(onLayoutValue, scaledDimension2);
					onLayoutValue -= scaledDimension;
				}
			}
			btnDelete.Location = new Point(width - btnDelete.Width, scaledDimension3);
			int scaledDimension4 = Theme.S(4);
			if (swCleanup.Visible)
			{
				scaledDimension4 += LabelWidth(Lang.T("col.cleanup")) + Theme.S(8);
				swCleanup.Location = new Point(scaledDimension4, scaledDimension3 + Theme.S(4));
				scaledDimension4 += swCleanup.Width + Theme.S(22);
			}
			scaledDimension4 += LabelWidth(Lang.T("col.headless")) + Theme.S(8);
			swHeadless.Location = new Point(scaledDimension4, scaledDimension3 + Theme.S(4));
		}

		private static int LabelWidth(string text)
		{
			return TextRenderer.MeasureText(text, Theme.UI(8.2f, true), Size.Empty, TextFormatFlags.NoPadding).Width;
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			using (SolidBrush brush = new SolidBrush(surface))
			{
				e.Graphics.FillRectangle(brush, base.ClientRectangle);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			using (Pen pen = new Pen(Theme.Stroke))
			{
				graphics.DrawLine(pen, 0, 0, base.Width, 0);
			}
			LolOptimizationSnapshot lolOptimizationSnapshot = last;
			int left = base.Width;
			if (!CompactLayout)
			{
				PillButton[] pillButtonItems = new PillButton[2] { btnClean, btnRestore };
				foreach (PillButton pillButton in pillButtonItems)
				{
					if (pillButton.Visible && pillButton.Left < left)
					{
						left = pillButton.Left;
					}
				}
			}
			left -= Theme.S(14);
			string message;
			Color color;
			StateOf(lolOptimizationSnapshot, out message, out color);
			int scaledDimension = Theme.S(25);
			int width = Theme.S(7);
			using (SolidBrush brush = new SolidBrush(color))
			{
				graphics.FillEllipse(brush, Theme.S(4), scaledDimension - width / 2, width, width);
			}
			Font font = Theme.UI(8.6f, true);
			int val = TextRenderer.MeasureText(message, font, Size.Empty, TextFormatFlags.NoPadding).Width;
			Rectangle bounds = new Rectangle(Theme.S(17), scaledDimension - Theme.S(10), Math.Min(val, Math.Max(1, left - Theme.S(17))), Theme.S(20));
			TextRenderer.DrawText(graphics, message, font, bounds, color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			int rightOffset = bounds.Right + Theme.S(18);
			Font capFont = Theme.UI(7.4f, false);
			Font valFont = Theme.UI(7.4f, true);
			if (lolOptimizationSnapshot != null)
			{
				if (!lolOptimizationSnapshot.InstallationFound)
				{
					rightOffset = DrawCell(graphics, rightOffset, scaledDimension, left, capFont, valFont, Lang.T("col.cell.install"), lolOptimizationSnapshot.Discovering ? Lang.T("col.install.wait") : Lang.T("col.install.none"), Theme.Faint);
				}
				rightOffset = DrawCell(graphics, rightOffset, scaledDimension, left, capFont, valFont, Lang.T("col.cell.lcu"), lolOptimizationSnapshot.LcuReady ? Lang.T("col.lcu.on") : Lang.T("col.lcu.off"), lolOptimizationSnapshot.LcuReady ? Theme.Green : Theme.Faint);
				rightOffset = DrawCell(graphics, rightOffset, scaledDimension, left, capFont, valFont, Lang.T("col.cell.phase"), lolOptimizationSnapshot.ClientRunning ? PhaseText(lolOptimizationSnapshot.Phase) : Lang.T("col.client.off"), lolOptimizationSnapshot.GameRunning ? Theme.Green : (lolOptimizationSnapshot.ClientRunning ? Theme.Dim : Theme.Faint));
				DrawCell(graphics, rightOffset, scaledDimension, left, capFont, valFont, Lang.T("col.cell.ux"), lolOptimizationSnapshot.HeadlessActive ? Lang.T("col.ux.closed") : ((lolOptimizationSnapshot.UxProcessCount > 0) ? Lang.T("col.ux.visible") : Lang.T("col.proc.none")), lolOptimizationSnapshot.HeadlessActive ? Theme.Green : ((lolOptimizationSnapshot.UxProcessCount > 0) ? Theme.Dim : Theme.Faint));
			}
			int scaledDimension2 = Theme.S(69);
			Font font2 = Theme.UI(8.2f, true);
			if (CompactLayout)
			{
				if (swCleanup.Visible)
				{
					TextRenderer.DrawText(graphics, Lang.T("col.cleanup"), font2, new Rectangle(Theme.S(4), swCleanup.Top, Math.Max(1, swCleanup.Left - Theme.S(12)), swCleanup.Height), swCleanup.Enabled ? Theme.Fg : Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				}
				TextRenderer.DrawText(graphics, Lang.T("col.headless"), font2, new Rectangle(Theme.S(4), swHeadless.Top, Math.Max(1, swHeadless.Left - Theme.S(12)), swHeadless.Height), swHeadless.Enabled ? Theme.Fg : Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				return;
			}
			if (swCleanup.Visible)
			{
				TextRenderer.DrawText(graphics, Lang.T("col.cleanup"), font2, new Rectangle(Theme.S(4), scaledDimension2 - Theme.S(10), LabelWidth(Lang.T("col.cleanup")) + 2, Theme.S(20)), swCleanup.Enabled ? Theme.Fg : Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			int rightOffset2 = (swCleanup.Visible ? (swCleanup.Right + Theme.S(22)) : Theme.S(4));
			TextRenderer.DrawText(graphics, Lang.T("col.headless"), font2, new Rectangle(rightOffset2, scaledDimension2 - Theme.S(10), LabelWidth(Lang.T("col.headless")) + 2, Theme.S(20)), swHeadless.Enabled ? Theme.Fg : Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}

		private static int DrawCell(Graphics g, int x, int cy, int limit, Font capFont, Font valFont, string cap, string val, Color valColor)
		{
			int width = TextRenderer.MeasureText(cap, capFont, Size.Empty, TextFormatFlags.NoPadding).Width;
			int width2 = TextRenderer.MeasureText(val, valFont, Size.Empty, TextFormatFlags.NoPadding).Width;
			if (x + width + Theme.S(5) + width2 > limit)
			{
				return limit;
			}
			TextRenderer.DrawText(g, cap, capFont, new Rectangle(x, cy - Theme.S(9), width + 2, Theme.S(18)), Theme.Faint, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			x += width + Theme.S(5);
			TextRenderer.DrawText(g, val, valFont, new Rectangle(x, cy - Theme.S(9), width2 + 2, Theme.S(18)), valColor, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			return x + width2 + Theme.S(14);
		}

		private static void StateOf(LolOptimizationSnapshot s, out string text, out Color color)
		{
			if (s == null || !s.Enabled)
			{
				text = Lang.T("col.state.off");
				color = Theme.Faint;
			}
			else if (s.Discovering)
			{
				text = Lang.T("col.state.scanning");
				color = Theme.Accent;
			}
			else if (s.HeadlessActive)
			{
				text = Lang.T("col.state.headless");
				color = Theme.Green;
			}
			else if (s.LcuReady)
			{
				text = Lang.T("col.state.linked");
				color = Theme.Green;
			}
			else
			{
				text = Lang.T("col.state.wait");
				color = Theme.Dim;
			}
		}

		private static string PhaseText(string phase)
		{
			string phaseOffset = phase ?? "";
			if (phaseOffset.Equals("InProgress", StringComparison.OrdinalIgnoreCase))
			{
				return Lang.T("col.phase.ingame");
			}
			if (phaseOffset.Equals("ChampSelect", StringComparison.OrdinalIgnoreCase))
			{
				return Lang.T("col.phase.champselect");
			}
			if (phaseOffset.Equals("Matchmaking", StringComparison.OrdinalIgnoreCase))
			{
				return Lang.T("col.phase.matchmaking");
			}
			if (phaseOffset.Equals("ReadyCheck", StringComparison.OrdinalIgnoreCase))
			{
				return Lang.T("col.phase.readycheck");
			}
			if (phaseOffset.Equals("WaitingForStats", StringComparison.OrdinalIgnoreCase))
			{
				return Lang.T("col.phase.stats");
			}
			if (phaseOffset.Equals("Lobby", StringComparison.OrdinalIgnoreCase))
			{
				return Lang.T("col.phase.lobby");
			}
			if (phaseOffset.Equals("None", StringComparison.OrdinalIgnoreCase) || phaseOffset.Length == 0)
			{
				return Lang.T("col.phase.idle");
			}
			return phaseOffset;
		}

		private void OnServiceChanged()
		{
			if (base.IsDisposed || !base.IsHandleCreated)
			{
				return;
			}
			try
			{
				BeginInvoke((MethodInvoker)delegate
				{
					if (!base.IsDisposed)
					{
						RefreshState(true);
					}
				});
			}
			catch
			{
			}
		}

		private void RefreshState(bool inspectFiles)
		{
			if (!base.IsDisposed)
			{
				LolOptimizationSnapshot snapshot;
				try
				{
					snapshot = service.GetSnapshot();
				}
				catch (Exception ex)
				{
					Logger.Log("英雄联盟增强 读取状态失败 " + ex.Message);
					return;
				}
				last = snapshot;
				swCleanup.SetSilently(snapshot.CleanupEnabled);
				swHeadless.SetSilently(snapshot.HeadlessEnabled);
				bool isNotEnabled = !snapshot.Enabled;
				bool anyConditionMet = Interlocked.CompareExchange(ref uiBusy, 0, 0) != 0 || Interlocked.CompareExchange(ref fileOpBusy, 0, 0) != 0;
				bool matchesDiscovering = anyConditionMet || isNotEnabled || snapshot.Discovering;
				swCleanup.Enabled = !isNotEnabled;
				swHeadless.Enabled = !isNotEnabled;
				if (swCleanup.Visible != snapshot.WeGameFound)
				{
					swCleanup.Visible = snapshot.WeGameFound;
					PerformLayout();
				}
				btnClean.Enabled = !matchesDiscovering && snapshot.ClientRunning && snapshot.LcuReady;
				btnRestore.Enabled = !matchesDiscovering && snapshot.ClientRunning && (snapshot.HeadlessActive || snapshot.UxProcessCount == 0);
				if (inspectFiles)
				{
					QueueInspection(snapshot);
				}
				bool inspecting = Interlocked.CompareExchange(ref inspectBusy, 0, 0) != 0;
				SetButtonText(btnDelete, AddonActionText(inspecting, inspectCompleted, candidateCount, inspectError));
				btnDelete.Kind = ((candidateCount > 0) ? BtnKind.Danger : BtnKind.Normal);
				tips.SetToolTip(btnDelete, (!string.IsNullOrEmpty(inspectError)) ? inspectError : ((inspectCompleted && candidateCount == 0) ? Lang.T("col.addon.clean") : Lang.T("col.addon.tip")));
				btnDelete.Enabled = CanRequestAddonDeletion(snapshot, anyConditionMet, inspecting, canDelete || candidateCount > 0, inspectBlocked || blockers.Count > 0, inspectError);
				PerformLayout();
				Invalidate();
			}
		}

		internal static bool IsAddonDeletionBlocked(LolOptimizationSnapshot snapshot, bool inspectionBlocked)
		{
			if (!inspectionBlocked)
			{
				if (snapshot != null)
				{
					if (!snapshot.ClientRunning && !snapshot.GameRunning && !snapshot.WeGameMainRunning && snapshot.WeGameProcessCount <= 0 && snapshot.UxProcessCount <= 0)
					{
						return snapshot.CrossProcessCount > 0;
					}
					return true;
				}
				return false;
			}
			return true;
		}

		internal static bool CanRequestAddonDeletion(LolOptimizationSnapshot snapshot, bool busy, bool inspecting, bool hasCandidates, bool inspectionBlocked, string inspectionError)
		{
			if (snapshot != null && snapshot.Enabled && !snapshot.Discovering && !busy && !inspecting && hasCandidates)
			{
				return string.IsNullOrEmpty(inspectionError);
			}
			return false;
		}

		internal static string AddonActionText(bool inspecting, bool completed, int count, string error)
		{
			if (inspecting)
			{
				return Lang.T("col.btn.addon.checking");
			}
			if (!string.IsNullOrEmpty(error))
			{
				return Lang.T("col.btn.addon.error");
			}
			if (!completed)
			{
				return Lang.T("col.btn.addon.unknown");
			}
			return Lang.T((count > 0) ? "col.btn.delete" : "col.btn.addon.clean");
		}

		private void RunAction(Func<bool> action)
		{
			if (action == null || Interlocked.CompareExchange(ref fileOpBusy, 0, 0) != 0 || Interlocked.CompareExchange(ref uiBusy, 1, 0) != 0)
			{
				return;
			}
			SetButtonsBusy();
			ThreadPool.QueueUserWorkItem(delegate
			{
				bool ok = false;
				string error = "";
				try
				{
					ok = action();
					if (!ok)
					{
						error = service.GetSnapshot().LastError;
					}
				}
				catch (Exception ex)
				{
					error = ex.Message;
				}
				Interlocked.Exchange(ref uiBusy, 0);
				try
				{
					BeginInvoke((MethodInvoker)delegate
					{
						if (!base.IsDisposed)
						{
							RefreshState(true);
							if (!ok && !string.IsNullOrEmpty(error))
							{
								PaviseDialog.Warn(FindForm(), "PAVISE AUTO", error);
							}
						}
					});
				}
				catch
				{
				}
			});
		}

		private void SetButtonsBusy()
		{
			btnClean.Enabled = false;
			btnRestore.Enabled = false;
			btnDelete.Enabled = false;
		}

		private readonly ProcessActionFailureBudget inspectionFailures = new ProcessActionFailureBudget();

		private void QueueInspection(LolOptimizationSnapshot s)
		{
			string root = ((s != null) ? s.LolRoot : null) ?? "";
			if (root.Length == 0)
			{
				inspectRoot = "";
				inspectUtc = DateTime.MinValue;
				inspectSignature = -1;
				canDelete = false;
				inspectBlocked = false;
				inspectCompleted = false;
				candidateCount = 0;
				inspectError = "";
				blockers.Clear();
				return;
			}
			bool isNotValuesEqual = !string.Equals(root, inspectRoot, StringComparison.OrdinalIgnoreCase);
			int combinedMask = (s.ClientRunning ? 1 : 0) | (s.GameRunning ? 2 : 0) | ((s.WeGameProcessCount > 0) ? 4 : 0) | ((s.UxProcessCount > 0) ? 8 : 0) | ((s.CrossProcessCount > 0) ? 16 : 0);
			if (!inspectionFailures.Allowed(0, combinedMask, root, "availability")) return;
			bool matchesCombinedMask = combinedMask != inspectSignature;
			TimeSpan timeSpan = ((inspectBlocked && combinedMask == 0) ? TimeSpan.FromSeconds(5.0) : TimeSpan.FromMinutes(2.0));
			if ((!isNotValuesEqual && !matchesCombinedMask && DateTime.UtcNow - inspectUtc < timeSpan) || Interlocked.CompareExchange(ref fileOpBusy, 0, 0) != 0 || Interlocked.CompareExchange(ref inspectBusy, 1, 0) != 0)
			{
				return;
			}
			inspectSignature = combinedMask;
			if (isNotValuesEqual)
			{
				canDelete = false;
				inspectBlocked = false;
				inspectCompleted = false;
				candidateCount = 0;
				inspectError = "";
				inspectUtc = DateTime.MinValue;
				blockers.Clear();
			}
			inspectRoot = root;
			ThreadPool.QueueUserWorkItem(delegate
			{
				bool can = false;
				bool blocked = false;
				int count = 0;
				string error = "";
				List<string> names = new List<string>();
				try
				{
					LolAddonCleaner.Inspection inspection = LolAddonCleaner.InspectAvailability(root);
					can = inspection.CanDelete;
					blocked = inspection.IsBlocked;
					count = inspection.CandidateCount;
					error = inspection.Error;
					names.AddRange(inspection.BlockingProcesses);
				}
				catch (Exception ex)
				{
					error = ex.Message;
				}
				if (string.IsNullOrEmpty(error)) inspectionFailures.Succeeded(0, combinedMask, root, "availability");
				else inspectionFailures.Failed(0, combinedMask, root, "availability");
				try
				{
					BeginInvoke((MethodInvoker)delegate
					{
						if (base.IsDisposed)
						{
							Interlocked.Exchange(ref inspectBusy, 0);
						}
						else if (!string.Equals(root, service.GetSnapshot().LolRoot, StringComparison.OrdinalIgnoreCase))
						{
							Interlocked.Exchange(ref inspectBusy, 0);
							RefreshState(true);
						}
						else
						{
							canDelete = can;
							inspectBlocked = blocked;
							candidateCount = count;
							inspectCompleted = true;
							blockers.Clear();
							blockers.AddRange(names);
							inspectError = error ?? "";
							inspectUtc = DateTime.UtcNow;
							Interlocked.Exchange(ref inspectBusy, 0);
							if (!base.IsDisposed)
							{
								RefreshState(false);
							}
						}
					});
				}
				catch
				{
					Interlocked.Exchange(ref inspectBusy, 0);
				}
			});
		}

		private void OnDelete()
		{
			LolOptimizationSnapshot snapshot = service.GetSnapshot();
			bool busy = Interlocked.CompareExchange(ref uiBusy, 0, 0) != 0 || Interlocked.CompareExchange(ref fileOpBusy, 0, 0) != 0;
			bool inspecting = Interlocked.CompareExchange(ref inspectBusy, 0, 0) != 0;
			bool inspectionBlocked = inspectBlocked || blockers.Count > 0;
			if (!CanRequestAddonDeletion(snapshot, busy, inspecting, canDelete || candidateCount > 0, inspectionBlocked, inspectError))
			{
				return;
			}
			if (IsAddonDeletionBlocked(snapshot, inspectionBlocked))
			{
				string body = PerformancePolicy.Text("请先完全退出 WeGame 和英雄联盟（包括仍在后台运行的相关进程），再点击“删除附加层”重试。", "Fully exit WeGame and League of Legends, including their background processes, then try Delete add-ons again.");
				inspectUtc = DateTime.MinValue;
				PaviseDialog.Warn(FindForm(), Lang.T("col.btn.delete"), body);
			}
			else
			{
				if (!canDelete || !PaviseDialog.Confirm(FindForm(), Lang.T("col.btn.delete"), Lang.T("col.addon.confirm"), DlgKind.Danger))
				{
					return;
				}
				string root = snapshot.LolRoot;
				if (string.IsNullOrEmpty(root) || Interlocked.CompareExchange(ref fileOpBusy, 1, 0) != 0)
				{
					return;
				}
				SetButtonsBusy();
				Invalidate();
				ThreadPool.QueueUserWorkItem(delegate
				{
					bool success = false;
					string message = "";
					try
					{
						LolAddonCleaner.OperationResult operationResult = LolAddonCleaner.Delete(root);
						success = operationResult.Success;
						message = operationResult.Message;
						Logger.Log("英雄联盟附加层删除 " + message);
					}
					catch (Exception ex)
					{
						message = ex.Message;
					}
					Interlocked.Exchange(ref fileOpBusy, 0);
					try
					{
						BeginInvoke((MethodInvoker)delegate
						{
							if (!base.IsDisposed)
							{
								inspectUtc = DateTime.MinValue;
								RefreshState(true);
								if (!string.IsNullOrEmpty(message))
								{
									if (success)
									{
										PaviseDialog.Info(FindForm(), "PAVISE AUTO", message);
									}
									else
									{
										PaviseDialog.Warn(FindForm(), "PAVISE AUTO", message);
									}
								}
							}
						});
					}
					catch
					{
					}
				});
			}
		}

		public static void WaitForFileOps(int waitMs)
		{
			DateTime dateTime = DateTime.UtcNow.AddMilliseconds(Math.Max(0, waitMs));
			while (Interlocked.CompareExchange(ref fileOpBusy, 0, 0) != 0 && DateTime.UtcNow < dateTime)
			{
				Thread.Sleep(50);
			}
		}
	}
}
