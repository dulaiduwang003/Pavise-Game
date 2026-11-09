namespace PaviseApp
{
    internal interface IHardThreadTransientReads
    {
        bool ReleaseUnowned(string[] retainedIdentities);
    }
}
