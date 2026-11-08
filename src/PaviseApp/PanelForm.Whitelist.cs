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
				int processedCount = 0;
				string whitelistLastError = null;
				foreach (string item in list)
				{
					string whitelistTarget = ResolveWhitelistTarget(item);
					if (!string.IsNullOrEmpty(whitelistTarget))
					{
						if (gameMode.AddWhitelistAuto(whitelistTarget))
						{
							processedCount++;
						}
						else if (whitelistLastError == null)
						{
							whitelistLastError = gameMode.WhitelistLastError;
						}
					}
				}
				return (processedCount != 0) ? null : whitelistLastError;
			});
		}
		internal static string ResolveWhitelistTarget(string raw)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return null;
			}
			string trimTrimmed = raw.Trim().Trim('"');
			try
			{
				if (trimTrimmed.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
				{
					string shortcut = GameExecutableResolver.ResolveShortcut(trimTrimmed);
					if (!string.IsNullOrEmpty(shortcut))
					{
						trimTrimmed = shortcut;
					}
				}
			}
			catch
			{
			}
			if (!trimTrimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			return trimTrimmed;
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
			string whitelistSelectionKeyResult = WhitelistSelectionKey(item);
			int topIndex = lstWhite.TopIndex;
			List<WhitelistItem> list = whiteRows;
			string trimmed = (whiteSearchText ?? "").Trim();
			lstWhite.BeginUpdate();
			try
			{
				lstWhite.Items.Clear();
				foreach (WhitelistItem item2 in list)
				{
					int filterWhitelistRowsValue = ((item2.View != null && item2.View.PlatformId != null) ? 2 : ((!item2.Automatic && (item2.View == null || !item2.View.Required)) ? 1 : 3));
					if ((whiteFilterIndex == 0 || whiteFilterIndex == filterWhitelistRowsValue) && (trimmed.Length <= 0 || WhitelistContains(item2.Title, trimmed) || WhitelistContains(item2.Subtitle, trimmed) || WhitelistContains(item2.Details, trimmed) || (item2.View != null && WhitelistContains(item2.View.Rule.Value, trimmed))))
					{
						int selectedIndex = lstWhite.Items.Add(item2);
						if (whitelistSelectionKeyResult != null && string.Equals(WhitelistSelectionKey(item2), whitelistSelectionKeyResult, StringComparison.OrdinalIgnoreCase))
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
			bool valuesMatch = (e.State & DrawItemState.Selected) != 0;
			bool allConditionsMet = !valuesMatch && e.Index == Theme.HoverIndex(lstWhite);
			bool matchesRequired = !whitelistItem.Automatic && whitelistItem.View.Required;
			bool allConditionsMet2 = !whitelistItem.Automatic && whitelistItem.View.CurrentMatches > 0;
			using (SolidBrush brush = new SolidBrush(Theme.Card))
			{
				graphics.FillRectangle(brush, bounds);
			}
			Rectangle r = new Rectangle(bounds.Left + Theme.S(2), bounds.Top + Theme.S(2), Math.Max(1, bounds.Width - Theme.S(4)), Math.Max(1, bounds.Height - Theme.S(4)));
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(6)))
			{
				if (valuesMatch || allConditionsMet)
				{
					using (SolidBrush brush2 = new SolidBrush(valuesMatch ? Theme.Sel : Theme.CardHover))
					{
						graphics.FillPath(brush2, path);
					}
					using (Pen pen = new Pen(valuesMatch ? Col.Alpha(Theme.Accent, 140) : Theme.StrokeHi))
					{
						graphics.DrawPath(pen, path);
					}
				}
			}
			if (valuesMatch)
			{
				using (Pen pen2 = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
				{
					graphics.DrawLine(pen2, r.Left, r.Top + Theme.S(8), r.Left, r.Bottom - Theme.S(8));
				}
			}
			int scaledDimension = Theme.S(28);
			Rectangle rectangle = new Rectangle(bounds.Left + Theme.S(13), bounds.Top + (bounds.Height - scaledDimension) / 2, scaledDimension, scaledDimension);
			if (whitelistItem.Automatic)
			{
				DrawWhitelistAutomaticIcon(graphics, rectangle, whitelistItem.Glyph);
			}
			else
			{
				Bitmap bitmap = WhitelistIcon(whitelistItem.View.Rule);
				if (bitmap == null)
				{
					DrawWhitelistDefaultIcon(graphics, rectangle, matchesRequired, allConditionsMet2);
				}
				else
				{
					graphics.DrawImage(bitmap, rectangle);
				}
			}
			int uResult = Dpi.U(bounds.Width);
			bool matchesUResult = uResult >= 420;
			bool matchesUResult2 = uResult >= 640;
			int scaledDimension2 = (matchesUResult ? Theme.S(82) : 0);
			int scaledDimension3 = (matchesUResult2 ? Theme.S(100) : 0);
			int rightOffset = rectangle.Right + Theme.S(12);
			int sLimit = Math.Max(Theme.S(30), bounds.Right - rightOffset - scaledDimension2 - scaledDimension3 - Theme.S(22));
			TextRenderer.DrawText(graphics, whitelistItem.Title, Theme.UI(9.3f, true), new Rectangle(rightOffset, bounds.Top + Theme.S(6), sLimit, Theme.S(22)), matchesRequired ? Theme.Dim : Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, whitelistItem.Subtitle, Theme.UI(7.7f, false), new Rectangle(rightOffset, bounds.Top + Theme.S(29), sLimit, Theme.S(18)), Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			if (matchesUResult)
			{
				Rectangle rectangle2 = new Rectangle(bounds.Right - scaledDimension3 - scaledDimension2 - Theme.S(11), bounds.Top + (bounds.Height - Theme.S(23)) / 2, scaledDimension2 - Theme.S(10), Theme.S(23));
				Color color = ((matchesRequired || whitelistItem.Automatic || whitelistItem.View.PlatformId != null) ? Theme.Dim : Theme.Accent);
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
			if (matchesUResult2 && !matchesRequired && !whitelistItem.Automatic)
			{
				Rectangle rectangle3 = new Rectangle(bounds.Right - scaledDimension3 - Theme.S(10), bounds.Top, scaledDimension3, bounds.Height);
				int width = Theme.S(5);
				Color color2 = (allConditionsMet2 ? Theme.Green : Theme.Faint);
				using (SolidBrush brush4 = new SolidBrush(color2))
				{
					graphics.FillEllipse(brush4, rectangle3.Left, rectangle3.Top + (rectangle3.Height - width) / 2, width, width);
				}
				TextRenderer.DrawText(graphics, whitelistItem.StateText, Theme.UI(7.6f, false), new Rectangle(rectangle3.Left + Theme.S(11), rectangle3.Top, rectangle3.Width - Theme.S(11), rectangle3.Height), color2, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			if (!valuesMatch && !allConditionsMet)
			{
				using (Pen pen4 = new Pen(Col.Alpha(Theme.Stroke, 100)))
				{
					graphics.DrawLine(pen4, bounds.Left + Theme.S(13), bounds.Bottom - 1, bounds.Right - Theme.S(12), bounds.Bottom - 1);
				}
			}
			if (valuesMatch && lstWhite.Focused && (e.State & DrawItemState.Focus) != 0)
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
			int scaledDimension = Theme.S(19);
			Glyphs.Draw(g, string.IsNullOrEmpty(glyph) ? "shield" : glyph, new Rectangle(box.Left + (box.Width - scaledDimension) / 2, box.Top + (box.Height - scaledDimension) / 2, scaledDimension, scaledDimension), Col.Alpha(Theme.Accent, 210));
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
			int scaledDimension = Theme.S(19);
			Rectangle b = new Rectangle(box.Left + (box.Width - scaledDimension) / 2, box.Top + (box.Height - scaledDimension) / 2, scaledDimension, scaledDimension);
			Glyphs.Draw(g, "shield", b, Col.Alpha(c, required ? 145 : 205));
			int width = Math.Max(2, Theme.S(2));
			int top = box.Top + box.Height / 2;
			int leftOffset = box.Left + box.Width / 2;
			using (Pen pen2 = new Pen(Col.Alpha(c, required ? 112 : 172), Math.Max(1f, Theme.S(1))))
			{
				g.DrawLine(pen2, leftOffset - Theme.S(4), top, leftOffset + Theme.S(4), top);
			}
			using (SolidBrush brush2 = new SolidBrush(Col.Alpha(c, required ? 160 : 225)))
			{
				g.FillEllipse(brush2, leftOffset - Theme.S(5) - width / 2, top - width / 2, width, width);
				g.FillEllipse(brush2, leftOffset - width / 2, top - width / 2, width, width);
				g.FillEllipse(brush2, leftOffset + Theme.S(5) - width / 2, top - width / 2, width, width);
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
			int leftOffset = bounds.Left + Theme.S(14);
			int top = bounds.Top + bounds.Height / 2;
			TextRenderer.DrawText(graphics, text, Theme.UI(7.8f, false), new Rectangle(leftOffset, bounds.Top, size.Width + Theme.S(8), bounds.Height), Theme.Faint, TextFormatFlags.VerticalCenter);
			using (Pen pen = new Pen(Col.Alpha(Theme.Stroke, 150)))
			{
				graphics.DrawLine(pen, leftOffset + size.Width + Theme.S(12), top, bounds.Right - Theme.S(14), top);
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
			string productName = null;
			try
			{
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(value);
				productName = (string.IsNullOrEmpty(versionInfo.FileDescription) ? versionInfo.ProductName : versionInfo.FileDescription);
				if (!string.IsNullOrEmpty(productName))
				{
					productName = productName.Trim();
				}
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(productName))
			{
				try
				{
					productName = Path.GetFileNameWithoutExtension(value);
				}
				catch
				{
					productName = value;
				}
			}
			whiteNameCache[value] = productName;
			return productName;
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
