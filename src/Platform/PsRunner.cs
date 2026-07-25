// @author bdth 2074055628@qq.com
// 文件用途 以隐藏窗口方式执行 PowerShell 脚本并回收输出

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace AegisApp
{
    internal static class PsRunner
    {
        // 三条硬性约束，缺一条就会出事：
        //
        // 1) 用户数据一律走环境变量，绝不拼进脚本文本。
        //    PowerShell 把 U+2018/U+2019 等排版引号也当作单引号定界符，所以"把 ' 翻倍"
        //    这种转义挡不住注入——实测 'X’;Write-Output PWNED;’' 会真的执行，且退出码为 0。
        //    Aegis 带管理员清单，被注入等于把管理员权限交出去。
        //
        // 2) 不落临时 .ps1 文件。写在用户可写的 %TEMP% 再由管理员进程执行，
        //    中间存在被同用户中完整性进程替换文件的窗口。改用 -EncodedCommand，
        //    脚本正文以 base64(UTF-16LE) 直接进命令行，既不落盘也不需要任何引号转义。
        //
        // 3) stdout 和 stderr 都必须异步读。同步 ReadToEnd 会一直阻塞到子进程关闭管道，
        //    排在它后面的 WaitForExit(timeout) 就永远等不到超时——超时参数会变成死代码，
        //    而这些调用有的跑在 UI 线程上。
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
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script ?? ""));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
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
                        Logger.Log(label + "：PowerShell 执行超时（" + timeoutMs + "ms）");
                        return false;
                    }
                    lock (outBuf) stdout = outBuf.ToString();
                    if (p.ExitCode != 0)
                    {
                        string detail;
                        lock (errBuf) detail = errBuf.ToString().Trim();
                        Logger.Log(label + "：PowerShell 执行失败(exit=" + p.ExitCode + ")：" + detail);
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex) { Logger.Log(label + "：无法执行 PowerShell：" + ex.Message); return false; }
        }
    }
}
