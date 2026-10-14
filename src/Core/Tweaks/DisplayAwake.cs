// 作者：bdth 2074055628@qq.com
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class DisplayAwake
    {
        private const uint EsContinuous = 0x80000000;
        private const uint EsDisplayRequired = 0x00000002;
        private const uint EsSystemRequired = 0x00000001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint flags);

        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                uint previous = ChangeExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired);
                if (previous == 0) return false;
                active = true;
                if (!AutomaticPolicy.Enabled) Logger.Log(Lang.T("log.displayawake.1"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (!active) return true;
                uint previous = ChangeExecutionState(EsContinuous);
                if (previous == 0) return false;
                active = false;
                if (!AutomaticPolicy.Enabled) Logger.Log(Lang.T("log.displayawake.2"));
                return true;
            }
        }

        private static uint ChangeExecutionState(uint flags)
        {
#if PAVISE_SELFTEST
            if (ExecutionStateForTest == null)
                throw new InvalidOperationException("DisplayAwake requires an injected execution-state test double.");
            return ExecutionStateForTest(flags);
#else
            return SetThreadExecutionState(flags);
#endif
        }

#if PAVISE_SELFTEST
        internal static Func<uint, uint> ExecutionStateForTest;
        internal static void ResetForTest()
        {
            lock (lk) { active = false; ExecutionStateForTest = null; }
        }
#endif
    }
}
