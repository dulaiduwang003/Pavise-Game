using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal static bool ReanchorToStickyInstance(GameDetection fresh, GameDetection sticky, IList<int> verifiedStickyPids)
        {
            if (fresh == null || sticky == null || sticky.RendererPid <= 0 || verifiedStickyPids == null)
            {
                return false;
            }
            bool rendererVerified = false;
            foreach (int verifiedStickyPid in verifiedStickyPids)
            {
                if (verifiedStickyPid == sticky.RendererPid)
                {
                    rendererVerified = true;
                    break;
                }
            }
            if (!rendererVerified)
            {
                return false;
            }
            fresh.RendererPid = sticky.RendererPid;
            fresh.RendererCreation = sticky.RendererCreation;
            fresh.RendererName = sticky.RendererName;
            fresh.RendererPath = sticky.RendererPath;
            fresh.RendererForeground = sticky.RendererForeground;
            fresh.RendererCandidateSelected = true;
            fresh.RendererUserSelected = sticky.RendererUserSelected;
            fresh.RendererLearnable = sticky.RendererLearnable;
            fresh.RequiresGpuConfirm = false;
            fresh.RendererSafetyOnly = false;
            fresh.RendererGpuProofExpiresMs = 0L;
            fresh.Evidence = sticky.Evidence;
            fresh.FamilyPids.Clear();
            foreach (int verifiedStickyPid2 in verifiedStickyPids)
            {
                if (verifiedStickyPid2 > 0)
                {
                    fresh.FamilyPids.Add(verifiedStickyPid2);
                }
            }
            fresh.FamilyNames.Clear();
            foreach (string familyName in sticky.FamilyNames)
            {
                fresh.FamilyNames.Add(familyName);
            }
            return true;
        }
        private bool RememberSticky(GameDetection hit)
        {
            if (hit == null)
            {
                ClearSticky();
                return false;
            }
            Dictionary<int, GameId> dictionary = new Dictionary<int, GameId>();
            foreach (int familyPid in hit.FamilyPids)
            {
                string name;
                long creation;
                if (TryStickyIdentity(familyPid, out name, out creation) && (familyPid != hit.RendererPid || StickyIdentityMatches(hit, name, creation)))
                {
                    dictionary[familyPid] = new GameId
                    {
                        Name = name,
                        Creation = creation
                    };
                }
            }
            string name2;
            long creation2;
            if (hit.RendererPid > 0 && !dictionary.ContainsKey(hit.RendererPid) && TryStickyIdentity(hit.RendererPid, out name2, out creation2) && StickyIdentityMatches(hit, name2, creation2))
            {
                dictionary[hit.RendererPid] = new GameId
                {
                    Name = name2,
                    Creation = creation2
                };
            }
            GameId value;
            if (hit.RendererCreation <= 0 && dictionary.TryGetValue(hit.RendererPid, out value))
            {
                hit.RendererCreation = value.Creation;
            }
            if (hit.RendererPid <= 0 || !dictionary.ContainsKey(hit.RendererPid))
            {
                return false;
            }
            stickyIds.Clear();
            foreach (KeyValuePair<int, GameId> item in dictionary)
            {
                stickyIds[item.Key] = item.Value;
            }
            stickyDetection = hit;
            return true;
        }
        private static bool StickyIdentityMatches(GameDetection hit, string name, long creation)
        {
            if (creation > 0 && (hit.RendererCreation <= 0 || hit.RendererCreation == creation))
            {
                return string.Equals(hit.RendererName, name, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        private void ClearSticky()
        {
            stickyDetection = null;
            stickyIds.Clear();
            stickyMiss = 0;
            stickyGraceOnly = false;
        }
        private bool AnyStickyReused()
        {
            foreach (KeyValuePair<int, GameId> stickyId in stickyIds)
            {
                string name;
                long creation;
                if (TryStickyIdentity(stickyId.Key, out name, out creation) && (creation != stickyId.Value.Creation || !string.Equals(name, stickyId.Value.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        }
        private bool TryStickyIdentity(int pid, out string name, out long creation)
        {
            return TryIdentity(pid, out name, out creation);
        }

    }
}
