using System;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
	internal static class SystemRestart
	{
		internal static bool Request()
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo();
			processStartInfo.FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
			processStartInfo.Arguments = "/r /f /t 0";
			processStartInfo.UseShellExecute = false;
			processStartInfo.CreateNoWindow = true;
			processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
			ProcessStartInfo startInfo = processStartInfo;
			try
			{
				using (Process process = Process.Start(startInfo))
				{
					return process != null && process.WaitForExit(5000) && process.ExitCode == 0;
				}
			}
			catch (Exception error)
			{
				Logger.LogFailure("Windows restart request", error);
				return false;
			}
		}
	}
}
