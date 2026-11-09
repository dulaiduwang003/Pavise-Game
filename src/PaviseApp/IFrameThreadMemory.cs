namespace PaviseApp
{
    internal interface IFrameThreadMemory
    {
        bool UnderPressure(int pid, long birth);

        int ReadMemory(FrameThreadReceipt identity, out int priority);

        bool SetMemory(FrameThreadReceipt identity, int expected, int desired);
    }
}
