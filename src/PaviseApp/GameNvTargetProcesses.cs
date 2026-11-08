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
			bool phaseReady = false;
			bool phaseReady2 = false;
			Process[] processesByNameSnapshot = processesByName;
			foreach (Process process in processesByNameSnapshot)
			{
				using (process)
				{
					try
					{
						GameProcessSnapshot identity;
						if (!GameSessionDetector.TryCaptureProcessIdentity(process.Id, session, out identity))
						{
							phaseReady = true;
						}
						else if (string.Equals(Path.GetFullPath(identity.Path), path, StringComparison.OrdinalIgnoreCase))
						{
							phaseReady2 = true;
						}
					}
					catch
					{
						phaseReady = true;
					}
				}
			}
			if (!phaseReady2)
			{
				if (!phaseReady)
				{
					return GameNvTargetPhase.Closed;
				}
				return GameNvTargetPhase.Unknown;
			}
			return GameNvTargetPhase.Running;
		}
	}
}
