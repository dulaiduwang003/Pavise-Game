using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    // 日志行交给线程池去写 调用方不用等磁盘
    // 最多排 capacity 行 满了先丢最早的 连写日志的工作都排不进线程池时 排着的行直接丢掉
    internal sealed class BackgroundLogQueue
    {
        private readonly object gate = new object();
        private readonly Queue<string> lines = new Queue<string>();
        private readonly int capacity;
        private int busy;

        internal BackgroundLogQueue(int capacity) { this.capacity = capacity; }

        internal void Enqueue(string item)
        {
            lock (gate)
            {
                if (lines.Count >= capacity)
                {
                    lines.Dequeue();
                }
                lines.Enqueue(item);
                if (busy != 0)
                {
                    return;
                }
                busy = 1;
                try
                {
                    if (ThreadPool.QueueUserWorkItem(delegate
                    {
                        Drain();
                    }))
                    {
                        return;
                    }
                }
                catch
                {
                }
                busy = 0;
                lines.Clear();
            }
        }

        private void Drain()
        {
            while (true)
            {
                string msg;
                lock (gate)
                {
                    if (lines.Count == 0)
                    {
                        busy = 0;
                        break;
                    }
                    msg = lines.Dequeue();
                }
                Logger.Log(msg);
            }
        }

#if PAVISE_SELFTEST
        internal object GateForTest { get { return gate; } }
        internal Queue<string> LinesForTest { get { return lines; } }
        internal int BusyForTest { get { return Volatile.Read(ref busy); } }
#endif
    }
}
