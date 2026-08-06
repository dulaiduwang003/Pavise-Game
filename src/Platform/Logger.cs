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
        private static readonly object lk = new object();
        public static string LogPath;

        public static void Log(string msg)
        {
            try
            {
                lock (lk)
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = LogPath + ".old";
                        try
                        {
                            if (File.Exists(old)) File.Delete(old);
                            fi.MoveTo(old);
                        }
                        catch { try { fi.Delete(); } catch { } }
                    }
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }

        public static void LogFailure(string context, Exception error)
        {
            string detail = context + "：" + error.GetType().Name + " - " + error.Message;
            Debug.WriteLine(detail);
            Log(detail);
        }

        public static void Clear()
        {
            try { lock (lk) File.WriteAllText(LogPath, ""); } catch { }
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
