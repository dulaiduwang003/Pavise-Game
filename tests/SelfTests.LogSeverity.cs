// Pure classification checks; no log file, no UI, no registry.
// 日志分级回归 只验证标记优先和词表兜底 不写日志文件 不建窗口
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
                LogSeverityTagIsStrippedFromBody
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
    }
}
#endif
