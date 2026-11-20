using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static class Logger
    {
        private sealed class RepeatedFailure
        {
            internal string Message;

            internal long Next;

            internal int Suppressed;
        }

        private const long RotateBytes = 524288L;

        public const string WritesEnabledKey = "LogWritesEnabled";

        public const string WarnTag = "警告 ";

        public const string FailTag = "失败 ";

        public const string InfoTag = "信息 ";

        // 和其他标签一样不带前导空格 会话成功行就是这样写的 测试按它拼期望行
        public const string SuccessTag = "成功 ";

        private static readonly object lk = new object();

        public static string LogPath;

        private static long version;

        private static long knownLength = -1L;

        private static string knownPath;

        private static bool writesSuspendedForReset;

        private static bool writesEnabled = true;

        private static readonly Dictionary<string, RepeatedFailure> repeated = new Dictionary<string, RepeatedFailure>();

        public static long Version
        {
            get
            {
                return Interlocked.Read(ref version);
            }
        }

        public static bool WritesEnabled
        {
            get
            {
                lock (lk)
                {
                    return writesEnabled;
                }
            }
            set
            {
                lock (lk)
                {
                    writesEnabled = value;
                    if (!value)
                    {
                        repeated.Clear();
                    }
                }
            }
        }

        internal static void SuspendWritesForReset()
        {
            lock (lk)
            {
                writesSuspendedForReset = true;
                repeated.Clear();
            }
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

        public static void Info(string msg)
        {
            Log(LogText.LevelTag(LogEventSeverity.Info, Lang.DiagnosticLanguage) + msg);
        }

        internal static void RuntimeIdentity()
        {
            Info(Format("运行时 身份 版本={0} 构建标识={1} 系统版本={2} 架构={3} 进程编号={4}",
                "RUNTIME event=identity version={0} mvid={1} os={2} arch={3} pid={4}",
                App.Version, typeof(App).Module.ModuleVersionId.ToString("D"), Environment.OSVersion.Version,
                Environment.Is64BitProcess ? "x64" : "x86", System.Diagnostics.Process.GetCurrentProcess().Id));
        }

        public static void Warn(string msg)
        {
            Log(LogText.LevelTag(LogEventSeverity.Warning, Lang.DiagnosticLanguage) + msg);
        }

        // 日志调用点用的中英双写 放在 Logger 上是因为只编部分源文件的测试都自带 Logger 替身 不带 Lang
        // 葡萄牙语界面仍使用原有中文技术记录
        internal static string Text(string chinese, string english)
        { return Lang.Effective == Lang.Portuguese ? chinese : Lang.Text(chinese, english); }

        // 带参数的要传模板 先拼好再传的话其他语言查不到译文
        internal static string Format(string chinese, string english, params object[] args)
        { return string.Format(Text(chinese, english), args); }

        internal static void Log(string chinese, string english)
        { WriteLocalized(chinese, Text(chinese, english), Lang.DiagnosticLanguage); }

        internal static void LogFormat(string chinese, string english, params object[] args)
        { WriteLocalized(string.Format(chinese ?? "", args), Format(chinese, english, args), Lang.DiagnosticLanguage); }

        // 一行由好几段文案拼成时 调用点交一个拼整行的函数 中文模式只拼一次
        // 其他语言先按当前语言拼出要写的文字 再在本线程临时切成中文拼一次用来定级别 所以拼的过程不能有副作用
        internal static void Log(Func<string> build)
        {
            if (build == null) return;
            int language = Lang.DiagnosticLanguage;
            string localized;
            if (language == Lang.Effective) localized = build();
            else using (Lang.Override(language)) localized = build();
            if (language == Lang.Chinese) { Log(localized); return; }
            string chinese;
            try { using (Lang.Override(Lang.Chinese)) chinese = build(); }
            catch (Exception) { chinese = localized; }
            WriteLocalized(chinese, localized, language);
        }

        // 没带级别标签的行靠中文关键字定级别 所以其他语言写入时按中文原文算好级别直接带上标签 中文原样写
        private static void WriteLocalized(string chinese, string localized, int language)
        {
            if (language == Lang.Chinese) { Log(chinese); return; }
            if (LogText.HasLevelTag(chinese)) { Log(localized); return; }
            string body;
            Log(LogText.LevelTag(LogText.ClassifyLine(chinese, out body), language) + localized);
        }

        public static void Error(string msg)
        {
            Log(LogText.LevelTag(LogEventSeverity.Error, Lang.DiagnosticLanguage) + msg);
        }

        internal static void RepeatedError(string source, string message, long now = -1L)
        {
            if (now < 0)
            {
                now = DateTime.UtcNow.Ticks;
            }
            lock (lk)
            {
                if (writesSuspendedForReset || !writesEnabled)
                {
                    repeated.Remove(source);
                    return;
                }
                RepeatedFailure value;
                if (repeated.TryGetValue(source, out value))
                {
                    if (value.Message == message && now < value.Next)
                    {
                        if (value.Suppressed < int.MaxValue)
                        {
                            value.Suppressed++;
                        }
                        return;
                    }
                    FlushRepeated(source, value);
                }
                else
                {
                    if (repeated.Count >= 64)
                    {
                        repeated.Clear();
                    }
                    value = new RepeatedFailure();
                    repeated[source] = value;
                }
                value.Message = message;
                value.Next = now + TimeSpan.FromMinutes(1.0).Ticks;
                Error(message);
            }
        }

        private static void FlushRepeated(string source, RepeatedFailure state)
        {
            if (state.Suppressed > 0)
            {
                Warn(Format("{0} | 重复次数={1} | {2}", "{0} | repeated={1} | {2}", source, state.Suppressed, state.Message));
            }
            state.Suppressed = 0;
        }

        internal static void RepeatedRecovered(string source)
        {
            lock (lk)
            {
                RepeatedFailure value;
                if (writesSuspendedForReset || !writesEnabled)
                {
                    repeated.Remove(source);
                }
                else if (repeated.TryGetValue(source, out value))
                {
                    FlushRepeated(source, value);
                    repeated.Remove(source);
                    Info(Format("{0} | 已恢复", "{0} | recovered", source));
                }
            }
        }

        public static void Log(string msg)
        {
            try
            {
                lock (lk)
                {
                    if (writesSuspendedForReset || !writesEnabled)
                    {
                        return;
                    }
                    msg = LogText.Record(msg, Lang.DiagnosticLanguage);
                    string newLineOffset = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + (msg ?? "").Replace("\r\n", " ⏎ ").Replace("\r", " ⏎ ").Replace("\n", " ⏎ ") + Environment.NewLine;
                    if (knownLength < 0 || !string.Equals(knownPath, LogPath, StringComparison.OrdinalIgnoreCase))
                    {
                        FileInfo fileInfo = new FileInfo(LogPath);
                        knownLength = (fileInfo.Exists ? fileInfo.Length : 0);
                        knownPath = LogPath;
                    }
                    if (knownLength > RotateBytes)
                    {
                        FileInfo fileInfo2 = new FileInfo(LogPath);
                        if (fileInfo2.Exists && fileInfo2.Length > RotateBytes)
                        {
                            string logPathOffset = LogPath + ".old";
                            try
                            {
                                if (File.Exists(logPathOffset))
                                {
                                    File.Delete(logPathOffset);
                                }
                                fileInfo2.MoveTo(logPathOffset);
                            }
                            catch
                            {
                                try
                                {
                                    fileInfo2.Delete();
                                }
                                catch
                                {
                                }
                            }
                        }
                        knownLength = 0L;
                    }
                    File.AppendAllText(LogPath, newLineOffset);
                    knownLength += Encoding.UTF8.GetByteCount(newLineOffset);
                    Interlocked.Increment(ref version);
                }
            }
            catch
            {
                lock (lk)
                {
                    knownLength = -1L;
                }
            }
        }

        public static void LogFailure(string context, Exception error)
        {
            string messageOffset = context + " " + error.GetType().Name + " - " + error.Message;
            Log(LogText.LevelTag(LogEventSeverity.Error, Lang.DiagnosticLanguage) + messageOffset);
        }

        internal static void AppendCrash(string path, string details)
        {
            try
            {
                lock (lk)
                {
                    if (!writesSuspendedForReset)
                    {
                        File.AppendAllText(path, details);
                    }
                }
            }
            catch
            {
            }
        }

        public static void Clear()
        {
            try
            {
                lock (lk)
                {
                    if (!writesSuspendedForReset)
                    {
                        File.WriteAllText(LogPath, "");
                        repeated.Clear();
                        Interlocked.Increment(ref version);
                        knownLength = 0L;
                        knownPath = LogPath;
                    }
                }
            }
            catch
            {
                lock (lk)
                {
                    knownLength = -1L;
                }
            }
        }

        internal static void ExportTo(string destination)
        {
            string output = Path.GetFullPath(destination);
            byte[] snapshot = ReadExportSnapshot(output);
            // 目标写入较慢时 不能阻塞运行日志的写入或轮换
            File.WriteAllBytes(output, snapshot);
        }

        internal static void ExportActionsTo(string destination, string activeSession)
        {
            string output = Path.GetFullPath(destination);
            byte[] snapshot = ReadExportSnapshot(output);
            string actions = ActionLogProjection.Export(Encoding.UTF8.GetString(snapshot).TrimStart('\uFEFF'), activeSession);
            File.WriteAllText(output, actions, new UTF8Encoding(false));
        }

        private static byte[] ReadExportSnapshot(string output)
        {
            byte[] snapshot;
            lock (lk)
            {
                if (string.IsNullOrEmpty(LogPath)) throw new FileNotFoundException("No runtime log is available.");
                string source = Path.GetFullPath(LogPath);
                if (string.Equals(output, source, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(output, source + ".old", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Choose a different file from the active runtime logs.");
                using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    snapshot = new byte[checked((int)input.Length)];
                    int offset = 0;
                    while (offset < snapshot.Length)
                    {
                        int count = input.Read(snapshot, offset, snapshot.Length - offset);
                        if (count == 0) throw new EndOfStreamException("The runtime log changed while it was being read.");
                        offset += count;
                    }
                }
            }
            return snapshot;
        }

        public static string Tail(int maxLines)
        {
            try
            {
                lock (lk)
                {
                    if (maxLines <= 0 || string.IsNullOrEmpty(LogPath) || !File.Exists(LogPath))
                    {
                        return "";
                    }
                    using (FileStream fileStream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long positionValue = 0L;
                        long positionValue2 = 0L;
                        long length = fileStream.Length;
                        if (length > 0)
                        {
                            byte[] rawBytes = new byte[4096];
                            int processedCount = 0;
                            long lengthSnapshot = length;
                            while (lengthSnapshot > 0 && processedCount <= maxLines)
                            {
                                int lengthSnapshotLimit = (int)Math.Min(rawBytes.Length, lengthSnapshot);
                                lengthSnapshot = (fileStream.Position = lengthSnapshot - lengthSnapshotLimit);
                                int readResult = fileStream.Read(rawBytes, 0, lengthSnapshotLimit);
                                for (int rawBytesIndex = readResult - 1; rawBytesIndex >= 0; rawBytesIndex--)
                                {
                                    long lengthSnapshotOffset = lengthSnapshot + rawBytesIndex;
                                    if (rawBytes[rawBytesIndex] == 10 && lengthSnapshotOffset != length - 1)
                                    {
                                        processedCount++;
                                        if (processedCount == maxLines)
                                        {
                                            positionValue = lengthSnapshotOffset + 1;
                                        }
                                        else if (processedCount == maxLines + 1)
                                        {
                                            positionValue2 = lengthSnapshotOffset + 1;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        fileStream.Position = positionValue;
                        string readToEndTrimmed;
                        using (StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8, true, 4096, true))
                        {
                            readToEndTrimmed = streamReader.ReadToEnd().TrimEnd('\r', '\n');
                        }
                        if (positionValue > positionValue2 && readToEndTrimmed.TrimStart().StartsWith("<Objs", StringComparison.Ordinal))
                        {
                            fileStream.Position = positionValue2;
                            using (StreamReader streamReader2 = new StreamReader(fileStream, Encoding.UTF8, true, 4096, true))
                            {
                                string line = streamReader2.ReadLine();
                                if (line != null && line.IndexOf("#< CLIXML", StringComparison.Ordinal) >= 0)
                                {
                                    readToEndTrimmed = line + Environment.NewLine + readToEndTrimmed;
                                }
                            }
                        }
                        return readToEndTrimmed;
                    }
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
