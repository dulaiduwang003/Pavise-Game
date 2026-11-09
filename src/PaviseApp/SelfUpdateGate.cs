using System.Threading;

namespace PaviseApp
{
    // 文件暂存期间，由唯一进程内所有者阻止退出、重置和重叠更新。
    internal static class SelfUpdateGate
    {
        private static int busy;

        internal static bool IsBusy { get { return Volatile.Read(ref busy) != 0; } }

        internal static bool TryBegin()
        {
            return Interlocked.CompareExchange(ref busy, 1, 0) == 0;
        }

        internal static void End()
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }
}
