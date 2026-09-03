// @author bdth 2074055628@qq.com
// 文件用途 卸载脚本调用的无界面模式 用程序自身的还原代码把系统改动全部按收据还原 再删数据与设置
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static class UninstallMode
    {
        public static bool Run(string resultPath)
        {
            var report = new StringBuilder();
            bool ok = false;
            try
            {
                Paths.Init();
                Lang.Init();
                StopOtherInstances();
                int files;
                string unrestored;
                ok = LegacyPurge.WipeAll(Paths.Data, true, "卸载脚本", out files, out unrestored);
                report.Append("cleared=").Append(ok ? "1" : "0").Append("\r\n");
                report.Append("files=").Append(files).Append("\r\n");
                if (!string.IsNullOrEmpty(unrestored)) report.Append("unrestored=").Append(unrestored).Append("\r\n");
                int taskRc = TaskHelper.DeleteStartupTask();
                report.Append("task=").Append(taskRc).Append("\r\n");
            }
            catch (Exception ex)
            {
                report.Append("cleared=0\r\nerror=").Append(ex.GetType().Name).Append(' ').Append(ex.Message).Append("\r\n");
                ok = false;
            }
            try
            {
                if (!string.IsNullOrEmpty(resultPath))
                    File.WriteAllText(resultPath, report.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return ok;
        }

        // 脚本已经先发过退出信号 这里再兜一次 还原持久项时不能有别的实例在写收据
        private static void StopOtherInstances()
        {
            try { using (var exit = EventWaitHandle.OpenExisting("Global\\Pavise_Exit")) exit.Set(); }
            catch { }
            int self = Process.GetCurrentProcess().Id;
            for (int i = 0; i < 16; i++)
            {
                if (!OthersAlive(self)) return;
                Thread.Sleep(500);
            }
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != self && p.ProcessName.StartsWith("Pavise", StringComparison.OrdinalIgnoreCase))
                    { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        private static bool OthersAlive(int self)
        {
            bool alive = false;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != self && p.ProcessName.StartsWith("Pavise", StringComparison.OrdinalIgnoreCase)
                        && !p.ProcessName.EndsWith("selftest", StringComparison.OrdinalIgnoreCase)) alive = true;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return alive;
        }
    }
}
