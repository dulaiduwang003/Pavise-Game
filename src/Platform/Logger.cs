// @author bdth 2074055628@qq.com
// File purpose Writes the runtime log and notifies the UI to refresh
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal static class Logger
    {
        private const long RotateBytes = 512 * 1024;

        private static readonly object lk = new object();
        public static string LogPath;
        // Incremented on every flush, the log page compares it and skips re-reading the file under the same lock when nothing is new
        private static long version;
        public static long Version { get { return System.Threading.Interlocked.Read(ref version); } }
        private static long knownLength = -1;
        private static string knownPath;
        private static bool writesSuspendedForReset;
        // User-toggleable runtime log switch, separate from writesSuspendedForReset
        //   the latter is the reset flow's one-shot write barrier, lifted only by a new process
        //   this one is a regular setting, toggled back and forth any time, nothing is flushed or queued for later while off
        //   crash dumps go through AppendCrash on a separate path, unaffected by this switch, the scene must be preserved when things break
        private static bool writesEnabled = true;

        public const string WritesEnabledKey = "LogWritesEnabled";

        public static bool WritesEnabled
        {
            get { lock (lk) return writesEnabled; }
            set { lock (lk) writesEnabled = value; }
        }

        // Before reset deletes the files the same lock drains pending append, rotate and clear work
        // the tail stays readable, writes are normally re-allowed only once the new process is up
        internal static void SuspendWritesForReset()
        {
            lock (lk) writesSuspendedForReset = true;
        }

#if PAVISE_SELFTEST
        internal static void ResetWriteBarrierForTest()
        {
            lock (lk)
            {
                writesSuspendedForReset = false;
                writesEnabled = true;
                knownLength = -1;
                knownPath = null;
            }
        }
#endif

        // Level tags placed after the timestamp, the log page classifies by tag, untagged lines still go by the word list, see LogStreamView.Classify
        //   WARN environment limits: this machine lacks a component, blocked by permissions or anti-cheat, stripped-down OS missing parts, the kind that goes away on another machine
        //   FAIL functional faults: a write, restore or flush failed, an exception was thrown, data at risk, the kind someone must look at
        //   INFO explicitly normal, overrides the word list, structured diagnostic lines often carry field names like lastFailure= code=
        //     substring matching on the word list would flag lastFailure=none as an error, such lines must declare their own level
        //   PASS confirmed effective items, keeps words like error in process names from affecting classification
        public const string WarnTag = "WARN ";
        public const string FailTag = "FAIL ";
        public const string InfoTag = "INFO ";
        public const string SuccessTag = "PASS ";

        public static void Info(string msg) { Log(InfoTag + msg); }

        public static void Warn(string msg) { Log(WarnTag + msg); }

        public static void Error(string msg) { Log(FailTag + msg); }

        public static void Log(string msg)
        {
            try
            {
                lock (lk)
                {
                    if (writesSuspendedForReset || !writesEnabled) return;
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        + "  " + msg + Environment.NewLine;
                    if (knownLength < 0 || !string.Equals(knownPath, LogPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var probe = new FileInfo(LogPath);
                        knownLength = probe.Exists ? probe.Length : 0;
                        knownPath = LogPath;
                    }
                    if (knownLength > RotateBytes)
                    {
                        var fi = new FileInfo(LogPath);
                        if (fi.Exists && fi.Length > RotateBytes)
                        {
                            string old = LogPath + ".old";
                            try
                            {
                                if (File.Exists(old)) File.Delete(old);
                                fi.MoveTo(old);
                            }
                            catch { try { fi.Delete(); } catch { } }
                        }
                        knownLength = 0;
                    }
                    File.AppendAllText(LogPath, line);
                    knownLength += Encoding.UTF8.GetByteCount(line);
                    System.Threading.Interlocked.Increment(ref version);
                }
            }
            catch { lock (lk) knownLength = -1; }
        }

        public static void LogFailure(string context, Exception error)
        {
            string detail = context + " " + error.GetType().Name + " - " + error.Message;
            Debug.WriteLine(detail);
            Log(FailTag + detail);
        }

        internal static void AppendCrash(string path, string details)
        {
            try
            {
                lock (lk)
                {
                    if (writesSuspendedForReset) return;
                    File.AppendAllText(path, details);
                }
            }
            catch { }
        }

        public static void Clear()
        {
            try
            {
                lock (lk)
                {
                    if (writesSuspendedForReset) return;
                    File.WriteAllText(LogPath, "");
                    System.Threading.Interlocked.Increment(ref version);
                    knownLength = 0;
                    knownPath = LogPath;
                }
            }
            catch { lock (lk) knownLength = -1; }
        }

        public static string Tail(int maxLines)
        {
            try
            {
                lock (lk)
                {
                    if (maxLines <= 0 || string.IsNullOrEmpty(LogPath) || !File.Exists(LogPath)) return "";
                    using (var stream = new FileStream(
                        LogPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        long start = 0;
                        long length = stream.Length;
                        if (length > 0)
                        {
                            var buffer = new byte[4096];
                            int breaks = 0;
                            long cursor = length;
                            while (cursor > 0 && breaks < maxLines)
                            {
                                int take = (int)Math.Min(buffer.Length, cursor);
                                cursor -= take;
                                stream.Position = cursor;
                                int read = stream.Read(buffer, 0, take);
                                for (int i = read - 1; i >= 0; i--)
                                {
                                    long absolute = cursor + i;
                                    if (buffer[i] != (byte)'\n' || absolute == length - 1) continue;
                                    breaks++;
                                    if (breaks == maxLines) { start = absolute + 1; break; }
                                }
                            }
                        }
                        stream.Position = start;
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
                            return reader.ReadToEnd().TrimEnd('\r', '\n');
                    }
                }
            }
            catch { return ""; }
        }
    }

}
