namespace PaviseApp
{
    internal interface IPrivacyBackend
    {
        string Read(string action, string target);

        bool Write(string action, string target, string value);
    }
}
