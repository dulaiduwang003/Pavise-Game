// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace PaviseApp
{
    internal static class PsRunner
    {

        public static bool Run(string script, string label, int timeoutMs, out string stdout)
        {
            return Run(script, label, timeoutMs, null, out stdout);
        }

        public static bool Run(string script, string label, int timeoutMs,
            IDictionary<string, string> args, out string stdout)
        {
            string failure;
            bool ok = RunCore(script, label, timeoutMs, args, out stdout, out failure);
            if (!ok) Logger.Warn(failure);
            return ok;
        }

        internal static bool RunWithFailure(string script, string label, int timeoutMs,
            out string stdout, out string failure)
        { return RunCore(script, label, timeoutMs, null, out stdout, out failure); }

        private static bool RunCore(string script, string label, int timeoutMs,
            IDictionary<string, string> args, out string stdout, out string failure)
        {
            stdout = "";
            failure = "";
            try
            {

                string wrapped =
                    "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\r\n"
                    + "$OutputEncoding = [Console]::OutputEncoding\r\n"
                    + "$ProgressPreference = 'SilentlyContinue'\r\n"
                    + (script ?? "")
                    // 脚本块调用会重置宿主的成功状态。
                    + "\r\nif (!$?) { exit 1 }\r\n";
                string encoded = Convert.ToBase64String(
                    Encoding.Unicode.GetBytes(wrapped));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // 避免 EncodedCommand 将错误输出编码为 CLIXML。
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -Command \"& ([ScriptBlock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('"
                        + encoded + "'))))\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);
                if (args != null)
                    foreach (KeyValuePair<string, string> kv in args)
                        psi.EnvironmentVariables[kv.Key] = kv.Value ?? "";

                using (Process p = Process.Start(psi))
#if PAVISE_STABILITY_COMPARISON
                using (IDisposable helperScope = null)
#else
                using (IDisposable helperScope = SelfYield.OwnHelperScope(p.Handle))
#endif
                {
                    var outBuf = new StringBuilder();
                    var errBuf = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) lock (outBuf) outBuf.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) lock (errBuf) errBuf.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    var waitBudget = Stopwatch.StartNew();
#if PAVISE_STABILITY_COMPARISON
                    int firstWait = timeoutMs;
#else
                    int firstWait = timeoutMs >= 1000 ? timeoutMs - Math.Min(2000, timeoutMs / 4) : timeoutMs;
#endif
                    bool exited = p.WaitForExit(firstWait);
                    if (!exited && firstWait != timeoutMs)
                    {
                        helperScope.Dispose();
                        int remaining = (int)Math.Max(0L, timeoutMs - waitBudget.ElapsedMilliseconds);
                        exited = p.WaitForExit(remaining);
                    }
                    if (!exited)
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(3000); } catch { }
                        failure = label + Lang.T("log.psrunner.1") + timeoutMs + "ms";
                        return false;
                    }
                    p.WaitForExit();
                    lock (outBuf) stdout = outBuf.ToString();
                    if (p.ExitCode != 0)
                    {
                        string detail;
                        lock (errBuf) detail = errBuf.ToString().Trim();
                        failure = label + Lang.T("log.psrunner.2") + p.ExitCode + " " + NormalizeError(detail);
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex) { failure = label + Lang.T("log.psrunner.3") + NormalizeError(ex.Message); return false; }
        }

        internal static string NormalizeError(string text)
        {
            text = (text ?? "").Replace("#< CLIXML", "").Trim();
            int start = text.IndexOf("<Objs", StringComparison.Ordinal);
            if (start >= 0)
            {
                int end = text.IndexOf("</Objs>", start, StringComparison.Ordinal);
                if (end < 0) return "Unrecognized PowerShell error output";
                try
                {
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                    var document = new XmlDocument { XmlResolver = null };
                    using (var input = new StringReader(text.Substring(start, end + 7 - start)))
                    using (var reader = XmlReader.Create(input, settings)) document.Load(reader);
                    var detail = new StringBuilder(text.Substring(0, start));
                    foreach (XmlNode node in document.GetElementsByTagName("S"))
                    {
                        XmlAttribute stream = node.Attributes == null ? null : node.Attributes["S"];
                        if (stream == null || (stream.Value != "Error" && stream.Value != "Warning")) continue;
                        // 只解码一次，保留转义下划线。
                        detail.Append(' ').Append(Regex.Replace(node.InnerText, "_x([0-9a-fA-F]{4})_",
                            delegate(Match match) { return ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString(); }));
                    }
                    detail.Append(' ').Append(text.Substring(end + 7));
                    text = detail.ToString();
                }
                catch (XmlException) { return "Unrecognized PowerShell error output"; }
            }
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length <= 2000 ? text : text.Substring(0, 2000) + "…";
        }
    }
}
