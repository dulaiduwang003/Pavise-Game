namespace PaviseApp
{
    internal interface IPrivacyBackend
    {
        PrivacyPlan Inspect(string action, string selectedTarget);

        string Read(string action, string target);

        bool Write(string action, string target, string value);
    }
}
