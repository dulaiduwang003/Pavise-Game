// @author bdth 2074055628@qq.com
// 文件用途 创建和移除登录启动计划任务

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class TaskHelper
    {
        public const string TaskName = "Aegis";

        private static int cachedExists = -1;

        public static bool TaskExists()
        {
            bool ok = Run("/Query /TN " + TaskName) == 0;
            cachedExists = ok ? 1 : 0;
            return ok;
        }

        public static bool TaskExistsCached()
        {
            return cachedExists < 0 ? TaskExists() : cachedExists == 1;
        }

        public static int CreateStartupTask()
        {
            int rc = Run("/Create /F /SC ONLOGON /RL HIGHEST /TN " + TaskName + " /TR \"\\\"" + Application.ExecutablePath + "\\\"\"");
            if (rc == 0)
            {
                cachedExists = 1;
                Settings.SaveStr("AutostartExe", Application.ExecutablePath);
            }
            return rc;
        }

        public static int DeleteStartupTask()
        {
            int rc = Run("/Delete /F /TN " + TaskName);
            if (rc == 0)
            {
                cachedExists = 0;
                Settings.SaveStr("AutostartExe", "");
            }
            return rc;
        }

        public static void RefreshStartupTask()
        {
            try
            {
                string cur = Application.ExecutablePath;
                if (string.Equals(Settings.LoadStr("AutostartExe", ""), cur, StringComparison.OrdinalIgnoreCase)) return;
                if (!TaskExists()) return;
                string target = TaskCommand();
                if (target != null && string.Equals(target, cur, StringComparison.OrdinalIgnoreCase))
                {
                    Settings.SaveStr("AutostartExe", cur);
                    return;
                }
                if (target != null && File.Exists(target)) return;
                Logger.Log("开机自启任务指向的程序已不存在，重建为当前路径：" + cur);
                CreateStartupTask();
            }
            catch { }
        }

        private static string TaskCommand()
        {
            string outp = RunRead("/Query /TN " + TaskName + " /XML");
            if (outp == null) return null;
            int a = outp.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
            int b = outp.IndexOf("</Command>", StringComparison.OrdinalIgnoreCase);
            if (a < 0 || b < 0 || b <= a) return null;
            string cmd = outp.Substring(a + 9, b - a - 9).Trim().Trim('"');
            return cmd.Length == 0 ? null : cmd;
        }

        public static int Run(string arguments)
        {
            string ignored;
            return RunCore(arguments, false, out ignored);
        }

        private static string RunRead(string arguments)
        {
            string outp;
            return RunCore(arguments, true, out outp) == 0 ? outp : null;
        }

        private static int RunCore(string arguments, bool capture, out string stdout)
        {
            stdout = null;
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = capture;
                using (var p = Process.Start(psi))
                {
                    if (capture) stdout = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        return -1;
                    }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }
    }



}
