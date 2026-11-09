using System;
using System.IO;

namespace PaviseApp
{
    internal sealed class HardThreadGuardianLaunchBlockedException : IOException
    {
        internal HardThreadGuardianLaunchBlockedException(string detail)
            : base(detail)
        {
        }

        internal static bool Contains(Exception error)
        {
            if (error == null)
            {
                return false;
            }
            if (error is HardThreadGuardianLaunchBlockedException)
            {
                return true;
            }
            AggregateException ex = error as AggregateException;
            if (ex != null)
            {
                foreach (Exception innerException in ex.InnerExceptions)
                {
                    if (Contains(innerException))
                    {
                        return true;
                    }
                }
            }
            return Contains(error.InnerException);
        }
    }
}
