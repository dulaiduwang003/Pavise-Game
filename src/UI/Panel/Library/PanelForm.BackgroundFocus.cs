using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private RoundPanel focusSection;
        private Label focusDescription, focusEmpty, focusFeedback;
        private PillButton focusAdd, focusRemove;
        private TechListBox focusList;
        private Control focusListHost;
        private BackgroundFocusItem[] focusEntries = new BackgroundFocusItem[0];
        private int focusRefreshPending, focusOpPending, focusEntriesVersion;
        private long focusNextRead;

        private void BuildFocusSuppressionSection()
        {
            focusEntries = new BackgroundFocusItem[0];
            focusEntriesVersion++;
            focusNextRead = 0;
            focusSection = MakeConsolePanel(pageWhitelist, 0, 0, 1, 1, false);
            focusSection.Name = "FocusedSuppressionSection";
            focusSection.Visible = false;
            focusDescription = CardLabel(focusSection, Lang.T("background.focus.scope"), 0, 0, 1, 1, 9f, false, Theme.Dim);
            focusDescription.Name = "FocusedSuppressionScope";
            focusDescription.TextAlign = ContentAlignment.MiddleLeft;
            focusDescription.AccessibleDescription = Lang.T("background.focus.description");
            var policyTip = new ToolTip();
            policyTip.SetToolTip(focusDescription, focusDescription.AccessibleDescription);
            focusSection.Disposed += delegate { policyTip.Dispose(); };
            focusEmpty = CardLabel(focusSection, Lang.T("background.focus.empty"), 0, 0, 1, 1, 9f, false, Theme.Dim);
            focusEmpty.TextAlign = ContentAlignment.MiddleCenter;
            focusFeedback = CardLabel(focusSection, "", 0, 0, 1, 1, 8.5f, false, Theme.Warning);
            focusFeedback.Name = "FocusedSuppressionFeedback";
            focusFeedback.TextAlign = ContentAlignment.MiddleLeft;
            focusAdd = new PillButton(Lang.T("background.focus.action.add"), BtnKind.Primary) { Name = "AddFocusedSuppression" };
            focusRemove = new PillButton(Lang.T("background.focus.remove")) { Name = "RemoveFocusedSuppression", Enabled = false, Visible = false };
            focusAdd.AccessibleDescription = Lang.T("background.focus.warning");
            focusAdd.Click += delegate { PickFocusSuppression(); };
            focusRemove.Click += delegate { RemoveFocusSuppression(); };
            focusList = new TechListBox { Name = "FocusedSuppressionRules", Dock = DockStyle.Fill };
            Theme.StyleList(focusList, false);
            focusList.AccessibleName = Lang.T("background.focus.title");
            focusList.ItemHeight = Theme.S(48);
            focusList.DrawItem += DrawFocusRule;
            focusList.SelectedIndexChanged += delegate { SyncFocusActions(); };
            focusList.KeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.KeyCode != Keys.Delete || e.Modifiers != Keys.None) return;
                RemoveFocusSuppression();
                e.SuppressKeyPress = true;
            };
            focusListHost = new TechListScrollHost(focusList) { Visible = false };
            focusSection.Controls.AddRange(new Control[] { focusAdd, focusRemove, focusListHost });
            SyncFocusActions();
        }

        private void LayoutFocusSuppressionSection(int x, int y, int width, int height)
        {
            if (focusSection == null) return;
            const int padding = 12, gap = 8, buttonHeight = 34;
            var measureFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int addWidth = Math.Max(96, Dpi.U(TextRenderer.MeasureText(focusAdd.Text, DsType.Font(DsText.Body, true), Size.Empty, measureFlags).Width) + 24);
            int removeWidth = Math.Max(76, Dpi.U(TextRenderer.MeasureText(focusRemove.Text, focusRemove.Font, Size.Empty, measureFlags).Width) + 24);
            int scopeWidth = Dpi.U(TextRenderer.MeasureText(focusDescription.Text, focusDescription.Font, Size.Empty, measureFlags).Width);
            int insideWidth = width - 2 * padding;
            int actionWidth = addWidth + gap + removeWidth;
            bool stack = scopeWidth + gap + actionWidth > insideWidth;
            int actionTop = stack ? 10 + buttonHeight + gap : 10;
            int contentTop = actionTop + buttonHeight + gap;
            int cardHeight = focusEntries.Length == 0 ? Math.Min(height, contentTop + 36 + 26 + padding) : height;
            WhiteBounds(focusSection, x, y, width, cardHeight);
            SetOverviewBounds(focusDescription, padding, 10, stack ? insideWidth : insideWidth - actionWidth - gap, buttonHeight);
            SetOverviewBounds(focusAdd, width - padding - addWidth, actionTop, addWidth, buttonHeight);
            SetOverviewBounds(focusRemove, width - padding - actionWidth, actionTop, removeWidth, buttonHeight);
            int feedbackY = Math.Max(contentTop, cardHeight - padding - 26);
            SetOverviewBounds(focusListHost, padding, contentTop, insideWidth, feedbackY - contentTop);
            SetOverviewBounds(focusEmpty, padding, contentTop, insideWidth, Math.Min(36, feedbackY - contentTop));
            SetOverviewBounds(focusFeedback, padding, feedbackY, insideWidth, 26);
        }

        private BackgroundFocusItem SelectedFocusRule()
        {
            int index = focusList.SelectedIndex;
            return index >= 0 && index < focusEntries.Length ? focusEntries[index] : null;
        }

        private void DrawFocusRule(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= focusEntries.Length) return;
            BackgroundFocusItem item = focusEntries[e.Index];
            Rectangle bounds = e.Bounds;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var fill = new SolidBrush(selected ? Theme.Sel : e.Index == Theme.HoverIndex(focusList) ? Theme.CardHover : Theme.Card))
                e.Graphics.FillRectangle(fill, bounds);
            int pad = Theme.S(8);
            int width = Math.Max(1, bounds.Width - pad * 2);
            int x = bounds.Left + pad;
            var style = TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            string status = FocusRuleStatus(item);
            bool showStatus = status != null;
            TextRenderer.DrawText(e.Graphics, item.Name, Theme.UI(9f, true),
                new Rectangle(x, bounds.Top + Theme.S(showStatus ? 1 : 5), width, Theme.S(17)), Theme.Fg, style);
            TextRenderer.DrawText(e.Graphics, item.Path, Theme.UI(8f, false),
                new Rectangle(x, bounds.Top + Theme.S(showStatus ? 17 : 23), width, Theme.S(16)), Theme.Dim,
                TextFormatFlags.PathEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (showStatus)
                TextRenderer.DrawText(e.Graphics, status, Theme.UI(8f, true),
                    new Rectangle(x, bounds.Top + Theme.S(32), width, Theme.S(15)),
                    string.IsNullOrEmpty(item.BlockReason) ? Theme.Fg : Theme.Warning, style);
            e.DrawFocusRectangle();
        }

        private void SyncFocusActions()
        {
            if (focusList != null && !focusList.IsDisposed)
            {
                BackgroundFocusItem selected = SelectedFocusRule();
                focusAdd.Enabled = FocusActionsAvailable;
                focusRemove.Visible = selected != null;
                focusRemove.Enabled = selected != null && FocusActionsAvailable;
                focusList.AccessibleDescription = selected == null ? focusDescription.AccessibleDescription : FocusRuleText(selected);
            }
        }

        private bool FocusActionsAvailable
        {
            get { return !runtimeShutdownBusy && wipeBusy == 0 && Volatile.Read(ref focusOpPending) == 0; }
        }

        private static string FocusRuleStatus(BackgroundFocusItem entry)
        {
            if (!string.IsNullOrEmpty(entry.BlockReason))
                return Lang.F("background.focus.inactive", Lang.T(entry.BlockReason));
            return entry.UsesPerProcessLimits ? Lang.T("background.focus.perprocess") : null;
        }

        private static string FocusRuleText(BackgroundFocusItem entry)
        {
            string label = entry.Name + " · " + entry.Path;
            string status = FocusRuleStatus(entry);
            return status == null ? label : label + " · " + status;
        }

        private void PresentFocusEntries(BackgroundFocusItem[] entries)
        {
            if (FocusItemsEqual(focusEntries, entries)) return;
            bool emptyChanged = (focusEntries.Length == 0) != (entries.Length == 0);
            BackgroundFocusItem selected = focusList == null || focusList.IsDisposed ? null : SelectedFocusRule();
            focusEntries = entries;
            if (focusList != null && !focusList.IsDisposed)
            {
                int topIndex = focusList.TopIndex;
                focusList.BeginUpdate();
                focusList.Items.Clear();
                for (int i = 0; i < entries.Length; i++)
                {
                    focusList.Items.Add(FocusRuleText(entries[i]));
                    if (selected != null && SameFocusApp(selected, entries[i])) focusList.SelectedIndex = i;
                }
                if (entries.Length != 0) focusList.TopIndex = Math.Min(topIndex, entries.Length - 1);
                focusList.EndUpdate();
                focusListHost.Visible = entries.Length != 0;
                focusEmpty.Visible = entries.Length == 0;
            }
            SyncFocusActions();
            SyncProcessRulesTabLabels();
            if (emptyChanged) LayoutWhitelistViewport();
        }

        private static bool SameFocusApp(BackgroundFocusItem first, BackgroundFocusItem second)
        {
            return string.Equals(first.Name, second.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(first.Path, second.Path, StringComparison.OrdinalIgnoreCase);
        }

        private static bool FocusItemsEqual(BackgroundFocusItem[] first, BackgroundFocusItem[] second)
        {
            if (first == second) return true;
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++)
                if (!SameFocusApp(first[i], second[i])
                    || first[i].BlockReason != second[i].BlockReason || first[i].UsesPerProcessLimits != second[i].UsesPerProcessLimits)
                    return false;
            return true;
        }

        private void RefreshFocusEntries()
        {
            long now = AutoCores.NowTicks;
            if (gameMode == null || IsDisposed || Disposing || !IsHandleCreated || now < focusNextRead
                || Volatile.Read(ref focusOpPending) != 0 || Interlocked.CompareExchange(ref focusRefreshPending, 1, 0) != 0) return;
            focusNextRead = now + TimeSpan.FromSeconds(2).Ticks;
            int version = focusEntriesVersion;
            try
            {
                QueueUiStateWork(delegate {
                    try
                    {
                        BackgroundFocusItem[] entries = gameMode.BackgroundFocusEntries();
                        PostUiStateResult(delegate {
                            Interlocked.Exchange(ref focusRefreshPending, 0);
                            if (IsDisposed || Disposing) return;
                            if (version == focusEntriesVersion) PresentFocusEntries(entries);
                            else RefreshFocusEntries();
                        });
                    }
                    catch (Exception) { Interlocked.Exchange(ref focusRefreshPending, 0); }
                });
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref focusRefreshPending, 0); }
        }

        private void PickFocusSuppression()
        {
            if (!FocusActionsAvailable) return;
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (BackgroundFocusItem entry in focusEntries) listed.Add(entry.Key);
            using (var picker = new RunningPickerDialog(listed, Lang.T("background.focus.add"), Lang.T("background.focus.pick"),
                Lang.T("background.focus.warning"), gameMode.GovernedBackgroundPids(), focusPicker: true))
            {
                if (ShowDim(picker) != DialogResult.OK || picker.FocusSelection == null) return;
                RunningPickerDialog.Entry selected = picker.FocusSelection;
                ChangeFocusSuppression(selected.ProcessName, selected.Path, true, focusFeedback);
            }
        }

        private void RemoveFocusSuppression()
        {
            BackgroundFocusItem selected = SelectedFocusRule();
            if (selected != null) ChangeFocusSuppression(selected.Name, selected.Path, false, focusFeedback);
        }

        private void ChangeFocusSuppression(string name, string path, bool add, Label feedback)
        {
            if (!FocusActionsAvailable || Interlocked.CompareExchange(ref focusOpPending, 1, 0) != 0) return;
            focusEntriesVersion++;
            feedback.Text = "";
            SyncFocusActions();
            try
            {
                QueueUiStateWork(delegate {
                    bool succeeded = false;
                    string reason = null;
                    try
                    {
                        succeeded = add ? gameMode.TryAddBackgroundFocus(name, path, out reason)
                            : gameMode.TryRemoveBackgroundFocus(name, path, out reason);
                    }
                    catch (Exception) { reason = "background.focus.reason.save"; }
                    try
                    {
                        PostUiStateResult(delegate {
                            Interlocked.Exchange(ref focusOpPending, 0);
                            if (IsDisposed || Disposing) return;
                            if (!feedback.IsDisposed)
                            {
                                feedback.ForeColor = succeeded ? Theme.Dim : Theme.Warning;
                                feedback.Text = succeeded ? (add ? Lang.T("background.focus.added") : "")
                                    : Lang.T(reason ?? "background.focus.reason.save");
                                feedback.AccessibleDescription = feedback.Text;
                            }
                            focusNextRead = 0;
                            RefreshFocusEntries();
                            SyncFocusActions();
                        });
                    }
                    catch (InvalidOperationException) { Interlocked.Exchange(ref focusOpPending, 0); }
                });
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref focusOpPending, 0);
                feedback.Text = Lang.T("background.focus.reason.save");
                SyncFocusActions();
            }
        }
    }
}
