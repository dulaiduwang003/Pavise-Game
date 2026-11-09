namespace PaviseApp
{
    internal sealed class FrameThreadReceipt
    {
        internal int Pid;

        internal int Tid;

        internal int Original;

        internal int Applied = 1;

        internal long ProcessBirth;

        internal long ThreadBirth;

        internal string Encode()
        {
            return Pid + "|" + ProcessBirth + "|" + Tid + "|" + ThreadBirth + "|" + Original;
        }
    }
}
