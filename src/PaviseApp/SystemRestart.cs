using System;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal static class SystemRestart
    {
        internal static bool Request()
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            startInfo.Arguments = "/r /f /t 0";
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            try
            {
                using (Process process = Process.Start(startInfo))
                {
                    return process != null && process.WaitForExit(5000) && process.ExitCode == 0;
                }
            }
            catch (Exception error)
            {
                Logger.LogFailure("Windows 重启请求", error);
                return false;
            }
        }
    }
}
