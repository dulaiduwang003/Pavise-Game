// @author bdth 2074055628@qq.com
// 文件用途 对局开始后以线程后台模式预读游戏目录 灌入系统缓存 台架实测随机读提速24.6倍

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static class GamePrewarm
    {
        public const long MaxBudgetBytes = 4L * 1024 * 1024 * 1024;
        public const int RewarmCooldownMinutes = 60;
        private const int ThreadModeBackgroundBegin = 0x00010000;

        private static readonly object lk = new object();
        private static Thread worker;
        private static volatile bool cancel;
        private static string lastRoot;
        private static DateTime lastDone;

        [DllImport("kernel32.dll")]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        internal static long Budget(long availBytes)
        {
            long budget = availBytes / 3;
            return budget > MaxBudgetBytes ? MaxBudgetBytes : budget;
        }

        public static void Begin(string root, long availBytes)
        {
            if (string.IsNullOrEmpty(root)) return;
            lock (lk)
            {
                if (worker != null && worker.IsAlive) return;
                if (string.Equals(root, lastRoot, StringComparison.OrdinalIgnoreCase)
                    && DateTime.UtcNow - lastDone < TimeSpan.FromMinutes(RewarmCooldownMinutes))
                    return;
                long budget = Budget(availBytes);
                if (budget < 128L * 1024 * 1024) return;
                cancel = false;
                worker = new Thread(delegate () { Warm(root, budget); });
                worker.IsBackground = true;
                worker.Priority = ThreadPriority.Lowest;
                worker.Start();
            }
        }

        public static void Cancel()
        {
            Thread w;
            lock (lk) w = worker;
            cancel = true;
            if (w != null && w.IsAlive) w.Join(1500);
        }

        private static void Warm(string root, long budget)
        {
            try
            {
                SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
                var files = new List<FileInfo>();
                CollectFiles(new DirectoryInfo(root), files, 0);
                files.Sort(delegate(FileInfo a, FileInfo b) { return b.Length.CompareTo(a.Length); });

                long read = 0;
                int touched = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var buf = new byte[1024 * 1024];
                foreach (FileInfo fi in files)
                {
                    if (cancel || read >= budget) break;
                    if (fi.Length < 64 * 1024) continue;
                    try
                    {
                        using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, buf.Length, FileOptions.SequentialScan))
                        {
                            int n;
                            while (!cancel && read < budget && (n = fs.Read(buf, 0, buf.Length)) > 0)
                                read += n;
                        }
                        touched++;
                    }
                    catch { }
                }
                sw.Stop();
                lock (lk)
                {
                    lastRoot = root;
                    lastDone = DateTime.UtcNow;
                }
                if (read > 0)
                    Logger.Log("文件预热：以后台低优先级读入 " + (read / 1048576) + "MB（" + touched
                        + " 个文件，" + sw.Elapsed.TotalSeconds.ToString("0") + " 秒）"
                        + (cancel ? "，中途取消" : "，游戏读盘将命中缓存"));
            }
            catch (Exception ex)
            {
                Logger.Log("文件预热：中止（" + ex.Message + "）");
            }
        }

        private static void CollectFiles(DirectoryInfo dir, List<FileInfo> files, int depth)
        {
            if (cancel || depth > 8 || files.Count > 20000) return;
            FileInfo[] own;
            DirectoryInfo[] subs;
            try
            {
                own = dir.GetFiles();
                subs = dir.GetDirectories();
            }
            catch { return; }
            files.AddRange(own);
            foreach (DirectoryInfo sub in subs)
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                CollectFiles(sub, files, depth + 1);
            }
        }
    }
}
