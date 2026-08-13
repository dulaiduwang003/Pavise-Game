// @author bdth 2074055628@qq.com
// 文件用途 ROG 风格切角下拉选择器 展开列表复用应用统一的深色菜单渲染

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{

    internal sealed class MenuBadge
    {
        public readonly string Text;
        public readonly bool Strong;
        public MenuBadge(string text, bool strong) { Text = text; Strong = strong; }
    }

    internal sealed class TechMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = e.AffectedBounds;
            r.Width -= 1; r.Height -= 1;
            using (GraphicsPath path = Theme.TechPath(r, Theme.S(8)))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Stroke)) g.DrawPath(p, path);
            }
            using (var p = new Pen(Theme.Accent, Math.Max(1.6f, Theme.S(2))))
                g.DrawLine(p, r.Left + Theme.S(2), r.Top, r.Left + Theme.S(26), r.Top);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            var r = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
            using (var b = new SolidBrush(Col.Alpha(Theme.Accent, 40))) g.FillRectangle(b, r);
            using (var b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, new Rectangle(0, 0, Theme.S(3), r.Height));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            var badge = e.Item.Tag as MenuBadge;
            var r = new Rectangle(Theme.S(12), 0, e.Item.Width - Theme.S(18), e.Item.Height);

            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

            if (badge == null || string.IsNullOrEmpty(badge.Text))
            {
                TextRenderer.DrawText(g, e.Text, e.TextFont, r, e.TextColor, Line);
                return;
            }

            Size bs = TextRenderer.MeasureText(g, badge.Text, Theme.MonoFor(badge.Text, 7.0f));
            int bw = bs.Width + Theme.S(12);
            var pill = new Rectangle(r.Right - bw, (e.Item.Height - Theme.S(17)) / 2,
                bw, Theme.S(17));
            r.Width -= bw + Theme.S(8);
            TextRenderer.DrawText(g, e.Text, e.TextFont, r, e.TextColor, Line);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = badge.Strong ? Theme.Accent : Theme.Inset;
            Color edge = badge.Strong ? Col.Alpha(Theme.Accent, 235) : Theme.Stroke;
            Color ink = badge.Strong ? Theme.OnAccent : Theme.Faint;
            using (GraphicsPath path = Theme.TechPath(pill, Theme.S(4)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(edge)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, badge.Text, Theme.MonoFor(badge.Text, 7.0f), pill, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class TechSelect : Control
    {
        private string[] items = new string[0];
        private MenuBadge[] tags = new MenuBadge[0];
        private int idx;
        private bool hover;
        private bool open;
        private Motion lit;
        private Motion spin;
        private ContextMenuStrip menu;
        public Action<int> IndexChanged;
        public Action BeforeOpen;

        public TechSelect()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
            lit.Speed = 0.28f;
            spin.Speed = 0.32f;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UiClock.Frame += OnFrame;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UiClock.Frame -= OnFrame;
            base.OnHandleDestroyed(e);
        }

        private void OnFrame(object s, EventArgs e)
        {
            bool a = lit.Step();
            bool b = spin.Step();
            if (a || b) Invalidate();
        }

        private void SyncMotion()
        {
            lit.To(hover || open ? 1f : 0f);
            spin.To(open ? 1f : 0f);
            UiClock.Wake();
        }

        public void SetItems(string[] values)
        {
            SetItems(values, null);
        }

        public void SetItems(string[] values, MenuBadge[] badges)
        {
            items = values ?? new string[0];
            tags = badges ?? new MenuBadge[0];
            if (idx >= items.Length) idx = 0;
            Invalidate();
        }

        public int Index
        {
            get { return idx; }
            set
            {
                if (value < 0 || value >= items.Length || value == idx) return;
                idx = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true; SyncMotion(); Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false; SyncMotion(); Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && items.Length > 0) ShowList();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && menu != null) { try { menu.Dispose(); } catch { } menu = null; }
            base.Dispose(disposing);
        }

        private void ShowList()
        {
            Action prepare = BeforeOpen;
            if (prepare != null) { try { prepare(); } catch { } }
            if (items.Length == 0) return;
            if (menu != null) { try { menu.Dispose(); } catch { } }
            menu = new ContextMenuStrip();
            menu.Font = Theme.UI(9f, false);
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.MinimumSize = new Size(Width, 0);
            menu.BackColor = Theme.Card;
            menu.ForeColor = Theme.Fg;
            menu.DropShadowEnabled = false;
            menu.Renderer = new TechMenuRenderer();
            for (int i = 0; i < items.Length; i++)
            {
                var mi = new ToolStripMenuItem(items[i]);
                mi.ForeColor = i == idx ? Theme.Accent : Theme.Fg;
                mi.Padding = new Padding(0, Theme.S(6), 0, Theme.S(6));
                if (i < tags.Length && tags[i] != null) mi.Tag = tags[i];
                int picked = i;
                mi.Click += delegate
                {
                    if (picked == idx) return;
                    idx = picked;
                    Invalidate();
                    Action<int> handler = IndexChanged;
                    if (handler != null) handler(picked);
                };
                menu.Items.Add(mi);
            }
            ContextMenuStrip shown = menu;
            shown.Opened += delegate { try { Native.RoundCorners(shown.Handle); } catch { } };
            shown.Closed += delegate { open = false; SyncMotion(); Invalidate(); };
            open = true;
            SyncMotion();
            Invalidate();
            menu.Show(this, new Point(0, Height + Theme.S(2)));
            StretchItems(menu);
        }

        private static void StretchItems(ToolStripDropDown dd)
        {
            int full = dd.ClientSize.Width;
            foreach (ToolStripItem item in dd.Items)
            {
                int want = full - item.Bounds.X;
                if (item.Width >= want) continue;
                item.AutoSize = false;
                item.Size = new Size(want, item.Height);
            }
        }

#if PAVISE_SELFTEST
        internal ContextMenuStrip SelfTestShowList() { ShowList(); return menu; }
#endif

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var body = new Rectangle(0, 0, Width - 1, Height - 1);
            int cut = Theme.S(9);
            float glow = lit.Value;
            float drop = spin.Value;
            using (GraphicsPath p = Theme.TechPath(body, cut))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, glow))) g.FillPath(b, p);
                using (var pen = new Pen(Col.Lerp(Col.Lerp(Theme.Stroke, Theme.StrokeHi, glow),
                    Col.Alpha(Theme.Accent, 230), drop))) g.DrawPath(pen, p);
            }
            using (var mark = new Pen(Col.Alpha(Theme.Accent, (int)(130 + 60 * glow + 45 * drop)),
                Math.Max(1f, Theme.S(2))))
                g.DrawLine(mark, body.Left, body.Top + cut, body.Left, body.Bottom - cut);
            using (var corner = new Pen(Col.Alpha(Theme.Accent, (int)(80 + 60 * glow + 60 * drop)),
                Math.Max(1f, Theme.S(1))))
                g.DrawLine(corner, body.Right - cut, body.Top, body.Right, body.Top + cut);

            int arrow = Theme.S(22);
            string text = idx >= 0 && idx < items.Length ? items[idx] : "";
            TextRenderer.DrawText(g, text, Theme.UI(8.75f, false),
                new Rectangle(Theme.S(13), body.Top, Width - arrow - Theme.S(18), Height),
                Theme.Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            float cx = Width - arrow / 2f - Theme.S(4), cy = Height / 2f - 1, a = Theme.S(4);
            float flip = 1f - drop * 2f;
            using (var pen = new Pen(Col.Lerp(Theme.Dim, Theme.Accent, 0.45f + glow * 0.4f),
                Math.Max(1.4f, Theme.S(1))))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[] {
                    new PointF(cx - a, cy - a / 2 * flip), new PointF(cx, cy + a / 2 * flip),
                    new PointF(cx + a, cy - a / 2 * flip) });
            }
        }
    }
}
