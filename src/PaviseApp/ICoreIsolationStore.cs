namespace PaviseApp
{
    internal interface ICoreIsolationStore
    {
        bool Read(out string value);

        bool Write(string value);
    }
}
