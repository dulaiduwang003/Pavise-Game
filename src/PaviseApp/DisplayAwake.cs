using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class DisplayAwake
    {

        private static readonly object lk = new object();

        private static bool active;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint flags);

        public static bool Activate()
        {
            return Activate(true);
        }

        public static bool Restore()
        {
            return Restore(true);
        }

        internal static bool ActivateForSession()
        {
            return Activate(false);
        }

        internal static bool RestoreForSession()
        {
            return Restore(false);
        }

        private static bool Activate(bool logEdge)
        {
            lock (lk)
            {
                if (active)
                {
                    return true;
                }
                if (ChangeExecutionState(2147483651u) == 0)
                {
                    return false;
                }
                active = true;
                if (logEdge)
                {
                    Logger.Log(Lang.T("log.displayawake.1", 0));
                }
                return true;
            }
        }

        private static bool Restore(bool logEdge)
        {
            lock (lk)
            {
                if (!active)
                {
                    return true;
                }
                if (ChangeExecutionState(2147483648u) == 0)
                {
                    return false;
                }
                active = false;
                if (logEdge)
                {
                    Logger.Log(Lang.T("log.displayawake.2", 0));
                }
                return true;
            }
        }

        private static uint ChangeExecutionState(uint flags)
        {
            return SetThreadExecutionState(flags);
        }
    }
}
