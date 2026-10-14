// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal static partial class SelfYield
    {
        private static readonly object sync = new object();
        private static bool engaged;
        private static uint origPriority;
        private static int origIo = -1;
        private static uint[] origCpuSets;

        private static IntPtr Self { get { return (IntPtr)(-1); } }

        public static bool Engaged { get { lock (sync) return engaged; } }

        public static void Engage()
        {
            lock (sync)
            {
                if (engaged) return;
                IntPtr h = Self;
                uint pri = Native.GetPriorityClass(h);
                if (pri == 0) return;
                origPriority = pri;
                origIo = Native.QueryIoPriority(h);
                origCpuSets = Native.QueryCpuSets(h);
                bool moved = false;
                uint[] yieldIds = CpuTopology.BackgroundYieldCpuSetIds();
                if (yieldIds != null && yieldIds.Length > 0)
                    moved = Native.TrySetCpuSets(h, yieldIds);
                helperCpuSets = OvernightPerformancePolicy.Enabled && moved && origCpuSets != null && Native.CpuSetsMatch(h, yieldIds)
                    ? (uint[])yieldIds.Clone() : null;
                bool lowered = false;
                if (!moved)
                    lowered = pri == Native.BELOW_NORMAL_PRIORITY_CLASS
                        || Native.SetPriorityClass(h, Native.BELOW_NORMAL_PRIORITY_CLASS);
                bool ioLowered = origIo >= 0 && origIo != 1 && Native.TrySetIoPriority(h, 1);
                engaged = lowered || moved || ioLowered;
                if (engaged) Logger.Log(Lang.T("log.selfyield.1"));
            }
        }

        public static void Release()
        {
            lock (sync)
            {
                helperCpuSets = null;
                helperRendererPid = 0; helperRendererBirth = 0;
                ReleaseHelpersLocked();
                if (!engaged) return;
                engaged = false;
                IntPtr h = Self;
                Native.RestoreCpuSetsVerified(h, origCpuSets ?? new uint[0]);
                if (origPriority != 0) Native.SetPriorityClass(h, origPriority);
                if (origIo >= 0) Native.TrySetIoPriority(h, origIo);
                Logger.Log(Lang.T("log.selfyield.2"));
            }
        }
    }
}
