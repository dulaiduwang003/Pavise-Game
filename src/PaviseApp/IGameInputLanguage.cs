using System;

namespace PaviseApp
{
    internal interface IGameInputLanguage
    {
        EnglishInputOutcome TryOnce(GameInputProcess process, Func<bool> claim, Func<bool> mayContinue);

        GameInputProcessState Probe(GameInputProcess process);
    }
}
