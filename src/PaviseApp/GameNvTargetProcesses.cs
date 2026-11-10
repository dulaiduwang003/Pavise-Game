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
            bool identityUnknown = false;
            bool targetRunning = false;
            foreach (Process process in processesByName)
            {
                using (process)
                {
                    try
                    {
                        GameProcessSnapshot identity;
                        if (!GameSessionDetector.TryCaptureProcessIdentity(process.Id, session, out identity))
                        {
                            identityUnknown = true;
                        }
                        else if (string.Equals(Path.GetFullPath(identity.Path), path, StringComparison.OrdinalIgnoreCase))
                        {
                            targetRunning = true;
                        }
                    }
                    catch
                    {
                        identityUnknown = true;
                    }
                }
            }
            if (!targetRunning)
            {
                if (!identityUnknown)
                {
                    return GameNvTargetPhase.Closed;
                }
                return GameNvTargetPhase.Unknown;
            }
            return GameNvTargetPhase.Running;
        }
    }
}
