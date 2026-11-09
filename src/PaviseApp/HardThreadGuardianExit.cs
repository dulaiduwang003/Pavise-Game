using System;

namespace PaviseApp
{
    internal static class HardThreadGuardianExit
    {
        internal static void CloseForExit(IDisposable original, ref int exitCode)
        {
            if (original == null)
            {
                return;
            }
            try
            {
                original.Dispose();
            }
            catch
            {
                exitCode = 14;
            }
        }
    }
}
