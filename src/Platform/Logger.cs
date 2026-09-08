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
        // 每次落盘递增 日志页比对它 没新内容就不再拿着同一把锁去读文件
        private static long version;
        public static long Version { get { return System.Threading.Interlocked.Read(ref version); } }
        private static long knownLength = -1;
        private static string knownPath;
        private static bool writesSuspendedForReset;
        // 用户可关的运行日志开关 和 writesSuspendedForReset 分开
        //   后者是重置流程的一次性写屏障 只有新进程才解除
        //   这个是常态设置 随时可来回切 关闭期间不落盘也不排队补写
        //   崩溃转储走 AppendCrash 另一条路 不受此开关影响 出事时必须留下现场
        private static bool writesEnabled = true;

        public const string WritesEnabledKey = "LogWritesEnabled";

        public static bool WritesEnabled
        {
            get { lock (lk) return writesEnabled; }
            set { lock (lk) writesEnabled = value; }
        }

        // 重置删文件之前 同一把锁会把追加 轮转和清空的活排干
        // 尾部仍然可读 通常要等新进程起来才重新允许写入
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

        // 分级标记 落在时间戳之后 日志页按标记分级 没有标记的行仍按词表判 见 LogStreamView.Classify
        //   WARN 环境限制 本机没有某个部件 权限或反作弊拦住 精简系统缺组件 换台机器就没事的那种
        //   FAIL 功能性故障 写入 还原 落盘没成 抛了异常 数据有风险 需要有人看的那种
        public const string WarnTag = "WARN ";
        public const string FailTag = "FAIL ";

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
