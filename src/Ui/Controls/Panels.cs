// @author bdth 2074055628@qq.com
// 文件用途 提供项目通用面板控件

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal class RoundPanel : Panel
    {
        public int Radius = Dpi.S(10);
        public Color Fill = Theme.Card;
        public Color Border = Color.Empty;
        public bool AccentEdge;

        public RoundPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.TechPath(r, Math.Max(Theme.S(5), Radius / 2)))
            {
                using (var b = new SolidBrush(Fill)) g.FillPath(b, path);
                if (Border != Color.Empty) using (var pen = new Pen(Border)) g.DrawPath(pen, path);
                using (var hi = new Pen(Col.Alpha(Color.White, 13)))
                    g.DrawLine(hi, Theme.S(1), Theme.S(1), Math.Max(Theme.S(2), Width - Radius), Theme.S(1));
            }
            if (AccentEdge)
            {
                using (var pen = new Pen(Theme.Accent, Math.Max(1f, Theme.S(1))))
                {
                    g.DrawLine(pen, Theme.S(1), Theme.S(1), Math.Min(Width - Theme.S(8), Theme.S(58)), Theme.S(1));
                    g.DrawLine(pen, Width - Theme.S(13), Theme.S(2), Width - Theme.S(2), Theme.S(13));
                }
            }
        }
    }

    internal class DBPanel : Panel
    {
        public DBPanel() { DoubleBuffered = true; }
    }

    internal sealed class EmptyStatePanel : RoundPanel
    {
        public bool ShowEmpty;
        public string EmptyTitle = "";
        public string EmptyDetail = "";
        public string EmptyGlyph = "game";

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!ShowEmpty) return;

            int cx = ClientSize.Width / 2;
            int cy = ClientSize.Height / 2;
            Glyphs.Draw(e.Graphics, EmptyGlyph,
                new Rectangle(cx - Theme.S(18), cy - Theme.S(72), Theme.S(36), Theme.S(36)), Theme.Accent);
            TextRenderer.DrawText(e.Graphics, EmptyTitle, Theme.UI(9.25f, true),
                    new Rectangle(Theme.S(24), cy - Theme.S(26), ClientSize.Width - Theme.S(48), Theme.S(24)),
                    Theme.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, EmptyDetail, Theme.UI(8.4f, false),
                    new Rectangle(Theme.S(42), cy + Theme.S(8), ClientSize.Width - Theme.S(84), Theme.S(54)),
                    Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class AccentLine : Control
    {
        public AccentLine()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, ClientRectangle);
        }
    }

    internal sealed class DashboardTile : FxControl
    {
        public string Title = "";
        public string Detail = "";
        public string Glyph = "game";
        public int Channel = 1;

        public DashboardTile()
        {
            Cursor = Cursors.Default;
            TabStop = false;
            BackColor = Theme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            FillBg(g);

            Rectangle frame = new Rectangle(0, 0, Width - 1, Height - 1);
            Color surface = Col.Lerp(Theme.Card, Theme.CardHover, hover.Value * 0.72f);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(8)))
            {
                using (var fill = new LinearGradientBrush(frame, surface,
                    Col.Lerp(surface, Theme.Inset, 0.22f), LinearGradientMode.Horizontal))
                    g.FillPath(fill, path);
                using (var border = new Pen(Col.Lerp(Theme.Stroke, Theme.StrokeHi, hover.Value * 0.72f)))
                    g.DrawPath(border, path);
            }

            int railX = Theme.S(14);
            int railY = Theme.S(16);
            int railSize = Theme.S(34);
            using (var glow = new SolidBrush(Col.Alpha(Theme.Accent, (int)(10 + hover.Value * 14))))
                g.FillEllipse(glow, railX - Theme.S(5), railY - Theme.S(5), railSize + Theme.S(10), railSize + Theme.S(10));
            using (var socket = Theme.TechPath(new Rectangle(railX, railY, railSize, railSize), Theme.S(6)))
            {
                using (var fill = new SolidBrush(Col.Lerp(Theme.Inset, Theme.Sel, 0.22f + hover.Value * 0.22f)))
                    g.FillPath(fill, socket);
                using (var border = new Pen(Col.Alpha(Theme.Accent, (int)(72 + hover.Value * 92))))
                    g.DrawPath(border, socket);
            }
            Glyphs.Draw(g, Glyph,
                new Rectangle(railX + Theme.S(8), railY + Theme.S(8), Theme.S(18), Theme.S(18)), Theme.Accent);

            int dividerX = Theme.S(60);
            using (var divider = new Pen(Col.Alpha(Theme.StrokeHi, 92)))
                g.DrawLine(divider, dividerX, Theme.S(13), dividerX, Height - Theme.S(13));
            using (var live = new Pen(Theme.Accent, Math.Max(1f, Theme.S(1))))
                g.DrawLine(live, dividerX, Theme.S(18), dividerX, Theme.S(35));

            TextRenderer.DrawText(g, Title, Theme.UI(9.1f, true),
                new Rectangle(Theme.S(72), Theme.S(11), Width - Theme.S(119), Theme.S(23)), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Detail, Theme.UI(7.45f, false),
                new Rectangle(Theme.S(72), Theme.S(35), Width - Theme.S(84), Theme.S(30)), Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Channel.ToString("00"), Theme.Mono(6.2f),
                new Rectangle(Width - Theme.S(39), Theme.S(11), Theme.S(24), Theme.S(16)), Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int trackY = Height - Theme.S(4);
            int trackW = Math.Max(Theme.S(18), (Width - Theme.S(92)) / 3);
            int offset = (int)(hover.Value * Theme.S(10));
            using (var track = new Pen(Col.Alpha(Theme.Accent, 120)))
                g.DrawLine(track, Theme.S(72), trackY, Theme.S(72) + trackW + offset, trackY);
        }
    }

}
