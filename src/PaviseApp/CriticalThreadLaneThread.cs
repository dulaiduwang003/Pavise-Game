namespace PaviseApp
{
    internal sealed class CriticalThreadLaneThread
    {
        internal readonly int Pid;

        internal readonly int Tid;

        internal readonly long ProcessBirth;

        internal readonly long ThreadBirth;

        internal CriticalThreadLaneThread(int pid, long processBirth, int tid, long threadBirth)
        {
            Pid = pid;
            ProcessBirth = processBirth;
            Tid = tid;
            ThreadBirth = threadBirth;
        }
    }
}
