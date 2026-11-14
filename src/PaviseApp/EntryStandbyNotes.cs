using System;

namespace PaviseApp
{
    // 限频记录待命与识别循环停顿，不参与入场判定。
    internal sealed class EntryStandbyNotes
    {
        internal const int MaximumNotes = 3, MaximumStallNotes = 3;
        internal const long StallMs = 30000;
        private static readonly long[] DueMs = { 60000, 300000, 900000 };

        internal enum Decision { None, Note, Stall }

        private string name;
        private long sinceMs, lastTickMs;
        private int notes, stallNotes;

        // 无待命目标时清除计时；输出分别为待命时长和本轮停顿时长。
        internal Decision Tick(string armed, long nowMs, out long waitedMs, out long stalledMs)
        {
            waitedMs = 0; stalledMs = 0;
            if (armed == null) { name = null; return Decision.None; }
            if (!string.Equals(armed, name, StringComparison.OrdinalIgnoreCase))
            {
                name = armed; sinceMs = nowMs; lastTickMs = nowMs;
                notes = 0; stallNotes = 0;
                return Decision.None;
            }
            long gap = nowMs - lastTickMs;
            lastTickMs = nowMs;
            waitedMs = nowMs - sinceMs;
            if (gap >= StallMs && stallNotes < MaximumStallNotes) { stallNotes++; stalledMs = gap; return Decision.Stall; }
            if (notes < MaximumNotes && waitedMs >= DueMs[Math.Min(notes, DueMs.Length - 1)]) { notes++; return Decision.Note; }
            return Decision.None;
        }
    }
}
