using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class GameFamilyEvidence
    {
        internal sealed class Member
        {
            internal readonly long Creation;

            internal readonly string Path;

            internal Member(long creation, string path)
            {
                Creation = creation;
                Path = path;
            }
        }

        private readonly Dictionary<GameFamilyProfileKey, Dictionary<int, Member>> profiles;

        internal static readonly GameFamilyEvidence Empty = new GameFamilyEvidence(new Dictionary<GameFamilyProfileKey, Dictionary<int, Member>>());

        internal GameFamilyEvidence(Dictionary<GameFamilyProfileKey, Dictionary<int, Member>> source)
        {
            profiles = new Dictionary<GameFamilyProfileKey, Dictionary<int, Member>>();
            foreach (KeyValuePair<GameFamilyProfileKey, Dictionary<int, Member>> item in source)
            {
                profiles.Add(item.Key, new Dictionary<int, Member>(item.Value));
            }
        }

        internal bool Contains(GameProfile profile, int pid, long creation, string path)
        {
            if (profile == null || string.IsNullOrEmpty(profile.Id) || pid <= 4 || creation <= 0 || string.IsNullOrEmpty(path))
            {
                return false;
            }
            Dictionary<int, Member> value;
            Member value2;
            if (profiles.TryGetValue(new GameFamilyProfileKey(profile), out value) && value.TryGetValue(pid, out value2) && value2.Creation == creation)
            {
                return string.Equals(value2.Path, path, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        internal HashSet<int> MatchingProcesses(IList<GameProfile> configured, ProcessSnapshot snapshot,
            int selfPid, int ownerSession, bool forceFamilyProtection)
        {
            var result = new HashSet<int>();
            if (configured == null || snapshot == null || ownerSession < 0) return result;
            var byPid = new Dictionary<int, List<Member>>();
            var seen = new HashSet<GameFamilyProfileKey>();
            foreach (GameProfile profile in configured)
            {
                if (profile == null || string.IsNullOrEmpty(profile.Id)
                    || !forceFamilyProtection && !FamilyBoundary.FamilyExemptFor(profile)) continue;
                var key = new GameFamilyProfileKey(profile);
                Dictionary<int, Member> members;
                if (!seen.Add(key) || !profiles.TryGetValue(key, out members)) continue;
                foreach (KeyValuePair<int, Member> entry in members)
                {
                    List<Member> candidates;
                    if (!byPid.TryGetValue(entry.Key, out candidates))
                        byPid.Add(entry.Key, candidates = new List<Member>());
                    candidates.Add(entry.Value);
                }
            }
            // 只为本次不可变证据建立索引；每个成员仍匹配同一快照中的创建时间、路径和会话。
            foreach (ProcEntry process in snapshot.Entries)
            {
                if (process.Pid <= 4 || process.Pid == selfPid || process.Session != ownerSession
                    || process.Creation <= 0 || string.IsNullOrEmpty(process.Path)) continue;
                List<Member> candidates;
                if (!byPid.TryGetValue(process.Pid, out candidates)) continue;
                foreach (Member member in candidates)
                    if (member.Creation == process.Creation
                        && string.Equals(member.Path, process.Path, StringComparison.OrdinalIgnoreCase))
                    { result.Add(process.Pid); break; }
            }
            return result;
        }
    }
}
