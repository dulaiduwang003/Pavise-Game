using System;
using System.IO;
using System.Linq;

namespace PaviseApp
{
    internal static class ChineseLogChecks
    {
        private static int checks;

        private static void Check(bool condition, string reason)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(reason);
        }

        private static void RoundTrip(string source)
        {
            string record = LogText.Record(source);
            Check(LogText.Canonical(record) == source, "中文记录往返改变了诊断字段：" + source + " -> " + record + " -> " + LogText.Canonical(record));
        }

        private static void CheckRecords()
        {
            foreach (int language in new[] { 0, 1, 2 })
            {
                Lang.Cur = language;
                Check(Lang.T("auto.log.started") == Lang.T("auto.log.started", 0), "日志资源受界面语言影响");
                RoundTrip("INFO SESSION [session-a] start | THREAD helper.exe 线程 游戏");
                RoundTrip("INFO SESSION [session-a] end | 游戏 原名");
                RoundTrip("INFO SESSION [session-a] start | 结果=applied 状态=stopped");
                Check(LogText.Record("INFO SESSION [session-a] start | 测试").Contains("] 开始 |"), "会话开始状态未中文化");
                Check(LogText.Record("INFO SESSION [session-a] end | 测试").Contains("] 结束 |"), "会话结束状态未中文化");
                RoundTrip("INFO AUTO [session-a] GmBoost | applied");
                foreach (string state in new[] { "started", "restored", "restoring", "stopped", "ready", "pending", "failed", "unverified", "configured", "fso-configured", "pending-reboot", "restore-pending-reboot", "timer-requested", "timer-locked", "timer-limited", "timer-unverified", "unsupported", "unchanged", "canceled", "skipped", "waiting", "exhausted" })
                    RoundTrip("INFO AUTO [session-a] TimerTick | " + state);
                RoundTrip("INFO THREAD [session-a] result=stopped");
                RoundTrip("WARN AUTO [session-a] GmBoost | restore-failed");
                RoundTrip("PASS THREAD exclusive verified: THREAD helper.exe pid=42 physical=0x3 workers=1 workerMask=0xC");
                RoundTrip("INFO GAME placement verified pid=42 mask=0x3 name=线程 THREAD helper.exe");
                RoundTrip("PASS SUPPRESSION success action=background-applied count=2 targets=THREAD helper.exe(pid=42,birth=123);线程.exe(pid=43,birth=124)");
                RoundTrip("INFO STARTUP path=C:\\游戏目录\\THREAD helper.exe");
                RoundTrip("INFO STARTUP path=C:\\游戏目录\\state=applied");

                string session = "2026-11-09 22:00:00  " + LogText.Record("INFO SESSION [session-a] start | 测试游戏") + "\n"
                    + "2026-11-09 22:00:01  INFO AUTO [session-a] GmBoost | applied\n"
                    + "2026-11-09 22:00:02  " + LogText.Record("WARN AUTO [session-a] GmBoost | restore-failed") + "\n"
                    + "2026-11-09 22:00:03  " + LogText.Record("INFO SESSION [session-a] end | 测试游戏");
                var entries = LogStreamView.ParseEvents(session);
                Check(entries.Count == 4, "新旧日志混合解析丢失记录");
                Check(entries.All(entry => entry.SessionToken == "session-a"), "会话归属丢失");
                int warnings, errors;
                LogStreamView.CountIssues(session, out warnings, out errors);
                Check(warnings == 1 && errors == 0, "中文告警级别改变");
                var actions = ActionLogProjection.Project(entries, 4, "session-a");
                Check(actions.Count >= 4, "动作视图丢失会话动作");
                Check(actions.All(entry => entry.Module != "SESSION" && entry.Module != "FEATURE"), "动作模块仍显示英文");
                Check(actions.Any(entry => entry.Message.Contains("恢复未完成")), "恢复失败被误报为成功");
                Check(ActionLogProjection.Export(session, "session-a").Contains("Pavise 会话动作记录"), "动作导出没有使用中文");

                string name = "THREAD helper.exe";
                string raw = "2026-11-09 22:01:00  信息 线程整核隔离已核验：" + name + " 进程编号=42 物理核掩码=0x3";
                var parsed = LogStreamView.Parse(raw);
                Check(parsed.Raw == raw && parsed.Message.Contains(name), "诊断展示改写了进程名称");
                Check(LogText.Record("信息 路径=C:\\游戏目录\\THREAD helper.exe").Contains("C:\\游戏目录\\THREAD helper.exe"), "日志写入改写了路径");
            }
        }

        private static void CheckWriter()
        {
            Logger.LogPath = Path.Combine(Environment.CurrentDirectory, "chinese-test.log");
            Logger.WritesEnabled = true;
            Logger.Info("日志中文检查");
            Logger.Warn("测试告警");
            Logger.Error("测试失败");
            Logger.RuntimeIdentity();
            Logger.RepeatedError("测试来源", "测试重复失败", 100);
            Logger.RepeatedError("测试来源", "测试重复失败", 101);
            Logger.RepeatedRecovered("测试来源");
            string text = File.ReadAllText(Logger.LogPath);
            Check(text.Contains("信息 日志中文检查"), "信息标签未中文化");
            Check(text.Contains("警告 测试告警") && text.Contains("失败 测试失败"), "告警或失败标签丢失");
            Check(text.Contains("运行时 身份 版本=" + App.Version) && text.Contains("构建标识="), "构建身份信息丢失");
            Check(text.Contains("重复次数=1") && text.Contains("已恢复"), "节流或恢复说明丢失");
            long length = new FileInfo(Logger.LogPath).Length;
            Logger.WritesEnabled = false;
            Logger.Info("关闭日志后不应写入");
            Check(new FileInfo(Logger.LogPath).Length == length, "日志关闭后仍写入");
            Logger.WritesEnabled = true;
        }

        public static int Main()
        {
            try
            {
                CheckRecords();
                CheckWriter();
                Console.WriteLine("通过：中文日志、新旧格式兼容、会话归属、级别与原始数据保留，共 " + checks + " 项检查；未启动产品、未访问真实设置。");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }
    }
}
