using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class AutoIgnoreDialog : Form
    {
        private readonly GameMode mode;
        private readonly WorkspacePanel content = new WorkspacePanel();
        private readonly Label title, description, status, pathTitle, empty;
        private readonly TechListBox list = new TechListBox();
        private readonly RoundPanel listWrap = new RoundPanel();
        private readonly TextBox path = new TextBox();
        private readonly LibraryDialogButton resume, close;
        private int changeBusy;
#if PAVISE_SELFTEST
        internal Action<Action> WorkQueueForTest;
        internal Action<Action> PostForTest;
#endif
        internal AutoIgnoreDialog(GameMode gameMode)
        {
            mode = gameMode;
            AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9f, false);
            Text = Lang.T("autoignore.title"); AccessibleName = Text;
            title = MakeLabel(Text, "autoIgnoreTitle", Theme.UI(15f, true), Theme.Fg);
            description = MakeLabel(Lang.T("autoignore.desc"), "autoIgnoreDescription", Theme.UI(8.8f, false), Theme.Dim);
            status = MakeLabel("", "autoIgnoreStatus", Theme.UI(8.2f, false), Theme.Dim);
            status.AutoEllipsis = true;
            pathTitle = MakeLabel(Lang.T("autoignore.path"), "autoIgnorePathTitle", Theme.UI(8.2f, false), Theme.Dim);
            empty = MakeLabel(Lang.T("autoignore.empty"), "autoIgnoreEmpty", Theme.UI(10f, false), Theme.Dim);
            empty.TextAlign = ContentAlignment.MiddleCenter; empty.Dock = DockStyle.Fill;
            list.Name = "autoIgnoreList"; list.AccessibleName = Text;
            Theme.StyleList(list, false); list.ItemHeight = Math.Min(255, Theme.S(56)); list.Dock = DockStyle.Fill;
            list.DrawItem += DrawEntry; list.SelectedIndexChanged += delegate { UpdateSelection(); };
            listWrap.Name = "autoIgnoreListWrap"; listWrap.Fill = Theme.Card; listWrap.Border = Theme.Stroke;
            listWrap.Padding = new Padding(Theme.S(8));
            listWrap.Controls.AddRange(new Control[] { list, empty });
            path.Name = "autoIgnorePath"; path.AccessibleName = pathTitle.Text;
            path.ReadOnly = true; path.Multiline = true; path.WordWrap = false; path.ScrollBars = ScrollBars.Both;
            path.BackColor = Theme.Inset; path.ForeColor = Theme.Fg; path.Font = Theme.UI(8.3f, false);
            path.BorderStyle = BorderStyle.FixedSingle; Native.Dark(path);
            resume = new LibraryDialogButton(Lang.T("autoignore.resume"), true) { Name = "autoIgnoreResume" };
            close = new LibraryDialogButton(Lang.T("autoignore.close"), false) { Name = "autoIgnoreClose", DialogResult = DialogResult.Cancel };
            resume.Click += delegate { ResumeSelected(); };
            close.Click += delegate { Close(); };
            CancelButton = close;
            content.Name = "autoIgnoreContent"; content.AutoScroll = true; content.BackColor = Theme.Bg; Native.Dark(content);
            content.Controls.AddRange(new Control[] { title, description, listWrap, pathTitle, path });
            Controls.AddRange(new Control[] { content, status, resume, close });
            Rectangle available = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(Theme.S(680), available.Width - 32), Math.Min(Theme.S(530), available.Height - 32));
            LayoutContent(); RefreshEntries(null);
        }
        private static Label MakeLabel(string text, string name, Font font, Color color)
        { return new Label { Text = text, Name = name, Font = font, ForeColor = color, BackColor = Color.Transparent, UseMnemonic = false }; }
        private void LayoutContent()
        {
            int pad = Theme.S(24), inner = ClientSize.Width - pad * 2;
            int buttonY = ClientSize.Height - Theme.S(64), buttonH = Theme.S(40), buttonW = (inner - Theme.S(12)) / 2;
            close.SetBounds(pad, buttonY, buttonW, buttonH);
            resume.SetBounds(close.Right + Theme.S(12), buttonY, buttonW, buttonH);
            status.SetBounds(pad, buttonY - Theme.S(32), inner, Theme.S(26));
            content.SetBounds(0, 0, ClientSize.Width, Math.Max(1, status.Top - Theme.S(12)));
            int scrollSpace = Math.Max(Theme.S(20), SystemInformation.VerticalScrollBarWidth + 2);
            int bodyWidth = inner - scrollSpace;
            int titleHeight = Math.Max(Theme.S(36), TextRenderer.MeasureText(title.Text, title.Font,
                new Size(bodyWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + Theme.S(4));
            title.SetBounds(pad, Theme.S(20), bodyWidth, titleHeight);
            int descHeight = TextRenderer.MeasureText(description.Text, description.Font,
                new Size(bodyWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + Theme.S(6);
            description.SetBounds(pad, title.Bottom + Theme.S(8), bodyWidth, descHeight);
            int listY = description.Bottom + Theme.S(16);
            int listHeight = Math.Max(Theme.S(110), content.Height - listY - Theme.S(134));
            listWrap.SetBounds(pad, listY, bodyWidth, listHeight);
            pathTitle.SetBounds(pad, listWrap.Bottom + Theme.S(16), bodyWidth, Theme.S(22));
            path.SetBounds(pad, pathTitle.Bottom + Theme.S(4), bodyWidth, Theme.S(66));
            content.AutoScrollMinSize = new Size(0, path.Bottom + Theme.S(16));
        }
        protected override void OnClientSizeChanged(EventArgs e)
        { base.OnClientSizeChanged(e); if (close != null) LayoutContent(); }
        protected override void OnLoad(EventArgs e)
        {
            if (Owner != null)
            {
                Rectangle available = Screen.FromControl(Owner).WorkingArea;
                ClientSize = new Size(Math.Min(Theme.S(680), available.Width - 32), Math.Min(Theme.S(530), available.Height - 32));
            }
            base.OnLoad(e);
        }
        private static string BaseName(string executable)
        { try { return Path.GetFileName(executable); } catch { return executable; } }
        private void DrawEntry(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            string entry = (string)list.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var fill = new SolidBrush(selected ? Theme.CardHover : Theme.Card)) e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, BaseName(entry), Theme.UI(9.2f, true),
                new Rectangle(e.Bounds.Left + Theme.S(10), e.Bounds.Top + Theme.S(5), e.Bounds.Width - Theme.S(20), Theme.S(22)),
                selected ? Theme.Accent : Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, entry, Theme.UI(7.8f, false),
                new Rectangle(e.Bounds.Left + Theme.S(10), e.Bounds.Top + Theme.S(28), e.Bounds.Width - Theme.S(20), Theme.S(21)),
                Theme.Dim, TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (selected) e.DrawFocusRectangle();
        }
        private void RefreshEntries(string keep)
        {
            List<string> entries = mode.GetAutoAddIgnoredPaths();
            list.BeginUpdate();
            try
            {
                list.Items.Clear(); foreach (string entry in entries) list.Items.Add(entry);
                if (keep != null)
                    for (int i = 0; i < list.Items.Count; i++)
                        if (string.Equals((string)list.Items[i], keep, StringComparison.OrdinalIgnoreCase)) { list.SelectedIndex = i; break; }
                if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
            }
            finally { list.EndUpdate(); }
            empty.Visible = entries.Count == 0; list.Visible = entries.Count > 0;
            status.Text = Lang.F("autoignore.count", entries.Count); status.ForeColor = Theme.Dim;
            UpdateSelection();
        }
        private void UpdateSelection()
        {
            path.Text = list.SelectedItem as string ?? "";
            resume.Enabled = Interlocked.CompareExchange(ref changeBusy, 0, 0) == 0 && list.SelectedItem != null;
        }
        private void QueueWork(Action action)
        {
#if PAVISE_SELFTEST
            if (WorkQueueForTest != null) { WorkQueueForTest(action); return; }
#endif
            if (!ThreadPool.QueueUserWorkItem(delegate { action(); })) throw new InvalidOperationException("Work queue rejected");
        }
        private void PostResult(Action action)
        {
#if PAVISE_SELFTEST
            if (PostForTest != null) { PostForTest(action); return; }
#endif
            BeginInvoke((MethodInvoker)delegate { action(); });
        }
        private void ResumeSelected()
        {
            string selected = list.SelectedItem as string;
            if (selected == null || IsDisposed || Disposing || Interlocked.CompareExchange(ref changeBusy, 1, 0) != 0) return;
            resume.Enabled = false; status.Text = Lang.T("autoignore.busy"); status.ForeColor = Theme.Dim;
            try
            {
                QueueWork(delegate
                {
                    bool saved = false;
                    try { saved = mode.RemoveAutoAddIgnoredPath(selected); } catch { }
                    try
                    {
                        PostResult(delegate
                        {
                            try
                            {
                                if (IsDisposed || Disposing) return;
                                RefreshEntries(selected);
                                bool retained = mode.GetAutoAddIgnoredPaths().Exists(p => string.Equals(p, selected, StringComparison.OrdinalIgnoreCase));
                                status.Text = Lang.T(saved || !retained ? "autoignore.removed" : "autoignore.failed");
                                status.ForeColor = saved || !retained ? Theme.Accent : Theme.Warning;
                            }
                            finally
                            {
                                Interlocked.Exchange(ref changeBusy, 0);
                                if (!IsDisposed && !Disposing) UpdateSelection();
                            }
                        });
                    }
                    catch { Interlocked.Exchange(ref changeBusy, 0); }
                });
            }
            catch
            {
                Interlocked.Exchange(ref changeBusy, 0); UpdateSelection();
                status.Text = Lang.T("autoignore.failed"); status.ForeColor = Theme.Warning;
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var edge = new Pen(Theme.StrokeHi)) e.Graphics.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        }
    }
}
