using System.Collections.Generic;

namespace PaviseApp
{
    internal static class CriticalThreadRuntimePolicy
    {
        internal static bool ValidShape(ThreadTimes sample)
        {
            if (sample != null && sample.Ids != null && sample.Created != null && sample.Ids.Length <= 16384 && sample.Ids.Length == sample.Created.Length)
            {
                return sample.Cpu == null || sample.Ids.Length == sample.Cpu.Length;
            }
            return false;
        }

        internal static bool Valid(ThreadTimes sample, long processBirth, long now)
        {
            if (!ValidShape(sample) || sample.Creation != processBirth || processBirth <= 0 || sample.Ids.Length == 0 || sample.ProcessCpu < 0 || sample.Ticks <= 0 || now < sample.Ticks || now - sample.Ticks > 15000000)
            {
                return false;
            }
            HashSet<long> hashSet = new HashSet<long>();
            for (int i = 0; i < sample.Ids.Length; i++)
            {
                if (sample.Ids[i] <= 0 || sample.Ids[i] > int.MaxValue || sample.Created[i] <= 0 || sample.Cpu != null && sample.Cpu[i] < 0 || !hashSet.Add(sample.Ids[i]))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool Contains(ThreadTimes sample, HardThreadIdentity thread)
        {
            int processedCount = 0;
            while (sample != null && processedCount < sample.Ids.Length)
            {
                if (sample.Ids[processedCount] == thread.Tid && sample.Created[processedCount] == thread.Creation)
                {
                    return true;
                }
                processedCount++;
            }
            return false;
        }

    }
}
