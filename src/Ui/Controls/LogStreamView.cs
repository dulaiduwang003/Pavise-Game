// @author bdth 2074055628@qq.com
// 文件用途 将原始日志渲染成结构化战术事件流
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace PaviseApp
{
    internal enum LogEventSeverity { Info, Success, Warning, Error }

    internal sealed class LogEventEntry
    {
        public string Raw = "";
        public string Date = "-- --";
        public string Time = "--:--:--";
        public string Module = "SYSTEM";
        public string Message = "";
        public LogEventSeverity Severity;
    }

    internal sealed class LogStreamView : ScrollableControl
    {
        private readonly List<LogEventEntry> all = new List<LogEventEntry>();
        private readonly List<LogEventEntry> shown = new List<LogEventEntry>();
        private string source = "";
        private int filter;
        private int selected = -1;

        public int TotalCount { get { return all.Count; } }
        public int WarningCount { get; private set; }
        public int ErrorCount { get; private set; }
        public string LatestTime { get { return all.Count > 0 ? all[0].Time : "--:--:--"; } }
        public string SelectedRaw { get { return selected >= 0 && selected < shown.Count ? shown[selected].Raw : ""; } }

        private int RowH { get { return Theme.S(68); } }

        public LogStreamView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            AutoScroll = true;
            BackColor = Theme.Inset;
            TabStop = true;
        }

        public int Filter
        {
            get { return filter; }
            set
            {
                int next = value < 0 ? 0 : value > 2 ? 2 : value;
                if (filter == next) return;
                filter = next;
                RebuildShown();
            }
        }

        public bool SetText(string text)
        {
            text = text ?? "";
            if (source == text) return false;
            source = text;
            all.Clear();
            WarningCount = ErrorCount = 0;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].TrimEnd();
                if (line.Length == 0) continue;
                LogEventEntry entry = Parse(line);
                all.Add(entry);
                if (entry.Severity == LogEventSeverity.Warning) WarningCount++;
                else if (entry.Severity == LogEventSeverity.Error) ErrorCount++;
            }
            RebuildShown();
            return true;
        }

        private void RebuildShown()
        {
            shown.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                LogEventEntry entry = all[i];
                if (filter == 1 && entry.Severity != LogEventSeverity.Warning && entry.Severity != LogEventSeverity.Error) continue;
                if (filter == 2 && entry.Severity != LogEventSeverity.Error) continue;
                shown.Add(entry);
            }
            selected = -1;
            AutoScrollMinSize = new Size(0, Math.Max(ClientSize.Height, shown.Count * RowH + Theme.S(10)));
            AutoScrollPosition = Point.Empty;
            Invalidate();
        }

        private static LogEventEntry Parse(string line)
        {
            var entry = new LogEventEntry { Raw = line };
            string message = line.Trim();
            DateTime stamp;
            if (line.Length >= 19 && DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp))
            {
                entry.Date = stamp.ToString("MM-dd");
                entry.Time = stamp.ToString("HH:mm:ss");
                message = line.Substring(19).Trim();
            }
            entry.Severity = ClassifyLine(message, out message);
            int split = message.IndexOf(' ');
            if (split > 0 && split <= 16)
            {
                entry.Module = message.Substring(0, split).Trim().ToUpperInvariant();
                entry.Message = message.Substring(split + 1).Trim();
            }
            else
            {
                entry.Module = Lang.T("v20.log.system");
                entry.Message = message;
            }
            if (entry.Message.Length == 0) entry.Message = message;
            return entry;
        }

        // 带 Logger 分级标记的行按标记走 标记本身不进正文 其余行仍按下面的词表判
        //   环境限制类的日志文案里常带"无法 失败" 这些词会被判成异常 那些调用点用 Logger.Warn 写
        internal static LogEventSeverity ClassifyLine(string text, out string body)
        {
            body = text ?? "";
            if (body.StartsWith(Logger.WarnTag, StringComparison.Ordinal))
            {
                body = body.Substring(Logger.WarnTag.Length).Trim();
                return LogEventSeverity.Warning;
            }
            if (body.StartsWith(Logger.FailTag, StringComparison.Ordinal))
            {
                body = body.Substring(Logger.FailTag.Length).Trim();
                return LogEventSeverity.Error;
            }
            return Classify(body);
        }

        // 这里的词表就是分级依据 写日志文案时得顺带想一下会被判成什么色
        //   "跳过"和 skip 不在警告里 本机没这块硬件所以不做某项 是常规结论不是出事
        //   真要报警告得写清是什么没成 比如 未生效 不完整 不可用 被占用
        private static LogEventSeverity Classify(string text)
        {
            string lower = (text ?? "").ToLowerInvariant();
            if (HasAny(lower, "失败", "异常", "错误", "未能", "无法", "fail", "error", "exception", "denied"))
                return LogEventSeverity.Error;
            if (HasAny(lower, "警告", "待重试", "不完整", "不可用", "未生效", "风险", "被占用",
                    "warn", "retry", "unavailable", "in use"))
                return LogEventSeverity.Warning;
            if (HasAny(lower, "已生效", "已完成", "已还原", "成功", "完成", "已开启", "已恢复", "active", "restored", "success", "done", "started"))
                return LogEventSeverity.Success;
            return LogEventSeverity.Info;
        }

        private static bool HasAny(string text, params string[] needles)
        {
            for (int i = 0; i < needles.Length; i++) if (text.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal static Color SeverityColor(LogEventSeverity value)
        {
            if (value == LogEventSeverity.Error) return Theme.Danger;
            if (value == LogEventSeverity.Warning)
                return Theme.LightMode ? Color.FromArgb(205, 122, 16) : Color.FromArgb(255, 174, 52);
            if (value == LogEventSeverity.Success) return Theme.Green;
            return Theme.Accent;
        }

        internal static string SeverityCode(LogEventSeverity value)
        {
            if (value == LogEventSeverity.Error) return "FAIL";
            if (value == LogEventSeverity.Warning) return "WARN";
            if (value == LogEventSeverity.Success) return "PASS";
            return "INFO";
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int index = (e.Y - AutoScrollPosition.Y) / Math.Max(1, RowH);
            if (index >= 0 && index < shown.Count) { selected = index; Focus(); Invalidate(); }
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            string raw = SelectedRaw;
            if (raw.Length > 0) try { Clipboard.SetText(raw); } catch { }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            AutoScrollMinSize = new Size(0, Math.Max(ClientSize.Height, shown.Count * RowH + Theme.S(10)));
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Backdrop.AppliesTo(this)) { Backdrop.Paint(e.Graphics, this, e.ClipRectangle); return; }
            base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int offset = AutoScrollPosition.Y;
            int first = Math.Max(0, (-offset) / Math.Max(1, RowH));
            int last = Math.Min(shown.Count, first + ClientSize.Height / Math.Max(1, RowH) + 2);
            if (shown.Count == 0)
            {
                TextRenderer.DrawText(g, Lang.T("rep.log.none"), Theme.UI(10f, true), ClientRectangle, Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            for (int i = first; i < last; i++) DrawEntry(g, shown[i], i, offset + i * RowH);
        }

        private void DrawEntry(Graphics g, LogEventEntry entry, int index, int y)
        {
            int pad = Theme.S(8);
            int rowH = RowH - Theme.S(6);
            int width = ClientSize.Width - Theme.S(4);
            var frame = new Rectangle(pad, y + Theme.S(3), width - pad * 2, rowH);
            Color signal = SeverityColor(entry.Severity);
            Color surface = index == selected ? Col.Lerp(Theme.Card, signal, 0.11f)
                : Col.Lerp(Theme.Card, Theme.Inset, (index & 1) == 0 ? 0.06f : 0.16f);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(8)))
            {
                using (var fill = new SolidBrush(Backdrop.CardFill(this, surface))) g.FillPath(fill, path);
                using (var border = new Pen(index == selected ? Col.Alpha(signal, 170) : Theme.Stroke)) g.DrawPath(border, path);
            }
            using (var live = new Pen(signal, Math.Max(1.5f, Theme.S(2))))
                g.DrawLine(live, frame.Left, frame.Top + Theme.S(11), frame.Left, frame.Bottom - Theme.S(11));

            int timelineX = frame.Left + Theme.S(24);
            using (var timeline = new Pen(Col.Alpha(Theme.StrokeHi, 80)))
                g.DrawLine(timeline, timelineX, frame.Top, timelineX, frame.Bottom);
            using (var halo = new SolidBrush(Col.Alpha(signal, 36)))
                g.FillEllipse(halo, timelineX - Theme.S(7), frame.Top + Theme.S(25) - Theme.S(7), Theme.S(14), Theme.S(14));
            using (var dot = new SolidBrush(signal))
                g.FillEllipse(dot, timelineX - Theme.S(3), frame.Top + Theme.S(25) - Theme.S(3), Theme.S(6), Theme.S(6));

            int timeX = frame.Left + Theme.S(42);
            TextRenderer.DrawText(g, entry.Time, Theme.Mono(8f),
                new Rectangle(timeX, frame.Top + Theme.S(9), Theme.S(76), Theme.S(19)), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, entry.Date, Theme.Mono(6.4f),
                new Rectangle(timeX, frame.Top + Theme.S(30), Theme.S(76), Theme.S(16)), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var chip = new Rectangle(frame.Left + Theme.S(122), frame.Top + Theme.S(17), Theme.S(100), Theme.S(27));
            using (GraphicsPath path = Theme.TechPath(chip, Theme.S(6)))
            {
                using (var fill = new SolidBrush(Col.Alpha(signal, Theme.LightMode ? 22 : 30))) g.FillPath(fill, path);
                using (var border = new Pen(Col.Alpha(signal, 110))) g.DrawPath(border, path);
            }
            TextRenderer.DrawText(g, entry.Module, Theme.UI(7.5f, true), chip, signal,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            int messageX = frame.Left + Theme.S(240);
            int codeW = Theme.S(52);
            TextRenderer.DrawText(g, entry.Message, Theme.UI(8.4f, false),
                new Rectangle(messageX, frame.Top + Theme.S(10), Math.Max(40, frame.Right - messageX - codeW - Theme.S(18)), frame.Height - Theme.S(18)),
                Theme.Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, SeverityCode(entry.Severity), Theme.Mono(6.3f),
                new Rectangle(frame.Right - codeW - Theme.S(10), frame.Top + Theme.S(8), codeW, Theme.S(17)), signal,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, (index + 1).ToString("000"), Theme.Mono(5.8f),
                new Rectangle(frame.Right - codeW - Theme.S(10), frame.Bottom - Theme.S(22), codeW, Theme.S(14)), Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
