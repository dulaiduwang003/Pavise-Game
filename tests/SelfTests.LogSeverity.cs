// File purpose Pure classification checks, no log file writes, no UI or registry
// Log severity regression, verifies only tag-first and word-list fallback, no log file writes, no windows
#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int logSeverityChecks;

        internal static int RunLogSeverityRegressionTests()
        {
            Action[] tests =
            {
                LogSeverityTagBeatsKeywords,
                LogSeverityKeywordsStillClassifyUntaggedLines,
                LogSeverityTagIsStrippedFromBody,
                LogSeverityInfoTagBeatsFieldNames,
                LogSeverityRendererObservationDeclaresItsOwnLevel,
                LogSeverityCountedFailureLineDeclaresItsLevel,
                LogSeverityBoostSeparatesPlacementFromPriorities,
                LogSeverityLegacyBoostSummaryIsAWarning
            };
            logSeverityChecks = 0;
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS log-severity assertions=" + logSeverityChecks
                + " logfile=untouched windows_shown=false");
            return tests.Length;
        }

        private static void LogSeverityCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("log severity regression: " + message);
            logSeverityChecks++;
        }

        private static void LogSeverityTagBeatsKeywords()
        {
            string body;
            LogSeverityCheck(LogStreamView.ClassifyLine(Logger.WarnTag + "渲染主权域 无法采样 cs2", out body) == LogEventSeverity.Warning,
                "a WARN tag wins over the error keyword in the text");
            LogSeverityCheck(LogStreamView.ClassifyLine(Logger.FailTag + "疑似恶意进程 yIgaJZfC", out body) == LogEventSeverity.Error,
                "a FAIL tag makes a line an error even without any keyword");
            LogSeverityCheck(LogStreamView.ClassifyLine(Logger.WarnTag + "电源计划已还原", out body) == LogEventSeverity.Warning,
                "a WARN tag wins over the success keyword too");
        }

        private static void LogSeverityKeywordsStillClassifyUntaggedLines()
        {
            string body;
            LogSeverityCheck(LogStreamView.ClassifyLine("保留核写入失败 未生效", out body) == LogEventSeverity.Error,
                "an untagged line with a failure word is still an error");
            LogSeverityCheck(LogStreamView.ClassifyLine("显卡功耗墙 不可用", out body) == LogEventSeverity.Warning,
                "an untagged line with a warning word is still a warning");
            LogSeverityCheck(LogStreamView.ClassifyLine("Game DVR 设置已还原", out body) == LogEventSeverity.Success,
                "an untagged success line is still a success");
            LogSeverityCheck(LogStreamView.ClassifyLine("处理器 混合架构 大核 12 个", out body) == LogEventSeverity.Info,
                "a plain line stays informational");
        }

        // Structured diagnostic lines always carry field names like lastFailure= code=, substring word-list matching would read the key name as a verdict
        //   Renderer observation used to have every line judged abnormal, the successful one landed on the abnormal page too and bumped the count
        private static void LogSeverityInfoTagBeatsFieldNames()
        {
            string body;
            const string diagnostics = "GacRunnerNG(pid 12904) 已记录 GPU 3D 活动 [reason=Recorded]"
                + " gpu3d=68.31836304362983% pidCount=13 lastFailure=none code=0x00000000"
                + " collected=2 valid=2 instances=60 rejected=0";
            LogSeverityCheck(LogStreamView.ClassifyLine(diagnostics, out body) == LogEventSeverity.Error,
                "without a tag the lastFailure= field name still makes the词表 call it an error");
            LogSeverityCheck(LogStreamView.ClassifyLine(Logger.InfoTag + diagnostics, out body) == LogEventSeverity.Info,
                "an INFO tag wins over a field name that merely contains the word fail");
            LogSeverityCheck(body == diagnostics, "the INFO tag is not part of the displayed text");
            LogSeverityCheck(LogStreamView.ClassifyLine(Logger.InfoTag + "保留核写入失败", out body) == LogEventSeverity.Info,
                "an INFO tag wins over a real failure word too, so callers must use it deliberately");
        }

        // Renderer observation declares its own level by outcome: real faults are FAIL, no evidence collected is an environment limit, the rest are normal conclusions
        private static void LogSeverityRendererObservationDeclaresItsOwnLevel()
        {
            foreach (string reason in new[] { "Error", "QueueFailed", "SaveFailed" })
                LogSeverityCheck(GameMode.SeverityTagFor(reason) == Logger.FailTag,
                    reason + " is a real fault and must stay FAIL");
            foreach (string reason in new[] { "GpuUnavailable", "FileUnavailable", "InvalidEvidence", "FileChanged" })
                LogSeverityCheck(GameMode.SeverityTagFor(reason) == Logger.WarnTag,
                    reason + " could not obtain evidence and belongs in WARN");
            foreach (string reason in new[] { "Started", "Recorded", "PidMissing", "BelowThreshold",
                "IdentityUnavailable", "ForegroundChanged", "ForegroundWaiting", "SamplingBusy",
                "Canceled", "Closed", "ActiveChanged", "ProfileChanged" })
                LogSeverityCheck(GameMode.SeverityTagFor(reason) == Logger.InfoTag,
                    reason + " is normal timing or a valid conclusion, not a fault");
            // The record-success line is no longer abnormal end to end, this is the line the user sees
            string body;
            LogSeverityCheck(LogStreamView.ClassifyLine(
                GameMode.SeverityTagFor("Recorded") + "已记录 GPU 3D 活动 lastFailure=none", out body)
                == LogEventSeverity.Info, "a recorded observation must not land in the error tab");
        }

        // The copy always carries failed N items, with failed 0 items the word list still judges it abnormal, so this line must declare its own level too
        private static void LogSeverityCountedFailureLineDeclaresItsLevel()
        {
            string body;
            // The original sample was the Extreme tier startup patch line, that tier has been retired
            //   What's guarded is the general ClassifyLine property that lines containing failed must declare their own level, kept with an inline sample
            string clean = "启动补写环境项 翻动 2 项 失败 0 项";
            LogSeverityCheck(LogStreamView.ClassifyLine(clean, out body) == LogEventSeverity.Error,
                "without a tag the word 失败 makes even a zero-failure line an error");
            LogSeverityCheck(LogStreamView.ClassifyLine(Logger.InfoTag + clean, out body) == LogEventSeverity.Info,
                "a zero-failure reconcile line must not land in the error tab");
            string withFailures = "启动补写环境项 翻动 0 项 失败 1 项 失败项 HAGS";
            LogSeverityCheck(LogStreamView.ClassifyLine(Logger.WarnTag + withFailures, out body) == LogEventSeverity.Warning,
                "a real failure still shows up, as a warning rather than a fault");
            LogSeverityCheck(body.Contains("HAGS"), "the failed item name survives into the displayed text");
        }

        private static void LogSeverityTagIsStrippedFromBody()
        {
            string body;
            LogStreamView.ClassifyLine(Logger.WarnTag + "IRQ 模块加载失败", out body);
            LogSeverityCheck(body == "IRQ 模块加载失败", "the WARN tag is not part of the displayed text");
            LogStreamView.ClassifyLine(Logger.FailTag + "  设置写入失败", out body);
            LogSeverityCheck(body == "设置写入失败", "the FAIL tag and the spacing after it are removed");
            LogStreamView.ClassifyLine("WARNING light", out body);
            LogSeverityCheck(body == "WARNING light", "a word that merely starts with the tag letters is left alone");
        }

        private static void LogSeverityBoostSeparatesPlacementFromPriorities()
        {
            int oldLang = Lang.Cur;
            try
            {
                for (int language = 0; language < 3; language++)
                {
                    Lang.Cur = language;
                    string body;
                    const string placement = " [selected CPUs 2,3]";
                    string priority = Lang.T("log.gamemodeboost.28");
                    string otherStates = Lang.T("log.gamemodeboost.30") + Lang.T("log.gamemodeboost.31");
                    string success = GameMode.BoostSuccessLine("UAGame", 37520, priority, placement, false, otherStates);
                    LogSeverityCheck(LogStreamView.ClassifyLine(success, out body) == LogEventSeverity.Success,
                        "verified priorities stay successful when placement is unconfirmed, language=" + language);
                    LogSeverityCheck(body.StartsWith(Lang.T("log.gamemodeboost.27"), StringComparison.Ordinal)
                        && body.Contains("UAGame(pid 37520)") && body.Contains(priority) && body.Contains(otherStates),
                        "the summary names the verified priorities and process");
                    LogSeverityCheck(!body.Contains(placement), "unconfirmed CPU selection is not presented as applied");
                    success = GameMode.BoostSuccessLine("error-game", 37520, priority, placement, true, otherStates);
                    LogSeverityCheck(LogStreamView.ClassifyLine(success, out body) == LogEventSeverity.Success && body.Contains(placement),
                        "confirmed placement survives and a game name cannot turn success into failure");
                    LogSeverityCheck(!body.StartsWith(Logger.SuccessTag, StringComparison.Ordinal), "the PASS tag is hidden");

                    foreach (bool unreadable in new[] { false, true })
                        foreach (bool gaveUp in new[] { false, true })
                        {
                            string warning = GameMode.PlacementWarningLine("error-game", 37520, true, unreadable, gaveUp);
                            LogSeverityCheck(LogStreamView.ClassifyLine(warning, out body) == LogEventSeverity.Warning,
                                "an unconfirmed placement is WARN even after retries stop or the process name contains error");
                            LogSeverityCheck(body.StartsWith(Lang.T("schedule.placement.unconfirmed"), StringComparison.Ordinal)
                                && body.Contains("error-game pid 37520"), "the warning identifies manual placement and its process");
                            LogSeverityCheck(body.Contains(Lang.T(unreadable ? "log.placement.unreadable" : "log.placement.unconfirmed")),
                                "unreadable placement is described as unknown, not a confirmed failure");
                            LogSeverityCheck(body.EndsWith(Lang.T(gaveUp ? "log.placement.stopped" : "log.gamemodeboost.24"), StringComparison.Ordinal),
                                "the warning accurately distinguishes another retry from stopping");
                        }
                }
            }
            finally { Lang.Cur = oldLang; }
        }

        private static void LogSeverityLegacyBoostSummaryIsAWarning()
        {
            string body;
            const string screenshot = "游戏提优已生效 UAGame(pid 37520) 高优先级；手动核心分配未确认，请查看落核失败日志 高读写优先级 显卡高优先级 已退出省电模式";
            foreach (string line in new[]
            {
                screenshot,
                "Game boost in effect UAGame(pid 37520) high priority; manual core placement unconfirmed; see placement failure log high I/O priority",
                "游戏提优已生效 UAGame(pid 37520) 高优先级；手動コア割り当て未確認。失敗ログを確認してください"
            })
            {
                LogSeverityCheck(LogStreamView.ClassifyLine(line, out body) == LogEventSeverity.Warning,
                    "the old mixed summary belongs in warnings, not errors");
                LogSeverityCheck(body.Contains("UAGame(pid 37520)") && body != line,
                    "legacy display clarifies the summary while preserving the process identity");
                LogSeverityCheck(LogStreamView.ClassifyLine(Logger.FailTag + line, out body) == LogEventSeverity.Error,
                    "an explicit failure tag must never be downgraded by legacy compatibility");
            }
            LogSeverityCheck(LogStreamView.ClassifyLine(screenshot + " 设置还原失败", out body) == LogEventSeverity.Error,
                "a separate real error is not hidden by legacy normalization");
            LogSeverityCheck(LogStreamView.ClassifyLine("游戏提优失败 UAGame(pid 37520) 手动核心分配未确认，请查看落核失败日志", out body)
                == LogEventSeverity.Error, "compatibility does not suppress other boost failures");
            LogSeverityCheck(LogStreamView.ClassifyLine("PASSENGER error", out body) == LogEventSeverity.Error,
                "the success marker requires a complete tag, not a prefix match");
        }
    }
}
#endif
