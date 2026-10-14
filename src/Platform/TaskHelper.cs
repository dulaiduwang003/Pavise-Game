// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class TaskHelper
    {
        public const string TaskName = "PaviseAuto";

        private static int cachedExists = -1;
        private static volatile int cachedEnabled = -1;
        private static readonly object startupGate = new object();
#if PAVISE_SELFTEST
        internal delegate int StartupRunForTest(string arguments, bool capture, out string stdout);
        internal static StartupRunForTest RunForTest;
#endif

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

        public static bool IsStartupTaskEnabled()
        {
            lock (startupGate)
            {
                // 任务读写串行，避免旧查询覆盖新状态。
                string xml;
                bool exists = RunCore("/Query /TN " + TaskName + " /XML", true, out xml) == 0;
                cachedExists = exists ? 1 : 0;
                bool enabled = exists && StartupTaskEnabled(xml);
                cachedEnabled = enabled ? 1 : 0;
                return enabled;
            }
        }

        public static bool IsStartupTaskEnabledCached()
        {
            return cachedEnabled < 0 ? IsStartupTaskEnabled() : cachedEnabled == 1;
        }

        public const string AutostartArgument = "--autostart";
        internal const string AutostartDataArgument = "--autostart-data";

        [ThreadStatic] private static string lastSchtasksError;

        public static string LastSchtasksError { get { return lastSchtasksError; } }

        // 修复失败后冷却，避免每次启动重复创建任务。
        private const string RepairFailKey = "AutostartRepairFails";
        private const int RepairGiveUp = 3;

        public static int CreateStartupTask()
        {
            lock (startupGate)
            {
                try
                {
                    string arguments = BuildStartupArguments(Paths.Data);
                    string executable = StartupPayload.Ensure(Application.ExecutablePath);
                    return RegisterStartupTask(executable, arguments, true);
                }
                catch (Exception ex) { lastSchtasksError = ex.Message; return -1; }
            }
        }

        private static int RegisterStartupTask(string executable, string arguments, bool allowFallback)
        {
            int rc = CreateStartupTaskFromXml(executable, arguments);
            if (rc != 0 && allowFallback)
                rc = Run("/Create /F /SC ONLOGON /RL HIGHEST /TN " + TaskName
                    + " /TR " + QuoteCommandArgument(BuildStartupCommandLine(executable, arguments)));
            if (rc == 0)
            {
                cachedExists = 1;
                cachedEnabled = 1;
                Settings.SaveStr("AutostartExe", executable);
                Settings.SaveStr(RepairFailKey, "0");
            }
            return rc;
        }

        private static int CreateStartupTaskFromXml(string executable, string arguments)
        {
            string path = null;
            try
            {
                string xml = BuildStartupTaskXml(executable, arguments);
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
                // 调用 schtasks 前关闭 XML 写入句柄。
                return Run("/Create /F /TN " + TaskName + " /XML \"" + path + "\"");
            }
            catch (Exception ex) { lastSchtasksError = ex.Message; return -1; }
            finally { try { if (path != null) File.Delete(path); } catch { } }
        }

        internal static string BuildStartupTaskXml(string exePath)
        { return BuildStartupTaskXml(exePath, BuildStartupArguments(Paths.Data)); }

        internal static string BuildStartupTaskXml(string exePath, string arguments)
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
                + "<Arguments>" + XmlText(arguments) + "</Arguments>"
                + "<WorkingDirectory>" + XmlText(Path.GetDirectoryName(exePath)) + "</WorkingDirectory></Exec></Actions>\r\n"
                + "</Task>";
        }

        private static string XmlText(string value)
        {
            return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        internal static string BuildStartupArguments(string dataPath)
        {
            if (!Paths.IsAbsoluteStartupDataPath(dataPath)) throw new ArgumentException("An absolute startup data directory is required");
            return AutostartArgument + " " + AutostartDataArgument + " " + QuoteCommandArgument(Path.GetFullPath(dataPath));
        }

        internal static string BuildStartupCommandLine(string executable, string arguments)
        { return QuoteCommandArgument(executable) + " " + arguments; }

        internal static string QuoteCommandArgument(string value)
        {
            var quoted = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value ?? "")
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { quoted.Append('\\', slashes * 2 + 1); quoted.Append(c); slashes = 0; continue; }
                quoted.Append('\\', slashes); slashes = 0; quoted.Append(c);
            }
            quoted.Append('\\', slashes * 2); quoted.Append('"');
            return quoted.ToString();
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
            lock (startupGate)
            {
                int rc = Run("/Delete /F /TN " + TaskName);
                if (rc == 0)
                {
                    cachedExists = 0;
                    cachedEnabled = 0;
                    Settings.SaveStr("AutostartExe", "");
                }
                return rc;
            }
        }

        internal static bool RemoveStartupTaskForUninstall()
        {
            lastSchtasksError = null;
            lock (startupGate)
            {
                bool removed = RemoveStartupTaskForUninstall(ReadStartupTaskPresence, DeleteStartupTask);
                if (removed)
                    try { StartupPayload.Cleanup(StartupPayload.ManagedRoot, Application.ExecutablePath); } catch { }
                return removed;
            }
        }

        internal static bool RemoveStartupTaskForUninstall(Func<bool?> readPresence, Func<int> remove)
        {
            try
            {
                bool? exists = readPresence();
                if (!exists.HasValue) return false;
                if (exists.Value)
                {
                    remove();
                    exists = readPresence();
                    if (!exists.HasValue || exists.Value) return false;
                }
                cachedExists = 0;
                cachedEnabled = 0;
                return true;
            }
            catch { return false; }
        }

        // 任务不可读不等于不存在，不能据此判定卸载成功。
        private static bool? ReadStartupTaskPresence()
        {
            object service = null, folder = null, task = null;
            try
            {
                Type type = Type.GetTypeFromProgID("Schedule.Service", true);
                service = Activator.CreateInstance(type);
                type.InvokeMember("Connect", BindingFlags.InvokeMethod, null, service,
                    new object[] { Type.Missing, Type.Missing, Type.Missing, Type.Missing });
                folder = type.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, service,
                    new object[] { "\\" });
                try
                {
                    task = folder.GetType().InvokeMember("GetTask", BindingFlags.InvokeMethod,
                        null, folder, new object[] { TaskName });
                    return task == null ? (bool?)null : true;
                }
                catch (Exception error)
                {
                    while (error is TargetInvocationException && error.InnerException != null)
                        error = error.InnerException;
                    if (Marshal.GetHRForException(error) == unchecked((int)0x80070002)) return false;
                    lastSchtasksError = error.Message;
                    return null;
                }
            }
            catch (Exception error) { lastSchtasksError = error.Message; return null; }
            finally
            {
                foreach (object value in new[] { task, folder, service })
                    if (value != null && Marshal.IsComObject(value))
                        try { Marshal.ReleaseComObject(value); } catch { }
            }
        }

        public static void RefreshStartupTask()
        {
            lock (startupGate)
            try
            {
                string cur = Application.ExecutablePath;
                if (!TaskExists()) return;
                string xml;
                if (RunCore("/Query /TN " + TaskName + " /XML", true, out xml) != 0)
                {
                    Logger.Warn(Lang.T("log.taskhelper.1"));
                    return;
                }
                // 保留外部禁用的任务状态。
                bool enabled = StartupTaskEnabled(xml);
                cachedEnabled = enabled ? 1 : 0;
                if (!enabled) return;
                string target = ParseTaskCommandXml(xml);
                if (string.IsNullOrWhiteSpace(target))
                {
                    Logger.Warn(Lang.T("log.taskhelper.1"));
                    return;
                }
                string taskArguments = ParseTaskArgumentsXml(xml);
                // 临时下载不替换仍有效的旧安装；显式启用时发布托管副本。
                if (ShouldPreserveLegacyTarget(cur, target, StartupPayload.ManagedRoot))
                {
                    Logger.Log(Lang.T("log.taskhelper.2") + cur
                        + Lang.T("log.taskhelper.3"));
                    return;
                }
                // 任务指向哈希副本，新版本需同步目标路径。
                string arguments = BuildStartupArguments(Paths.Data);
                string executable = StartupPayload.Ensure(cur);
                bool argumentsStale = !string.Equals(taskArguments, arguments, StringComparison.OrdinalIgnoreCase);
                bool settingsStale = StartupTaskSettingsStale(xml);
                bool pathChanged = NeedsStartupTaskRefresh(executable, target);
                if (!pathChanged && !argumentsStale && !settingsStale)
                {
                    Settings.SaveStr("AutostartExe", executable);
                    Settings.SaveStr(RepairFailKey, "0");
                    return;
                }
                if (pathChanged || argumentsStale)
                {
                    Logger.Log(pathChanged
                        ? Lang.T("t.taskhelper.4") + (target ?? Lang.T("t.taskhelper.5")) + " " + executable
                        : Lang.T("t.taskhelper.6") + arguments + Lang.T("t.taskhelper.7"));
                    int rc = RegisterStartupTask(executable, arguments, true);
                    if (rc != 0)
                        Logger.Warn((pathChanged ? Lang.T("log.taskhelper.8") : Lang.T("log.taskhelper.9"))
                            + rc + DescribeSchtasksError());
                    return;
                }
                RepairStartupTaskSettings(executable, arguments);
            }
            catch (Exception ex)
            {
                lastSchtasksError = ex.Message;
                Logger.Warn(Lang.T("log.taskhelper.8") + "-1" + DescribeSchtasksError());
            }
        }

        internal static bool ShouldPreserveLegacyTarget(string currentExecutable, string target, string managedRoot)
        { return ShouldPreserveLegacyTarget(currentExecutable, target, managedRoot, File.Exists(target)); }

        internal static bool ShouldPreserveLegacyTarget(string currentExecutable, string target, string managedRoot, bool targetExists)
        {
            return IsVolatileAutostartPath(currentExecutable) && !IsVolatileAutostartPath(target)
                && !StartupPayload.IsManagedExecutable(target, managedRoot)
                && NeedsStartupTaskRefresh(currentExecutable, target) && targetExists;
        }

        private static void RepairStartupTaskSettings(string executable, string arguments)
        {
            int fails = RepairFailCount();
            if (fails >= RepairGiveUp) return;
            int rc = CreateStartupTaskFromXml(executable, arguments);
            if (rc == 0)
            {
                cachedExists = 1;
                cachedEnabled = 1;
                Settings.SaveStr("AutostartExe", executable);
                Settings.SaveStr(RepairFailKey, "0");
                Logger.Log(Lang.T("log.taskhelper.10"));
                return;
            }
            fails++;
            Settings.SaveStr(RepairFailKey,
                fails.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Logger.Warn(fails >= RepairGiveUp
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

        internal static bool StartupTaskEnabled(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return false;
            try
            {
                var document = new System.Xml.XmlDocument();
                document.XmlResolver = null;
                document.LoadXml(xml.TrimStart('\uFEFF'));
                System.Xml.XmlNode node = document.SelectSingleNode(
                    "/*[local-name()='Task']/*[local-name()='Settings']/*[local-name()='Enabled']");
                if (node == null) return true;
                return string.Equals(node.InnerText.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool SettingEquals(System.Xml.XmlNode settings, string name, string expected)
        {
            if (settings == null) return true;
            // 任务 XML 省略默认项，缺失字段仍可能需要修复。
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
            string arguments;
            return TryReadTaskTarget(out command, out arguments);
        }

        internal static bool TryReadTaskTarget(out string command, out string arguments)
        {
            command = null;
            arguments = null;
            string xml;
            if (RunCore(
                    "/Query /TN " + TaskName + " /XML",
                    true, out xml) != 0)
                return false;
            if (!StartupTaskEnabled(xml)) return false;
            command = ParseTaskCommandXml(xml);
            arguments = ParseTaskArgumentsXml(xml);
            return !string.IsNullOrWhiteSpace(command) && arguments != null;
        }

        internal static bool IsCurrentTaskTarget(string currentExecutable, string dataPath,
            string taskExecutable, string taskArguments)
        {
            try { return IsCurrentTaskTarget(currentExecutable, dataPath, taskExecutable, taskArguments, StartupPayload.ManagedRoot); }
            catch { return false; }
        }

        internal static bool IsCurrentTaskTarget(string currentExecutable, string dataPath,
            string taskExecutable, string taskArguments, string managedRoot)
        {
            try
            {
                string expected = StartupPayload.GetExpectedExecutable(currentExecutable, managedRoot);
                return !NeedsStartupTaskRefresh(expected, taskExecutable)
                    && StartupPayload.IsVerifiedExecutable(taskExecutable, managedRoot)
                    && string.Equals(BuildStartupArguments(dataPath), taskArguments, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
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
#if PAVISE_SELFTEST
            if (RunForTest != null) return RunForTest(arguments, capture, out stdout);
            lastSchtasksError = "Scheduled task operations must be mocked in self-tests";
            return -1;
#else
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
#endif
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
