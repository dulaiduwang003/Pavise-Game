// @author bdth 2074055628@qq.com
// 文件用途 游戏库里 WeGame 游戏卡片下方的脱壳区 一行 借壳启动开关 状态 脱壳启动与立即净化
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class WeGameCardPanel : GameCardExtension
    {
        private readonly WeGameShellService service;
        private readonly GameProfile profile;
        private readonly PillButton btnLaunch, btnClean;
        private readonly Toggle swAuto;
        private readonly System.Windows.Forms.Timer tick;
        private readonly ToolTip tips = new ToolTip();
        private Color surface = Theme.Card;
        private WeGameShellSnapshot last;
        private int uiBusy;

        public WeGameCardPanel(WeGameShellService svc, GameProfile owner)
        {
            service = svc;
            profile = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false; BackColor = surface;
            AccessibleRole = AccessibleRole.Grouping; AccessibleName = Lang.T("wg.card.title");
            tips.InitialDelay = 500; tips.ReshowDelay = 120; tips.AutoPopDelay = 18000; tips.ShowAlways = true;

            btnLaunch = MakeButton(Lang.T("col.btn.launch"), BtnKind.Primary, delegate { RunAction(delegate { return service.LaunchWeGame(); }); });
            btnClean = MakeButton(Lang.T("col.btn.clean"), BtnKind.Normal, delegate { RunAction(delegate { return service.CleanNow(profile); }); });

            swAuto = new Toggle();
            swAuto.Size = new Size(Theme.S(42), Theme.S(22)); swAuto.Bg = surface;
            swAuto.SetSilently(service.IsAutoEnabled(profile != null ? profile.Id : null));
            swAuto.CheckedChanged += delegate { service.SetAutoEnabled(profile != null ? profile.Id : null, swAuto.Checked); RefreshState(); };
            swAuto.AccessibleName = Lang.T("col.cleanup");
            tips.SetToolTip(swAuto, Lang.T("wg.tip.auto"));
            Controls.Add(swAuto);

            tick = new System.Windows.Forms.Timer(); tick.Interval = 2000;
            tick.Tick += delegate { if (Visible && FindForm() != null) RefreshState(); };
            service.Changed += OnServiceChanged;
            Disposed += delegate
            {
                service.Changed -= OnServiceChanged;
                try { tick.Stop(); tick.Dispose(); } catch { }
                try { tips.Dispose(); } catch { }
            };
            RefreshState();
        }

        public override int PreferredHeight { get { return Theme.S(52); } }

        public override void SetSurface(Color value)
        {
            surface = value; BackColor = value;
            foreach (Control c in Controls)
            {
                var fx = c as FxControl;
                if (fx != null) { fx.Bg = value; fx.Invalidate(); }
            }
            Invalidate();
        }

        private PillButton MakeButton(string text, BtnKind kind, EventHandler onClick)
        {
            var b = new PillButton(text, kind);
            b.Bg = surface; b.Font = Theme.UI(8.4f, false); b.Height = Theme.S(30);
            b.Width = TextRenderer.MeasureText(text, b.Font, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(30);
            b.Click += onClick;
            Controls.Add(b);
            return b;
        }

        private void SetButtonText(PillButton b, string text)
        {
            if (string.Equals(b.Text, text, StringComparison.Ordinal)) return;
            b.Text = text;
            int w = TextRenderer.MeasureText(text, b.Font, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(30);
            if (b.Width != w) { b.Width = w; PerformLayout(); }
            b.Invalidate();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { tick.Start(); RefreshState(); } else tick.Stop();
        }

        private static int LabelWidth(string text)
        {
            return TextRenderer.MeasureText(text, Theme.UI(8.2f, true), Size.Empty, TextFormatFlags.NoPadding).Width;
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (swAuto == null || btnClean == null) return;
            int w = ClientSize.Width, gap = Theme.S(8);
            int row = Theme.S(11);
            int x = w;
            foreach (PillButton b in new[] { btnClean, btnLaunch })
            {
                if (!b.Visible) continue;
                x -= b.Width; b.Location = new Point(x, row); x -= gap;
            }
            swAuto.Location = new Point(Theme.S(4) + LabelWidth(Lang.T("col.cleanup")) + Theme.S(8), Theme.S(15));
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(surface)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.Stroke)) g.DrawLine(p, 0, 0, Width, 0);
            const TextFormatFlags line = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            WeGameShellSnapshot s = last;
            int cy = Theme.S(26);
            Font labFont = Theme.UI(8.2f, true);
            string label = Lang.T("col.cleanup");
            TextRenderer.DrawText(g, label, labFont,
                new Rectangle(Theme.S(4), cy - Theme.S(10), LabelWidth(label) + 2, Theme.S(20)),
                swAuto.Enabled ? Theme.Fg : Theme.Faint, line);

            int rightLimit = Width;
            foreach (PillButton b in new[] { btnLaunch, btnClean })
                if (b.Visible && b.Left < rightLimit) rightLimit = b.Left;
            rightLimit -= Theme.S(14);

            string state; Color stateColor;
            StateOf(s, out state, out stateColor);
            int x = swAuto.Right + Theme.S(18);
            int dot = Theme.S(7);
            using (var b = new SolidBrush(stateColor)) g.FillEllipse(b, x, cy - dot / 2, dot, dot);
            x += Theme.S(13);
            Font stateFont = Theme.UI(8.6f, true);
            int stateW = TextRenderer.MeasureText(state, stateFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            var stateRect = new Rectangle(x, cy - Theme.S(10), Math.Min(stateW, Math.Max(1, rightLimit - x)), Theme.S(20));
            TextRenderer.DrawText(g, state, stateFont, stateRect, stateColor, line);
            x = stateRect.Right + Theme.S(18);

            if (s != null)
            {
                Font capFont = Theme.UI(7.4f, false), valFont = Theme.UI(7.4f, true);
                string shell = s.ShellProcessCount > 0 ? s.ShellProcessCount.ToString() : Lang.T("col.proc.none");
                DrawCell(g, x, cy, rightLimit, capFont, valFont, Lang.T("wg.cell.shell"), shell,
                    s.ShellProcessCount > 0 ? Theme.Dim : Theme.Faint);
            }
        }

        private static int DrawCell(Graphics g, int x, int cy, int limit, Font capFont, Font valFont, string cap, string val, Color valColor)
        {
            const TextFormatFlags f = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int cw = TextRenderer.MeasureText(cap, capFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            int vw = TextRenderer.MeasureText(val, valFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (x + cw + Theme.S(5) + vw > limit) return limit;
            TextRenderer.DrawText(g, cap, capFont, new Rectangle(x, cy - Theme.S(9), cw + 2, Theme.S(18)), Theme.Faint, f);
            x += cw + Theme.S(5);
            TextRenderer.DrawText(g, val, valFont, new Rectangle(x, cy - Theme.S(9), vw + 2, Theme.S(18)), valColor, f);
            return x + vw + Theme.S(14);
        }

        private static void StateOf(WeGameShellSnapshot s, out string text, out Color color)
        {
            if (s == null) { text = Lang.T("wg.state.idle"); color = Theme.Dim; return; }
            if (s.Fused) { text = Lang.T("wg.state.fused"); color = Theme.Danger; return; }
            if (!s.AutoEnabled) { text = Lang.T("wg.state.off"); color = Theme.Faint; return; }
            if (s.SessionActive && s.CircuitOpen) { text = Lang.T("wg.state.circuit"); color = Theme.Danger; return; }
            if (s.SessionActive && s.CleanedThisSession) { text = Lang.F("wg.state.cleaned", s.CleanedCount.ToString()); color = Theme.Green; return; }
            if (s.SessionActive && s.SecondsUntilClean >= 0) { text = Lang.F("wg.state.armed", s.SecondsUntilClean.ToString()); color = Theme.Accent; return; }
            text = Lang.T("wg.state.idle"); color = Theme.Dim;
        }

        private void OnServiceChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) RefreshState(); }); }
            catch { }
        }

        private void RefreshState()
        {
            if (IsDisposed) return;
            WeGameShellSnapshot s;
            try { s = service.GetSnapshot(profile); }
            catch (Exception ex) { Logger.Log("WeGame 脱壳 读取状态失败 " + ex.Message); return; }
            last = s;
            swAuto.SetSilently(s.AutoEnabled);
            bool busy = Interlocked.CompareExchange(ref uiBusy, 0, 0) != 0;
            if (btnLaunch.Visible != s.WeGameFound) { btnLaunch.Visible = s.WeGameFound; PerformLayout(); }
            SetButtonText(btnLaunch, s.WeGameRunning ? Lang.T("lol.btn.wegamerunning") : Lang.T("col.btn.launch"));
            btnLaunch.Enabled = !busy && s.WeGameFound && !s.WeGameRunning;
            btnClean.Enabled = !busy && s.ShellProcessCount > 0;
            PerformLayout(); Invalidate();
        }

        private void RunAction(Func<bool> action)
        {
            if (action == null || Interlocked.CompareExchange(ref uiBusy, 1, 0) != 0) return;
            btnLaunch.Enabled = false; btnClean.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false; string error = "";
                try
                {
                    ok = action();
                    if (!ok) error = service.GetSnapshot(profile).LastError;
                }
                catch (Exception ex) { error = ex.Message; }
                Interlocked.Exchange(ref uiBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        RefreshState();
                        if (!ok && !string.IsNullOrEmpty(error)) PaviseDialog.Warn(FindForm(), App.DisplayName, error);
                    });
                }
                catch { }
            });
        }
    }
}
