using System;

namespace PaviseApp
{
    internal interface IOptionalServiceControl
    {
        bool TryGetBootIdentity(out string identity);

        bool TryQuery(string name, out OptionalServiceSnapshot snapshot);

        bool IsPrintingIdle();

        bool TryStop(string name, OptionalServiceSnapshot expected, Func<bool> mayContinue);

        OptionalServiceStartResult TryStart(string name, string expectedConfiguration);
    }
}
