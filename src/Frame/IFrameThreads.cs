namespace PaviseApp
{
    internal interface IFrameThreads
    {
        int Read(FrameThreadReceipt identity, out int priority);

        bool Set(FrameThreadReceipt identity, int expected, int desired);
    }
}
