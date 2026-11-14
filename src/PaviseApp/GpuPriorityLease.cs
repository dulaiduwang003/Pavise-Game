using System;
using System.Globalization;

namespace PaviseApp
{
    // 原值快照不等于写入归属。P 为写前意图，A 为调用已成功，S 为已让出或已恢复。
    internal sealed class GpuPriorityLease
    {
        internal char Phase = 'N';
        internal int Previous = -1;
        internal int Applied = -1;
        internal bool PendingCheckpoint;

        internal static GpuPriorityLease Legacy(int original)
        {
            return original < 0 ? new GpuPriorityLease() : new GpuPriorityLease { Phase = 'L', Previous = original };
        }

        internal string Encode()
        {
            return Phase + "," + Previous.ToString(CultureInfo.InvariantCulture) + "," + Applied.ToString(CultureInfo.InvariantCulture);
        }

        internal static GpuPriorityLease Parse(string value)
        {
            string[] parts = (value ?? "").Split(',');
            int previous, applied;
            if (parts.Length != 3 || parts[0].Length != 1
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out previous)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out applied)) return null;
            char phase = parts[0][0];
            if (phase == 'N' || phase == 'S')
            { if (previous != -1 || applied != -1) return null; }
            else if (phase == 'L')
            { if (!Valid(previous) || applied != -1) return null; }
            else if (phase == 'P' || phase == 'A')
            { if (!Valid(previous) || !Valid(applied) || previous == applied) return null; }
            else return null;
            return new GpuPriorityLease { Phase = phase, Previous = previous, Applied = applied };
        }

        private static bool Valid(int value) { return value >= 0 && value <= 5; }
        private static bool Read(IntPtr handle, out int value)
        { return Native.D3DKMTGetProcessSchedulingPriorityClass(handle, out value) == 0 && Valid(value); }

        private bool Settle(Func<bool> checkpoint)
        {
            Phase = 'S'; Previous = Applied = -1;
            // 保存失败仍保留内存中的已结清状态，下一次只补写回执，不再次覆盖外部值。
            return SaveCheckpoint(checkpoint);
        }

        private bool SaveCheckpoint(Func<bool> checkpoint)
        {
            PendingCheckpoint = true;
            bool saved = checkpoint();
            PendingCheckpoint = !saved;
            return saved;
        }

        internal bool Apply(IntPtr handle, int desired, Func<int> identity, Func<bool> checkpoint, out bool wrote)
        {
            wrote = false;
            if (!Valid(desired) || identity() != 0) return false;
            int current;
            if (!Read(handle, out current)) return false;
            if (current == desired) return !PendingCheckpoint || SaveCheckpoint(checkpoint);
            if (Phase == 'L' || Phase == 'P') return false;
            char oldPhase = Phase; int oldPrevious = Previous, oldApplied = Applied;
            Previous = Phase == 'A' && current == Applied ? Previous : current;
            Applied = desired; Phase = 'P';
            if (Previous == Applied)
            {
                // 取消治理目标时走恢复，不能创建原值与目标相等的租约。
                Phase = oldPhase; Previous = oldPrevious; Applied = oldApplied;
                return Restore(handle, identity, checkpoint);
            }
            if (!SaveCheckpoint(checkpoint))
            {
                Phase = oldPhase; Previous = oldPrevious; Applied = oldApplied;
                SaveCheckpoint(checkpoint);
                return false;
            }
            int beforeWrite;
            if (identity() != 0 || !Read(handle, out beforeWrite) || beforeWrite != current)
            {
                Phase = oldPhase; Previous = oldPrevious; Applied = oldApplied;
                SaveCheckpoint(checkpoint);
                return false;
            }
            int status = Native.D3DKMTSetProcessSchedulingPriorityClass(handle, desired);
            if (status != 0)
            {
                // API 明确拒绝时没有新的已写归属；旧租约继续保留。
                Phase = oldPhase; Previous = oldPrevious; Applied = oldApplied;
                SaveCheckpoint(checkpoint);
                return false;
            }
            wrote = true; Phase = 'A';
            bool saved = SaveCheckpoint(checkpoint);
            int actual;
            return saved && identity() == 0 && Read(handle, out actual) && actual == desired;
        }

        internal bool Restore(IntPtr handle, Func<int> identity, Func<bool> checkpoint)
        {
            int state = identity();
            if (state == 1) return Settle(checkpoint);
            if (state != 0) return false;
            if (Phase == 'N') return !PendingCheckpoint || SaveCheckpoint(checkpoint);
            if (Phase == 'S') return SaveCheckpoint(checkpoint);
            int current;
            if (!Read(handle, out current)) return false;
            if (Phase == 'L')
            {
                // 旧回执只有快照，不能证明当前不同值来自本方；原值已恢复时才结清。
                return current == Previous && Settle(checkpoint);
            }
            if (current == Previous || current != Applied) return Settle(checkpoint);
            // 写前日志不能证明 API 已执行。崩溃后仅剩意图且当前恰等目标时，保留未知债务。
            if (Phase == 'P') return false;
            state = identity();
            if (state == 1) return Settle(checkpoint);
            if (state != 0 || !Read(handle, out current)) return false;
            if (current == Previous || current != Applied) return Settle(checkpoint);
            if (Native.D3DKMTSetProcessSchedulingPriorityClass(handle, Previous) != 0) return false;
            int actual;
            state = identity();
            if (state == 1) return Settle(checkpoint);
            if (state != 0 || !Read(handle, out actual) || actual != Previous) return false;
            return Settle(checkpoint);
        }
    }
}
