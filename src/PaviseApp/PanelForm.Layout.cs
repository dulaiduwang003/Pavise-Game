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
		private void LayoutAboutViewport()
		{
			if (aboutArranging || pageAbout == null || aboutHeroPanel == null || aboutIdentityPanel == null || aboutReleasePanel == null || aboutHeroPanel.IsDisposed || aboutViewport == pageAbout.ClientSize)
			{
				return;
			}
			aboutArranging = true;
			pageAbout.SuspendLayout();
			try
			{
				for (int i = 0; i < 3; i++)
				{
					int num = Math.Max(240, Dpi.U(pageAbout.ClientSize.Width));
					int num2 = Math.Max(1, Dpi.U(pageAbout.ClientSize.Height));
					int num3 = ((num < 500) ? 16 : 24);
					int num4 = Math.Max(1, num - num3 * 2);
					bool flag = num < 900;
					bool flag2 = num4 < 430;
					int num5 = ((!flag) ? 130 : (flag2 ? 328 : 210));
					int num6 = aboutContentTop + num5 + 12;
					int num7 = (flag ? 366 : Math.Max(366, num2 - num6 - 16));
					int num8 = 14;
					int num9 = (flag ? num4 : ((num4 - num8) / 2));
					int v = (flag ? num4 : (num4 - num9 - num8));
					int num10 = (flag ? (num6 + num7 + 12) : num6);
					int v2 = num10 + num7 + 16;
					bool flag3 = Theme.S(v2) > pageAbout.ClientSize.Height;
					pageAbout.AutoScrollMargin = Size.Empty;
					pageAbout.AutoScrollMinSize = (flag3 ? new Size(0, Theme.S(v2)) : Size.Empty);
					pageAbout.AutoScroll = flag3;
					if (!flag3)
					{
						pageAbout.AutoScrollPosition = Point.Empty;
					}
					int num11 = pageAbout.AutoScrollPosition.Y;
					aboutHeroPanel.SetBounds(Theme.S(num3), Theme.S(aboutContentTop) + num11, Theme.S(num4), Theme.S(num5));
					aboutIdentityPanel.SetBounds(Theme.S(num3), Theme.S(num6) + num11, Theme.S(num9), Theme.S(num7));
					aboutReleasePanel.SetBounds(Theme.S(flag ? num3 : (num3 + num9 + num8)), Theme.S(num10) + num11, Theme.S(v), Theme.S(num7));
					int v3 = ((num4 < 430) ? 64 : 84);
					int num12 = ((num4 < 430) ? 100 : 132);
					aboutIcon.SetBounds(Theme.S(24), Theme.S(23), Theme.S(v3), Theme.S(v3));
					int num13 = ((!flag) ? Math.Max(132, Dpi.U(TextRenderer.MeasureText("WINDOWS x64", Theme.UI(9.2f, true), Size.Empty, TextFormatFlags.NoPadding).Width) + 4) : (flag2 ? (num4 - 40) : ((num4 - 56) / 3)));
					int num14 = ((!flag) ? (num4 - 20 - (num13 * 3 + 24)) : 20);
					int val = ((!flag) ? (num14 - num12 - 16) : (num4 - num12 - 20));
					int[] array = new int[4] { 12, 31, 71, 96 };
					int[] array2 = new int[4] { 20, 40, 22, 29 };
					for (int j = 0; j < aboutHeroLabels.Length; j++)
					{
						aboutHeroLabels[j].SetBounds(Theme.S(num12), Theme.S(array[j]), Theme.S(Math.Max(1, val)), Theme.S(array2[j]));
					}
					for (int k = 0; k < aboutMetricControls.Length; k++)
					{
						int v4 = (flag ? (flag2 ? 20 : (20 + k * (num13 + 8))) : (num14 + k * (num13 + 12)));
						int num15 = (flag ? (flag2 ? (140 + k * 60) : 140) : 27);
						Control[] array3 = aboutMetricControls[k];
						array3[0].SetBounds(Theme.S(v4), Theme.S(num15), Theme.S(num13), Theme.S(18));
						array3[1].SetBounds(Theme.S(v4), Theme.S(num15 + 22), Theme.S(num13), Theme.S(27));
						array3[2].SetBounds(Theme.S(v4), Theme.S(num15 + 52), Theme.S(Math.Max(1, num13 - 14)), Theme.S(1));
					}
					LayoutAboutActions(aboutReleasePanel, "aboutReleaseNotes", "aboutWebsiteDownload");
					if (Dpi.U(pageAbout.ClientSize.Width) == num)
					{
						break;
					}
				}
				aboutViewport = pageAbout.ClientSize;
			}
			finally
			{
				pageAbout.ResumeLayout(true);
				aboutArranging = false;
			}
		}
		private void LayoutAboutActions(Control card, string firstName, string secondName)
		{
			Control control = card.Controls[firstName];
			Control control2 = card.Controls[secondName];
			if (control != null && control2 != null)
			{
				int num = Math.Max(1, card.ClientSize.Width - Theme.S(52));
				int num2 = num / 2;
				int num3 = Math.Max(0, card.ClientSize.Height - Theme.S(50));
				control.SetBounds(Theme.S(20), num3, num2, Theme.S(36));
				control2.SetBounds(Theme.S(32) + num2, num3, num - num2, Theme.S(36));
			}
		}
		private void LayoutAntiCheatViewport()
		{
			if (acRosterBar == null || acList == null || acArranging || acViewport == null)
			{
				return;
			}
			AcViewState acViewState = CaptureAcViewState();
			acArranging = true;
			try
			{
				int num = Theme.S((pageAntiCheat.ClientSize.Width < Theme.S(500)) ? 16 : 24);
				int num2 = Theme.S(16);
				acViewport.SetBounds(num, num2, Math.Max(1, pageAntiCheat.ClientSize.Width - num * 2), Math.Max(1, pageAntiCheat.ClientSize.Height - num2 - Theme.S(8)));
				Point point = new Point(0, Math.Max(0, -acViewport.AutoScrollPosition.Y));
				acViewport.AutoScrollPosition = Point.Empty;
				int num3 = Math.Max(1, acViewport.Width);
				int num4 = Theme.S(8);
				int num5 = Theme.S(20);
				int num6 = Theme.S(64);
				bool flag = num3 < Theme.S(840);
				int num7 = ((!flag) ? Math.Max(Theme.S(290), Math.Min(Theme.S(360), (int)((float)num3 * 0.29f))) : 0);
				int num8 = (flag ? num4 : (num7 + num5));
				int num9 = Math.Max(1, num3 - num8 - num4);
				int num10 = (flag ? (num3 - num4 * 2) : (num7 - num4));
				int num11 = LayoutAcControlBay(num10, flag);
				int num12 = 0;
				foreach (SettingCard acCard in acCards)
				{
					num12 += ((AntiCheatModuleRow)acCard).MeasureHeight(num9);
				}
				num12 += Math.Max(0, acCards.Count - 1);
				int num13 = (flag ? (num11 + Theme.S(20)) : num4);
				int val = Math.Max((!flag) ? (num11 + num4) : 0, num13 + num12 + num6 + num4);
				int num14 = Math.Max(val, Math.Min(Theme.S(540), acViewport.Height));
				bool flag2 = num14 > acViewport.Height;
				if (flag2)
				{
					num3 = Math.Max(1, num3 - SystemInformation.VerticalScrollBarWidth);
					num7 = ((!flag) ? Math.Max(Theme.S(290), Math.Min(Theme.S(360), (int)((float)num3 * 0.29f))) : 0);
					num8 = (flag ? num4 : (num7 + num5));
					num9 = Math.Max(1, num3 - num8 - num4);
					num10 = (flag ? (num3 - num4 * 2) : (num7 - num4));
					num11 = LayoutAcControlBay(num10, flag);
					num13 = (flag ? (num11 + Theme.S(20)) : num4);
					num12 = 0;
					foreach (SettingCard acCard2 in acCards)
					{
						num12 += ((AntiCheatModuleRow)acCard2).MeasureHeight(num9);
					}
					num12 += Math.Max(0, acCards.Count - 1);
					num14 = Math.Max(num14, Math.Max((!flag) ? (num11 + num4) : 0, num13 + num12 + num6 + num4));
				}
				acRosterBar.SetBounds(0, 0, num3, num14);
				acControlBay.SetBounds(num4, num4, num10, flag ? num11 : (num14 - num4 * 2));
				int num15 = num14 - num4 - num6;
				int num16 = Math.Max(num12, num15 - num13);
				acList.SetBounds(num8, num13, num9, num16);
				int num17 = 0;
				int num18 = 0;
				int num19 = Math.Max(0, num16 - num12);
				foreach (SettingCard acCard3 in acCards)
				{
					AntiCheatModuleRow antiCheatModuleRow = (AntiCheatModuleRow)acCard3;
					int num20 = antiCheatModuleRow.MeasureHeight(num9) + num19 / Math.Max(1, acCards.Count) + ((num18 < num19 % Math.Max(1, acCards.Count)) ? 1 : 0);
					antiCheatModuleRow.SetBounds(0, num17, num9, num20);
					antiCheatModuleRow.ArrangeContent();
					num17 += num20 + 1;
					num18++;
				}
				acUtility.SetBounds(num8, num15, num9, num6);
				int num21 = Theme.S(16);
				int num22 = Math.Max(1, (num9 - num21) / 2);
				acRosterEntry.SetBounds(Theme.S(8), Theme.S(12), Math.Max(1, num22 - Theme.S(8)), Theme.S(42));
				acOtherToggle.SetBounds(num22 + num21, Theme.S(12), Math.Max(1, num9 - num22 - num21 - Theme.S(8)), Theme.S(42));
				acDrawer.SetBounds(num8, num13, num9, num16);
				LayoutAcDrawer(flag2);
				if (flag2 && acDrawer.Visible)
				{
					int num23 = Math.Max(0, acDrawerBody.AutoScrollMinSize.Height - acDrawerBody.Height);
					if (num23 > 0)
					{
						acDrawerBody.Height += num23;
						acDrawer.Height += num23;
						num14 += num23;
						acRosterBar.Height = num14;
						acUtility.Top += num23;
					}
				}
				acRosterBar.SplitX = ((!flag) ? (num7 + Theme.S(8)) : 0);
				acRosterBar.UtilityBounds = new Rectangle(num8, acUtility.Top, num9, 1);
				acRosterBar.Invalidate();
				acList.Invalidate();
				acViewport.AutoScrollMinSize = new Size(0, num14);
				acViewport.AutoScrollPosition = new Point(0, Math.Min(point.Y, DockScrollLimit(acViewport)));
				pageAntiCheat.AutoScrollMinSize = Size.Empty;
				acLayoutScale = Dpi.Scale;
				RestoreAcScrollPosition(acViewState);
			}
			finally
			{
				acArranging = false;
			}
		}
		private int LayoutAcControlBay(int width, bool stacked)
		{
			int num = Theme.S(stacked ? 14 : 22);
			int num2 = Theme.S(stacked ? 14 : 24);
			int num3 = Theme.S(28);
			int num4 = num + num3 + Theme.S(10);
			acTitleIcon.SetBounds(num, num2 + Theme.S(6), num3, num3);
			int val = Math.Max(1, width - num - num4 - Theme.S(26));
			acTitleLabel.SetBounds(num4, num2 + Theme.S(4), val, Theme.S(30));
			int val2 = TextRenderer.MeasureText(acTitleLabel.Text, acTitleLabel.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			acHelp.SetBounds(num4 + Math.Min(val, val2), num2 + Theme.S(6), Theme.S(26), Theme.S(26));
			int num5 = num2 + Theme.S(stacked ? 55 : 86);
			acStrengthLabel.SetBounds(num, num5, width - num * 2, Theme.S(22));
			acModePicker.Horizontal = stacked;
			int num6 = acModePicker.MinimumContentHeight(Math.Max(1, width - num * 2));
			acModePicker.SetBounds(num, num5 + Theme.S(34), Math.Max(1, width - num * 2), num6);
			int num7 = acModePicker.Bottom + Theme.S(22);
			if (stacked)
			{
				acStrengthHelp.SetBounds(Math.Max(num, width - num - Theme.S(126)), num5 - Theme.S(2), Theme.S(126), Theme.S(26));
				return acModePicker.Bottom + Theme.S(16);
			}
			acStrengthHelp.SetBounds(num, num7, width - num * 2, Theme.S(32));
			return acStrengthHelp.Bottom + Theme.S(20);
		}
		private void LayoutAntiCheatCards()
		{
			LayoutAntiCheatViewport();
		}
		private void RestoreAcScrollPosition(AcViewState state)
		{
			if (state != null && !(state.Drawer != acDrawerKind) && curPage == pageAntiCheat)
			{
				if (acDrawerBody.AutoScroll && state.Drawer.Length > 0)
				{
					float num = (state.BodyScroll ? state.InnerY : Math.Max(0f, state.OuterY - state.BodyOrigin));
					int val = DockScrollLimit(acDrawerBody);
					acDrawerBody.AutoScrollPosition = new Point(0, Math.Min((int)Math.Round(num * Dpi.Scale), val));
				}
				float num2 = state.OuterY;
				if (!acDrawerBody.AutoScroll && state.BodyScroll && state.Drawer.Length > 0)
				{
					int num3 = Math.Max(0, -acViewport.AutoScrollPosition.Y);
					num2 = (float)(acRosterBar.Top + num3 + acDrawer.Top + acDrawerBody.Top) / Dpi.Scale + state.InnerY;
				}
				int val2 = DockScrollLimit(acViewport);
				acViewport.AutoScrollPosition = new Point(0, Math.Min((int)Math.Round(num2 * Dpi.Scale), val2));
			}
		}
		private void LayoutAcDrawer(bool outerScroll)
		{
			Point point = new Point(0, Math.Max(0, -acDrawerBody.AutoScrollPosition.Y));
			int num = Theme.S(8);
			int num2 = Theme.S(122);
			int num3 = Theme.S(52);
			acDrawerTitle.SetBounds(num, Theme.S(12), Math.Max(1, acDrawer.Width - num2 - num * 3), Theme.S(26));
			acDrawerClose.SetBounds(Math.Max(num, acDrawer.Width - num2 - num), Theme.S(8), num2, Theme.S(34));
			int num4 = num3;
			if (acDrawerKind == "roster")
			{
				lblAcRoster.SetBounds(num, num3, Math.Max(1, acDrawer.Width - Theme.S(120)), Theme.S(28));
				acClear.SetBounds(Math.Max(num, acDrawer.Width - Theme.S(96)), num3 - Theme.S(3), Theme.S(88), Theme.S(32));
				num4 += Theme.S(42);
			}
			acDrawerBody.SetBounds(num, num4, Math.Max(1, acDrawer.Width - num * 2), Math.Max(1, acDrawer.Height - num4 - num));
			acDrawerBody.AutoScrollPosition = Point.Empty;
			int num5 = Math.Max(1, acDrawerBody.Width - ((!outerScroll) ? SystemInformation.VerticalScrollBarWidth : 0));
			int num6 = 0;
			if (acDrawerKind == "records")
			{
				int num7 = ((num5 < Theme.S(620)) ? 1 : 2);
				int num8 = Theme.S(10);
				int num9 = Math.Max(1, (num5 - (num7 - 1) * num8) / num7);
				for (int i = 0; i < acReadOnlyCards.Count; i++)
				{
					acReadOnlyCards[i].SetBounds(i % num7 * (num9 + num8), i / num7 * Theme.S(62), num9, Theme.S(52));
				}
				num6 = (acReadOnlyCards.Count + num7 - 1) / num7 * Theme.S(62);
			}
			else if (acDrawerKind == "roster")
			{
				foreach (Control acRosterRow in acRosterRows)
				{
					acRosterRow.SetBounds(0, num6, num5, Theme.S(48));
					num6 += Theme.S(54);
				}
			}
			acDrawerBody.AutoScrollMinSize = new Size(0, num6);
			acDrawerBody.AutoScroll = !outerScroll;
			if (!outerScroll)
			{
				acDrawerBody.AutoScrollPosition = new Point(0, Math.Min(point.Y, DockScrollLimit(acDrawerBody)));
			}
		}
		private void LayoutAuditViewport()
		{
			if (auditArranging || pageAudit == null || auditScan == null || btnAuditStart == null || auditScroll == null || auditScan.IsDisposed)
			{
				return;
			}
			auditArranging = true;
			pageAudit.SuspendLayout();
			try
			{
				int val = Math.Max(0, -pageAudit.AutoScrollPosition.Y);
				pageAudit.AutoScrollPosition = Point.Empty;
				pageAudit.AutoScroll = false;
				pageAudit.AutoScrollMinSize = Size.Empty;
				int num = Theme.S(24);
				int num2 = Theme.S(8);
				int num3 = Theme.S(16);
				int num4 = pageAudit.ClientSize.Height;
				bool flag = Volatile.Read(ref auditBusy) != 0;
				bool flag2 = auditReport != null && !flag;
				int num5 = ((flag2 || flag) ? (num3 + Theme.S(44)) : num3);
				int num6 = (flag2 ? Math.Max(1, num4 - num5 - num2) : Math.Max(Theme.S(400), num4 - num5 - num2));
				bool flag3 = !flag2 && num5 + num6 + num2 > num4;
				int num7 = Math.Max(1, pageAudit.ClientSize.Width - num * 2 - (flag3 ? SystemInformation.VerticalScrollBarWidth : 0));
				int num8 = Math.Min(Theme.S(150), Math.Max(1, (num7 - num2) / 2));
				btnAuditRun.SetBounds(num, num3, num8, Theme.S(34));
				btnAuditFixAll.SetBounds(num + num7 - num8, num3, num8, Theme.S(34));
				btnAuditCancel.Bounds = btnAuditFixAll.Bounds;
				lblAuditStatus.SetBounds(btnAuditRun.Right + num2, num3 + Theme.S(8), Math.Max(1, btnAuditFixAll.Left - btnAuditRun.Right - num2 * 2), Theme.S(20));
				auditScan.SetBounds(num, num5, num7, num6);
				int num9 = Math.Min(Theme.S(184), Math.Max(1, num7 - num2 * 2));
				int num10 = Theme.S(38);
				btnAuditStart.SetBounds((num7 - num9) / 2, Math.Min(auditScan.ContentBottom + Theme.S(20), num6 - num10 - num2), num9, num10);
				auditScroll.SetBounds(num, num5, num7, Math.Max(1, num4 - num5 - num2));
				LayoutAuditRows();
				if (flag3)
				{
					pageAudit.AutoScrollMinSize = new Size(0, num5 + num6 + num2);
					pageAudit.AutoScroll = true;
					pageAudit.AutoScrollPosition = new Point(0, Math.Min(val, DockScrollLimit(pageAudit)));
				}
			}
			finally
			{
				pageAudit.ResumeLayout(true);
				auditArranging = false;
			}
		}
		private void LayoutAuditRows()
		{
			int val = Math.Max(0, -auditScroll.AutoScrollPosition.Y);
			auditScroll.SuspendLayout();
			try
			{
				auditScroll.AutoScrollPosition = Point.Empty;
				Control[] array = new Control[auditScroll.Controls.Count];
				auditScroll.Controls.CopyTo(array, 0);
				Array.Sort(array, (Control a, Control b) => a.Top.CompareTo(b.Top));
				int num = 0;
				int auditContentWidth = AuditContentWidth;
				Control[] array2 = array;
				foreach (Control control in array2)
				{
					control.Top += num;
					AuditRowCard auditRowCard = control as AuditRowCard;
					if (auditRowCard != null)
					{
						int num2 = auditRowCard.Height;
						auditRowCard.SetLogicalWidth(auditContentWidth);
						num += auditRowCard.Height - num2;
					}
					else if (control is Label)
					{
						control.Width = Math.Max(1, Theme.S(auditContentWidth + 6) - control.Left);
					}
				}
			}
			finally
			{
				auditScroll.ResumeLayout(true);
			}
			auditScroll.AutoScrollPosition = new Point(0, Math.Min(val, DockScrollLimit(auditScroll)));
		}
		private void LayoutPerformanceViewport()
		{
			if (pagePolicy == null || pagePolicy.IsDisposed || performanceTabPanels == null)
			{
				return;
			}
			int num = ((root != null && !root.IsDisposed) ? root.Left : 0);
			int num2 = ((root != null && !root.IsDisposed) ? root.Top : 0);
			int num3 = Math.Max(0, Math.Min(pagePolicy.ClientSize.Width, base.ClientSize.Width - num - pagePolicy.Left));
			int num4 = Math.Max(0, Math.Min(pagePolicy.ClientSize.Height, base.ClientSize.Height - num2 - pagePolicy.Top));
			pagePolicy.AutoScroll = false;
			pagePolicy.AutoScrollMinSize = Size.Empty;
			int num5 = Theme.S(16);
			performanceViewportTop = num5 + Theme.S(62);
			int num6 = Theme.S(30);
			int num7 = Math.Max(num6, num3 - num6);
			if (performanceCategories != null)
			{
				performanceCategories.SetBounds(num6, num5, num7 - num6, Theme.S(44));
			}
			LayoutConfigurationPresetControls(num6, num5, num7);
			int num8 = Theme.S(20);
			int num9 = Theme.S(8);
			DBPanel[] array = performanceTabPanels;
			foreach (DBPanel dBPanel in array)
			{
				if (dBPanel != null && !dBPanel.IsDisposed)
				{
					dBPanel.SetBounds(num8, performanceViewportTop, Math.Max(0, num3 - num8 * 2), Math.Max(1, num4 - performanceViewportTop - num9));
				}
			}
		}
		private void LayoutCoreEditor()
		{
			LayoutCoreWorkspace();
		}
		private void LayoutCoreViewport()
		{
			LayoutCoreWorkspace();
		}
		private void LayoutCorePage()
		{
			LayoutCoreWorkspace();
		}
		private static int WrappedHeight(Label label)
		{
			return TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, label.Width), int.MaxValue), TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak).Height + Theme.S(2);
		}
		private void LayoutCoreShortcuts()
		{
			int num = 0;
			int num2 = 0;
			int num3 = Theme.S(6);
			int num4 = Theme.S(30);
			foreach (Control control in coreQuickPanel.Controls)
			{
				if (num > 0 && num + control.Width > coreQuickPanel.Width)
				{
					num = 0;
					num2 += num4 + num3;
				}
				control.Location = new Point(num, num2);
				num += control.Width + num3;
			}
			coreQuickPanel.Top = Theme.S(coreWorkspaceCompact ? 38 : 64);
			coreQuickPanel.Height = num2 + num4;
		}
		private void LayoutCriticalThreadEditor()
		{
			if (criticalThreadEditor != null && !criticalThreadEditor.IsDisposed && criticalThreadInfo != null && criticalThreadMode != null)
			{
				int num = Math.Max(1, criticalThreadEditor.Width);
				criticalThreadEnabled.SetBounds(0, 0, num, Theme.S(40));
				int num2 = TextRenderer.MeasureText(criticalThreadEnabled.Text, Theme.UI(8f, true)).Width;
				criticalThreadInfo.SetBounds(Math.Min(Theme.S(36) + num2, Math.Max(0, num - Theme.S(142))), Theme.S(4), Theme.S(28), Theme.S(32));
				criticalThreadMode.SetBounds(Math.Max(0, num - Theme.S(65)), Theme.S(2), Theme.S(64), Theme.S(36));
				criticalThreadResult.SetBounds(Theme.S(12), Theme.S(46), Math.Max(1, num - Theme.S(24)), Theme.S(30));
				if (criticalThreadIssue)
				{
					criticalThreadResult.Height = WrappedHeight(criticalThreadResult);
				}
				criticalThreadEditor.Height = (criticalThreadIssue ? (criticalThreadResult.Bottom + Theme.S(8)) : Theme.S(40));
			}
		}
		private void LayoutCoreWorkspace()
		{
			if (coreWorkspaceArranging || coreWorkspaceSyncing || coreIdentity == null || coreIdentity.IsDisposed || coreMatrixViewport == null || coreRightRail == null || lblCoreState == null)
			{
				return;
			}
			string text = CoreWorkspaceLayoutKey();
			if (text == coreWorkspaceLayoutKey)
			{
				return;
			}
			coreWorkspaceArranging = true;
			CoreViewState coreViewState = CaptureCoreViewState();
			try
			{
				pageCoreScheduling.AutoScroll = false;
				pageCoreScheduling.AutoScrollMinSize = Size.Empty;
				coreWorkspaceCompact = pageCoreScheduling.ClientSize.Height < Theme.S(420);
				int num = Theme.S(24);
				int num2 = Theme.S(coreWorkspaceCompact ? 6 : 16);
				int num3 = Math.Max(1, pageCoreScheduling.ClientSize.Width - num * 2);
				coreIdentity.SetBounds(num, Theme.S(coreWorkspaceCompact ? 6 : 20), num3, Theme.S(coreWorkspaceCompact ? 56 : 88));
				coreEnableToggle.SetBounds(num3 - Theme.S(72), (coreIdentity.Height - Theme.S(30)) / 2, Theme.S(46), Theme.S(30));
				coreEnableInfo.SetBounds(num3 - Theme.S(110), (coreIdentity.Height - Theme.S(36)) / 2, Theme.S(32), Theme.S(36));
				coreEnableLabel.SetBounds(num3 - Theme.S(228), (coreIdentity.Height - Theme.S(36)) / 2, Theme.S(110), Theme.S(36));
				coreScrollPanel.SetBounds(num, coreIdentity.Bottom + num2, num3, Math.Max(1, pageCoreScheduling.ClientSize.Height - coreIdentity.Bottom - num2 - Theme.S(8)));
				coreScrollPanel.AutoScroll = false;
				int num4 = (int)Math.Round((double)(num3 - num2) * 0.59);
				coreEditor.SetBounds(0, 0, num4, coreScrollPanel.Height);
				((CoreWorkspaceSurface)coreEditor).Divider1 = Theme.S(coreWorkspaceCompact ? 36 : 54);
				coreRightRail.SetBounds(num4 + num2, 0, Math.Max(1, num3 - num4 - num2), coreScrollPanel.Height);
				coreRightRail.AutoScrollPosition = Point.Empty;
				int num5 = Theme.S(22);
				int num6 = Math.Max(1, num4 - num5 * 2);
				coreEditorTitle.SetBounds(num5, Theme.S(coreWorkspaceCompact ? 6 : 20), Math.Max(1, num6 - Theme.S(120)), Theme.S(30));
				coreRangeInfo.SetBounds(num5 + Math.Min(Theme.S(140), coreEditorTitle.Width), Theme.S(coreWorkspaceCompact ? 5 : 19), Theme.S(30), Theme.S(32));
				coreSelectedCount.SetBounds(Math.Max(num5, num4 - num5 - Theme.S(115)), Theme.S(coreWorkspaceCompact ? 8 : 22), Theme.S(115), Theme.S(26));
				coreSelectedCount.Text = CpuTopology.MultiGroup ? PerformancePolicy.Text("范围未确认", "Range unconfirmed") : PerformancePolicy.Text("已选 ", "Selected ") + CpuTopology.CountSetBits(coreEditMatrix.Selected) + " / " + CpuTopology.CountSetBits(CpuTopology.AllMask);
				coreQuickPanel.SetBounds(num5, Theme.S(coreWorkspaceCompact ? 38 : 64), num6, coreQuickPanel.Height);
				LayoutCoreShortcuts();
				coreTopologyCount.Text = CpuTopology.MultiGroup ? PerformancePolicy.Text("全机核心范围未确认", "Total CPU range unconfirmed") : CoreMatrix.TopologyCountsConfirmed ? CpuTopology.PhysicalCoreCount + PerformancePolicy.Text(" 物理核 / ", " cores / ") + CpuTopology.CountSetBits(CpuTopology.AllMask) + PerformancePolicy.Text(" 逻辑核", " logical CPUs") : CpuTopology.CountSetBits(CpuTopology.AllMask) + PerformancePolicy.Text(" 逻辑核 · 物理核数未确认", " logical CPUs · physical core count unconfirmed");
				int num7 = 0;
				foreach (Control control in coreQuickPanel.Controls)
				{
					num7 = Math.Max(num7, control.Right);
				}
				int num8 = TextRenderer.MeasureText(coreTopologyCount.Text, coreTopologyCount.Font).Width + Theme.S(6);
				coreTopologyCount.Visible = coreQuickPanel.Height <= Theme.S(30) && num7 + Theme.S(14) + num8 <= num6;
				coreTopologyCount.TextAlign = ContentAlignment.MiddleRight;
				coreTopologyCount.SetBounds(num4 - num5 - num8, coreQuickPanel.Top + Theme.S(3), num8, Theme.S(24));
				coreMatrixViewport.SetBounds(num5 - Theme.S(2), coreQuickPanel.Bottom + Theme.S(coreWorkspaceCompact ? 10 : 18), num6 + Theme.S(6), Math.Max(1, coreEditor.Height - coreQuickPanel.Bottom - Theme.S(coreWorkspaceCompact ? 18 : 34)));
				Point autoScrollPosition = coreMatrixViewport.AutoScrollPosition;
				coreMatrixViewport.AutoScrollPosition = Point.Empty;
				int num9 = Math.Max(1, coreMatrixViewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - Theme.S(4));
				if (coreMatrixLayoutWidth != num9)
				{
					coreMatrixLayoutWidth = num9;
					coreEditMatrix.SetBounds(Theme.S(2), 0, num9, coreEditMatrix.LayoutFor(num9));
				}
				coreMatrixViewport.AutoScrollMinSize = new Size(0, coreEditMatrix.Height);
				coreMatrixViewport.AutoScroll = true;
				coreMatrixViewport.AutoScrollPosition = new Point(0, Math.Max(0, -autoScrollPosition.Y));
				Label label = coreEditorHelp;
				bool visible = (coreEditorRuntime.Visible = false);
				label.Visible = visible;
				int num10 = Math.Max(1, coreRightRail.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
				int num11 = Math.Max(1, num10 - num5 * 2);
				coreIsolationGlyph.SetBounds(num5, Theme.S(27), Theme.S(28), Theme.S(28));
				coreEditorIsolation.Text = PerformancePolicy.Text("核心隔离", "Core isolation");
				bool flag2 = num11 < Theme.S(280);
				int num12 = ((!flag2) ? Theme.S(44) : 0);
				coreIsolationGlyph.Visible = !flag2;
				coreEditorIsolation.SetBounds(num5 + num12, Theme.S(24), Math.Max(1, num11 - num12 - Theme.S(flag2 ? 32 : 88)), Theme.S(30));
				coreEditorIsolation.Height = Math.Max(Theme.S(30), WrappedHeight(coreEditorIsolation));
				coreIsolationInfo.SetBounds(num10 - num5 - Theme.S(flag2 ? 28 : 88), Theme.S(24), Theme.S(28), Theme.S(32));
				coreIsolationToggle.SetBounds(num10 - num5 - Theme.S(46), flag2 ? (coreEditorIsolation.Bottom + Theme.S(8)) : Theme.S(25), Theme.S(46), Theme.S(30));
				coreIsolationHint.SetBounds(num5 + num12, Math.Max(Theme.S(64), Math.Max(coreEditorIsolation.Bottom, coreIsolationToggle.Bottom) + Theme.S(8)), Math.Max(1, num11 - num12), Theme.S(24));
				coreIsolationHint.Height = WrappedHeight(coreIsolationHint);
				int num13 = coreIsolationHint.Bottom + Theme.S(20);
				((CoreWorkspaceSurface)coreRightRail).Divider1 = num13 - Theme.S(10);
				((CoreWorkspaceSurface)coreRightRail).Divider2 = 0;
				corePlanPreview.SetBounds(num5, num13, num11, corePlanPreview.Height);
				num13 = corePlanPreview.Bottom + Theme.S(12);
				AutoCoreStatus status = AutoCores.Status;
				coreStatusPanel.Visible = coreDraftProfile == null || status.Pending || CoreIsolationClient.State == "schedule.isolation.pending" || (status.ProfileId == coreDraftProfile && status.Blocked != null);
				if (coreStatusPanel.Visible)
				{
					coreStatusPanel.SetBounds(num5, num13, num11, Theme.S(80));
					lblCoreState.SetBounds(0, 0, num11, Theme.S(26));
					lblCoreState.Height = WrappedHeight(lblCoreState);
					lblCoreReason.SetBounds(0, lblCoreState.Bottom + Theme.S(4), num11, Theme.S(30));
					lblCoreReason.Height = WrappedHeight(lblCoreReason);
					coreStatusPanel.Height = lblCoreReason.Bottom + Theme.S(8);
					num13 = coreStatusPanel.Bottom + Theme.S(8);
				}
				coreMapPanel.Visible = lblCoreReserved.Text.Length > 0;
				if (coreMapPanel.Visible)
				{
					coreMapPanel.SetBounds(num5, num13, num11, Theme.S(80));
					lblCoreReserved.SetBounds(0, 0, num11, Theme.S(40));
					lblCoreReserved.Height = WrappedHeight(lblCoreReserved);
					btnCoreClearReserved.SetBounds(0, lblCoreReserved.Bottom + Theme.S(6), num11, Theme.S(34));
					coreMapPanel.Height = (btnCoreClearReserved.Visible ? btnCoreClearReserved.Bottom : lblCoreReserved.Bottom) + Theme.S(8);
					num13 = coreMapPanel.Bottom + Theme.S(8);
				}
				coreEditorResult.SetBounds(num5, num13, num11, Theme.S(30));
				if (coreEditorResult.Visible)
				{
					coreEditorResult.Height = WrappedHeight(coreEditorResult);
					num13 = coreEditorResult.Bottom + Theme.S(10);
				}
				coreEvidenceToggle.Text = PerformancePolicy.Text("查看绑定证据  ›", "View affinity evidence  ›");
				coreEvidenceToggle.SetBounds(num5, num13, num11, Theme.S(40));
				coreRightRail.AutoScrollMinSize = new Size(0, coreEvidenceToggle.Bottom + Theme.S(18));
				coreRightRail.AutoScroll = true;
				GameProfile gameProfile = CorePageProfile();
				coreIdentity.SetIdentity((gameProfile == null) ? Lang.T("schedule.manual.pickgame") : gameProfile.Name, coreCpuLine, (gameProfile == null) ? null : GameIcon(gameProfile.ExecutablePath));
				coreLayoutScale = Dpi.Scale;
				RestoreCoreViewState(coreViewState);
				coreWorkspaceLayoutKey = CoreWorkspaceLayoutKey();
			}
			finally
			{
				coreWorkspaceArranging = false;
			}
		}
		private void PositionDataOperationProgress()
		{
			if (dataOperationPanel != null && !dataOperationPanel.IsDisposed)
			{
				dataOperationPanel.Location = new Point(Math.Max(0, (base.ClientSize.Width - dataOperationPanel.Width) / 2), Math.Max(0, (base.ClientSize.Height - dataOperationPanel.Height) / 2));
			}
		}
		private void LayoutLibraryViewport()
		{
			if (libraryArranging || pageLibrary == null || lstGames == null || libraryViewport == pageLibrary.ClientSize)
			{
				return;
			}
			libraryArranging = true;
			pageLibrary.SuspendLayout();
			try
			{
				int num = Math.Max(280, Dpi.U(pageLibrary.ClientSize.Width));
				int num2 = Dpi.U(pageLibrary.ClientSize.Height);
				int num3 = ((num < 500) ? 16 : 24);
				int num4 = num - num3 * 2;
				bool flag = num >= 1040;
				bool flag2 = num >= 570;
				int num5 = (flag ? 238 : (flag2 ? (num4 - 132) : num4));
				SetOverviewBounds(librarySearchPanel, num3, 16, num5, 36);
				librarySearch.SetBounds(Theme.S(42), Theme.S(9), Math.Max(1, librarySearchPanel.Width - Theme.S(54)), Theme.S(24));
				SetOverviewBounds(libraryAdd, num - num3 - 120, flag2 ? 16 : 62, 120, 36);
				int num6 = (flag ? 16 : 62);
				SetOverviewBounds(libraryPlatforms, flag ? (num3 + 250) : num3, num6, flag2 ? 214 : (num4 - 132), 36);
				int num7 = (flag ? 16 : (flag2 ? 62 : 108));
				int num8 = (flag ? (num - num3 - 120 - 12 - 196) : (flag2 ? (num - num3 - 196) : num3));
				SetOverviewBounds(swAutoAdd, num8, num7, 152, 36);
				SetOverviewBounds(libraryAutoSettings, num8 + 160, num7, 36, 36);
				int num9 = (flag ? (num3 + 480) : (flag2 ? (num3 + 226) : (num - num3 - 100)));
				SetOverviewBounds(lblLibraryCount, num9, flag ? 20 : (flag2 ? 66 : 112), flag ? Math.Max(0, num8 - num9 - 12) : (flag2 ? Math.Max(0, num8 - num9 - 12) : 100), 28);
				int num10 = (flag ? 68 : (flag2 ? 114 : 160));
				SetOverviewBounds(lstGames, num3, num10, num4, Math.Max(80, num2 - num10 - 8));
				lstGames.CardGap = 12;
				AdjustLibraryCardDensity();
				pageLibrary.AutoScroll = false;
				pageLibrary.AutoScrollMinSize = Size.Empty;
				libraryViewport = pageLibrary.ClientSize;
			}
			finally
			{
				pageLibrary.ResumeLayout(true);
				libraryArranging = false;
			}
		}
		private static void SetOverviewBounds(Control control, int x, int y, int width, int height)
		{
			Rectangle rectangle = new Rectangle(Theme.S(x), Theme.S(y), Theme.S(Math.Max(1, width)), Theme.S(Math.Max(1, height)));
			if (control.Bounds != rectangle)
			{
				control.Bounds = rectangle;
			}
		}
		private void LayoutOverviewPage()
		{
			if (overviewArranging || pageOverview == null || pageOverview.IsDisposed || paviseCore == null || swGame == null || lblOverviewGuard == null || overviewViewport == pageOverview.ClientSize)
			{
				return;
			}
			overviewArranging = true;
			pageOverview.SuspendLayout();
			try
			{
				Point point = new Point(0, Math.Max(0, -pageOverview.AutoScrollPosition.Y));
				pageOverview.AutoScrollPosition = Point.Empty;
				pageOverview.AutoScrollMinSize = Size.Empty;
				pageOverview.AutoScroll = false;
				int num = Math.Max(1, Dpi.U(pageOverview.ClientSize.Width));
				int num2 = Math.Max(1, Dpi.U(pageOverview.ClientSize.Height));
				int num3 = 42;
				int statusY = Math.Max(8, Math.Min(num2 - num3 - 8 - (overviewActivityShown ? 30 : 0), (int)Math.Round((float)num2 * 0.87f) - num3 / 2));
				int warningReserve = overviewSecurityWarningShown ? 44 : 0;
				int val = Math.Max(1, Math.Min(450, statusY - 26 - warningReserve));
				int num4 = Math.Max(1, Math.Min(580, num - 48));
				val = Math.Min(val, (int)Math.Round((float)num4 * 450f / 580f));
				num4 = Math.Min(num4, (int)Math.Round((float)val * 580f / 450f));
				int coreY = Math.Max(0, (statusY - warningReserve - val) / 2);
				int num5 = ArrangeOverviewHud(num, num2, ref num4, ref val, ref coreY, ref statusY);
				SetOverviewBounds(paviseCore, (num - num4) / 2, coreY, num4, val);
				int num6 = ((num5 > num2) ? Math.Max(1, num - Dpi.U(SystemInformation.VerticalScrollBarWidth)) : num);
				int num7 = (int)Math.Ceiling((float)TextRenderer.MeasureText(lblOverviewGuard.Text, lblOverviewGuard.Font, Size.Empty, TextFormatFlags.NoPadding).Width / Math.Max(0.1f, Dpi.Scale));
				int val2 = (int)Math.Ceiling((float)TextRenderer.MeasureText(lblStatus.Text, Theme.UI(14.5f, true), Size.Empty, TextFormatFlags.NoPadding).Width / Math.Max(0.1f, Dpi.Scale)) + 3;
				int num8 = 86 + num7 + 12 + 72;
				val2 = Math.Min(val2, Math.Max(24, num6 - 32 - num8));
				int num9 = num8 + val2;
				int num10 = Math.Max(8, (num6 - num9) / 2);
				SetOverviewBounds(statusDot, num10, statusY + 9, 24, 24);
				num10 += 33;
				SetOverviewBounds(lblStatus, num10, statusY, val2, num3);
				FitLabelFont(lblStatus, true, 14.5f, 9.5f);
				num10 += val2 + 26;
				SetOverviewBounds(overviewStatusDivider, num10, statusY + 11, 1, 20);
				num10 += 27;
				SetOverviewBounds(lblOverviewGuard, num10, statusY, num7, num3);
				num10 += num7 + 12;
				SetOverviewBounds(swGame, num10, statusY + 5, 72, 32);
				LayoutOverviewSecurityWarning(num6, statusY);
				if (overviewActivity != null)
				{
					SetOverviewBounds(overviewActivity, 16, statusY + num3 + 2, Math.Max(1, num6 - 32), 26);
					if (overviewActivityShown) num5 = Math.Max(num5, statusY + num3 + 36);
				}
				pageOverview.AutoScrollMinSize = ((num5 > num2) ? new Size(0, Theme.S(num5)) : Size.Empty);
				pageOverview.AutoScroll = num5 > num2;
				if (pageOverview.AutoScroll)
				{
					pageOverview.AutoScrollPosition = new Point(0, Math.Min(point.Y, DockScrollLimit(pageOverview)));
				}
				overviewViewport = pageOverview.ClientSize;
			}
			finally
			{
				try
				{
					pageOverview.ResumeLayout(true);
				}
				finally
				{
					overviewArranging = false;
				}
			}
		}
		private void RestackCards(Control panel)
		{
			Dictionary<Control, int> value;
			if (!stackBase.TryGetValue(panel, out value))
			{
				return;
			}
			List<int[]> list = new List<int[]>();
			Theme.S(8);
			foreach (Control control3 in panel.Controls)
			{
				SettingCard settingCard = control3 as SettingCard;
				int value2;
				if (settingCard != null && value.TryGetValue(settingCard, out value2) && settingCard.Collapsible)
				{
					int num = settingCard.Height - settingCard.ExpandedHeight;
					if (num != 0)
					{
						list.Add(new int[2] { value2, num });
					}
				}
			}
			ScrollableControl scrollableControl = panel as ScrollableControl;
			int num2 = ((scrollableControl != null) ? scrollableControl.AutoScrollPosition.Y : 0);
			panel.SuspendLayout();
			foreach (Control control4 in panel.Controls)
			{
				int value3;
				if (!value.TryGetValue(control4, out value3))
				{
					continue;
				}
				int num3 = 0;
				foreach (int[] item in list)
				{
					if (item[0] < value3)
					{
						num3 += item[1];
					}
				}
				int num4 = value3 + num3 + num2;
				if (control4.Top != num4)
				{
					control4.Top = num4;
				}
			}
			panel.ResumeLayout();
		}
		private void LayoutCompactSettingsAppearance(DBPanel panel)
		{
			Point autoScrollPosition = new Point(0, -panel.AutoScrollPosition.Y);
			panel.AutoScrollPosition = Point.Empty;
			int num = SettingsContentWidth(panel);
			int num2 = 0;
			SettingsAppearanceEditorPanel settingsAppearanceEditorPanel = panel.Controls["SettingsAppearanceEditor"] as SettingsAppearanceEditorPanel;
			if (settingsAppearanceEditorPanel != null)
			{
				int num3 = Theme.S(16);
				int num4 = Theme.S(24);
				int val = Math.Max(1, num - num3 * 2);
				bool flag = num >= Theme.S(940);
				int num5 = (flag ? Math.Min(Theme.S(490), (num - num4) * 44 / 100) : num);
				int num6 = (flag ? (num5 + num4) : 0);
				int num7 = (flag ? (num - num6) : num);
				int num8 = Theme.S(22);
				int num9 = Theme.S(12);
				int num10 = Theme.S(48);
				settingsAppearanceEditorPanel.BackgroundIconBounds = new Rectangle(num3, num9, num8, num8);
				Control control = settingsAppearanceEditorPanel.Controls["SettingsBackdropTitle"];
				if (control != null)
				{
					control.SetBounds(num3 + Theme.S(34), num9, Math.Max(1, num5 - num3 * 2 - Theme.S(34)), Theme.S(24));
				}
				backdropPreview.SetBounds(num3, num10, Theme.S(120), Theme.S(80));
				int num11 = num3 + Theme.S(136);
				int num12 = Math.Max(1, num5 - num11 - num3);
				int num13 = Math.Max(Theme.S(24), Math.Max(CompactAppearanceTextHeight(Lang.T("backdrop.status.off"), lblBackdropState.Font, num12), CompactAppearanceTextHeight(Lang.F("backdrop.status.on", Lang.T("backdrop.dim.3")), lblBackdropState.Font, num12)));
				lblBackdropState.SetBounds(num11, num10, num12, num13);
				int num14 = Math.Max(CompactAppearanceButtonWidth(btnBackdrop), TextRenderer.MeasureText(Lang.T("btn.backdrop.change"), btnBackdrop.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(28));
				int num15 = CompactAppearanceButtonWidth(btnBackdropReset);
				int num16 = Theme.S(8);
				bool flag2 = num12 >= num14 + num15 + num16;
				int num17 = (flag2 ? num11 : num3);
				int num18 = (flag2 ? (num10 + num13 + Theme.S(8)) : (Math.Max(backdropPreview.Bottom, lblBackdropState.Bottom) + Theme.S(12)));
				int num19 = num5 - num17 - num3;
				bool flag3 = num19 < num14 + num15 + num16;
				btnBackdropReset.SetBounds(num17, num18, Math.Min(num15, num19), Theme.S(32));
				btnBackdrop.SetBounds(flag3 ? num17 : (btnBackdropReset.Right + num16), flag3 ? (btnBackdropReset.Bottom + num16) : num18, Math.Min(num14, num19), Theme.S(32));
				Control control2 = settingsAppearanceEditorPanel.Controls["SettingsBackdropDimTitle"];
				int num20 = Math.Max(Math.Max(backdropPreview.Bottom, lblBackdropState.Bottom), btnBackdrop.Bottom) + Theme.S(16);
				int num21 = ((control2 == null) ? Theme.S(100) : TextRenderer.MeasureText(control2.Text, control2.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
				int num22 = CompactAppearancePickerWidth(pickBackdropDim);
				bool flag4 = num5 - num3 * 2 >= num21 + Theme.S(16) + num22;
				if (control2 != null)
				{
					control2.SetBounds(num3, num20, Math.Min(val, num21 + Theme.S(4)), Theme.S(32));
				}
				int num23 = (flag4 ? (num3 + num21 + Theme.S(16)) : num3);
				pickBackdropDim.SetBounds(num23, flag4 ? num20 : (num20 + Theme.S(34)), Math.Min(num22, Math.Max(1, num5 - num23 - num3)), Theme.S(32));
				int bottom = pickBackdropDim.Bottom;
				int num24 = (flag ? num9 : (bottom + Theme.S(28)));
				settingsAppearanceEditorPanel.AccentIconBounds = new Rectangle(num6 + num3, num24, num8, num8);
				Control control3 = settingsAppearanceEditorPanel.Controls["SettingsAccentTitle"];
				Control control4 = settingsAppearanceEditorPanel.Controls["SettingsAccentHint"];
				if (control3 != null)
				{
					control3.SetBounds(num6 + num3 + Theme.S(34), num24, Math.Max(1, num7 - num3 * 2 - Theme.S(34)), Theme.S(24));
				}
				if (control4 != null)
				{
					control4.Visible = false;
				}
				int num25 = num24 + Theme.S(36);
				int num26 = Theme.S(30);
				int num27 = Theme.S(24);
				int num28 = Theme.S(82);
				int num29 = num6 + num3 + num28 + Theme.S(8);
				int num30 = Math.Max(1, num7 - num3 * 2 - num28 - Theme.S(8));
				int num31 = Math.Max(1, (num30 + num26 - num27) / num26);
				PerformancePreset[] array = PresetValue.VisibleOrder();
				foreach (PerformancePreset performancePreset in array)
				{
					Label label = modeAccentLabels[(int)performancePreset];
					if (label != null)
					{
						label.SetBounds(num6 + num3, num25, num28, num27);
					}
					int num32 = 0;
					foreach (ColorSwatch item in modeSwatches[(int)performancePreset])
					{
						item.SetBounds(num29 + num32 % num31 * num26, num25 + num32 / num31 * num26, num27, num27);
						num32++;
					}
					num25 += Math.Max(Theme.S(32), (num32 + num31 - 1) / num31 * num26);
				}
				int num33 = Math.Max(bottom, num25) + Theme.S(16);
				Control control5 = settingsAppearanceEditorPanel.Controls["SettingsAppearanceSeparator"];
				if (control5 != null)
				{
					if (flag)
					{
						control5.SetBounds(num5 + num4 / 2, Theme.S(12), Math.Max(1, Theme.S(1)), Math.Max(1, num33 - Theme.S(24)));
					}
					else
					{
						control5.SetBounds(num3, bottom + Theme.S(14), val, Math.Max(1, Theme.S(1)));
					}
				}
				settingsAppearanceEditorPanel.SetBounds(0, 0, num, num33);
				settingsAppearanceEditorPanel.Invalidate();
				num2 = settingsAppearanceEditorPanel.Bottom;
			}
			if (motionAppearance != null && !motionAppearance.IsDisposed && motionAppearance.Parent == panel)
			{
				motionAppearance.SetBounds(0, num2 + Theme.S(12), num, motionAppearance.MeasureHeight(num));
				motionAppearance.PerformLayout();
				num2 = Math.Max(motionAppearance.Bottom, motionAppearance.Top + motionAppearance.Controls["MotionSaveStatus"].Bottom + Theme.S(12));
				if (motionAppearance.Bottom < num2) motionAppearance.Height = num2 - motionAppearance.Top;
			}
			foreach (Control control6 in panel.Controls)
			{
				if (control6 is SettingsWorkspaceTile)
				{
					SettingsWorkspaceTile settingsWorkspaceTile = (SettingsWorkspaceTile)control6;
					settingsWorkspaceTile.CompactRow = true;
					settingsWorkspaceTile.BottomSeparator = false;
					settingsWorkspaceTile.SetBounds(0, num2 + Theme.S(4), num, settingsWorkspaceTile.MeasureHeight(num));
					settingsWorkspaceTile.ArrangeContent();
					num2 = settingsWorkspaceTile.Bottom;
				}
			}
			panel.AutoScrollMinSize = new Size(0, num2 + Theme.S(4));
			panel.AutoScrollPosition = autoScrollPosition;
		}
		private static int CompactAppearanceTextHeight(string text, Font font, int width)
		{
			return TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
		}
		private static int CompactAppearanceButtonWidth(PillButton button)
		{
			return Math.Max(Theme.S(72), TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(28));
		}
		private static int CompactAppearancePickerWidth(TierPicker picker)
		{
			int num = Theme.S(48);
			string[] labels = picker.Labels;
			foreach (string text in labels)
			{
				num = Math.Max(num, TextRenderer.MeasureText(text, DsType.Font(DsText.Label, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(20));
			}
			return num * picker.Labels.Length + Theme.S(4) * (picker.Labels.Length - 1);
		}
		private void LayoutSettingsWorkspace()
		{
			if (arrangingSettingsWorkspace || settingsWorkspaceViewport == null || settingsWorkspaceSurface == null || settingsTabPanels == null || settingsWorkspaceSurface.IsDisposed || pageSettings == null || pageSettings.IsDisposed)
			{
				return;
			}
			arrangingSettingsWorkspace = true;
			try
			{
				pageSettings.AutoScroll = false;
				int num = Theme.S(24);
				int num2 = Theme.S(12);
				int num3 = Theme.S(12);
				settingsWorkspaceViewport.SetBounds(0, 0, pageSettings.ClientSize.Width, pageSettings.ClientSize.Height);
				int num4 = Math.Max(Theme.S(300), settingsWorkspaceViewport.ClientSize.Width - num * 2);
				int tabGap = Theme.S(6);
				int tabHeight = Theme.S(44);
				int tabX = 0;
				int tabY = Theme.S(4);
				foreach (SettingsWorkspaceTab settingsWorkspaceTab in settingsWorkspaceTabs)
				{
					int tabWidth = Math.Min(num4, Math.Max(Theme.S(124), TextRenderer.MeasureText(settingsWorkspaceTab.Text, Theme.UI(10f, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(44)));
					if (tabX > 0 && tabX + tabWidth > num4)
					{
						tabX = 0;
						tabY += tabHeight + tabGap;
					}
					settingsWorkspaceTab.SetBounds(tabX, tabY, tabWidth, tabHeight);
					tabX += tabWidth + tabGap;
				}
				int num8 = Math.Max(1, settingsWorkspaceViewport.ClientSize.Height - num2 - num3);
				int num9 = tabY + tabHeight + Theme.S(12);
				int num10 = num9 + Theme.S(12);
				int num11 = Math.Min(Theme.S(540), num8);
				int num12 = Math.Max(1, num11 - num10 - Theme.S(12));
				settingsWorkspaceSurface.FrameTop = num9;
				settingsWorkspaceSurface.BackColor = Theme.Bg;
				settingsWorkspaceSurface.SetBounds(num, num2, num4, num11);
				int num13 = Theme.S(16);
				settingsWorkspaceSurface.HeaderLine = 0;
				for (int j = 0; j < settingsTabPanels.Length; j++)
				{
					DBPanel dBPanel = settingsTabPanels[j];
					dBPanel.SetBounds(num13, num10, num4 - num13 * 2, num12);
					dBPanel.AutoScroll = true;
					dBPanel.BackColor = Theme.Card;
				}
				LayoutSettingsGeneral(settingsTabPanels[0]);
				LayoutSettingsMaintenance(settingsTabPanels[1]);
				LayoutSettingsAppearance(settingsTabPanels[2]);

				DBPanel dBPanel2 = settingsTabPanels[settingsTabs.Index];
				num11 = Math.Min(num8, num10 + dBPanel2.AutoScrollMinSize.Height + Theme.S(12));
				settingsWorkspaceSurface.Height = num11;
				dBPanel2.Height = Math.Max(1, num11 - num10 - Theme.S(12));
				settingsWorkspaceSurface.DivisionX = 0;
				settingsWorkspaceSurface.DivisionTop = 0;
				SettingsWorkspaceTile settingsWorkspaceTile = SettingsTile(settingsTabPanels[0], "SettingsStandbyCleanerCard");
				settingsWorkspaceSurface.DivisionBottom = 0;
				SettingsWorkspaceCategoryPanel settingsWorkspaceCategoryPanel = settingsTabPanels[0] as SettingsWorkspaceCategoryPanel;
				if (settingsWorkspaceCategoryPanel != null)
				{
					int num18 = SettingsContentWidth(settingsWorkspaceCategoryPanel);
					settingsWorkspaceCategoryPanel.DivisionX = ((num18 >= Theme.S(806)) ? (num18 / 2) : 0);
					settingsWorkspaceCategoryPanel.DivisionBottom = ((settingsWorkspaceTile != null) ? (settingsWorkspaceTile.Top - Theme.S(8)) : 0);
					settingsWorkspaceCategoryPanel.Invalidate();
				}
				settingsWorkspaceSurface.Invalidate();
			}
			finally
			{
				arrangingSettingsWorkspace = false;
			}
		}
		private void LayoutSettingsGeneral(DBPanel panel)
		{
			SettingsWorkspaceTile settingsWorkspaceTile = SettingsTile(panel, "SettingsStartup");
			SettingsWorkspaceTile settingsWorkspaceTile2 = SettingsTile(panel, "SettingsAutoHide");
			SettingsWorkspaceTile settingsWorkspaceTile3 = SettingsTile(panel, "SettingsExitGrace");
			SettingsWorkspaceTile settingsWorkspaceTile4 = SettingsTile(panel, "SettingsLanguage");
			SettingsWorkspaceTile settingsWorkspaceTile5 = SettingsTile(panel, "SettingsStandbyCleanerCard");
			SettingsWorkspaceTile aiTuning = SettingsTile(panel, "SettingsAiTuning");
			if (settingsWorkspaceTile == null || settingsWorkspaceTile2 == null || settingsWorkspaceTile3 == null || settingsWorkspaceTile4 == null || settingsWorkspaceTile5 == null || aiTuning == null)
			{
				return;
			}
			Point autoScrollPosition = new Point(0, -panel.AutoScrollPosition.Y);
			panel.AutoScrollPosition = Point.Empty;
			int num = SettingsContentWidth(panel);
			int num2 = Theme.S(24);
			int num3 = 0;
			if (num >= Theme.S(806))
			{
				int num4 = (num - num2) / 2;
				int num5 = Math.Max(settingsWorkspaceTile.MeasureHeight(num4), settingsWorkspaceTile3.MeasureHeight(num4));
				int num6 = Math.Max(settingsWorkspaceTile2.MeasureHeight(num4), settingsWorkspaceTile4.MeasureHeight(num4));
				int num7 = settingsWorkspaceTile5.MeasureHeight(num);
				settingsWorkspaceTile.SetBounds(0, 0, num4, num5);
				settingsWorkspaceTile3.SetBounds(num4 + num2, 0, num - num4 - num2, num5);
				settingsWorkspaceTile2.SetBounds(0, num5, num4, num6);
				settingsWorkspaceTile4.SetBounds(num4 + num2, num5, num - num4 - num2, num6);
				settingsWorkspaceTile5.SetBounds(0, num5 + num6, num, num7);
				num3 = settingsWorkspaceTile5.Bottom;
			}
			else
			{
				SettingsWorkspaceTile[] array = new SettingsWorkspaceTile[5] { settingsWorkspaceTile, settingsWorkspaceTile2, settingsWorkspaceTile3, settingsWorkspaceTile4, settingsWorkspaceTile5 };
				foreach (SettingsWorkspaceTile settingsWorkspaceTile7 in array)
				{
					settingsWorkspaceTile7.SetBounds(0, num3, num, settingsWorkspaceTile7.MeasureHeight(num));
					num3 = settingsWorkspaceTile7.Bottom;
				}
			}
			aiTuning.SetBounds(0, num3, num, aiTuning.MeasureHeight(num));
			num3 = aiTuning.Bottom;
			settingsWorkspaceTile5.BottomSeparator = true;
			aiTuning.BottomSeparator = false;
			SettingsWorkspaceTile[] array2 = new SettingsWorkspaceTile[6] { settingsWorkspaceTile, settingsWorkspaceTile2, settingsWorkspaceTile3, settingsWorkspaceTile4, settingsWorkspaceTile5, aiTuning };
			foreach (SettingsWorkspaceTile settingsWorkspaceTile8 in array2)
			{
				settingsWorkspaceTile8.ArrangeContent();
			}
			panel.AutoScrollMinSize = new Size(0, num3);
			panel.AutoScrollPosition = autoScrollPosition;
		}
		private void LayoutSettingsMaintenance(DBPanel panel)
		{
			SettingsWorkspaceTile[] tiles = {
				SettingsTile(panel, "SettingsLogs"), SettingsTile(panel, "SettingsShader"),
				SettingsTile(panel, "SettingsReset"), SettingsTile(panel, "SettingsUninstall")
			};
			foreach (SettingsWorkspaceTile tile in tiles) if (tile == null) return;
			int width = SettingsContentWidth(panel), gap = Theme.S(24);
			bool twoColumns = width >= Theme.S(806);
			int column = twoColumns ? (width - gap) / 2 : width;
			Point scroll = new Point(0, -panel.AutoScrollPosition.Y);
			panel.AutoScrollPosition = Point.Empty;
			int top = 0;
			for (int i = 0; i < tiles.Length; i += twoColumns ? 2 : 1)
			{
				int height = tiles[i].MeasureHeight(column);
				if (twoColumns) height = Math.Max(height, tiles[i + 1].MeasureHeight(width - column - gap));
				tiles[i].SetBounds(0, top, column, height);
				if (twoColumns) tiles[i + 1].SetBounds(column + gap, top, width - column - gap, height);
				top += height;
			}
			for (int i = 0; i < tiles.Length; i++)
			{
				tiles[i].TabIndex = i;
				tiles[i].BottomSeparator = twoColumns ? i < 2 : i < tiles.Length - 1;
				tiles[i].ArrangeContent();
			}
			SettingsWorkspaceCategoryPanel category = panel as SettingsWorkspaceCategoryPanel;
			if (category != null)
			{
				category.DivisionX = twoColumns ? width / 2 : 0;
				category.DivisionBottom = twoColumns ? tiles[0].Bottom - Theme.S(8) : 0;
				category.Invalidate();
			}
			panel.AutoScrollMinSize = new Size(0, top);
			panel.AutoScrollPosition = scroll;
		}
		private void LayoutSettingsAppearance(DBPanel panel)
		{
			LayoutCompactSettingsAppearance(panel);
		}
		private void WhiteBounds(Control control, int x, int y, int width, int height)
		{
			control.SetBounds(Theme.S(x), Theme.S(y) + pageWhitelist.AutoScrollPosition.Y, Theme.S(Math.Max(1, width)), Theme.S(Math.Max(1, height)));
		}
		private void LayoutWhitelistViewport()
		{
			if (whiteArranging || pageWhitelist == null || whitePanel == null || whitePanel.IsDisposed)
			{
				return;
			}
			whiteArranging = true;
			pageWhitelist.SuspendLayout();
			Point point = new Point(Math.Max(0, -pageWhitelist.AutoScrollPosition.X), Math.Max(0, -pageWhitelist.AutoScrollPosition.Y));
			try
			{
				for (int i = 0; i < 2; i++)
				{
					int num = pageWhitelist.ClientSize.Width;
					int num2 = Math.Max(280, Dpi.U(num));
					int num3 = Math.Max(1, Dpi.U(pageWhitelist.ClientSize.Height));
					int num4 = ((num2 < 600) ? 16 : 24);
					int num5 = num2 - num4 * 2;
					int num6 = 10;
					bool flag = num2 >= 920;
					bool flag2 = num2 >= 520;
					int num7 = 16;
					int num8;
					if (flag)
					{
						WhiteBounds(whiteSearchPanel, num4, num7, 280, 38);
						WhiteBounds(whiteFilters, num4 + 292, num7, 300, 38);
						WhiteBounds(whiteCount, num4 + 608, num7, Math.Max(1, num5 - 864), 38);
						WhiteBounds(whitePick, num2 - num4 - 248, num7, 126, 38);
						WhiteBounds(whiteBrowse, num2 - num4 - 112, num7, 112, 38);
						num8 = num7 + 38 + 12;
					}
					else if (flag2)
					{
						WhiteBounds(whiteSearchPanel, num4, num7, num5 - 248, 38);
						WhiteBounds(whitePick, num2 - num4 - 238, num7, 126, 38);
						WhiteBounds(whiteBrowse, num2 - num4 - 102, num7, 102, 38);
						WhiteBounds(whiteFilters, num4, num7 + 48, Math.Min(330, num5 - 100), 34);
						WhiteBounds(whiteCount, num4 + Math.Min(330, num5 - 100) + 12, num7 + 48, Math.Max(1, num5 - 342), 34);
						num8 = num7 + 94;
					}
					else
					{
						WhiteBounds(whiteSearchPanel, num4, num7, num5, 38);
						WhiteBounds(whiteFilters, num4, num7 + 48, num5, 34);
						int num9 = (num5 - num6) / 2;
						WhiteBounds(whitePick, num4, num7 + 92, num9, 34);
						WhiteBounds(whiteBrowse, num4 + num9 + num6, num7 + 92, num5 - num9 - num6, 34);
						num8 = num7 + 138;
					}
					whiteCount.Visible = (flag || flag2) && whiteCount.Width >= Theme.S(70);
					whiteSearch.SetBounds(Theme.S(40), Theme.S(10), Math.Max(1, whiteSearchPanel.Width - Theme.S(52)), Theme.S(24));
					bool flag3 = num5 < 620;
					int num10 = (flag3 ? 82 : 36);
					int num11 = Math.Max(num8 + 152, num3 - num4 - num10);
					WhiteBounds(whitePanel, num4, num8, num5, num11 - num8 - 12);
					if (flag3)
					{
						int num12 = (num5 - num6) / 2;
						WhiteBounds(btnRestorePlatformProtection, num4, num11, num12, 34);
						WhiteBounds(whiteReset, num4 + num12 + num6, num11, num5 - num12 - num6, 34);
						WhiteBounds(btnRemoveWhitelist, num2 - num4 - 88, num11 + 44, 88, 34);
						WhiteBounds(lblWhiteHint, num4 + 2, num11 + 44, Math.Max(1, num5 - 104), 34);
					}
					else
					{
						WhiteBounds(btnRemoveWhitelist, num2 - num4 - 88, num11, 88, 36);
						WhiteBounds(whiteReset, num2 - num4 - 206, num11, 108, 36);
						WhiteBounds(btnRestorePlatformProtection, num2 - num4 - 356, num11, 140, 36);
						WhiteBounds(lblWhiteHint, num4 + 2, num11, Math.Max(1, num5 - 374), 36);
					}
					int v = num11 + num10 + num4;
					bool flag4 = Theme.S(v) > pageWhitelist.ClientSize.Height;
					pageWhitelist.AutoScrollMargin = Size.Empty;
					pageWhitelist.AutoScrollMinSize = (flag4 ? new Size(0, Theme.S(v)) : Size.Empty);
					pageWhitelist.AutoScroll = flag4;
					if (num == pageWhitelist.ClientSize.Width)
					{
						break;
					}
				}
				pageWhitelist.AutoScrollPosition = new Point(point.X, Math.Min(point.Y, DockScrollLimit(pageWhitelist)));
			}
			finally
			{
				pageWhitelist.ResumeLayout(true);
				whiteArranging = false;
			}
		}
		private void LayoutWorkspaceViewport()
		{
			if (root == null || root.IsDisposed)
			{
				return;
			}
			int num = Theme.S(WinW);
			int num2 = Theme.S(WinH);
			root.Size = new Size(num, num2);
			CenterRoot();
			int num3 = Math.Min(num2, Theme.S(TopH));
			if (nav != null)
			{
				Size size = nav.MeasureFloatingDock(Math.Max(1, num - Theme.S(32)));
				nav.SetBounds((num - size.Width) / 2, Math.Max(num3, num2 - size.Height - Theme.S(16)), size.Width, size.Height);
			}
			if (tuningNav != null)
			{
				tuningNav.Visible = false;
			}
			if (lblSub != null)
			{
				lblSub.Visible = false;
			}
			Control control = root.Controls["WorkspaceTopBar"];
			if (control != null)
			{
				control.SetBounds(0, 0, num, num3);
				LayoutWorkspaceHeader(control);
			}
			int num4 = ((nav == null) ? num2 : (nav.Top - Theme.S(8)));
			if (pages != null)
			{
				DBPanel[] array = pages;
				foreach (DBPanel dBPanel in array)
				{
					dBPanel.SetBounds(0, num3, num, Math.Max(1, num4 - num3));
					if (dBPanel != pageOverview && dBPanel != pageLibrary && dBPanel != pageWhitelist && dBPanel != pagePolicy && dBPanel != pageAntiCheat && dBPanel != pageAbout && dBPanel != pageCoreScheduling && dBPanel != pageSettings && dBPanel != pageAudit)
					{
						dBPanel.AutoScroll = true;
					}
					DBPanel[] value;
					if (!pageTabPanels.TryGetValue(dBPanel, out value))
					{
						continue;
					}
					DBPanel[] array2 = value;
					foreach (DBPanel dBPanel2 in array2)
					{
						if (!(dBPanel2 is PerformanceFeatureGrid))
						{
							dBPanel2.SetBounds(dBPanel2.Left, dBPanel2.Top, Math.Max(1, dBPanel.ClientSize.Width - dBPanel2.Left - Theme.S(20)), Math.Max(1, dBPanel.ClientSize.Height - dBPanel2.Top - Theme.S(8)));
						}
					}
				}
			}
			int num5 = Math.Min(Theme.S(230), Math.Max(1, num - Theme.S(160)));
			if (coreBackBar != null)
			{
				coreBackBar.SetBounds(Theme.S(10), 0, num5, Math.Min(num3, Theme.S(60)));
			}
			if (overviewBackBar != null)
			{
				overviewBackBar.SetBounds(Theme.S(10), 0, num5, Math.Min(num3, Theme.S(60)));
			}
			LayoutLibraryViewport();
			LayoutWhitelistViewport();
			LayoutPerformanceViewport();
			LayoutAntiCheatViewport();
			LayoutAboutViewport();
			LayoutAuditViewport();
			LayoutCoreViewport();
			LayoutSettingsWorkspace();
			if (nav != null)
			{
				nav.BringToFront();
			}
			if (curPage != null)
			{
				pageBaseLeft = curPage.Left;
			}
		}
		private void LayoutWorkspaceHeader(Control bar)
		{
			int num = Theme.S(4);
			int num2 = Theme.S(40);
			int num3 = Math.Min(bar.Height, Theme.S(60));
			Control control = bar.Controls["WindowClose"];
			Control control3 = bar.Controls["WindowMinimize"];
			if (control != null)
			{
				control.SetBounds(bar.Width - num - num2, 0, num2, num3);
			}
			if (control3 != null)
			{
				control3.SetBounds(bar.Width - num - num2 * 2, 0, num2, num3);
			}
			bool flag = TopH > 60;
			int num4 = (flag ? (bar.Width - Theme.S(24)) : (bar.Width - num - num2 * 2 - Theme.S(14)));
			int rowY = Theme.S(flag ? 64 : 8);
			int num5 = Theme.S(94);
			int num6 = Theme.S(12);
			int num7 = Math.Min(Theme.S(188), Math.Max(Theme.S(100), num4 - num5 - num6 - Theme.S(24)));
			if (modeButton != null)
			{
				modeButton.SetBounds(Math.Max(0, num4 - num7), rowY, num7, Theme.S(44));
			}
			if (themeSwitch != null)
			{
				themeSwitch.SetBounds(Math.Max(0, num4 - num7 - num6 - num5), rowY, num5, Theme.S(44));
			}
			Control control4 = ((coreBackBar != null && coreBackBar.Visible) ? coreBackBar : ((overviewBackBar != null && overviewBackBar.Visible) ? overviewBackBar : null));
			int floor = (flag ? Theme.S(24) : ((control4 != null) ? (control4.Right + Theme.S(20)) : Theme.S(196)));
			int num8 = LayoutHeaderLinks(themeSwitch.Left, rowY, num6, floor);
			Control control5 = bar.Controls["WorkspaceBrand"];
			if (control5 != null)
			{
				int num9 = (flag ? (bar.Width - num - num2 * 2 - Theme.S(12)) : (num8 - Theme.S(20)));
				control5.SetBounds(Theme.S(24), Theme.S(4), Math.Max(1, Math.Min(Theme.S(270), num9 - Theme.S(24))), Theme.S(52));
				control5.Visible = control4 == null;
			}
		}
		private void LayoutModeFlyout()
		{
			modeFlyout.FitViewport(new Size(Math.Max(1, root.ClientSize.Width - Theme.S(24)), Math.Max(1, root.ClientSize.Height - Theme.S(24))));
			Point point = root.PointToClient(modeButton.PointToScreen(Point.Empty));
			modeFlyout.Left = Math.Max(Theme.S(12), Math.Min(point.X + modeButton.Width - modeFlyout.Width, root.ClientSize.Width - modeFlyout.Width - Theme.S(12)));
			modeFlyout.Top = Math.Max(Theme.S(12), Math.Min(point.Y + modeButton.Height + Theme.S(8), root.ClientSize.Height - modeFlyout.Height - Theme.S(12)));
		}
		private int LayoutHeaderLinks(int right, int rowY, int gap, int floor)
		{
			if (btnDonate == null || headerWebsite == null)
			{
				return right;
			}
			RogLinkButton[] array = new RogLinkButton[2] { btnDonate, headerWebsite };
			int[] array2 = new int[2]
			{
				HeaderLinkWidth(btnDonate),
				HeaderLinkWidth(headerWebsite)
			};
			int num = Theme.S(44);
			for (int num2 = array.Length; num2 >= 0; num2--)
			{
				for (int i = 0; i < 2; i++)
				{
					int num3 = right;
					for (int j = 0; j < num2; j++)
					{
						num3 -= gap + ((i == 0) ? array2[j] : num);
					}
					if (num2 > 0 && num3 < floor)
					{
						continue;
					}
					num3 = right;
					for (int k = 0; k < array.Length; k++)
					{
						array[k].Visible = k < num2;
						if (k < num2)
						{
							int num4 = ((i == 0) ? array2[k] : num);
							num3 -= gap + num4;
							array[k].SetBounds(num3, rowY, num4, Theme.S(44));
						}
					}
					return num3;
				}
			}
			return right;
		}
		private static int HeaderLinkWidth(RogLinkButton link)
		{
			Size size = TextRenderer.MeasureText(link.Text, DsType.Font(DsText.Body, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
			return Theme.S(81) + size.Width;
		}
		private int AutoCardHeight(string desc, int cardW, Control host, int minHeight)
		{
			return AutoCardHeight(desc, cardW, host, minHeight, 0);
		}
		private int AutoCardHeight(string desc, int cardW, Control host, int minHeight, int valueReserve)
		{
			if (string.IsNullOrEmpty(desc))
			{
				return minHeight;
			}
			int num = Theme.S(42);
			int num2 = num + ((host != null) ? (host.Width + Theme.S(14)) : 0);
			int num3 = Theme.S(20);
			int num4 = Theme.S(cardW) - num - num2 - valueReserve - num3;
			if (num4 <= 0)
			{
				return minHeight;
			}
			Font font = DsType.Font(DsText.Description);
			int num5 = TextRenderer.MeasureText("Ag", font).Height;
			if (num5 <= 0)
			{
				return minHeight;
			}
			int num6 = TextRenderer.MeasureText(desc, font, new Size(num4, int.MaxValue), TextFormatFlags.WordBreak).Height;
			int num7 = (num6 + num5 - 1) / num5;
			if (num7 < 1)
			{
				num7 = 1;
			}
			if (num7 > 3)
			{
				num7 = 3;
			}
			int num8 = Theme.S(100);
			if (num8 <= 0)
			{
				return minHeight;
			}
			int num9 = num7 * num5 + Theme.S(17) + Theme.S(44);
			int num10 = (num9 * 100 + num8 - 1) / num8;
			if (num10 <= minHeight)
			{
				return minHeight;
			}
			return num10;
		}
		private int FullTextCardHeight(string desc, int width, Control host, int minimum)
		{
			int num = Theme.S(width - 84 - 20) - ((host != null) ? (host.Width + Theme.S(14)) : 0);
			if (num <= 0)
			{
				return minimum;
			}
			Font font = DsType.Font(DsText.Description);
			int num2 = Math.Max(1, TextRenderer.MeasureText("Ag", font).Height);
			int num3 = TextRenderer.MeasureText(desc ?? "", font, new Size(num, int.MaxValue), TextFormatFlags.WordBreak).Height;
			num3 = (num3 + num2 - 1) / num2 * num2;
			int num4 = num3 + Theme.S(64);
			int num5 = Math.Max(1, Theme.S(100));
			return Math.Max(minimum, (num4 * 100 + num5 - 1) / num5);
		}
		internal static float FitFontSize(string text, int widthPx, bool bold, float maxSize, float minSize)
		{
			if (string.IsNullOrEmpty(text) || widthPx <= 0)
			{
				return maxSize;
			}
			for (float num = maxSize; num > minSize; num -= 0.5f)
			{
				if (TextRenderer.MeasureText(text, Theme.UI(num, bold), new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width <= widthPx)
				{
					return num;
				}
			}
			return minSize;
		}
		private static void FitLabelFont(Label label, bool bold, float maxSize, float minSize)
		{
			if (label != null && !label.IsDisposed && label.Width > 0)
			{
				float size = FitFontSize(label.Text, label.Width, bold, maxSize, minSize);
				Font font = Theme.UI(size, bold);
				if (!object.ReferenceEquals(label.Font, font))
				{
					label.Font = font;
				}
			}
		}
		protected override void OnResize(EventArgs e)
		{
			AcViewState acViewState = CaptureAcViewState();
			CoreViewState coreViewState = CaptureCoreViewState();
			SetModeFlyout(false);
			if (base.WindowState == FormWindowState.Minimized)
			{
			}
			base.OnResize(e);
			CenterRoot();
			RestoreAcScrollPosition(acViewState);
			RestoreCoreViewState(coreViewState);
			ScheduleFit();
			SyncUiActivity();
		}
		private void CenterRoot()
		{
			if (root == null || root.IsDisposed) return;
			int x = (base.ClientSize.Width - root.Width) / 2;
			int y = (base.ClientSize.Height - root.Height) / 2;
			if (x < 0) x = 0;
			if (y < 0) y = 0;
			if (root.Left != x || root.Top != y) root.Location = new Point(x, y);
		}
		private void ScheduleFit()
		{
			if (fitting || base.IsDisposed || !base.IsHandleCreated) return;
			if (base.WindowState == FormWindowState.Minimized) return;
			if (!Dpi.FitDiffers(base.ClientSize.Width, base.ClientSize.Height)) return;
			if (moveSizeLoop) { fitDeferredByDrag = true; return; }
			if (fitTimer == null)
			{
				fitTimer = new System.Windows.Forms.Timer();
				fitTimer.Interval = 220;
				fitTimer.Tick += OnFitTick;
			}
			fitTimer.Stop();
			fitTimer.Start();
		}
	}
}
