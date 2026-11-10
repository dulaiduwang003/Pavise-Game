using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class WeGameRuntimeProtection
    {
        private static readonly HashSet<string> RuntimeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wegame", "wegame_env", "wegameclient", "pallas", "rail", "tcls_core" };

        private static readonly HashSet<string> HelperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "browser", "crashpad_handler", "msedgewebview2", "QtWebEngineProcess", "cef_subprocess" };

        internal static bool IsRuntimeName(string name)
        {
            return RuntimeNames.Contains(WhitelistRule.NormalizeName(name));
        }

        internal static bool IsProtectedRuntimeName(string name)
        {
            if (IsRuntimeName(name))
            {
                return PlatformRuntimePolicy.Enabled("wegame");
            }
            return false;
        }

        internal static bool IsCriticalRuntime(string name, string path)
        {
            if (!IsProtectedRuntimeName(name))
            {
                return false;
            }
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }
            string imagePath = WhitelistRule.NormalizeImagePath(path);
            if (imagePath.Length != 0)
            {
                return string.Equals(WhitelistRule.NormalizeName(name), WhitelistRule.NormalizeName(Path.GetFileName(imagePath)), StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        internal static HashSet<int> Collect(ProcessSnapshot snapshot, int selfPid, int ownerSession)
        {
            HashSet<int> pids = new HashSet<int>();
            if (snapshot == null || ownerSession < 0)
            {
                return pids;
            }
            Dictionary<int, ProcEntry> procEntriesByPid = new Dictionary<int, ProcEntry>();
            Dictionary<int, string> imagePathsByPid = new Dictionary<int, string>();
            HashSet<int> currentPids = new HashSet<int>();
            HashSet<string> directoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcEntry procEntry in snapshot.Entries)
            {
                if (procEntry == null)
                {
                    continue;
                }
                if (!currentPids.Add(procEntry.Pid))
                {
                    procEntriesByPid.Remove(procEntry.Pid);
                    imagePathsByPid.Remove(procEntry.Pid);
                }
                else if (procEntry.Pid > 4 && procEntry.Pid != selfPid && procEntry.Session == ownerSession && procEntry.Creation > 0)
                {
                    string imagePath = WhitelistRule.NormalizeImagePath(procEntry.Path);
                    if (imagePath.Length != 0 && string.Equals(WhitelistRule.NormalizeName(procEntry.Name), WhitelistRule.NormalizeName(Path.GetFileName(imagePath)), StringComparison.OrdinalIgnoreCase))
                    {
                        procEntriesByPid[procEntry.Pid] = procEntry;
                        imagePathsByPid[procEntry.Pid] = imagePath;
                    }
                }
            }
            foreach (ProcEntry protectedProcess in procEntriesByPid.Values)
            {
                if (FamilyBoundary.CatalogProtected(protectedProcess.Pid, protectedProcess.Creation, protectedProcess.Name, protectedProcess.Path))
                {
                    pids.Add(protectedProcess.Pid);
                    string directoryName = Path.GetDirectoryName(imagePathsByPid[protectedProcess.Pid]);
                    // 只有身份核验通过的后代继承推流保护，附近的浏览器或编码器进程不自动继承。
                    if (!StreamingToolCatalog.IsStreamingToolProcess(protectedProcess.Name, protectedProcess.Path) && FamilyBoundary.SafeFamilyDir(directoryName))
                    {
                        directoryNames.Add(directoryName);
                    }
                }
            }
            foreach (ProcEntry helperProcess in procEntriesByPid.Values)
            {
                if (!HelperNames.Contains(WhitelistRule.NormalizeName(helperProcess.Name)))
                {
                    continue;
                }
                foreach (string currentDirectoryName in directoryNames)
                {
                    if (FamilyBoundary.UnderRoot(imagePathsByPid[helperProcess.Pid], currentDirectoryName))
                    {
                        pids.Add(helperProcess.Pid);
                        break;
                    }
                }
            }
            for (int j = 0; j < 24; j++)
            {
                bool descendantsAdded = false;
                foreach (ProcEntry childProcess in procEntriesByPid.Values)
                {
                    ProcEntry currentProcEntry;
                    if (!pids.Contains(childProcess.Pid) && pids.Contains(childProcess.ParentPid) && procEntriesByPid.TryGetValue(childProcess.ParentPid, out currentProcEntry) && currentProcEntry.Creation < childProcess.Creation)
                    {
                        pids.Add(childProcess.Pid);
                        descendantsAdded = true;
                    }
                }
                if (!descendantsAdded)
                {
                    break;
                }
            }
            return pids;
        }
    }
}
