// 作者：bdth 2074055628@qq.com
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
        public DateTime Timestamp;
        public string SessionToken = "", SessionName = "", Phase = "", StableKey = "";
        public string FirstTime = "", LastTime = "";
        public int RepeatCount = 1;
        public bool IsSummary;
        public List<LogEventEntry> Details = new List<LogEventEntry>();
    }

    internal sealed class LogStreamView : ScrollableControl
    {
        private readonly List<LogEventEntry> all = new List<LogEventEntry>();
        private readonly List<LogEventEntry> shown = new List<LogEventEntry>();
        private readonly List<LogEventEntry> groups = new List<LogEventEntry>();
        private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<LogEventEntry> detailRows = new HashSet<LogEventEntry>();
        private string source = "";
        private int filter;
        private string sessionToken = "";
        public string SessionToken
        {
            get { return sessionToken; }
            set
            {
                string next = value ?? "";
                if (sessionToken == next) return;
                sessionToken = next;
                RebuildShown(false);
            }
        }
        private int selected = -1;
        public int GroupCount { get { return groups.Count; } }
        public int GroupWarningCount { get; private set; }
        public int GroupErrorCount { get; private set; }
        public int GroupNormalCount { get { return GroupCount - GroupErrorCount; } }

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
                int next = value < 0 ? 0 : value > 4 ? 4 : value;
                if (filter == next) return;
                filter = next;
                RebuildShown(false);
            }
        }

        public bool SetText(string text)
        {
            text = text ?? "";
            if (source == text) return false;
            List<LogEventEntry> events = ParseEvents(text);
            CarrySessionAssociations(events);
            source = text;
            all.Clear();
            WarningCount = ErrorCount = 0;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                LogEventEntry entry = events[i];
                all.Add(entry);
                if (entry.Severity == LogEventSeverity.Warning) WarningCount++;
                else if (entry.Severity == LogEventSeverity.Error) ErrorCount++;
            }
            if (events.Count == 0) expanded.Clear();
            RebuildShown(true);
            return true;
        }

        private void CarrySessionAssociations(IList<LogEventEntry> events)
        {
            var known = new Dictionary<string, Queue<LogEventEntry>>(StringComparer.Ordinal);
            for (int i = all.Count - 1; i >= 0; i--)
            {
                Queue<LogEventEntry> matches;
                if (!known.TryGetValue(all[i].Raw, out matches))
                { matches = new Queue<LogEventEntry>(); known[all[i].Raw] = matches; }
                matches.Enqueue(all[i]);
            }
            string token = "", name = "", phase = "";
            foreach (LogEventEntry entry in events)
            {
                Queue<LogEventEntry> matches;
                LogEventEntry old = known.TryGetValue(entry.Raw, out matches) && matches.Count > 0 ? matches.Dequeue() : null;
                if (old != null && old.SessionToken.Length > 0
                    && (entry.SessionToken.Length == 0 || entry.Phase == "fragment" && entry.SessionToken == old.SessionToken))
                {
                    entry.SessionToken = old.SessionToken; entry.SessionName = old.SessionName; entry.Phase = old.Phase;
                }
                else if (old == null && entry.SessionToken.Length == 0 && token.Length > 0
                    && (phase == "active" || phase == "restore" || phase == "fragment"))
                {
                    entry.SessionToken = token; entry.SessionName = name; entry.Phase = phase;
                }
                if (entry.SessionToken.Length == 0) continue;
                token = entry.SessionToken; name = entry.SessionName;
                phase = entry.Phase == "start" ? "active" : entry.Phase == "end" ? "restore" : entry.Phase;
            }
        }

        internal static List<LogEventEntry> ParseEvents(string text)
        {
            var events = new List<LogEventEntry>();
            string[] lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                if (line.Length == 0) continue;
                // 旧 CLIXML 续行沿用原时间和级别。
                if (line.IndexOf("#< CLIXML", StringComparison.Ordinal) >= 0 && i + 1 < lines.Length
                    && lines[i + 1].TrimStart().StartsWith("<Objs", StringComparison.Ordinal))
                {
                    do { line += "\n" + lines[++i]; }
                    while (line.IndexOf("</Objs>", StringComparison.Ordinal) < 0 && i + 1 < lines.Length
                        && lines[i + 1].TrimStart().StartsWith("<", StringComparison.Ordinal));
                }
                events.Add(Parse(line));
            }
            LogEventProjection.AssignSessions(events);
            return events;
        }

        private sealed class ViewAnchor
        {
            internal string Key, Raw, SelectedKey, SelectedRaw;
            internal int Row, Pixel, Offset;
        }

        private ViewAnchor CaptureAnchor()
        {
            int offset = Math.Max(0, -AutoScrollPosition.Y);
            int row = Math.Min(shown.Count - 1, offset / Math.Max(1, RowH));
            return new ViewAnchor {
                Row = row, Pixel = offset % Math.Max(1, RowH), Offset = offset,
                Key = row >= 0 ? shown[row].StableKey : "", Raw = row >= 0 ? shown[row].Raw : "",
                SelectedKey = selected >= 0 && selected < shown.Count ? shown[selected].StableKey : "",
                SelectedRaw = SelectedRaw
            };
        }

        private int FindEntry(string key, string raw)
        {
            if (!string.IsNullOrEmpty(key))
                for (int i = 0; i < shown.Count; i++) if (shown[i].StableKey == key) return i;
            if (!string.IsNullOrEmpty(raw))
            {
                for (int i = 0; i < shown.Count; i++)
                    if (shown[i].Raw == raw) return i;
                for (int i = 0; i < shown.Count; i++)
                    if (shown[i].Raw.IndexOf(raw, StringComparison.Ordinal) >= 0) return i;
            }
            return -1;
        }

        private void RebuildShown(bool preserve)
        {
            ViewAnchor anchor = preserve ? CaptureAnchor() : null;
            var open = new List<LogEventEntry>();
            foreach (LogEventEntry group in groups) if (expanded.Contains(group.StableKey)) open.Add(group);
            var chronological = new List<LogEventEntry>(all);
            chronological.Reverse();
            groups.Clear();
            groups.AddRange(LogEventProjection.Project(chronological, filter, SessionToken));
            foreach (LogEventEntry old in open)
            {
                foreach (LogEventEntry group in groups)
                {
                    if (group.IsSummary != old.IsSummary || group.Severity != old.Severity) continue;
                    bool survives = false;
                    foreach (LogEventEntry detail in group.Details)
                        if (old.Raw.IndexOf(detail.Raw, StringComparison.Ordinal) >= 0) { survives = true; break; }
                    if (!survives) continue;
                    expanded.Add(group.StableKey);
                    if (group.StableKey != old.StableKey) expanded.Remove(old.StableKey);
                    break;
                }
            }
            if (expanded.Count > 128)
            {
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (LogEventEntry group in groups) present.Add(group.StableKey);
                expanded.RemoveWhere(delegate(string key) { return !present.Contains(key); });
            }
            GroupWarningCount = GroupErrorCount = 0;
            foreach (LogEventEntry entry in groups)
            {
                if (entry.Severity == LogEventSeverity.Warning) GroupWarningCount++;
                else if (entry.Severity == LogEventSeverity.Error) GroupErrorCount++;
            }
            RebuildRows(anchor);
        }

        private void RebuildRows(ViewAnchor anchor)
        {
            shown.Clear(); detailRows.Clear();
            foreach (LogEventEntry entry in groups)
            {
                shown.Add(entry);
                if (!expanded.Contains(entry.StableKey)) continue;
                for (int i = 0; i < entry.Details.Count; i++)
                {
                    LogEventEntry detail = entry.Details[i];
                    detail.StableKey = "detail:" + entry.StableKey + "\u001f" + i.ToString(CultureInfo.InvariantCulture) + "\u001f" + detail.Raw;
                    shown.Add(detail); detailRows.Add(detail);
                }
            }
            selected = anchor == null ? -1 : FindEntry(anchor.SelectedKey, anchor.SelectedRaw);
            AutoScrollMinSize = new Size(0, Math.Max(ClientSize.Height, shown.Count * RowH + Theme.S(10)));
            if (anchor != null && anchor.Offset > 0)
            {
                int found = FindEntry(anchor.Key, anchor.Raw);
                if (found < 0) found = Math.Min(Math.Max(0, anchor.Row), Math.Max(0, shown.Count - 1));
                int desired = found * RowH + anchor.Pixel;
                int maximum = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
                AutoScrollPosition = new Point(0, Math.Min(desired, maximum));
            }
            else AutoScrollPosition = Point.Empty;
            Invalidate();
        }

        internal void ToggleExpanded(int index)
        {
            if (index < 0 || index >= shown.Count || detailRows.Contains(shown[index]) || shown[index].Details.Count == 0) return;
            ViewAnchor anchor = CaptureAnchor();
            string key = shown[index].StableKey;
            if (!expanded.Add(key)) expanded.Remove(key);
            RebuildRows(anchor);
        }

        internal static LogEventEntry Parse(string line)
        {
            var entry = new LogEventEntry { Raw = line };
            string message = line.Trim();
            DateTime stamp;
            if (line.Length >= 19 && DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp))
            {
                entry.Date = stamp.ToString("MM-dd");
                entry.Time = stamp.ToString("HH:mm:ss");
                entry.Timestamp = stamp;
                message = line.Substring(19).Trim();
            }
            entry.Severity = ClassifyLine(message, out message);
            int clixml = message.IndexOf("#< CLIXML", StringComparison.Ordinal);
            if (clixml >= 0)
                message = message.Substring(0, clixml).TrimEnd() + " " + PsRunner.NormalizeError(message.Substring(clixml));
            // 诊断原文保留完整对局标记。
            if (message.StartsWith("AUTO [", StringComparison.Ordinal))
            {
                int end = message.IndexOf(']', 6);
                int separator = end >= 0 ? message.IndexOf('|', end + 1) : -1;
                if (separator > end + 1)
                {
                    string action = message.Substring(end + 1, separator - end - 1).Trim();
                    string state = message.Substring(separator + 1).Trim();
                    entry.Module = Lang.T("auto.log.module");
                    entry.Message = GameMode.AutomationActionLabel(action) + " · " + GameMode.AutomationStateLabel(state);
                    return entry;
                }
            }
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
            if (body.StartsWith(Logger.InfoTag, StringComparison.Ordinal))
            {
                body = body.Substring(Logger.InfoTag.Length).Trim();
                return LogEventSeverity.Info;
            }
            if (body.StartsWith(Logger.SuccessTag, StringComparison.Ordinal))
            {
                body = body.Substring(Logger.SuccessTag.Length).Trim();
                return LogEventSeverity.Success;
            }
            if ((body.StartsWith("FRAME 线程协同待命：", StringComparison.Ordinal)
                || body.StartsWith("FRAME Coordination waiting: ", StringComparison.Ordinal))
                && body.EndsWith(" capture canceled, lost or over budget; error=0 lost=0", StringComparison.Ordinal))
                return LogEventSeverity.Warning;
            if (NormalizeLegacyBoostSummary(ref body))
                return Classify(body) == LogEventSeverity.Error ? LogEventSeverity.Error : LogEventSeverity.Warning;
            return Classify(body);
        }

        private static bool NormalizeLegacyBoostSummary(ref string body)
        {
            bool chinese = body.StartsWith("游戏提优已生效 ", StringComparison.Ordinal);
            bool english = body.StartsWith("Game boost in effect ", StringComparison.Ordinal);
            if (!chinese && !english) return false;
            string normalized = body
                .Replace("；手动核心分配未确认，请查看落核失败日志", "；手动选核未确认，当前状态未知")
                .Replace("; manual core placement unconfirmed; see placement failure log", "; manual core placement unconfirmed; current state unknown")
                .Replace("；手動コア割り当て未確認。失敗ログを確認してください", "；手動コア割り当て未確認。現在の状態は不明です");
            if (normalized == body) return false;
            body = chinese
                ? "游戏优先级已生效 " + normalized.Substring("游戏提优已生效 ".Length)
                : "Game priorities in effect " + normalized.Substring("Game boost in effect ".Length);
            return true;
        }

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
                return Theme.Warning;
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
            if (index >= 0 && index < shown.Count) { selected = index; Focus(); ToggleExpanded(index); Invalidate(); }
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
                TextRenderer.DrawText(g, Lang.T(filter == 4 ? "log.latest.empty" : "rep.log.none"), Theme.UI(10f, true), ClientRectangle, Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            for (int i = first; i < last; i++) DrawEntry(g, shown[i], i, offset + i * RowH);
        }

        internal static LogEventSeverity DisplaySeverity(LogEventSeverity value)
        { return value == LogEventSeverity.Warning ? LogEventSeverity.Info : value; }

        private void DrawEntry(Graphics g, LogEventEntry entry, int index, int y)
        {
            int pad = Theme.S(8);
            int rowH = RowH - Theme.S(6);
            int width = ClientSize.Width - Theme.S(4);
            bool detail = detailRows.Contains(entry);
            int indent = detail ? Theme.S(16) : 0;
            var frame = new Rectangle(pad + indent, y + Theme.S(3), width - pad * 2 - indent, rowH);
            LogEventSeverity displaySeverity = DisplaySeverity(entry.Severity);
            Color signal = SeverityColor(displaySeverity);
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
            string period = entry.Date;
            if (entry.RepeatCount > 1)
            {
                string first = entry.FirstTime.Length >= 19 ? entry.FirstTime.Substring(11, 8) : entry.FirstTime;
                string last = entry.LastTime.Length >= 19 ? entry.LastTime.Substring(11, 8) : entry.LastTime;
                period = first + "–" + last;
                if (entry.FirstTime.Length >= 19 && entry.LastTime.Length >= 19
                    && entry.FirstTime.Substring(0, 10) != entry.LastTime.Substring(0, 10))
                    period = entry.FirstTime.Substring(5, 5) + "→" + entry.LastTime.Substring(5, 5);
            }
            TextRenderer.DrawText(g, period, Theme.Mono(entry.RepeatCount > 1 ? 5.5f : 6.4f),
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
            string message = entry.Message;
            if (!detail && entry.Details.Count > 0)
                message = (expanded.Contains(entry.StableKey) ? "▾ " : "▸ ") + message;
            TextRenderer.DrawText(g, message, Theme.UI(8.4f, false),
                new Rectangle(messageX, frame.Top + Theme.S(10), Math.Max(40, frame.Right - messageX - codeW - Theme.S(18)), frame.Height - Theme.S(18)),
                Theme.Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, SeverityCode(displaySeverity), Theme.Mono(6.3f),
                new Rectangle(frame.Right - codeW - Theme.S(10), frame.Top + Theme.S(8), codeW, Theme.S(17)), signal,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        }
    }
}
