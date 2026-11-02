using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PaviseApp
{
	internal static class LogEventProjection
	{
		private sealed class Boundary
		{
			internal string Token;

			internal string State;

			internal string Name;
		}

		private sealed class Bucket
		{
			internal LogEventEntry Row;

			internal int LastIndex;

			internal readonly HashSet<string> Actions = new HashSet<string>(StringComparer.Ordinal);
		}

		private static readonly Regex CaptureTelemetry = new Regex("(?<![A-Za-z0-9_])(eventCount|sampleCpuMs|systemEvents|retryMs)=[0-9]+(?:\\.[0-9]+)?(?=$|[\\s\\]])", RegexOptions.CultureInvariant);

		internal static void AssignSessions(IList<LogEventEntry> chronological)
		{
			if (chronological == null)
			{
				return;
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
			Dictionary<string, string> dictionary2 = new Dictionary<string, string>(StringComparer.Ordinal);
			Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
			string text = "";
			string text2 = "";
			string text3 = "";
			int num = -1;
			bool flag = false;
			for (int i = 0; i < chronological.Count; i++)
			{
				LogEventEntry logEventEntry = chronological[i];
				if (logEventEntry == null)
				{
					continue;
				}
				logEventEntry.SessionToken = (logEventEntry.SessionName = (logEventEntry.Phase = ""));
				string text4 = Body(logEventEntry.Raw);
				Boundary boundary = SessionBoundary(text4);
				string token;
				string action;
				string state;
				bool flag2 = Auto(text4, out token, out action, out state);
				string remaining;
				bool flag3 = StripPrefix(text4, new string[2] { "游戏模式激活 检测到 ", "Game mode activated, detected " }, out remaining);
				string remaining2;
				bool flag4 = StripPrefix(text4, new string[2] { "游戏模式 检测目标变更 ", "Game mode detection target changed " }, out remaining2);
				if (boundary != null)
				{
					if (boundary.State == "start")
					{
						string text5 = text;
						if (text5.StartsWith("legacy:", StringComparison.Ordinal) && num < 0 && Phase(dictionary2, text5) == "active" && (boundary.Name.Length == 0 || Name(dictionary, text5) == boundary.Name))
						{
							Remap(chronological, i, text5, boundary.Token);
						}
						text = boundary.Token;
						text2 = "";
						if (!string.IsNullOrEmpty(boundary.Name))
						{
							dictionary[text] = boundary.Name;
						}
						else if (!string.IsNullOrEmpty(text3))
						{
							dictionary[text] = text3;
						}
						else if (dictionary.ContainsKey(text5))
						{
							dictionary[text] = dictionary[text5];
						}
						dictionary2[text] = "active";
						AttachPendingChange(chronological, num, text, Name(dictionary, text));
						text3 = "";
						num = -1;
						flag = false;
					}
					else
					{
						if (!string.IsNullOrEmpty(boundary.Name))
						{
							dictionary[boundary.Token] = boundary.Name;
						}
						dictionary2[boundary.Token] = ((boundary.State == "end") ? "restore" : boundary.State);
					}
					logEventEntry.SessionToken = boundary.Token;
					logEventEntry.SessionName = Name(dictionary, boundary.Token);
					logEventEntry.Phase = boundary.State;
					if (boundary.State == "restored" || boundary.State == "restore-failed")
					{
						if (text == boundary.Token)
						{
							text = "";
						}
						text2 = ((boundary.State == "restore-failed") ? boundary.Token : "");
					}
					else if (boundary.State == "end" && text.Length == 0)
					{
						text = boundary.Token;
					}
					continue;
				}
				if (flag4)
				{
					text3 = remaining2;
					num = i;
					flag = false;
				}
				else if (flag3)
				{
					text = SyntheticToken(logEventEntry, remaining, counts);
					dictionary[text] = remaining;
					dictionary2[text] = "active";
					text2 = "";
				}
				if (flag2)
				{
					bool flag5 = IsSessionAction(action);
					if (flag5 && IsStarted(state))
					{
						string text6 = text;
						if (text6.StartsWith("legacy:", StringComparison.Ordinal) && num < 0 && Phase(dictionary2, text6) == "active")
						{
							Remap(chronological, i, text6, token);
						}
						text = token;
						if (!dictionary.ContainsKey(text))
						{
							dictionary[text] = ((text3.Length > 0) ? text3 : Name(dictionary, text6));
						}
						dictionary2[text] = "active";
						text2 = "";
						AttachPendingChange(chronological, num, text, Name(dictionary, text));
						text3 = "";
						num = -1;
						flag = false;
					}
					else if (token.Length > 0 && text.Length == 0 && !dictionary2.ContainsKey(token))
					{
						text = token;
						dictionary2[text] = "fragment";
					}
					logEventEntry.SessionToken = token;
					logEventEntry.SessionName = Name(dictionary, token);
					logEventEntry.Phase = Phase(dictionary2, token);
					if (flag5 && (IsRestored(state) || IsRestoreFailed(state)))
					{
						logEventEntry.Phase = (IsRestored(state) ? "restored" : "restore-failed");
						dictionary2[token] = logEventEntry.Phase;
						if (text == token)
						{
							text = "";
						}
						text2 = (IsRestoreFailed(state) ? token : "");
					}
					else if (flag5 && IsStarted(state))
					{
						logEventEntry.Phase = "start";
					}
					continue;
				}
				if (flag && !flag4 && !ReliableStartAhead(chronological, i))
				{
					text = SyntheticToken(chronological[num], text3, counts);
					dictionary[text] = text3;
					dictionary2[text] = "active";
					AttachPendingChange(chronological, num, text, text3);
					text3 = "";
					num = -1;
					flag = false;
				}
				string text7 = text;
				if (text7.Length == 0 && text2.Length > 0 && RestorationText(text4))
				{
					text7 = text2;
				}
				logEventEntry.SessionToken = text7;
				logEventEntry.SessionName = Name(dictionary, text7);
				logEventEntry.Phase = Phase(dictionary2, text7);
				if (flag3)
				{
					logEventEntry.Phase = "start";
				}
				if (text4.StartsWith("本局结束 ", StringComparison.Ordinal) || text4.StartsWith("Match ended ", StringComparison.Ordinal))
				{
					if (text7.Length > 0)
					{
						dictionary2[text7] = "restore";
					}
					logEventEntry.Phase = "end";
					if (num >= 0)
					{
						flag = true;
					}
				}
				else if (text4.StartsWith("游戏模式解除 ", StringComparison.Ordinal) || text4.StartsWith("Game mode ended ", StringComparison.Ordinal))
				{
					logEventEntry.Phase = "restore";
					if (text7.StartsWith("legacy:", StringComparison.Ordinal))
					{
						text = "";
						text2 = text7;
					}
				}
			}
			for (int j = 0; j < chronological.Count; j++)
			{
				if (chronological[j] != null && chronological[j].SessionToken.Length > 0)
				{
					chronological[j].SessionName = Name(dictionary, chronological[j].SessionToken);
				}
			}
		}

		internal static List<LogEventEntry> Project(IList<LogEventEntry> chronological, int filter, string activeSession)
		{
			List<LogEventEntry> list = new List<LogEventEntry>();
			if (chronological == null || chronological.Count == 0)
			{
				return list;
			}
			string text = SelectSession(chronological, activeSession);
			if (filter == 4 && text.Length == 0)
			{
				return list;
			}
			int num = -1;
			int num2 = chronological.Count;
			if (text.Length > 0)
			{
				for (int i = 0; i < chronological.Count; i++)
				{
					LogEventEntry logEventEntry = chronological[i];
					if (logEventEntry != null && logEventEntry.SessionToken == text)
					{
						num = i;
						break;
					}
				}
				for (int j = num + 1; j < chronological.Count; j++)
				{
					LogEventEntry logEventEntry2 = chronological[j];
					if (logEventEntry2 != null && logEventEntry2.SessionToken.Length > 0 && logEventEntry2.SessionToken != text && logEventEntry2.Phase == "start")
					{
						num2 = j;
						break;
					}
				}
			}
			if (filter == 4 && num < 0)
			{
				return list;
			}
			List<Bucket> list2 = new List<Bucket>();
			Dictionary<string, Bucket> dictionary = new Dictionary<string, Bucket>(StringComparer.Ordinal);
			Dictionary<string, string> dictionary2 = new Dictionary<string, string>(StringComparer.Ordinal);
			Dictionary<string, Bucket> dictionary3 = new Dictionary<string, Bucket>(StringComparer.Ordinal);
			Dictionary<string, Bucket> dictionary4 = new Dictionary<string, Bucket>(StringComparer.Ordinal);
			Dictionary<string, string> dictionary5 = new Dictionary<string, string>(StringComparer.Ordinal);
			Dictionary<string, int> dictionary6 = new Dictionary<string, int>(StringComparer.Ordinal);
			Dictionary<string, string> dictionary7 = new Dictionary<string, string>(StringComparer.Ordinal);
			Dictionary<string, int> dictionary8 = new Dictionary<string, int>(StringComparer.Ordinal);
			Dictionary<string, int> occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
			for (int k = 0; k < chronological.Count; k++)
			{
				LogEventEntry logEventEntry3 = chronological[k];
				if (logEventEntry3 == null)
				{
					continue;
				}
				bool flag = logEventEntry3.Severity == LogEventSeverity.Warning || logEventEntry3.Severity == LogEventSeverity.Error;
				string text2 = Body(logEventEntry3.Raw);
				string token;
				string action;
				string state;
				bool flag2 = Auto(text2, out token, out action, out state);
				string text3 = logEventEntry3.SessionToken + "\u001f" + logEventEntry3.Module;
				string[] array = new string[5];
				int severity = (int)logEventEntry3.Severity;
				array[0] = severity.ToString(CultureInfo.InvariantCulture);
				array[1] = "\u001f";
				array[2] = logEventEntry3.Phase;
				array[3] = "\u001f";
				array[4] = CaptureTelemetry.Replace(text2, "$1=*");
				string text4 = string.Concat(array);
				string value;
				bool flag3 = !dictionary2.TryGetValue(text3, out value) || value != text4;
				dictionary2[text3] = text4;
				int value2;
				dictionary8.TryGetValue(logEventEntry3.SessionToken, out value2);
				bool flag4 = false;
				if (flag2)
				{
					string key = logEventEntry3.SessionToken + "\u001f" + action;
					flag4 = dictionary7.TryGetValue(key, out value) && value != state;
					dictionary7[key] = state;
				}
				bool flag5 = logEventEntry3.SessionToken.Length > 0 && (SessionBoundary(text2) != null || (flag2 && IsSessionAction(action)) || (logEventEntry3.Phase == "start" && (text2.StartsWith("游戏模式", StringComparison.Ordinal) || text2.StartsWith("Game mode ", StringComparison.Ordinal))) || (logEventEntry3.Phase == "end" && (text2.StartsWith("本局结束 ", StringComparison.Ordinal) || text2.StartsWith("Match ended ", StringComparison.Ordinal)))) && (logEventEntry3.Phase == "start" || logEventEntry3.Phase == "end" || logEventEntry3.Phase == "restored" || logEventEntry3.Phase == "restore-failed");
				int value3;
				dictionary6.TryGetValue(logEventEntry3.SessionToken, out value3);
				if (flag5)
				{
					if (dictionary5.TryGetValue(logEventEntry3.SessionToken, out value) && value != logEventEntry3.Phase)
					{
						value3++;
						dictionary6[logEventEntry3.SessionToken] = value3;
					}
					dictionary5[logEventEntry3.SessionToken] = logEventEntry3.Phase;
				}
				if ((filter == 1 && !flag) || (filter == 2 && logEventEntry3.Severity != LogEventSeverity.Error) || (filter == 3 && !flag2) || (filter == 4 && text.Length > 0 && logEventEntry3.SessionToken != text && (!flag || logEventEntry3.SessionToken.Length != 0 || k < num || k >= num2)))
				{
					continue;
				}
				if (flag5)
				{
					string key2 = logEventEntry3.SessionToken + "\u001f" + logEventEntry3.Phase + "\u001f" + value3;
					Bucket value4;
					if (!dictionary4.TryGetValue(key2, out value4))
					{
						value4 = Create(logEventEntry3, k, "boundary:", occurrences);
						value4.Row.Details.Add(Copy(logEventEntry3));
						dictionary4[key2] = value4;
						list2.Add(value4);
					}
					else
					{
						Append(value4, logEventEntry3, k);
					}
					value4.Row.Module = Text("对局", "SESSION", "セッション");
					value4.Row.Message = BoundaryMessage(logEventEntry3.SessionName, logEventEntry3.Phase);
					value4.Row.Severity = BoundarySeverity(value4.Row.Severity, logEventEntry3.Severity, logEventEntry3.Phase);
				}
				else if ((!flag2 && logEventEntry3.Severity == LogEventSeverity.Success && logEventEntry3.SessionToken.Length > 0 && (logEventEntry3.Phase == "active" || logEventEntry3.Phase == "restore") && !string.Equals(logEventEntry3.Module, "FRAME", StringComparison.OrdinalIgnoreCase) && !string.Equals(logEventEntry3.Module, "SESSION", StringComparison.OrdinalIgnoreCase)) || (logEventEntry3.Severity == LogEventSeverity.Success && flag2 && logEventEntry3.SessionToken.Length > 0 && !IsSessionAction(action) && (IsApplied(state) || IsRestored(state))))
				{
					string text5 = ((!flag2) ? ((logEventEntry3.Phase == "restore" || RestorationText(text2)) ? "restored" : "applied") : (IsApplied(state) ? "applied" : "restored"));
					string key3 = logEventEntry3.SessionToken + "\u001f" + logEventEntry3.Phase + "\u001f" + text5 + "\u001f" + value2;
					Bucket value5;
					if (flag4 && dictionary3.TryGetValue(key3, out value5) && value5.Actions.Contains(action))
					{
						value2++;
						dictionary8[logEventEntry3.SessionToken] = value2;
						key3 = logEventEntry3.SessionToken + "\u001f" + logEventEntry3.Phase + "\u001f" + text5 + "\u001f" + value2;
					}
					if (!dictionary3.TryGetValue(key3, out value5))
					{
						value5 = Create(logEventEntry3, k, "summary:", occurrences);
						value5.Row.IsSummary = true;
						value5.Row.Module = Text("对局摘要", "SESSION", "セッション");
						value5.Row.Details.Add(Copy(logEventEntry3));
						dictionary3[key3] = value5;
						list2.Add(value5);
					}
					else
					{
						Append(value5, logEventEntry3, k);
					}
					if (flag2)
					{
						value5.Actions.Add(action);
					}
					value5.Row.Message = SummaryMessage(value5.Row.SessionName, value5.Row.Phase, text5, value5.Actions.Count);
				}
				else
				{
					string key4 = text3 + "\u001f" + text4;
					Bucket value6;
					if (!flag3 && dictionary.TryGetValue(key4, out value6))
					{
						Append(value6, logEventEntry3, k);
						value6.Row.Message = logEventEntry3.Message;
					}
					else
					{
						value6 = (dictionary[key4] = Create(logEventEntry3, k, "event:", occurrences));
						list2.Add(value6);
					}
				}
			}
			list2.Sort((Bucket a, Bucket b) => b.LastIndex.CompareTo(a.LastIndex));
			for (int l = 0; l < list2.Count; l++)
			{
				list.Add(list2[l].Row);
			}
			return list;
		}

		private static Bucket Create(LogEventEntry source, int index, string prefix, Dictionary<string, int> occurrences)
		{
			LogEventEntry logEventEntry = Copy(source);
			logEventEntry.FirstTime = (logEventEntry.LastTime = Stamp(source));
			logEventEntry.RepeatCount = 1;
			string text = prefix + source.SessionToken + "\u001f" + source.Raw;
			int value;
			occurrences.TryGetValue(text, out value);
			occurrences[text] = value + 1;
			logEventEntry.StableKey = text + "\u001f" + value.ToString(CultureInfo.InvariantCulture);
			Bucket bucket = new Bucket();
			bucket.Row = logEventEntry;
			bucket.LastIndex = index;
			return bucket;
		}

		private static void Append(Bucket bucket, LogEventEntry source, int index)
		{
			LogEventEntry row = bucket.Row;
			if (row.Details.Count == 0)
			{
				row.Details.Add(Copy(row));
			}
			row.Details.Add(Copy(source));
			row.Raw = row.Raw + "\r\n" + source.Raw;
			row.Date = source.Date;
			row.Time = source.Time;
			row.Timestamp = source.Timestamp;
			row.LastTime = Stamp(source);
			row.RepeatCount++;
			bucket.LastIndex = index;
		}

		private static LogEventEntry Copy(LogEventEntry e)
		{
			LogEventEntry logEventEntry = new LogEventEntry();
			logEventEntry.Raw = e.Raw;
			logEventEntry.Date = e.Date;
			logEventEntry.Time = e.Time;
			logEventEntry.Module = e.Module;
			logEventEntry.Message = e.Message;
			logEventEntry.Severity = e.Severity;
			logEventEntry.Timestamp = e.Timestamp;
			logEventEntry.SessionToken = e.SessionToken;
			logEventEntry.SessionName = e.SessionName;
			logEventEntry.Phase = e.Phase;
			logEventEntry.StableKey = e.StableKey;
			logEventEntry.FirstTime = e.FirstTime;
			logEventEntry.LastTime = e.LastTime;
			logEventEntry.RepeatCount = e.RepeatCount;
			logEventEntry.IsSummary = e.IsSummary;
			return logEventEntry;
		}

		private static string SelectSession(IList<LogEventEntry> events, string active)
		{
			if (!string.IsNullOrEmpty(active))
			{
				return active;
			}
			string text = "";
			string result = "";
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			for (int i = 0; i < events.Count; i++)
			{
				LogEventEntry logEventEntry = events[i];
				if (logEventEntry != null && logEventEntry.SessionToken.Length != 0)
				{
					result = logEventEntry.SessionToken;
					if (logEventEntry.Phase == "start" || (logEventEntry.Phase == "fragment" && hashSet.Add(logEventEntry.SessionToken)))
					{
						text = logEventEntry.SessionToken;
					}
				}
			}
			if (text.Length <= 0)
			{
				return result;
			}
			return text;
		}

		private static string SummaryMessage(string name, string phase, string state, int actions)
		{
			string text = ((state == "applied") ? Text("已应用", "Applied", "適用済み") : ((phase == "restore") ? Text("退局还原记录", "Exit restoration records", "終了時の復元記録") : Text("局部还原记录", "Individual restoration records", "個別の復元記録")));
			if (actions == 0)
			{
				return (string.IsNullOrEmpty(name) ? "" : (name + " · ")) + ((state == "restored") ? Text("还原记录", "Restoration records", "復元記録") : Text("执行记录", "Execution records", "実行記録"));
			}
			return (string.IsNullOrEmpty(name) ? "" : (name + " · ")) + text + " " + actions.ToString(CultureInfo.InvariantCulture) + Text(" 项", " actions", " 項目");
		}

		private static string BoundaryMessage(string name, string phase)
		{
			string text;
			switch (phase)
			{
			default:
				text = Text("还原未完成", "Restoration incomplete", "復元未完了");
				break;
			case "restored":
				text = Text("恢复检查通过", "Restoration checks passed", "復元チェック完了");
				break;
			case "end":
				text = Text("退局，开始还原", "Game ended; restoring", "ゲーム終了、復元を開始");
				break;
			case "start":
				text = Text("开始接管", "Management started", "管理を開始");
				break;
			}
			string text2 = text;
			return (string.IsNullOrEmpty(name) ? "" : (name + " · ")) + text2;
		}

		private static LogEventSeverity BoundarySeverity(LogEventSeverity existing, LogEventSeverity incoming, string phase)
		{
			if (existing == LogEventSeverity.Error || incoming == LogEventSeverity.Error)
			{
				return LogEventSeverity.Error;
			}
			if (phase == "restore-failed" || existing == LogEventSeverity.Warning || incoming == LogEventSeverity.Warning)
			{
				return LogEventSeverity.Warning;
			}
			if (phase == "restored" || existing == LogEventSeverity.Success || incoming == LogEventSeverity.Success)
			{
				return LogEventSeverity.Success;
			}
			return LogEventSeverity.Info;
		}

		private static string Stamp(LogEventEntry e)
		{
			if (!(e.Timestamp != DateTime.MinValue))
			{
				return e.Date + " " + e.Time;
			}
			return e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
		}

		private static string Body(string raw)
		{
			string text = (raw ?? "").Trim();
			DateTime result;
			if (text.Length >= 19 && DateTime.TryParseExact(text.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
			{
				text = text.Substring(19).Trim();
			}
			string[] array = new string[4] { "WARN ", "FAIL ", "INFO ", "PASS " };
			for (int i = 0; i < array.Length; i++)
			{
				if (text.StartsWith(array[i], StringComparison.Ordinal))
				{
					return text.Substring(array[i].Length).Trim();
				}
			}
			return text;
		}

		private static Boundary SessionBoundary(string body)
		{
			if (!body.StartsWith("SESSION [", StringComparison.Ordinal))
			{
				return null;
			}
			int num = body.IndexOf(']', 9);
			if (num <= 9)
			{
				return null;
			}
			int num2 = body.IndexOf('|', num + 1);
			string text = ((num2 < 0) ? body.Substring(num + 1) : body.Substring(num + 1, num2 - num - 1)).Trim();
			if (text != "start" && text != "end" && text != "restored" && text != "restore-failed")
			{
				return null;
			}
			Boundary boundary = new Boundary();
			boundary.Token = body.Substring(9, num - 9);
			boundary.State = text;
			boundary.Name = ((num2 < 0) ? "" : body.Substring(num2 + 1).Trim());
			return boundary;
		}

		private static bool Auto(string body, out string token, out string action, out string state)
		{
			token = (action = (state = ""));
			if (!body.StartsWith("AUTO [", StringComparison.Ordinal))
			{
				return false;
			}
			int num = body.IndexOf(']', 6);
			int num2 = ((num >= 0) ? body.IndexOf('|', num + 1) : (-1));
			if (num < 6)
			{
				return false;
			}
			token = body.Substring(6, num - 6);
			if (num2 <= num + 1)
			{
				return true;
			}
			action = body.Substring(num + 1, num2 - num - 1).Trim();
			state = body.Substring(num2 + 1).Trim();
			return true;
		}

		private static bool IsSessionAction(string action)
		{
			switch (action)
			{
			default:
				return action == Lang.T("auto.log.session");
			case "session":
			case "本次对局":
			case "Game session":
				return true;
			}
		}

		private static bool IsStarted(string state)
		{
			switch (state)
			{
			default:
				return state == Lang.T("auto.log.started");
			case "started":
			case "已开始接管":
			case "Session management started":
				return true;
			}
		}

		private static bool IsApplied(string state)
		{
			switch (state)
			{
			default:
				return state == Lang.T("auto.log.applied");
			case "applied":
			case "已应用":
			case "Applied":
				return true;
			}
		}

		private static bool IsRestored(string state)
		{
			switch (state)
			{
			default:
				return state == Lang.T("auto.log.restored");
			case "restored":
			case "已还原":
			case "Restored":
				return true;
			}
		}

		private static bool IsRestoreFailed(string state)
		{
			switch (state)
			{
			default:
				return state == Lang.T("auto.log.restore-failed");
			case "restore-failed":
			case "还原失败":
			case "Could not restore":
				return true;
			}
		}

		private static bool StripPrefix(string body, string[] prefixes, out string remaining)
		{
			for (int i = 0; i < prefixes.Length; i++)
			{
				if (body.StartsWith(prefixes[i], StringComparison.Ordinal))
				{
					remaining = body.Substring(prefixes[i].Length).Trim();
					return true;
				}
			}
			remaining = "";
			return false;
		}

		private static string SyntheticToken(LogEventEntry e, string name, Dictionary<string, int> counts)
		{
			string text = Stamp(e) + "\u001f" + name;
			int value;
			counts.TryGetValue(text, out value);
			counts[text] = value + 1;
			return "legacy:" + text + "\u001f" + value.ToString(CultureInfo.InvariantCulture);
		}

		private static void Remap(IList<LogEventEntry> events, int end, string old, string replacement)
		{
			for (int num = end - 1; num >= 0; num--)
			{
				if (events[num] != null && events[num].SessionToken == old)
				{
					events[num].SessionToken = replacement;
				}
			}
		}

		private static void AttachPendingChange(IList<LogEventEntry> events, int index, string token, string name)
		{
			if (index >= 0 && index < events.Count && events[index] != null)
			{
				events[index].SessionToken = token;
				events[index].SessionName = name;
				events[index].Phase = "start";
			}
		}

		private static bool ReliableStartAhead(IList<LogEventEntry> events, int from)
		{
			for (int i = from; i < events.Count; i++)
			{
				if (events[i] != null)
				{
					string text = Body(events[i].Raw);
					Boundary boundary = SessionBoundary(text);
					if (boundary != null && boundary.State == "start")
					{
						return true;
					}
					string token;
					string action;
					string state;
					if (Auto(text, out token, out action, out state) && IsSessionAction(action) && IsStarted(state))
					{
						return true;
					}
					if (text.StartsWith("游戏模式激活 检测到 ", StringComparison.Ordinal) || text.StartsWith("Game mode activated, detected ", StringComparison.Ordinal) || text.StartsWith("游戏模式 检测目标变更 ", StringComparison.Ordinal) || text.StartsWith("Game mode detection target changed ", StringComparison.Ordinal) || text.StartsWith("游戏模式解除 ", StringComparison.Ordinal) || text.StartsWith("Game mode ended ", StringComparison.Ordinal))
					{
						return false;
					}
				}
			}
			return false;
		}

		private static string Name(Dictionary<string, string> names, string token)
		{
			string value;
			if (!names.TryGetValue(token, out value))
			{
				return "";
			}
			return value;
		}

		private static string Phase(Dictionary<string, string> phases, string token)
		{
			string value;
			if (!phases.TryGetValue(token, out value))
			{
				return "";
			}
			return value;
		}

		private static bool RestorationText(string body)
		{
			if (body.IndexOf("还原", StringComparison.Ordinal) < 0 && body.IndexOf("恢复", StringComparison.Ordinal) < 0)
			{
				return body.IndexOf("restore", StringComparison.OrdinalIgnoreCase) >= 0;
			}
			return true;
		}

		private static string Text(string chinese, string english, string japanese)
		{
			if (Lang.Cur != 1)
			{
				if (Lang.Cur != 2)
				{
					return chinese;
				}
				return japanese;
			}
			return english;
		}
	}
}
