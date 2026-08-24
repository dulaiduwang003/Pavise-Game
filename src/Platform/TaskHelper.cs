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

namespace PaviseApp
{
    internal static class TaskHelper
    {
        public const string TaskName = "Pavise";

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

        public const string AutostartArgument = "--autostart";

        // schtasks 的失败原因只在 stderr 里 退出码本身分辨不出是权限还是 XML 读不到
        [ThreadStatic] private static string lastSchtasksError;

        public static string LastSchtasksError { get { return lastSchtasksError; } }

        // ONLOGON 兜底建出来的任务缺电池与运行时限设置 补不上就退避 别每次开机白重建一遍
        private const string RepairFailKey = "AutostartRepairFails";
        private const int RepairGiveUp = 3;

        public static int CreateStartupTask()
        {
            int rc = CreateStartupTaskFromXml();
            if (rc != 0)
                rc = Run("/Create /F /SC ONLOGON /RL HIGHEST /TN " + TaskName
                    + " /TR \"\\\"" + Application.ExecutablePath + "\\\" " + AutostartArgument + "\"");
            if (rc == 0)
            {
                cachedExists = 1;
                Settings.SaveStr("AutostartExe", Application.ExecutablePath);
                // 手动开关过就给补齐重新来一次机会
                Settings.SaveStr(RepairFailKey, "0");
            }
            return rc;
        }

        private static int CreateStartupTaskFromXml()
        {
            string path = null;
            try
            {
                string xml = BuildStartupTaskXml(Application.ExecutablePath);
                if (xml == null) { lastSchtasksError = Lang.T("t.taskhelper.14"); return -1; }
                string dir = string.IsNullOrEmpty(Paths.Data) ? Path.GetTempPath() : Paths.Data;
                path = Path.Combine(dir, "Pavise_" + Guid.NewGuid().ToString("N") + ".xml");
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    byte[] bom = System.Text.Encoding.Unicode.GetPreamble();
                    byte[] body = System.Text.Encoding.Unicode.GetBytes(xml);
                    fs.Write(bom, 0, bom.Length);
                    fs.Write(body, 0, body.Length);
                    fs.Flush();
                }
                // 必须先放掉自己的写句柄 schtasks 读 XML 时不共享 攥着句柄调用它只会拿到 rc=1
                return Run("/Create /F /TN " + TaskName + " /XML \"" + path + "\"");
            }
            catch (Exception ex) { lastSchtasksError = ex.Message; return -1; }
            finally { try { if (path != null) File.Delete(path); } catch { } }
        }

