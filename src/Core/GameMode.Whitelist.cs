// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class WhitelistProcessInfo
        {
            public int Pid;
            public int Session = -1;
            public int Parent;
            public string Name;
            public string Path;
            public long Creation;
            public long Cpu;
            public ulong Io;
        }

        private sealed class WhitelistEvaluation
        {
            public readonly HashSet<int> Protected = new HashSet<int>();
            public Dictionary<int, PlatformProcessOwner> PlatformProcesses;
            public readonly HashSet<int> RuntimeProtected = new HashSet<int>();
            public readonly Dictionary<string, HashSet<int>> ByRule =
                new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<int, WhitelistProcessInfo> Processes =
                new Dictionary<int, WhitelistProcessInfo>();
            public readonly Dictionary<int, int> Parents = new Dictionary<int, int>();
            public readonly Dictionary<int, string> Names = new Dictionary<int, string>();
            public readonly Dictionary<int, long> Creations = new Dictionary<int, long>();
        }

        private readonly List<WhitelistRule> whiteRules = new List<WhitelistRule>();
        private readonly HashSet<string> whiteRuleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Dictionary<int, long>> whiteFamilyMembers =
            new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
        private readonly object whiteEvalSync = new object();
        private int whiteRevision;
        private int whiteHasFamilyRules;
        private long lastWhiteFamilyPruneTicks;
        private const int WhiteFamilyPruneIntervalMs = 20000;

        public bool NeedsWhitelistParentIdentity(int session)
        {
            return session == selfSession
                && Interlocked.CompareExchange(
                    ref whiteHasFamilyRules, 0, 0) != 0;
        }

        private bool AddWhiteRuleNoSave(WhitelistRule rule)
        {
            if (rule == null || !whiteRuleKeys.Add(rule.Key)) return false;
            whiteRules.Add(rule);
            whiteRevision++;
            if (RuleProtectsFamily(rule))
                Interlocked.Exchange(ref whiteHasFamilyRules, 1);
            return true;
        }

        private void RefreshWhitelistFamilyFlagLocked()
        {
            int found = 0;
            foreach (WhitelistRule rule in whiteRules)
                if (RuleProtectsFamily(rule))
                {
                    found = 1;
                    break;
                }
            Interlocked.Exchange(ref whiteHasFamilyRules, found);
        }
        private static bool RuleProtectsFamily(WhitelistRule rule)
        {
            return rule != null && !(rule.Kind == WhitelistRuleKind.LegacyName
                && IsPresetWhitelistName(rule.Value));
        }

        private ProcessSnapshot whiteEvalSnapshot;
        private WhitelistEvaluation whiteEvalCached;
        private int whiteEvalRevision;
        private int whiteEvalMembersVersion;
        private int whiteFamilyMembersVersion;
        private Dictionary<int, long> runtimeFamilyMembers = new Dictionary<int, long>();

        private WhitelistEvaluation EvaluateWhitelist(ProcessSnapshot all)
        {
            lock (whiteEvalSync)
            {
                if (all != null && ReferenceEquals(all, whiteEvalSnapshot) && whiteEvalCached != null)
                {
                    bool fresh;
                    lock (sync)
                        fresh = whiteEvalRevision == whiteRevision
                            && whiteEvalMembersVersion == whiteFamilyMembersVersion
                            && whiteEvalPlatformMask == PlatformRuntimePolicy.Mask
                            && whiteEvalPlatformRenderer == (activeDetection == null ? 0 : activeDetection.RendererPid)
                            && whiteEvalPlatformRendererCreation == (activeDetection == null ? 0 : activeDetection.RendererCreation);
                    if (fresh) return whiteEvalCached;
                }
                WhitelistEvaluation built = EvaluateWhitelistLocked(all);
                whiteEvalSnapshot = all;
                whiteEvalCached = built;
                return built;
            }
        }

        private WhitelistEvaluation EvaluateWhitelistLocked(ProcessSnapshot all)
        {
            {
                var result = BuildWhitelistProcessSnapshot(all);
                int platformMask = PlatformRuntimePolicy.Mask;
                int platformRenderer; long platformRendererCreation;
                lock (sync)
                { platformRenderer = activeDetection == null ? 0 : activeDetection.RendererPid;
                    platformRendererCreation = activeDetection == null ? 0 : activeDetection.RendererCreation; }
                result.PlatformProcesses = CollectPlatformMembers(all);
                var retainedRuntime = new Dictionary<int, long>(runtimeFamilyMembers);
                foreach (var member in result.PlatformProcesses)
                {
                    if (PlatformRuleEnabled(member.Value)) result.RuntimeProtected.Add(member.Key);
                    else retainedRuntime.Remove(member.Key);
                }
                result.RuntimeProtected.UnionWith(WeGameRuntimeProtection.Collect(all, selfPid, selfSession));
                result.RuntimeProtected.UnionWith(ExpandApplicationFamily(result.Parents,
                    result.RuntimeProtected, retainedRuntime, result.Creations));
                runtimeFamilyMembers = new Dictionary<int, long>();
                foreach (int pid in result.RuntimeProtected)
                {
                    long born;
                    if (result.Creations.TryGetValue(pid, out born) && born > 0) runtimeFamilyMembers[pid] = born;
                }
                result.Protected.UnionWith(result.RuntimeProtected);
                List<WhitelistRule> rules;
                int revision;
                Dictionary<string, Dictionary<int, long>> retained =
                    new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
                int membersAtInput;
                lock (sync)
                {
                    revision = whiteRevision;
                    membersAtInput = whiteFamilyMembersVersion;
                    rules = new List<WhitelistRule>(whiteRules);
                    foreach (var pair in whiteFamilyMembers)
                        retained[pair.Key] = new Dictionary<int, long>(pair.Value);
                }

                Dictionary<int, long> currentCreation = result.Creations;
                foreach (var pair in result.Processes)
                    if (pair.Value.Creation > 0) currentCreation[pair.Key] = pair.Value.Creation;

                var nextFamilies = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
                foreach (WhitelistRule rule in rules)
                {
                    var direct = new HashSet<int>();
                    foreach (var pair in result.Processes)
                    {
                        WhitelistProcessInfo info = pair.Value;
                        if (info.Session != selfSession
                            && !(info.Session == 0 && BackgroundServicePolicy.CandidateName(info.Name))) continue;
                        if (!rule.MatchesNormalized(info.Name, info.Path)) continue;

                        if (rule.Kind == WhitelistRuleKind.LegacyName
                            && IsPresetWhitelistName(rule.Value)
                            && !IsTrustedPresetProcessPath(rule.Value, info.Path))
                            continue;
                        direct.Add(info.Pid);
                    }

                    HashSet<int> matched = direct;
                    if (RuleProtectsFamily(rule))
                    {
                        var anchors = new HashSet<int>();
                        foreach (int pid in direct)
                        {
                            WhitelistProcessInfo info = result.Processes[pid];
                            if (!string.IsNullOrEmpty(info.Path) && !WhitelistRule.IsUnsafeFamilyAnchor(info.Path)) anchors.Add(pid);
                        }
                        Dictionary<int, long> old;
                        if (!retained.TryGetValue(rule.Key, out old)) old = new Dictionary<int, long>();
                        matched = ExpandApplicationFamily(result.Parents, anchors, old, currentCreation);
                        matched.UnionWith(direct);
                        var keep = new Dictionary<int, long>();
                        foreach (int pid in matched)
                        {
                            long creation;
                            if (currentCreation.TryGetValue(pid, out creation) && creation > 0)
                                keep[pid] = creation;
                        }
                        nextFamilies[rule.Key] = keep;
                    }

                    result.ByRule[rule.Key] = matched;
                    result.Protected.UnionWith(matched);
                }

                lock (sync)
                {
                    // 计算期间输入已变更时不发布旧结果。
                    bool inputsIntact = revision == whiteRevision && membersAtInput == whiteFamilyMembersVersion;
                    if (inputsIntact)
                    {
                        whiteFamilyMembers.Clear();
                        foreach (var pair in nextFamilies)
                            whiteFamilyMembers[pair.Key] = pair.Value;
                        whiteFamilyMembersVersion++;
                    }
                    whiteEvalRevision = inputsIntact ? revision : whiteRevision - 1;
                    whiteEvalMembersVersion = whiteFamilyMembersVersion;
                    whiteEvalPlatformMask = platformMask; whiteEvalPlatformRenderer = platformRenderer;
                    whiteEvalPlatformRendererCreation = platformRendererCreation;
                }
                return result;
            }
        }

        private WhitelistEvaluation BuildWhitelistProcessSnapshot(ProcessSnapshot all)
        {
            var result = new WhitelistEvaluation();
            if (all == null) return result;
            foreach (ProcEntry entry in all.Entries)
            {
                try
                {
                    int pid = entry.Pid;
                    var info = new WhitelistProcessInfo { Pid = pid };
                    info.Name = WhitelistRule.NormalizeName(entry.Name);
                    info.Session = entry.Session;
                    result.Processes[pid] = info;
                    result.Names[pid] = info.Name;
                    if (pid <= 4 || selfSession < 0 || (info.Session != selfSession
                        && !(info.Session == 0 && BackgroundServicePolicy.CandidateName(info.Name)))) continue;
                    if (entry.Path == null) continue;

                    info.Path = WhitelistRule.NormalizeImagePath(entry.Path);
                    info.Parent = entry.ParentPid;
                    info.Creation = entry.Creation;
                    result.Creations[pid] = info.Creation;
                    info.Cpu = entry.Cpu;
                    info.Io = entry.Io;
                    if (info.Session == selfSession) result.Parents[pid] = info.Parent;
                }
                catch { }
            }
            return result;
        }

        internal static HashSet<int> ExpandApplicationFamily(Dictionary<int, int> parents,
            HashSet<int> anchors, Dictionary<int, long> retained, Dictionary<int, long> currentCreation)
        {
            var result = anchors == null ? new HashSet<int>() : new HashSet<int>(anchors);
            if (retained != null && currentCreation != null)
                foreach (var pair in retained)
                {
                    long now;
                    if (pair.Value > 0 && currentCreation.TryGetValue(pair.Key, out now) && now == pair.Value)
                        result.Add(pair.Key);
                }

            bool changed;
            do
            {
                changed = false;
                if (parents == null) break;
                foreach (var pair in parents)
                    if (!result.Contains(pair.Key) && result.Contains(pair.Value))
                    {

                        long childCreation;
                        long parentCreation;
                        if (currentCreation == null
                            || !currentCreation.TryGetValue(pair.Key, out childCreation)
                            || !currentCreation.TryGetValue(pair.Value, out parentCreation)
                            || childCreation <= 0 || parentCreation <= 0
                            || childCreation <= parentCreation)
                            continue;
                        result.Add(pair.Key);
                        changed = true;
                    }
            }
            while (changed);
            return result;
        }

        private static bool IsPresetWhitelistName(string name)
        {
            foreach (string preset in SystemProcessCatalog.PresetWhitelist)
                if (string.Equals(preset, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static bool IsTrustedPresetProcessPath(string name, string imagePath)
        {
            string path = WhitelistRule.NormalizeImagePath(imagePath);
            if (path.Length == 0) return false;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(windows)) windows = @"C:\Windows";
            windows = windows.TrimEnd('\\');

            if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase)
                && WhitelistRule.PathEquals(path, System.IO.Path.Combine(windows, "explorer.exe")))
                return true;
            if (FamilyBoundary.UnderRoot(path, System.IO.Path.Combine(windows, "System32"))
                || FamilyBoundary.UnderRoot(path, System.IO.Path.Combine(windows, "SysWOW64"))
                || FamilyBoundary.UnderRoot(path, System.IO.Path.Combine(windows, "SystemApps")))
                return true;

            if (string.Equals(name, "wslservice", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "vmmem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "vmmemwsl", StringComparison.OrdinalIgnoreCase))
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (string.IsNullOrEmpty(programFiles)) return false;
                if (FamilyBoundary.UnderRoot(path, System.IO.Path.Combine(programFiles, "WindowsApps")))
                    return true;
                return string.Equals(name, "wslservice", StringComparison.OrdinalIgnoreCase)
                    && WhitelistRule.PathEquals(path, System.IO.Path.Combine(
                        programFiles, "WSL", "wslservice.exe"));
            }
            return false;
        }

        private List<WhitelistRuleView> SnapshotWhitelistRuleViews()
        {
            ProcessSnapshot all = ProcessSnapshotSource.Capture(selfSession);
            WhitelistEvaluation evaluation = EvaluateWhitelist(all);
            var views = new List<WhitelistRuleView>();
            AppendPlatformRuleViews(views, evaluation.PlatformProcesses);
            lock (sync)
                foreach (WhitelistRule rule in whiteRules)
                {
                    HashSet<int> matches;
                    int count = evaluation.ByRule.TryGetValue(rule.Key, out matches) ? matches.Count : 0;
                    views.Add(new WhitelistRuleView(
                        rule, count, rule.Kind == WhitelistRuleKind.LegacyName
                            && IsPresetWhitelistName(rule.Value)));
                }
            return views;
        }

        private int ReleaseCurrentWhitelistMatches(out int matched)
        {
            matched = 0;
            lock (whiteEvalSync)
            {
                ProcessSnapshot all = ProcessSnapshotSource.Capture(selfSession);
                WhitelistEvaluation evaluation = EvaluateWhitelist(all);
                matched = evaluation.Protected.Count;
                int restored = 0;
                foreach (int pid in evaluation.Protected)
                {
                    WhitelistProcessInfo info;
                    if (!evaluation.Processes.TryGetValue(
                            pid, out info)
                        || info.Creation <= 0)
                        continue;
                    backgroundFreeze.Release(pid, info.Creation, "whitelist-added");
                    backgroundCpuCap.Release(pid);
                    if (core.ReleaseIfCreation(
                            pid, SuppressReason.Background,
                            info.Creation))
                    {
                        ReportUntrack(pid);
                        restored++;
                    }
                }
                return restored;
            }
        }

        private void ObserveWhitelistProcessChanges(ProcessChangeBatch batch)
        {
            if (batch == null || batch.Changes.Length == 0) return;
            lock (whiteEvalSync)
            {
                var familyRules = new List<WhitelistRule>();
                var newlyAdmitted = new Dictionary<int, long>();
                var trackedStops = new HashSet<int>();
                int revision;
                lock (sync)
                {
                    revision = whiteRevision;
                    foreach (WhitelistRule rule in whiteRules)
                        if (RuleProtectsFamily(rule))
                            familyRules.Add(rule);
                    foreach (ProcessChange change in batch.Changes)
                    {
                        if (change == null
                            || change.Kind != ProcessChangeKind.Stopped)
                            continue;
                        foreach (WhitelistRule rule in familyRules)
                        {
                            Dictionary<int, long> members;
                            if (whiteFamilyMembers.TryGetValue(
                                    rule.Key, out members)
                                && members.ContainsKey(change.Pid))
                            {
                                trackedStops.Add(change.Pid);
                                break;
                            }
                        }
                    }
                }
                if (familyRules.Count == 0) return;
                Dictionary<int, long> stopIdentity =
                    CaptureStoppedProcessIdentity(trackedStops);

                lock (sync)
                {
                    if (revision != whiteRevision) return;
                    foreach (WhitelistRule rule in familyRules)
                    {
                        Dictionary<int, long> members;
                        if (!whiteFamilyMembers.TryGetValue(rule.Key, out members))
                        {
                            members = new Dictionary<int, long>();
                            whiteFamilyMembers[rule.Key] = members;
                        }
                        foreach (KeyValuePair<int, long> entry
                            in ApplyApplicationFamilyEvents(
                            rule, members, batch.Changes,
                            selfSession, stopIdentity))
                            newlyAdmitted[entry.Key] = entry.Value;
                    }
                    whiteFamilyMembersVersion++;
                }

                foreach (KeyValuePair<int, long> entry in newlyAdmitted)
                {
                    backgroundFreeze.Release(entry.Key, entry.Value, "whitelist-family-start");
                    backgroundCpuCap.Release(entry.Key);
                    if (core.ReleaseIfCreation(
                            entry.Key, SuppressReason.Background, entry.Value))
                        ReportUntrack(entry.Key);
                }
            }
        }

        internal static Dictionary<int, long> ApplyApplicationFamilyEvents(
            WhitelistRule rule,
            Dictionary<int, long> members, IList<ProcessChange> changes, int ownerSession)
        {
            return ApplyApplicationFamilyEvents(
                rule, members, changes, ownerSession, null);
        }

        internal static Dictionary<int, long> ApplyApplicationFamilyEvents(
            WhitelistRule rule,
            Dictionary<int, long> members, IList<ProcessChange> changes,
            int ownerSession, IDictionary<int, long> stopIdentity)
        {
            var admitted = new Dictionary<int, long>();
            if (rule == null || members == null || changes == null) return admitted;
            var ordered = new List<ProcessChange>();
            foreach (ProcessChange change in changes)
                if (change != null && change.Pid > 0) ordered.Add(change);
            ordered.Sort(delegate(ProcessChange left, ProcessChange right)
            {
                return left.Sequence.CompareTo(right.Sequence);
            });

            var liveStarts = new Dictionary<int, ProcessChange>();
            foreach (ProcessChange change in ordered)
            {
                if (change.Kind == ProcessChangeKind.Stopped)
                {

                    long currentCreation;
                    long memberCreation;
                    if (stopIdentity != null
                        && stopIdentity.TryGetValue(
                            change.Pid, out currentCreation)
                        && members.TryGetValue(
                            change.Pid, out memberCreation)
                        && (currentCreation <= 0
                            || currentCreation != memberCreation))
                    {
                        members.Remove(change.Pid);
                        admitted.Remove(change.Pid);
                        liveStarts.Remove(change.Pid);
                    }
                    continue;
                }
                if (change.Creation <= 0 || change.Session != ownerSession) continue;

                long previousCreation;
                if (members.TryGetValue(change.Pid, out previousCreation)
                    && previousCreation != change.Creation)
                {
                    members.Remove(change.Pid);
                    admitted.Remove(change.Pid);
                }
                liveStarts[change.Pid] = change;

                string name = WhitelistRule.NormalizeName(change.Name);
                string path = WhitelistRule.NormalizeImagePath(change.Path);
                if (rule.MatchesNormalized(name, path) && !WhitelistRule.IsUnsafeFamilyAnchor(path)
                    || HasVerifiedFamilyParent(change, members, liveStarts))
                {
                    bool isNew = !members.TryGetValue(
                        change.Pid, out previousCreation)
                        || previousCreation != change.Creation;
                    members[change.Pid] = change.Creation;
                    if (isNew) admitted[change.Pid] = change.Creation;
                }
            }

            bool expanded;
            do
            {
                expanded = false;
                foreach (ProcessChange change in liveStarts.Values)
                {
                    if (members.ContainsKey(change.Pid)
                        || !HasVerifiedFamilyParent(change, members, liveStarts))
                        continue;
                    members[change.Pid] = change.Creation;
                    admitted[change.Pid] = change.Creation;
                    expanded = true;
                }
            }
            while (expanded);
            return admitted;
        }

        private static Dictionary<int, long>
            CaptureStoppedProcessIdentity(IEnumerable<int> pids)
        {
            var result = new Dictionary<int, long>();
            if (pids == null) return result;
            foreach (int pid in pids)
            {
                IntPtr handle = Native.OpenProcess(
                    Native.PROCESS_QUERY_LIMITED_INFORMATION,
                    false, pid);
                if (handle == IntPtr.Zero)
                {
                    if (Native.LastOpenProcessFailureWasNoSuchProcess())
                        result[pid] = 0;
                    continue;
                }
                try
                {
                    long creation;
                    long cpu;
                    ulong io;
                    if (Native.QueryProcessSample(
                            handle, out creation, out cpu, out io)
                        && creation > 0)
                        result[pid] = creation;
                }
                finally { Native.CloseHandle(handle); }
            }
            return result;
        }

        private void PruneWhitelistFamilyMembersIfDue(ProcessSnapshot all)
        {
            if (all == null
                || Interlocked.CompareExchange(
                    ref whiteHasFamilyRules, 0, 0) == 0)
                return;
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref lastWhiteFamilyPruneTicks);
            if (last > 0 && now >= last
                && now - last < WhiteFamilyPruneIntervalMs
                    * TimeSpan.TicksPerMillisecond)
                return;
            if (Interlocked.CompareExchange(
                    ref lastWhiteFamilyPruneTicks, now, last) != last)
                return;

            lock (whiteEvalSync)
            {
                var retained = new HashSet<int>();
                lock (sync)
                    foreach (Dictionary<int, long> members
                        in whiteFamilyMembers.Values)
                        retained.UnionWith(members.Keys);
                if (retained.Count == 0) return;

                var live = new HashSet<int>();
                var current = new Dictionary<int, long>();
                foreach (ProcEntry process in all.Entries)
                {
                    int pid = process.Pid;
                    if (!retained.Contains(pid)) continue;
                    live.Add(pid);
                    current[pid] = process.Creation;
                }

                lock (sync)
                    foreach (Dictionary<int, long> members
                        in whiteFamilyMembers.Values)
                    {
                        List<int> remove = null;
                        foreach (KeyValuePair<int, long> entry in members)
                        {
                            long creation;
                            if (!live.Contains(entry.Key)
                                || current.TryGetValue(
                                    entry.Key, out creation)
                                    && creation != entry.Value)
                            {
                                if (remove == null)
                                    remove = new List<int>();
                                remove.Add(entry.Key);
                            }
                        }
                        if (remove != null)
                        {
                            foreach (int pid in remove)
                                members.Remove(pid);
                            whiteFamilyMembersVersion++;
                        }
                    }
            }
        }

        private static bool HasVerifiedFamilyParent(
            ProcessChange change, Dictionary<int, long> members,
            Dictionary<int, ProcessChange> liveStarts)
        {
            long parentCreation;
            if (change == null || change.ParentPid <= 0 || change.Creation <= 0
                || !members.TryGetValue(change.ParentPid, out parentCreation)
                || parentCreation <= 0 || change.Creation <= parentCreation)
                return false;
            long observedParentCreation = change.ParentCreation;
            if (observedParentCreation <= 0 && liveStarts != null)
            {
                ProcessChange parentStart;
                if (liveStarts.TryGetValue(change.ParentPid, out parentStart))
                    observedParentCreation = parentStart.Creation;
            }
            return observedParentCreation == parentCreation;
        }
    }
}
