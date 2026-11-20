using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PaviseApp
{
    internal static class CriticalThreadRuntimePolicy
    {
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
