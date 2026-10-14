// 作者：bdth 2074055628@qq.com
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class Logger
    {
        private const long RotateBytes = 512 * 1024;

        private static readonly object lk = new object();
        public static string LogPath;
        private static long version;
        public static long Version { get { return System.Threading.Interlocked.Read(ref version); } }
        private static long knownLength = -1;
        private static string knownPath;
        private static bool writesSuspendedForReset;
        // 关闭运行日志不影响独立的崩溃记录。
        private static bool writesEnabled = true;

        public const string WritesEnabledKey = "LogWritesEnabled";

        public static bool WritesEnabled
        {
            get { lock (lk) return writesEnabled; }
            set { lock (lk) { writesEnabled = value; if (!value) repeated.Clear(); } }
        }

        // 删除日志前先排空写入，再拒绝后续回调。
        internal static void SuspendWritesForReset()
        {
            lock (lk) { writesSuspendedForReset = true; repeated.Clear(); }
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
                repeated.Clear();
            }
        }
#endif

        // 结构化日志显式标级别，避免把 lastFailure=none 误判为异常。
        public const string WarnTag = "WARN ";
        public const string FailTag = "FAIL ";
        public const string InfoTag = "INFO ";
        public const string SuccessTag = "PASS ";

        public static void Info(string msg) { Log(InfoTag + msg); }

        public static void Warn(string msg) { Log(WarnTag + msg); }

        public static void Error(string msg) { Log(FailTag + msg); }

        private sealed class RepeatedFailure
        {
            internal string Message;
            internal long Next;
            internal int Suppressed;
        }
        private static readonly Dictionary<string, RepeatedFailure> repeated = new Dictionary<string, RepeatedFailure>();
        internal static void RepeatedError(string source, string message, long now = -1)
        {
            if (now < 0) now = DateTime.UtcNow.Ticks;
            lock (lk)
            {
                if (writesSuspendedForReset || !writesEnabled) { repeated.Remove(source); return; }
                RepeatedFailure state;
                if (repeated.TryGetValue(source, out state))
                {
                    if (state.Message == message && now < state.Next)
                    { if (state.Suppressed < int.MaxValue) state.Suppressed++; return; }
                    FlushRepeated(source, state);
                }
                else
                {
                    if (repeated.Count >= 64) repeated.Clear();
                    state = new RepeatedFailure(); repeated[source] = state;
                }
                state.Message = message; state.Next = now + TimeSpan.FromMinutes(1).Ticks;
                Error(message);
            }
        }
        private static void FlushRepeated(string source, RepeatedFailure state)
        {
            if (state.Suppressed > 0)
                Warn(source + " | repeated=" + state.Suppressed + " | " + state.Message);
            state.Suppressed = 0;
        }
        internal static void RepeatedRecovered(string source)
        {
            lock (lk)
            {
                if (writesSuspendedForReset || !writesEnabled) { repeated.Remove(source); return; }
                RepeatedFailure state;
                if (!repeated.TryGetValue(source, out state)) return;
                FlushRepeated(source, state); repeated.Remove(source);
                Info(source + " | recovered");
            }
        }

        public static void Log(string msg)
        {
            try
            {
                lock (lk)
                {
                    if (writesSuspendedForReset || !writesEnabled) return;
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        + "  " + (msg ?? "").Replace("\r\n", " ⏎ ").Replace("\r", " ⏎ ").Replace("\n", " ⏎ ") + Environment.NewLine;
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
                    repeated.Clear();
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
                        long previousStart = 0;
                        long length = stream.Length;
                        if (length > 0)
                        {
                            var buffer = new byte[4096];
                            int breaks = 0;
                            long cursor = length;
                            while (cursor > 0 && breaks <= maxLines)
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
                                    if (breaks == maxLines) start = absolute + 1;
                                    else if (breaks == maxLines + 1) { previousStart = absolute + 1; break; }
                                }
                            }
                        }
                        stream.Position = start;
                        string tail;
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
                            tail = reader.ReadToEnd().TrimEnd('\r', '\n');
                        if (start > previousStart && tail.TrimStart().StartsWith("<Objs", StringComparison.Ordinal))
                        {
                            stream.Position = previousStart;
                            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
                            {
                                string parent = reader.ReadLine();
                                if (parent != null && parent.IndexOf("#< CLIXML", StringComparison.Ordinal) >= 0)
                                    tail = parent + Environment.NewLine + tail;
                            }
                        }
                        return tail;
                    }
                }
            }
            catch { return ""; }
        }
    }

}
