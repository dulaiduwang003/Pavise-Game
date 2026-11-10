using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace PaviseApp
{
    internal static class PsRunner
    {
        public static bool Run(string script, string label, int timeoutMs, out string stdout)
        {
            return Run(script, label, timeoutMs, null, out stdout);
        }

        public static bool Run(string script, string label, int timeoutMs, IDictionary<string, string> args, out string stdout)
        {
            string failure;
            bool runCoreSucceeded = RunCore(script, label, timeoutMs, args, out stdout, out failure);
            if (!runCoreSucceeded)
            {
                Logger.Warn(failure);
            }
            return runCoreSucceeded;
        }

        internal static bool RunWithFailure(string script, string label, int timeoutMs, out string stdout, out string failure)
        {
            return RunCore(script, label, timeoutMs, null, out stdout, out failure);
        }

        private static readonly object ownerGate = new object();
        private sealed class ScriptOwner
        {
            internal Process Process;
            internal readonly PipeCapture Output = new PipeCapture(), Error = new PipeCapture();
        }
        internal const int OutputCaptureLimit = 65536;
        private sealed class PipeCapture
        {
            internal readonly object Gate = new object();
            internal readonly StringBuilder Text = new StringBuilder();
            internal int Ended, Failed, Overflow;
            internal bool Capture = true;
            internal void StopCapture() { lock (Gate) { Capture = false; Text.Clear(); } }
        }
        private static void StartDrain(StreamReader reader, PipeCapture state)
        {
            Task.Factory.StartNew(delegate
            {
                // 使用有界缓冲区将继承管道读取到 EOF，超时后也继续排空。
                char[] chunk = new char[4096];
                try
                {
                    int length;
                    while ((length = reader.Read(chunk, 0, chunk.Length)) != 0)
                    {
                        lock (state.Gate)
                        {
                            if (!state.Capture) continue;
                            int take = Math.Min(length, OutputCaptureLimit - state.Text.Length);
                            if (take > 0) state.Text.Append(chunk, 0, take);
                            if (take != length) { state.Overflow = 1; state.Capture = false; }
                        }
                    }
                    Interlocked.Exchange(ref state.Ended, 1);
                }
                catch { Interlocked.Exchange(ref state.Failed, 1); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        private static ScriptOwner ownedProcess;
        private static bool runnerBusy;

        private static bool ReleaseExitedOwnerLocked()
        {
            if (ownedProcess == null) return true;
            try { if (!ownedProcess.Process.HasExited) return false; }
            catch { return false; }
            // 继承管道可能比父进程存活更久；两个读取者都到达 EOF 后才释放运行器。
            if (Volatile.Read(ref ownedProcess.Output.Ended) == 0 || Volatile.Read(ref ownedProcess.Error.Ended) == 0) return false;
            Process exited = ownedProcess.Process;
            ownedProcess = null;
            try { exited.Dispose(); } catch { }
            return true;
        }

        private static bool RunCore(string script, string label, int timeoutMs, IDictionary<string, string> args, out string stdout, out string failure)
        {
            stdout = "";
            failure = "";
            lock (ownerGate)
            {
                if (runnerBusy || !ReleaseExitedOwnerLocked())
                {
                    failure = label + ": previous PowerShell operation still owns the runner";
                    return false;
                }
                runnerBusy = true;
            }
            try { return RunOwned(script, label, timeoutMs, args, out stdout, out failure); }
            finally
            {
                lock (ownerGate)
                {
                    // 超时不能证明恢复完成，保留所有权直到进程退出。
                    ReleaseExitedOwnerLocked();
                    runnerBusy = false;
                }
            }
        }

        private static bool RunOwned(string script, string label, int timeoutMs, IDictionary<string, string> args, out string stdout, out string failure)
        {
            stdout = "";
            failure = "";
            ScriptOwner owner = null;
            try
            {
                string command = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\r\n$OutputEncoding = [Console]::OutputEncoding\r\n$ProgressPreference = 'SilentlyContinue'\r\n" + (script ?? "") + "\r\nif (!$?) { exit 1 }\r\n";
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = "powershell.exe";
                start.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -Command \"& ([ScriptBlock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + encoded + "'))))\"";
                start.UseShellExecute = false;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Hidden;
                start.StandardOutputEncoding = new UTF8Encoding(false);
                start.StandardErrorEncoding = new UTF8Encoding(false);
                if (args != null)
                    foreach (KeyValuePair<string, string> arg in args)
                        start.EnvironmentVariables[arg.Key] = arg.Value ?? "";
                Process process = Process.Start(start);
                if (process == null) { failure = label + ": PowerShell process was not created"; return false; }
                owner = new ScriptOwner { Process = process };
                lock (ownerGate) ownedProcess = owner;
                StartDrain(process.StandardOutput, owner.Output);
                StartDrain(process.StandardError, owner.Error);
                if (!process.WaitForExit(timeoutMs))
                {
                    failure = label + Lang.T("log.psrunner.1") + timeoutMs + "ms; process owner retained until exit";
                    return false;
                }
                // 父进程退出后，WaitForExit() 仍可能等待继承管道而阻塞，避免使用会排空管道的重载。
                if (!SpinWait.SpinUntil(() => (Volatile.Read(ref owner.Output.Ended) != 0 && Volatile.Read(ref owner.Error.Ended) != 0) || Volatile.Read(ref owner.Output.Failed) != 0 || Volatile.Read(ref owner.Error.Failed) != 0, 3000))
                {
                    failure = label + ": PowerShell output did not complete after exit";
                    return false;
                }
                                if (Volatile.Read(ref owner.Output.Failed) != 0 || Volatile.Read(ref owner.Error.Failed) != 0)
                { failure = label + ": PowerShell output reader failed; ownership retained"; return false; }
                if (Volatile.Read(ref owner.Output.Overflow) != 0 || Volatile.Read(ref owner.Error.Overflow) != 0)
                { failure = label + ": PowerShell output exceeded capture limit"; return false; }
                lock (owner.Output.Gate) stdout = owner.Output.Text.ToString();
                if (process.ExitCode != 0)
                {
                    string error;
                    lock (owner.Error.Gate) error = owner.Error.Text.ToString().Trim();
                    failure = label + Lang.T("log.psrunner.2") + process.ExitCode + " " + NormalizeError(error);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                failure = label + Lang.T("log.psrunner.3") + NormalizeError(ex.Message);
                return false;
            }
            finally
            {
                if (owner != null) { owner.Output.StopCapture(); owner.Error.StopCapture(); }
            }
        }
        internal static string NormalizeError(string text)
        {
            text = (text ?? "").Replace("#< CLIXML", "").Trim();
            int matchIndex = text.IndexOf("<Objs", StringComparison.Ordinal);
            if (matchIndex >= 0)
            {
                int matchIndex2 = text.IndexOf("</Objs>", matchIndex, StringComparison.Ordinal);
                if (matchIndex2 < 0)
                {
                    return "Unrecognized PowerShell error output";
                }
                try
                {
                    XmlReaderSettings settings = new XmlReaderSettings();
                    settings.DtdProcessing = DtdProcessing.Prohibit;
                    settings.XmlResolver = null;
                    XmlDocument currentXmlDocument = new XmlDocument();
                    currentXmlDocument.XmlResolver = null;
                    using (StringReader input = new StringReader(text.Substring(matchIndex, matchIndex2 + 7 - matchIndex)))
                    {
                        using (XmlReader reader = XmlReader.Create(input, settings))
                        {
                            currentXmlDocument.Load(reader);
                        }
                    }
                    StringBuilder stringBuilder = new StringBuilder(text.Substring(0, matchIndex));
                    foreach (XmlNode element in currentXmlDocument.GetElementsByTagName("S"))
                    {
                        XmlAttribute xmlAttribute = ((element.Attributes == null) ? null : element.Attributes["S"]);
                        if (xmlAttribute != null && (xmlAttribute.Value == "Error" || xmlAttribute.Value == "Warning"))
                        {
                            stringBuilder.Append(' ').Append(Regex.Replace(element.InnerText, "_x([0-9a-fA-F]{4})_", (Match match) => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString()));
                        }
                    }
                    stringBuilder.Append(' ').Append(text.Substring(matchIndex2 + 7));
                    text = stringBuilder.ToString();
                }
                catch (XmlException)
                {
                    return "Unrecognized PowerShell error output";
                }
            }
            text = Regex.Replace(text, "\\s+", " ").Trim();
            if (text.Length > 2000)
            {
                return text.Substring(0, 2000) + "…";
            }
            return text;
        }
    }
}
