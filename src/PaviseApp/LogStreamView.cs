using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class LogStreamView : ScrollableControl
	{
		internal static void CountIssues(string raw, out int warnings, out int errors)
		{
			warnings = errors = 0;
			foreach (LogEventEntry entry in ParseEvents(raw))
			{
				if (entry.Severity == LogEventSeverity.Warning) warnings++;
				if (entry.Severity == LogEventSeverity.Error) errors++;
			}
		}

		private sealed class ViewAnchor
		{
			internal string Key;

			internal string Raw;

			internal string SelectedKey;

			internal string SelectedRaw;

			internal int Row;

			internal int Pixel;

			internal int Offset;
		}

		private readonly List<LogEventEntry> all = new List<LogEventEntry>();

		private readonly List<LogEventEntry> shown = new List<LogEventEntry>();

		private readonly List<LogEventEntry> groups = new List<LogEventEntry>();

		private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.Ordinal);

		private readonly HashSet<LogEventEntry> detailRows = new HashSet<LogEventEntry>();

		private string source = "";

		private int filter;

		private bool technicalEvents;

		private bool actionEvents;

		internal bool ActionEvents
		{
			get { return actionEvents; }
			set
			{
				if (actionEvents == value) return;
				actionEvents = value;
				expanded.Clear();
				RebuildShown(false);
			}
		}

		internal bool TechnicalEvents
		{
			get { return technicalEvents; }
			set
			{
				if (technicalEvents == value) return;
				technicalEvents = value;
				expanded.Clear();
				RebuildShown(false);
			}
		}

		private string sessionToken = "";

		private int selected = -1;

		public string SessionToken
		{
			get
			{
				return sessionToken;
			}
			set
			{
				string valueOffset = value ?? "";
				if (!(sessionToken == valueOffset))
				{
					sessionToken = valueOffset;
					RebuildShown(false);
				}
			}
		}

		public int GroupCount
		{
			get
			{
				return groups.Count;
			}
		}

		public int GroupWarningCount { get; private set; }

		public int GroupErrorCount { get; private set; }

		public int GroupNormalCount
		{
			get
			{
				return GroupCount - GroupErrorCount;
			}
		}

		public int TotalCount
		{
			get
			{
				return all.Count;
			}
		}

		public int WarningCount { get; private set; }

		public int ErrorCount { get; private set; }

		public string LatestTime
		{
			get
			{
				if (all.Count <= 0)
				{
					return "--:--:--";
				}
				return all[0].Time;
			}
		}

		public string SelectedRaw
		{
			get
			{
				if (selected < 0 || selected >= shown.Count)
				{
					return "";
				}
				return shown[selected].Raw;
			}
		}

		private int RowH
		{
			get
			{
				return Theme.S(68);
			}
		}

		public int Filter
		{
			get
			{
				return filter;
			}
			set
			{
				int valueSnapshot = ((value >= 0) ? ((value > 4) ? 4 : value) : 0);
				if (filter != valueSnapshot)
				{
					filter = valueSnapshot;
					RebuildShown(false);
				}
			}
		}

		public LogStreamView()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			AutoScroll = true;
			BackColor = Theme.Inset;
			base.TabStop = true;
		}

		public bool SetText(string text)
		{
			text = text ?? "";
			if (source == text)
			{
				return false;
			}
			List<LogEventEntry> list = ParseEvents(text);
			CarrySessionAssociations(list);
			source = text;
			all.Clear();
			int warningCount = (ErrorCount = 0);
			WarningCount = warningCount;
			for (int listIndex = list.Count - 1; listIndex >= 0; listIndex--)
			{
				LogEventEntry logEventEntry = list[listIndex];
				all.Add(logEventEntry);
				if (logEventEntry.Severity == LogEventSeverity.Warning)
				{
					WarningCount++;
				}
				else if (logEventEntry.Severity == LogEventSeverity.Error)
				{
					ErrorCount++;
				}
			}
			if (list.Count == 0)
			{
				expanded.Clear();
			}
			RebuildShown(true);
			return true;
		}

		private void CarrySessionAssociations(IList<LogEventEntry> events)
		{
			Dictionary<string, Queue<LogEventEntry>> dictionary = new Dictionary<string, Queue<LogEventEntry>>(StringComparer.Ordinal);
			for (int allIndex = all.Count - 1; allIndex >= 0; allIndex--)
			{
				Queue<LogEventEntry> value;
				if (!dictionary.TryGetValue(all[allIndex].Raw, out value))
				{
					value = new Queue<LogEventEntry>();
					dictionary[all[allIndex].Raw] = value;
				}
				value.Enqueue(all[allIndex]);
			}
			string sessionTokenValue = "";
			string sessionName = "";
			string phaseValue = "";
			foreach (LogEventEntry @event in events)
			{
				Queue<LogEventEntry> value2;
				LogEventEntry logEventEntry = ((dictionary.TryGetValue(@event.Raw, out value2) && value2.Count > 0) ? value2.Dequeue() : null);
				if (logEventEntry != null && logEventEntry.SessionToken.Length > 0 && (@event.SessionToken.Length == 0 || (@event.Phase == "fragment" && @event.SessionToken == logEventEntry.SessionToken)))
				{
					@event.SessionToken = logEventEntry.SessionToken;
					@event.SessionName = logEventEntry.SessionName;
					@event.Phase = logEventEntry.Phase;
				}
				else if (logEventEntry == null && @event.SessionToken.Length == 0 && sessionTokenValue.Length > 0)
				{
					switch (phaseValue)
					{
					case "active":
					case "restore":
					case "fragment":
						@event.SessionToken = sessionTokenValue;
						@event.SessionName = sessionName;
						@event.Phase = phaseValue;
						break;
					}
				}
				if (@event.SessionToken.Length != 0)
				{
					sessionTokenValue = @event.SessionToken;
					sessionName = @event.SessionName;
					phaseValue = ((@event.Phase == "start") ? "active" : ((@event.Phase == "end") ? "restore" : @event.Phase));
				}
			}
		}

		internal static List<LogEventEntry> ParseEvents(string text)
		{
			List<LogEventEntry> list = new List<LogEventEntry>();
			string[] replaceParts = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
			for (int i = 0; i < replaceParts.Length; i++)
			{
				string trimmed = replaceParts[i].TrimEnd();
				if (trimmed.Length == 0)
				{
					continue;
				}
				if (trimmed.IndexOf("#< CLIXML", StringComparison.Ordinal) >= 0 && i + 1 < replaceParts.Length && replaceParts[i + 1].TrimStart().StartsWith("<Objs", StringComparison.Ordinal))
				{
					do
					{
						trimmed = trimmed + "\n" + replaceParts[++i];
					}
					while (trimmed.IndexOf("</Objs>", StringComparison.Ordinal) < 0 && i + 1 < replaceParts.Length && replaceParts[i + 1].TrimStart().StartsWith("<", StringComparison.Ordinal));
				}
				list.Add(Parse(trimmed));
			}
			LogEventProjection.AssignSessions(list);
			return list;
		}

		private ViewAnchor CaptureAnchor()
		{
			int offsetValue = Math.Max(0, -base.AutoScrollPosition.Y);
			int rowValue = Math.Min(shown.Count - 1, offsetValue / Math.Max(1, RowH));
			ViewAnchor viewAnchor = new ViewAnchor();
			viewAnchor.Row = rowValue;
			viewAnchor.Pixel = offsetValue % Math.Max(1, RowH);
			viewAnchor.Offset = offsetValue;
			viewAnchor.Key = ((rowValue >= 0) ? shown[rowValue].StableKey : "");
			viewAnchor.Raw = ((rowValue >= 0) ? shown[rowValue].Raw : "");
			viewAnchor.SelectedKey = ((selected >= 0 && selected < shown.Count) ? shown[selected].StableKey : "");
			viewAnchor.SelectedRaw = SelectedRaw;
			return viewAnchor;
		}

		private int FindEntry(string key, string raw)
		{
			if (!string.IsNullOrEmpty(key))
			{
				for (int i = 0; i < shown.Count; i++)
				{
					if (shown[i].StableKey == key)
					{
						return i;
					}
				}
			}
			if (!string.IsNullOrEmpty(raw))
			{
				for (int j = 0; j < shown.Count; j++)
				{
					if (shown[j].Raw == raw)
					{
						return j;
					}
				}
				for (int k = 0; k < shown.Count; k++)
				{
					if (shown[k].Raw.IndexOf(raw, StringComparison.Ordinal) >= 0)
					{
						return k;
					}
				}
			}
			return -1;
		}

		private void RebuildShown(bool preserve)
		{
			ViewAnchor anchor = (preserve ? CaptureAnchor() : null);
			List<LogEventEntry> list = new List<LogEventEntry>();
			foreach (LogEventEntry group in groups)
			{
				if (expanded.Contains(group.StableKey))
				{
					list.Add(group);
				}
			}
			List<LogEventEntry> list2 = new List<LogEventEntry>(all);
			list2.Reverse();
			groups.Clear();
			groups.AddRange(actionEvents ? ActionLogProjection.Project(list2, filter, SessionToken)
				: technicalEvents ? ProjectTechnical(list2, filter, SessionToken)
				: LogEventProjection.Project(list2, filter, SessionToken));
			foreach (LogEventEntry item in list)
			{
				foreach (LogEventEntry group2 in groups)
				{
					if (group2.IsSummary != item.IsSummary || group2.Severity != item.Severity)
					{
						continue;
					}
					bool rebuildShownReady = false;
					foreach (LogEventEntry detail in group2.Details)
					{
						if (item.Raw.IndexOf(detail.Raw, StringComparison.Ordinal) >= 0)
						{
							rebuildShownReady = true;
							break;
						}
					}
					if (rebuildShownReady)
					{
						expanded.Add(group2.StableKey);
						if (group2.StableKey != item.StableKey)
						{
							expanded.Remove(item.StableKey);
						}
						break;
					}
				}
			}
			if (expanded.Count > 128)
			{
				HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
				foreach (LogEventEntry group3 in groups)
				{
					present.Add(group3.StableKey);
				}
				expanded.RemoveWhere((string key) => !present.Contains(key));
			}
			int groupWarningCount = (GroupErrorCount = 0);
			GroupWarningCount = groupWarningCount;
			foreach (LogEventEntry group4 in groups)
			{
				if (group4.Severity == LogEventSeverity.Warning)
				{
					GroupWarningCount++;
				}
				else if (group4.Severity == LogEventSeverity.Error)
				{
					GroupErrorCount++;
				}
			}
			RebuildRows(anchor);
		}

		private static List<LogEventEntry> ProjectTechnical(IList<LogEventEntry> chronological, int filter, string activeSession)
		{
			var result = new List<LogEventEntry>();
			string session = LogEventProjection.SelectSession(chronological, activeSession);
			if (filter == 4 && session.Length == 0) return result;
			int start = -1, end = chronological.Count;
			for (int i = 0; i < chronological.Count; i++)
			{
				LogEventEntry entry = chronological[i];
				if (entry.SessionToken == session && start < 0) start = i;
				else if (start >= 0 && entry.SessionToken.Length > 0 && entry.SessionToken != session && entry.Phase == "start")
				{
					end = i;
					break;
				}
			}
			var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
			for (int i = 0; i < chronological.Count; i++)
			{
				LogEventEntry entry = chronological[i];
				bool issue = entry.Severity == LogEventSeverity.Warning || entry.Severity == LogEventSeverity.Error;
				if ((filter == 1 && !issue) || (filter == 2 && entry.Severity != LogEventSeverity.Error)
					|| (filter == 3 && entry.Module != Lang.T("auto.log.module"))
					|| (filter == 4 && (start < 0 || entry.SessionToken != session
						&& (!issue || entry.SessionToken.Length != 0 || i < start || i >= end)))) continue;
				string key = "raw:" + entry.SessionToken + "\u001f" + entry.Raw;
				int occurrence;
				occurrences.TryGetValue(key, out occurrence);
				occurrences[key] = occurrence + 1;
				entry.StableKey = key + "\u001f" + occurrence.ToString(CultureInfo.InvariantCulture);
				result.Add(entry);
			}
			result.Reverse();
			return result;
		}

		private void RebuildRows(ViewAnchor anchor)
		{
			shown.Clear();
			detailRows.Clear();
			foreach (LogEventEntry group in groups)
			{
				shown.Add(group);
				if (expanded.Contains(group.StableKey))
				{
					for (int i = 0; i < group.Details.Count; i++)
					{
						LogEventEntry logEventEntry = group.Details[i];
						logEventEntry.StableKey = "detail:" + group.StableKey + "\u001f" + i.ToString(CultureInfo.InvariantCulture) + "\u001f" + logEventEntry.Raw;
						shown.Add(logEventEntry);
						detailRows.Add(logEventEntry);
					}
				}
			}
			selected = ((anchor == null) ? (-1) : FindEntry(anchor.SelectedKey, anchor.SelectedRaw));
			base.AutoScrollMinSize = new Size(0, Math.Max(base.ClientSize.Height, shown.Count * RowH + Theme.S(10)));
			if (anchor != null && anchor.Offset > 0)
			{
				int entry = FindEntry(anchor.Key, anchor.Raw);
				if (entry < 0)
				{
					entry = Math.Min(Math.Max(0, anchor.Row), Math.Max(0, shown.Count - 1));
				}
				int val = entry * RowH + anchor.Pixel;
				int val2 = Math.Max(0, base.AutoScrollMinSize.Height - base.ClientSize.Height);
				base.AutoScrollPosition = new Point(0, Math.Min(val, val2));
			}
			else
			{
				base.AutoScrollPosition = Point.Empty;
			}
			Invalidate();
		}

		internal void ToggleExpanded(int index)
		{
			if (index >= 0 && index < shown.Count && !detailRows.Contains(shown[index]) && shown[index].Details.Count != 0)
			{
				ViewAnchor anchor = CaptureAnchor();
				string stableKey = shown[index].StableKey;
				if (!expanded.Add(stableKey))
				{
					expanded.Remove(stableKey);
				}
				RebuildRows(anchor);
			}
		}

		internal static LogEventEntry Parse(string line)
		{
			LogEventEntry logEventEntry = new LogEventEntry();
			logEventEntry.Raw = line;
			LogEventEntry logEventEntry2 = logEventEntry;
			string body = line.Trim();
			DateTime result;
			if (line.Length >= 19 && DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
			{
				logEventEntry2.Date = result.ToString("MM-dd");
				logEventEntry2.Time = result.ToString("HH:mm:ss");
				logEventEntry2.Timestamp = result;
				body = line.Substring(19).Trim();
			}
			logEventEntry2.Severity = ClassifyLine(body, out body);
			int matchIndex = body.IndexOf("#< CLIXML", StringComparison.Ordinal);
			if (matchIndex >= 0)
			{
				body = body.Substring(0, matchIndex).TrimEnd() + " " + PsRunner.NormalizeError(body.Substring(matchIndex));
			}
			if (body.StartsWith("AUTO [", StringComparison.Ordinal))
			{
				int matchIndex2 = body.IndexOf(']', 6);
				int matchIndex3 = ((matchIndex2 >= 0) ? body.IndexOf('|', matchIndex2 + 1) : (-1));
				if (matchIndex3 > matchIndex2 + 1)
				{
					string key = body.Substring(matchIndex2 + 1, matchIndex3 - matchIndex2 - 1).Trim();
					string substringTrimmed = body.Substring(matchIndex3 + 1).Trim();
					logEventEntry2.Module = Lang.T("auto.log.module");
					logEventEntry2.Message = "action=" + key + " result=" + substringTrimmed;
					return logEventEntry2;
				}
			}
			int matchIndex4 = body.IndexOf(' ');
			if (matchIndex4 > 0 && matchIndex4 <= 16)
			{
				logEventEntry2.Module = body.Substring(0, matchIndex4).Trim().ToUpperInvariant();
				logEventEntry2.Message = body.Substring(matchIndex4 + 1).Trim();
			}
			else
			{
				logEventEntry2.Module = Lang.T("v20.log.system");
				logEventEntry2.Message = body;
			}
			if (logEventEntry2.Message.Length == 0)
			{
				logEventEntry2.Message = body;
			}
			return logEventEntry2;
		}

		internal static LogEventSeverity ClassifyLine(string text, out string body)
		{
			body = text ?? "";
			if (body.StartsWith("WARN ", StringComparison.Ordinal))
			{
				body = body.Substring("WARN ".Length).Trim();
				return LogEventSeverity.Warning;
			}
			if (body.StartsWith("FAIL ", StringComparison.Ordinal))
			{
				body = body.Substring("FAIL ".Length).Trim();
				return LogEventSeverity.Error;
			}
			if (body.StartsWith("INFO ", StringComparison.Ordinal))
			{
				body = body.Substring("INFO ".Length).Trim();
				return LogEventSeverity.Info;
			}
			if (body.StartsWith("PASS ", StringComparison.Ordinal))
			{
				body = body.Substring("PASS ".Length).Trim();
				return LogEventSeverity.Success;
			}
			if ((body.StartsWith("FRAME 线程协同待命：", StringComparison.Ordinal) || body.StartsWith("FRAME Coordination waiting: ", StringComparison.Ordinal)) && body.EndsWith(" capture canceled, lost or over budget; error=0 lost=0", StringComparison.Ordinal))
			{
				return LogEventSeverity.Warning;
			}
			if (NormalizeLegacyBoostSummary(ref body))
			{
				if (Classify(body) != LogEventSeverity.Error)
				{
					return LogEventSeverity.Warning;
				}
				return LogEventSeverity.Error;
			}
			return Classify(body);
		}

		private static bool NormalizeLegacyBoostSummary(ref string body)
		{
			bool startsWith = body.StartsWith("游戏提优已生效 ", StringComparison.Ordinal);
			bool startsWith2 = body.StartsWith("Game boost in effect ", StringComparison.Ordinal);
			if (!startsWith && !startsWith2)
			{
				return false;
			}
			string replaceResult = body.Replace("；手动核心分配未确认，请查看落核失败日志", "；手动选核未确认，当前状态未知").Replace("; manual core placement unconfirmed; see placement failure log", "; manual core placement unconfirmed; current state unknown").Replace("；手動コア割り当て未確認。失敗ログを確認してください", "；手動コア割り当て未確認。現在の状態は不明です");
			if (replaceResult == body)
			{
				return false;
			}
			body = (startsWith ? ("游戏优先级已生效 " + replaceResult.Substring("游戏提优已生效 ".Length)) : ("Game priorities in effect " + replaceResult.Substring("Game boost in effect ".Length)));
			return true;
		}

		private static LogEventSeverity Classify(string text)
		{
			string toLowerInvariantResult = (text ?? "").ToLowerInvariant();
			if (HasAny(toLowerInvariantResult, "失败", "异常", "错误", "未能", "无法", "fail", "error", "exception", "denied"))
			{
				return LogEventSeverity.Error;
			}
			if (HasAny(toLowerInvariantResult, "警告", "待重试", "不完整", "不可用", "未生效", "风险", "被占用", "warn", "retry", "unavailable", "in use"))
			{
				return LogEventSeverity.Warning;
			}
			if (HasAny(toLowerInvariantResult, "已生效", "已完成", "已还原", "成功", "完成", "已开启", "已恢复", "active", "restored", "success", "done", "started"))
			{
				return LogEventSeverity.Success;
			}
			return LogEventSeverity.Info;
		}

		private static bool HasAny(string text, params string[] needles)
		{
			for (int i = 0; i < needles.Length; i++)
			{
				if (text.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
			return false;
		}

		internal static Color SeverityColor(LogEventSeverity value)
		{
			switch (value)
			{
			case LogEventSeverity.Error:
				return Theme.Danger;
			case LogEventSeverity.Warning:
				return Theme.Warning;
			case LogEventSeverity.Success:
				return Theme.Green;
			default:
				return Theme.Accent;
			}
		}

		internal static string SeverityCode(LogEventSeverity value)
		{
			switch (value)
			{
			case LogEventSeverity.Error:
				return "FAIL";
			case LogEventSeverity.Warning:
				return "WARN";
			case LogEventSeverity.Success:
				return "PASS";
			default:
				return "INFO";
			}
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			int maxRatio = (e.Y - base.AutoScrollPosition.Y) / Math.Max(1, RowH);
			if (maxRatio >= 0 && maxRatio < shown.Count)
			{
				selected = maxRatio;
				Focus();
				ToggleExpanded(maxRatio);
				Invalidate();
			}
		}

		protected override void OnDoubleClick(EventArgs e)
		{
			base.OnDoubleClick(e);
			string selectedRaw = SelectedRaw;
			if (selectedRaw.Length > 0)
			{
				try
				{
					Clipboard.SetText(selectedRaw);
				}
				catch
				{
				}
			}
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			base.AutoScrollMinSize = new Size(0, Math.Max(base.ClientSize.Height, shown.Count * RowH + Theme.S(10)));
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.Paint(e.Graphics, this, e.ClipRectangle);
			}
			else
			{
				base.OnPaintBackground(e);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
			int top = base.AutoScrollPosition.Y;
			int lowerBound = Math.Max(0, -top / Math.Max(1, RowH));
			int countLimit = Math.Min(shown.Count, lowerBound + base.ClientSize.Height / Math.Max(1, RowH) + 2);
			if (shown.Count == 0)
			{
				TextRenderer.DrawText(graphics, Lang.T((filter == 4) ? "log.latest.empty" : "rep.log.none"), Theme.UI(10f, true), base.ClientRectangle, Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
				return;
			}
			for (int i = lowerBound; i < countLimit; i++)
			{
				DrawEntry(graphics, shown[i], i, top + i * RowH);
			}
		}

		internal static LogEventSeverity DisplaySeverity(LogEventSeverity value)
		{
			if (value != LogEventSeverity.Warning)
			{
				return value;
			}
			return LogEventSeverity.Info;
		}

		internal LogEventSeverity VisibleSeverity(LogEventSeverity value)
		{
			return (technicalEvents || actionEvents) ? value : DisplaySeverity(value);
		}

		private void DrawEntry(Graphics g, LogEventEntry entry, int index, int y)
		{
			int scaledDimension = Theme.S(8);
			int remainingRowH = RowH - Theme.S(6);
			int remainingWidth = base.ClientSize.Width - Theme.S(4);
			bool isInDetailRows = detailRows.Contains(entry);
			int scaledDimension2 = (isInDetailRows ? Theme.S(16) : 0);
			Rectangle r = new Rectangle(scaledDimension + scaledDimension2, y + Theme.S(3), remainingWidth - scaledDimension * 2 - scaledDimension2, remainingRowH);
			LogEventSeverity value = VisibleSeverity(entry.Severity);
			Color color = SeverityColor(value);
			Color baseFill = ((index == selected) ? Col.Lerp(Theme.Card, color, 0.11f) : Col.Lerp(Theme.Card, Theme.Inset, ((index & 1) == 0) ? 0.06f : 0.16f));
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(8)))
			{
				using (SolidBrush brush = new SolidBrush(Backdrop.CardFill(this, baseFill)))
				{
					g.FillPath(brush, path);
				}
				using (Pen pen = new Pen((index == selected) ? Col.Alpha(color, 170) : Theme.Stroke))
				{
					g.DrawPath(pen, path);
				}
			}
			using (Pen pen2 = new Pen(color, Math.Max(1.5f, Theme.S(2))))
			{
				g.DrawLine(pen2, r.Left, r.Top + Theme.S(11), r.Left, r.Bottom - Theme.S(11));
			}
			int left = r.Left + Theme.S(24);
			using (Pen pen3 = new Pen(Col.Alpha(Theme.StrokeHi, 80)))
			{
				g.DrawLine(pen3, left, r.Top, left, r.Bottom);
			}
			using (SolidBrush brush2 = new SolidBrush(Col.Alpha(color, 36)))
			{
				g.FillEllipse(brush2, left - Theme.S(7), r.Top + Theme.S(25) - Theme.S(7), Theme.S(14), Theme.S(14));
			}
			using (SolidBrush brush3 = new SolidBrush(color))
			{
				g.FillEllipse(brush3, left - Theme.S(3), r.Top + Theme.S(25) - Theme.S(3), Theme.S(6), Theme.S(6));
			}
			int leftOffset = r.Left + Theme.S(42);
			TextRenderer.DrawText(g, entry.Time, Theme.Mono(8f), new Rectangle(leftOffset, r.Top + Theme.S(9), Theme.S(76), Theme.S(19)), Theme.Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			string date = entry.Date;
			if (entry.RepeatCount > 1)
			{
				string substringResult = ((entry.FirstTime.Length >= 19) ? entry.FirstTime.Substring(11, 8) : entry.FirstTime);
				string substringResult2 = ((entry.LastTime.Length >= 19) ? entry.LastTime.Substring(11, 8) : entry.LastTime);
				date = substringResult + "–" + substringResult2;
				if (entry.FirstTime.Length >= 19 && entry.LastTime.Length >= 19 && entry.FirstTime.Substring(0, 10) != entry.LastTime.Substring(0, 10))
				{
					date = entry.FirstTime.Substring(5, 5) + "→" + entry.LastTime.Substring(5, 5);
				}
			}
			TextRenderer.DrawText(g, date, Theme.Mono((entry.RepeatCount > 1) ? 5.5f : 6.4f), new Rectangle(leftOffset, r.Top + Theme.S(30), Theme.S(76), Theme.S(16)), Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			Rectangle rectangle = new Rectangle(r.Left + Theme.S(122), r.Top + Theme.S(17), Theme.S(100), Theme.S(27));
			using (GraphicsPath path2 = Theme.TechPath(rectangle, Theme.S(6)))
			{
				using (SolidBrush brush4 = new SolidBrush(Col.Alpha(color, Theme.LightMode ? 22 : 30)))
				{
					g.FillPath(brush4, path2);
				}
				using (Pen pen4 = new Pen(Col.Alpha(color, 110)))
				{
					g.DrawPath(pen4, path2);
				}
			}
			TextRenderer.DrawText(g, entry.Module, Theme.UI(7.5f, true), rectangle, color, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
			int leftOffset2 = r.Left + Theme.S(240);
			int scaledDimension3 = Theme.S(52);
			string message = entry.Message;
			if (!isInDetailRows && entry.Details.Count > 0)
			{
				message = (expanded.Contains(entry.StableKey) ? "▾ " : "▸ ") + message;
			}
			TextRenderer.DrawText(g, message, Theme.UI(8.4f, false), new Rectangle(leftOffset2, r.Top + Theme.S(10), Math.Max(40, r.Right - leftOffset2 - scaledDimension3 - Theme.S(18)), r.Height - Theme.S(18)), Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
			TextRenderer.DrawText(g, SeverityCode(value), Theme.Mono(6.3f), new Rectangle(r.Right - scaledDimension3 - Theme.S(10), r.Top + Theme.S(8), scaledDimension3, Theme.S(17)), color, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}
	}
}
