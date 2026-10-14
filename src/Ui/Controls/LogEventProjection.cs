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
            internal string Token, State, Name;
        }

        private sealed class Bucket
        {
            internal LogEventEntry Row;
            internal int LastIndex;
            internal readonly HashSet<string> Actions = new HashSet<string>(StringComparer.Ordinal);
        }

        // 警告归并仅忽略易变采样值，保留原因、身份和丢失计数。
        private static readonly Regex CaptureTelemetry = new Regex(
            @"(?<![A-Za-z0-9_])(eventCount|sampleCpuMs|systemEvents|retryMs)=[0-9]+(?:\.[0-9]+)?(?=$|[\s\]])",
            RegexOptions.CultureInvariant);

        internal static void AssignSessions(IList<LogEventEntry> chronological)
        {
            if (chronological == null) return;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var phases = new Dictionary<string, string>(StringComparer.Ordinal);
            var syntheticCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            string current = "", pendingRecovery = "", pendingName = "";
            int pendingChange = -1;
            bool changedAfterReport = false;

            for (int i = 0; i < chronological.Count; i++)
            {
                LogEventEntry e = chronological[i];
                if (e == null) continue;
                e.SessionToken = e.SessionName = e.Phase = "";
                string body = Body(e.Raw);
                Boundary boundary = SessionBoundary(body);
                string autoToken, action, state;
                bool automatic = Auto(body, out autoToken, out action, out state);
                string detected;
                bool activated = StripPrefix(body, new[] { "游戏模式激活 检测到 ", "Game mode activated, detected " }, out detected);
                string changedName;
                bool changed = StripPrefix(body, new[] { "游戏模式 检测目标变更 ", "Game mode detection target changed " }, out changedName);

                if (boundary != null)
                {
                    if (boundary.State == "start")
                    {
                        string old = current;
                        if (old.StartsWith("legacy:", StringComparison.Ordinal) && pendingChange < 0
                            && Phase(phases, old) == "active" && (boundary.Name.Length == 0 || Name(names, old) == boundary.Name))
                            Remap(chronological, i, old, boundary.Token);
                        current = boundary.Token;
                        pendingRecovery = "";
                        if (!string.IsNullOrEmpty(boundary.Name)) names[current] = boundary.Name;
                        else if (!string.IsNullOrEmpty(pendingName)) names[current] = pendingName;
                        else if (names.ContainsKey(old)) names[current] = names[old];
                        phases[current] = "active";
                        AttachPendingChange(chronological, pendingChange, current, Name(names, current));
                        pendingName = ""; pendingChange = -1; changedAfterReport = false;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(boundary.Name)) names[boundary.Token] = boundary.Name;
                        phases[boundary.Token] = boundary.State == "end" ? "restore" : boundary.State;
                    }
                    e.SessionToken = boundary.Token;
                    e.SessionName = Name(names, boundary.Token);
                    e.Phase = boundary.State;
                    if (boundary.State == "restored" || boundary.State == "restore-failed")
                    {
                        if (current == boundary.Token) current = "";
                        pendingRecovery = boundary.State == "restore-failed" ? boundary.Token : "";
                    }
                    else if (boundary.State == "end" && current.Length == 0)
                        current = boundary.Token;
                    continue;
                }

                if (changed)
                {
                    pendingName = changedName;
                    pendingChange = i;
                    changedAfterReport = false;
                }
                else if (activated)
                {
                    current = SyntheticToken(e, detected, syntheticCounts);
                    names[current] = detected;
                    phases[current] = "active";
                    pendingRecovery = "";
                }

                if (automatic)
                {
                    bool sessionAction = IsSessionAction(action);
                    if (sessionAction && IsStarted(state))
                    {
                        string old = current;
                        if (old.StartsWith("legacy:", StringComparison.Ordinal) && pendingChange < 0 && Phase(phases, old) == "active")
                            Remap(chronological, i, old, autoToken);
                        current = autoToken;
                        if (!names.ContainsKey(current)) names[current] = pendingName.Length > 0 ? pendingName : Name(names, old);
                        phases[current] = "active";
                        pendingRecovery = "";
                        AttachPendingChange(chronological, pendingChange, current, Name(names, current));
                        pendingName = ""; pendingChange = -1; changedAfterReport = false;
                    }
                    else if (autoToken.Length > 0 && current.Length == 0 && !phases.ContainsKey(autoToken))
                    {
                        current = autoToken;
                        phases[current] = "fragment";
                    }
                    e.SessionToken = autoToken;
                    e.SessionName = Name(names, autoToken);
                    e.Phase = Phase(phases, autoToken);
                    if (sessionAction && (IsRestored(state) || IsRestoreFailed(state)))
                    {
                        e.Phase = IsRestored(state) ? "restored" : "restore-failed";
                        phases[autoToken] = e.Phase;
                        if (current == autoToken) current = "";
                        pendingRecovery = IsRestoreFailed(state) ? autoToken : "";
                    }
                    else if (sessionAction && IsStarted(state)) e.Phase = "start";
                    continue;
                }

                if (changedAfterReport && !changed && !ReliableStartAhead(chronological, i))
                {
                    current = SyntheticToken(chronological[pendingChange], pendingName, syntheticCounts);
                    names[current] = pendingName;
                    phases[current] = "active";
                    AttachPendingChange(chronological, pendingChange, current, pendingName);
                    pendingName = ""; pendingChange = -1; changedAfterReport = false;
                }

                string owner = current;
                if (owner.Length == 0 && pendingRecovery.Length > 0 && RestorationText(body)) owner = pendingRecovery;
                e.SessionToken = owner;
                e.SessionName = Name(names, owner);
                e.Phase = Phase(phases, owner);
                if (activated) e.Phase = "start";
                if (body.StartsWith("本局结束 ", StringComparison.Ordinal) || body.StartsWith("Match ended ", StringComparison.Ordinal))
                {
                    if (owner.Length > 0) phases[owner] = "restore";
                    e.Phase = "end";
                    if (pendingChange >= 0) changedAfterReport = true;
                }
                // 旧恢复提示只证明尝试过，不能推断整局恢复成功。
                else if (body.StartsWith("游戏模式解除 ", StringComparison.Ordinal) || body.StartsWith("Game mode ended ", StringComparison.Ordinal))
                {
                    e.Phase = "restore";
                    if (owner.StartsWith("legacy:", StringComparison.Ordinal)) { current = ""; pendingRecovery = owner; }
                }
            }

            for (int i = 0; i < chronological.Count; i++)
                if (chronological[i] != null && chronological[i].SessionToken.Length > 0)
                    chronological[i].SessionName = Name(names, chronological[i].SessionToken);
        }

        internal static List<LogEventEntry> Project(IList<LogEventEntry> chronological, int filter, string activeSession)
        {
            var output = new List<LogEventEntry>();
            if (chronological == null || chronological.Count == 0) return output;
            string session = SelectSession(chronological, activeSession);
            if (filter == 4 && session.Length == 0) return output;
            int sessionStart = -1, nextSession = chronological.Count;
            if (session.Length > 0)
            {
                for (int i = 0; i < chronological.Count; i++)
                {
                    LogEventEntry item = chronological[i];
                    if (item != null && item.SessionToken == session) { sessionStart = i; break; }
                }
                for (int i = sessionStart + 1; i < chronological.Count; i++)
                {
                    LogEventEntry item = chronological[i];
                    if (item != null && item.SessionToken.Length > 0 && item.SessionToken != session && item.Phase == "start")
                    { nextSession = i; break; }
                }
            }

            if (filter == 4 && sessionStart < 0) return output;

            var buckets = new List<Bucket>();
            var repeat = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            var lastReason = new Dictionary<string, string>(StringComparer.Ordinal);
            var summaries = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            var boundaries = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            var lastBoundary = new Dictionary<string, string>(StringComparer.Ordinal);
            var boundaryGenerations = new Dictionary<string, int>(StringComparer.Ordinal);
            var actionStates = new Dictionary<string, string>(StringComparer.Ordinal);
            var summaryGenerations = new Dictionary<string, int>(StringComparer.Ordinal);
            var keyOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < chronological.Count; i++)
            {
                LogEventEntry source = chronological[i];
                if (source == null) continue;
                bool problem = source.Severity == LogEventSeverity.Warning || source.Severity == LogEventSeverity.Error;
                string body = Body(source.Raw), token, action, state;
                bool automatic = Auto(body, out token, out action, out state);

                string lane = source.SessionToken + "\u001f" + source.Module;
                string reason = ((int)source.Severity).ToString(CultureInfo.InvariantCulture) + "\u001f" + source.Phase + "\u001f" + CaptureTelemetry.Replace(body, "$1=*");
                string previous;
                bool reasonChanged = !lastReason.TryGetValue(lane, out previous) || previous != reason;
                lastReason[lane] = reason;

                int generation;
                summaryGenerations.TryGetValue(source.SessionToken, out generation);
                bool actionChanged = false;
                if (automatic)
                {
                    string actionKey = source.SessionToken + "\u001f" + action;
                    actionChanged = actionStates.TryGetValue(actionKey, out previous) && previous != state;
                    actionStates[actionKey] = state;
                }
                bool boundaryRow = source.SessionToken.Length > 0 && (SessionBoundary(body) != null
                    || automatic && IsSessionAction(action)
                    || source.Phase == "start" && (body.StartsWith("游戏模式", StringComparison.Ordinal) || body.StartsWith("Game mode ", StringComparison.Ordinal))
                    || source.Phase == "end" && (body.StartsWith("本局结束 ", StringComparison.Ordinal) || body.StartsWith("Match ended ", StringComparison.Ordinal)));
                bool knownBoundary = boundaryRow && (source.Phase == "start" || source.Phase == "end" || source.Phase == "restored" || source.Phase == "restore-failed");
                int boundaryGeneration;
                boundaryGenerations.TryGetValue(source.SessionToken, out boundaryGeneration);
                if (knownBoundary)
                {
                    if (lastBoundary.TryGetValue(source.SessionToken, out previous) && previous != source.Phase)
                    { boundaryGeneration++; boundaryGenerations[source.SessionToken] = boundaryGeneration; }
                    lastBoundary[source.SessionToken] = source.Phase;
                }
                // 被过滤的恢复行仍切断警告分组。
                if (filter == 1 && !problem) continue;
                if (filter == 2 && source.Severity != LogEventSeverity.Error) continue;
                if (filter == 3 && !automatic) continue;
                if (filter == 4 && session.Length > 0 && source.SessionToken != session
                    && !(problem && source.SessionToken.Length == 0 && i >= sessionStart && i < nextSession)) continue;
                if (knownBoundary)
                {
                    string boundaryKey = source.SessionToken + "\u001f" + source.Phase + "\u001f" + boundaryGeneration;
                    Bucket grouped;
                    if (!boundaries.TryGetValue(boundaryKey, out grouped))
                    {
                        grouped = Create(source, i, "boundary:", keyOccurrences);
                        grouped.Row.Details.Add(Copy(source));
                        boundaries[boundaryKey] = grouped;
                        buckets.Add(grouped);
                    }
                    else Append(grouped, source, i);
                    grouped.Row.Module = Text("对局", "SESSION", "セッション");
                    grouped.Row.Message = BoundaryMessage(source.SessionName, source.Phase);
                    grouped.Row.Severity = BoundarySeverity(grouped.Row.Severity, source.Severity, source.Phase);
                    continue;
                }

                bool plainSuccess = !automatic && source.Severity == LogEventSeverity.Success
                    && source.SessionToken.Length > 0 && (source.Phase == "active" || source.Phase == "restore")
                    && !string.Equals(source.Module, "FRAME", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(source.Module, "SESSION", StringComparison.OrdinalIgnoreCase);
                bool summarize = plainSuccess || source.Severity == LogEventSeverity.Success && automatic && source.SessionToken.Length > 0
                    && !IsSessionAction(action) && (IsApplied(state) || IsRestored(state));
                if (summarize)
                {
                    string canonicalState = automatic ? (IsApplied(state) ? "applied" : "restored")
                        : source.Phase == "restore" || RestorationText(body) ? "restored" : "applied";
                    string summaryKey = source.SessionToken + "\u001f" + source.Phase + "\u001f" + canonicalState + "\u001f" + generation;
                    Bucket summary;
                    if (actionChanged && summaries.TryGetValue(summaryKey, out summary) && summary.Actions.Contains(action))
                    {
                        generation++;
                        summaryGenerations[source.SessionToken] = generation;
                        summaryKey = source.SessionToken + "\u001f" + source.Phase + "\u001f" + canonicalState + "\u001f" + generation;
                    }
                    if (!summaries.TryGetValue(summaryKey, out summary))
                    {
                        summary = Create(source, i, "summary:", keyOccurrences);
                        summary.Row.IsSummary = true;
                        summary.Row.Module = Text("对局摘要", "SESSION", "セッション");
                        summary.Row.Details.Add(Copy(source));
                        summaries[summaryKey] = summary;
                        buckets.Add(summary);
                    }
                    else Append(summary, source, i);
                    if (automatic) summary.Actions.Add(action);
                    summary.Row.Message = SummaryMessage(summary.Row.SessionName, summary.Row.Phase, canonicalState, summary.Actions.Count);
                    continue;
                }

                // 恢复后再次失败应形成新组。
                string repeatKey = lane + "\u001f" + reason;
                Bucket repeated;
                if (!reasonChanged && repeat.TryGetValue(repeatKey, out repeated))
                {
                    Append(repeated, source, i);
                    repeated.Row.Message = source.Message;
                }
                else
                {
                    repeated = Create(source, i, "event:", keyOccurrences);
                    repeat[repeatKey] = repeated;
                    buckets.Add(repeated);
                }
            }

            buckets.Sort(delegate(Bucket a, Bucket b) { return b.LastIndex.CompareTo(a.LastIndex); });
            for (int i = 0; i < buckets.Count; i++) output.Add(buckets[i].Row);
            return output;
        }

        private static Bucket Create(LogEventEntry source, int index, string prefix, Dictionary<string, int> occurrences)
        {
            LogEventEntry copy = Copy(source);
            copy.FirstTime = copy.LastTime = Stamp(source);
            copy.RepeatCount = 1;
            string key = prefix + source.SessionToken + "\u001f" + source.Raw;
            int occurrence;
            occurrences.TryGetValue(key, out occurrence);
            occurrences[key] = occurrence + 1;
            copy.StableKey = key + "\u001f" + occurrence.ToString(CultureInfo.InvariantCulture);
            return new Bucket { Row = copy, LastIndex = index };
        }

        private static void Append(Bucket bucket, LogEventEntry source, int index)
        {
            LogEventEntry row = bucket.Row;
            if (row.Details.Count == 0) row.Details.Add(Copy(row));
            row.Details.Add(Copy(source));
            row.Raw += "\r\n" + source.Raw;
            row.Date = source.Date; row.Time = source.Time; row.Timestamp = source.Timestamp;
            row.LastTime = Stamp(source);
            row.RepeatCount++;
            bucket.LastIndex = index;
        }

        private static LogEventEntry Copy(LogEventEntry e)
        {
            return new LogEventEntry { Raw = e.Raw, Date = e.Date, Time = e.Time, Module = e.Module,
                Message = e.Message, Severity = e.Severity, Timestamp = e.Timestamp, SessionToken = e.SessionToken,
                SessionName = e.SessionName, Phase = e.Phase, StableKey = e.StableKey,
                FirstTime = e.FirstTime, LastTime = e.LastTime, RepeatCount = e.RepeatCount, IsSummary = e.IsSummary };
        }

        private static string SelectSession(IList<LogEventEntry> events, string active)
        {
            if (!string.IsNullOrEmpty(active)) return active;
            string latestRecognized = "", lastSeen = "";
            var fragments = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < events.Count; i++)
            {
                LogEventEntry e = events[i];
                if (e == null || e.SessionToken.Length == 0) continue;
                lastSeen = e.SessionToken;
                if (e.Phase == "start" || e.Phase == "fragment" && fragments.Add(e.SessionToken)) latestRecognized = e.SessionToken;
            }
            return latestRecognized.Length > 0 ? latestRecognized : lastSeen;
        }

        private static string SummaryMessage(string name, string phase, string state, int actions)
        {
            string label = state == "applied" ? Text("已应用", "Applied", "適用済み")
                : phase == "restore" ? Text("退局还原记录", "Exit restoration records", "終了時の復元記録")
                : Text("局部还原记录", "Individual restoration records", "個別の復元記録");
            if (actions == 0)
                return (string.IsNullOrEmpty(name) ? "" : name + " · ")
                    + (state == "restored" ? Text("还原记录", "Restoration records", "復元記録") : Text("执行记录", "Execution records", "実行記録"));
            return (string.IsNullOrEmpty(name) ? "" : name + " · ") + label + " "
                + actions.ToString(CultureInfo.InvariantCulture) + Text(" 项", " actions", " 項目");
        }

        private static string BoundaryMessage(string name, string phase)
        {
            string label = phase == "start" ? Text("开始接管", "Management started", "管理を開始")
                : phase == "end" ? Text("退局，开始还原", "Game ended; restoring", "ゲーム終了、復元を開始")
                : phase == "restored" ? Text("恢复检查通过", "Restoration checks passed", "復元チェック完了")
                : Text("还原未完成", "Restoration incomplete", "復元未完了");
            return (string.IsNullOrEmpty(name) ? "" : name + " · ") + label;
        }

        private static LogEventSeverity BoundarySeverity(LogEventSeverity existing, LogEventSeverity incoming, string phase)
        {
            if (existing == LogEventSeverity.Error || incoming == LogEventSeverity.Error) return LogEventSeverity.Error;
            if (phase == "restore-failed" || existing == LogEventSeverity.Warning || incoming == LogEventSeverity.Warning) return LogEventSeverity.Warning;
            if (phase == "restored" || existing == LogEventSeverity.Success || incoming == LogEventSeverity.Success) return LogEventSeverity.Success;
            return LogEventSeverity.Info;
        }

        private static string Stamp(LogEventEntry e)
        {
            return e.Timestamp != DateTime.MinValue ? e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : e.Date + " " + e.Time;
        }

        private static string Body(string raw)
        {
            string body = (raw ?? "").Trim();
            DateTime ignored;
            if (body.Length >= 19 && DateTime.TryParseExact(body.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out ignored)) body = body.Substring(19).Trim();
            string[] tags = { "WARN ", "FAIL ", "INFO ", "PASS " };
            for (int i = 0; i < tags.Length; i++)
                if (body.StartsWith(tags[i], StringComparison.Ordinal)) return body.Substring(tags[i].Length).Trim();
            return body;
        }

        private static Boundary SessionBoundary(string body)
        {
            if (!body.StartsWith("SESSION [", StringComparison.Ordinal)) return null;
            int close = body.IndexOf(']', 9);
            if (close <= 9) return null;
            int separator = body.IndexOf('|', close + 1);
            string state = (separator < 0 ? body.Substring(close + 1) : body.Substring(close + 1, separator - close - 1)).Trim();
            if (state != "start" && state != "end" && state != "restored" && state != "restore-failed") return null;
            return new Boundary { Token = body.Substring(9, close - 9), State = state,
                Name = separator < 0 ? "" : body.Substring(separator + 1).Trim() };
        }

        private static bool Auto(string body, out string token, out string action, out string state)
        {
            token = action = state = "";
            if (!body.StartsWith("AUTO [", StringComparison.Ordinal)) return false;
            int close = body.IndexOf(']', 6);
            int separator = close >= 0 ? body.IndexOf('|', close + 1) : -1;
            if (close < 6) return false;
            token = body.Substring(6, close - 6);
            if (separator <= close + 1) return true;
            action = body.Substring(close + 1, separator - close - 1).Trim();
            state = body.Substring(separator + 1).Trim();
            return true;
        }

        private static bool IsSessionAction(string action)
        { return action == "session" || action == "本次对局" || action == "Game session" || action == Lang.T("auto.log.session"); }
        private static bool IsStarted(string state)
        { return state == "started" || state == "已开始接管" || state == "Session management started" || state == Lang.T("auto.log.started"); }
        private static bool IsApplied(string state)
        { return state == "applied" || state == "已应用" || state == "Applied" || state == Lang.T("auto.log.applied"); }
        private static bool IsRestored(string state)
        { return state == "restored" || state == "已还原" || state == "Restored" || state == Lang.T("auto.log.restored"); }
        private static bool IsRestoreFailed(string state)
        { return state == "restore-failed" || state == "还原失败" || state == "Could not restore" || state == Lang.T("auto.log.restore-failed"); }

        private static bool StripPrefix(string body, string[] prefixes, out string remaining)
        {
            for (int i = 0; i < prefixes.Length; i++)
                if (body.StartsWith(prefixes[i], StringComparison.Ordinal)) { remaining = body.Substring(prefixes[i].Length).Trim(); return true; }
            remaining = ""; return false;
        }

        private static string SyntheticToken(LogEventEntry e, string name, Dictionary<string, int> counts)
        {
            string anchor = Stamp(e) + "\u001f" + name;
            int count; counts.TryGetValue(anchor, out count); counts[anchor] = count + 1;
            return "legacy:" + anchor + "\u001f" + count.ToString(CultureInfo.InvariantCulture);
        }

        private static void Remap(IList<LogEventEntry> events, int end, string old, string replacement)
        {
            for (int i = end - 1; i >= 0; i--)
                if (events[i] != null && events[i].SessionToken == old) events[i].SessionToken = replacement;
        }
        private static void AttachPendingChange(IList<LogEventEntry> events, int index, string token, string name)
        {
            if (index < 0 || index >= events.Count || events[index] == null) return;
            events[index].SessionToken = token; events[index].SessionName = name; events[index].Phase = "start";
        }
        private static bool ReliableStartAhead(IList<LogEventEntry> events, int from)
        {
            for (int i = from; i < events.Count; i++)
            {
                if (events[i] == null) continue;
                string body = Body(events[i].Raw);
                Boundary boundary = SessionBoundary(body);
                if (boundary != null && boundary.State == "start") return true;
                string token, action, state;
                if (Auto(body, out token, out action, out state) && IsSessionAction(action) && IsStarted(state)) return true;
                if (body.StartsWith("游戏模式激活 检测到 ", StringComparison.Ordinal)
                    || body.StartsWith("Game mode activated, detected ", StringComparison.Ordinal)
                    || body.StartsWith("游戏模式 检测目标变更 ", StringComparison.Ordinal)
                    || body.StartsWith("Game mode detection target changed ", StringComparison.Ordinal)
                    || body.StartsWith("游戏模式解除 ", StringComparison.Ordinal)
                    || body.StartsWith("Game mode ended ", StringComparison.Ordinal)) return false;
            }
            return false;
        }
        private static string Name(Dictionary<string, string> names, string token)
        { string name; return names.TryGetValue(token, out name) ? name : ""; }
        private static string Phase(Dictionary<string, string> phases, string token)
        { string phase; return phases.TryGetValue(token, out phase) ? phase : ""; }
        private static bool RestorationText(string body)
        {
            return body.IndexOf("还原", StringComparison.Ordinal) >= 0 || body.IndexOf("恢复", StringComparison.Ordinal) >= 0
                || body.IndexOf("restore", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private static string Text(string chinese, string english, string japanese)
        { return Lang.Cur == 1 ? english : Lang.Cur == 2 ? japanese : chinese; }
    }
}
