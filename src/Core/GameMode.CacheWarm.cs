// @author bdth 2074055628@qq.com
// 文件用途 缓存预热的会话编排 对局稳定后一局一次 在独立低优先级线程上预读游戏资产
using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // 比自动节能显卡再晚半分钟 加载和大厅切换的读盘高峰要彻底过去
        private const int CacheWarmDelaySeconds = 90;

        private volatile bool cacheWarmDone;
        private int cacheWarmBusy;

        private void MaybeWarmCache()
        {
            if (!EffCacheWarm || cacheWarmDone || stopping || panicReq) return;
            long start = Interlocked.Read(ref sessionStartTicks);
            if (start == 0 || DateTime.UtcNow.Ticks - start
                < CacheWarmDelaySeconds * TimeSpan.TicksPerSecond) return;
            string root = null;
            lock (sync)
            {
                GameDetection detection = activeDetection;
                if (detection != null && detection.Profile != null) root = detection.Profile.Root;
            }
            // 待机清理生效的对局不预热 清理器阈值一到会把预热的页整锅倒掉
            //   两个一起开就是"花钱装满 按钮清空"的循环 清理治的是内存压力 它优先
            PolicySnapshot cleaner = sessionPolicy;
            if (cleaner != null ? cleaner.StandbyCleaner : standbyCleanerOn)
            {
                cacheWarmDone = true;
                Logger.Log(Lang.T("log.cachewarm.9"));
                return;
            }
            // 只认游戏库档案给出的可靠目录 目录不可靠宁可整局不做 也不预读错地方
            if (!SafeFamilyDir(root))
            {
                cacheWarmDone = true;
                Logger.Log(Lang.T("log.cachewarm.4"));
                return;
            }
            if (Interlocked.CompareExchange(ref cacheWarmBusy, 1, 0) != 0) return;
            cacheWarmDone = true;
            string dir = root;
            // 中止条件要带会话身份 直接换局不经过 Deactivate active 全程为真
            //   没有这一条 上一局的预热会拿着旧目录在新对局里继续读 还占着 busy 名额
            long sessionStamp = start;
            var worker = new Thread(delegate()
            {
                try
                {
                    CacheWarm.Run(dir, delegate
                    {
                        return stopping || panicReq || !Volatile.Read(ref active) || !EffCacheWarm
                            || Interlocked.Read(ref sessionStartTicks) != sessionStamp;
                    });
                }
                catch { }
                finally { Interlocked.Exchange(ref cacheWarmBusy, 0); }
            });
            worker.IsBackground = true;
            worker.Priority = ThreadPriority.BelowNormal;
            worker.Name = "PaviseCacheWarm";
            try { worker.Start(); }
            catch { Interlocked.Exchange(ref cacheWarmBusy, 0); }
        }
    }
}
