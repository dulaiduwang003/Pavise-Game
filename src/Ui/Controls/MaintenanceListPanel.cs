using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class MaintenanceListItem
    {
        internal string Key, Title, Detail, Status;
        internal bool Retryable;
    }

    // 读取失败时禁用重试，避免对旧列表执行修改。
    internal sealed class MaintenanceListPanel : DBPanel
    {
        private readonly Func<List<MaintenanceListItem>> load;
        private readonly Func<string, string> retry;
        private readonly PillButton refresh;
        private readonly Label summary;
        private readonly DBPanel rows;
        private readonly ToolTip tips = new ToolTip();
        private List<MaintenanceListItem> items = new List<MaintenanceListItem>();
        private bool busy, loaded, readable;
        private int generation;
        private string message = "";
#if PAVISE_SELFTEST
        internal Action<Action> QueueForTest, PostForTest;
        internal bool BusyForTest { get { return busy; } }
        internal bool ReadableForTest { get { return readable; } }
        internal int ItemCountForTest { get { return items.Count; } }
        internal void RefreshForTest() { RefreshItems(); }
#endif
        private static string TextFor(string zh, string en) { return AutomaticPolicy.Text(zh, en); }

        internal MaintenanceListPanel(Func<List<MaintenanceListItem>> query, Func<string, string> retryItem)
        {
            if (query == null) throw new ArgumentNullException("query");
            load = query; retry = retryItem;
            Name = "maintenanceList";
            BackColor = Theme.Bg;
            Size = new Size(Theme.S(708), Theme.S(330));
            refresh = new PillButton(TextFor("刷新", "Refresh")) { Name = "maintenanceRefresh" };
            refresh.Click += delegate { RefreshItems(); };
            summary = new Label { Name = "maintenanceSummary", BackColor = Theme.Bg,
                ForeColor = Theme.Dim, Font = Theme.UI(8.5f, false), AutoEllipsis = true };
            rows = new DBPanel { Name = "maintenanceRows", BackColor = Theme.Bg, AutoScroll = true };
            Controls.Add(refresh); Controls.Add(summary); Controls.Add(rows);
            Resize += delegate { LayoutChildren(); RenderItems(); };
            LayoutChildren();
        }

        protected override void OnHandleCreated(EventArgs e)
        { base.OnHandleCreated(e); if (!loaded) { loaded = true; RefreshItems(); } }
        protected override void OnHandleDestroyed(EventArgs e)
        { generation++; busy = false; loaded = false; base.OnHandleDestroyed(e); }

        private void LayoutChildren()
        {
            int gap = Theme.S(8), button = Theme.S(76), header = Theme.S(32);
            refresh.SetBounds(Math.Max(0, Width - button), 0, button, header);
            summary.SetBounds(0, 0, Math.Max(1, refresh.Left - gap), header);
            rows.SetBounds(0, header + gap, Math.Max(1, Width), Math.Max(1, Height - header - gap));
        }

        private void SetBusy(bool value)
        {
            busy = value;
            refresh.Busy = value;
            refresh.Enabled = !value;
            rows.Enabled = !value && readable;
        }

        private void Queue(Action work)
        {
#if PAVISE_SELFTEST
            if (QueueForTest != null) { QueueForTest(work); return; }
#endif
            if (!ThreadPool.QueueUserWorkItem(delegate { work(); })) throw new InvalidOperationException("Maintenance task could not be queued");
        }
        private void Post(Action result)
        {
#if PAVISE_SELFTEST
            if (PostForTest != null) { PostForTest(result); return; }
#endif
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && !Disposing) result(); }); }
            catch (InvalidOperationException) { }
        }

        private void RefreshItems() { loaded = true; RunOperation(null); }
        private void RunOperation(Func<string> operation)
        {
            if (busy || IsDisposed || Disposing) return;
            int current = ++generation;
            SetBusy(true);
            summary.Text = TextFor("正在读取记录…", "Reading records…");
            summary.ForeColor = Theme.Dim;
            try { Queue(delegate {
                List<MaintenanceListItem> next = null;
                string failure = null;
                try {
                    if (operation != null) failure = operation();
                    next = load();
                    if (next == null) throw new InvalidOperationException("Record query returned no result");
                } catch (Exception error) { failure = PsRunner.NormalizeError(error.Message); }
                Post(delegate {
                    if (current != generation) return;
                    readable = next != null;
                    if (readable) items = next;
                    message = failure ?? "";
                    SetBusy(false); RenderItems();
                });
            }); }
            catch (Exception error) {
                readable = false; message = PsRunner.NormalizeError(error.Message);
                SetBusy(false); RenderItems();
            }
        }

        private void RenderItems()
        {
            if (rows == null || IsDisposed) return;
            rows.SuspendLayout();
            try {
                foreach (Control child in new List<Control>(Children(rows))) child.Dispose();
                int y = 0, shown = 0;
                foreach (MaintenanceListItem item in items) {
                    var card = new SettingCard { Name = "maintenanceRecord", Title = item.Title ?? "",
                        Desc = item.Detail ?? "", AccessibleName = item.Title ?? "",
                        AccessibleDescription = (item.Status ?? "") + "\n" + (item.Detail ?? ""),
                        TabStop = false };
                    card.SetBounds(0, y, Math.Max(Theme.S(150), rows.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - Theme.S(4)), Theme.S(108));
                    card.SetStatus(item.Status ?? "", Theme.Warning);
                    tips.SetToolTip(card, item.Detail ?? "");
                    if (retry != null && item.Retryable) {
                        string key = item.Key;
                        var button = new PillButton(TextFor("重试一次", "Retry once")) { Name = "maintenanceRetry", Bg = Theme.Card };
                        button.Size = new Size(Theme.S(120), Theme.S(32));
                        button.AccessibleDescription = TextFor("仅允许这项动作重试一次，失败后继续停用。", "Allows one retry for this action. It remains disabled if the retry fails.");
                        button.Click += delegate { if (readable && !busy) RunOperation(delegate { return retry(key); }); };
                        card.Host(button);
                    }
                    rows.Controls.Add(card); y += card.Height + Theme.S(8); shown++;
                }
                if (shown == 0 && readable) {
                    var empty = new Label { Name = "maintenanceEmpty", AutoSize = false,
                        Text = TextFor("暂无记录", "No records"), Font = Theme.UI(11, false),
                        ForeColor = Theme.Dim, TextAlign = ContentAlignment.MiddleCenter,
                        BackColor = Theme.Bg, Dock = DockStyle.Top, Height = Theme.S(70) };
                    rows.Controls.Add(empty);
                }
                rows.AutoScrollMinSize = new Size(0, y);
                summary.Text = message.Length > 0 ? message
                    : TextFor("已停用 ", "Disabled ") + items.Count + TextFor(" 项 · 仅手动重试", " actions · manual retry only");
                summary.ForeColor = message.Length > 0 ? Theme.Warning : Theme.Dim;
                tips.SetToolTip(summary, message);
                rows.Enabled = !busy && readable;
            } finally { rows.ResumeLayout(); }
        }

        private static IEnumerable<Control> Children(Control parent)
        { foreach (Control child in parent.Controls) yield return child; }

        protected override void Dispose(bool disposing)
        { if (disposing) { generation++; tips.Dispose(); } base.Dispose(disposing); }
    }
}
