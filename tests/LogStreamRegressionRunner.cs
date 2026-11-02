#if PAVISE_SELFTEST && PAVISE_LOG_STREAM_RUNNER
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class LogStreamRegressionRunner
    {
        private static int checks, suites, failures;
        private static string output;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            try
            {
                output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
                Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest(); Logger.LogPath = Path.Combine(output, "isolated.log");
                Logger.WritesEnabled = false;
                Dpi.Init(); Dpi.NoFit = true; Lang.Init();
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Run("severity compatibility", delegate { SelfTests.RunLogSeverityRegressionTests(); });
                Run("log writes compatibility", delegate { SelfTests.RunLogWritesRegressionTests(); });
                Run("repetition and telemetry", RepetitionAndTelemetry);
                Run("fault identities remain separate", FaultIdentityBoundaries);
                Run("recovery separates repetition", RecoverySplitsRepetition);
                Run("automatic success summaries retain originals", SuccessSummaries);
                Run("session filtering includes all modules", SessionFiltering);
                Run("session metadata recording", SessionRecording);
                Run("legacy translated session association", LegacySessions);
                Run("truncated and missing sessions", MissingSessions);
                Run("clear rotation and legacy XML", FileBoundaries);
                Run("page shows only the latest session without counts", LatestSessionPage);
                Run("selection scroll and expansion", ReadingState);
                Run("language and DPI render matrix", RenderMatrix);
                Console.WriteLine("RESULT suites=" + suites + " failed=" + failures + " assertions=" + checks
                    + " settings=transient logs=temp application_started=false tuning_workers_started=false");
                return failures == 0 ? 0 : 1;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { Logger.WritesEnabled = false; Dpi.Update(96); Theme.DropFontCache(); }
        }

        private static void Run(string name, Action test)
        {
            suites++;
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                failures++; Console.Error.WriteLine("FAIL " + name + ": " + error);
            }
        }
        private static void Check(bool good, string reason)
        { if (!good) throw new InvalidOperationException(reason); checks++; }
        private static string Fixture(params string[] bodies)
        {
            var lines = new List<string>(); DateTime start = new DateTime(2026, 10, 5, 1, 0, 0);
            for (int i = 0; i < bodies.Length; i++)
                lines.Add(start.AddSeconds(i).ToString("yyyy-MM-dd HH:mm:ss") + "  " + bodies[i]);
            return string.Join("\r\n", lines.ToArray());
        }
        private static List<LogEventEntry> Events(string text)
        { return LogStreamView.ParseEvents(text); }
        private static List<LogEventEntry> Project(string text, int filter = 0, string session = "")
        { return LogEventProjection.Project(Events(text), filter, session); }
        private static int Count(List<LogEventEntry> entries, Predicate<LogEventEntry> condition)
        { return entries.FindAll(condition).Count; }
        private static void AllRawPresent(List<LogEventEntry> projected, List<LogEventEntry> original)
        {
            string raw = string.Join("\n", projected.ConvertAll(delegate(LogEventEntry item) { return item.Raw; }).ToArray());
            foreach (LogEventEntry entry in original) Check(raw.Contains(entry.Raw), "Projection dropped original: " + entry.Raw);
        }

        private static void RepetitionAndTelemetry()
        {
            string text = Fixture("INFO SESSION [same] start | Rainbow Six",
                "WARN FRAME budget [reason=EventBudget traceCode=0 lostEvents=0 eventCount=750001 sampleCpuMs=93.8]",
                "WARN FRAME budget [reason=EventBudget traceCode=0 lostEvents=0 eventCount=800001 sampleCpuMs=125.0]");
            var original = Events(text); var projected = LogEventProjection.Project(original, 1, "same");
            Check(projected.Count == 1, "Same warning with telemetry changes was not folded");
            LogEventEntry folded = projected[0];
            Check(folded.RepeatCount == 2, "Repeated count must represent both original events");
            Check(folded.FirstTime == "2026-10-05 01:00:01" && folded.LastTime == "2026-10-05 01:00:02", "First/last timestamp lost");
            Check(folded.Details != null && folded.Details.Count == 2, "Repeated details incomplete");
            Check(folded.Message.IndexOf("Repeated", StringComparison.Ordinal) < 0 && folded.Message.IndexOf("重复", StringComparison.Ordinal) < 0,
                "Folded events still display a log repetition count");
            AllRawPresent(projected, original.FindAll(delegate(LogEventEntry entry) { return entry.Severity == LogEventSeverity.Warning; }));
            string stable = folded.StableKey;
            text += "\r\n2026-10-05 01:00:03  WARN FRAME budget [reason=EventBudget traceCode=0 lostEvents=0 eventCount=900001 sampleCpuMs=140.0]";
            var appended = Project(text, 1, "same");
            Check(appended.Count == 1 && appended[0].RepeatCount == 3, "Appended same warning did not join its group");
            Check(appended[0].StableKey == stable, "Identity changed merely because repeated count increased");
            Check(original[1].RepeatCount == 1 && original[1].Raw.IndexOf("800001", StringComparison.Ordinal) < 0,
                "Project mutated its source event");
            Check(Project(text, 2, "same").Count == 0, "Warnings leaked into the error-only filter");
        }

        private static void FaultIdentityBoundaries()
        {
            foreach (string field in new[] { "code", "pid", "tid", "reason", "lostEvents" })
            {
                string first = field == "reason" ? "A" : "1";
                string second = field == "reason" ? "B" : "2";
                string text = Fixture("INFO SESSION [identity] start | game",
                    "FAIL FRAME operation [" + field + "=" + first + "]",
                    "FAIL FRAME operation [" + field + "=" + second + "]");
                var rows = Project(text, 2, "identity");
                Check(rows.Count == 2 && rows[0].RepeatCount == 1 && rows[1].RepeatCount == 1,
                    "Distinct fault field was swallowed: " + field);
            }
            string sessions = Fixture("INFO SESSION [one] start | same game",
                "WARN FRAME budget reason=EventBudget", "INFO SESSION [one] restored | same game",
                "INFO SESSION [two] start | same game", "WARN FRAME budget reason=EventBudget");
            var warnings = Project(sessions, 1);
            Check(warnings.Count == 2 && warnings[0].SessionToken != warnings[1].SessionToken, "Two same-name games/sessions merged");
            var levels = Project(Fixture("INFO SESSION [level] start | game", "WARN FRAME fault code=7", "FAIL FRAME fault code=7"), 1);
            Check(levels.Count == 2 && levels[0].Severity == LogEventSeverity.Error, "Different severity levels merged or newest order changed");
        }

        private static void RecoverySplitsRepetition()
        {
            string text = Fixture("INFO SESSION [recover] start | game", "WARN FRAME unavailable code=7",
                "INFO FRAME recovered", "WARN FRAME unavailable code=7");
            var warnings = Project(text, 1, "recover");
            Check(warnings.Count == 2, "A recovery transition must end a previous repeated warning group");
            Check(warnings[0].RepeatCount == 1 && warnings[1].RepeatCount == 1, "Recovery still counted as one continuous incident");
        }

        private static void SuccessSummaries()
        {
            string text = Fixture("INFO SESSION [summary] start | game", "PASS AUTO [summary] AutoGameDvr | applied",
                "PASS AUTO [summary] AutoDefenderResourcesV1 | applied", "WARN AUTO [summary] AutoWU | failed",
                "FAIL POWER restore code=5", "PASS AUTO [summary] AutoGameDvr | restored",
                "PASS AUTO [summary] AutoDefenderResourcesV1 | restored");
            var original = Events(text); var rows = LogEventProjection.Project(original, 0, "summary");
            Check(Count(rows, delegate(LogEventEntry e) { return e.IsSummary; }) == 2, "Applied and restored successes should have distinct summaries");
            Check(Count(rows, delegate(LogEventEntry e) { return e.Severity == LogEventSeverity.Warning; }) == 1, "Warning was swallowed by success summary");
            Check(Count(rows, delegate(LogEventEntry e) { return e.Severity == LogEventSeverity.Error; }) == 1, "Restore failure was swallowed by success summary");
            foreach (LogEventEntry row in rows.FindAll(delegate(LogEventEntry e) { return e.IsSummary; }))
                Check(row.Details != null && row.Details.Count == 2 && row.RepeatCount == 2, "Success summary has incomplete original detail/count");
            AllRawPresent(rows, original);
            string duplicates = Fixture("INFO SESSION [duplicate-actions] start | game", "PASS AUTO [duplicate-actions] AutoGameDvr | applied",
                "PASS AUTO [duplicate-actions] AutoGameDvr | applied", "PASS POWER selected", "PASS MEMORY selected");
            foreach (LogEventEntry summary in Project(duplicates).FindAll(delegate(LogEventEntry e) { return e.IsSummary; }))
                Check(!summary.Message.Contains("条") && !summary.Message.Contains("records:") && !summary.Message.Contains("件"),
                    "Success summary still displays raw log quantities");
            Check(Project(text, 2, "summary").Count == 1, "Error filter failed to retain real fault");
            string ordinary = Fixture("INFO FRAME observation reason=Recorded", "INFO POWER configured", "INFO FRAME observation reason=ForegroundChanged");
            Check(Project(ordinary).Count == 3, "Unrelated informative state changes must remain visible");
        }

        private static void SessionFiltering()
        {
            string text = Fixture("INFO POWER startup", "INFO SESSION [old] start | old game", "INFO FRAME old lane",
                "PASS SESSION [old] restored | old game", "INFO SESSION [new] start | new game", "PASS POWER plan applied",
                "WARN FRAME new budget reason=EventBudget", "PASS AUTO [new] AutoGameDvr | applied");
            var rows = Project(text, 4, "new");
            string raw = string.Join("\n", rows.ConvertAll(delegate(LogEventEntry e) { return e.Raw; }).ToArray());
            Check(raw.Contains("POWER plan applied") && raw.Contains("FRAME new budget"), "Latest-session view omitted non-AUTO modules");
            Check(raw.Contains("AUTO [new]") && !raw.Contains("old lane") && !raw.Contains("POWER startup"), "Latest-session scope is incorrect");
            var missing = Project(text, 4, "missing-token");
            Check(missing.Count == 0, "Unknown active token leaked the previous session or unowned errors");
            Check(Project(text, 3, "new").Count == 1, "AUTO filter must remain the automatic action filter");
            string platform = Fixture("INFO AUTO [platform] [PLATFORM] protected pid=123 exe=rail.exe", "INFO FRAME platform lane");
            Check(Project(platform, 3).Count == 1, "Unstructured platform AUTO envelope disappeared from the automatic filter");
            Check(Project(platform, 4, "platform").Count == 2, "Platform envelope did not establish a retained session fragment");
            var global = Events(Fixture("INFO AUTO [] AutoGlobalTimer | configured", "INFO FRAME global"));
            Check(global[0].SessionToken == "" && global[1].SessionToken == "", "An empty global AUTO token invented a game session");
            string late = Fixture("INFO AUTO [primary-fragment] AutoGameDvr | configured", "INFO FRAME retained-primary",
                "PASS AUTO [foreign-late] session | restored");
            var lateRows = Project(late, 4);
            Check(lateRows.Exists(delegate(LogEventEntry e) { return e.Raw.Contains("retained-primary"); })
                && !lateRows.Exists(delegate(LogEventEntry e) { return e.SessionToken == "foreign-late"; }),
                "Late foreign restoration stole the current retained fragment");
            string laterFragment = Fixture("INFO SESSION [closed] start | old", "PASS SESSION [closed] restored | old",
                "INFO AUTO [new-fragment] AutoGameDvr | configured", "INFO FRAME fragment-after-closed",
                "PASS AUTO [closed] session | restored");
            Check(Project(laterFragment, 4).Exists(delegate(LogEventEntry e) { return e.Raw.Contains("fragment-after-closed"); }),
                "A closed historical reliable start overrides a later new fragment");
            var old = Project(text, 4, "old");
            Check(old.Exists(delegate(LogEventEntry e) { return e.Raw.Contains("old lane"); })
                && !old.Exists(delegate(LogEventEntry e) { return e.Raw.Contains("new budget"); }), "Explicit older session is ignored");
            var end = Project(Fixture("INFO SESSION [end] start | game", "INFO SESSION [end] end | game", "WARN SESSION [end] restore-failed | game"), 4, "end");
            Check(end.Exists(delegate(LogEventEntry e) { return e.Severity == LogEventSeverity.Warning && e.Raw.Contains("restore-failed"); }),
                "Restore failure disappeared or became green at session end");
        }

        private static void MissingSessions()
        {
            string plain = Fixture("INFO FRAME waiting", "WARN POWER denied code=5");
            Check(Project(plain, 4).Count == 0, "No-session logs leaked into the latest-session page");
            Check(Project(plain).Count == 2, "Strict session display deleted the original non-session records");
            string truncated = Fixture("INFO AUTO [tail] session | active", "PASS POWER selected", "WARN FRAME budget reason=EventBudget");
            var rows = Project(truncated, 4);
            Check(rows.Exists(delegate(LogEventEntry e) { return e.Raw.Contains("POWER selected"); })
                && rows.Exists(delegate(LogEventEntry e) { return e.Raw.Contains("FRAME budget"); }), "Truncated session cannot associate subsequent modules");
            Check(Project("", 4).Count == 0 && LogStreamView.ParseEvents("\r\n").Count == 0, "Empty/clear input creates phantom rows");
            string unicode = Fixture("INFO SESSION [游戏] start | ゲーム", "WARN FRAME 执行受限：权限不足 code=5");
            AllRawPresent(Project(unicode), Events(unicode));
        }

        private static void ModeCall(GameMode mode, string method, params object[] arguments)
        { typeof(GameMode).GetMethod(method, Hidden).Invoke(mode, arguments); }
        private static void SessionRecording()
        {
            string previous = Logger.LogPath;
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Settings.Save(AutomaticPolicy.VersionKey, true);
                Logger.ResetWriteBarrierForTest(); Logger.LogPath = Path.Combine(output, "session-metadata.log");
                Logger.WritesEnabled = false;
                string dir = Path.Combine(output, "session-mode"); Directory.CreateDirectory(dir);
                var mode = new GameMode(dir, new SuppressionCore());
                FieldInfo active = typeof(GameMode).GetField("activeGame", Hidden);
                active.SetValue(mode, "游戏 | Rainbow Six");
                Logger.WritesEnabled = true;
                ModeCall(mode, "BeginAutomationEvidence"); string first = mode.AutomationSession;
                Check(first.Length > 0 && !mode.IsActive, "Metadata start should create only a log session");
                ModeCall(mode, "EndAutomationEvidence"); ModeCall(mode, "EndAutomationEvidence");
                ModeCall(mode, "RecordAutomation", "session", "restore-failed");
                ModeCall(mode, "RecordAutomation", "session", "restore-failed");
                ModeCall(mode, "RecordAutomation", "session", "restored");
                string firstText = File.ReadAllText(Logger.LogPath);
                Check(Occurrences(firstText, "SESSION [" + first + "] start |") == 1, "Start boundary was duplicated");
                Check(Occurrences(firstText, "SESSION [" + first + "] end |") == 1, "Repeated end emitted duplicate boundaries");
                Check(Occurrences(firstText, "SESSION [" + first + "] restore-failed |") == 1,
                    "Repeated same recovery failure emitted duplicate boundaries");
                Check(Occurrences(firstText, "SESSION [" + first + "] restored |") == 1, "Success retry failed to close original session");
                var parsed = Events(firstText);
                Check(parsed.Exists(delegate(LogEventEntry e) { return e.SessionToken == first && e.SessionName == "游戏 | Rainbow Six"; }),
                    "Session name was cut at its embedded separator");
                Check(!parsed.Exists(delegate(LogEventEntry e) { return e.SessionToken.Length > 0 && e.SessionToken != first; }),
                    "SESSION and AUTO produced separate identities for one game");
                active.SetValue(mode, "Second Game"); ModeCall(mode, "BeginAutomationEvidence"); string second = mode.AutomationSession;
                active.SetValue(mode, "Third Game"); ModeCall(mode, "BeginAutomationEvidence"); string third = mode.AutomationSession;
                Check(first != second && second != third && first != third, "Rapid successive games reused a session identity");
                string allText = File.ReadAllText(Logger.LogPath);
                Check(Occurrences(allText, "SESSION [" + second + "] end |") == 1, "Game handoff failed to end old metadata session");
                Check(!allText.Contains("SESSION [" + second + "] restored |"), "Handoff invented a completed restoration");
                Check(!mode.IsActive, "Pure metadata methods started the game runtime");
            }
            finally { Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false; Logger.LogPath = previous; }
        }
        private static int Occurrences(string text, string pattern)
        {
            int count = 0, position = 0;
            while ((position = text.IndexOf(pattern, position, StringComparison.Ordinal)) >= 0) { count++; position += pattern.Length; }
            return count;
        }
        private static void LegacySessions()
        {
            int oldLanguage = Lang.Cur;
            try
            {
                foreach (int language in new[] { 0, 1, 2 })
                {
                    Lang.Cur = language;
                    string text = Fixture("INFO 游戏模式激活 检测到 测试游戏",
                        "INFO FRAME activation-before-AUTO", "PASS AUTO [legacy] " + GameMode.AutomationActionLabel("session") + " | " + GameMode.AutomationStateLabel("started"),
                        "PASS POWER game-plan", "INFO 本局结束 测试游戏", "INFO POWER restoring",
                        "WARN AUTO [legacy] " + GameMode.AutomationActionLabel("session") + " | " + GameMode.AutomationStateLabel("restore-failed"),
                        "PASS AUTO [legacy] " + GameMode.AutomationActionLabel("session") + " | " + GameMode.AutomationStateLabel("restored"));
                    var rows = Project(text, 4, "legacy");
                    Check(rows.Exists(delegate(LogEventEntry e) { return e.Raw.Contains("activation-before-AUTO") && e.SessionToken == "legacy"; }),
                        "Legacy activation wasn't remapped to its actual AUTO token: " + language);
                    Check(rows.Exists(delegate(LogEventEntry e) { return e.Raw.Contains("POWER game-plan"); }), "Legacy session omitted normal POWER module");
                    Check(rows.Exists(delegate(LogEventEntry e) { return e.Phase == "restore-failed" && e.Severity == LogEventSeverity.Warning; }),
                        "Translated restore failure lost phase/severity: " + language);
                    string handoff = Fixture("INFO 游戏模式激活 检测到 First", "INFO FRAME first owned",
                        "INFO 游戏模式 检测目标变更 Second", "INFO 本局结束 First", "INFO FRAME second owned");
                    var history = Events(handoff);
                    string old = history[1].SessionToken, current = history[4].SessionToken;
                    Check(old.Length > 0 && current.Length > 0 && old != current && history[3].SessionToken == old,
                        "Legacy handoff attached old ReportFinish to new game");
                    string mixed = Fixture("INFO 游戏模式激活 检测到 First", "INFO FRAME first owned",
                        "INFO 游戏模式 检测目标变更 Second", "INFO 本局结束 First",
                        "PASS AUTO [second] " + GameMode.AutomationActionLabel("session") + " | " + GameMode.AutomationStateLabel("started"),
                        "INFO FRAME second owned");
                    var mixedEvents = Events(mixed);
                    Check(mixedEvents[1].SessionToken != "second" && mixedEvents[3].SessionToken != "second"
                        && mixedEvents[5].SessionToken == "second", "AUTO handoff stole the previous legacy game rows");
                    foreach (bool legacy in new[] { false, true })
                    {
                        string completeHandoff = Fixture(legacy ? "INFO 游戏模式激活 检测到 First" : "INFO SESSION [first] start | First",
                            "INFO FRAME first owned", "INFO 游戏模式 检测目标变更 Second", "INFO 本局结束 First",
                            "WARN CORE old-core-restoration-pending reason=Retry", "INFO SESSION [first] end | First",
                            "INFO SESSION [second] start | Second",
                            "PASS AUTO [second] " + GameMode.AutomationActionLabel("session") + " | " + GameMode.AutomationStateLabel("started"),
                            "INFO FRAME second owned");
                        var ordered = Events(completeHandoff);
                        Check(ordered[4].SessionToken == ordered[1].SessionToken && ordered[4].SessionToken != "second",
                            "Old core restoration warning moved to next game before its reliable start, legacy=" + legacy);
                        Check(ordered[8].SessionToken == "second", "Reliable mixed handoff failed to associate new game");
                    }
                }
            }
            finally { Lang.Cur = oldLanguage; }
        }

        private static void FileBoundaries()
        {
            string previous = Logger.LogPath;
            try
            {
                Logger.ResetWriteBarrierForTest(); Logger.LogPath = Path.Combine(output, "rotation.log");
                File.WriteAllText(Logger.LogPath, new string('x', 512 * 1024 + 1), new UTF8Encoding(false));
                Logger.Log("INFO SESSION [rotated] start | test"); Logger.Log("WARN FRAME after-rotation code=5");
                Check(File.Exists(Logger.LogPath + ".old"), "Controlled log rotation did not preserve the old file");
                string tail = Logger.Tail(400);
                Check(tail.Contains("[rotated]") && !tail.StartsWith("xxx", StringComparison.Ordinal), "Rotated tail includes stale file bytes");
                Check(Project(tail, 4).Count == 2, "Rotation broke latest session projection");
                Logger.Clear(); Check(Logger.Tail(400) == "", "Clear retained old projected content");
                Logger.Log("INFO FRAME fresh-after-clear");
                Check(Project(Logger.Tail(400), 4).Count == 0 && Project(Logger.Tail(400)).Count == 1,
                    "Post-clear records invented a session or vanished from the source");
                Logger.Log("INFO SESSION [post-clear] start | test"); Logger.Log("INFO FRAME post-clear-owned");
                Check(Project(Logger.Tail(400), 4).Count == 2, "First new session after clear was not displayed");
                string xml = "2026-10-05 01:00:00  WARN PERFORMANCE failed #< CLIXML\r\n"
                    + "<Objs Version=\"1.1.0.1\"><S S=\"Error\">denied_x000D__x000A_</S></Objs>\r\n"
                    + "2026-10-05 01:00:01  INFO FRAME next";
                Logger.LogPath = Path.Combine(output, "legacy-xml.log"); File.WriteAllText(Logger.LogPath, xml, new UTF8Encoding(false));
                string xmlTail = Logger.Tail(2); var events = LogStreamView.ParseEvents(xmlTail);
                Check(events.Count == 2 && events[0].Severity == LogEventSeverity.Warning, "CLIXML tail boundary created a fake error row");
                Check(events[0].Raw.Contains("#< CLIXML") && events[0].Raw.Contains("</Objs>"), "Legacy raw XML lost during folding");
            }
            finally { Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false; Logger.LogPath = previous; }
        }

        private static T Field<T>(object instance, string name)
        { return (T)instance.GetType().GetField(name, Hidden).GetValue(instance); }
        private static void LatestSessionPage()
        {
            var panel = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
            GC.SuppressFinalize(panel);
            string previousPath = Logger.LogPath;
            bool previousWrites = Logger.WritesEnabled;
            using (var view = new LogStreamView())
            using (var hint = new Label())
            try
            {
                Action<string, object> set = delegate(string name, object value) { typeof(PanelForm).GetField(name, Hidden).SetValue(panel, value); };
                var mode = new GameMode(Path.Combine(output, "page-session-mode"), new SuppressionCore());
                set("logStream", view); set("lblLogStreamHint", hint); set("gameMode", mode);
                typeof(PanelForm).GetMethod("ConfigureLogView", Hidden).Invoke(panel, null);
                Check(view.Filter == 4, "Page must always select only the latest session");
                foreach (string field in new[] { "logFilterTabs", "lblLogTotal", "lblLogNormal", "lblLogErrors", "lblLogLatest" })
                    Check(typeof(PanelForm).GetField(field, Hidden) == null, "Page still exposes filter/count controls: " + field);
                Check(typeof(PanelForm).GetMethod("ConfigureLogFilters", Hidden) == null
                    && typeof(PanelForm).GetMethod("SyncLogCounters", Hidden) == null
                    && typeof(PanelForm).GetMethod("OpenTextFile", Hidden) == null,
                    "Page still exposes all-log filters, counters or raw-log opening");
                string text = Fixture("INFO SESSION [old-page] start | same game", "WARN FRAME old warning",
                    "PASS SESSION [old-page] restored | same game", "INFO SESSION [page] start | same game",
                    "WARN FRAME current warning reason=Limited", "WARN FRAME current warning reason=Limited",
                    "FAIL POWER current restore code=5", "PASS SESSION [old-page] restored | same game");
                Logger.LogPath = Path.Combine(output, "page-source-preserved.log");
                File.WriteAllText(Logger.LogPath, text, new UTF8Encoding(false));
                Logger.WritesEnabled = false;
                typeof(GameMode).GetField("automationSession", Hidden).SetValue(mode, "page");
                typeof(PanelForm).GetMethod("RefreshLog", Hidden, null, new[] { typeof(bool) }, null).Invoke(panel, new object[] { true });
                Check(view.GroupCount == 3 && view.GroupErrorCount == 1, "Latest session dropped warning/failure or retained the previous same-name game");
                Check(Field<List<LogEventEntry>>(view, "groups").TrueForAll(delegate(LogEventEntry e) { return e.SessionToken == "page"; }),
                    "A late restoration from the previous match stole the latest session");
                var originals = Field<List<LogEventEntry>>(view, "all");
                Check(originals.Exists(delegate(LogEventEntry e) { return e.Raw.Contains("old warning"); })
                    && originals.Exists(delegate(LogEventEntry e) { return e.Raw.Contains("current warning"); }),
                    "Display-only session filtering deleted older or newer originals from memory");
                Check(view.TotalCount == Events(text).Count && File.ReadAllText(Logger.LogPath) == text,
                    "Viewing the latest session altered persisted raw logs");
                Action sync = delegate { typeof(PanelForm).GetMethod("SyncLogHint", Hidden).Invoke(panel, null); };
                sync(); Check(hint.Text == Lang.T("v20.log.paused"), "Paused hint has counters or incorrect text");
                Logger.WritesEnabled = true; sync();
                Check(hint.Text == Lang.T("v20.log.hint"), "Live hint has counters or incorrect text");
                set("diagnosticCopiedUntil", DateTime.UtcNow.AddMinutes(1).Ticks); sync();
                Check(hint.Text == Lang.T("workflow.diagnostic.copied"), "Copied feedback was lost");
                set("diagnosticCopiedUntil", 0L); Logger.WritesEnabled = false;
                long version = Logger.Version;
                typeof(GameMode).GetField("automationSession", Hidden).SetValue(mode, "new-unlogged-page");
                typeof(PanelForm).GetMethod("RefreshLog", Hidden, null, new[] { typeof(bool) }, null).Invoke(panel, new object[] { false });
                Check(view.GroupCount == 0 && view.TotalCount == Events(text).Count && Logger.Version == version,
                    "New game with writes paused retained previous rows or changed the log");
                Check(File.ReadAllText(Logger.LogPath) == text, "Changing the visible game deleted persisted history");
                view.SessionToken = ""; view.SetText(Fixture("INFO CORE startup", "FAIL POWER startup failure"));
                Check(view.GroupCount == 0 && view.TotalCount == 2, "A page with no recognized game fell back to all logs");
                view.SetText("");
                Check(view.GroupCount == 0 && view.TotalCount == 0, "Clear retained a phantom session");
                Check(LogStreamView.DisplaySeverity(LogEventSeverity.Warning) == LogEventSeverity.Info,
                    "Warnings should display as ordinary events");
            }
            finally { Logger.WritesEnabled = previousWrites; Logger.LogPath = previousPath; }
        }
        private static void Mouse(LogStreamView view, int x, int y)
        {
            typeof(LogStreamView).GetMethod("OnMouseDown", Hidden).Invoke(view,
                new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
        }
        private static string AnchorKey(LogStreamView view)
        {
            var shown = Field<List<LogEventEntry>>(view, "shown");
            int row = Math.Max(0, -view.AutoScrollPosition.Y) / Math.Max(1, Theme.S(68));
            return row < shown.Count ? shown[row].StableKey : "";
        }
        private static void ReadingState()
        {
            Dpi.Update(96); Theme.DropFontCache(); Lang.Cur = 0;
            var bodies = new List<string> { "INFO SESSION [read] start | game" };
            for (int i = 0; i < 36; i++) bodies.Add("INFO CORE visible-state-" + i);
            bodies.Add("WARN FRAME budget reason=EventBudget eventCount=750001 sampleCpuMs=93.8");
            bodies.Add("WARN FRAME budget reason=EventBudget eventCount=750002 sampleCpuMs=140.6");
            string text = Fixture(bodies.ToArray());
            using (var host = new LogOffscreenForm())
            using (var view = new LogStreamView())
            {
                host.ClientSize = new Size(1080, 380); host.Controls.Add(view);
                view.Dock = DockStyle.Fill; host.Show(); Application.DoEvents();
                Check(view.SetText(text), "First text did not build log rows");
                Check(!view.SetText(text), "Identical text needlessly rebuilds reading state");
                Check(view.TotalCount == bodies.Count && view.GroupCount == bodies.Count - 1,
                    "Raw counters should remain complete while visible groups are reduced");
                Check(view.WarningCount == 2 && view.GroupWarningCount == 1, "Raw warnings and grouped warnings are confused");
                int rowH = Theme.S(68);
                view.AutoScrollPosition = new Point(0, rowH * 6 + 9); Application.DoEvents();
                Mouse(view, Theme.S(270), rowH + Theme.S(18));
                string selected = view.SelectedRaw, anchor = AnchorKey(view);
                int pixel = -view.AutoScrollPosition.Y % rowH;
                Check(selected.Length > 0 && view.AutoScrollPosition.Y < 0, "Reading fixture did not select a scrolled row");
                var groups = Field<List<LogEventEntry>>(view, "groups");
                int repeatIndex = groups.FindIndex(delegate(LogEventEntry e) { return e.RepeatCount == 2; });
                Check(repeatIndex >= 0, "Repeated warning is unavailable for expansion");
                string repeatKey = groups[repeatIndex].StableKey;
                int shownBefore = Field<List<LogEventEntry>>(view, "shown").Count;
                view.ToggleExpanded(repeatIndex); Application.DoEvents();
                Check(Field<List<LogEventEntry>>(view, "shown").Count == shownBefore + 2, "Expansion omitted original detail rows");
                Check(AnchorKey(view) == anchor && -view.AutoScrollPosition.Y % rowH == pixel, "Expanding above reader jumped the viewport");
                Check(view.SelectedRaw == selected, "Expansion changed the selected event");
                var expanded = Field<HashSet<string>>(view, "expanded");
                Check(expanded.Contains(repeatKey), "Expanded group state not recorded");
                int detailIndex = Field<List<LogEventEntry>>(view, "shown").FindIndex(delegate(LogEventEntry e) { return e.Raw.Contains("eventCount=750001") && e.RepeatCount == 1; });
                int detailCount = Field<List<LogEventEntry>>(view, "shown").Count;
                view.ToggleExpanded(detailIndex);
                Check(Field<List<LogEventEntry>>(view, "shown").Count == detailCount, "Original child detail incorrectly expands again");
                text += "\r\n2026-10-05 01:01:00  INFO POWER newly-added-one\r\n2026-10-05 01:01:01  INFO MEMORY newly-added-two";
                Check(view.SetText(text), "New text was not accepted"); Application.DoEvents();
                Check(AnchorKey(view) == anchor && -view.AutoScrollPosition.Y % rowH == pixel, "Refresh jumped away from the reader's anchor");
                Check(view.SelectedRaw == selected && expanded.Contains(repeatKey), "Refresh lost selected/expanded state");
                view.AutoScrollPosition = Point.Empty; Application.DoEvents();
                Check(view.AutoScrollPosition.Y == 0, "Scrolling to the top failed");
                Check(view.SelectedRaw == selected, "Jump to latest discarded selection");
                Mouse(view, Theme.S(280), Theme.S(20));
                string newestSelected = view.SelectedRaw;
                Check(newestSelected.Contains("newly-added-two"), "Top row ordering or mouse routing is wrong");
                view.AutoScrollPosition = new Point(0, rowH * 6 + 9);
                string tail = string.Join("\r\n", text.Split(new[] { "\r\n" }, StringSplitOptions.None), 10,
                    text.Split(new[] { "\r\n" }, StringSplitOptions.None).Length - 10);
                view.SetText(tail); Application.DoEvents();
                Check(view.SelectedRaw == newestSelected, "Tail trimming discarded a still-visible selected event");
                var rowsBeforeChild = Field<List<LogEventEntry>>(view, "shown");
                int child = rowsBeforeChild.FindIndex(delegate(LogEventEntry e) { return e.Raw.Contains("eventCount=750001") && e.RepeatCount == 1; });
                Check(child >= 0, "Expanded first child disappeared before its selection regression");
                view.AutoScrollPosition = new Point(0, child * rowH + Theme.S(8)); Application.DoEvents();
                Mouse(view, Theme.S(500), Theme.S(18));
                string childRaw = view.SelectedRaw;
                string childKey = rowsBeforeChild[Field<int>(view, "selected")].StableKey;
                Check(childRaw.Contains("eventCount=750001") && !childRaw.Contains("750002"), "Clicking an expanded child selected its parent");
                tail += "\r\n2026-10-05 01:01:02  WARN FRAME budget reason=EventBudget eventCount=750003 sampleCpuMs=166.0";
                view.SetText(tail); Application.DoEvents();
                Check(view.SelectedRaw == childRaw && Field<List<LogEventEntry>>(view, "shown")[Field<int>(view, "selected")].StableKey == childKey,
                    "Refresh changed a selected detail into its aggregate parent");
                Check(Field<List<LogEventEntry>>(view, "groups").FindAll(delegate(LogEventEntry e) { return e.SessionToken != "read"; }).Count == 0,
                    "Retained tail forgot its confirmed original session when start was cropped out");
                tail += "\r\n2026-10-05 01:01:03  INFO CORE continuing-original-session";
                view.SetText(tail);
                Check(Field<List<LogEventEntry>>(view, "groups").Exists(delegate(LogEventEntry e) { return e.SessionToken == "read" && e.Raw.Contains("continuing-original-session"); }),
                    "Appended tokenless event lost current session after cropped start");
                tail += "\r\n2026-10-05 01:01:04  INFO SESSION [new-read] start | next game\r\n2026-10-05 01:01:05  INFO CORE new-session-owner";
                view.SetText(tail);
                Check(Field<List<LogEventEntry>>(view, "groups").Exists(delegate(LogEventEntry e) { return e.SessionToken == "new-read" && e.Raw.Contains("new-session-owner"); }),
                    "New reliable start inherited the cropped previous session");
                view.SetText(""); Application.DoEvents();
                Check(view.TotalCount == 0 && view.GroupCount == 0 && view.SelectedRaw == "",
                    "Clear retained event state");
                Check(expanded.Count == 0 && view.AutoScrollPosition.Y == 0, "Clear retained expanded groups or stale scroll offset");
                view.SetText(Fixture("INFO CORE unowned-after-clear"));
                Check(Field<List<LogEventEntry>>(view, "groups")[0].SessionToken == "", "Clear did not discard session continuity");
                var sessionBodies = new List<string> { "INFO SESSION [first] start | same game" };
                for (int i = 0; i < 16; i++) sessionBodies.Add("INFO FRAME first-" + i);
                sessionBodies.Add("PASS SESSION [first] restored | same game");
                sessionBodies.Add("INFO SESSION [second] start | same game");
                for (int i = 0; i < 16; i++) sessionBodies.Add("INFO FRAME second-" + i);
                view.Filter = 4; view.SetText(Fixture(sessionBodies.ToArray()));
                view.SessionToken = "first";
                Check(Field<List<LogEventEntry>>(view, "groups").Exists(delegate(LogEventEntry e) { return e.Raw.Contains("FRAME first"); }), "Session setter did not apply chosen token");
                view.AutoScrollPosition = new Point(0, Theme.S(68) * 6);
                Mouse(view, Theme.S(270), Theme.S(18));
                Check(view.SelectedRaw.Length > 0 && view.AutoScrollPosition.Y < 0, "Session-switch fixture selected no scrolled row");
                view.SessionToken = "second";
                Application.DoEvents();
                Check(view.SelectedRaw == "" && view.AutoScrollPosition.Y == 0, "Switching sessions retained the previous selection or scroll offset");
                Check(Field<List<LogEventEntry>>(view, "groups").Exists(delegate(LogEventEntry e) { return e.Raw.Contains("FRAME second"); })
                    && !Field<List<LogEventEntry>>(view, "groups").Exists(delegate(LogEventEntry e) { return e.Raw.Contains("FRAME first"); }),
                    "Changing session without changing file did not rebuild the default view");
                var warnings = new List<string>();
                for (int i = 0; i < 20; i++) warnings.Add("WARN FRAME visible-warning reason=Reason" + i);
                view.Filter = 1; string warnText = Fixture(warnings.ToArray()); view.SetText(warnText);
                view.AutoScrollPosition = new Point(0, rowH * 3); Application.DoEvents();
                view.SetText(warnText + "\r\n2026-10-05 01:02:00  INFO CORE hidden-new-info");
                view.SetText(warnText + "\r\n2026-10-05 01:02:00  INFO CORE hidden-new-info\r\n2026-10-05 01:02:01  WARN FRAME visible-new-warning reason=New");
                int beforeClick = view.AutoScrollPosition.Y;
                Mouse(view, view.ClientSize.Width - Theme.S(120), Theme.S(20));
                Check(view.AutoScrollPosition.Y == beforeClick && view.SelectedRaw.Length > 0,
                    "New records must not add an overlay that intercepts event clicks or jumps the viewport");
                host.Hide();
            }
        }
        private static void RenderMatrix()
        {
            foreach (int language in new[] { 0, 1, 2 })
            foreach (int dpi in new[] { 96, 120, 144, 192 })
            {
                Lang.Cur = language; Dpi.Update(dpi); Theme.DropFontCache();
                string text = Fixture("INFO SESSION [visual] start | Rainbow Six Siege",
                    "INFO POWER preparing first", "INFO CORE preparing second", "INFO MEMORY preparing third",
                    "INFO GPU preparing fourth", "INFO FRAME preparing fifth", "INFO SYSTEM preparing sixth",
                    "PASS AUTO [visual] AutoGameDvr | applied", "PASS AUTO [visual] AutoDefenderResourcesV1 | applied",
                    "WARN FRAME long diagnostic reason=EventBudget code=0 lostEvents=0 eventCount=750001 sampleCpuMs=93.8",
                    "WARN FRAME long diagnostic reason=EventBudget code=0 lostEvents=0 eventCount=750002 sampleCpuMs=140.6");
                using (var host = new LogOffscreenForm())
                using (var view = new LogStreamView())
                {
                    host.ClientSize = new Size(Theme.S(1100), Theme.S(420));
                    host.Controls.Add(view); view.Dock = DockStyle.Fill; host.Show(); Application.DoEvents();
                    view.SetText(text);
                    var rows = Field<List<LogEventEntry>>(view, "shown");
                    int summary = rows.FindIndex(delegate(LogEventEntry e) { return e.IsSummary; });
                    Check(summary >= 0, "Language changed summary eligibility: " + language);
                    view.ToggleExpanded(summary); Application.DoEvents();
                    Check(Field<List<LogEventEntry>>(view, "shown").Count >= 5, "Expanded original rows missing at DPI=" + dpi);
                    Check(view.ClientSize.Width > Theme.S(430) && view.AutoScrollMinSize.Height >= view.ClientSize.Height,
                        "Layout has no room for event text at DPI=" + dpi);
                    using (var bitmap = new Bitmap(view.Width, view.Height))
                    {
                        view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        string path = Path.Combine(output, "log-language-" + language + "-dpi-" + dpi + ".png");
                        bitmap.Save(path); Check(new FileInfo(path).Length > 1024, "Render bitmap empty: " + path);
                    }
                    view.AutoScrollPosition = new Point(0, Theme.S(68));
                    string anchor = AnchorKey(view);
                    view.SetText(text + "\r\n2026-10-05 01:00:05  INFO FRAME newest visual event");
                    Check(AnchorKey(view) == anchor, "Appending a log record moved the reading position");
                    using (var bitmap = new Bitmap(view.Width, view.Height))
                    {
                        view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(output, "log-updated-language-" + language + "-dpi-" + dpi + ".png"));
                    }
                    host.Hide();
                }
                Console.WriteLine("PASS log view render language=" + language + " dpi=" + dpi);
            }
            Lang.Cur = 0; Dpi.Update(96); Theme.DropFontCache();
        }
    }

    internal sealed class LogOffscreenForm : Form
    {
        internal LogOffscreenForm()
        { ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000); }
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    internal static partial class SelfTests
    {
        private static void Eq<T>(T expected, T actual)
        { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Expected " + expected + ", actual " + actual); }
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Tuning runtime cannot start in the isolated log runner"); }
    }
}
#endif
