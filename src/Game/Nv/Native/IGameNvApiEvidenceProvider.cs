namespace PaviseApp
{
    internal interface IGameNvApiEvidenceProvider
    {
        GameNvApiEvidence Observe(string normalizedExecutable, string currentExecutableHash);
    }
}
