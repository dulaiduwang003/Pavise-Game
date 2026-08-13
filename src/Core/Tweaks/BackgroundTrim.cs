// @author bdth 2074055628@qq.com
// 文件用途 对局稳定后把已被压制的后台进程工作集交还系统 游戏 前台和白名单一个不碰

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static class BackgroundTrim
    {
        public const int DelaySeconds = 30;
        public const int MaxProcesses = 200;
        public const int BudgetMs = 3000;
        private const int BatchSize = 8;
        private const int BatchPauseMs = 15;
        private const int ThreadModeBackgroundBegin = 0x00010000;

        private static readonly object lk = new object();
        private static Thread worker;
        private static volatile bool cancel;

        internal static List<int> Targets(IEnumerable<int> background, int foregroundPid,
            ICollection<int> gamePids, int selfPid)
        {
            var picked = new List<int>();
            if (background == null) return picked;
            var seen = new HashSet<int>();
            foreach (int pid in background)
            {
                if (pid <= 4 || pid == selfPid || pid == foregroundPid) continue;
                if (gamePids != null && gamePids.Contains(pid)) continue;
                if (!seen.Add(pid)) continue;
                picked.Add(pid);
                if (picked.Count >= MaxProcesses) break;
            }
            return picked;
        }

        public static void Begin(List<int> targets)
        {
            if (targets == null || targets.Count == 0) return;
            lock (lk)
            {
                if (worker != null && worker.IsAlive) return;
                cancel = false;
                worker = new Thread(delegate () { Trim(targets); });
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

        private static void Trim(List<int> targets)
        {
            try
            {
                Native.SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
                var sw = Stopwatch.StartNew();
                int done = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    if (cancel || sw.ElapsedMilliseconds >= BudgetMs) break;
                    IntPtr h = Native.OpenProcess(Native.PROCESS_SET_QUOTA, false, targets[i]);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        if (Native.SetProcessWorkingSetSize(h, (IntPtr)(-1), (IntPtr)(-1))) done++;
                    }
                    catch { }
                    finally { Native.CloseHandle(h); }
                    if ((i + 1) % BatchSize == 0) Thread.Sleep(BatchPauseMs);
                }
                sw.Stop();
                if (done > 0)
                    Logger.Log("后台内存归还 " + done + " 个后台进程的工作集已交还系统 用时 "
                        + sw.ElapsedMilliseconds + " 毫秒 内存回到待机列表 游戏要用随时能拿"
                        + (cancel ? " 中途取消" : ""));
            }
            catch (Exception ex)
            {
                Logger.Log("后台内存归还 中止 " + ex.Message + " ");
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
    }
}
