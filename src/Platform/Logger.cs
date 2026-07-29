// @author bdth 2074055628@qq.com
// 文件用途 记录运行日志并通知界面刷新

using System;
using System.Diagnostics;
using System.IO;

namespace AegisApp
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
    }

}
