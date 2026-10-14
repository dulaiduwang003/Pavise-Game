using System;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal static class SystemRestart
    {
#if PAVISE_SELFTEST
        internal static Func<ProcessStartInfo, bool> RequestForTest;
#endif
        internal static bool Request()
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = "/r /f /t 0", UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
#if PAVISE_SELFTEST
            return RequestForTest != null && RequestForTest(start);
#else
            try
            {
                using (Process process = Process.Start(start))
                    return process != null && process.WaitForExit(5000) && process.ExitCode == 0;
            }
            catch (Exception error)
            {
                Logger.LogFailure("Windows restart request", error);
                return false;
            }
#endif
        }
    }
}
