namespace PaviseApp
{
    internal interface IHardThreadSettledStore
    {
        HardThreadSettledSource CaptureSettled(HardThreadTarget target, long revision);
    }
}
