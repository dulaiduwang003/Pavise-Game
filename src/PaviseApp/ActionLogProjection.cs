using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PaviseApp
{
    internal static class ActionLogProjection
    {
        private static readonly Regex Boundary = new Regex(@"^SESSION \[(?<token>[^\]]+)\]\s*(?<state>start|end|restoring|restored|restore-failed)(?:\s*\|\s*(?<name>.*))?$", RegexOptions.CultureInvariant);
        private static readonly Regex Auto = new Regex(@"^AUTO \[(?<token>[^\]]*)\]\s*(?<action>[^|]+)\|\s*(?<state>[^|]+)", RegexOptions.CultureInvariant);
        private static readonly Regex ExplicitToken = new Regex(@"^(?:THREAD|FOCUS) \[(?<token>[^\]]+)\]", RegexOptions.CultureInvariant);
        private static readonly Regex Target = new Regex(@"(?:^|;)(?<name>.*?)\(pid=(?<pid>\d+),birth=\d+\)(?=;|$)", RegexOptions.CultureInvariant);
        private static readonly Regex Technical = new Regex(@"(?<!\S)(?:birth|creation|epoch|generation|win32|code|traceCode|lostEvents|sampleCpuMs|eventCount|systemEvents|activationMs|workerMask|granted|countScope|includes)=[^\s;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static string Text(string zh, string en) { return zh; }

        internal static List<LogEventEntry> Project(IList<LogEventEntry> chronological, int filter, string activeSession)
        {
            var result = new List<LogEventEntry>();
            if (chronological == null || chronological.Count == 0) return result;
            List<LogEventEntry> entries = Normalize(chronological);
            string selected = LogEventProjection.SelectSession(entries, activeSession);
            int start = -1, end = entries.Count;
            for (int i = 0; i < entries.Count; i++)
            {
                LogEventEntry entry = entries[i];
                if (entry.SessionToken == selected && selected.Length > 0 && start < 0) start = i;
                else if (start >= 0 && entry.SessionToken.Length > 0 && entry.SessionToken != selected && entry.Phase == "start") { end = i; break; }
            }
            if (filter == 4 && (selected.Length == 0 || start < 0)) return result;
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < entries.Count; index++)
            {
                LogEventEntry source = entries[index];
                bool issue = source.Severity == LogEventSeverity.Warning || source.Severity == LogEventSeverity.Error;
                string body = Body(source.Raw);
                if ((filter == 1 && !issue) || (filter == 2 && source.Severity != LogEventSeverity.Error)
                    || (filter == 3 && !body.StartsWith("AUTO [", StringComparison.Ordinal))
                    || (filter == 4 && source.SessionToken != selected && (!issue || source.SessionToken.Length != 0 || index < start || index >= end))) continue;
                int before = result.Count;
                Translate(source, body, result);
                for (int row = before; row < result.Count; row++)
                {
                    string key = "action:" + source.SessionToken + "\u001f" + source.Raw + "\u001f" + (row - before).ToString(CultureInfo.InvariantCulture);
                    int occurrence; occurrences.TryGetValue(key, out occurrence); occurrences[key] = occurrence + 1;
                    result[row].StableKey = key + "\u001f" + occurrence.ToString(CultureInfo.InvariantCulture);
                }
            }
            result.Reverse();
            return result;
        }

        internal static string Export(string raw, string activeSession)
        {
            List<LogEventEntry> events = LogStreamView.ParseEvents(raw ?? "");
            List<LogEventEntry> rows = Project(events, 4, activeSession);
            if (rows.Count == 0) return Text("当前日志没有该会话的动作记录。", "The current log has no action records for this session.") + Environment.NewLine;
            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine(Text("Pavise 会话动作记录", "Pavise session action log"));
            stringBuilder.AppendLine(Text("仅列出现有日志确认的动作；恢复状态以各项记录为准。", "Actions reflect recorded evidence; restoration status is reported per action."));
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                LogEventEntry row = rows[i];
                string stamp = row.Timestamp == DateTime.MinValue ? row.Date + " " + row.Time : row.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                stringBuilder.Append(stamp).Append("  [").Append(row.Module).Append("] ").AppendLine(row.Message);
            }
            return stringBuilder.ToString();
        }

        private static List<LogEventEntry> Normalize(IList<LogEventEntry> input)
        {
            var output = new List<LogEventEntry>(); var names = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < input.Count; i++)
            {
                LogEventEntry original = input[i]; if (original == null) continue;
                LogEventEntry entry = Copy(original); string body = Body(entry.Raw);
                Match boundary = Boundary.Match(body), automatic = Auto.Match(body), explicitToken = ExplicitToken.Match(body);
                string token = boundary.Success ? boundary.Groups["token"].Value : automatic.Success ? automatic.Groups["token"].Value : explicitToken.Success ? explicitToken.Groups["token"].Value : null;
                if (token != null && token != entry.SessionToken) { entry.SessionToken = token; entry.SessionName = ""; }
                if (boundary.Success)
                {
                    entry.Phase = boundary.Groups["state"].Value;
                    if (boundary.Groups["name"].Success) entry.SessionName = boundary.Groups["name"].Value;
                }
                if (entry.SessionToken.Length > 0 && entry.SessionName.Length > 0) names[entry.SessionToken] = entry.SessionName;
                output.Add(entry);
            }
            foreach (LogEventEntry entry in output)
            { string name; if (entry.SessionToken.Length > 0 && names.TryGetValue(entry.SessionToken, out name)) entry.SessionName = name; }
            return output;
        }

        private static void Translate(LogEventEntry source, string body, List<LogEventEntry> output)
        {
            if (source.Severity != LogEventSeverity.Error && body.StartsWith("Process path read failed pid=", StringComparison.Ordinal)) return;
            Match boundary = Boundary.Match(body);
            if (boundary.Success)
            { Add(output, source, "session", SessionMessage(source, boundary.Groups["state"].Value), StateSeverity(source, boundary.Groups["state"].Value)); return; }
            Match automatic = Auto.Match(body);
            if (automatic.Success) { Automatic(output, source, body, automatic); return; }
            if (body.StartsWith("SUPPRESSION success", StringComparison.Ordinal)) { Suppression(output, source, body); return; }
            if (body.StartsWith("THREAD ", StringComparison.Ordinal)) { ThreadAction(output, source, body); return; }
            if (body.StartsWith("GAME placement verified ", StringComparison.Ordinal))
            {
                string range = Mask(Value(body, "mask")), pid = Value(body, "pid"), name = Tail(body, " name=");
                if (range.Length > 0 && pid.Length > 0)
                    Add(output, source, "affinity", Text("游戏进程落核已核验：", "Game process CPU placement verified: ") + Process(name, pid) + " · CPU " + range, LogEventSeverity.Success);
                else Add(output, source, "affinity", Text("游戏落核记录缺少可读范围，请查看诊断日志。", "The placement record lacks a readable CPU range; see diagnostics."), LogEventSeverity.Info);
                return;
            }
            if (body.StartsWith("SESSION event=end", StringComparison.Ordinal))
            {
                string count = Value(body, "restoredProcessCount");
                Add(output, source, "recovery", count.Length == 0 ? Text("会话结束记录；恢复状态见后续确认。", "Session end recorded; restoration requires subsequent confirmation.")
                    : Text("已归还后台设置的进程：", "Processes with background settings returned: ") + count + Text(" 个；不代表所有功能已完成恢复。", "; this does not confirm restoration of every feature."), LogEventSeverity.Info); return;
            }
            if (Starts(body, "手动游戏核心：", "Manual game cores:"))
            { Add(output, source, "affinity", Text("游戏核心配置：", "Configured game CPUs: ") + Clean(body) + Text("（配置记录，尚非落核确认）", " (configuration only; placement is not confirmed)"), LogEventSeverity.Info); return; }
            if (Starts(body, "核心独占已生效 ", "Exclusive cores in effect:"))
            { Add(output, source, "affinity", Text("游戏进程核心范围与系统隔离已核验：", "Game process CPU range and system isolation verified: ") + Clean(body), LogEventSeverity.Success); return; }
            if (Starts(body, "核心独占已还原", "Exclusive cores released;"))
            { Add(output, source, "recovery", Text("游戏进程亲和性与核心隔离已恢复。", "Game process affinity and core isolation restored."), LogEventSeverity.Success); return; }
            if (Starts(body, "核心独占还原未完全确认", "Exclusive core release not fully confirmed"))
            { Add(output, source, "recovery", Text("核心隔离恢复尚未确认，仍需继续恢复。", "Core isolation restoration is not confirmed; recovery is still required."), LogEventSeverity.Warning); return; }
            if (Starts(body, "后台压制 ", "Background suppression "))
            {
                Match process = Regex.Match(body, @"(?<name>[^\r\n]+?)\(pid[= ](?<pid>\d+)\)\s*(?<level>.*)$");
                if (process.Success) Add(output, source, "background", Text("后台治理已生效：", "Background control applied: ") + Process(AfterFirstSpace(process.Groups["name"].Value), process.Groups["pid"].Value) + " · " + Clean(process.Groups["level"].Value), source.Severity);
                else
                {
                    Match count = Regex.Match(body, @"(?:首轮|first(?: pass)?)\s*(?<count>\d+)", RegexOptions.IgnoreCase);
                    Add(output, source, "background", Text("后台治理首轮记录", "Initial background-control record") + (count.Success ? " · " + count.Groups["count"].Value + Text(" 个进程", " processes") : "") + Text("；名单见逐项记录，旧日志未记录的名单无法补全。", "; see process records; lists absent from older logs cannot be recovered."), LogEventSeverity.Info);
                }
                return;
            }
            if (Starts(body, "游戏优先级已生效 ", "Game priorities in effect "))
            { Add(output, source, "boost", Text("游戏提优已生效：", "Game priorities applied: ") + Clean(body), LogEventSeverity.Success); return; }
            if (body.StartsWith("FRAME ", StringComparison.Ordinal))
            {
                if (body.IndexOf("no change needed", StringComparison.OrdinalIgnoreCase) >= 0 || body.Contains("无需重复调整"))
                    Add(output, source, "feature", Text("线程协同：游戏已自行管理优先级，本阶段不再重复调整。", "Thread coordination: the game manages its priorities; no repeated adjustment this phase."), LogEventSeverity.Info);
                else if (body.Contains("仅观测") || body.Contains("observation only"))
                    Add(output, source, "feature", Text("已识别画面提交线程；此记录仅为观测，尚非绑核确认。", "Presentation thread identified; this is observation, not CPU-binding confirmation."), LogEventSeverity.Info);
                else if (source.Severity == LogEventSeverity.Warning || source.Severity == LogEventSeverity.Error) Issue(output, source, body);
                return;
            }
            if (body.StartsWith("POWER event=activation-verified", StringComparison.Ordinal))
            { Add(output, source, "feature", Text("对局电源计划已切换并核验。", "The game power plan was activated and verified."), LogEventSeverity.Success); return; }
            if (source.Severity == LogEventSeverity.Warning || source.Severity == LogEventSeverity.Error) Issue(output, source, body);
        }

        private static void Automatic(List<LogEventEntry> output, LogEventEntry source, string body, Match parsed)
        {
            string key = parsed.Groups["action"].Value.Trim(), state = parsed.Groups["state"].Value.Trim();
            string normalized = NormalizeState(state); string name = FeatureName(key);
            if (key == "session" || key == "本次对局" || key == "Current session")
            { Add(output, source, "session", SessionMessage(source, normalized), StateSeverity(source, normalized)); return; }
            if (normalized == "timer-locked")
            {
                ulong actual; bool verified = string.Equals(Value(body, "verified"), "True", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Value(body, "active"), "True", StringComparison.OrdinalIgnoreCase)
                    && ulong.TryParse(Value(body, "actual100ns"), out actual) && actual > 0;
                if (verified)
                {
                    ulong.TryParse(Value(body, "actual100ns"), out actual);
                    Add(output, source, "feature", Text("计时器已读回确认：", "Timer resolution verified: ") + (actual / 10000.0).ToString("0.####", CultureInfo.InvariantCulture) + " ms", LogEventSeverity.Success);
                }
                else Add(output, source, "feature", Text("计时器目标尚未读回确认。", "The requested timer resolution is not verified."), LogEventSeverity.Warning);
                return;
            }
            string message;
            switch (normalized)
            {
                case "applied": message = Text("已生效", "Applied"); break;
                case "restored": message = Text("已恢复", "Restored"); break;
                case "ready": message = key == "AutoTimerTick"
                    ? Text("启动配置已核验", "Boot configuration verified")
                    : Text("已就绪；未确认生效", "Ready; application not confirmed"); break;
                case "restoring": message = Text("恢复中；尚未完成确认", "Restoring; completion is not confirmed"); break;
                case "restore-failed": message = Text("恢复未完成，请查看诊断日志", "Restoration incomplete; see diagnostics"); break;
                case "restore-pending-reboot": message = Text("恢复设置已写回，重启后生效；尚未完成最终确认", "Restoration settings written back; a reboot is required before final confirmation"); break;
                case "failed": message = Text("执行未成功，请查看诊断日志", "Application failed; see diagnostics"); break;
                case "stopped": message = Text("已停止尝试；未新增生效确认", "Attempts stopped; no new application is confirmed"); break;
                case "pending": case "unverified": case "timer-unverified": message = Text("尚未确认生效", "Application is not confirmed"); break;
                default: message = Text("状态记录：", "Recorded state: ") + Clean(state); break;
            }
            Add(output, source, key.StartsWith("GmBoost", StringComparison.Ordinal) ? "boost" : normalized == "restored" || normalized == "restoring" || normalized == "restore-failed" ? "recovery" : "feature", name + " · " + message, StateSeverity(source, normalized));
        }

        private static void Suppression(List<LogEventEntry> output, LogEventEntry source, string body)
        {
            string action = Value(body, "action"), targets = Tail(body, " targets="), label;
            switch (action)
            {
                case "background-applied": label = Text("后台治理已生效", "Background control applied"); break;
                case "background-level-changed": label = Text("后台治理档位已调整", "Background control level changed"); break;
                case "background-affinity-verified": label = Text("后台核心范围已核验", "Background CPU range verified"); break;
                case "reconcile-verified": label = Text("后台治理状态已核验", "Background control verified"); break;
                case "restored": case "reconcile-recovered": label = Text("后台设置已恢复", "Background settings restored"); break;
                default: label = Text("后台操作记录", "Background action recorded"); break;
            }
            MatchCollection parsedTargets = Target.Matches(targets); int expected;
            if (!int.TryParse(Value(body, "count"), out expected) || expected <= 0 || parsedTargets.Count != expected)
            { Add(output, source, "background", Text("后台批次名单无法完整读取，请查看诊断日志。", "The background batch process list could not be read completely; see diagnostics."), LogEventSeverity.Info); return; }
            int found = 0;
            foreach (Match target in parsedTargets)
            {
                Add(output, source, action == "restored" || action == "reconcile-recovered" ? "recovery" : "background",
                    Process(target.Groups["name"].Value, target.Groups["pid"].Value) + " · " + label,
                    action == "background-applied" || action == "background-level-changed" || action == "background-affinity-verified" || action == "reconcile-verified" || action == "restored" || action == "reconcile-recovered" ? LogEventSeverity.Success : LogEventSeverity.Info); found++;
            }
            if (found == 0) Add(output, source, "background", label + Text("；记录未包含完整进程名单，请查看诊断日志。", "; the complete process list is not in this record; see diagnostics."), LogEventSeverity.Info);
        }

        private static void ThreadAction(List<LogEventEntry> output, LogEventEntry source, string body)
        {
            if (body.StartsWith("THREAD exclusive verified:", StringComparison.Ordinal))
            {
                string range = Mask(Value(body, "physical")), workers = Value(body, "workers"), workerRange = Mask(Value(body, "workerMask"));
                ulong isolatedMask, workerMask; int workerCount;
                if (!TryMask(Value(body, "physical"), out isolatedMask) || !TryMask(Value(body, "workerMask"), out workerMask)
                    || (isolatedMask & workerMask) != 0 || !int.TryParse(workers, out workerCount) || workerCount < 0)
                { Add(output, source, "affinity", Text("清核尚未确认：核验记录不完整，请查看诊断日志。", "Core clearing is unconfirmed: incomplete verification record; see diagnostics."), LogEventSeverity.Info); return; }
                Add(output, source, "affinity", Text("清核完成（本轮）：CPU ", "Core clearing verified (this pass): CPU ") + range
                    + Text("；", "; ") + workers + Text(" 个游戏工作线程避让、普通外部进程隔离已核验", " game workers and eligible external processes excluded"), LogEventSeverity.Success);
                Add(output, source, "affinity", Text("游戏工作线程范围：CPU ", "Game worker CPU range: ") + workerRange, LogEventSeverity.Success);
                return;
            }
            Match chosen = Regex.Match(body, @"\bhard TID (?<tid>\d+) -> (?<cpu>.*?)(?:\s+workers-avoid|\s+candidates-only|\s+hard=)");
            if (chosen.Success)
            {
                string cpu = chosen.Groups["cpu"].Value.Trim();
                Match range = Regex.Match(cpu, @"(?:核|cores?|CPUs?|processors?)\s*(?<range>\d[\d,\- ]*)", RegexOptions.IgnoreCase);
                Add(output, source, "affinity", Text("关键线程已绑定：TID ", "Critical thread bound: TID ") + chosen.Groups["tid"].Value + " → " + (range.Success ? "CPU " + range.Groups["range"].Value.Trim() : cpu)
                    + (body.Contains("candidates-only") ? Text("；未执行清核，其他游戏线程不避让", "; no core clearing; other game threads are not excluded") : ""), LogEventSeverity.Success); return;
            }
            string state = Value(body, "result"), message = "";
            switch (state)
            {
                case "active": message = Text("线程亲和性已生效；核心明细见对应核验记录。", "Thread affinity is active; see its CPU verification record."); break;
                case "recovery-pending": message = Text("线程恢复尚在收尾，未确认全部资源已关闭。", "Thread recovery is finishing; resource closure is not fully confirmed."); break;
                case "withdrawn": message = Text("线程绑定/清核已撤回，恢复状态等待后续确认。", "Thread binding/core clearing withdrawn; restoration awaits confirmation."); break;
                case "revoked": message = Text("已停止本次线程绑定授权，进入撤回处理。", "Thread-binding authorization ended; withdrawal is required."); break;
                case "conflict": case "unsupported": case "failed": message = Text("清核/线程绑定未确认或已停止，请查看诊断日志。", "Core clearing/thread binding is unconfirmed or stopped; see diagnostics."); break;
                case "coverage-pending": message = Text("清核尚未确认，正在核验游戏线程覆盖。", "Core clearing is unconfirmed; checking game thread coverage."); break;
                case "observing": case "waiting-game": message = Text("正在等待或识别关键线程，尚未确认绑定。", "Waiting for or identifying critical threads; binding is not confirmed."); break;
            }
            if (message.Length > 0) Add(output, source, state == "recovery-pending" || state == "withdrawn" || state == "revoked" ? "recovery" : "affinity", message, StateSeverity(source, state));
            else if (source.Severity == LogEventSeverity.Warning || source.Severity == LogEventSeverity.Error) Issue(output, source, body);
        }

        private static string SessionMessage(LogEventEntry source, string state)
        {
            string name = source.SessionName.Length > 0 ? source.SessionName + " · " : "";
            switch (state)
            {
                case "start": case "started": return name + Text("对局开始，开始按配置接管。", "Game session started; configured management begins.");
                case "end": return name + Text("对局结束，正在恢复设置。", "Game ended; restoring settings.");
                case "restoring": return name + Text("本局设置恢复中。", "Restoring this session's settings.");
                case "restored": return name + (source.SessionToken.Length == 0 ? Text("恢复检查通过（该记录未标识具体对局）。", "Restoration checks passed (this record has no game-session identifier).") : Text("本次对局的恢复检查通过。", "Restoration checks passed for this game session."));
                case "restore-failed": return name + Text("本次对局的恢复未完成，请查看诊断日志。", "Restoration is incomplete for this session; see diagnostics.");
                default: return name + Text("会话状态：", "Session state: ") + Clean(state);
            }
        }
        private static LogEventSeverity StateSeverity(LogEventEntry source, string state)
        {
            if (source.Severity == LogEventSeverity.Error) return LogEventSeverity.Error;
            if (source.Severity == LogEventSeverity.Warning || state == "failed" || state == "restore-failed" || state == "unsupported" || state == "conflict") return LogEventSeverity.Warning;
            return state == "applied" || state == "restored" || state == "active" ? LogEventSeverity.Success : LogEventSeverity.Info;
        }
        private static string NormalizeState(string state)
        {
            switch (state)
            {
                case "已应用": case "已生效": return "applied";
                case "已还原": case "已恢复": return "restored";
                case "恢复中": return "restoring";
                case "还原失败": case "恢复失败": return "restore-failed";
                default: return state.ToLowerInvariant();
            }
        }
        private static string FeatureName(string key)
        {
            switch (key)
            {
                case "PowerPlanOn": return Text("电源计划", "Power plan");
                case "AutoGlobalTimer": return Text("全局计时器", "Global timer");
                case "AutoTimerTick": return Text("计时器节拍策略", "Timer tick policy");
                case "GmBoostCpuV1": return Text("游戏 CPU 优先级", "Game CPU priority");
                case "GmBoostGpuV1": return Text("游戏 GPU 优先级", "Game GPU priority");
                case "GmBoostIoV1": return Text("游戏磁盘 I/O 优先级", "Game disk I/O priority");
                case "GmBoostMemoryV1": return Text("游戏内存优先级", "Game memory priority");
                case "GameEcoQoS": return Text("游戏高性能 QoS", "Game high-performance QoS");
                case "FrameCoordination": case "GmRenderLane": return Text("线程协同", "Thread coordination");
                case "mmcss": case "GmMmcss": return Lang.T("auto.mmcss");
                case "pqos": return Lang.T("t.gamemodeenv.4");
                case "awake": return Lang.T("t.gamemodeenv.5");
                case "wu": return Lang.T("t.gamemodeenv.3");
                case "gamedvr": case "GameDvrOff": return Lang.T("set.dvr");
            }
            PolicyItem policy = PolicyCatalog.ItemOf(key);
            if (policy != null) return Lang.T(policy.LangKey);
            PerformanceAction action = PerformanceActionCatalog.Find(key);
            return action != null ? action.Title : key;
        }
        private static void Issue(List<LogEventEntry> output, LogEventEntry source, string body)
        {
            if (body.StartsWith("Retired environment recovery", StringComparison.Ordinal) && body.Contains("Unreadable performance receipt names"))
                Add(output, source, "recovery", Text("旧配置恢复记录读取失败，旧设置恢复尚未确认，详见诊断日志。", "Older configuration recovery records could not be read; restoration remains unconfirmed. See diagnostics."), source.Severity);
            else Add(output, source, "issue", Text("需要留意：", "Needs attention: ") + Clean(body), source.Severity);
        }
        private static string Module(string kind)
        {
            switch (kind)
            {
                case "session": return Text("对局", "SESSION");
                case "background": return Text("后台治理", "BACKGROUND");
                case "boost": return Text("游戏提优", "GAME PRIORITY");
                case "affinity": return Text("线程与核心", "CPU PLACEMENT");
                case "recovery": return Text("恢复", "RESTORATION");
                case "issue": return Text("异常与提示", "ATTENTION");
                default: return Text("功能状态", "FEATURE");
            }
        }
        private static void Add(List<LogEventEntry> output, LogEventEntry source, string kind, string message, LogEventSeverity severity)
        {
            LogEventEntry entry = Copy(source); entry.Module = Module(kind); entry.Message = message;
            entry.Severity = source.Severity == LogEventSeverity.Error ? LogEventSeverity.Error : source.Severity == LogEventSeverity.Warning ? LogEventSeverity.Warning : severity;
            entry.Raw = (entry.Timestamp == DateTime.MinValue ? entry.Date + " " + entry.Time : entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) + "  [" + entry.Module + "] " + message;
            entry.Details.Add(source); output.Add(entry);
        }
        private static LogEventEntry Copy(LogEventEntry source)
        { return new LogEventEntry { Raw = source.Raw ?? "", Date = source.Date, Time = source.Time, Timestamp = source.Timestamp, Module = source.Module, Message = source.Message, Severity = source.Severity, SessionToken = source.SessionToken ?? "", SessionName = source.SessionName ?? "", Phase = source.Phase, FirstTime = source.FirstTime, LastTime = source.LastTime }; }
        private static string Body(string raw)
        {
            string value = (raw ?? "").Trim(); DateTime timestamp;
            if (value.Length >= 19 && DateTime.TryParseExact(value.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp)) value = value.Substring(19).Trim();
            value = LogText.Canonical(value);
            foreach (string prefix in new[] { "INFO ", "PASS ", "WARN ", "FAIL " }) if (value.StartsWith(prefix, StringComparison.Ordinal)) return value.Substring(prefix.Length).Trim();
            return value;
        }
        private static string Value(string body, string key)
        {
            string marker = key + "="; int start = 0;
            while ((start = body.IndexOf(marker, start, StringComparison.Ordinal)) >= 0)
            {
                if (start == 0 || (!char.IsLetterOrDigit(body[start - 1]) && body[start - 1] != '_'))
                { int begin = start + marker.Length, end = begin; while (end < body.Length && !char.IsWhiteSpace(body[end]) && body[end] != ';') end++; return body.Substring(begin, end - begin); }
                start += marker.Length;
            }
            return "";
        }
        private static string Tail(string body, string marker) { int index = body.IndexOf(marker, StringComparison.Ordinal); return index < 0 ? "" : body.Substring(index + marker.Length); }
        private static bool Starts(string value, params string[] prefixes) { foreach (string prefix in prefixes) if (value.StartsWith(prefix, StringComparison.Ordinal)) return true; return false; }
        private static string Process(string name, string pid) { return (name ?? "").Trim() + " (PID " + pid + ")"; }
        private static string AfterFirstSpace(string text) { int at = text.IndexOf(' '); return at < 0 ? text : text.Substring(at + 1); }
        private static string Clean(string value)
        {
            string replaceResult = Technical.Replace(value ?? "", "");
            replaceResult = Regex.Replace(replaceResult, @"(?<!\S)[A-Za-z][A-Za-z0-9_-]*=[^\s;]+", "");
            replaceResult = Regex.Replace(replaceResult, @"\b0x[0-9A-Fa-f]+\b", "");
            return Regex.Replace(replaceResult, @"\s+", " ").Trim();
        }
        private static string Mask(string value)
        {
            ulong mask; if (!TryMask(value, out mask)) return "";
            var ranges = new List<string>();
            for (int index = 0; index < 64; index++)
            {
                if ((mask & (1UL << index)) == 0) continue;
                int first = index; while (index < 63 && (mask & (1UL << (index + 1))) != 0) index++;
                ranges.Add(first == index ? first.ToString(CultureInfo.InvariantCulture) : first.ToString(CultureInfo.InvariantCulture) + "–" + index.ToString(CultureInfo.InvariantCulture));
            }
            return string.Join(", ", ranges.ToArray());
        }
        private static bool TryMask(string value, out ulong mask)
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
            return ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out mask) && mask != 0;
        }
    }
}
