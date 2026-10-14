using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private Dictionary<int, PlatformProcessOwner> platformMembers = new Dictionary<int, PlatformProcessOwner>();
        private int whiteEvalPlatformMask = -1;
        private int whiteEvalPlatformRenderer;
        private long whiteEvalPlatformRendererCreation;

        private Dictionary<int, PlatformProcessOwner> CollectPlatformMembers(ProcessSnapshot snapshot)
        {
            var gameRoots = new HashSet<int>();
            var gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (sync)
            {
                if (activeDetection != null && activeDetection.RendererPid > 4) gameRoots.Add(activeDetection.RendererPid);
                foreach (GameProfile p in profiles)
                {
                    if (!string.IsNullOrEmpty(p.ExecutablePath)) gamePaths.Add(p.ExecutablePath);
                    if (!string.IsNullOrEmpty(p.LearnedExecutablePath)) gamePaths.Add(p.LearnedExecutablePath);
                }
            }
            if (snapshot != null)
                foreach (ProcEntry p in snapshot.Entries)
                    if (p != null && (gamePaths.Contains(p.Path) || (PlatformRuntimePolicy.RuntimePlatform(p.Name) == null
                        && FamilyBoundary.CatalogProtected(p.Pid, p.Creation, p.Name, p.Path)))) gameRoots.Add(p.Pid);
            platformMembers = PlatformRuntimePolicy.Collect(snapshot, selfPid, selfSession, gameRoots, platformMembers);
            return platformMembers;
        }
        private static bool PlatformRuleEnabled(PlatformProcessOwner owner)
        { return owner.Platform == "wegame" && PlatformRuntimePolicy.Enabled(owner.Platform); }

        private void AppendPlatformRuleViews(List<WhitelistRuleView> views, Dictionary<int, PlatformProcessOwner> members)
        {
            foreach (string id in PlatformRuntimePolicy.Ids)
            {
                if (!PlatformRuntimePolicy.Enabled(id)) continue;
                int count = members == null ? -1 : 0;
                if (members != null) foreach (PlatformProcessOwner member in members.Values) if (member.Platform == id) count++;
                WhitelistRule rule;
                if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, id, out rule))
                    views.Add(new WhitelistRuleView(rule, count, false, id));
            }
        }
        private bool RemovePlatformProtection(string id)
        {
            lock (whiteEvalSync)
                lock (sync)
                {
                    if (stopping) return false;
                    if (!PlatformRuntimePolicy.TryRemove(id))
                    { whitelistLastError = Lang.T("white.save.failed"); return false; }
                    whiteRevision++;
                    whitelistLastError = "";
                }
            RequestPolicyApply();
            return true;
        }
        public bool RestorePlatformProtection()
        {
            lock (whiteEvalSync)
            {
                lock (sync)
                {
                    if (stopping) return false;
                    if (!PlatformRuntimePolicy.TryRestore())
                    { whitelistLastError = Lang.T("white.save.failed"); return false; }
                    whiteRevision++;
                    whitelistLastError = "";
                }
                int matched;
                ReleaseCurrentWhitelistMatches(out matched);
            }
            RequestPolicyApply();
            return true;
        }
    }
}
