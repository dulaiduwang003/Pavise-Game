namespace PaviseApp
{
    internal interface IOptionalServiceLedger
    {
        bool TryRead(out string value);

        bool TryWrite(string value);
    }
}
