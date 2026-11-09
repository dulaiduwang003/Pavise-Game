using System;

namespace PaviseApp
{
    internal sealed class GameInputWindow
    {
        internal readonly IntPtr Foreground;

        internal readonly IntPtr Focus;

        internal readonly uint ThreadId;

        internal readonly int Pid;

        internal GameInputWindow(IntPtr foreground, IntPtr focus, uint threadId, int pid)
        {
            Foreground = foreground;
            Focus = focus;
            ThreadId = threadId;
            Pid = pid;
        }

        internal bool SameAs(GameInputWindow other)
        {
            if (other != null && Foreground == other.Foreground && Focus == other.Focus && ThreadId == other.ThreadId)
            {
                return Pid == other.Pid;
            }
            return false;
        }
    }
}
