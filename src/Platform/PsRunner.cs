// @author bdth 2074055628@qq.com
// 文件用途 以隐藏窗口方式执行 PowerShell 脚本并回收输出

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

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
            stdout = "";
            try
            {

                string wrapped =
                    "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\r\n"
                    + "$OutputEncoding = [Console]::OutputEncoding\r\n"
                    + (script ?? "");
                string encoded = Convert.ToBase64String(
                    Encoding.Unicode.GetBytes(wrapped));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
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
                {
                    var outBuf = new StringBuilder();
                    var errBuf = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) lock (outBuf) outBuf.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) lock (errBuf) errBuf.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(3000); } catch { }
                        Logger.Log(label + " PowerShell 执行超时 " + timeoutMs + "ms ");
                        return false;
                    }
                    p.WaitForExit();
                    lock (outBuf) stdout = outBuf.ToString();
                    if (p.ExitCode != 0)
                    {
                        string detail;
                        lock (errBuf) detail = errBuf.ToString().Trim();
                        Logger.Log(label + " PowerShell 执行失败 exit " + p.ExitCode + " " + detail);
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex) { Logger.Log(label + " 无法执行 PowerShell " + ex.Message); return false; }
        }
    }
}
