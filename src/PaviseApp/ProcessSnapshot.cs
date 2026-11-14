using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class ProcessSnapshot
    {
        public readonly ProcEntry[] Entries;

        public readonly Dictionary<int, ProcEntry> ByPid;

        public readonly long TakenTicks;

        public readonly long MonotonicTicks;

        public int Count
        {
            get
            {
                return Entries.Length;
            }
        }

        internal ProcessSnapshot(ProcEntry[] entries)
        {
            Entries = entries ?? new ProcEntry[0];
            TakenTicks = DateTime.UtcNow.Ticks;
            MonotonicTicks = AutoCores.NowTicks;
            ByPid = new Dictionary<int, ProcEntry>(Entries.Length);
            ProcEntry[] entries2 = Entries;
            foreach (ProcEntry procEntry in entries2)
            {
                ByPid[procEntry.Pid] = procEntry;
            }
        }

        public ProcEntry Find(int pid)
        {
            ProcEntry value;
            if (!ByPid.TryGetValue(pid, out value))
            {
                return null;
            }
            return value;
        }
    }
}
