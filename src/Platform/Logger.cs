// @author bdth 2074055628@qq.com
// 文件用途 记录运行日志并通知界面刷新

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
        private static long knownLength = -1;
        private static string knownPath;

        public static void Log(string msg)
        {
            try
            {
                lock (lk)
                {
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
                }
            }
            catch { knownLength = -1; }
        }

        public static void LogFailure(string context, Exception error)
        {
            string detail = context + "：" + error.GetType().Name + " - " + error.Message;
            Debug.WriteLine(detail);
            Log(detail);
        }

        public static void Clear()
        {
            try { lock (lk) { File.WriteAllText(LogPath, ""); knownLength = 0; knownPath = LogPath; } }
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
