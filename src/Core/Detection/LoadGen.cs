// @author bdth 2074055628@qq.com
// 文件用途 造一段内存带宽压力 让设备中断的落点和时长在观测窗口内显出真实形态
using System;
using System.Threading;

namespace PaviseApp
{
    internal sealed class LoadGen : IDisposable
    {
        private volatile bool running;
        private Thread[] threads;
        private readonly object gate = new object();

        private const int FallbackL3Mb = 32;

        internal static int PerThreadMb()
        {
            int l3Mb = 0;
            try { l3Mb = CpuTopology.L3CacheMb(); } catch { }
            if (l3Mb <= 0) l3Mb = FallbackL3Mb;
            int mb = l3Mb * 2;
            if (mb < 16) mb = 16;
            if (mb > 64) mb = 64;
            return mb;
        }

        internal static int WorkerCount()
        {
            int n = Environment.ProcessorCount - 2;
            if (n < 1) n = 1;
            if (n > 16) n = 16;
            return n;
        }

        public bool Running { get { return running; } }

        public void Start()
        {
            lock (gate)
            {
                if (running) return;
                int workers = WorkerCount();
                int mb = PerThreadMb();
                running = true;
                threads = new Thread[workers];
                for (int i = 0; i < workers; i++)
                {
                    var t = new Thread(delegate () { Stream(mb); });
                    t.IsBackground = true;
                    t.Priority = ThreadPriority.BelowNormal;
                    t.Start();
                    threads[i] = t;
                }
                Logger.Log(Lang.F("log.loadgen.1", workers, mb));
            }
        }

        public void Stop()
        {
            Thread[] local;
            lock (gate)
            {
                if (!running) return;
                running = false;
                local = threads;
                threads = null;
            }
            if (local == null) return;
            foreach (Thread t in local) { try { t.Join(1500); } catch { } }
        }

        private void Stream(int mb)
        {
            double[] buf;
            try { buf = new double[Math.Max(1, mb * 1024 * 1024 / 8)]; }
            catch (OutOfMemoryException)
            {
                Logger.Log(Lang.T("log.loadgen.2"));
                return;
            }
            double acc = 1.0;
            while (running)
            {
                for (int i = 0; i < buf.Length; i += 8)
                {
                    if (!running) break;
                    acc += buf[i] * 0.5 + 1.0;
                    buf[i] = acc;
                }
            }
            GC.KeepAlive(acc);
        }

        public void Dispose() { Stop(); }
    }
}
