using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class LoopProfile
    {
        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [Conditional("PAVISE_LOOP_PROFILE")]
        internal static void Tag(string name)
        {
            Logger.Log(() => Logger.Format("线程 {0} 线程编号={1}", "THREAD {0} tid={1}", name, GetCurrentThreadId()));
        }
    }
}