        private static string BuildStartupTaskXml(string exePath)
        {
            string user;
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    user = id.User.Value;
            }
            catch { user = null; }
            if (string.IsNullOrEmpty(user))
            {
                user = Environment.UserDomainName + "\\" + Environment.UserName;
                if (user.Length <= 1) return null;
            }
            string cmd = XmlText(exePath);
            string start = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
                + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
                + "  <RegistrationInfo><URI>\\" + TaskName + "</URI></RegistrationInfo>\r\n"
                + "  <Principals><Principal id=\"Author\"><UserId>" + XmlText(user) + "</UserId>"
                + "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel>"
                + "</Principal></Principals>\r\n"
                + "  <Settings>"
                + "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
                + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"
                + "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"
                + "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>"
                + "</Settings>\r\n"
                + "  <Triggers><LogonTrigger><StartBoundary>" + start + "</StartBoundary></LogonTrigger></Triggers>\r\n"
                + "  <Actions Context=\"Author\"><Exec><Command>" + cmd + "</Command>"
                + "<Arguments>" + AutostartArgument + "</Arguments></Exec></Actions>\r\n"
                + "</Task>";
        }

        private static string XmlText(string value)
        {
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        internal static bool IsVolatileAutostartPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string low = path.Replace('/', '\\').ToLowerInvariant();
            return low.Contains(@"\temp\") || low.Contains(@"\tmp\")
                || low.Contains(@"\xwechat_files\") || low.Contains(@"\wechat files\")
                || low.Contains(@"\inetcache\") || low.Contains(@"\$recycle.bin\");
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
                if (!TaskExists()) return;
                string xml;
                if (RunCore("/Query /TN " + TaskName + " /XML", true, out xml) != 0)
                {
                    Logger.Log(Lang.T("log.taskhelper.1"));
                    return;
                }
                string target = ParseTaskCommandXml(xml);
                if (string.IsNullOrWhiteSpace(target))
                {
                    Logger.Log(Lang.T("log.taskhelper.1"));
                    return;
                }
                string taskArguments = ParseTaskArgumentsXml(xml);
                bool argumentsStale = taskArguments != null
                    && taskArguments.IndexOf(
                        AutostartArgument, StringComparison.OrdinalIgnoreCase) < 0;
                bool settingsStale = StartupTaskSettingsStale(xml);
                bool pathChanged = NeedsStartupTaskRefresh(cur, target);
                if (!pathChanged && !argumentsStale && !settingsStale)
                {
                    Settings.SaveStr("AutostartExe", cur);
                    Settings.SaveStr(RepairFailKey, "0");
                    return;
                }
                if (IsVolatileAutostartPath(cur))
                {
                    Logger.Log(Lang.T("log.taskhelper.2") + cur
                        + Lang.T("log.taskhelper.3"));
                    return;
                }
                if (pathChanged || argumentsStale)
                {
                    Logger.Log(pathChanged
                        ? Lang.T("t.taskhelper.4") + (target ?? Lang.T("t.taskhelper.5")) + " " + cur
                        : Lang.T("t.taskhelper.6") + AutostartArgument + Lang.T("t.taskhelper.7"));
                    int rc = CreateStartupTask();
                    if (rc != 0)
                        Logger.Log((pathChanged ? Lang.T("log.taskhelper.8") : Lang.T("log.taskhelper.9"))
                            + rc + DescribeSchtasksError());
                    return;
                }
                RepairStartupTaskSettings(cur);
            }
            catch { }
        }

        // 只补电池与运行时限那三项 补不上就退避 别每次开机刷同一条错
        private static void RepairStartupTaskSettings(string cur)
        {
            int fails = RepairFailCount();
            if (fails >= RepairGiveUp) return;
            int rc = CreateStartupTaskFromXml();
            if (rc == 0)
            {
                cachedExists = 1;
                Settings.SaveStr("AutostartExe", cur);
                Settings.SaveStr(RepairFailKey, "0");
                Logger.Log(Lang.T("log.taskhelper.10"));
                return;
            }
            fails++;
            Settings.SaveStr(RepairFailKey,
                fails.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Logger.Log(fails >= RepairGiveUp
                ? Lang.T("log.taskhelper.11") + rc + DescribeSchtasksError() + Lang.T("t.taskhelper.12")
                : Lang.T("log.taskhelper.13") + rc + DescribeSchtasksError());
        }

        private static int RepairFailCount()
        {
            int value;
            return int.TryParse(Settings.LoadStr(RepairFailKey, "0"), out value) && value > 0 ? value : 0;
        }

        private static string DescribeSchtasksError()
        {
            string err = lastSchtasksError;
            return string.IsNullOrEmpty(err) ? "" : Lang.T("t.taskhelper.16") + err;
        }

        internal static bool StartupTaskSettingsStale(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return false;
            try
            {
                var document = new System.Xml.XmlDocument();
                document.XmlResolver = null;
                document.LoadXml(xml.TrimStart('﻿'));
                System.Xml.XmlNode settings = document.SelectSingleNode(
                    "/*[local-name()='Task']/*[local-name()='Settings']");
                return !SettingEquals(settings, "ExecutionTimeLimit", "PT0S")
                    || !SettingEquals(settings, "DisallowStartIfOnBatteries", "false")
                    || !SettingEquals(settings, "StopIfGoingOnBatteries", "false");
            }
            catch { return false; }
        }

        private static bool SettingEquals(System.Xml.XmlNode settings, string name, string expected)
        {
            // 整个 Settings 读不出来是解析异常 当作不需要动 别拿一次读失败去触发重建
            if (settings == null) return true;
            // 单项缺失是真的缺 任务计划导出时会省略取默认值的项 例如 ExecutionTimeLimit 默认 PT72H
            System.Xml.XmlNode node = settings.SelectSingleNode("*[local-name()='" + name + "']");
            if (node == null) return false;
            return string.Equals(
                (node.InnerText ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool NeedsStartupTaskRefresh(
            string currentExecutable, string taskExecutable)
        {
            if (string.IsNullOrWhiteSpace(currentExecutable)) return false;
            if (string.IsNullOrWhiteSpace(taskExecutable)) return false;
            try
            {
                string current = Path.GetFullPath(
                    currentExecutable.Trim().Trim('"'));
                string target = Path.GetFullPath(
                    taskExecutable.Trim().Trim('"'));
                return !string.Equals(
                    current, target, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return !string.Equals(
                    currentExecutable.Trim().Trim('"'),
                    taskExecutable.Trim().Trim('"'),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static bool TryReadTaskCommand(out string command)
        {
            command = null;
            string xml;
            if (RunCore(
                    "/Query /TN " + TaskName + " /XML",
                    true, out xml) != 0)
                return false;
            command = ParseTaskCommandXml(xml);
            return !string.IsNullOrWhiteSpace(command);
        }

        internal static string ParseTaskArgumentsXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var document = new System.Xml.XmlDocument();
                document.XmlResolver = null;
                document.LoadXml(xml.TrimStart('﻿'));
                System.Xml.XmlNode exec = document.SelectSingleNode(
                    "/*[local-name()='Task']"
                    + "/*[local-name()='Actions']"
                    + "/*[local-name()='Exec']");
                if (exec == null) return null;
                System.Xml.XmlNode node = exec.SelectSingleNode("*[local-name()='Arguments']");
                return node == null ? "" : (node.InnerText ?? "").Trim();
            }
            catch { return null; }
        }

        internal static string ParseTaskCommandXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var document = new System.Xml.XmlDocument();
                document.XmlResolver = null;
                document.LoadXml(xml.TrimStart('\uFEFF'));
                System.Xml.XmlNode command = document.SelectSingleNode(
                    "/*[local-name()='Task']"
                    + "/*[local-name()='Actions']"
                    + "/*[local-name()='Exec']"
                    + "/*[local-name()='Command']");
                if (command == null) return null;
                string value = (command.InnerText ?? "").Trim().Trim('"');
                return value.Length == 0 ? null : value;
            }
            catch { return null; }
        }

        public static int Run(string arguments)
        {
            string ignored;
            return RunCore(arguments, false, out ignored);
        }

        private static int RunCore(string arguments, bool capture, out string stdout)
        {
            stdout = null;
            lastSchtasksError = null;
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = capture;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    var buf = new System.Text.StringBuilder();
                    var err = new System.Text.StringBuilder();
                    if (capture)
                    {
                        p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                        { if (e.Data != null) lock (buf) buf.AppendLine(e.Data); };
                        p.BeginOutputReadLine();
                    }
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        lastSchtasksError = Lang.T("t.taskhelper.15");
                        return -1;
                    }
                    p.WaitForExit();
                    if (capture) lock (buf) stdout = buf.ToString();
                    lock (err) lastSchtasksError = CondenseError(err.ToString());
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { lastSchtasksError = ex.Message; return -1; }
        }

        private static string CondenseError(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string[] lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var parts = new List<string>();
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) parts.Add(trimmed);
            }
            if (parts.Count == 0) return null;
            string joined = string.Join(" ", parts.ToArray());
            return joined.Length > 240 ? joined.Substring(0, 240) : joined;
        }
    }

}
