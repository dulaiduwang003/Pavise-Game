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
            Dictionary<string, string> namesByMessage = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, string> phasesByToken = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            string message = "";
            string message2 = "";
            string message3 = "";
            int pendingChangeIndex = -1;
            bool endedWithoutStart = false;
            for (int i = 0; i < chronological.Count; i++)
            {
                LogEventEntry logEventEntry = chronological[i];
                if (logEventEntry == null)
                {
                    continue;
                }
                logEventEntry.SessionToken = (logEventEntry.SessionName = (logEventEntry.Phase = ""));
                string bodyResult = Body(logEventEntry.Raw);
                Boundary boundary = SessionBoundary(bodyResult);
                string token;
                string action;
                string state;
                bool autoSucceeded = Auto(bodyResult, out token, out action, out state);
                string remaining;
                bool stripPrefixSucceeded = StripPrefix(bodyResult, new string[2] { "游戏模式激活 检测到 ", "Game mode activated, detected " }, out remaining);
                string remaining2;
                bool stripPrefixSucceeded2 = StripPrefix(bodyResult, new string[2] { "游戏模式 检测目标变更 ", "Game mode detection target changed " }, out remaining2);
                if (boundary != null)
                {
                    if (boundary.State == "start")
                    {
                        string message4 = message;
                        if (message4.StartsWith("legacy:", StringComparison.Ordinal) && pendingChangeIndex < 0 && Phase(phasesByToken, message4) == "active" && (boundary.Name.Length == 0 || Name(namesByMessage, message4) == boundary.Name))
                        {
                            Remap(chronological, i, message4, boundary.Token);
                        }
                        message = boundary.Token;
                        message2 = "";
                        if (!string.IsNullOrEmpty(boundary.Name))
                        {
                            namesByMessage[message] = boundary.Name;
                        }
                        else if (!string.IsNullOrEmpty(message3))
                        {
                            namesByMessage[message] = message3;
                        }
                        else if (namesByMessage.ContainsKey(message4))
                        {
                            namesByMessage[message] = namesByMessage[message4];
                        }
                        phasesByToken[message] = "active";
                        AttachPendingChange(chronological, pendingChangeIndex, message, Name(namesByMessage, message));
                        message3 = "";
                        pendingChangeIndex = -1;
                        endedWithoutStart = false;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(boundary.Name))
                        {
                            namesByMessage[boundary.Token] = boundary.Name;
                        }
                        phasesByToken[boundary.Token] = ((boundary.State == "end") ? "restore" : boundary.State);
                    }
                    logEventEntry.SessionToken = boundary.Token;
                    logEventEntry.SessionName = Name(namesByMessage, boundary.Token);
                    logEventEntry.Phase = boundary.State;
                    if (boundary.State == "restored" || boundary.State == "restore-failed")
                    {
                        if (message == boundary.Token)
                        {
                            message = "";
                        }
                        message2 = ((boundary.State == "restore-failed") ? boundary.Token : "");
                    }
                    else if (boundary.State == "end" && message.Length == 0)
                    {
                        message = boundary.Token;
                    }
                    continue;
                }
                if (stripPrefixSucceeded2)
                {
                    message3 = remaining2;
                    pendingChangeIndex = i;
                    endedWithoutStart = false;
                }
                else if (stripPrefixSucceeded)
                {
                    message = SyntheticToken(logEventEntry, remaining, counts);
                    namesByMessage[message] = remaining;
                    phasesByToken[message] = "active";
                    message2 = "";
                }
                if (autoSucceeded)
                {
                    bool isSessionAction = IsSessionAction(action);
                    if (isSessionAction && IsStarted(state))
                    {
                        string message5 = message;
                        if (message5.StartsWith("legacy:", StringComparison.Ordinal) && pendingChangeIndex < 0 && Phase(phasesByToken, message5) == "active")
                        {
                            Remap(chronological, i, message5, token);
                        }
                        message = token;
                        if (!namesByMessage.ContainsKey(message))
                        {
                            namesByMessage[message] = ((message3.Length > 0) ? message3 : Name(namesByMessage, message5));
                        }
                        phasesByToken[message] = "active";
                        message2 = "";
                        AttachPendingChange(chronological, pendingChangeIndex, message, Name(namesByMessage, message));
                        message3 = "";
                        pendingChangeIndex = -1;
                        endedWithoutStart = false;
                    }
                    else if (token.Length > 0 && message.Length == 0 && !phasesByToken.ContainsKey(token))
                    {
                        message = token;
                        phasesByToken[message] = "fragment";
                    }
                    logEventEntry.SessionToken = token;
                    logEventEntry.SessionName = Name(namesByMessage, token);
                    logEventEntry.Phase = Phase(phasesByToken, token);
                    if (isSessionAction && (IsRestored(state) || IsRestoreFailed(state)))
                    {
                        logEventEntry.Phase = (IsRestored(state) ? "restored" : "restore-failed");
                        phasesByToken[token] = logEventEntry.Phase;
                        if (message == token)
                        {
                            message = "";
                        }
                        message2 = (IsRestoreFailed(state) ? token : "");
                    }
                    else if (isSessionAction && IsStarted(state))
                    {
                        logEventEntry.Phase = "start";
                    }
                    continue;
                }
                if (endedWithoutStart && !stripPrefixSucceeded2 && !ReliableStartAhead(chronological, i))
                {
                    message = SyntheticToken(chronological[pendingChangeIndex], message3, counts);
                    namesByMessage[message] = message3;
                    phasesByToken[message] = "active";
                    AttachPendingChange(chronological, pendingChangeIndex, message, message3);
                    message3 = "";
                    pendingChangeIndex = -1;
                    endedWithoutStart = false;
                }
                string sessionTokenValue = message;
                if (sessionTokenValue.Length == 0 && message2.Length > 0 && RestorationText(bodyResult))
                {
                    sessionTokenValue = message2;
                }
                logEventEntry.SessionToken = sessionTokenValue;
                logEventEntry.SessionName = Name(namesByMessage, sessionTokenValue);
                logEventEntry.Phase = Phase(phasesByToken, sessionTokenValue);
                if (stripPrefixSucceeded)
                {
                    logEventEntry.Phase = "start";
                }
                if (bodyResult.StartsWith("本局结束 ", StringComparison.Ordinal) || bodyResult.StartsWith("Match ended ", StringComparison.Ordinal))
                {
                    if (sessionTokenValue.Length > 0)
                    {
                        phasesByToken[sessionTokenValue] = "restore";
                    }
                    logEventEntry.Phase = "end";
                    if (pendingChangeIndex >= 0)
                    {
                        endedWithoutStart = true;
                    }
                }
                else if (bodyResult.StartsWith("游戏模式解除 ", StringComparison.Ordinal) || bodyResult.StartsWith("Game mode ended ", StringComparison.Ordinal))
                {
                    logEventEntry.Phase = "restore";
                    if (sessionTokenValue.StartsWith("legacy:", StringComparison.Ordinal))
                    {
                        message = "";
                        message2 = sessionTokenValue;
                    }
                }
            }
            for (int j = 0; j < chronological.Count; j++)
            {
                if (chronological[j] != null && chronological[j].SessionToken.Length > 0)
                {
                    chronological[j].SessionName = Name(namesByMessage, chronological[j].SessionToken);
                }
            }
        }

        internal static List<LogEventEntry> Project(IList<LogEventEntry> chronological, int filter, string activeSession)
        {
            List<LogEventEntry> rows = new List<LogEventEntry>();
            if (chronological == null || chronological.Count == 0)
            {
                return rows;
            }
            string selectSessionResult = SelectSession(chronological, activeSession);
            if (filter == 4 && selectSessionResult.Length == 0)
            {
                return rows;
            }
            int sessionStartIndex = -1;
            int chronologicalCount = chronological.Count;
            if (selectSessionResult.Length > 0)
            {
                for (int i = 0; i < chronological.Count; i++)
                {
                    LogEventEntry logEventEntry = chronological[i];
                    if (logEventEntry != null && logEventEntry.SessionToken == selectSessionResult)
                    {
                        sessionStartIndex = i;
                        break;
                    }
                }
                for (int j = sessionStartIndex + 1; j < chronological.Count; j++)
                {
                    LogEventEntry currentLogEventEntry = chronological[j];
                    if (currentLogEventEntry != null && currentLogEventEntry.SessionToken.Length > 0 && currentLogEventEntry.SessionToken != selectSessionResult && currentLogEventEntry.Phase == "start")
                    {
                        chronologicalCount = j;
                        break;
                    }
                }
            }
            if (filter == 4 && sessionStartIndex < 0)
            {
                return rows;
            }
            List<Bucket> buckets = new List<Bucket>();
            Dictionary<string, Bucket> bucketsByName = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            Dictionary<string, string> concatResultsByModuleOffset = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, Bucket> currentBucketsByName = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            Dictionary<string, Bucket> candidateBucketsByName = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            Dictionary<string, string> phasesBySessionToken = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, int> integerValuesBySessionToken = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, string> statesByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, int> currentIntegerValuesBySessionToken = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int k = 0; k < chronological.Count; k++)
            {
                LogEventEntry candidateLogEventEntry = chronological[k];
                if (candidateLogEventEntry == null)
                {
                    continue;
                }
                bool needsAttention = candidateLogEventEntry.Severity == LogEventSeverity.Warning || candidateLogEventEntry.Severity == LogEventSeverity.Error;
                string bodyResult = Body(candidateLogEventEntry.Raw);
                string token;
                string action;
                string state;
                bool autoSucceeded = Auto(bodyResult, out token, out action, out state);
                string moduleOffset = candidateLogEventEntry.SessionToken + "\u001f" + candidateLogEventEntry.Module;
                string[] eventFields = new string[5];
                int severity = (int)candidateLogEventEntry.Severity;
                eventFields[0] = severity.ToString(CultureInfo.InvariantCulture);
                eventFields[1] = "\u001f";
                eventFields[2] = candidateLogEventEntry.Phase;
                eventFields[3] = "\u001f";
                eventFields[4] = CaptureTelemetry.Replace(bodyResult, "$1=*");
                string concatResult = string.Concat(eventFields);
                string currentConcatResult;
                bool moduleChanged = !concatResultsByModuleOffset.TryGetValue(moduleOffset, out currentConcatResult) || currentConcatResult != concatResult;
                concatResultsByModuleOffset[moduleOffset] = concatResult;
                int sessionDurationMinutes;
                currentIntegerValuesBySessionToken.TryGetValue(candidateLogEventEntry.SessionToken, out sessionDurationMinutes);
                bool stateChanged = false;
                if (autoSucceeded)
                {
                    string key = candidateLogEventEntry.SessionToken + "\u001f" + action;
                    stateChanged = statesByKey.TryGetValue(key, out currentConcatResult) && currentConcatResult != state;
                    statesByKey[key] = state;
                }
                bool sessionBoundary = candidateLogEventEntry.SessionToken.Length > 0 && (SessionBoundary(bodyResult) != null || (autoSucceeded && IsSessionAction(action)) || (candidateLogEventEntry.Phase == "start" && (bodyResult.StartsWith("游戏模式", StringComparison.Ordinal) || bodyResult.StartsWith("Game mode ", StringComparison.Ordinal))) || (candidateLogEventEntry.Phase == "end" && (bodyResult.StartsWith("本局结束 ", StringComparison.Ordinal) || bodyResult.StartsWith("Match ended ", StringComparison.Ordinal)))) && (candidateLogEventEntry.Phase == "start" || candidateLogEventEntry.Phase == "end" || candidateLogEventEntry.Phase == "restored" || candidateLogEventEntry.Phase == "restore-failed");
                int sessionEventCount;
                integerValuesBySessionToken.TryGetValue(candidateLogEventEntry.SessionToken, out sessionEventCount);
                if (sessionBoundary)
                {
                    if (phasesBySessionToken.TryGetValue(candidateLogEventEntry.SessionToken, out currentConcatResult) && currentConcatResult != candidateLogEventEntry.Phase)
                    {
                        sessionEventCount++;
                        integerValuesBySessionToken[candidateLogEventEntry.SessionToken] = sessionEventCount;
                    }
                    phasesBySessionToken[candidateLogEventEntry.SessionToken] = candidateLogEventEntry.Phase;
                }
                if ((filter == 1 && !needsAttention) || (filter == 2 && candidateLogEventEntry.Severity != LogEventSeverity.Error) || (filter == 3 && !autoSucceeded) || (filter == 4 && selectSessionResult.Length > 0 && candidateLogEventEntry.SessionToken != selectSessionResult && (!needsAttention || candidateLogEventEntry.SessionToken.Length != 0 || k < sessionStartIndex || k >= chronologicalCount)))
                {
                    continue;
                }
                if (sessionBoundary)
                {
                    string key2 = candidateLogEventEntry.SessionToken + "\u001f" + candidateLogEventEntry.Phase + "\u001f" + sessionEventCount;
                    Bucket candidateBucket;
                    if (!candidateBucketsByName.TryGetValue(key2, out candidateBucket))
                    {
                        candidateBucket = Create(candidateLogEventEntry, k, "boundary:", occurrences);
                        candidateBucket.Row.Details.Add(Copy(candidateLogEventEntry));
                        candidateBucketsByName[key2] = candidateBucket;
                        buckets.Add(candidateBucket);
                    }
                    else
                    {
                        Append(candidateBucket, candidateLogEventEntry, k);
                    }
                    candidateBucket.Row.Module = Text("对局", "SESSION");
                    candidateBucket.Row.Message = BoundaryMessage(candidateLogEventEntry.SessionName, candidateLogEventEntry.Phase);
                    candidateBucket.Row.Severity = BoundarySeverity(candidateBucket.Row.Severity, candidateLogEventEntry.Severity, candidateLogEventEntry.Phase);
                }
                else if ((!autoSucceeded && candidateLogEventEntry.Severity == LogEventSeverity.Success && candidateLogEventEntry.SessionToken.Length > 0 && (candidateLogEventEntry.Phase == "active" || candidateLogEventEntry.Phase == "restore") && !string.Equals(candidateLogEventEntry.Module, "FRAME", StringComparison.OrdinalIgnoreCase) && !string.Equals(candidateLogEventEntry.Module, "SESSION", StringComparison.OrdinalIgnoreCase)) || (candidateLogEventEntry.Severity == LogEventSeverity.Success && autoSucceeded && candidateLogEventEntry.SessionToken.Length > 0 && !IsSessionAction(action) && (IsApplied(state) || IsRestored(state))))
                {
                    string message = ((!autoSucceeded) ? ((candidateLogEventEntry.Phase == "restore" || RestorationText(bodyResult)) ? "restored" : "applied") : (IsApplied(state) ? "applied" : "restored"));
                    string key3 = candidateLogEventEntry.SessionToken + "\u001f" + candidateLogEventEntry.Phase + "\u001f" + message + "\u001f" + sessionDurationMinutes;
                    Bucket currentBucket;
                    if (stateChanged && currentBucketsByName.TryGetValue(key3, out currentBucket) && currentBucket.Actions.Contains(action))
                    {
                        sessionDurationMinutes++;
                        currentIntegerValuesBySessionToken[candidateLogEventEntry.SessionToken] = sessionDurationMinutes;
                        key3 = candidateLogEventEntry.SessionToken + "\u001f" + candidateLogEventEntry.Phase + "\u001f" + message + "\u001f" + sessionDurationMinutes;
                    }
                    if (!currentBucketsByName.TryGetValue(key3, out currentBucket))
                    {
                        currentBucket = Create(candidateLogEventEntry, k, "summary:", occurrences);
                        currentBucket.Row.IsSummary = true;
                        currentBucket.Row.Module = Text("对局摘要", "SESSION");
                        currentBucket.Row.Details.Add(Copy(candidateLogEventEntry));
                        currentBucketsByName[key3] = currentBucket;
                        buckets.Add(currentBucket);
                    }
                    else
                    {
                        Append(currentBucket, candidateLogEventEntry, k);
                    }
                    if (autoSucceeded)
                    {
                        currentBucket.Actions.Add(action);
                    }
                    currentBucket.Row.Message = SummaryMessage(currentBucket.Row.SessionName, currentBucket.Row.Phase, message, currentBucket.Actions.Count);
                }
                else
                {
                    string key4 = moduleOffset + "\u001f" + concatResult;
                    Bucket bucket;
                    if (!moduleChanged && bucketsByName.TryGetValue(key4, out bucket))
                    {
                        Append(bucket, candidateLogEventEntry, k);
                        bucket.Row.Message = candidateLogEventEntry.Message;
                    }
                    else
                    {
                        bucket = (bucketsByName[key4] = Create(candidateLogEventEntry, k, "event:", occurrences));
                        buckets.Add(bucket);
                    }
                }
            }
            buckets.Sort((Bucket a, Bucket b) => b.LastIndex.CompareTo(a.LastIndex));
            for (int l = 0; l < buckets.Count; l++)
            {
                rows.Add(buckets[l].Row);
            }
            return rows;
        }

        private static Bucket Create(LogEventEntry source, int index, string prefix, Dictionary<string, int> occurrences)
        {
            LogEventEntry logEventEntry = Copy(source);
            logEventEntry.FirstTime = (logEventEntry.LastTime = Stamp(source));
            logEventEntry.RepeatCount = 1;
            string rawOffset = prefix + source.SessionToken + "\u001f" + source.Raw;
            int value;
            occurrences.TryGetValue(rawOffset, out value);
            occurrences[rawOffset] = value + 1;
            logEventEntry.StableKey = rawOffset + "\u001f" + value.ToString(CultureInfo.InvariantCulture);
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

        internal static string SelectSession(IList<LogEventEntry> events, string active)
        {
            if (!string.IsNullOrEmpty(active))
            {
                return active;
            }
            string selectSessionResult = "";
            string result = "";
            HashSet<string> sessionTokens = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < events.Count; i++)
            {
                LogEventEntry logEventEntry = events[i];
                if (logEventEntry != null && logEventEntry.SessionToken.Length != 0)
                {
                    result = logEventEntry.SessionToken;
                    if (logEventEntry.Phase == "start" || (logEventEntry.Phase == "fragment" && sessionTokens.Add(logEventEntry.SessionToken)))
                    {
                        selectSessionResult = logEventEntry.SessionToken;
                    }
                }
            }
            if (selectSessionResult.Length <= 0)
            {
                return result;
            }
            return selectSessionResult;
        }

        private static string SummaryMessage(string name, string phase, string state, int actions)
        {
            string textResult = ((state == "applied") ? Text("已应用", "Applied") : ((phase == "restore") ? Text("退局还原记录", "Exit restoration records") : Text("局部还原记录", "Individual restoration records")));
            if (actions == 0)
            {
                return (string.IsNullOrEmpty(name) ? "" : (name + " · ")) + ((state == "restored") ? Text("还原记录", "Restoration records") : Text("执行记录", "Execution records"));
            }
            return (string.IsNullOrEmpty(name) ? "" : (name + " · ")) + textResult + " " + actions.ToString(CultureInfo.InvariantCulture) + Text(" 项", " actions");
        }

        private static string BoundaryMessage(string name, string phase)
        {
            string textResult;
            switch (phase)
            {
            default:
                textResult = Text("还原未完成", "Restoration incomplete");
                break;
            case "restored":
                textResult = Text("恢复检查通过", "Restoration checks passed");
                break;
            case "end":
                textResult = Text("退局，开始还原", "Game ended; restoring");
                break;
            case "start":
                textResult = Text("开始接管", "Management started");
                break;
            }
            string message = textResult;
            return (string.IsNullOrEmpty(name) ? "" : (name + " · ")) + message;
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
            if (e.Timestamp == DateTime.MinValue)
            {
                return e.Date + " " + e.Time;
            }
            return e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string Body(string raw)
        {
            string trimmed = (raw ?? "").Trim();
            DateTime result;
            if (trimmed.Length >= 19 && DateTime.TryParseExact(trimmed.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            {
                trimmed = trimmed.Substring(19).Trim();
            }
            trimmed = LogText.Canonical(trimmed);
            string[] levelPrefixes = new string[4] { "WARN ", "FAIL ", "INFO ", "PASS " };
            for (int i = 0; i < levelPrefixes.Length; i++)
            {
                if (trimmed.StartsWith(levelPrefixes[i], StringComparison.Ordinal))
                {
                    return trimmed.Substring(levelPrefixes[i].Length).Trim();
                }
            }
            return trimmed;
        }

        private static Boundary SessionBoundary(string body)
        {
            if (!body.StartsWith("SESSION [", StringComparison.Ordinal))
            {
                return null;
            }
            int matchIndex = body.IndexOf(']', 9);
            if (matchIndex <= 9)
            {
                return null;
            }
            int matchIndex2 = body.IndexOf('|', matchIndex + 1);
            string stateValue = ((matchIndex2 < 0) ? body.Substring(matchIndex + 1) : body.Substring(matchIndex + 1, matchIndex2 - matchIndex - 1)).Trim();
            if (stateValue != "start" && stateValue != "end" && stateValue != "restored" && stateValue != "restore-failed")
            {
                return null;
            }
            Boundary boundary = new Boundary();
            boundary.Token = body.Substring(9, matchIndex - 9);
            boundary.State = stateValue;
            boundary.Name = ((matchIndex2 < 0) ? "" : body.Substring(matchIndex2 + 1).Trim());
            return boundary;
        }

        private static bool Auto(string body, out string token, out string action, out string state)
        {
            token = (action = (state = ""));
            if (!body.StartsWith("AUTO [", StringComparison.Ordinal))
            {
                return false;
            }
            int matchIndex = body.IndexOf(']', 6);
            int matchIndex2 = ((matchIndex >= 0) ? body.IndexOf('|', matchIndex + 1) : (-1));
            if (matchIndex < 6)
            {
                return false;
            }
            token = body.Substring(6, matchIndex - 6);
            if (matchIndex2 <= matchIndex + 1)
            {
                return true;
            }
            action = body.Substring(matchIndex + 1, matchIndex2 - matchIndex - 1).Trim();
            state = body.Substring(matchIndex2 + 1).Trim();
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
            string nameOffset = Stamp(e) + "\u001f" + name;
            int value;
            counts.TryGetValue(nameOffset, out value);
            counts[nameOffset] = value + 1;
            return "legacy:" + nameOffset + "\u001f" + value.ToString(CultureInfo.InvariantCulture);
        }

        private static void Remap(IList<LogEventEntry> events, int end, string old, string replacement)
        {
            for (int eventsIndex = end - 1; eventsIndex >= 0; eventsIndex--)
            {
                if (events[eventsIndex] != null && events[eventsIndex].SessionToken == old)
                {
                    events[eventsIndex].SessionToken = replacement;
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
                    string bodyResult = Body(events[i].Raw);
                    Boundary boundary = SessionBoundary(bodyResult);
                    if (boundary != null && boundary.State == "start")
                    {
                        return true;
                    }
                    string token;
                    string action;
                    string state;
                    if (Auto(bodyResult, out token, out action, out state) && IsSessionAction(action) && IsStarted(state))
                    {
                        return true;
                    }
                    if (bodyResult.StartsWith("游戏模式激活 检测到 ", StringComparison.Ordinal) || bodyResult.StartsWith("Game mode activated, detected ", StringComparison.Ordinal) || bodyResult.StartsWith("游戏模式 检测目标变更 ", StringComparison.Ordinal) || bodyResult.StartsWith("Game mode detection target changed ", StringComparison.Ordinal) || bodyResult.StartsWith("游戏模式解除 ", StringComparison.Ordinal) || bodyResult.StartsWith("Game mode ended ", StringComparison.Ordinal))
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

        private static string Text(string chinese, string english)
        {
            return chinese;
        }
    }
}
