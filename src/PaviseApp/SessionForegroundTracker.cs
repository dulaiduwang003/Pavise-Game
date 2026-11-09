namespace PaviseApp
{
    internal sealed class SessionForegroundTracker
    {
        internal const int SwitchOutConfirmScans = 2;

        private int misses;

        internal SessionForegroundState State { get; private set; }

        internal long SwitchedOutSinceTicks { get; private set; }

        internal int SwitchOuts { get; private set; }

        internal bool Observe(bool gameForeground, bool foregroundKnown, long nowTicks)
        {
            if (!foregroundKnown)
            {
                return false;
            }
            if (gameForeground)
            {
                misses = 0;
                if (State == SessionForegroundState.Game)
                {
                    return false;
                }
                State = SessionForegroundState.Game;
                SwitchedOutSinceTicks = 0L;
                return true;
            }
            if (State == SessionForegroundState.SwitchedOut)
            {
                return false;
            }
            misses++;
            if (misses < 2)
            {
                return false;
            }
            State = SessionForegroundState.SwitchedOut;
            SwitchedOutSinceTicks = nowTicks;
            SwitchOuts++;
            return true;
        }

        internal bool DebounceElapsed(long nowTicks, long debounceTicks)
        {
            if (State == SessionForegroundState.SwitchedOut && SwitchedOutSinceTicks > 0 && nowTicks >= SwitchedOutSinceTicks)
            {
                return nowTicks - SwitchedOutSinceTicks >= debounceTicks;
            }
            return false;
        }

        internal void Reset()
        {
            misses = 0;
            State = SessionForegroundState.Unknown;
            SwitchedOutSinceTicks = 0L;
        }
    }
}
