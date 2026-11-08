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
		private List<WhitelistRuleView> QueryWhitelistState(bool deep)
		{
			if (!deep)
			{
				return gameMode.GetWhitelistRulesFast();
			}
			return gameMode.GetWhitelistRules();
		}
		private void AppendDetectedPlatformHint()
		{
			try
			{
				List<string> list = GamePlatformCatalog.DetectedPlatforms();
				if (list.Count != 0)
				{
					for (int i = 0; i < list.Count; i++)
					{
						list[i] = PlatformDisplayName(list[i]);
					}
					if (whiteFilters != null)
					{
						TierPicker tierPicker = whiteFilters;
						tierPicker.AccessibleDescription = tierPicker.AccessibleDescription + "\r\n" + Lang.F("white.platform.detected", string.Join(" · ", list.ToArray()));
					}
				}
			}
			catch
			{
			}
		}
		private void WarnWhitelistOperation(string error)
		{
			if (!string.IsNullOrEmpty(error) && !base.IsDisposed && !base.Disposing)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", error);
			}
		}
		private void RunWhitelistOp(Func<string> op)
		{
			if (Interlocked.CompareExchange(ref whiteOpBusy, 1, 0) != 0)
			{
				return;
			}
			try
			{
				QueueUiStateWork(delegate
				{
					string error;
					try
					{
						error = op();
					}
					catch (Exception ex2)
					{
						error = ex2.Message;
					}
					try
					{
						PostUiStateResult(delegate
						{
							try
							{
								if (!base.IsDisposed && !base.Disposing)
								{
									WarnWhitelistOperation(error);
									RefreshWhitelist(true);
								}
							}
							finally
							{
								Interlocked.Exchange(ref whiteOpBusy, 0);
							}
						});
					}
					catch
					{
						Interlocked.Exchange(ref whiteOpBusy, 0);
					}
				});
			}
			catch (Exception ex)
			{
				Interlocked.Exchange(ref whiteOpBusy, 0);
				WarnWhitelistOperation(ex.Message);
			}
		}
		private void AddWhitelistFiles(IEnumerable<string> files)
		{
			List<string> list = new List<string>(files);
			RunWhitelistOp(delegate
			{
				int num = 0;
				string text = null;
				foreach (string item in list)
				{
					string text2 = ResolveWhitelistTarget(item);
					if (!string.IsNullOrEmpty(text2))
					{
						if (gameMode.AddWhitelistAuto(text2))
						{
							num++;
						}
						else if (text == null)
						{
							text = gameMode.WhitelistLastError;
						}
					}
				}
				return (num != 0) ? null : text;
			});
		}
		internal static string ResolveWhitelistTarget(string raw)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return null;
			}
			string text = raw.Trim().Trim('"');
			try
			{
				if (text.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
				{
					string text2 = GameExecutableResolver.ResolveShortcut(text);
					if (!string.IsNullOrEmpty(text2))
					{
						text = text2;
					}
				}
			}
			catch
			{
			}
			if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			return text;
		}
		private void BrowseForWhitelist()
		{
			using (OpenFileDialog openFileDialog = new OpenFileDialog())
			{
				openFileDialog.Filter = Lang.T("white.browse.filter");
				openFileDialog.Multiselect = true;
				if (openFileDialog.ShowDialog(this) == DialogResult.OK)
				{
					AddWhitelistFiles(openFileDialog.FileNames);
				}
			}
		}
		private void PickRunningForWhitelist()
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (WhitelistRuleView item in gameMode.GetWhitelistRulesFast())
			{
				if (item.PlatformId == null && item.Rule.Kind != 0)
				{
					hashSet.Add(item.Rule.Value);
				}
			}
			using (RunningPickerDialog runningPickerDialog = new RunningPickerDialog(hashSet))
			{
				if (ShowDim(runningPickerDialog) == DialogResult.OK && runningPickerDialog.Selected.Count != 0)
				{
					AddWhitelistFiles(runningPickerDialog.Selected);
				}
			}
		}
		private bool CanRemoveSelectedWhitelist()
		{
			WhitelistItem whitelistItem = ((lstWhite == null) ? null : (lstWhite.SelectedItem as WhitelistItem));
			if (whitelistItem != null && !whitelistItem.Automatic && whitelistItem.View != null)
			{
				return !whitelistItem.View.Required;
			}
			return false;
		}
		private void RemoveSelectedWhitelist()
		{
			WhitelistItem whitelistItem = lstWhite.SelectedItem as WhitelistItem;
			if (whitelistItem == null || whitelistItem.View == null)
			{
				return;
			}
			if (whitelistItem.View.Required)
			{
				PaviseDialog.Info(this, "PAVISE AUTO", Lang.T("white.required.locked"));
				return;
			}
			string key = whitelistItem.View.Key;
			RunWhitelistOp(() => (!gameMode.RemoveWhitelistRule(key)) ? gameMode.WhitelistLastError : null);
		}
		private void FinishWhitelistRefresh()
		{
			Interlocked.Exchange(ref whiteBusy, 0);
			if (Interlocked.Exchange(ref whitePending, 0) != 0 && !base.IsDisposed && UiActive)
			{
				RefreshWhitelist(true);
			}
		}
		private void FillWhitelist(List<WhitelistRuleView> views)
		{
			if (lstWhite == null || lstWhite.IsDisposed || whitePanel == null)
			{
				return;
			}
			whiteViews = ((views == null) ? new List<WhitelistRuleView>() : new List<WhitelistRuleView>(views));
			List<WhitelistItem> list = new List<WhitelistItem>();
			List<WhitelistItem> list2 = new List<WhitelistItem>();
			foreach (WhitelistRuleView whiteView in whiteViews)
			{
				WhitelistItem item = Decorate(new WhitelistItem(whiteView));
				if (whiteView.PlatformId != null)
				{
					list2.Add(item);
				}
				else if (whiteView.Required)
				{
					continue;
				}
				else
				{
					list.Add(item);
				}
			}
			list.AddRange(list2);
			list.AddRange(AutomaticExemptionItems());
			whiteRows = list;
			FilterWhitelistRows();
		}
		private void FilterWhitelistRows()
		{
			if (lstWhite == null || lstWhite.IsDisposed || whitePanel == null)
			{
				return;
			}
			WhitelistItem item = lstWhite.SelectedItem as WhitelistItem;
			string text = WhitelistSelectionKey(item);
			int topIndex = lstWhite.TopIndex;
			List<WhitelistItem> list = whiteRows;
			string text2 = (whiteSearchText ?? "").Trim();
			lstWhite.BeginUpdate();
			try
			{
				lstWhite.Items.Clear();
				foreach (WhitelistItem item2 in list)
				{
					int num = ((item2.View != null && item2.View.PlatformId != null) ? 2 : ((!item2.Automatic && (item2.View == null || !item2.View.Required)) ? 1 : 3));
					if ((whiteFilterIndex == 0 || whiteFilterIndex == num) && (text2.Length <= 0 || WhitelistContains(item2.Title, text2) || WhitelistContains(item2.Subtitle, text2) || WhitelistContains(item2.Details, text2) || (item2.View != null && WhitelistContains(item2.View.Rule.Value, text2))))
					{
						int selectedIndex = lstWhite.Items.Add(item2);
						if (text != null && string.Equals(WhitelistSelectionKey(item2), text, StringComparison.OrdinalIgnoreCase))
						{
							lstWhite.SelectedIndex = selectedIndex;
						}
					}
				}
				if (lstWhite.Items.Count > 0)
				{
					lstWhite.TopIndex = Math.Min(topIndex, lstWhite.Items.Count - 1);
				}
			}
			finally
			{
				lstWhite.EndUpdate();
			}
			if (btnRestorePlatformProtection != null)
			{
				btnRestorePlatformProtection.Enabled = PlatformRuntimePolicy.Mask != 0;
			}
			whitePanel.ShowEmpty = lstWhite.Items.Count == 0;
			lstWhite.Visible = !whitePanel.ShowEmpty;
			whitePanel.Invalidate();
			if (whiteCount != null)
			{
				whiteCount.Text = ((lstWhite.Items.Count == list.Count) ? AutoText(list.Count + " 项", list.Count + " entries") : AutoText(lstWhite.Items.Count + " / " + list.Count + " 项", lstWhite.Items.Count + " / " + list.Count + " entries"));
			}
			SyncWhitelistRemoveAction();
		}
		private static bool WhitelistContains(string source, string query)
		{
			if (!string.IsNullOrEmpty(source))
			{
				return source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
			}
			return false;
		}
		private static string WhitelistSelectionKey(WhitelistItem item)
		{
			if (item != null)
			{
				if (item.View == null)
				{
					if (!item.Automatic)
					{
						return null;
					}
					return "auto:" + item.Title;
				}
				return item.View.Key;
			}
			return null;
		}
		private void DrawWhitelistItem(object sender, DrawItemEventArgs e)
		{
			if (e.Index < 0 || e.Index >= lstWhite.Items.Count)
			{
				return;
			}
			WhitelistItem whitelistItem = lstWhite.Items[e.Index] as WhitelistItem;
			if (whitelistItem == null || (whitelistItem.View == null && !whitelistItem.Automatic))
			{
				return;
			}
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle bounds = e.Bounds;
			bool flag = (e.State & DrawItemState.Selected) != 0;
			bool flag2 = !flag && e.Index == Theme.HoverIndex(lstWhite);
			bool flag3 = !whitelistItem.Automatic && whitelistItem.View.Required;
			bool flag4 = !whitelistItem.Automatic && whitelistItem.View.CurrentMatches > 0;
			using (SolidBrush brush = new SolidBrush(Theme.Card))
			{
				graphics.FillRectangle(brush, bounds);
			}
			Rectangle r = new Rectangle(bounds.Left + Theme.S(2), bounds.Top + Theme.S(2), Math.Max(1, bounds.Width - Theme.S(4)), Math.Max(1, bounds.Height - Theme.S(4)));
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(6)))
			{
				if (flag || flag2)
				{
					using (SolidBrush brush2 = new SolidBrush(flag ? Theme.Sel : Theme.CardHover))
					{
						graphics.FillPath(brush2, path);
					}
					using (Pen pen = new Pen(flag ? Col.Alpha(Theme.Accent, 140) : Theme.StrokeHi))
					{
						graphics.DrawPath(pen, path);
					}
				}
			}
			if (flag)
			{
				using (Pen pen2 = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
				{
					graphics.DrawLine(pen2, r.Left, r.Top + Theme.S(8), r.Left, r.Bottom - Theme.S(8));
				}
			}
			int num = Theme.S(28);
			Rectangle rectangle = new Rectangle(bounds.Left + Theme.S(13), bounds.Top + (bounds.Height - num) / 2, num, num);
			if (whitelistItem.Automatic)
			{
				DrawWhitelistAutomaticIcon(graphics, rectangle, whitelistItem.Glyph);
			}
			else
			{
				Bitmap bitmap = WhitelistIcon(whitelistItem.View.Rule);
				if (bitmap == null)
				{
					DrawWhitelistDefaultIcon(graphics, rectangle, flag3, flag4);
				}
				else
				{
					graphics.DrawImage(bitmap, rectangle);
				}
			}
			int num2 = Dpi.U(bounds.Width);
			bool flag5 = num2 >= 420;
			bool flag6 = num2 >= 640;
			int num3 = (flag5 ? Theme.S(82) : 0);
			int num4 = (flag6 ? Theme.S(100) : 0);
			int num5 = rectangle.Right + Theme.S(12);
			int num6 = Math.Max(Theme.S(30), bounds.Right - num5 - num3 - num4 - Theme.S(22));
			TextRenderer.DrawText(graphics, whitelistItem.Title, Theme.UI(9.3f, true), new Rectangle(num5, bounds.Top + Theme.S(6), num6, Theme.S(22)), flag3 ? Theme.Dim : Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, whitelistItem.Subtitle, Theme.UI(7.7f, false), new Rectangle(num5, bounds.Top + Theme.S(29), num6, Theme.S(18)), Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			if (flag5)
			{
				Rectangle rectangle2 = new Rectangle(bounds.Right - num4 - num3 - Theme.S(11), bounds.Top + (bounds.Height - Theme.S(23)) / 2, num3 - Theme.S(10), Theme.S(23));
				Color color = ((flag3 || whitelistItem.Automatic || whitelistItem.View.PlatformId != null) ? Theme.Dim : Theme.Accent);
				using (GraphicsPath path2 = Theme.TechPath(rectangle2, Theme.S(4)))
				{
					using (SolidBrush brush3 = new SolidBrush(Col.Alpha(color, 15)))
					{
						graphics.FillPath(brush3, path2);
					}
					using (Pen pen3 = new Pen(Col.Alpha(color, 80)))
					{
						graphics.DrawPath(pen3, path2);
					}
				}
				TextRenderer.DrawText(graphics, whitelistItem.Badge, Theme.UI(7.3f, false), rectangle2, color, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
			}
			if (flag6 && !flag3 && !whitelistItem.Automatic)
			{
				Rectangle rectangle3 = new Rectangle(bounds.Right - num4 - Theme.S(10), bounds.Top, num4, bounds.Height);
				int num7 = Theme.S(5);
				Color color2 = (flag4 ? Theme.Green : Theme.Faint);
				using (SolidBrush brush4 = new SolidBrush(color2))
				{
					graphics.FillEllipse(brush4, rectangle3.Left, rectangle3.Top + (rectangle3.Height - num7) / 2, num7, num7);
				}
				TextRenderer.DrawText(graphics, whitelistItem.StateText, Theme.UI(7.6f, false), new Rectangle(rectangle3.Left + Theme.S(11), rectangle3.Top, rectangle3.Width - Theme.S(11), rectangle3.Height), color2, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			if (!flag && !flag2)
			{
				using (Pen pen4 = new Pen(Col.Alpha(Theme.Stroke, 100)))
				{
					graphics.DrawLine(pen4, bounds.Left + Theme.S(13), bounds.Bottom - 1, bounds.Right - Theme.S(12), bounds.Bottom - 1);
				}
			}
			if (flag && lstWhite.Focused && (e.State & DrawItemState.Focus) != 0)
			{
				e.DrawFocusRectangle();
			}
		}
		private static void DrawWhitelistAutomaticIcon(Graphics g, Rectangle box, string glyph)
		{
			Rectangle r = box;
			r.Width--;
			r.Height--;
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(5)))
			{
				using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Accent, 18)))
				{
					g.FillPath(brush, path);
				}
				using (Pen pen = new Pen(Col.Alpha(Theme.Accent, 108)))
				{
					g.DrawPath(pen, path);
				}
			}
			int num = Theme.S(19);
			Glyphs.Draw(g, string.IsNullOrEmpty(glyph) ? "shield" : glyph, new Rectangle(box.Left + (box.Width - num) / 2, box.Top + (box.Height - num) / 2, num, num), Col.Alpha(Theme.Accent, 210));
		}
		private static void DrawWhitelistDefaultIcon(Graphics g, Rectangle box, bool required, bool live)
		{
			Color c = (required ? Theme.Faint : (live ? Theme.Accent : Theme.Dim));
			Rectangle r = box;
			r.Width--;
			r.Height--;
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(5)))
			{
				using (SolidBrush brush = new SolidBrush(Col.Alpha(c, required ? 12 : 20)))
				{
					g.FillPath(brush, path);
				}
				using (Pen pen = new Pen(Col.Alpha(c, required ? 76 : 118)))
				{
					g.DrawPath(pen, path);
				}
			}
			int num = Theme.S(19);
			Rectangle b = new Rectangle(box.Left + (box.Width - num) / 2, box.Top + (box.Height - num) / 2, num, num);
			Glyphs.Draw(g, "shield", b, Col.Alpha(c, required ? 145 : 205));
			int num2 = Math.Max(2, Theme.S(2));
			int num3 = box.Top + box.Height / 2;
			int num4 = box.Left + box.Width / 2;
			using (Pen pen2 = new Pen(Col.Alpha(c, required ? 112 : 172), Math.Max(1f, Theme.S(1))))
			{
				g.DrawLine(pen2, num4 - Theme.S(4), num3, num4 + Theme.S(4), num3);
			}
			using (SolidBrush brush2 = new SolidBrush(Col.Alpha(c, required ? 160 : 225)))
			{
				g.FillEllipse(brush2, num4 - Theme.S(5) - num2 / 2, num3 - num2 / 2, num2, num2);
				g.FillEllipse(brush2, num4 - num2 / 2, num3 - num2 / 2, num2, num2);
				g.FillEllipse(brush2, num4 + Theme.S(5) - num2 / 2, num3 - num2 / 2, num2, num2);
			}
		}
		private static void DrawWhitelistGroupHeader(Control owner, DrawItemEventArgs e, string text)
		{
			Graphics graphics = e.Graphics;
			Rectangle bounds = e.Bounds;
			using (SolidBrush brush = new SolidBrush(Backdrop.CardFill(owner, Theme.Card)))
			{
				graphics.FillRectangle(brush, bounds);
			}
			Size size = TextRenderer.MeasureText(graphics, text, Theme.UI(7.8f, false));
			int num = bounds.Left + Theme.S(14);
			int num2 = bounds.Top + bounds.Height / 2;
			TextRenderer.DrawText(graphics, text, Theme.UI(7.8f, false), new Rectangle(num, bounds.Top, size.Width + Theme.S(8), bounds.Height), Theme.Faint, TextFormatFlags.VerticalCenter);
			using (Pen pen = new Pen(Col.Alpha(Theme.Stroke, 150)))
			{
				graphics.DrawLine(pen, num + size.Width + Theme.S(12), num2, bounds.Right - Theme.S(14), num2);
			}
		}
		private Bitmap WhitelistIcon(WhitelistRule rule)
		{
			if (rule.Kind == WhitelistRuleKind.LegacyName)
			{
				return null;
			}
			string value = rule.Value;
			Bitmap value2;
			if (whiteIconCache.TryGetValue(value, out value2))
			{
				return value2;
			}
			try
			{
				using (Icon icon = Icon.ExtractAssociatedIcon(value))
				{
					value2 = icon.ToBitmap();
				}
			}
			catch
			{
				value2 = null;
			}
			whiteIconCache[value] = value2;
			return value2;
		}
		private string WhitelistTitle(WhitelistRule rule)
		{
			if (rule.Kind == WhitelistRuleKind.LegacyName)
			{
				return rule.Value;
			}
			string value = rule.Value;
			string value2;
			if (whiteNameCache.TryGetValue(value, out value2))
			{
				return value2;
			}
			string text = null;
			try
			{
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(value);
				text = (string.IsNullOrEmpty(versionInfo.FileDescription) ? versionInfo.ProductName : versionInfo.FileDescription);
				if (!string.IsNullOrEmpty(text))
				{
					text = text.Trim();
				}
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(text))
			{
				try
				{
					text = Path.GetFileNameWithoutExtension(value);
				}
				catch
				{
					text = value;
				}
			}
			whiteNameCache[value] = text;
			return text;
		}
		private static string PlatformDisplayName(string id)
		{
			if (string.IsNullOrEmpty(id) || !id.StartsWith("t.", StringComparison.Ordinal))
			{
				return id;
			}
			return Lang.T(id);
		}
		private static List<WhitelistItem> AutomaticExemptionItems()
		{
			List<WhitelistItem> list = new List<WhitelistItem>();
			string systemNames = string.Join(" · ", SystemProcessCatalog.PresetWhitelist);
			list.Add(AutomaticExemption("white.auto.system", Lang.F("white.auto.system.list", systemNames), "shield", DetailSection("white.auto.details.processes", systemNames) + "\r\n\r\n" + Lang.T("white.auto.details.system.note")));
			string[] value = NetAcceleratorCatalog.ProcessNamesForDisplay();
			string[] value2 = NetAcceleratorCatalog.TokensForDisplay();
			list.Add(AutomaticExemption("white.auto.accel", Lang.F("white.auto.accel.list", string.Join(" · ", value)), "chart", DetailSection("white.auto.details.processes", string.Join(" · ", value)) + "\r\n\r\n" + DetailSection("white.auto.details.namekeywords", string.Join(" · ", value2))));
			string[] value3 = HardwareControlCatalog.ProcessNamesForDisplay();
			string[] value4 = HardwareControlCatalog.TokensForDisplay();
			list.Add(AutomaticExemption("white.auto.hardware", Lang.F("white.auto.hardware.list", string.Join(" · ", value3)), "chip", DetailSection("white.auto.details.processes", string.Join(" · ", value3)) + "\r\n\r\n" + DetailSection("white.auto.details.tokens", string.Join(" · ", value4)) + "\r\n\r\n" + Lang.T("white.auto.details.hardware.note")));
			List<string> list2 = new List<string>();
			List<string> list3 = new List<string>();
			AcGroup[] groups = AntiCheatCatalog.Groups;
			foreach (AcGroup acGroup in groups)
			{
				list3.Add(acGroup.Name);
				list2.Add(acGroup.Name + "\r\n" + string.Join(" · ", acGroup.Procs));
			}
			AcProtectionGroup[] protectionOnlyGroups = AntiCheatCatalog.ProtectionOnlyGroups;
			foreach (AcProtectionGroup acProtectionGroup in protectionOnlyGroups)
			{
				list3.Add(acProtectionGroup.Name);
				list2.Add(acProtectionGroup.Name + " · " + Lang.T("ac.protectiononly") + "\r\n" + string.Join(" · ", acProtectionGroup.PatternsForDisplay()));
			}
			list.Add(AutomaticExemption("white.auto.anticheat", Lang.F("white.auto.anticheat.list", string.Join(" · ", list3.ToArray())), "acshield", Lang.T("white.auto.details.groups") + "\r\n\r\n" + string.Join("\r\n\r\n", list2.ToArray()) + "\r\n\r\n" + DetailSection("white.auto.details.tokens", string.Join(" · ", AntiCheatCatalog.NameTokensForDisplay())) + "\r\n\r\n" + DetailSection("white.auto.details.anticheatdirs", string.Join(" · ", AntiCheatCatalog.DirectoryNamesForDisplay()))));
			string[] value5 = PeripheralCatalog.NameKeywordsForDisplay();
			string[] value6 = PeripheralCatalog.DescriptionWordsForDisplay();
			list.Add(AutomaticExemption("white.auto.peripheral", Lang.F("white.auto.peripheral.list", string.Join(" · ", value5)), "settings", DetailSection("white.auto.details.namekeywords", string.Join(" · ", value5)) + "\r\n\r\n" + DetailSection("white.auto.details.descriptionwords", string.Join(" · ", value6)) + "\r\n\r\n" + DetailSection("white.auto.details.vendors", string.Join(" · ", PeripheralCatalog.PresentVendorTokensForDisplay())) + "\r\n\r\n" + Lang.T("white.auto.details.peripheral.note")));
			string streamingProducts = string.Join(" · ", StreamingToolCatalog.ProductsForDisplay());
			list.Add(AutomaticExemption("white.auto.streaming", Lang.F("white.auto.streaming.list", streamingProducts), "chart", DetailSection("white.auto.details.products", streamingProducts) + "\r\n\r\n" + DetailSection("white.auto.details.processes", string.Join(" · ", StreamingToolCatalog.ProcessNamesForDisplay())) + "\r\n\r\n" + Lang.T("white.auto.details.streaming.note")));
			return list;
		}
		private static WhitelistItem AutomaticExemption(string titleKey, string subtitle, string glyph, string details)
		{
			WhitelistItem whitelistItem = new WhitelistItem(null);
			whitelistItem.Automatic = true;
			whitelistItem.Title = Lang.T(titleKey);
			whitelistItem.Subtitle = subtitle;
			whitelistItem.Badge = Lang.T("white.auto.badge");
			whitelistItem.StateText = Lang.T("white.auto.state");
			whitelistItem.Glyph = glyph;
			whitelistItem.Details = details;
			return whitelistItem;
		}
	}
}
