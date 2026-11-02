using System;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
	internal static class GameNvTargetProcesses
	{
		internal static GameNvTargetPhase Phase(string path, int session)
		{
			if (session < 0)
			{
				return GameNvTargetPhase.Unknown;
			}
			Process[] processesByName;
			try
			{
				processesByName = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
			}
			catch
			{
				return GameNvTargetPhase.Unknown;
			}
			bool flag = false;
			bool flag2 = false;
			Process[] array = processesByName;
			foreach (Process process in array)
			{
				using (process)
				{
					try
					{
						GameProcessSnapshot identity;
						if (!GameSessionDetector.TryCaptureProcessIdentity(process.Id, session, out identity))
						{
							flag = true;
						}
						else if (string.Equals(Path.GetFullPath(identity.Path), path, StringComparison.OrdinalIgnoreCase))
						{
							flag2 = true;
						}
					}
					catch
					{
						flag = true;
					}
				}
			}
			if (!flag2)
			{
				if (!flag)
				{
					return GameNvTargetPhase.Closed;
				}
				return GameNvTargetPhase.Unknown;
			}
			return GameNvTargetPhase.Running;
		}
	}
}
