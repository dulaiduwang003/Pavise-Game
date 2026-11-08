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
					int uLimit = Math.Max(240, Dpi.U(pageAbout.ClientSize.Width));
					int uLimit2 = Math.Max(1, Dpi.U(pageAbout.ClientSize.Height));
					int layoutAboutViewportValue = ((uLimit < 500) ? 16 : 24);
					int lowerBound = Math.Max(1, uLimit - layoutAboutViewportValue * 2);
					bool matchesULimit = uLimit < 900;
					bool matchesLowerBound = lowerBound < 430;
					int layoutAboutViewportValue2 = ((!matchesULimit) ? 130 : (matchesLowerBound ? 328 : 210));
					int layoutAboutViewportValue3 = aboutContentTop + layoutAboutViewportValue2 + 12;
					int lowerBound2 = (matchesULimit ? 366 : Math.Max(366, uLimit2 - layoutAboutViewportValue3 - 16));
					int layoutAboutViewportValue4 = 14;
					int ratio = (matchesULimit ? lowerBound : ((lowerBound - layoutAboutViewportValue4) / 2));
					int v = (matchesULimit ? lowerBound : (lowerBound - ratio - layoutAboutViewportValue4));
					int layoutAboutViewportValue5 = (matchesULimit ? (layoutAboutViewportValue3 + lowerBound2 + 12) : layoutAboutViewportValue3);
					int v2 = layoutAboutViewportValue5 + lowerBound2 + 16;
					bool autoScrollValue = Theme.S(v2) > pageAbout.ClientSize.Height;
					pageAbout.AutoScrollMargin = Size.Empty;
					pageAbout.AutoScrollMinSize = (autoScrollValue ? new Size(0, Theme.S(v2)) : Size.Empty);
					pageAbout.AutoScroll = autoScrollValue;
					if (!autoScrollValue)
					{
						pageAbout.AutoScrollPosition = Point.Empty;
					}
					int top = pageAbout.AutoScrollPosition.Y;
					aboutHeroPanel.SetBounds(Theme.S(layoutAboutViewportValue), Theme.S(aboutContentTop) + top, Theme.S(lowerBound), Theme.S(layoutAboutViewportValue2));
					aboutIdentityPanel.SetBounds(Theme.S(layoutAboutViewportValue), Theme.S(layoutAboutViewportValue3) + top, Theme.S(ratio), Theme.S(lowerBound2));
					aboutReleasePanel.SetBounds(Theme.S(matchesULimit ? layoutAboutViewportValue : (layoutAboutViewportValue + ratio + layoutAboutViewportValue4)), Theme.S(layoutAboutViewportValue5) + top, Theme.S(v), Theme.S(lowerBound2));
					int v3 = ((lowerBound < 430) ? 64 : 84);
					int layoutAboutViewportValue6 = ((lowerBound < 430) ? 100 : 132);
					aboutIcon.SetBounds(Theme.S(24), Theme.S(23), Theme.S(v3), Theme.S(v3));
					int lowerBound3 = ((!matchesULimit) ? Math.Max(132, Dpi.U(TextRenderer.MeasureText("WINDOWS x64", Theme.UI(9.2f, true), Size.Empty, TextFormatFlags.NoPadding).Width) + 4) : (matchesLowerBound ? (lowerBound - 40) : ((lowerBound - 56) / 3)));
					int layoutAboutViewportValue7 = ((!matchesULimit) ? (lowerBound - 20 - (lowerBound3 * 3 + 24)) : 20);
					int val = ((!matchesULimit) ? (layoutAboutViewportValue7 - layoutAboutViewportValue6 - 16) : (lowerBound - layoutAboutViewportValue6 - 20));
					int[] integerValues = new int[4] { 12, 31, 71, 96 };
					int[] integerValues2 = new int[4] { 20, 40, 22, 29 };
					for (int j = 0; j < aboutHeroLabels.Length; j++)
					{
						aboutHeroLabels[j].SetBounds(Theme.S(layoutAboutViewportValue6), Theme.S(integerValues[j]), Theme.S(Math.Max(1, val)), Theme.S(integerValues2[j]));
					}
					for (int k = 0; k < aboutMetricControls.Length; k++)
					{
						int v4 = (matchesULimit ? (matchesLowerBound ? 20 : (20 + k * (lowerBound3 + 8))) : (layoutAboutViewportValue7 + k * (lowerBound3 + 12)));
						int layoutAboutViewportValue8 = (matchesULimit ? (matchesLowerBound ? (140 + k * 60) : 140) : 27);
						Control[] controlItems = aboutMetricControls[k];
						controlItems[0].SetBounds(Theme.S(v4), Theme.S(layoutAboutViewportValue8), Theme.S(lowerBound3), Theme.S(18));
						controlItems[1].SetBounds(Theme.S(v4), Theme.S(layoutAboutViewportValue8 + 22), Theme.S(lowerBound3), Theme.S(27));
						controlItems[2].SetBounds(Theme.S(v4), Theme.S(layoutAboutViewportValue8 + 52), Theme.S(Math.Max(1, lowerBound3 - 14)), Theme.S(1));
					}
					LayoutAboutActions(aboutReleasePanel, "aboutReleaseNotes", "aboutWebsiteDownload");
					if (Dpi.U(pageAbout.ClientSize.Width) == uLimit)
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
				int lowerBound = Math.Max(1, card.ClientSize.Width - Theme.S(52));
				int width = lowerBound / 2;
				int top = Math.Max(0, card.ClientSize.Height - Theme.S(50));
				control.SetBounds(Theme.S(20), top, width, Theme.S(36));
				control2.SetBounds(Theme.S(32) + width, top, lowerBound - width, Theme.S(36));
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
				int left = Theme.S((pageAntiCheat.ClientSize.Width < Theme.S(500)) ? 16 : 24);
				int top = Theme.S(16);
				acViewport.SetBounds(left, top, Math.Max(1, pageAntiCheat.ClientSize.Width - left * 2), Math.Max(1, pageAntiCheat.ClientSize.Height - top - Theme.S(8)));
				Point point = new Point(0, Math.Max(0, -acViewport.AutoScrollPosition.Y));
				acViewport.AutoScrollPosition = Point.Empty;
				int width = Math.Max(1, acViewport.Width);
				int left2 = Theme.S(8);
				int scaledDimension = Theme.S(20);
				int height = Theme.S(64);
				bool matchesWidth = width < Theme.S(840);
				int minLimit = ((!matchesWidth) ? Math.Max(Theme.S(290), Math.Min(Theme.S(360), (int)((float)width * 0.29f))) : 0);
				int left3 = (matchesWidth ? left2 : (minLimit + scaledDimension));
				int width2 = Math.Max(1, width - left3 - left2);
				int width3 = (matchesWidth ? (width - left2 * 2) : (minLimit - left2));
				int layoutAcControlBayResult = LayoutAcControlBay(width3, matchesWidth);
				int layoutAntiCheatViewportValue = 0;
				foreach (SettingCard acCard in acCards)
				{
					layoutAntiCheatViewportValue += ((AntiCheatModuleRow)acCard).MeasureHeight(width2);
				}
				layoutAntiCheatViewportValue += Math.Max(0, acCards.Count - 1);
				int top2 = (matchesWidth ? (layoutAcControlBayResult + Theme.S(20)) : left2);
				int val = Math.Max((!matchesWidth) ? (layoutAcControlBayResult + left2) : 0, top2 + layoutAntiCheatViewportValue + height + left2);
				int height2 = Math.Max(val, Math.Min(Theme.S(540), acViewport.Height));
				bool matchesHeight2 = height2 > acViewport.Height;
				if (matchesHeight2)
				{
					width = Math.Max(1, width - SystemInformation.VerticalScrollBarWidth);
					minLimit = ((!matchesWidth) ? Math.Max(Theme.S(290), Math.Min(Theme.S(360), (int)((float)width * 0.29f))) : 0);
					left3 = (matchesWidth ? left2 : (minLimit + scaledDimension));
					width2 = Math.Max(1, width - left3 - left2);
					width3 = (matchesWidth ? (width - left2 * 2) : (minLimit - left2));
					layoutAcControlBayResult = LayoutAcControlBay(width3, matchesWidth);
					top2 = (matchesWidth ? (layoutAcControlBayResult + Theme.S(20)) : left2);
					layoutAntiCheatViewportValue = 0;
					foreach (SettingCard acCard2 in acCards)
					{
						layoutAntiCheatViewportValue += ((AntiCheatModuleRow)acCard2).MeasureHeight(width2);
					}
					layoutAntiCheatViewportValue += Math.Max(0, acCards.Count - 1);
					height2 = Math.Max(height2, Math.Max((!matchesWidth) ? (layoutAcControlBayResult + left2) : 0, top2 + layoutAntiCheatViewportValue + height + left2));
				}
				acRosterBar.SetBounds(0, 0, width, height2);
				acControlBay.SetBounds(left2, left2, width3, matchesWidth ? layoutAcControlBayResult : (height2 - left2 * 2));
				int top3 = height2 - left2 - height;
				int height3 = Math.Max(layoutAntiCheatViewportValue, top3 - top2);
				acList.SetBounds(left3, top2, width2, height3);
				int top4 = 0;
				int processedCount = 0;
				int lowerBound = Math.Max(0, height3 - layoutAntiCheatViewportValue);
				foreach (SettingCard acCard3 in acCards)
				{
					AntiCheatModuleRow antiCheatModuleRow = (AntiCheatModuleRow)acCard3;
					int height4 = antiCheatModuleRow.MeasureHeight(width2) + lowerBound / Math.Max(1, acCards.Count) + ((processedCount < lowerBound % Math.Max(1, acCards.Count)) ? 1 : 0);
					antiCheatModuleRow.SetBounds(0, top4, width2, height4);
					antiCheatModuleRow.ArrangeContent();
					top4 += height4 + 1;
					processedCount++;
				}
				acUtility.SetBounds(left3, top3, width2, height);
				int scaledDimension2 = Theme.S(16);
				int lowerBound2 = Math.Max(1, (width2 - scaledDimension2) / 2);
				acRosterEntry.SetBounds(Theme.S(8), Theme.S(12), Math.Max(1, lowerBound2 - Theme.S(8)), Theme.S(42));
				acOtherToggle.SetBounds(lowerBound2 + scaledDimension2, Theme.S(12), Math.Max(1, width2 - lowerBound2 - scaledDimension2 - Theme.S(8)), Theme.S(42));
				acDrawer.SetBounds(left3, top2, width2, height3);
				LayoutAcDrawer(matchesHeight2);
				if (matchesHeight2 && acDrawer.Visible)
				{
					int heightValue = Math.Max(0, acDrawerBody.AutoScrollMinSize.Height - acDrawerBody.Height);
					if (heightValue > 0)
					{
						acDrawerBody.Height += heightValue;
						acDrawer.Height += heightValue;
						height2 += heightValue;
						acRosterBar.Height = height2;
						acUtility.Top += heightValue;
					}
				}
				acRosterBar.SplitX = ((!matchesWidth) ? (minLimit + Theme.S(8)) : 0);
				acRosterBar.UtilityBounds = new Rectangle(left3, acUtility.Top, width2, 1);
				acRosterBar.Invalidate();
				acList.Invalidate();
				acViewport.AutoScrollMinSize = new Size(0, height2);
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
			int left = Theme.S(stacked ? 14 : 22);
			int scaledDimension = Theme.S(stacked ? 14 : 24);
			int width2 = Theme.S(28);
			int left2 = left + width2 + Theme.S(10);
			acTitleIcon.SetBounds(left, scaledDimension + Theme.S(6), width2, width2);
			int val = Math.Max(1, width - left - left2 - Theme.S(26));
			acTitleLabel.SetBounds(left2, scaledDimension + Theme.S(4), val, Theme.S(30));
			int val2 = TextRenderer.MeasureText(acTitleLabel.Text, acTitleLabel.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			acHelp.SetBounds(left2 + Math.Min(val, val2), scaledDimension + Theme.S(6), Theme.S(26), Theme.S(26));
			int top = scaledDimension + Theme.S(stacked ? 55 : 86);
			acStrengthLabel.SetBounds(left, top, width - left * 2, Theme.S(22));
			acModePicker.Horizontal = stacked;
			int height = acModePicker.MinimumContentHeight(Math.Max(1, width - left * 2));
			acModePicker.SetBounds(left, top + Theme.S(34), Math.Max(1, width - left * 2), height);
			int top2 = acModePicker.Bottom + Theme.S(22);
			if (stacked)
			{
				acStrengthHelp.SetBounds(Math.Max(left, width - left - Theme.S(126)), top - Theme.S(2), Theme.S(126), Theme.S(26));
				return acModePicker.Bottom + Theme.S(16);
			}
			acStrengthHelp.SetBounds(left, top2, width - left * 2, Theme.S(32));
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
					float innerY = (state.BodyScroll ? state.InnerY : Math.Max(0f, state.OuterY - state.BodyOrigin));
					int val = DockScrollLimit(acDrawerBody);
					acDrawerBody.AutoScrollPosition = new Point(0, Math.Min((int)Math.Round(innerY * Dpi.Scale), val));
				}
				float outerY = state.OuterY;
				if (!acDrawerBody.AutoScroll && state.BodyScroll && state.Drawer.Length > 0)
				{
					int lowerBound = Math.Max(0, -acViewport.AutoScrollPosition.Y);
					outerY = (float)(acRosterBar.Top + lowerBound + acDrawer.Top + acDrawerBody.Top) / Dpi.Scale + state.InnerY;
				}
				int val2 = DockScrollLimit(acViewport);
				acViewport.AutoScrollPosition = new Point(0, Math.Min((int)Math.Round(outerY * Dpi.Scale), val2));
			}
		}
		private void LayoutAcDrawer(bool outerScroll)
		{
			Point point = new Point(0, Math.Max(0, -acDrawerBody.AutoScrollPosition.Y));
			int left = Theme.S(8);
			int width = Theme.S(122);
			int top = Theme.S(52);
			acDrawerTitle.SetBounds(left, Theme.S(12), Math.Max(1, acDrawer.Width - width - left * 3), Theme.S(26));
			acDrawerClose.SetBounds(Math.Max(left, acDrawer.Width - width - left), Theme.S(8), width, Theme.S(34));
			int top2 = top;
			if (acDrawerKind == "roster")
			{
				lblAcRoster.SetBounds(left, top, Math.Max(1, acDrawer.Width - Theme.S(120)), Theme.S(28));
				acClear.SetBounds(Math.Max(left, acDrawer.Width - Theme.S(96)), top - Theme.S(3), Theme.S(88), Theme.S(32));
				top2 += Theme.S(42);
			}
			acDrawerBody.SetBounds(left, top2, Math.Max(1, acDrawer.Width - left * 2), Math.Max(1, acDrawer.Height - top2 - left));
			acDrawerBody.AutoScrollPosition = Point.Empty;
			int width2 = Math.Max(1, acDrawerBody.Width - ((!outerScroll) ? SystemInformation.VerticalScrollBarWidth : 0));
			int top3 = 0;
			if (acDrawerKind == "records")
			{
				int layoutAcDrawerValue = ((width2 < Theme.S(620)) ? 1 : 2);
				int scaledDimension = Theme.S(10);
				int width3 = Math.Max(1, (width2 - (layoutAcDrawerValue - 1) * scaledDimension) / layoutAcDrawerValue);
				for (int i = 0; i < acReadOnlyCards.Count; i++)
				{
					acReadOnlyCards[i].SetBounds(i % layoutAcDrawerValue * (width3 + scaledDimension), i / layoutAcDrawerValue * Theme.S(62), width3, Theme.S(52));
				}
				top3 = (acReadOnlyCards.Count + layoutAcDrawerValue - 1) / layoutAcDrawerValue * Theme.S(62);
			}
			else if (acDrawerKind == "roster")
			{
				foreach (Control acRosterRow in acRosterRows)
				{
					acRosterRow.SetBounds(0, top3, width2, Theme.S(48));
					top3 += Theme.S(54);
				}
			}
			acDrawerBody.AutoScrollMinSize = new Size(0, top3);
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
				int left = Theme.S(24);
				int scaledDimension = Theme.S(8);
				int top = Theme.S(16);
				int height = pageAudit.ClientSize.Height;
				bool matchesRead = Volatile.Read(ref auditBusy) != 0;
				bool allConditionsMet = auditReport != null && !matchesRead;
				int top2 = ((allConditionsMet || matchesRead) ? (top + Theme.S(44)) : top);
				int height2 = (allConditionsMet ? Math.Max(1, height - top2 - scaledDimension) : Math.Max(Theme.S(400), height - top2 - scaledDimension));
				bool allConditionsMet2 = !allConditionsMet && top2 + height2 + scaledDimension > height;
				int width = Math.Max(1, pageAudit.ClientSize.Width - left * 2 - (allConditionsMet2 ? SystemInformation.VerticalScrollBarWidth : 0));
				int width2 = Math.Min(Theme.S(150), Math.Max(1, (width - scaledDimension) / 2));
				btnAuditRun.SetBounds(left, top, width2, Theme.S(34));
				btnAuditFixAll.SetBounds(left + width - width2, top, width2, Theme.S(34));
				btnAuditCancel.Bounds = btnAuditFixAll.Bounds;
				lblAuditStatus.SetBounds(btnAuditRun.Right + scaledDimension, top + Theme.S(8), Math.Max(1, btnAuditFixAll.Left - btnAuditRun.Right - scaledDimension * 2), Theme.S(20));
				auditScan.SetBounds(left, top2, width, height2);
				int width3 = Math.Min(Theme.S(184), Math.Max(1, width - scaledDimension * 2));
				int height3 = Theme.S(38);
				btnAuditStart.SetBounds((width - width3) / 2, Math.Min(auditScan.ContentBottom + Theme.S(20), height2 - height3 - scaledDimension), width3, height3);
				auditScroll.SetBounds(left, top2, width, Math.Max(1, height - top2 - scaledDimension));
				LayoutAuditRows();
				if (allConditionsMet2)
				{
					pageAudit.AutoScrollMinSize = new Size(0, top2 + height2 + scaledDimension);
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
				Control[] controlItems = new Control[auditScroll.Controls.Count];
				auditScroll.Controls.CopyTo(controlItems, 0);
				Array.Sort(controlItems, (Control a, Control b) => a.Top.CompareTo(b.Top));
				int topValue = 0;
				int auditContentWidth = AuditContentWidth;
				Control[] controlItems2 = controlItems;
				foreach (Control control in controlItems2)
				{
					control.Top += topValue;
					AuditRowCard auditRowCard = control as AuditRowCard;
					if (auditRowCard != null)
					{
						int height = auditRowCard.Height;
						auditRowCard.SetLogicalWidth(auditContentWidth);
						topValue += auditRowCard.Height - height;
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
			int left = ((root != null && !root.IsDisposed) ? root.Left : 0);
			int top = ((root != null && !root.IsDisposed) ? root.Top : 0);
			int minLimit = Math.Max(0, Math.Min(pagePolicy.ClientSize.Width, base.ClientSize.Width - left - pagePolicy.Left));
			int minLimit2 = Math.Max(0, Math.Min(pagePolicy.ClientSize.Height, base.ClientSize.Height - top - pagePolicy.Top));
			pagePolicy.AutoScroll = false;
			pagePolicy.AutoScrollMinSize = Size.Empty;
			int top2 = Theme.S(16);
			performanceViewportTop = top2 + Theme.S(62);
			int left2 = Theme.S(30);
			int left2Limit = Math.Max(left2, minLimit - left2);
			if (performanceCategories != null)
			{
				performanceCategories.SetBounds(left2, top2, left2Limit - left2, Theme.S(44));
			}
			LayoutConfigurationPresetControls(left2, top2, left2Limit);
			int left3 = Theme.S(20);
			int scaledDimension = Theme.S(8);
			DBPanel[] performanceTabPanelsSnapshot = performanceTabPanels;
			foreach (DBPanel dBPanel in performanceTabPanelsSnapshot)
			{
				if (dBPanel != null && !dBPanel.IsDisposed)
				{
					dBPanel.SetBounds(left3, performanceViewportTop, Math.Max(0, minLimit - left3 * 2), Math.Max(1, minLimit2 - performanceViewportTop - scaledDimension));
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
			int layoutCoreShortcutsValue = 0;
			int layoutCoreShortcutsValue2 = 0;
			int scaledDimension = Theme.S(6);
			int scaledDimension2 = Theme.S(30);
			foreach (Control control in coreQuickPanel.Controls)
			{
				if (layoutCoreShortcutsValue > 0 && layoutCoreShortcutsValue + control.Width > coreQuickPanel.Width)
				{
					layoutCoreShortcutsValue = 0;
					layoutCoreShortcutsValue2 += scaledDimension2 + scaledDimension;
				}
				control.Location = new Point(layoutCoreShortcutsValue, layoutCoreShortcutsValue2);
				layoutCoreShortcutsValue += control.Width + scaledDimension;
			}
			coreQuickPanel.Top = Theme.S(coreWorkspaceCompact ? 38 : 64);
			coreQuickPanel.Height = layoutCoreShortcutsValue2 + scaledDimension2;
		}
		private void LayoutCriticalThreadEditor()
		{
			if (criticalThreadEditor != null && !criticalThreadEditor.IsDisposed && criticalThreadInfo != null && criticalThreadMode != null)
			{
				int width = Math.Max(1, criticalThreadEditor.Width);
				criticalThreadEnabled.SetBounds(0, 0, width, Theme.S(40));
				int width2 = TextRenderer.MeasureText(criticalThreadEnabled.Text, Theme.UI(8f, true)).Width;
				criticalThreadInfo.SetBounds(Math.Min(Theme.S(36) + width2, Math.Max(0, width - Theme.S(142))), Theme.S(4), Theme.S(28), Theme.S(32));
				criticalThreadMode.SetBounds(Math.Max(0, width - Theme.S(65)), Theme.S(2), Theme.S(64), Theme.S(36));
				criticalThreadResult.SetBounds(Theme.S(12), Theme.S(46), Math.Max(1, width - Theme.S(24)), Theme.S(30));
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
			string coreWorkspaceLayoutKeyResult = CoreWorkspaceLayoutKey();
			if (coreWorkspaceLayoutKeyResult == coreWorkspaceLayoutKey)
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
				int left = Theme.S(24);
				int scaledDimension = Theme.S(coreWorkspaceCompact ? 6 : 16);
				int width = Math.Max(1, pageCoreScheduling.ClientSize.Width - left * 2);
				coreIdentity.SetBounds(left, Theme.S(coreWorkspaceCompact ? 6 : 20), width, Theme.S(coreWorkspaceCompact ? 56 : 88));
				coreEnableToggle.SetBounds(width - Theme.S(72), (coreIdentity.Height - Theme.S(30)) / 2, Theme.S(46), Theme.S(30));
				coreEnableInfo.SetBounds(width - Theme.S(110), (coreIdentity.Height - Theme.S(36)) / 2, Theme.S(32), Theme.S(36));
				coreEnableLabel.SetBounds(width - Theme.S(228), (coreIdentity.Height - Theme.S(36)) / 2, Theme.S(110), Theme.S(36));
				coreScrollPanel.SetBounds(left, coreIdentity.Bottom + scaledDimension, width, Math.Max(1, pageCoreScheduling.ClientSize.Height - coreIdentity.Bottom - scaledDimension - Theme.S(8)));
				coreScrollPanel.AutoScroll = false;
				int width2 = (int)Math.Round((double)(width - scaledDimension) * 0.59);
				coreEditor.SetBounds(0, 0, width2, coreScrollPanel.Height);
				((CoreWorkspaceSurface)coreEditor).Divider1 = Theme.S(coreWorkspaceCompact ? 36 : 54);
				coreRightRail.SetBounds(width2 + scaledDimension, 0, Math.Max(1, width - width2 - scaledDimension), coreScrollPanel.Height);
				coreRightRail.AutoScrollPosition = Point.Empty;
				int left2 = Theme.S(22);
				int width3 = Math.Max(1, width2 - left2 * 2);
				coreEditorTitle.SetBounds(left2, Theme.S(coreWorkspaceCompact ? 6 : 20), Math.Max(1, width3 - Theme.S(120)), Theme.S(30));
				coreRangeInfo.SetBounds(left2 + Math.Min(Theme.S(140), coreEditorTitle.Width), Theme.S(coreWorkspaceCompact ? 5 : 19), Theme.S(30), Theme.S(32));
				coreSelectedCount.SetBounds(Math.Max(left2, width2 - left2 - Theme.S(115)), Theme.S(coreWorkspaceCompact ? 8 : 22), Theme.S(115), Theme.S(26));
				coreSelectedCount.Text = CpuTopology.MultiGroup ? PerformancePolicy.Text("范围未确认", "Range unconfirmed") : PerformancePolicy.Text("已选 ", "Selected ") + CpuTopology.CountSetBits(coreEditMatrix.Selected) + " / " + CpuTopology.CountSetBits(CpuTopology.AllMask);
				coreQuickPanel.SetBounds(left2, Theme.S(coreWorkspaceCompact ? 38 : 64), width3, coreQuickPanel.Height);
				LayoutCoreShortcuts();
				coreTopologyCount.Text = CpuTopology.MultiGroup ? PerformancePolicy.Text("全机核心范围未确认", "Total CPU range unconfirmed") : CoreMatrix.TopologyCountsConfirmed ? CpuTopology.PhysicalCoreCount + PerformancePolicy.Text(" 物理核 / ", " cores / ") + CpuTopology.CountSetBits(CpuTopology.AllMask) + PerformancePolicy.Text(" 逻辑核", " logical CPUs") : CpuTopology.CountSetBits(CpuTopology.AllMask) + PerformancePolicy.Text(" 逻辑核 · 物理核数未确认", " logical CPUs · physical core count unconfirmed");
				int layoutCoreWorkspaceValue = 0;
				foreach (Control control in coreQuickPanel.Controls)
				{
					layoutCoreWorkspaceValue = Math.Max(layoutCoreWorkspaceValue, control.Right);
				}
				int width4 = TextRenderer.MeasureText(coreTopologyCount.Text, coreTopologyCount.Font).Width + Theme.S(6);
				coreTopologyCount.Visible = coreQuickPanel.Height <= Theme.S(30) && layoutCoreWorkspaceValue + Theme.S(14) + width4 <= width3;
				coreTopologyCount.TextAlign = ContentAlignment.MiddleRight;
				coreTopologyCount.SetBounds(width2 - left2 - width4, coreQuickPanel.Top + Theme.S(3), width4, Theme.S(24));
				coreMatrixViewport.SetBounds(left2 - Theme.S(2), coreQuickPanel.Bottom + Theme.S(coreWorkspaceCompact ? 10 : 18), width3 + Theme.S(6), Math.Max(1, coreEditor.Height - coreQuickPanel.Bottom - Theme.S(coreWorkspaceCompact ? 18 : 34)));
				Point autoScrollPosition = coreMatrixViewport.AutoScrollPosition;
				coreMatrixViewport.AutoScrollPosition = Point.Empty;
				int width5 = Math.Max(1, coreMatrixViewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - Theme.S(4));
				if (coreMatrixLayoutWidth != width5)
				{
					coreMatrixLayoutWidth = width5;
					coreEditMatrix.SetBounds(Theme.S(2), 0, width5, coreEditMatrix.LayoutFor(width5));
				}
				coreMatrixViewport.AutoScrollMinSize = new Size(0, coreEditMatrix.Height);
				coreMatrixViewport.AutoScroll = true;
				coreMatrixViewport.AutoScrollPosition = new Point(0, Math.Max(0, -autoScrollPosition.Y));
				Label label = coreEditorHelp;
				bool visible = (coreEditorRuntime.Visible = false);
				label.Visible = visible;
				int lowerBound = Math.Max(1, coreRightRail.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
				int width6 = Math.Max(1, lowerBound - left2 * 2);
				coreIsolationGlyph.SetBounds(left2, Theme.S(27), Theme.S(28), Theme.S(28));
				coreEditorIsolation.Text = PerformancePolicy.Text("核心隔离", "Core isolation");
				bool matchesWidth6 = width6 < Theme.S(280);
				int scaledDimension2 = ((!matchesWidth6) ? Theme.S(44) : 0);
				coreIsolationGlyph.Visible = !matchesWidth6;
				coreEditorIsolation.SetBounds(left2 + scaledDimension2, Theme.S(24), Math.Max(1, width6 - scaledDimension2 - Theme.S(matchesWidth6 ? 32 : 88)), Theme.S(30));
				coreEditorIsolation.Height = Math.Max(Theme.S(30), WrappedHeight(coreEditorIsolation));
				coreIsolationInfo.SetBounds(lowerBound - left2 - Theme.S(matchesWidth6 ? 28 : 88), Theme.S(24), Theme.S(28), Theme.S(32));
				coreIsolationToggle.SetBounds(lowerBound - left2 - Theme.S(46), matchesWidth6 ? (coreEditorIsolation.Bottom + Theme.S(8)) : Theme.S(25), Theme.S(46), Theme.S(30));
				coreIsolationHint.SetBounds(left2 + scaledDimension2, Math.Max(Theme.S(64), Math.Max(coreEditorIsolation.Bottom, coreIsolationToggle.Bottom) + Theme.S(8)), Math.Max(1, width6 - scaledDimension2), Theme.S(24));
				coreIsolationHint.Height = WrappedHeight(coreIsolationHint);
				int top = coreIsolationHint.Bottom + Theme.S(20);
				((CoreWorkspaceSurface)coreRightRail).Divider1 = top - Theme.S(10);
				((CoreWorkspaceSurface)coreRightRail).Divider2 = 0;
				corePlanPreview.SetBounds(left2, top, width6, corePlanPreview.Height);
				top = corePlanPreview.Bottom + Theme.S(12);
				AutoCoreStatus status = AutoCores.Status;
				coreStatusPanel.Visible = coreDraftProfile == null || status.Pending || CoreIsolationClient.State == "schedule.isolation.pending" || (status.ProfileId == coreDraftProfile && status.Blocked != null);
				if (coreStatusPanel.Visible)
				{
					coreStatusPanel.SetBounds(left2, top, width6, Theme.S(80));
					lblCoreState.SetBounds(0, 0, width6, Theme.S(26));
					lblCoreState.Height = WrappedHeight(lblCoreState);
					lblCoreReason.SetBounds(0, lblCoreState.Bottom + Theme.S(4), width6, Theme.S(30));
					lblCoreReason.Height = WrappedHeight(lblCoreReason);
					coreStatusPanel.Height = lblCoreReason.Bottom + Theme.S(8);
					top = coreStatusPanel.Bottom + Theme.S(8);
				}
				coreMapPanel.Visible = lblCoreReserved.Text.Length > 0;
				if (coreMapPanel.Visible)
				{
					coreMapPanel.SetBounds(left2, top, width6, Theme.S(80));
					lblCoreReserved.SetBounds(0, 0, width6, Theme.S(40));
					lblCoreReserved.Height = WrappedHeight(lblCoreReserved);
					btnCoreClearReserved.SetBounds(0, lblCoreReserved.Bottom + Theme.S(6), width6, Theme.S(34));
					coreMapPanel.Height = (btnCoreClearReserved.Visible ? btnCoreClearReserved.Bottom : lblCoreReserved.Bottom) + Theme.S(8);
					top = coreMapPanel.Bottom + Theme.S(8);
				}
				coreEditorResult.SetBounds(left2, top, width6, Theme.S(30));
				if (coreEditorResult.Visible)
				{
					coreEditorResult.Height = WrappedHeight(coreEditorResult);
					top = coreEditorResult.Bottom + Theme.S(10);
				}
				coreEvidenceToggle.Text = PerformancePolicy.Text("查看绑定证据  ›", "View affinity evidence  ›");
				coreEvidenceToggle.SetBounds(left2, top, width6, Theme.S(40));
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
				int uLimit = Math.Max(280, Dpi.U(pageLibrary.ClientSize.Width));
				int uResult = Dpi.U(pageLibrary.ClientSize.Height);
				int layoutLibraryViewportValue = ((uLimit < 500) ? 16 : 24);
				int remainingULimit = uLimit - layoutLibraryViewportValue * 2;
				bool matchesULimit = uLimit >= 1040;
				bool matchesULimit2 = uLimit >= 570;
				int remainingRemainingULimit = (matchesULimit ? 238 : (matchesULimit2 ? (remainingULimit - 132) : remainingULimit));
				SetOverviewBounds(librarySearchPanel, layoutLibraryViewportValue, 16, remainingRemainingULimit, 36);
				librarySearch.SetBounds(Theme.S(42), Theme.S(9), Math.Max(1, librarySearchPanel.Width - Theme.S(54)), Theme.S(24));
				SetOverviewBounds(libraryAdd, uLimit - layoutLibraryViewportValue - 120, matchesULimit2 ? 16 : 62, 120, 36);
				int layoutLibraryViewportValue2 = (matchesULimit ? 16 : 62);
				SetOverviewBounds(libraryPlatforms, matchesULimit ? (layoutLibraryViewportValue + 250) : layoutLibraryViewportValue, layoutLibraryViewportValue2, matchesULimit2 ? 214 : (remainingULimit - 132), 36);
				int layoutLibraryViewportValue3 = (matchesULimit ? 16 : (matchesULimit2 ? 62 : 108));
				int layoutLibraryViewportValue4 = (matchesULimit ? (uLimit - layoutLibraryViewportValue - 120 - 12 - 196) : (matchesULimit2 ? (uLimit - layoutLibraryViewportValue - 196) : layoutLibraryViewportValue));
				SetOverviewBounds(swAutoAdd, layoutLibraryViewportValue4, layoutLibraryViewportValue3, 152, 36);
				SetOverviewBounds(libraryAutoSettings, layoutLibraryViewportValue4 + 160, layoutLibraryViewportValue3, 36, 36);
				int layoutLibraryViewportValueOffset = (matchesULimit ? (layoutLibraryViewportValue + 480) : (matchesULimit2 ? (layoutLibraryViewportValue + 226) : (uLimit - layoutLibraryViewportValue - 100)));
				SetOverviewBounds(lblLibraryCount, layoutLibraryViewportValueOffset, matchesULimit ? 20 : (matchesULimit2 ? 66 : 112), matchesULimit ? Math.Max(0, layoutLibraryViewportValue4 - layoutLibraryViewportValueOffset - 12) : (matchesULimit2 ? Math.Max(0, layoutLibraryViewportValue4 - layoutLibraryViewportValueOffset - 12) : 100), 28);
				int layoutLibraryViewportValue5 = (matchesULimit ? 68 : (matchesULimit2 ? 114 : 160));
				SetOverviewBounds(lstGames, layoutLibraryViewportValue, layoutLibraryViewportValue5, remainingULimit, Math.Max(80, uResult - layoutLibraryViewportValue5 - 8));
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
				int uLimit = Math.Max(1, Dpi.U(pageOverview.ClientSize.Width));
				int uLimit2 = Math.Max(1, Dpi.U(pageOverview.ClientSize.Height));
				int layoutOverviewPageValue = 42;
				int statusY = Math.Max(8, Math.Min(uLimit2 - layoutOverviewPageValue - 8 - (overviewActivityShown ? 30 : 0), (int)Math.Round((float)uLimit2 * 0.87f) - layoutOverviewPageValue / 2));
				int warningReserve = overviewSecurityWarningShown ? 44 : 0;
				int val = Math.Max(1, Math.Min(450, statusY - 26 - warningReserve));
				int minLimit = Math.Max(1, Math.Min(580, uLimit - 48));
				val = Math.Min(val, (int)Math.Round((float)minLimit * 450f / 580f));
				minLimit = Math.Min(minLimit, (int)Math.Round((float)val * 580f / 450f));
				int coreY = Math.Max(0, (statusY - warningReserve - val) / 2);
				int arrangeOverviewHudResult = ArrangeOverviewHud(uLimit, uLimit2, ref minLimit, ref val, ref coreY, ref statusY);
				SetOverviewBounds(paviseCore, (uLimit - minLimit) / 2, coreY, minLimit, val);
				int lowerBound = ((arrangeOverviewHudResult > uLimit2) ? Math.Max(1, uLimit - Dpi.U(SystemInformation.VerticalScrollBarWidth)) : uLimit);
				int roundedValue = (int)Math.Ceiling((float)TextRenderer.MeasureText(lblOverviewGuard.Text, lblOverviewGuard.Font, Size.Empty, TextFormatFlags.NoPadding).Width / Math.Max(0.1f, Dpi.Scale));
				int val2 = (int)Math.Ceiling((float)TextRenderer.MeasureText(lblStatus.Text, Theme.UI(14.5f, true), Size.Empty, TextFormatFlags.NoPadding).Width / Math.Max(0.1f, Dpi.Scale)) + 3;
				int layoutOverviewPageValue2 = 86 + roundedValue + 12 + 72;
				val2 = Math.Min(val2, Math.Max(24, lowerBound - 32 - layoutOverviewPageValue2));
				int layoutOverviewPageValue2Offset = layoutOverviewPageValue2 + val2;
				int lowerBound2 = Math.Max(8, (lowerBound - layoutOverviewPageValue2Offset) / 2);
				SetOverviewBounds(statusDot, lowerBound2, statusY + 9, 24, 24);
				lowerBound2 += 33;
				SetOverviewBounds(lblStatus, lowerBound2, statusY, val2, layoutOverviewPageValue);
				FitLabelFont(lblStatus, true, 14.5f, 9.5f);
				lowerBound2 += val2 + 26;
				SetOverviewBounds(overviewStatusDivider, lowerBound2, statusY + 11, 1, 20);
				lowerBound2 += 27;
				SetOverviewBounds(lblOverviewGuard, lowerBound2, statusY, roundedValue, layoutOverviewPageValue);
				lowerBound2 += roundedValue + 12;
				SetOverviewBounds(swGame, lowerBound2, statusY + 5, 72, 32);
				LayoutOverviewSecurityWarning(lowerBound, statusY);
				if (overviewActivity != null)
				{
					SetOverviewBounds(overviewActivity, 16, statusY + layoutOverviewPageValue + 2, Math.Max(1, lowerBound - 32), 26);
					if (overviewActivityShown) arrangeOverviewHudResult = Math.Max(arrangeOverviewHudResult, statusY + layoutOverviewPageValue + 36);
				}
				pageOverview.AutoScrollMinSize = ((arrangeOverviewHudResult > uLimit2) ? new Size(0, Theme.S(arrangeOverviewHudResult)) : Size.Empty);
				pageOverview.AutoScroll = arrangeOverviewHudResult > uLimit2;
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
					int remainingHeight = settingCard.Height - settingCard.ExpandedHeight;
					if (remainingHeight != 0)
					{
						list.Add(new int[2] { value2, remainingHeight });
					}
				}
			}
			ScrollableControl scrollableControl = panel as ScrollableControl;
			int top = ((scrollableControl != null) ? scrollableControl.AutoScrollPosition.Y : 0);
			panel.SuspendLayout();
			foreach (Control control4 in panel.Controls)
			{
				int value3;
				if (!value.TryGetValue(control4, out value3))
				{
					continue;
				}
				int restackCardsValue = 0;
				foreach (int[] item in list)
				{
					if (item[0] < value3)
					{
						restackCardsValue += item[1];
					}
				}
				int topValue = value3 + restackCardsValue + top;
				if (control4.Top != topValue)
				{
					control4.Top = topValue;
				}
			}
			panel.ResumeLayout();
		}
		private void LayoutCompactSettingsAppearance(DBPanel panel)
		{
			Point autoScrollPosition = new Point(0, -panel.AutoScrollPosition.Y);
			panel.AutoScrollPosition = Point.Empty;
			int width = SettingsContentWidth(panel);
			int layoutCompactSettingsAppearanceValue = 0;
			SettingsAppearanceEditorPanel settingsAppearanceEditorPanel = panel.Controls["SettingsAppearanceEditor"] as SettingsAppearanceEditorPanel;
			if (settingsAppearanceEditorPanel != null)
			{
				int left = Theme.S(16);
				int scaledDimension = Theme.S(24);
				int val = Math.Max(1, width - left * 2);
				bool matchesWidth = width >= Theme.S(940);
				int sLimit = (matchesWidth ? Math.Min(Theme.S(490), (width - scaledDimension) * 44 / 100) : width);
				int sLimitOffset = (matchesWidth ? (sLimit + scaledDimension) : 0);
				int remainingWidth = (matchesWidth ? (width - sLimitOffset) : width);
				int scaledDimension2 = Theme.S(22);
				int top = Theme.S(12);
				int top2 = Theme.S(48);
				settingsAppearanceEditorPanel.BackgroundIconBounds = new Rectangle(left, top, scaledDimension2, scaledDimension2);
				Control control = settingsAppearanceEditorPanel.Controls["SettingsBackdropTitle"];
				if (control != null)
				{
					control.SetBounds(left + Theme.S(34), top, Math.Max(1, sLimit - left * 2 - Theme.S(34)), Theme.S(24));
				}
				backdropPreview.SetBounds(left, top2, Theme.S(120), Theme.S(80));
				int left2 = left + Theme.S(136);
				int width2 = Math.Max(1, sLimit - left2 - left);
				int height = Math.Max(Theme.S(24), Math.Max(CompactAppearanceTextHeight(Lang.T("backdrop.status.off"), lblBackdropState.Font, width2), CompactAppearanceTextHeight(Lang.F("backdrop.status.on", Lang.T("backdrop.dim.3")), lblBackdropState.Font, width2)));
				lblBackdropState.SetBounds(left2, top2, width2, height);
				int compactAppearanceButtonWidthLimit = Math.Max(CompactAppearanceButtonWidth(btnBackdrop), TextRenderer.MeasureText(Lang.T("btn.backdrop.change"), btnBackdrop.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(28));
				int compactAppearanceButtonWidthResult = CompactAppearanceButtonWidth(btnBackdropReset);
				int scaledDimension3 = Theme.S(8);
				bool matchesWidth2 = width2 >= compactAppearanceButtonWidthLimit + compactAppearanceButtonWidthResult + scaledDimension3;
				int left3 = (matchesWidth2 ? left2 : left);
				int top3 = (matchesWidth2 ? (top2 + height + Theme.S(8)) : (Math.Max(backdropPreview.Bottom, lblBackdropState.Bottom) + Theme.S(12)));
				int remainingLeft = sLimit - left3 - left;
				bool matchesRemainingLeft = remainingLeft < compactAppearanceButtonWidthLimit + compactAppearanceButtonWidthResult + scaledDimension3;
				btnBackdropReset.SetBounds(left3, top3, Math.Min(compactAppearanceButtonWidthResult, remainingLeft), Theme.S(32));
				btnBackdrop.SetBounds(matchesRemainingLeft ? left3 : (btnBackdropReset.Right + scaledDimension3), matchesRemainingLeft ? (btnBackdropReset.Bottom + scaledDimension3) : top3, Math.Min(compactAppearanceButtonWidthLimit, remainingLeft), Theme.S(32));
				Control control2 = settingsAppearanceEditorPanel.Controls["SettingsBackdropDimTitle"];
				int top4 = Math.Max(Math.Max(backdropPreview.Bottom, lblBackdropState.Bottom), btnBackdrop.Bottom) + Theme.S(16);
				int scaledDimension4 = ((control2 == null) ? Theme.S(100) : TextRenderer.MeasureText(control2.Text, control2.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
				int compactAppearancePickerWidthResult = CompactAppearancePickerWidth(pickBackdropDim);
				bool valuesMatch = sLimit - left * 2 >= scaledDimension4 + Theme.S(16) + compactAppearancePickerWidthResult;
				if (control2 != null)
				{
					control2.SetBounds(left, top4, Math.Min(val, scaledDimension4 + Theme.S(4)), Theme.S(32));
				}
				int left4 = (valuesMatch ? (left + scaledDimension4 + Theme.S(16)) : left);
				pickBackdropDim.SetBounds(left4, valuesMatch ? top4 : (top4 + Theme.S(34)), Math.Min(compactAppearancePickerWidthResult, Math.Max(1, sLimit - left4 - left)), Theme.S(32));
				int bottom = pickBackdropDim.Bottom;
				int top5 = (matchesWidth ? top : (bottom + Theme.S(28)));
				settingsAppearanceEditorPanel.AccentIconBounds = new Rectangle(sLimitOffset + left, top5, scaledDimension2, scaledDimension2);
				Control control3 = settingsAppearanceEditorPanel.Controls["SettingsAccentTitle"];
				Control control4 = settingsAppearanceEditorPanel.Controls["SettingsAccentHint"];
				if (control3 != null)
				{
					control3.SetBounds(sLimitOffset + left + Theme.S(34), top5, Math.Max(1, remainingWidth - left * 2 - Theme.S(34)), Theme.S(24));
				}
				if (control4 != null)
				{
					control4.Visible = false;
				}
				int top6 = top5 + Theme.S(36);
				int scaledDimension5 = Theme.S(30);
				int height2 = Theme.S(24);
				int width3 = Theme.S(82);
				int sOffset = sLimitOffset + left + width3 + Theme.S(8);
				int lowerBound = Math.Max(1, remainingWidth - left * 2 - width3 - Theme.S(8));
				int lowerBound2 = Math.Max(1, (lowerBound + scaledDimension5 - height2) / scaledDimension5);
				PerformancePreset[] visibleOrderResult = PresetValue.VisibleOrder();
				foreach (PerformancePreset performancePreset in visibleOrderResult)
				{
					Label label = modeAccentLabels[(int)performancePreset];
					if (label != null)
					{
						label.SetBounds(sLimitOffset + left, top6, width3, height2);
					}
					int processedCount = 0;
					foreach (ColorSwatch item in modeSwatches[(int)performancePreset])
					{
						item.SetBounds(sOffset + processedCount % lowerBound2 * scaledDimension5, top6 + processedCount / lowerBound2 * scaledDimension5, height2, height2);
						processedCount++;
					}
					top6 += Math.Max(Theme.S(32), (processedCount + lowerBound2 - 1) / lowerBound2 * scaledDimension5);
				}
				int height3 = Math.Max(bottom, top6) + Theme.S(16);
				Control control5 = settingsAppearanceEditorPanel.Controls["SettingsAppearanceSeparator"];
				if (control5 != null)
				{
					if (matchesWidth)
					{
						control5.SetBounds(sLimit + scaledDimension / 2, Theme.S(12), Math.Max(1, Theme.S(1)), Math.Max(1, height3 - Theme.S(24)));
					}
					else
					{
						control5.SetBounds(left, bottom + Theme.S(14), val, Math.Max(1, Theme.S(1)));
					}
				}
				settingsAppearanceEditorPanel.SetBounds(0, 0, width, height3);
				settingsAppearanceEditorPanel.Invalidate();
				layoutCompactSettingsAppearanceValue = settingsAppearanceEditorPanel.Bottom;
			}
			if (motionAppearance != null && !motionAppearance.IsDisposed && motionAppearance.Parent == panel)
			{
				motionAppearance.SetBounds(0, layoutCompactSettingsAppearanceValue + Theme.S(12), width, motionAppearance.MeasureHeight(width));
				motionAppearance.PerformLayout();
				layoutCompactSettingsAppearanceValue = Math.Max(motionAppearance.Bottom, motionAppearance.Top + motionAppearance.Controls["MotionSaveStatus"].Bottom + Theme.S(12));
				if (motionAppearance.Bottom < layoutCompactSettingsAppearanceValue) motionAppearance.Height = layoutCompactSettingsAppearanceValue - motionAppearance.Top;
			}
			foreach (Control control6 in panel.Controls)
			{
				if (control6 is SettingsWorkspaceTile)
				{
					SettingsWorkspaceTile settingsWorkspaceTile = (SettingsWorkspaceTile)control6;
					settingsWorkspaceTile.CompactRow = true;
					settingsWorkspaceTile.BottomSeparator = false;
					settingsWorkspaceTile.SetBounds(0, layoutCompactSettingsAppearanceValue + Theme.S(4), width, settingsWorkspaceTile.MeasureHeight(width));
					settingsWorkspaceTile.ArrangeContent();
					layoutCompactSettingsAppearanceValue = settingsWorkspaceTile.Bottom;
				}
			}
			panel.AutoScrollMinSize = new Size(0, layoutCompactSettingsAppearanceValue + Theme.S(4));
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
			int scaledDimension = Theme.S(48);
			string[] labels = picker.Labels;
			foreach (string entryText in labels)
			{
				scaledDimension = Math.Max(scaledDimension, TextRenderer.MeasureText(entryText, DsType.Font(DsText.Label, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(20));
			}
			return scaledDimension * picker.Labels.Length + Theme.S(4) * (picker.Labels.Length - 1);
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
				int left = Theme.S(24);
				int top = Theme.S(12);
				int scaledDimension = Theme.S(12);
				settingsWorkspaceViewport.SetBounds(0, 0, pageSettings.ClientSize.Width, pageSettings.ClientSize.Height);
				int width = Math.Max(Theme.S(300), settingsWorkspaceViewport.ClientSize.Width - left * 2);
				int tabGap = Theme.S(6);
				int tabHeight = Theme.S(44);
				int tabX = 0;
				int tabY = Theme.S(4);
				foreach (SettingsWorkspaceTab settingsWorkspaceTab in settingsWorkspaceTabs)
				{
					int tabWidth = Math.Min(width, Math.Max(Theme.S(124), TextRenderer.MeasureText(settingsWorkspaceTab.Text, Theme.UI(10f, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(44)));
					if (tabX > 0 && tabX + tabWidth > width)
					{
						tabX = 0;
						tabY += tabHeight + tabGap;
					}
					settingsWorkspaceTab.SetBounds(tabX, tabY, tabWidth, tabHeight);
					tabX += tabWidth + tabGap;
				}
				int lowerBound = Math.Max(1, settingsWorkspaceViewport.ClientSize.Height - top - scaledDimension);
				int frameTopValue = tabY + tabHeight + Theme.S(12);
				int top2 = frameTopValue + Theme.S(12);
				int height = Math.Min(Theme.S(540), lowerBound);
				int height2 = Math.Max(1, height - top2 - Theme.S(12));
				settingsWorkspaceSurface.FrameTop = frameTopValue;
				settingsWorkspaceSurface.BackColor = Theme.Bg;
				settingsWorkspaceSurface.SetBounds(left, top, width, height);
				int left2 = Theme.S(16);
				settingsWorkspaceSurface.HeaderLine = 0;
				for (int j = 0; j < settingsTabPanels.Length; j++)
				{
					DBPanel dBPanel = settingsTabPanels[j];
					dBPanel.SetBounds(left2, top2, width - left2 * 2, height2);
					dBPanel.AutoScroll = true;
					dBPanel.BackColor = Theme.Card;
				}
				LayoutSettingsGeneral(settingsTabPanels[0]);
				LayoutSettingsMaintenance(settingsTabPanels[1]);
				LayoutSettingsAppearance(settingsTabPanels[2]);

				DBPanel dBPanel2 = settingsTabPanels[settingsTabs.Index];
				height = Math.Min(lowerBound, top2 + dBPanel2.AutoScrollMinSize.Height + Theme.S(12));
				settingsWorkspaceSurface.Height = height;
				dBPanel2.Height = Math.Max(1, height - top2 - Theme.S(12));
				settingsWorkspaceSurface.DivisionX = 0;
				settingsWorkspaceSurface.DivisionTop = 0;
				SettingsWorkspaceTile settingsWorkspaceTile = SettingsTile(settingsTabPanels[0], "SettingsStandbyCleanerCard");
				settingsWorkspaceSurface.DivisionBottom = 0;
				SettingsWorkspaceCategoryPanel settingsWorkspaceCategoryPanel = settingsTabPanels[0] as SettingsWorkspaceCategoryPanel;
				if (settingsWorkspaceCategoryPanel != null)
				{
					int settingsContentWidthResult = SettingsContentWidth(settingsWorkspaceCategoryPanel);
					settingsWorkspaceCategoryPanel.DivisionX = ((settingsContentWidthResult >= Theme.S(806)) ? (settingsContentWidthResult / 2) : 0);
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
			int width = SettingsContentWidth(panel);
			int scaledDimension = Theme.S(24);
			int top = 0;
			if (width >= Theme.S(806))
			{
				int width2 = (width - scaledDimension) / 2;
				int height = Math.Max(settingsWorkspaceTile.MeasureHeight(width2), settingsWorkspaceTile3.MeasureHeight(width2));
				int height2 = Math.Max(settingsWorkspaceTile2.MeasureHeight(width2), settingsWorkspaceTile4.MeasureHeight(width2));
				int height3 = settingsWorkspaceTile5.MeasureHeight(width);
				settingsWorkspaceTile.SetBounds(0, 0, width2, height);
				settingsWorkspaceTile3.SetBounds(width2 + scaledDimension, 0, width - width2 - scaledDimension, height);
				settingsWorkspaceTile2.SetBounds(0, height, width2, height2);
				settingsWorkspaceTile4.SetBounds(width2 + scaledDimension, height, width - width2 - scaledDimension, height2);
				settingsWorkspaceTile5.SetBounds(0, height + height2, width, height3);
				top = settingsWorkspaceTile5.Bottom;
			}
			else
			{
				SettingsWorkspaceTile[] settingsWorkspaceTileItems = new SettingsWorkspaceTile[5] { settingsWorkspaceTile, settingsWorkspaceTile2, settingsWorkspaceTile3, settingsWorkspaceTile4, settingsWorkspaceTile5 };
				foreach (SettingsWorkspaceTile settingsWorkspaceTile7 in settingsWorkspaceTileItems)
				{
					settingsWorkspaceTile7.SetBounds(0, top, width, settingsWorkspaceTile7.MeasureHeight(width));
					top = settingsWorkspaceTile7.Bottom;
				}
			}
			aiTuning.SetBounds(0, top, width, aiTuning.MeasureHeight(width));
			top = aiTuning.Bottom;
			settingsWorkspaceTile5.BottomSeparator = true;
			aiTuning.BottomSeparator = false;
			SettingsWorkspaceTile[] settingsWorkspaceTileItems2 = new SettingsWorkspaceTile[6] { settingsWorkspaceTile, settingsWorkspaceTile2, settingsWorkspaceTile3, settingsWorkspaceTile4, settingsWorkspaceTile5, aiTuning };
			foreach (SettingsWorkspaceTile settingsWorkspaceTile8 in settingsWorkspaceTileItems2)
			{
				settingsWorkspaceTile8.ArrangeContent();
			}
			panel.AutoScrollMinSize = new Size(0, top);
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
					int width = pageWhitelist.ClientSize.Width;
					int uLimit = Math.Max(280, Dpi.U(width));
					int uLimit2 = Math.Max(1, Dpi.U(pageWhitelist.ClientSize.Height));
					int layoutWhitelistViewportValue = ((uLimit < 600) ? 16 : 24);
					int remainingULimit = uLimit - layoutWhitelistViewportValue * 2;
					int layoutWhitelistViewportValue2 = 10;
					bool matchesULimit = uLimit >= 920;
					bool matchesULimit2 = uLimit >= 520;
					int layoutWhitelistViewportValue3 = 16;
					int layoutWhitelistViewportValue4;
					if (matchesULimit)
					{
						WhiteBounds(whiteSearchPanel, layoutWhitelistViewportValue, layoutWhitelistViewportValue3, 280, 38);
						WhiteBounds(whiteFilters, layoutWhitelistViewportValue + 292, layoutWhitelistViewportValue3, 300, 38);
						WhiteBounds(whiteCount, layoutWhitelistViewportValue + 608, layoutWhitelistViewportValue3, Math.Max(1, remainingULimit - 864), 38);
						WhiteBounds(whitePick, uLimit - layoutWhitelistViewportValue - 248, layoutWhitelistViewportValue3, 126, 38);
						WhiteBounds(whiteBrowse, uLimit - layoutWhitelistViewportValue - 112, layoutWhitelistViewportValue3, 112, 38);
						layoutWhitelistViewportValue4 = layoutWhitelistViewportValue3 + 38 + 12;
					}
					else if (matchesULimit2)
					{
						WhiteBounds(whiteSearchPanel, layoutWhitelistViewportValue, layoutWhitelistViewportValue3, remainingULimit - 248, 38);
						WhiteBounds(whitePick, uLimit - layoutWhitelistViewportValue - 238, layoutWhitelistViewportValue3, 126, 38);
						WhiteBounds(whiteBrowse, uLimit - layoutWhitelistViewportValue - 102, layoutWhitelistViewportValue3, 102, 38);
						WhiteBounds(whiteFilters, layoutWhitelistViewportValue, layoutWhitelistViewportValue3 + 48, Math.Min(330, remainingULimit - 100), 34);
						WhiteBounds(whiteCount, layoutWhitelistViewportValue + Math.Min(330, remainingULimit - 100) + 12, layoutWhitelistViewportValue3 + 48, Math.Max(1, remainingULimit - 342), 34);
						layoutWhitelistViewportValue4 = layoutWhitelistViewportValue3 + 94;
					}
					else
					{
						WhiteBounds(whiteSearchPanel, layoutWhitelistViewportValue, layoutWhitelistViewportValue3, remainingULimit, 38);
						WhiteBounds(whiteFilters, layoutWhitelistViewportValue, layoutWhitelistViewportValue3 + 48, remainingULimit, 34);
						int ratio = (remainingULimit - layoutWhitelistViewportValue2) / 2;
						WhiteBounds(whitePick, layoutWhitelistViewportValue, layoutWhitelistViewportValue3 + 92, ratio, 34);
						WhiteBounds(whiteBrowse, layoutWhitelistViewportValue + ratio + layoutWhitelistViewportValue2, layoutWhitelistViewportValue3 + 92, remainingULimit - ratio - layoutWhitelistViewportValue2, 34);
						layoutWhitelistViewportValue4 = layoutWhitelistViewportValue3 + 138;
					}
					whiteCount.Visible = (matchesULimit || matchesULimit2) && whiteCount.Width >= Theme.S(70);
					whiteSearch.SetBounds(Theme.S(40), Theme.S(10), Math.Max(1, whiteSearchPanel.Width - Theme.S(52)), Theme.S(24));
					bool matchesRemainingULimit = remainingULimit < 620;
					int layoutWhitelistViewportValue5 = (matchesRemainingULimit ? 82 : 36);
					int lowerBound = Math.Max(layoutWhitelistViewportValue4 + 152, uLimit2 - layoutWhitelistViewportValue - layoutWhitelistViewportValue5);
					WhiteBounds(whitePanel, layoutWhitelistViewportValue, layoutWhitelistViewportValue4, remainingULimit, lowerBound - layoutWhitelistViewportValue4 - 12);
					if (matchesRemainingULimit)
					{
						int ratio2 = (remainingULimit - layoutWhitelistViewportValue2) / 2;
						WhiteBounds(btnRestorePlatformProtection, layoutWhitelistViewportValue, lowerBound, ratio2, 34);
						WhiteBounds(whiteReset, layoutWhitelistViewportValue + ratio2 + layoutWhitelistViewportValue2, lowerBound, remainingULimit - ratio2 - layoutWhitelistViewportValue2, 34);
						WhiteBounds(btnRemoveWhitelist, uLimit - layoutWhitelistViewportValue - 88, lowerBound + 44, 88, 34);
						WhiteBounds(lblWhiteHint, layoutWhitelistViewportValue + 2, lowerBound + 44, Math.Max(1, remainingULimit - 104), 34);
					}
					else
					{
						WhiteBounds(btnRemoveWhitelist, uLimit - layoutWhitelistViewportValue - 88, lowerBound, 88, 36);
						WhiteBounds(whiteReset, uLimit - layoutWhitelistViewportValue - 206, lowerBound, 108, 36);
						WhiteBounds(btnRestorePlatformProtection, uLimit - layoutWhitelistViewportValue - 356, lowerBound, 140, 36);
						WhiteBounds(lblWhiteHint, layoutWhitelistViewportValue + 2, lowerBound, Math.Max(1, remainingULimit - 374), 36);
					}
					int v = lowerBound + layoutWhitelistViewportValue5 + layoutWhitelistViewportValue;
					bool autoScrollValue = Theme.S(v) > pageWhitelist.ClientSize.Height;
					pageWhitelist.AutoScrollMargin = Size.Empty;
					pageWhitelist.AutoScrollMinSize = (autoScrollValue ? new Size(0, Theme.S(v)) : Size.Empty);
					pageWhitelist.AutoScroll = autoScrollValue;
					if (width == pageWhitelist.ClientSize.Width)
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
			int width = Theme.S(WinW);
			int scaledDimension = Theme.S(WinH);
			root.Size = new Size(width, scaledDimension);
			CenterRoot();
			int height = Math.Min(scaledDimension, Theme.S(TopH));
			if (nav != null)
			{
				Size size = nav.MeasureFloatingDock(Math.Max(1, width - Theme.S(32)));
				nav.SetBounds((width - size.Width) / 2, Math.Max(height, scaledDimension - size.Height - Theme.S(16)), size.Width, size.Height);
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
				control.SetBounds(0, 0, width, height);
				LayoutWorkspaceHeader(control);
			}
			int remainingTop = ((nav == null) ? scaledDimension : (nav.Top - Theme.S(8)));
			if (pages != null)
			{
				DBPanel[] pagesSnapshot = pages;
				foreach (DBPanel dBPanel in pagesSnapshot)
				{
					dBPanel.SetBounds(0, height, width, Math.Max(1, remainingTop - height));
					if (dBPanel != pageOverview && dBPanel != pageLibrary && dBPanel != pageWhitelist && dBPanel != pagePolicy && dBPanel != pageAntiCheat && dBPanel != pageAbout && dBPanel != pageCoreScheduling && dBPanel != pageSettings && dBPanel != pageAudit)
					{
						dBPanel.AutoScroll = true;
					}
					DBPanel[] value;
					if (!pageTabPanels.TryGetValue(dBPanel, out value))
					{
						continue;
					}
					DBPanel[] valueSnapshot = value;
					foreach (DBPanel dBPanel2 in valueSnapshot)
					{
						if (!(dBPanel2 is PerformanceFeatureGrid))
						{
							dBPanel2.SetBounds(dBPanel2.Left, dBPanel2.Top, Math.Max(1, dBPanel.ClientSize.Width - dBPanel2.Left - Theme.S(20)), Math.Max(1, dBPanel.ClientSize.Height - dBPanel2.Top - Theme.S(8)));
						}
					}
				}
			}
			int width2 = Math.Min(Theme.S(230), Math.Max(1, width - Theme.S(160)));
			if (coreBackBar != null)
			{
				coreBackBar.SetBounds(Theme.S(10), 0, width2, Math.Min(height, Theme.S(60)));
			}
			if (overviewBackBar != null)
			{
				overviewBackBar.SetBounds(Theme.S(10), 0, width2, Math.Min(height, Theme.S(60)));
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
			int scaledDimension = Theme.S(4);
			int width = Theme.S(40);
			int height = Math.Min(bar.Height, Theme.S(60));
			Control control = bar.Controls["WindowClose"];
			Control control3 = bar.Controls["WindowMinimize"];
			if (control != null)
			{
				control.SetBounds(bar.Width - scaledDimension - width, 0, width, height);
			}
			if (control3 != null)
			{
				control3.SetBounds(bar.Width - scaledDimension - width * 2, 0, width, height);
			}
			bool matchesTopH = TopH > 60;
			int remainingWidth = (matchesTopH ? (bar.Width - Theme.S(24)) : (bar.Width - scaledDimension - width * 2 - Theme.S(14)));
			int rowY = Theme.S(matchesTopH ? 64 : 8);
			int width2 = Theme.S(94);
			int scaledDimension2 = Theme.S(12);
			int width3 = Math.Min(Theme.S(188), Math.Max(Theme.S(100), remainingWidth - width2 - scaledDimension2 - Theme.S(24)));
			if (modeButton != null)
			{
				modeButton.SetBounds(Math.Max(0, remainingWidth - width3), rowY, width3, Theme.S(44));
			}
			if (themeSwitch != null)
			{
				themeSwitch.SetBounds(Math.Max(0, remainingWidth - width3 - scaledDimension2 - width2), rowY, width2, Theme.S(44));
			}
			Control control4 = ((coreBackBar != null && coreBackBar.Visible) ? coreBackBar : ((overviewBackBar != null && overviewBackBar.Visible) ? overviewBackBar : null));
			int floor = (matchesTopH ? Theme.S(24) : ((control4 != null) ? (control4.Right + Theme.S(20)) : Theme.S(196)));
			int layoutHeaderLinksResult = LayoutHeaderLinks(themeSwitch.Left, rowY, scaledDimension2, floor);
			Control control5 = bar.Controls["WorkspaceBrand"];
			if (control5 != null)
			{
				int remainingS = (matchesTopH ? (bar.Width - scaledDimension - width * 2 - Theme.S(12)) : (layoutHeaderLinksResult - Theme.S(20)));
				control5.SetBounds(Theme.S(24), Theme.S(4), Math.Max(1, Math.Min(Theme.S(270), remainingS - Theme.S(24))), Theme.S(52));
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
			RogLinkButton[] rogLinkButtonItems = new RogLinkButton[2] { btnDonate, headerWebsite };
			int[] integerValues = new int[2]
			{
				HeaderLinkWidth(btnDonate),
				HeaderLinkWidth(headerWebsite)
			};
			int scaledDimension = Theme.S(44);
			for (int iterationIndex = rogLinkButtonItems.Length; iterationIndex >= 0; iterationIndex--)
			{
				for (int i = 0; i < 2; i++)
				{
					int left = right;
					for (int j = 0; j < iterationIndex; j++)
					{
						left -= gap + ((i == 0) ? integerValues[j] : scaledDimension);
					}
					if (iterationIndex > 0 && left < floor)
					{
						continue;
					}
					left = right;
					for (int k = 0; k < rogLinkButtonItems.Length; k++)
					{
						rogLinkButtonItems[k].Visible = k < iterationIndex;
						if (k < iterationIndex)
						{
							int width = ((i == 0) ? integerValues[k] : scaledDimension);
							left -= gap + width;
							rogLinkButtonItems[k].SetBounds(left, rowY, width, Theme.S(44));
						}
					}
					return left;
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
			int scaledDimension = Theme.S(42);
			int scaledDimensionOffset = scaledDimension + ((host != null) ? (host.Width + Theme.S(14)) : 0);
			int scaledDimension2 = Theme.S(20);
			int remainingScaledDimension2 = Theme.S(cardW) - scaledDimension - scaledDimensionOffset - valueReserve - scaledDimension2;
			if (remainingScaledDimension2 <= 0)
			{
				return minHeight;
			}
			Font font = DsType.Font(DsText.Description);
			int height = TextRenderer.MeasureText("Ag", font).Height;
			if (height <= 0)
			{
				return minHeight;
			}
			int height2 = TextRenderer.MeasureText(desc, font, new Size(remainingScaledDimension2, int.MaxValue), TextFormatFlags.WordBreak).Height;
			int heightRatio = (height2 + height - 1) / height;
			if (heightRatio < 1)
			{
				heightRatio = 1;
			}
			if (heightRatio > 3)
			{
				heightRatio = 3;
			}
			int scaledDimension3 = Theme.S(100);
			if (scaledDimension3 <= 0)
			{
				return minHeight;
			}
			int sOffset = heightRatio * height + Theme.S(17) + Theme.S(44);
			int scaledDimension3Ratio = (sOffset * 100 + scaledDimension3 - 1) / scaledDimension3;
			if (scaledDimension3Ratio <= minHeight)
			{
				return minHeight;
			}
			return scaledDimension3Ratio;
		}
		private int FullTextCardHeight(string desc, int width, Control host, int minimum)
		{
			int remainingS = Theme.S(width - 84 - 20) - ((host != null) ? (host.Width + Theme.S(14)) : 0);
			if (remainingS <= 0)
			{
				return minimum;
			}
			Font font = DsType.Font(DsText.Description);
			int heightLimit = Math.Max(1, TextRenderer.MeasureText("Ag", font).Height);
			int height = TextRenderer.MeasureText(desc ?? "", font, new Size(remainingS, int.MaxValue), TextFormatFlags.WordBreak).Height;
			height = (height + heightLimit - 1) / heightLimit * heightLimit;
			int heightOffset = height + Theme.S(64);
			int sLimit = Math.Max(1, Theme.S(100));
			return Math.Max(minimum, (heightOffset * 100 + sLimit - 1) / sLimit);
		}
		internal static float FitFontSize(string text, int widthPx, bool bold, float maxSize, float minSize)
		{
			if (string.IsNullOrEmpty(text) || widthPx <= 0)
			{
				return maxSize;
			}
			for (float iterationIndex = maxSize; iterationIndex > minSize; iterationIndex -= 0.5f)
			{
				if (TextRenderer.MeasureText(text, Theme.UI(iterationIndex, bold), new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width <= widthPx)
				{
					return iterationIndex;
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
