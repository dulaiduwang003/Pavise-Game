// @author bdth 2074055628@qq.com
// File purpose Common panel controls for the project
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
        // When off, isolates the backdrop together with nested controls; for popup components in the same window
        public bool UseBackdrop = true;

        public RoundPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        // A card that wants to be transparent must first paint the backdrop slice beneath it; WinForms child controls have no real transparency
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Backdrop.AppliesTo(this)) { Backdrop.Paint(e.Graphics, this, e.ClipRectangle); return; }
            base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.TechPath(r, Math.Max(Theme.S(5), Radius / 2)))
            {
                if (Theme.LightMode)
                {
                    Color top = Backdrop.CardFill(this, Col.Lerp(Fill, Theme.Accent, 0.035f));
                    Color bottom = Backdrop.CardFill(this, Col.Lerp(Fill, Theme.Bg, 0.13f));
                    using (var b = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
                        g.FillPath(b, path);
                }
                else using (var b = new SolidBrush(Backdrop.CardFill(this, Fill))) g.FillPath(b, path);
                if (Theme.LightMode && AccentEdge)
                {
                    g.SetClip(path);
                    var wash = new Rectangle(Math.Max(0, Width - Theme.S(220)), 0, Theme.S(220), Theme.S(104));
                    using (var glow = new LinearGradientBrush(wash,
                        Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent, 24), LinearGradientMode.BackwardDiagonal))
                        g.FillPolygon(glow, new[] {
                            new Point(wash.Left, wash.Top), new Point(wash.Right, wash.Top),
                            new Point(wash.Right, wash.Bottom)
                        });
                    g.ResetClip();
                }
                if (Border != Color.Empty) using (var pen = new Pen(Border)) g.DrawPath(pen, path);
                using (var hi = new Pen(Theme.LightMode ? Col.Alpha(Theme.Accent, 24) : Col.Alpha(Color.White, 13)))
                    g.DrawLine(hi, Theme.S(1), Theme.S(1), Math.Max(Theme.S(2), Width - Radius), Theme.S(1));
                if (Theme.LightMode)
                    using (var depth = new Pen(Col.Alpha(Theme.StrokeHi, 34)))
                    {
                        g.DrawLine(depth, Theme.S(8), Height - Theme.S(2), Width - Theme.S(8), Height - Theme.S(2));
                        g.DrawLine(depth, Width - Theme.S(2), Theme.S(10), Width - Theme.S(2), Height - Theme.S(10));
                    }
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

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Backdrop.AppliesTo(this)) { Backdrop.Paint(e.Graphics, this, e.ClipRectangle); return; }
            base.OnPaintBackground(e);
        }
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
            Color surfaceBase = Col.Lerp(Theme.Card, Theme.CardHover, hover.Value * 0.72f);
            if (Theme.LightMode) surfaceBase = Col.Lerp(surfaceBase, Theme.Accent, 0.025f + hover.Value * 0.025f);
            Color tailBase = Theme.LightMode
                ? Col.Lerp(surfaceBase, Theme.Inset, 0.34f)
                : Col.Lerp(surfaceBase, Theme.Inset, 0.22f);
            Color surface = Backdrop.CardFill(this, surfaceBase);
            Color tail = Backdrop.CardFill(this, tailBase);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(8)))
            {
                using (var fill = new LinearGradientBrush(frame, surface, tail, LinearGradientMode.Horizontal))
                    g.FillPath(fill, path);
                using (var border = new Pen(Col.Lerp(Theme.Stroke, Theme.StrokeHi, hover.Value * 0.72f)))
                    g.DrawPath(border, path);
            }

            int railX = Theme.S(16);
            int railY = Theme.S(24);
            int railSize = Theme.S(50);
            using (var glow = new SolidBrush(Col.Alpha(Theme.Accent, (int)(10 + hover.Value * 14))))
                g.FillEllipse(glow, railX - Theme.S(5), railY - Theme.S(5), railSize + Theme.S(10), railSize + Theme.S(10));
            using (var socket = Theme.TechPath(new Rectangle(railX, railY, railSize, railSize), Theme.S(6)))
            {
                using (var fill = new SolidBrush(Backdrop.CardFill(this,
                    Col.Lerp(Theme.Inset, Theme.Sel, 0.22f + hover.Value * 0.22f))))
                    g.FillPath(fill, socket);
                using (var border = new Pen(Col.Alpha(Theme.Accent, (int)(72 + hover.Value * 92))))
                    g.DrawPath(border, socket);
            }
            Glyphs.Draw(g, Glyph,
                new Rectangle(railX + Theme.S(14), railY + Theme.S(14), Theme.S(22), Theme.S(22)), Theme.Accent);

            int dividerX = Theme.S(82);
            using (var divider = new Pen(Col.Alpha(Theme.StrokeHi, 92)))
                g.DrawLine(divider, dividerX, Theme.S(20), dividerX, Height - Theme.S(20));
            using (var live = new Pen(Theme.Accent, Math.Max(1f, Theme.S(1))))
                g.DrawLine(live, dividerX, Theme.S(28), dividerX, Theme.S(51));

            TextRenderer.DrawText(g, Title, Theme.UI(11f, true),
                new Rectangle(Theme.S(98), Theme.S(22), Width - Theme.S(145), Theme.S(28)), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Detail, Theme.UI(8.4f, false),
                new Rectangle(Theme.S(98), Theme.S(56), Width - Theme.S(112), Theme.S(34)), Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Channel.ToString("00"), Theme.Mono(6.7f),
                new Rectangle(Width - Theme.S(39), Theme.S(20), Theme.S(24), Theme.S(18)), Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int trackY = Height - Theme.S(8);
            int trackW = Math.Max(Theme.S(34), (Width - Theme.S(120)) / 3);
            using (var track = new Pen(Col.Alpha(Theme.Accent, (int)(120 + 80 * hover.Value))))
                g.DrawLine(track, Theme.S(98), trackY, Theme.S(98) + trackW, trackY);
        }
    }

}
