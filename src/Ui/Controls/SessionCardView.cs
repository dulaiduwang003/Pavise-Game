// @author bdth 2074055628@qq.com
// 文件用途 报告页的单局会话摘要卡片 大数字统计与归因徽章

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AegisApp
{
    internal sealed class SessionCardView : RoundPanel
    {
        private SessionSummary data;
        private bool closeHover;
        public Action<SessionSummary> DeleteRequested;

        public SessionCardView()
        {
            Radius = Theme.S(12);
            Fill = Theme.Card;
            Border = Theme.Stroke;
            BackColor = Theme.Bg;
        }

        public void Bind(SessionSummary summary) { data = summary; Invalidate(); }

        private Rectangle CloseRect()
        {
            return new Rectangle(Width - Theme.S(26), Theme.S(6), Theme.S(20), Theme.S(20));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = CloseRect().Contains(e.Location);
            if (over != closeHover)
            {
                closeHover = over;
                Cursor = over ? Cursors.Hand : Cursors.Default;
                Invalidate(CloseRect());
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (closeHover) { closeHover = false; Cursor = Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && CloseRect().Contains(e.Location)
                && data != null && DeleteRequested != null)
                DeleteRequested(data);
        }

        private static Color ChipColor(string chip)
        {
            if (chip.Contains("热节流")) return Theme.Danger;
            if (chip.Contains("功耗受限")) return PercentIn(chip) >= 20 ? Theme.Accent : Theme.Dim;
            if (chip.Contains("单核饱和")) return PercentIn(chip) >= 15 ? Theme.Accent : Theme.Dim;
            if (chip.Contains("可用内存"))
            {
                double gb;
                string token = null;
                int idx = chip.LastIndexOf(' ');
                if (idx >= 0) token = chip.Substring(idx + 1).Replace("GB", "");
                if (token != null && double.TryParse(token,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out gb) && gb < 2)
                    return Theme.Danger;
            }
            return Theme.Dim;
        }

        private static int PercentIn(string chip)
        {
            int pct = chip.IndexOf('%');
            if (pct <= 0) return -1;
            int start = pct - 1;
            while (start >= 0 && char.IsDigit(chip[start])) start--;
            start++;
            if (start >= pct) return -1;
            int value;
            return int.TryParse(chip.Substring(start, pct - start), out value) ? value : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (data == null) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int padL = Theme.S(16);

            Rectangle close = CloseRect();
            Color closeColor = closeHover ? Theme.Danger : Theme.Faint;
            if (closeHover)
                using (var fill = new SolidBrush(Col.Alpha(Theme.Danger, 30)))
                    g.FillEllipse(fill, close);
            using (var pen = new Pen(closeColor, Math.Max(1.2f, Theme.S(1))))
            {
                int inset = Theme.S(6);
                g.DrawLine(pen, close.X + inset, close.Y + inset,
                    close.Right - inset, close.Bottom - inset);
                g.DrawLine(pen, close.Right - inset, close.Y + inset,
                    close.X + inset, close.Bottom - inset);
            }

            int statW = Theme.S(66);
            int statsX = Width - Theme.S(34) - statW * 3;
            string[] values = { data.AvgFps ?? "—", data.Low1Fps ?? "—", data.Low01Fps ?? "—" };
            string[] labels = { Lang.T("rep.stat.avg"), Lang.T("rep.stat.low1"), Lang.T("rep.stat.low01") };
            for (int i = 0; i < 3; i++)
            {
                var valueRect = new Rectangle(statsX + i * statW, Theme.S(10), statW, Theme.S(24));
                TextRenderer.DrawText(g, values[i], Theme.UI(12.5f, true), valueRect,
                    data.AvgFps != null ? Theme.Fg : Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                var labelRect = new Rectangle(statsX + i * statW, Theme.S(34), statW, Theme.S(14));
                TextRenderer.DrawText(g, labels[i], Theme.UI(7f, false), labelRect, Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int textW = statsX - padL - Theme.S(10);
            TextRenderer.DrawText(g, data.Game, Theme.UI(10f, true),
                new Rectangle(padL, Theme.S(10), textW, Theme.S(20)), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string meta = (data.Time != null && data.Time.Length >= 16
                    ? data.Time.Substring(5, 11) : data.Time)
                + " · " + data.Preset + " · " + data.Duration
                + (string.IsNullOrEmpty(data.ControlText) ? "" : " · " + data.ControlText)
                + (string.IsNullOrEmpty(data.AegisCpuText) ? "" : " · " + data.AegisCpuText);
            TextRenderer.DrawText(g, meta, Theme.UI(7.8f, false),
                new Rectangle(padL, Theme.S(32), textW, Theme.S(16)), Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            var chips = new List<KeyValuePair<string, Color>>();
            if (!string.IsNullOrEmpty(data.BoostText))
                chips.Add(new KeyValuePair<string, Color>(data.BoostText,
                    data.BoostVerified ? Theme.Green : Theme.Dim));
            foreach (string chip in data.Chips)
                chips.Add(new KeyValuePair<string, Color>(chip, ChipColor(chip)));

            int x = padL;
            int chipY = Theme.S(56), chipH = Theme.S(22);
            int rightLimit = Width - Theme.S(12);
            Font chipFont = Theme.UI(7.5f, false);
            for (int i = 0; i < chips.Count; i++)
            {
                string text = chips[i].Key;
                Color color = chips[i].Value;
                int w = TextRenderer.MeasureText(g, text, chipFont).Width + Theme.S(16);
                if (x + w > rightLimit)
                {
                    string more = "+" + (chips.Count - i);
                    int moreW = TextRenderer.MeasureText(g, more, chipFont).Width + Theme.S(14);
                    if (x + moreW <= rightLimit)
                        DrawChip(g, new Rectangle(x, chipY, moreW, chipH), more, Theme.Dim, chipFont);
                    break;
                }
                DrawChip(g, new Rectangle(x, chipY, w, chipH), text, color, chipFont);
                x += w + Theme.S(8);
            }
        }

        private static void DrawChip(Graphics g, Rectangle rect, string text, Color color, Font font)
        {
            var r = new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            using (GraphicsPath path = Theme.TechPath(r, Theme.S(6)))
            {
                using (var fill = new SolidBrush(Col.Alpha(color, 26))) g.FillPath(fill, path);
                using (var pen = new Pen(Col.Alpha(color, 120))) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, text, font, r,
                Col.Lerp(color, Color.White, 0.35f),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
