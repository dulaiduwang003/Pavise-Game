// @author bdth 2074055628@qq.com
// 文件用途 粘滞检测 保持对局目标不抖动

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private GameDetection ApplyStickiness(GameDetection hit)
        {
            if (hit != null)
            {
                stickyMiss = 0;
                if (stickyDetection != null && stickyDetection.Profile != null && hit.Profile != null
                    && string.Equals(stickyDetection.Profile.Id, hit.Profile.Id, StringComparison.OrdinalIgnoreCase)
                    && stickyDetection.RendererPid > 0 && AliveWithIdentity(stickyDetection.RendererPid))
                {
                    bool freshMayReplace = false;
                    if (hit.RendererPid > 0 && hit.RendererPid != stickyDetection.RendererPid)
                    {
                        GameId anchorId; string nm; long cr;
                        if (stickyIds.TryGetValue(stickyDetection.RendererPid, out anchorId)
                            && TryIdentity(hit.RendererPid, out nm, out cr)
                            && FreshRendererMayReplaceSticky(
                                hit, nm, cr,
                                anchorId.Creation))
                            freshMayReplace = true;
                    }
                    if (hit.RendererPid
                            != stickyDetection.RendererPid
                        && !freshMayReplace)
                    {
                        List<int> retainedFamily =
                            LiveStickyFamily();
                        if (!ReanchorToStickyInstance(
                                hit, stickyDetection,
                                retainedFamily))
                        {

                            ClearSticky();
                        }
                    }
                }
                RememberSticky(hit);
                foreach (int pid in LiveStickyFamily()) hit.FamilyPids.Add(pid);
                return hit;
            }
            if (stickyDetection != null && stickyDetection.RendererPid > 0
                && AliveWithIdentity(stickyDetection.RendererPid))
            {
                stickyMiss = 0;
                var r = new GameDetection
                {
                    Profile = stickyDetection.Profile,
                    RendererPid = stickyDetection.RendererPid,
                    RendererCreation =
                        stickyDetection.RendererCreation,
                    RendererName = stickyDetection.RendererName,
                    RendererPath = stickyDetection.RendererPath,
                    RendererForeground =
                        stickyDetection.RendererForeground,
                    RendererCandidateSelected = true,
                    RendererUserSelected = stickyDetection.RendererUserSelected,
                    RendererLearnable = stickyDetection.RendererLearnable,
                    Evidence = stickyDetection.Evidence
                };
                r.FamilyPids.Add(stickyDetection.RendererPid);
                foreach (int pid in LiveStickyFamily()) r.FamilyPids.Add(pid);
                foreach (string n in stickyDetection.FamilyNames) r.FamilyNames.Add(n);
                stickyDetection = r;
                return r;
            }
            if (stickyDetection != null && stickyMiss < StickyGraceMisses && !AnyStickyReused())
            {
                stickyMiss++;

                RequestFullGameDetection();
                try { kick.Set(); } catch { }
                return CloneWithAnchoredFamily(stickyDetection);
            }
            ClearSticky();
            return null;
        }

        internal static bool FreshRendererMayReplaceSticky(
            GameDetection fresh, string verifiedName,
            long verifiedCreation, long stickyCreation)
        {
            if (fresh == null || fresh.RendererPid <= 0
                || string.IsNullOrEmpty(fresh.RendererName)
                || string.IsNullOrEmpty(verifiedName)
                || verifiedCreation <= 0 || stickyCreation <= 0
                || !string.Equals(
                    verifiedName, fresh.RendererName,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (fresh.RendererForeground
                && !GameSessionDetector.IsLauncherLikeName(
                    fresh.RendererName))
                return true;
            return verifiedCreation > stickyCreation;
        }

        internal static bool ReanchorToStickyInstance(
            GameDetection fresh, GameDetection sticky,
            IList<int> verifiedStickyPids)
        {
            if (fresh == null || sticky == null
                || sticky.RendererPid <= 0
                || verifiedStickyPids == null)
                return false;
            bool rendererVerified = false;
            foreach (int pid in verifiedStickyPids)
                if (pid == sticky.RendererPid)
                {
                    rendererVerified = true;
                    break;
                }
            if (!rendererVerified) return false;

            fresh.RendererPid = sticky.RendererPid;
            fresh.RendererCreation = sticky.RendererCreation;
            fresh.RendererName = sticky.RendererName;
            fresh.RendererPath = sticky.RendererPath;
            fresh.RendererForeground =
                sticky.RendererForeground;
            fresh.RendererCandidateSelected = true;
            fresh.RendererUserSelected =
                sticky.RendererUserSelected;
            fresh.Evidence = sticky.Evidence;
            fresh.FamilyPids.Clear();
            foreach (int pid in verifiedStickyPids)
                if (pid > 0) fresh.FamilyPids.Add(pid);
            fresh.FamilyNames.Clear();
            foreach (string name in sticky.FamilyNames)
                fresh.FamilyNames.Add(name);
            return true;
        }

        private GameDetection CloneWithAnchoredFamily(GameDetection src)
        {
            var g = new GameDetection
            {
                Profile = src.Profile,
                RendererPid = src.RendererPid,
                RendererCreation = src.RendererCreation,
                RendererName = src.RendererName,
                RendererPath = src.RendererPath,
                RendererForeground =
                    src.RendererForeground,
                RendererCandidateSelected = src.RendererCandidateSelected,
                RendererUserSelected = src.RendererUserSelected,
                RendererLearnable = src.RendererLearnable,
                Evidence = src.Evidence
            };
            foreach (int pid in src.FamilyPids) if (stickyIds.ContainsKey(pid)) g.FamilyPids.Add(pid);
            foreach (string n in src.FamilyNames) g.FamilyNames.Add(n);
            return g;
        }

        private void RememberSticky(GameDetection hit)
        {
            if (hit == null) { ClearSticky(); return; }
            if (!hit.RendererUserSelected && GameSessionDetector.IsLauncherLikeName(hit.RendererName)) { ClearSticky(); return; }
            var fresh = new Dictionary<int, GameId>();
            foreach (int pid in hit.FamilyPids)
            {
                string nm; long cr;
                if (TryIdentity(pid, out nm, out cr)) fresh[pid] = new GameId { Name = nm, Creation = cr };
            }
            if (hit.RendererPid > 0 && !fresh.ContainsKey(hit.RendererPid))
            {
                string nm; long cr;
                if (TryIdentity(hit.RendererPid, out nm, out cr)) fresh[hit.RendererPid] = new GameId { Name = nm, Creation = cr };
            }
            GameId rendererIdentity;
            if (fresh.TryGetValue(
                    hit.RendererPid, out rendererIdentity))
                hit.RendererCreation = rendererIdentity.Creation;
            if (hit.RendererPid > 0 && !fresh.ContainsKey(hit.RendererPid))
            {
                int anchorPid = stickyDetection != null ? stickyDetection.RendererPid : 0;
                List<int> gone = null;
                foreach (var kv in stickyIds)
                {
                    if (fresh.ContainsKey(kv.Key) || kv.Key == hit.RendererPid || kv.Key == anchorPid) continue;
                    if (AliveWithIdentity(kv.Key)) continue;
                    if (gone == null) gone = new List<int>();
                    gone.Add(kv.Key);
                }
                if (gone != null) foreach (int dead in gone) stickyIds.Remove(dead);
                foreach (var kv in fresh) stickyIds[kv.Key] = kv.Value;
                return;
            }
            stickyIds.Clear();
            foreach (var kv in fresh) stickyIds[kv.Key] = kv.Value;
            stickyDetection = hit;
        }

        private List<int> LiveStickyFamily()
        {
            var l = new List<int>();
            foreach (var kv in stickyIds) if (AliveWithIdentity(kv.Key)) l.Add(kv.Key);
            return l;
        }

        private void ClearSticky()
        {
            stickyDetection = null;
            stickyIds.Clear();
            stickyMiss = 0;
        }

        private bool AliveWithIdentity(int pid)
        {
            GameId id;
            if (!stickyIds.TryGetValue(pid, out id)) return false;
            string nm; long cr;
            return TryIdentity(pid, out nm, out cr) && cr == id.Creation
                && string.Equals(nm, id.Name, StringComparison.OrdinalIgnoreCase);
        }

        private bool AnyStickyReused()
        {
            foreach (var kv in stickyIds)
            {
                string nm; long cr;
                if (TryIdentity(kv.Key, out nm, out cr)
                    && (cr != kv.Value.Creation
                        || !string.Equals(nm, kv.Value.Name, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        private static bool TryIdentity(int pid, out string name, out long creation)
        {
            name = null; creation = 0;
            if (pid <= 0) return false;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                name = Native.ImageName(h);
                long cpu; ulong io;
                return Native.QueryProcessSample(h, out creation, out cpu, out io) && !string.IsNullOrEmpty(name);
            }
            finally { Native.CloseHandle(h); }
        }
    }
}
