using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

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
                List<string> texts = GamePlatformCatalog.DetectedPlatforms();
                if (texts.Count != 0)
                {
                    for (int i = 0; i < texts.Count; i++)
                    {
                        texts[i] = PlatformDisplayName(texts[i]);
                    }
                    if (whiteFilters != null)
                    {
                        TierPicker tierPicker = whiteFilters;
                        tierPicker.AccessibleDescription = tierPicker.AccessibleDescription + "\r\n" + Lang.F("white.platform.detected", string.Join(" · ", texts.ToArray()));
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
            List<string> filesSnapshot = new List<string>(files);
            RunWhitelistOp(delegate
            {
                int processedCount = 0;
                string whitelistLastError = null;
                foreach (string currentFilesSnapshot in filesSnapshot)
                {
                    string whitelistTarget = ResolveWhitelistTarget(currentFilesSnapshot);
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
            HashSet<string> ruleValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (WhitelistRuleView item in gameMode.GetWhitelistRulesFast())
            {
                if (item.PlatformId == null && item.Rule.Kind != 0)
                {
                    ruleValues.Add(item.Rule.Value);
                }
            }
            using (RunningPickerDialog runningPickerDialog = new RunningPickerDialog(ruleValues))
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
            List<WhitelistItem> whitelistItems = new List<WhitelistItem>();
            List<WhitelistItem> currentWhitelistItems = new List<WhitelistItem>();
            foreach (WhitelistRuleView whiteView in whiteViews)
            {
                WhitelistItem decorate = Decorate(new WhitelistItem(whiteView));
                if (whiteView.PlatformId != null)
                {
                    currentWhitelistItems.Add(decorate);
                }
                else if (whiteView.Required)
                {
                    continue;
                }
                else
                {
                    whitelistItems.Add(decorate);
                }
            }
            whitelistItems.AddRange(currentWhitelistItems);
            whitelistItems.AddRange(AutomaticExemptionItems());
            whiteRows = whitelistItems;
            FilterWhitelistRows();
        }
        private void FilterWhitelistRows()
        {
            if (lstWhite == null || lstWhite.IsDisposed || whitePanel == null)
            {
                return;
            }
            WhitelistItem whitelistItem = lstWhite.SelectedItem as WhitelistItem;
            string whitelistSelectionKeyResult = WhitelistSelectionKey(whitelistItem);
            int topIndex = lstWhite.TopIndex;
            List<WhitelistItem> whitelistItems = whiteRows;
            string trimmed = (whiteSearchText ?? "").Trim();
            lstWhite.BeginUpdate();
            try
            {
                lstWhite.Items.Clear();
                foreach (WhitelistItem currentWhitelistItem in whitelistItems)
                {
                    int rowCategory = ((currentWhitelistItem.View != null && currentWhitelistItem.View.PlatformId != null) ? 2 : ((!currentWhitelistItem.Automatic && (currentWhitelistItem.View == null || !currentWhitelistItem.View.Required)) ? 1 : 3));
                    if ((whiteFilterIndex == 0 || whiteFilterIndex == rowCategory) && (trimmed.Length <= 0 || WhitelistContains(currentWhitelistItem.Title, trimmed) || WhitelistContains(currentWhitelistItem.Subtitle, trimmed) || WhitelistContains(currentWhitelistItem.Details, trimmed) || (currentWhitelistItem.View != null && WhitelistContains(currentWhitelistItem.View.Rule.Value, trimmed))))
                    {
                        int selectedIndex = lstWhite.Items.Add(currentWhitelistItem);
                        if (whitelistSelectionKeyResult != null && string.Equals(WhitelistSelectionKey(currentWhitelistItem), whitelistSelectionKeyResult, StringComparison.OrdinalIgnoreCase))
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
                whiteCount.Text = ((lstWhite.Items.Count == whitelistItems.Count) ? AutoText(whitelistItems.Count + " 项", whitelistItems.Count + " entries") : AutoText(lstWhite.Items.Count + " / " + whitelistItems.Count + " 项", lstWhite.Items.Count + " / " + whitelistItems.Count + " entries"));
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
            bool hovered = !valuesMatch && e.Index == Theme.HoverIndex(lstWhite);
            bool requiredRule = !whitelistItem.Automatic && whitelistItem.View.Required;
            bool hasMatches = !whitelistItem.Automatic && whitelistItem.View.CurrentMatches > 0;
            using (SolidBrush brush = new SolidBrush(Theme.Card))
            {
                graphics.FillRectangle(brush, bounds);
            }
            Rectangle r = new Rectangle(bounds.Left + Theme.S(2), bounds.Top + Theme.S(2), Math.Max(1, bounds.Width - Theme.S(4)), Math.Max(1, bounds.Height - Theme.S(4)));
            using (GraphicsPath path = Theme.TechPath(r, Theme.S(6)))
            {
                if (valuesMatch || hovered)
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
                using (Pen accentPen = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
                {
                    graphics.DrawLine(accentPen, r.Left, r.Top + Theme.S(8), r.Left, r.Bottom - Theme.S(8));
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
                    DrawWhitelistDefaultIcon(graphics, rectangle, requiredRule, hasMatches);
                }
                else
                {
                    graphics.DrawImage(bitmap, rectangle);
                }
            }
            int uResult = Dpi.U(bounds.Width);
            bool showStatus = uResult >= 420;
            bool showMatchCount = uResult >= 640;
            int scaledDimension2 = (showStatus ? Theme.S(82) : 0);
            int scaledDimension3 = (showMatchCount ? Theme.S(100) : 0);
            int rightOffset = rectangle.Right + Theme.S(12);
            int sLimit = Math.Max(Theme.S(30), bounds.Right - rightOffset - scaledDimension2 - scaledDimension3 - Theme.S(22));
            TextRenderer.DrawText(graphics, whitelistItem.Title, Theme.UI(9.3f, true), new Rectangle(rightOffset, bounds.Top + Theme.S(6), sLimit, Theme.S(22)), requiredRule ? Theme.Dim : Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(graphics, whitelistItem.Subtitle, Theme.UI(7.7f, false), new Rectangle(rightOffset, bounds.Top + Theme.S(29), sLimit, Theme.S(18)), Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (showStatus)
            {
                Rectangle currentRectangle = new Rectangle(bounds.Right - scaledDimension3 - scaledDimension2 - Theme.S(11), bounds.Top + (bounds.Height - Theme.S(23)) / 2, scaledDimension2 - Theme.S(10), Theme.S(23));
                Color color = ((requiredRule || whitelistItem.Automatic || whitelistItem.View.PlatformId != null) ? Theme.Dim : Theme.Accent);
                using (GraphicsPath path2 = Theme.TechPath(currentRectangle, Theme.S(4)))
                {
                    using (SolidBrush brush3 = new SolidBrush(Col.Alpha(color, 15)))
                    {
                        graphics.FillPath(brush3, path2);
                    }
                    using (Pen currentPen = new Pen(Col.Alpha(color, 80)))
                    {
                        graphics.DrawPath(currentPen, path2);
                    }
                }
                TextRenderer.DrawText(graphics, whitelistItem.Badge, Theme.UI(7.3f, false), currentRectangle, color, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            }
            if (showMatchCount && !requiredRule && !whitelistItem.Automatic)
            {
                Rectangle candidateRectangle = new Rectangle(bounds.Right - scaledDimension3 - Theme.S(10), bounds.Top, scaledDimension3, bounds.Height);
                int width = Theme.S(5);
                Color currentColor = (hasMatches ? Theme.Green : Theme.Faint);
                using (SolidBrush brush4 = new SolidBrush(currentColor))
                {
                    graphics.FillEllipse(brush4, candidateRectangle.Left, candidateRectangle.Top + (candidateRectangle.Height - width) / 2, width, width);
                }
                TextRenderer.DrawText(graphics, whitelistItem.StateText, Theme.UI(7.6f, false), new Rectangle(candidateRectangle.Left + Theme.S(11), candidateRectangle.Top, candidateRectangle.Width - Theme.S(11), candidateRectangle.Height), currentColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            if (!valuesMatch && !hovered)
            {
                using (Pen candidatePen = new Pen(Col.Alpha(Theme.Stroke, 100)))
                {
                    graphics.DrawLine(candidatePen, bounds.Left + Theme.S(13), bounds.Bottom - 1, bounds.Right - Theme.S(12), bounds.Bottom - 1);
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
            using (Pen currentPen = new Pen(Col.Alpha(c, required ? 112 : 172), Math.Max(1f, Theme.S(1))))
            {
                g.DrawLine(currentPen, leftOffset - Theme.S(4), top, leftOffset + Theme.S(4), top);
            }
            using (SolidBrush brush2 = new SolidBrush(Col.Alpha(c, required ? 160 : 225)))
            {
                g.FillEllipse(brush2, leftOffset - Theme.S(5) - width / 2, top - width / 2, width, width);
                g.FillEllipse(brush2, leftOffset - width / 2, top - width / 2, width, width);
                g.FillEllipse(brush2, leftOffset + Theme.S(5) - width / 2, top - width / 2, width, width);
            }
        }
        private Bitmap WhitelistIcon(WhitelistRule rule)
        {
            if (rule.Kind == WhitelistRuleKind.LegacyName)
            {
                return null;
            }
            string ruleValue = rule.Value;
            Bitmap currentWhiteIconCache;
            if (whiteIconCache.TryGetValue(ruleValue, out currentWhiteIconCache))
            {
                return currentWhiteIconCache;
            }
            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(ruleValue))
                {
                    currentWhiteIconCache = icon.ToBitmap();
                }
            }
            catch
            {
                currentWhiteIconCache = null;
            }
            whiteIconCache[ruleValue] = currentWhiteIconCache;
            return currentWhiteIconCache;
        }
        private string WhitelistTitle(WhitelistRule rule)
        {
            if (rule.Kind == WhitelistRuleKind.LegacyName)
            {
                return rule.Value;
            }
            string ruleValue = rule.Value;
            string currentWhiteNameCache;
            if (whiteNameCache.TryGetValue(ruleValue, out currentWhiteNameCache))
            {
                return currentWhiteNameCache;
            }
            string productName = null;
            try
            {
                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(ruleValue);
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
                    productName = Path.GetFileNameWithoutExtension(ruleValue);
                }
                catch
                {
                    productName = ruleValue;
                }
            }
            whiteNameCache[ruleValue] = productName;
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
            List<WhitelistItem> automaticExemptions = new List<WhitelistItem>();
            string systemNames = string.Join(" · ", SystemProcessCatalog.PresetWhitelist);
            automaticExemptions.Add(AutomaticExemption("white.auto.system", Lang.F("white.auto.system.list", systemNames), "shield", DetailSection("white.auto.details.processes", systemNames) + "\r\n\r\n" + Lang.T("white.auto.details.system.note")));
            string[] processNamesForDisplay = NetAcceleratorCatalog.ProcessNamesForDisplay();
            string[] tokensForDisplay = NetAcceleratorCatalog.TokensForDisplay();
            automaticExemptions.Add(AutomaticExemption("white.auto.accel", Lang.F("white.auto.accel.list", string.Join(" · ", processNamesForDisplay)), "chart", DetailSection("white.auto.details.processes", string.Join(" · ", processNamesForDisplay)) + "\r\n\r\n" + DetailSection("white.auto.details.namekeywords", string.Join(" · ", tokensForDisplay))));
            string[] currentProcessNamesForDisplay = HardwareControlCatalog.ProcessNamesForDisplay();
            string[] currentTokensForDisplay = HardwareControlCatalog.TokensForDisplay();
            automaticExemptions.Add(AutomaticExemption("white.auto.hardware", Lang.F("white.auto.hardware.list", string.Join(" · ", currentProcessNamesForDisplay)), "chip", DetailSection("white.auto.details.processes", string.Join(" · ", currentProcessNamesForDisplay)) + "\r\n\r\n" + DetailSection("white.auto.details.tokens", string.Join(" · ", currentTokensForDisplay)) + "\r\n\r\n" + Lang.T("white.auto.details.hardware.note")));
            List<string> texts = new List<string>();
            List<string> names = new List<string>();
            foreach (AcGroup acGroup in AntiCheatCatalog.Groups)
            {
                names.Add(acGroup.Name);
                texts.Add(acGroup.Name + "\r\n" + string.Join(" · ", acGroup.Procs));
            }
            foreach (AcProtectionGroup acProtectionGroup in AntiCheatCatalog.ProtectionOnlyGroups)
            {
                names.Add(acProtectionGroup.Name);
                texts.Add(acProtectionGroup.Name + " · " + Lang.T("ac.protectiononly") + "\r\n" + string.Join(" · ", acProtectionGroup.PatternsForDisplay()));
            }
            automaticExemptions.Add(AutomaticExemption("white.auto.anticheat", Lang.F("white.auto.anticheat.list", string.Join(" · ", names.ToArray())), "acshield", Lang.T("white.auto.details.groups") + "\r\n\r\n" + string.Join("\r\n\r\n", texts.ToArray()) + "\r\n\r\n" + DetailSection("white.auto.details.tokens", string.Join(" · ", AntiCheatCatalog.NameTokensForDisplay())) + "\r\n\r\n" + DetailSection("white.auto.details.anticheatdirs", string.Join(" · ", AntiCheatCatalog.DirectoryNamesForDisplay()))));
            string[] nameKeywordsForDisplay = PeripheralCatalog.NameKeywordsForDisplay();
            string[] descriptionWordsForDisplay = PeripheralCatalog.DescriptionWordsForDisplay();
            automaticExemptions.Add(AutomaticExemption("white.auto.peripheral", Lang.F("white.auto.peripheral.list", string.Join(" · ", nameKeywordsForDisplay)), "settings", DetailSection("white.auto.details.namekeywords", string.Join(" · ", nameKeywordsForDisplay)) + "\r\n\r\n" + DetailSection("white.auto.details.descriptionwords", string.Join(" · ", descriptionWordsForDisplay)) + "\r\n\r\n" + DetailSection("white.auto.details.vendors", string.Join(" · ", PeripheralCatalog.PresentVendorTokensForDisplay())) + "\r\n\r\n" + Lang.T("white.auto.details.peripheral.note")));
            string streamingProducts = string.Join(" · ", StreamingToolCatalog.ProductsForDisplay());
            automaticExemptions.Add(AutomaticExemption("white.auto.streaming", Lang.F("white.auto.streaming.list", streamingProducts), "chart", DetailSection("white.auto.details.products", streamingProducts) + "\r\n\r\n" + DetailSection("white.auto.details.processes", string.Join(" · ", StreamingToolCatalog.ProcessNamesForDisplay())) + "\r\n\r\n" + Lang.T("white.auto.details.streaming.note")));
            return automaticExemptions;
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
