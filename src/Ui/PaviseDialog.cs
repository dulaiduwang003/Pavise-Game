// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PaviseApp
{
    internal enum DlgKind { Info, Warn, Danger, Success }

    // 对话框：必做动作经 BusyGate 门禁，成功才允许关闭；Escape 在必做动作下被吞掉。
    internal sealed class PaviseDialog : Form
    {
        private const int DlgW = DsMetrics.Dialog.Width;
        private const int PadX = DsMetrics.Dialog.PadX;
        private const int TagTop = DsMetrics.Dialog.TagTop;
        private const int TagHeight = DsMetrics.Dialog.TagHeight;
        private const int TitleTop = DsMetrics.Dialog.TitleTop;
        private const int BodyTop = DsMetrics.Dialog.BodyTop;
        private const int BtnH = DsMetrics.Dialog.ButtonHeight;
        private const int BtnGap = DsMetrics.Dialog.ButtonGap;
        private const int BottomPad = DsMetrics.Dialog.BottomPad;

        private readonly string title;
        private readonly string body;
        private readonly DlgKind kind;
        private readonly bool cancellable;
        private readonly bool requiredAction;
        private bool requiredActionCompleted;
        private float sweep;
        private Rectangle bodyRect;

        private PaviseDialog(string dlgTitle, string dlgBody, DlgKind dlgKind,
            string okText, string cancelText, Control extra, int width,
            Func<bool> action = null, Action onFailure = null)
        {
            title = dlgTitle ?? "";
            body = dlgBody ?? "";
            kind = dlgKind;
            cancellable = cancelText != null;
            requiredAction = action != null;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = DsType.Font(DsText.BodyLarge);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            KeyPreview = true;

            Rectangle workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            int w = Math.Min(Theme.S(width), Math.Max(1, workArea.Width - Theme.S(DsMetrics.Dialog.ScreenMargin)));
            int textW = Math.Max(1, w - Theme.S(PadX) * 2);
            int textH = MeasureBody(textW);
            int footer = Theme.S(BtnH) + Theme.S(BottomPad) + Theme.S(DsMetrics.Dialog.FooterPad);
            if (extra is MaintenanceListPanel)
            {
                extra.Width = textW;
                extra.Height = Math.Min(extra.Height, Math.Max(1, workArea.Height
                    - Theme.S(DsMetrics.Dialog.ScreenMargin + BodyTop + DsMetrics.Dialog.ExtraGap) - footer - textH));
            }
            int extraH = extra == null ? 0 : extra.Height + Theme.S(DsMetrics.Dialog.ExtraGap);
            int fixedH = Theme.S(BodyTop) + extraH + footer;
            int availableH = workArea.Height - Theme.S(DsMetrics.Dialog.ScreenMargin);
            int bodyLimit = Math.Max(Theme.S(DsMetrics.Dialog.MinBody), availableH - fixedH);
            bool scrollBody = extra == null && textH > bodyLimit;
            if (scrollBody) textH = bodyLimit;
            int h = Theme.S(BodyTop) + textH + extraH + footer;
            ClientSize = new Size(w, h);
            bodyRect = new Rectangle(Theme.S(PadX), Theme.S(BodyTop), textW, textH);
            if (scrollBody)
            {
                var details = Theme.MakeTextBox(bodyRect.X, bodyRect.Y, bodyRect.Width);
                details.Name = "ScrollableDialogDetails";
                details.Multiline = true;
                details.ReadOnly = true;
                details.WordWrap = true;
                details.ScrollBars = ScrollBars.Vertical;
                details.Height = bodyRect.Height;
                details.Text = body;
                Controls.Add(details);
                bodyRect = Rectangle.Empty;
            }
            if (extra != null)
            {
                extra.Location = new Point(Theme.S(PadX), Theme.S(BodyTop) + textH);
                Controls.Add(extra);
            }

            var ok = new PillButton(okText ?? Lang.T("dlg.ok"),
                kind == DlgKind.Danger ? BtnKind.Danger : BtnKind.Primary);
            ok.Size = new Size(Theme.S(DsMetrics.Dialog.AcceptWidth), Theme.S(BtnH));
            ok.Name = "dialogAccept";
            var gate = new BusyGate(ok, true);
            Action<bool> finishAction = delegate(bool completed)
            {
                if (IsDisposed || Disposing) return;
                if (completed)
                {
                    requiredActionCompleted = true;
                    gate.Leave(); DialogResult = DialogResult.OK; Close();
                }
                else
                {
                    try { if (onFailure != null) onFailure(); }
                    finally { gate.Leave(); }
                }
            };
            ok.Click += delegate
            {
                if (!requiredAction) { DialogResult = DialogResult.OK; Close(); return; }
                // 重复提交由门禁拒绝；动作运行期间按钮置忙并禁用。
                if (requiredActionCompleted || !IsHandleCreated || !gate.TryEnter()) return;
                try
                {
                    Task.Factory.StartNew(action).ContinueWith(delegate(Task<bool> finished)
                    {
                        bool completed = finished.Status == TaskStatus.RanToCompletion && finished.Result;
                        if (finished.IsFaulted && finished.Exception != null) completed = false;
                        if (IsDisposed || Disposing || !IsHandleCreated) return;
                        try { BeginInvoke((MethodInvoker)delegate { finishAction(completed); }); }
                        catch (InvalidOperationException) { }
                    }, TaskScheduler.Default);
                }
                catch { finishAction(false); }
            };
            Controls.Add(ok);

            PillButton cancel = null;
            if (cancellable)
            {
                cancel = new PillButton(cancelText, BtnKind.Normal);
                cancel.Bg = Theme.Card;
                cancel.Size = new Size(Theme.S(DsMetrics.Dialog.CancelWidth), Theme.S(BtnH));
                cancel.Name = "dialogCancel";
                cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
                Controls.Add(cancel);
            }

            int by = h - Theme.S(BottomPad) - Theme.S(BtnH);
            int rx = w - Theme.S(PadX) - ok.Width;
            ok.Location = new Point(rx, by);
            if (cancel != null)
                cancel.Location = new Point(rx - Theme.S(BtnGap) - cancel.Width, by);

            AcceptButton = null;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    if (requiredAction) { e.Handled = e.SuppressKeyPress = true; return; }
                    DialogResult = cancellable ? DialogResult.Cancel : DialogResult.OK;
                    Close();
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    if (requiredAction) { e.Handled = e.SuppressKeyPress = true; ok.PerformClick(); return; }
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };

            var timer = new Timer();
            timer.Interval = 33;
            timer.Tick += delegate
            {
                sweep += 0.012f;
                if (sweep > 1f) sweep -= 1f;
                Invalidate(new Rectangle(0, 0, Width, Theme.S(DsMetrics.Dialog.BarHeight)));
            };
            timer.Start();
            Disposed += delegate { timer.Stop(); timer.Dispose(); };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Fx.EnterForm(this);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (requiredAction && !requiredActionCompleted && e.CloseReason != CloseReason.WindowsShutDown
                && e.CloseReason != CloseReason.TaskManagerClosing)
            {
                e.Cancel = true;
                DialogResult = DialogResult.None;
            }
        }

        internal static PaviseDialog CreateRequiredRestart(Func<bool> restart)
        {
            if (restart == null) throw new ArgumentNullException("restart");
            var feedback = new Label { Name = "restartError", AutoSize = false,
                Font = DsType.Font(DsText.Body), ForeColor = Theme.Warning, BackColor = Theme.Bg,
                Width = Theme.S(DlgW - PadX * 2), Height = Theme.S(DsMetrics.Dialog.FeedbackHeight) };
            return new PaviseDialog(Lang.T("vbs.restart.title"), Lang.T("vbs.restart.body"), DlgKind.Warn,
                Lang.T("vbs.restart.now"), null, feedback, DlgW, restart,
                delegate { feedback.Text = Lang.T("vbs.restart.failed"); });
        }

        internal static bool RequireRestart(IWin32Window owner)
        {
            using (var dialog = CreateRequiredRestart(SystemRestart.Request))
                return (owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == DialogResult.OK;
        }

        private int MeasureBody(int width)
        {
            Font f = DsType.Font(DsText.BodyLarge);
            using (var g = CreateGraphics())
            {
                SizeF sz = g.MeasureString(body, f, width);
                return Math.Max(Theme.S(DsMetrics.Dialog.MinBody),
                    (int)Math.Ceiling(sz.Height) + Theme.S(DsMetrics.Dialog.BodySlack));
            }
        }

        private Color KindColor
        {
            get
            {
                switch (kind)
                {
                    case DlgKind.Danger: return DsPalette.Ink(DsTone.Danger);
                    case DlgKind.Warn: return DsPalette.Alert;
                    case DlgKind.Success: return DsPalette.Ink(DsTone.Success);
                    default: return DsPalette.Ink(DsTone.Accent);
                }
            }
        }

        private string KindTag
        {
            get
            {
                switch (kind)
                {
                    case DlgKind.Danger: return Lang.T("dlg.tag.danger");
                    case DlgKind.Warn: return Lang.T("dlg.tag.warn");
                    case DlgKind.Success: return Lang.T("dlg.tag.done");
                    default: return Lang.T("dlg.tag.info");
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = KindColor;
            var full = new Rectangle(0, 0, Width, Height);

            using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, full);

            int barH = Theme.S(DsMetrics.Dialog.BarHeight);
            using (var bar = new SolidBrush(accent))
                g.FillRectangle(bar, 0, 0, Width, barH);
            using (var glow = new LinearGradientBrush(
                DsPaint.GradientRect(new Rectangle(0, 0, Width, barH)),
                Color.FromArgb(0, 255, 255, 255), Color.FromArgb(150, 255, 255, 255),
                LinearGradientMode.Horizontal))
            {
                int bw = Theme.S(DsMetrics.Dialog.SweepBand);
                var band = new Rectangle((int)(sweep * (Width + bw)) - bw, 0, bw, barH);
                Region old = g.Clip;
                g.SetClip(new Rectangle(0, 0, Width, barH), CombineMode.Replace);
                g.FillRectangle(glow, band);
                g.Clip = old;
            }

            int skew = Theme.S(DsMetrics.Dialog.TagSkew);
            int tagW = Theme.S(DsMetrics.Dialog.TagWidth);
            var tagPts = new[]
            {
                new Point(Theme.S(PadX) + skew, Theme.S(TagTop)),
                new Point(Theme.S(PadX) + tagW + skew, Theme.S(TagTop)),
                new Point(Theme.S(PadX) + tagW - skew, Theme.S(TagTop + TagHeight)),
                new Point(Theme.S(PadX) - skew, Theme.S(TagTop + TagHeight))
            };
            using (var tagFill = new SolidBrush(Color.FromArgb(38, accent)))
                g.FillPolygon(tagFill, tagPts);
            using (var tagPen = new Pen(Color.FromArgb(120, accent), DsSpace.Line(1)))
                g.DrawPolygon(tagPen, tagPts);
            Font tagFont = DsType.Font(DsText.Tag, true);
            using (var tb = new SolidBrush(accent))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(KindTag, tagFont, tb,
                    new RectangleF(Theme.S(PadX), Theme.S(TagTop), tagW, Theme.S(TagHeight)), fmt);

            Font titleFont = DsType.Font(DsText.Heading, true);
            using (var tb = new SolidBrush(Theme.Fg))
                g.DrawString(title, titleFont, tb, Theme.S(PadX) - Theme.S(2), Theme.S(TitleTop));

            Font bodyFont = DsType.Font(DsText.BodyLarge);
            using (var bb = new SolidBrush(Theme.Dim))
                if (!bodyRect.IsEmpty) g.DrawString(body, bodyFont, bb, bodyRect);

            int lineY = ClientSize.Height - Theme.S(BottomPad) - Theme.S(BtnH) - Theme.S(DsMetrics.Dialog.SeparatorRise);
            using (var sep = new Pen(Theme.Stroke))
                g.DrawLine(sep, Theme.S(PadX), lineY, Width - Theme.S(PadX), lineY);

            using (var edge = new Pen(Theme.StrokeHi))
                g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        }

        private static DialogResult Run(IWin32Window owner, string title, string body,
            DlgKind kind, string okText, string cancelText)
        {
            using (var dlg = new PaviseDialog(title, body, kind, okText, cancelText, null, DlgW))
            {
                var form = owner as Form;
                if (form == null || !form.Visible) dlg.StartPosition = FormStartPosition.CenterScreen;
                return owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            }
        }

        public static void Info(IWin32Window owner, string title, string body)
        {
            Run(owner, title, body, DlgKind.Info, null, null);
        }

        public static void Warn(IWin32Window owner, string title, string body)
        {
            Run(owner, title, body, DlgKind.Warn, null, null);
        }

        public static void Error(IWin32Window owner, string title, string body)
        {
            Run(owner, title, body, DlgKind.Danger, null, null);
        }

        public static void Success(IWin32Window owner, string title, string body)
        {
            Run(owner, title, body, DlgKind.Success, null, null);
        }

        public static void Show(IWin32Window owner, string title, string body,
            Control content, int width)
        {
            using (var dlg = new PaviseDialog(title, body, DlgKind.Info, null, null, content, width))
            {
                var form = owner as Form;
                if (form == null || !form.Visible) dlg.StartPosition = FormStartPosition.CenterScreen;
                if (owner != null) dlg.ShowDialog(owner); else dlg.ShowDialog();
            }
        }

        public static bool Confirm(IWin32Window owner, string title, string body, DlgKind kind)
        {
            return Run(owner, title, body, kind, Lang.T("dlg.confirm"), Lang.T("dlg.cancel"))
                == DialogResult.OK;
        }

        public static bool Confirm(IWin32Window owner, string title, string body,
            DlgKind kind, Control content, int width)
        {
            using (var dlg = new PaviseDialog(title, body, kind,
                Lang.T("dlg.confirm"), Lang.T("dlg.cancel"), content, width))
            {
                var form = owner as Form;
                if (form == null || !form.Visible) dlg.StartPosition = FormStartPosition.CenterScreen;
                return (owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == DialogResult.OK;
            }
        }

        public static string Prompt(IWin32Window owner, string title, string body, string initial)
        {
            TextBox box = Theme.MakeTextBox(0, 0, Theme.S(DlgW) - Theme.S(PadX) * 2);
            box.Text = initial ?? "";
            using (var dlg = new PaviseDialog(title, body, DlgKind.Info,
                Lang.T("dlg.confirm"), Lang.T("dlg.cancel"), box, DlgW))
            {
                var form = owner as Form;
                if (form == null || !form.Visible) dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.Shown += delegate { box.Focus(); box.SelectAll(); };
                DialogResult result = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
                return result == DialogResult.OK ? box.Text.Trim() : null;
            }
        }
    }
}
