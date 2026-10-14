using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PaviseApp
{
    internal sealed class BackgroundFreeze
    {
        private readonly object gate = new object();
        private Process guard;
        private int activeCount;
        private readonly Dictionary<int, long> activeTargets = new Dictionary<int, long>();
        private bool failed;
        private int currentGame;
        private long currentCreation;
        private string automationToken = "";
        internal string Error { get; private set; }
        internal string DiagnosticLine
        {
            get { lock (gate) { return "backend=process-state-object supported=" + FreezeNative.Supported
                + " game=" + currentGame + "/" + currentCreation + " active=" + ActiveCount
                + " helper=" + GuardState() + " error=" + (Error ?? "none")
                + " ordinaryJobPercent=0.01 serviceJobPercent=5 altTabThaw=false"; } }
        }
        private string GuardState()
        {
            try { return guard == null ? "none" : guard.Id + (guard.HasExited ? ":exit=" + guard.ExitCode + ":" + ExitReason(guard.ExitCode) : ":alive"); }
            catch { return "unavailable"; }
        }
        private static string ExitReason(int code)
        {
            switch (code)
            {
                case 0: return "completed";
                case 1: return "startup-validation";
                case 2: return "watchdog";
                case 3: return "parent-exited";
                case 4: return "input-closed";
                case 5: return "stale-scan";
                case 6: return "invalid-protocol";
                case 7: return "guardian-exception";
                default: return "terminated-or-os-error";
            }
        }
        private void Note(string message, bool warning = false)
        {
            string line = (string.IsNullOrEmpty(automationToken) ? "" : "AUTO [" + automationToken + "] ")
                + "[FREEZE] game=" + currentGame + "/" + currentCreation + " helper=" + GuardState() + " " + message;
            if (warning) Logger.Warn(line); else Logger.Info(line);
        }
        private void ReadTrace()
        {
            string reply = Exchange("REPORT");
            if (reply == null || !reply.StartsWith("TRACE ", StringComparison.Ordinal)) throw new IOException("guardian-trace-reply");
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(reply.Substring(6)));
            foreach (string line in decoded.Split('\n'))
                if (line.StartsWith("refused ", StringComparison.Ordinal)
                    || line.StartsWith("anchor-rejected ", StringComparison.Ordinal)
                    || line.StartsWith("trace-truncated ", StringComparison.Ordinal)) Note(line, true);
        }
        internal int ActiveCount { get { lock (gate) { return guard == null || guard.HasExited ? 0 : activeCount; } } }
        internal bool HasPending { get { lock (gate) { return guard != null && !guard.HasExited; } } }
        internal bool Owns(int pid, long creation)
        { lock (gate) { long born; return activeTargets.TryGetValue(pid, out born) && born == creation && guard != null && !guard.HasExited; } }

        private string ReadReply()
        {
            StreamReader reader = guard.StandardOutput;
            Task<string> read = Task.Factory.StartNew(() => reader.ReadLine());
            if (!read.Wait(3000)) throw new TimeoutException("freeze-guardian");
            return read.Result;
        }
        private bool Start()
        {
            if (failed || !FreezeNative.Supported) return false;
            if (guard != null) return !guard.HasExited;
            long creation, cpu; ulong io; int pid;
            if (!Native.QueryProcessSample(new IntPtr(-1), out creation, out cpu, out io)) return false;
            using (Process self = Process.GetCurrentProcess()) pid = self.Id;
            guard = Process.Start(new ProcessStartInfo(typeof(BackgroundFreeze).Assembly.Location,
                FreezeGuard.Argument + " " + pid + " " + creation)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
            return ReadReply() == "READY";
        }
        private string Exchange(string message)
        {
            Process helper = guard;
            Task<string> exchange = Task.Factory.StartNew(delegate {
                helper.StandardInput.WriteLine(message); helper.StandardInput.Flush();
                return helper.StandardOutput.ReadLine();
            });
            if (!exchange.Wait(3000)) throw new TimeoutException("freeze-guardian");
            return exchange.Result;
        }
        internal bool Release(int pid, long creation, string reason = "protection-added")
        {
            lock (gate)
            {
                long tracked;
                if (!activeTargets.TryGetValue(pid, out tracked) || tracked != creation) return true;
                try
                {
                    if (guard == null || guard.HasExited || Exchange("RELEASE " + pid + " " + creation) == "OK")
                    {
                        activeTargets.Remove(pid); activeCount = activeTargets.Count;
                        Logger.Info(Lang.F("freeze.released", 1));
                        return true;
                    }
                }
                catch (Exception error) { Note("release-failed reason=" + reason + " error=" + error.GetType().Name, true); }
                return Clear("release-failed");
            }
        }
        internal HashSet<int> Sync(Dictionary<int, FreezeTarget> wanted, int gamePid, long gameCreation, string sessionToken = "")
        {
            lock (gate)
            {
                var frozen = new HashSet<int>();
                automationToken = sessionToken;
                currentGame = gamePid; currentCreation = gameCreation;
                if (wanted.Count == 0 || gamePid <= 4 || gameCreation <= 0)
                { Clear(gamePid <= 4 || gameCreation <= 0 ? "invalid-game-anchor" : "no-candidates"); return frozen; }
                if (!FreezeNative.Supported || failed) return frozen;
                try
                {
                    if (!Start()) throw new InvalidOperationException("guardian-start");
                    if (wanted.Count > FreezeGuard.MaxTargets) throw new InvalidOperationException("target-capacity");
                    var command = new StringBuilder("SYNC " + gamePid + " " + gameCreation);
                    foreach (FreezeTarget target in wanted.Values)
                        command.Append(' ').Append(target.Pid).Append(':').Append(target.Creation).Append(':')
                            .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(target.Path)));
                    if (command.Length > 1024 * 1024) throw new InvalidOperationException("target-capacity");
                    string reply = Exchange(command.ToString());
                    if (reply == null || !reply.StartsWith("OK ", StringComparison.Ordinal)) throw new IOException("guardian-reply");
                    foreach (string word in reply.Substring(3).Split(','))
                    {
                        if (word.Length == 0) continue;
                        int pid; if (!int.TryParse(word, out pid) || !wanted.ContainsKey(pid)) throw new IOException("guardian-identity");
                        frozen.Add(pid);
                    }
                    if (activeCount != frozen.Count) Logger.Info(Lang.F("freeze.status", frozen.Count));
                    activeTargets.Clear();
                    foreach (int pid in frozen) activeTargets[pid] = wanted[pid].Creation;
                    activeCount = frozen.Count; Error = null;
                    ReadTrace();
                }
                catch (Exception error)
                {
                    failed = true; Error = error.GetType().Name + ":" + error.Message;
                    Clear("backend-failed"); frozen.Clear(); Logger.Warn(Lang.F("freeze.failed", Error));
                }
                return frozen;
            }
        }
        internal bool Clear(string reason = "policy-release")
        {
            lock (gate)
            {
                if (guard == null) { activeCount = 0; activeTargets.Clear(); return true; }
                try
                {
                    if (!guard.HasExited)
                    {
                        // 管道写入超时后不能关闭仍被占用的写入器。
                        guard.Kill();
                        if (!guard.WaitForExit(1500)) { Note("cleanup-pending reason=" + reason, true); return false; }
                    }
                    if (activeTargets.Count > 0) Logger.Info(Lang.F("freeze.released", activeTargets.Count));
                    guard.Dispose(); guard = null; activeCount = 0; activeTargets.Clear(); return true;
                }
                catch (Exception error) { Note("cleanup-failed reason=" + reason + " error=" + error.GetType().Name, true); return false; }
            }
        }
    }
}
