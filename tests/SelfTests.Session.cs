// @author bdth 2074055628@qq.com
// 文件用途 校验单局压制统计在宽限期先行还原后仍然结账

using System;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestSessionReportCountsSealedProcesses()
        {
            string data = Path.Combine(Path.GetTempPath(),
                "PaviseSession_" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(data);
            string previousLog = Logger.LogPath;
            try
            {
                Lang.Init();
                string logPath = Path.Combine(data, "session.log");
                Logger.LogPath = logPath;
                var mode = new GameMode(data, new SuppressionCore());
                int self = Process.GetCurrentProcess().Id;

                File.WriteAllText(logPath, "");
                mode.ProbeSessionBegin("封账测试");
                mode.ProbeSessionTrack(self, "selftest");
                mode.ProbeSessionSeal(self);
                mode.ProbeSessionFinish();
                Eq(true, LastReportLine(logPath).Contains("压制 1 个后台进程"));

                File.WriteAllText(logPath, "");
                mode.ProbeSessionBegin("重复压制测试");
                mode.ProbeSessionTrack(self, "selftest");
                mode.ProbeSessionSeal(self);
                mode.ProbeSessionTrack(self, "selftest");
                mode.ProbeSessionFinish();
                Eq(true, LastReportLine(logPath).Contains("压制 1 个后台进程"));

                File.WriteAllText(logPath, "");
                mode.ProbeSessionBegin("销账测试");
                mode.ProbeSessionTrack(self, "selftest");
                mode.ProbeSessionUntrack(self);
                mode.ProbeSessionFinish();
                Eq(true, LastReportLine(logPath).Contains("压制 0 个后台进程"));
            }
            finally
            {
                Logger.LogPath = previousLog;
                try { Directory.Delete(data, true); } catch { }
            }
        }

        private static string LastReportLine(string logPath)
        {
            foreach (string line in File.ReadAllLines(logPath))
                if (line.Contains("本局结束：")) return line;
            throw new Exception("日志里没有出现本局结束汇总");
        }
    }
}
