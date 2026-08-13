// @author bdth 2074055628@qq.com
// 文件用途 标题栏设置搜索 按钮与结果弹层 跨页面定位设置卡片

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class SearchHit
    {
        public SettingCard Card;
        public int PageId;
        public string PageName;
    }

    internal sealed class SearchButton : FxControl
    {
        public Action Clicked;

        public SearchButton()
        {
            Bg = Theme.Bg;
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            AccessibleName = Lang.T("search.name");
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Clicked != null) Clicked();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; FillBg(g); g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = hover.Value;
            Rectangle body = new Rectangle(0, Theme.S(2), Width - 1, Height - Theme.S(5));
            int cut = Theme.S(9);
            using (GraphicsPath p = Theme.TechPath(body, cut))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, h))) g.FillPath(b, p);
                using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.30f + h * 0.5f))) g.DrawPath(pen, p);
            }
            using (var edge = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
                g.DrawLine(edge, 0, Theme.S(13), 0, Height - Theme.S(14));
            using (var corner = new Pen(Col.Alpha(Theme.Accent, (int)(110 + 110 * h)), Math.Max(1f, Theme.S(1))))
                g.DrawLine(corner, body.Right - cut, body.Top, body.Right, body.Top + cut);
            int ic = Theme.S(17);
            Glyphs.Draw(g, "search", new Rectangle((Width - ic) / 2, (Height - ic) / 2, ic, ic),
                Col.Lerp(Theme.Dim, Theme.Accent, 0.35f + h * 0.65f));
        }
    }

    internal sealed class SearchFlyout : RoundPanel
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        private const int EM_SETCUEBANNER = 0x1501;

        private readonly TextBox input;
        private readonly ListBox list;
        private readonly List<SearchHit> hits = new List<SearchHit>();
        private bool inputFocused;
        public Func<string, List<SearchHit>> Query;
        public Action<SearchHit> Chosen;
        public Action Dismiss;

        private static int FieldY { get { return Theme.S(76); } }
        private static int FieldH { get { return Theme.S(36); } }

        public SearchFlyout()
        {
            Fill = Theme.Card; Border = Theme.StrokeHi; BackColor = Theme.Bg; Radius = Theme.S(14); AccentEdge = true;

            input = new TextBox();
            input.BorderStyle = BorderStyle.None;
            input.BackColor = Theme.Inset;
            input.ForeColor = Theme.Fg;
            input.Font = Theme.UI(10f, false);
            input.TextChanged += delegate { RefreshHits(); };
            input.KeyDown += OnInputKeyDown;
            input.GotFocus += delegate { inputFocused = true; Invalidate(); };
            input.LostFocus += delegate { inputFocused = false; Invalidate(); CloseWhenFocusLeaves(); };
            input.SetBounds(Theme.S(54), 0, Theme.S(316), Theme.S(24));
            input.Top = FieldY + (FieldH - input.Height) / 2;
            Native.Dark(input);

            list = new TechListBox();
            list.SetBounds(Theme.S(10), Theme.S(126), Theme.S(376), Theme.S(296));
            Theme.StyleList(list, false);
            list.BackColor = Theme.Card;
            list.ItemHeight = Theme.S(44);
            list.DrawItem += DrawHit;
            list.Click += delegate { ChooseIndex(list.SelectedIndex); };
            list.KeyDown += OnInputKeyDown;
            list.LostFocus += delegate { CloseWhenFocusLeaves(); };

            Controls.AddRange(new Control[] { input, list });
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SendMessage(input.Handle, EM_SETCUEBANNER, (IntPtr)1, Lang.T("search.hint"));
        }

        public void Open()
        {
            input.Text = "";
            RefreshHits();
            input.Focus();
        }

        public void SetQuery(string text)
        {
            input.Text = text ?? "";
        }

        private void CloseWhenFocusLeaves()
        {
            if (!Visible || !IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate
            {
                if (Visible && !ContainsFocus && Dismiss != null) Dismiss();
            });
        }

        private void OnInputKeyDown(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (Dismiss != null) Dismiss();
                e.Handled = true; e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                ChooseIndex(list.SelectedIndex >= 0 ? list.SelectedIndex : 0);
                e.Handled = true; e.SuppressKeyPress = true;
            }
            else if (s == input && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up))
            {
                if (list.Items.Count > 0)
                {
                    int next = list.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1);
                    if (next >= 0 && next < list.Items.Count) list.SelectedIndex = next;
                }
                e.Handled = true; e.SuppressKeyPress = true;
            }
        }

        private void ChooseIndex(int index)
        {
            if (index < 0 || index >= hits.Count) return;
            if (Chosen != null) Chosen(hits[index]);
        }

        private void RefreshHits()
        {
            hits.Clear();
            if (Query != null) hits.AddRange(Query(input.Text) ?? new List<SearchHit>());
            list.BeginUpdate();
            list.Items.Clear();
            foreach (SearchHit hit in hits) list.Items.Add(hit.Card.Title);
            list.EndUpdate();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, Theme.S(18), Theme.S(17), Theme.S(3), Theme.S(8));
            TextRenderer.DrawText(g, "PAVISE  //  SEARCH", Theme.Mono(6.75f),
                new Rectangle(Theme.S(28), Theme.S(13), Theme.S(190), Theme.S(15)),
                Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            string tally = hits.Count > 0 ? hits.Count.ToString("00") + " // HIT" : "-- // HIT";
            TextRenderer.DrawText(g, tally, Theme.Mono(6.75f),
                new Rectangle(Width - Theme.S(110), Theme.S(13), Theme.S(94), Theme.S(15)),
                hits.Count > 0 ? Theme.Accent : Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(g, Lang.T("search.name"), Theme.UI(11f, true),
                new Rectangle(Theme.S(18), Theme.S(32), Theme.S(200), Theme.S(25)),
                Theme.Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Lang.T("search.sub"), Theme.UI(7.8f, false),
                new Rectangle(Theme.S(19), Theme.S(56), Theme.S(360), Theme.S(16)),
                Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            float focus = inputFocused ? 1f : 0f;
            Rectangle field = new Rectangle(Theme.S(16), FieldY, Width - Theme.S(33), FieldH);
            int cut = Theme.S(8);
            using (GraphicsPath p = Theme.TechPath(field, cut))
            {
                using (var b = new SolidBrush(Theme.Inset)) g.FillPath(b, p);
                g.SetClip(p);
                using (var hatch = new Pen(Col.Alpha(Theme.Accent, 12), Math.Max(1f, Theme.S(1))))
                    for (float sx = field.Left - field.Height; sx < field.Right; sx += Theme.S(7))
                        g.DrawLine(hatch, sx, field.Bottom, sx + field.Height, field.Top);
                g.ResetClip();
                using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.25f + focus * 0.6f))) g.DrawPath(pen, p);
            }
            using (var edge = new Pen(Col.Alpha(Theme.Accent, inputFocused ? 255 : 150), Math.Max(1f, Theme.S(2))))
                g.DrawLine(edge, field.Left, field.Top + Theme.S(8), field.Left, field.Bottom - Theme.S(8));
            using (var corner = new Pen(Col.Alpha(Theme.Accent, inputFocused ? 220 : 110), Math.Max(1f, Theme.S(1))))
                g.DrawLine(corner, field.Right - cut, field.Top, field.Right, field.Top + cut);
            int ic = Theme.S(15);
            Glyphs.Draw(g, "search", new Rectangle(field.Left + Theme.S(14), field.Top + (field.Height - ic) / 2, ic, ic),
                Col.Lerp(Theme.Dim, Theme.Accent, 0.3f + focus * 0.7f));

            if (hits.Count == 0 && input.Text.Trim().Length > 0)
            {
                Rectangle zone = new Rectangle(Theme.S(10), Theme.S(150), Width - Theme.S(20), Theme.S(60));
                TextRenderer.DrawText(g, "NO MATCH", Theme.Mono(8f), new Rectangle(zone.X, zone.Y, zone.Width, Theme.S(20)),
                    Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, Lang.T("search.empty"), Theme.UI(8.5f, false),
                    new Rectangle(zone.X, zone.Y + Theme.S(22), zone.Width, Theme.S(20)),
                    Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private void DrawHit(object s, DrawItemEventArgs e)
        {
            using (var bg = new SolidBrush(list.BackColor)) e.Graphics.FillRectangle(bg, e.Bounds);
            if (e.Index < 0 || e.Index >= hits.Count) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            bool hot = e.Index == Theme.HoverIndex(list);
            SearchHit hit = hits[e.Index];

            var r = Rectangle.Inflate(e.Bounds, -Theme.S(4), -Theme.S(3));
            if (sel || hot)
            {
                using (GraphicsPath p = Theme.TechPath(r, Theme.S(7)))
                {
                    Color fill = sel ? Col.Lerp(Theme.Card, Theme.Accent, 0.12f)
                        : Col.Lerp(Theme.Card, Theme.CardHover, 0.8f);
                    using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                    using (var pen = new Pen(sel ? Col.Alpha(Theme.Accent, 200) : Theme.StrokeHi)) g.DrawPath(pen, p);
                }
                if (sel)
                    using (var edge = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
                        g.DrawLine(edge, r.Left, r.Top + Theme.S(7), r.Left, r.Bottom - Theme.S(7));
            }

            TextRenderer.DrawText(g, (e.Index + 1).ToString("00"), Theme.Mono(6.5f),
                new Rectangle(r.Right - Theme.S(26), r.Y, Theme.S(22), r.Height),
                sel ? Theme.Accent : Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            int pageW = TextRenderer.MeasureText(g, hit.PageName, Theme.UI(7.8f, false)).Width + Theme.S(6);
            TextRenderer.DrawText(g, hit.PageName, Theme.UI(7.8f, false),
                new Rectangle(r.Right - Theme.S(30) - pageW, r.Y, pageW, r.Height),
                sel ? Theme.Dim : Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(g, hit.Card.Title, Theme.UI(9.25f, sel),
                new Rectangle(r.X + Theme.S(14), r.Y, r.Width - pageW - Theme.S(48), r.Height),
                Theme.Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

}
