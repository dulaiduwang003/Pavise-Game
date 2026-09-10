// 文件用途 纯分类检查 不写日志文件 不碰 UI 和注册表
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
                LogSeverityTagIsStrippedFromBody,
                LogSeverityInfoTagBeatsFieldNames,
                LogSeverityRendererObservationDeclaresItsOwnLevel,
                LogSeverityCountedFailureLineDeclaresItsLevel
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

        // 结构化诊断行固定带 lastFailure= code= 这类字段名 词表按子串匹配会把键名当结论
        //   渲染观测原来每条都被判成异常 成功的那条也进异常页 还让计数加一
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

        // 渲染观测按结果自己声明级别 真故障才是 FAIL 采不到证据算环境限制 其余是正常结论
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
            // 记录成功那条端到端不再是异常 这就是用户看到的那一行
            string body;
            LogSeverityCheck(LogStreamView.ClassifyLine(
                GameMode.SeverityTagFor("Recorded") + "已记录 GPU 3D 活动 lastFailure=none", out body)
                == LogEventSeverity.Info, "a recorded observation must not land in the error tab");
        }

        // 文案里固定带"失败 N 项" 失败 0 项时词表照样判异常 所以这行也得自己声明级别
        private static void LogSeverityCountedFailureLineDeclaresItsLevel()
        {
            string body;
            // 原样本是极限档的启动补写行 那一档已下架
            //   守的是 ClassifyLine 的通性 带"失败"字样的行必须自己声明级别 换成内联样本继续守
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
    }
}
#endif
