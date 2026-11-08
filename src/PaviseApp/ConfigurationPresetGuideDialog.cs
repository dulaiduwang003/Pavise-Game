using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ConfigurationPresetGuideDialog : Form
    {
        internal readonly Button AuthorButton, ExtremeButton;
        internal event Action<ConfigurationPresetKind> ApplyRequested;
        internal bool Busy { get; private set; }

        private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        private const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        private readonly GuideCard author, extreme;
        private readonly DBPanel body;
        private readonly Label title, intro;
        private readonly TextBox status;
        private readonly LibraryDialogButton close;
        private ConfigurationPresetKind? pending;
        private string issue;
        private bool arranging, ended;
        private int desiredHeight;

        internal ConfigurationPresetGuideDialog(ConfigurationPresetKind? current)
        {
            Name = "ConfigurationPresetGuide";
            Text = Lang.T("configpreset.guide");
            AccessibleName = Text;
            AccessibleDescription = Lang.T("configpreset.guide.intro");
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = Theme.UI(9f, false);
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            title = MakeLabel(Text, "PresetGuideTitle", Theme.UI(14f, true), Theme.Fg);
            intro = MakeLabel(Lang.T("configpreset.guide.intro"), "PresetGuideIntro", Theme.UI(9f, false), Theme.Dim);
            author = new GuideCard(ConfigurationPresetKind.Default, Lang.T("configpreset.default"),
                Lang.T("configpreset.guide.author.body"), true, current == ConfigurationPresetKind.Default)
            { Name = "PresetGuideAuthor", TabIndex = 0 };
            extreme = new GuideCard(ConfigurationPresetKind.Extreme, Lang.T("configpreset.extreme"),
                Lang.T("configpreset.guide.extreme.body"), false, current == ConfigurationPresetKind.Extreme)
            { Name = "PresetGuideExtreme", TabIndex = 1 };
            AuthorButton = author;
            ExtremeButton = extreme;
            body = new DBPanel { Name = "PresetGuideBody", AutoScroll = true, BackColor = Theme.Bg, TabIndex = 0 };
            body.Controls.AddRange(new Control[] { author, extreme });
            status = new TextBox
            {
                Name = "PresetGuideStatus", ReadOnly = true, Multiline = true, WordWrap = true,
                ScrollBars = ScrollBars.None, BorderStyle = BorderStyle.None, BackColor = Theme.Bg,
                ForeColor = Theme.Dim, Font = Theme.UI(9f, false), TabStop = false, TabIndex = 1,
                MaxLength = int.MaxValue, AccessibleName = Lang.T("configpreset.result")
            };
            close = new LibraryDialogButton(Lang.T("configpreset.guide.close"), false)
            { Name = "PresetGuideClose", DialogResult = DialogResult.Cancel, TabIndex = 2 };
            author.Click += delegate { Choose(ConfigurationPresetKind.Default); };
            extreme.Click += delegate { Choose(ConfigurationPresetKind.Extreme); };
            close.Click += delegate { Close(); };
            title.MouseDown += DragMove;
            intro.MouseDown += DragMove;
            body.MouseDown += DragMove;
            MouseDown += DragMove;
            Controls.AddRange(new Control[] { title, intro, body, status, close });
            CancelButton = close;
            Native.Dark(body);
            Native.Dark(status);
            Refit(Screen.FromPoint(Cursor.Position).WorkingArea, false);
        }

        internal void SetBusy(bool busy)
        {
            if (IsDisposed) return;
            Busy = busy;
            if (busy) issue = null;
            author.Enabled = !busy;
            extreme.Enabled = !busy;
            author.Pending = busy && pending == ConfigurationPresetKind.Default;
            extreme.Pending = busy && pending == ConfigurationPresetKind.Extreme;
            UpdateStatus();
        }

        internal void ShowIssue(string text)
        {
            if (IsDisposed) return;
            SetBusy(false);
            string detail = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            issue = (detail.Length == 0 ? Lang.T("configpreset.result") : detail).Replace("\n", "\r\n");
            UpdateStatus();
            Refit(Screen.FromControl(this).WorkingArea, Visible);
            GuideCard retry = pending == ConfigurationPresetKind.Extreme ? extreme : author;
            Control active = ActiveControl;
            if (Form.ActiveForm == this && (active == null || active == body || !active.CanFocus)) retry.Focus();
        }

        private void Choose(ConfigurationPresetKind kind)
        {
            // 忙碌状态拦截排队的重复点击，由宿主在操作结束后解除。
            if (ended || Busy || IsDisposed) return;
            Action<ConfigurationPresetKind> handler = ApplyRequested;
            if (handler == null) return;
            pending = kind;
            SetBusy(true);
            try { handler(kind); }
            catch (Exception ex) { if (!ended) ShowIssue(ex.Message); }
        }

        private void UpdateStatus()
        {
            string textValue = Busy ? Lang.T("configpreset.guide.working") : issue ?? "";
            if (status.Text != textValue)
            {
                status.Text = textValue;
                status.SelectionStart = 0;
                status.SelectionLength = 0;
            }
            status.ForeColor = issue != null && !Busy ? Theme.Warning : Theme.Dim;
            status.TabStop = issue != null && !Busy;
            status.AccessibleDescription = textValue;
            Arrange();
        }

        private static Label MakeLabel(string text, string name, Font font, Color color)
        {
            return new Label { Name = name, Text = text, Font = font, ForeColor = color,
                BackColor = Color.Transparent, AutoSize = false, UseMnemonic = false };
        }

        private void Refit(Rectangle area, bool reposition)
        {
            int width = Math.Max(1, Math.Min(Theme.S(660), area.Width - Theme.S(24)));
            int limit = Math.Max(1, Math.Min(area.Height - Theme.S(24), (int)Math.Floor(area.Height * .9)));
            int oldHeight = Height;
            if (ClientSize.Width != width || !reposition) ClientSize = new Size(width, limit);
            Arrange();
            ClientSize = new Size(width, Math.Min(limit, Math.Max(1, desiredHeight)));
            if (!reposition) return;
            int top = Top - (Height - oldHeight) / 2;
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)),
                Math.Max(area.Top, Math.Min(top, area.Bottom - Height)));
        }

        protected override void OnLoad(EventArgs e)
        {
            Refit(Owner == null ? Screen.FromPoint(Cursor.Position).WorkingArea : Screen.FromControl(Owner).WorkingArea, false);
            base.OnLoad(e);
            // Start on Close so a held Enter from the entry button cannot apply a preset.
            ActiveControl = close;
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            Arrange();
        }

        private void Arrange()
        {
            if (arranging || close == null) return;
            arranging = true;
            try
            {
                int width = ClientSize.Width, height = ClientSize.Height;
                int margin = Math.Min(Theme.S(20), Math.Max(Theme.S(6), width / 20));
                int inner = Math.Max(1, width - 2 * margin);
                title.SetBounds(margin, Theme.S(16), inner, Measure(title.Text, title.Font, inner));
                intro.SetBounds(margin, title.Bottom + Theme.S(3), inner, Measure(intro.Text, intro.Font, inner));
                int headerBottom = intro.Bottom + Theme.S(14);

                int buttonHeight = Math.Max(1, Math.Min(Theme.S(34), height / 6));
                int closeWidth = Math.Min(inner, Math.Max(Theme.S(92), TextWidth(close.Text, close.Font) + Theme.S(36)));
                int closeTop = Math.Max(0, height - margin - buttonHeight);
                close.SetBounds(width - margin - closeWidth, closeTop, closeWidth, buttonHeight);
                int gap = Theme.S(12);
                int oneLine = StatusHeight("Ag", inner, false);
                int beside = inner - closeWidth - gap;
                int footerTop, footerSpan;
                if (issue == null && beside >= Theme.S(80) && StatusHeight(status.Text, beside, false) <= oneLine)
                {
                    SetStatusScroll(false);
                    status.SetBounds(margin, closeTop + Math.Max(0, (buttonHeight - oneLine) / 2), beside, oneLine);
                    footerTop = closeTop;
                    footerSpan = buttonHeight + margin;
                }
                else
                {
                    int natural = StatusHeight(status.Text, inner, false);
                    int cap = Math.Max(oneLine, Math.Min(Theme.S(120), (closeTop - headerBottom) / 3));
                    bool scroll = natural > cap;
                    SetStatusScroll(scroll);
                    if (scroll) natural = StatusHeight(status.Text, inner, true);
                    int statusHeight = Math.Max(oneLine, Math.Min(natural, cap));
                    status.SetBounds(margin, Math.Max(0, closeTop - Theme.S(10) - statusHeight), inner, statusHeight);
                    footerTop = status.Top;
                    footerSpan = Math.Max(oneLine, Math.Min(natural, Theme.S(120))) + Theme.S(10) + buttonHeight + margin;
                }

                int bodyTop = headerBottom;
                body.SetBounds(margin, bodyTop, inner, Math.Max(1, footerTop - Theme.S(14) - bodyTop));
                Point scrollPosition = new Point(0, -body.AutoScrollPosition.Y);
                body.AutoScrollPosition = Point.Empty;
                int cards = LayoutCards(inner, false);
                int available = inner;
                if (cards > body.Height)
                    available = Math.Max(1, inner - SystemInformation.VerticalScrollBarWidth - Theme.S(4));
                int total = LayoutCards(available, true);
                body.AutoScrollMinSize = new Size(0, total);
                body.AutoScrollPosition = scrollPosition;
                desiredHeight = bodyTop + cards + Theme.S(14) + footerSpan;
                Invalidate();
            }
            finally { arranging = false; }
        }

        private int LayoutCards(int width, bool apply)
        {
            int gap = Theme.S(12);
            int column = (width - gap) / 2;
            if (column >= Theme.S(230))
            {
                int cardHeight = Math.Max(author.PreferredHeightFor(column), extreme.PreferredHeightFor(width - column - gap));
                if (apply)
                {
                    author.SetBounds(0, 0, column, cardHeight);
                    extreme.SetBounds(column + gap, 0, width - column - gap, cardHeight);
                }
                return cardHeight;
            }
            int first = author.PreferredHeightFor(width), second = extreme.PreferredHeightFor(width);
            if (apply)
            {
                author.SetBounds(0, 0, width, first);
                extreme.SetBounds(0, first + gap, width, second);
            }
            return first + gap + second;
        }

        private void SetStatusScroll(bool scroll)
        {
            ScrollBars wanted = scroll ? ScrollBars.Vertical : ScrollBars.None;
            if (status.ScrollBars != wanted) status.ScrollBars = wanted;
        }

        private int StatusHeight(string text, int width, bool scroll)
        {
            int room = Math.Max(1, width - Theme.S(6) - (scroll ? SystemInformation.VerticalScrollBarWidth : 0));
            return TextRenderer.MeasureText(text.Length == 0 ? "Ag" : text, status.Font, new Size(room, int.MaxValue),
                Wrap | TextFormatFlags.TextBoxControl).Height + Theme.S(4);
        }

        private static int Measure(string text, Font font, int width)
        {
            return TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue), Wrap).Height + Theme.S(2);
        }

        private static int TextWidth(string text, Font font)
        { return TextRenderer.MeasureText(text, font, Size.Empty, Line).Width; }

        private static int LineHeight(Font font)
        { return TextRenderer.MeasureText("Ag", font, Size.Empty, Line).Height; }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, 161, (IntPtr)2, IntPtr.Zero);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        { ended = true; base.OnFormClosed(e); }

        protected override void OnPaintBackground(PaintEventArgs e)
        { e.Graphics.Clear(Theme.Bg); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var outline = new Pen(Theme.StrokeHi))
                e.Graphics.DrawRectangle(outline, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            using (var divider = new Pen(Theme.Stroke))
            {
                int y = body.Top - Theme.S(7);
                e.Graphics.DrawLine(divider, body.Left, y, body.Right - 1, y);
            }
            using (var accent = new Pen(Theme.Accent, Math.Max(1, Theme.S(2))))
            {
                e.Graphics.DrawLine(accent, 1, 1, Math.Min(Width - 1, Theme.S(130)), 1);
                e.Graphics.DrawLine(accent, 1, Math.Max(1, Height - Theme.S(15)), Math.Min(Width - 1, Theme.S(15)), Height - 1);
            }
        }

        private sealed class GuideCard : Button
        {
            private readonly ConfigurationPresetKind kind;
            private readonly string heading, detail;
            private readonly string[] badges;
            private readonly bool current;
            private bool hover, pending;

            private sealed class Plan
            {
                internal Rectangle[] Badges;
                internal Rectangle Heading, Detail, Action, Arrow;
                internal int Height;
            }

            internal GuideCard(ConfigurationPresetKind kind, string heading, string detail, bool recommended, bool current)
            {
                this.kind = kind;
                this.heading = heading;
                this.detail = detail;
                this.current = current;
                string primaryBadge = Lang.T(recommended ? "configpreset.guide.recommended" : "configpreset.guide.aggressive");
                badges = current ? new[] { primaryBadge, Lang.T("configpreset.guide.current") } : new[] { primaryBadge };
                Text = heading;
                UseMnemonic = false;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                TabStop = true;
                Cursor = Cursors.Hand;
                AccessibleName = heading;
                AccessibleDescription = (badges.Length == 0 ? "" : string.Join(" · ", badges) + "\n") + detail + "\n" + Lang.T("configpreset.guide.action");
                SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }

            internal bool Pending
            {
                get { return pending; }
                set { if (pending == value) return; pending = value; Invalidate(); }
            }

            private Color Tone { get { return kind == ConfigurationPresetKind.Extreme ? Theme.Warning : Theme.Accent; } }

            internal int PreferredHeightFor(int width) { return Compose(width, 0).Height; }

            private Plan Compose(int width, int height)
            {
                int pad = Theme.S(16);
                int x = pad, inner = Math.Max(1, width - 2 * pad), y = pad;
                Plan plan = new Plan { Badges = new Rectangle[badges.Length] };
                if (badges.Length > 0)
                {
                    Font badgeFont = Theme.UI(8.5f, true);
                    int badgeHeight = LineHeight(badgeFont) + Theme.S(6), bx = x, by = y;
                    for (int i = 0; i < badges.Length; i++)
                    {
                        int badgeWidth = Math.Min(inner, TextWidth(badges[i], badgeFont) + Theme.S(16));
                        if (bx > x && bx + badgeWidth > x + inner) { bx = x; by += badgeHeight + Theme.S(6); }
                        plan.Badges[i] = new Rectangle(bx, by, badgeWidth, badgeHeight);
                        bx += badgeWidth + Theme.S(6);
                    }
                    y = by + badgeHeight + Theme.S(10);
                }
                int headingHeight = Measure(heading, Theme.UI(12.5f, true), inner);
                plan.Heading = new Rectangle(x, y, inner, headingHeight);
                y += headingHeight + Theme.S(4);
                int detailHeight = Measure(detail, Theme.UI(9f, false), inner);
                plan.Detail = new Rectangle(x, y, inner, detailHeight);
                y += detailHeight + Theme.S(14);
                Font actionFont = Theme.UI(9f, true);
                int arrow = TextWidth("→", actionFont);
                int actionWidth = Math.Max(1, inner - arrow - Theme.S(8));
                int actionHeight = Math.Max(Measure(Lang.T("configpreset.guide.action"), actionFont, actionWidth),
                    Measure(Lang.T("configpreset.guide.working"), actionFont, actionWidth));
                plan.Height = y + actionHeight + pad;
                int actionTop = height > plan.Height ? height - pad - actionHeight : y;
                plan.Action = new Rectangle(x, actionTop, actionWidth, actionHeight);
                plan.Arrow = new Rectangle(x + inner - arrow, actionTop, arrow, LineHeight(actionFont));
                return plan;
            }

            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
            protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); hover = false; Invalidate(); }
            protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
            protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush back = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Bg))
                    graphics.FillRectangle(back, ClientRectangle);
                bool live = Enabled;
                bool hot = live && (hover || Focused);
                Color tone = Tone;
                Rectangle frame = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                Color edge = pending || hot ? tone : current ? Theme.StrokeHi : Theme.Stroke;
                using (GraphicsPath path = Theme.TechPath(frame, Theme.S(10)))
                {
                    using (SolidBrush fill = new SolidBrush(hot ? Theme.CardHover : Theme.Card))
                        graphics.FillPath(fill, path);
                    using (Pen pen = new Pen(edge, pending || hot ? Math.Max(2, Theme.S(2)) : 1))
                        graphics.DrawPath(pen, path);
                }
                using (SolidBrush mark = new SolidBrush(live || pending ? tone : Theme.Faint))
                    graphics.FillRectangle(mark, 1, 1, Math.Max(1, Math.Min(Width - 2, Theme.S(56))), Math.Max(2, Theme.S(3)));

                Plan plan = Compose(Width, Height);
                Font badgeFont = Theme.UI(8.5f, true);
                for (int i = 0; i < badges.Length; i++)
                {
                    Rectangle badge = plan.Badges[i];
                    bool filled = i == 0;
                    Color badgeTone = !live ? Theme.Faint : filled ? tone : Theme.Green;
                    using (GraphicsPath shape = Theme.TechPath(new Rectangle(badge.X, badge.Y, badge.Width - 1, badge.Height - 1), Theme.S(4)))
                    {
                        if (filled) using (SolidBrush brush = new SolidBrush(badgeTone)) graphics.FillPath(brush, shape);
                        else using (Pen pen = new Pen(badgeTone)) graphics.DrawPath(pen, shape);
                    }
                    TextRenderer.DrawText(graphics, badges[i], badgeFont, badge, filled && live ? Theme.OnAccent : badgeTone,
                        Line | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                TextRenderer.DrawText(graphics, heading, Theme.UI(12.5f, true), plan.Heading, live ? Theme.Fg : Theme.Dim, Wrap);
                TextRenderer.DrawText(graphics, detail, Theme.UI(9f, false), plan.Detail, live ? Theme.Dim : Theme.Faint, Wrap);
                Font actionFont = Theme.UI(9f, true);
                Color actionColor = pending || live ? tone : Theme.Faint;
                TextRenderer.DrawText(graphics, pending ? Lang.T("configpreset.guide.working") : Lang.T("configpreset.guide.action"),
                    actionFont, plan.Action, actionColor, Wrap);
                if (!pending)
                    TextRenderer.DrawText(graphics, "→", actionFont, plan.Arrow, actionColor, Line | TextFormatFlags.Right);
                if (Focused && ShowFocusCues)
                    ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(frame, -Theme.S(5), -Theme.S(5)), Theme.Fg, hot ? Theme.CardHover : Theme.Card);
            }
        }
    }
}
