namespace PaviseApp
{
    internal sealed class ProcEntry
    {
        public int Pid;

        public int ParentPid;

        public int Session = -1;

        public long Creation;

        public long Cpu;

        public string Name;

        public string Path;
    }
}
