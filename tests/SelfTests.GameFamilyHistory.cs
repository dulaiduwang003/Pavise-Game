// @author bdth 2074055628@qq.com
// 文件用途 纯内存历史亲缘回归：合成身份/事件/时钟，不启动进程、不读写注册表或游戏库。
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int TestGameFamilyHistory()
        {
            Action[] checks =
            {
                FamilyHistorySnapshotChain,
                FamilyHistoryRetainsExitedAncestors,
                FamilyHistoryNoLiveImpostors,
                FamilyHistoryUnorderedBatch,
                FamilyHistoryExplicitParent,
                FamilyHistoryPidOnlyParentRejected,
                FamilyHistoryParentPidReuse,
                FamilyHistoryStaleExplicitRejected,
                FamilyHistoryChildPidReuse,
                FamilyHistorySessionBoundary,
                FamilyHistoryAmbiguousSnapshot,
                FamilyHistoryAmbiguousEvents,
                FamilyHistoryIncompleteIdentities,
                FamilyHistoryProfileFingerprint,
                FamilyHistoryImmutableEvidence,
                FamilyHistorySharedHostBoundary,
                FamilyHistoryDepthBoundary,
                FamilyHistoryInvalidCreationChains,
                FamilyHistoryPruning,
                FamilyHistoryClearAndClockReset,
                FamilyHistoryQuota,
                FamilyHistoryOverflow,
                FamilyHistoryLateStartBoundary,
                FamilyHistoryRootAndLearnedSeeds,
                FamilyHistoryIdentityConflict,
                FamilyHistoryDelayedParentEvent,
                FamilyHistorySafetyBoundary
            };
            foreach (Action check in checks) check();
            return checks.Length;
        }

        private static GameProfile FamilyHistoryProfile()
        {
            return new GameProfile
            {
                Id = "family-history-test", Name = "Any title",
                ExecutablePath = @"C:\PaviseFamilyTests\Title\LoginBox\entry.exe",
                Root = @"C:\PaviseFamilyTests\Title\LoginBox"
            };
        }

        private static ProcEntry FamilyHistoryProcess(int pid, int parent, long creation, string path)
        {
            return new ProcEntry
            {
                Pid = pid, ParentPid = parent, Creation = creation, Session = 7,
                Path = path, Name = Path.GetFileNameWithoutExtension(path)
            };
        }

        private static ProcEntry[] FamilyHistoryChain(GameProfile profile)
        {
            return new[]
            {
                FamilyHistoryProcess(101, 10, 100, profile.ExecutablePath),
                FamilyHistoryProcess(102, 101, 200, @"D:\PaviseFamilyTests\Broker\bridge.exe"),
                FamilyHistoryProcess(103, 102, 300, @"C:\PaviseFamilyTests\Title\Arena\actual.exe")
            };
        }

        private static GameFamilyEvidence FamilyHistoryCapture(GameFamilyHistory history,
            GameProfile profile, long now, params ProcEntry[] entries)
        {
            return history.Capture(new ProcessSnapshot(entries), new[] { profile }, 7, now);
        }

        private static ProcessChange FamilyHistoryStart(ProcEntry process, long parentCreation)
        {
            return new ProcessChange
            {
                Pid = process.Pid, ParentPid = process.ParentPid, Creation = process.Creation,
                Path = process.Path, Name = process.Name, Session = process.Session,
                ParentCreation = parentCreation, Kind = ProcessChangeKind.Started
            };
        }

        private static bool FamilyHistoryHas(GameFamilyEvidence evidence, GameProfile profile, ProcEntry entry)
        {
            return evidence.Contains(profile, entry.Pid, entry.Creation, entry.Path);
        }

        private static void FamilyHistoryRequire(bool value, string message)
        {
            if (!value) throw new Exception("Game family history: " + message);
        }

        private static void FamilyHistorySnapshotChain()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 0, chain);
            FamilyHistoryRequire(!profile.ContainsPath(chain[2].Path), "fixture accidentally declared the renderer Root");
            foreach (ProcEntry entry in chain)
                FamilyHistoryRequire(FamilyHistoryHas(evidence, profile, entry), "live descendant missing");
        }

        private static void FamilyHistoryRetainsExitedAncestors()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            FamilyHistoryCapture(history, profile, 0, chain);
            FamilyHistoryRequire(FamilyHistoryHas(FamilyHistoryCapture(history, profile, 100, chain[1], chain[2]),
                profile, chain[2]), "launcher exit lost existing renderer");
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 3600000, chain[2]);
            FamilyHistoryRequire(FamilyHistoryHas(evidence, profile, chain[2]), "long match expired referenced ancestors");
            FamilyHistoryRequire(history.RetainedNodeCount == 3, "referenced ancestor chain not retained exactly");
        }

        private static void FamilyHistoryNoLiveImpostors()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            FamilyHistoryCapture(history, profile, 0, chain);
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 1, chain[2]);
            FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, chain[0])
                && !FamilyHistoryHas(evidence, profile, chain[1]), "dead parent exported as live member");
            FamilyHistoryRequire(FamilyHistoryHas(evidence, profile, chain[2]), "live child not exported");
        }

        private static void FamilyHistoryUnorderedBatch()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            history.ObserveEvents(new ProcessChangeBatch(new[]
            {
                FamilyHistoryStart(chain[2], 0),
                new ProcessChange { Pid = 102, Kind = ProcessChangeKind.Stopped },
                FamilyHistoryStart(chain[1], 0), FamilyHistoryStart(chain[0], 0),
                new ProcessChange { Pid = 101, Kind = ProcessChangeKind.Stopped }
            }, false), 7, 0);
            FamilyHistoryRequire(FamilyHistoryHas(FamilyHistoryCapture(history, profile, 750, chain[2]),
                profile, chain[2]), "coalesced short parent chain depended on callback order");
        }

        private static void FamilyHistoryExplicitParent()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            history.ObserveEvents(new ProcessChangeBatch(new[] { FamilyHistoryStart(chain[0], 0) }, false), 7, 0);
            history.ObserveEvents(new ProcessChangeBatch(new[] { FamilyHistoryStart(chain[1], 100) }, false), 7, 1);
            history.ObserveEvents(new ProcessChangeBatch(new[] { FamilyHistoryStart(chain[2], 200) }, false), 7, 2);
            FamilyHistoryRequire(FamilyHistoryHas(FamilyHistoryCapture(history, profile, 3, chain[2]),
                profile, chain[2]), "verified cross-batch parent identity not used");
        }

        private static void FamilyHistoryPidOnlyParentRejected()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            FamilyHistoryCapture(history, profile, 0, chain[0]);
            history.ObserveEvents(new ProcessChangeBatch(new[] { FamilyHistoryStart(chain[1], 0) }, false), 7, 1);
            FamilyHistoryRequire(!FamilyHistoryHas(FamilyHistoryCapture(history, profile, 2, chain[1]),
                profile, chain[1]), "unverified cached parent PID granted family membership");
        }

        private static void FamilyHistoryParentPidReuse()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            FamilyHistoryCapture(history, profile, 0, chain);
            ProcEntry impostor = FamilyHistoryProcess(102, 10, 400, @"D:\Other\browser.exe");
            ProcEntry unrelated = FamilyHistoryProcess(104, 102, 500, @"D:\Other\new-child.exe");
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 100, chain[2], impostor, unrelated);
            FamilyHistoryRequire(FamilyHistoryHas(evidence, profile, chain[2]), "PID reuse invalidated an already-proved old child");
            FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, impostor)
                && !FamilyHistoryHas(evidence, profile, unrelated), "PID reuse expanded the family");
        }

        private static void FamilyHistoryStaleExplicitRejected()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            FamilyHistoryCapture(history, profile, 0, chain);
            ProcEntry impostor = FamilyHistoryProcess(102, 10, 400, @"D:\Other\browser.exe");
            FamilyHistoryCapture(history, profile, 1, impostor, chain[2]);
            ProcEntry unrelated = FamilyHistoryProcess(104, 102, 500, @"D:\Other\new-child.exe");
            history.ObserveEvents(new ProcessChangeBatch(new[] { FamilyHistoryStart(unrelated, 200) }, false), 7, 2);
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 3, unrelated);
            FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, unrelated), "stale explicit parent contradicted known PID reuse");
        }

        private static void FamilyHistoryChildPidReuse()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            FamilyHistoryCapture(history, profile, 0, chain);
            ProcEntry reused = FamilyHistoryProcess(103, 10, 500, chain[2].Path);
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 1, reused);
            FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, reused)
                && !FamilyHistoryHas(evidence, profile, chain[2]), "child PID reuse inherited prior lifetime membership");
        }

        private static void FamilyHistorySessionBoundary()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            chain[0].Session = 8;
            FamilyHistoryRequire(!FamilyHistoryHas(FamilyHistoryCapture(history, profile, 0, chain),
                profile, chain[2]), "cross-session parent entered the chain");
            history.ObserveEvents(new ProcessChangeBatch(new[]
            {
                FamilyHistoryStart(chain[0], 0), FamilyHistoryStart(chain[1], 100), FamilyHistoryStart(chain[2], 200)
            }, false), 7, 1);
            FamilyHistoryRequire(!FamilyHistoryHas(FamilyHistoryCapture(history, profile, 2, chain[2]),
                profile, chain[2]), "cross-session event parent entered the chain");
        }

        private static void FamilyHistoryAmbiguousSnapshot()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            ProcEntry duplicate = FamilyHistoryProcess(101, 10, 100, @"D:\Other\ambiguous.exe");
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 0, chain[0], duplicate, chain[1], chain[2]);
            FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, chain[2]), "duplicate PID snapshot picked a convenient parent");
        }

        private static void FamilyHistoryAmbiguousEvents()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            history.ObserveEvents(new ProcessChangeBatch(new[]
            {
                FamilyHistoryStart(chain[0], 0), FamilyHistoryStart(chain[0], 0),
                FamilyHistoryStart(chain[1], 100), FamilyHistoryStart(chain[2], 200)
            }, false), 7, 0);
            FamilyHistoryRequire(!FamilyHistoryHas(FamilyHistoryCapture(history, profile, 1, chain[2]),
                profile, chain[2]), "ambiguous event PID picked a parent");
        }

        private static void FamilyHistoryIncompleteIdentities()
        {
            Action<ProcEntry>[] corrupt =
            {
                p => p.Creation = 0, p => p.Path = null, p => p.Name = "different",
                p => p.Path = @"relative\entry.exe", p => p.Path = @"C:entry.exe",
                p => p.Path = @"\Title\entry.exe", p => p.Path = @"C:\Title\..\entry.exe",
                p => p.Session = -1
            };
            foreach (Action<ProcEntry> change in corrupt)
            {
                var history = new GameFamilyHistory();
                GameProfile profile = FamilyHistoryProfile();
                ProcEntry[] chain = FamilyHistoryChain(profile);
                change(chain[0]);
                FamilyHistoryRequire(!FamilyHistoryHas(FamilyHistoryCapture(history, profile, 0, chain),
                    profile, chain[2]), "incomplete parent identity accepted");
            }
        }

        private static void FamilyHistoryProfileFingerprint()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 0, chain);
            Action<GameProfile>[] changes =
            {
                p => p.Id += "-new", p => p.Root += "-new", p => p.ExecutablePath += ".new",
                p => p.LearnedExecutablePath = chain[2].Path, p => p.ForceTrigger = true
            };
            foreach (Action<GameProfile> change in changes)
            {
                GameProfile changed = profile.Clone(); change(changed);
                FamilyHistoryRequire(!FamilyHistoryHas(evidence, changed, chain[2]), "changed profile reused stale evidence");
            }
            GameProfile cosmetic = profile.Clone();
            cosmetic.Id = cosmetic.Id.ToUpperInvariant(); cosmetic.Root = cosmetic.Root.ToUpperInvariant();
            cosmetic.ExecutablePath = cosmetic.ExecutablePath.ToUpperInvariant(); cosmetic.Name = "renamed";
            cosmetic.Overrides["unrelated"] = "1";
            FamilyHistoryRequire(FamilyHistoryHas(evidence, cosmetic, chain[2]), "cosmetic/policy edit changed configuration identity");
        }

        private static void FamilyHistoryImmutableEvidence()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            GameProfile original = profile.Clone();
            ProcEntry originalRenderer = FamilyHistoryProcess(103, 102, 300, chain[2].Path);
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 0, chain);
            profile.Root = @"D:\Other"; chain[2].Path = @"D:\Other\changed.exe";
            history.Clear();
            FamilyHistoryRequire(FamilyHistoryHas(evidence, original, originalRenderer), "published evidence mutated with source/history");
            FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, originalRenderer), "mutated source profile matched old evidence");
            FamilyHistoryRequire(!evidence.Contains(original, 103, 301, originalRenderer.Path)
                && !evidence.Contains(original, 103, 300, chain[2].Path), "evidence matched wrong creation/path");
        }

        private static void FamilyHistorySharedHostBoundary()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            ProcEntry host = FamilyHistoryProcess(10, 0, 10, @"D:\Shared\host.exe");
            ProcEntry sibling = FamilyHistoryProcess(104, 10, 400, @"D:\DifferentTitle\another.exe");
            ProcEntry siblingChild = FamilyHistoryProcess(105, 104, 500, @"D:\DifferentTitle\child.exe");
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 0,
                host, chain[0], chain[1], chain[2], sibling, siblingChild);
            FamilyHistoryRequire(FamilyHistoryHas(evidence, profile, chain[2]), "own descendant missing");
            FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, host)
                && !FamilyHistoryHas(evidence, profile, sibling) && !FamilyHistoryHas(evidence, profile, siblingChild),
                "walked up to common host and back down to unrelated siblings");
        }

        private static void FamilyHistoryDepthBoundary()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            var chain = new List<ProcEntry>();
            for (int i = 0; i <= GameFamilyHistory.MaxAncestorDepth + 1; i++)
                chain.Add(FamilyHistoryProcess(101 + i, i == 0 ? 10 : 100 + i, 100 + i,
                    i == 0 ? profile.ExecutablePath : @"D:\Detached\node" + i + ".exe"));
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 0, chain.ToArray());
            FamilyHistoryRequire(FamilyHistoryHas(evidence, profile, chain[GameFamilyHistory.MaxAncestorDepth]),
                "valid depth boundary lost");
            FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, chain[GameFamilyHistory.MaxAncestorDepth + 1]),
                "unbounded ancestor walk");
        }

        private static void FamilyHistoryInvalidCreationChains()
        {
            foreach (long childCreation in new long[] { 99, 100 })
            {
                var history = new GameFamilyHistory();
                GameProfile profile = FamilyHistoryProfile();
                ProcEntry[] chain = FamilyHistoryChain(profile);
                chain[1].Creation = childCreation;
                chain[0].ParentPid = chain[1].Pid;
                FamilyHistoryRequire(!FamilyHistoryHas(FamilyHistoryCapture(history, profile, 0, chain),
                    profile, chain[2]), "reversed/equal timestamp parent or cycle granted membership");
            }
        }

        private static void FamilyHistoryPruning()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            FamilyHistoryCapture(history, profile, 0, chain);
            FamilyHistoryCapture(history, profile, 1);
            FamilyHistoryRequire(history.RetainedNodeCount == 3, "short coalescing tail disappeared");
            FamilyHistoryCapture(history, profile, GameFamilyHistory.PendingEventRetentionMs + 1);
            FamilyHistoryRequire(history.RetainedNodeCount == 0, "finished family history leaked indefinitely");
            FamilyHistoryRequire(!FamilyHistoryHas(FamilyHistoryCapture(history, profile, 10000, chain[2]),
                profile, chain[2]), "pruned history reappeared by PID only");
        }

        private static void FamilyHistoryClearAndClockReset()
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                var history = new GameFamilyHistory();
                GameProfile profile = FamilyHistoryProfile();
                ProcEntry[] chain = FamilyHistoryChain(profile);
                FamilyHistoryCapture(history, profile, 100, chain);
                if (mode == 0) history.Clear();
                if (mode == 2)
                    history.ObserveEvents(new ProcessChangeBatch(new ProcessChange[0], false), 8, 101);
                GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, mode == 1 ? 50 : 102, chain[2]);
                FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, chain[2]), "clear/clock/session reset preserved stale association");
            }
        }

        private static void FamilyHistoryQuota()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            var entries = new List<ProcEntry>();
            for (int i = 0; i <= GameFamilyHistory.MaxNodes; i++)
                entries.Add(FamilyHistoryProcess(10000 + i, 0, 10000 + i, @"D:\Quota\entry" + i + ".exe"));
            GameFamilyEvidence over = FamilyHistoryCapture(history, profile, 0, entries.ToArray());
            FamilyHistoryRequire(ReferenceEquals(over, GameFamilyEvidence.Empty)
                && history.RetainedNodeCount == 0, "oversized snapshot exceeded node quota");
            entries.RemoveAt(entries.Count - 1);
            FamilyHistoryCapture(history, profile, 1, entries.ToArray());
            FamilyHistoryRequire(history.RetainedNodeCount == GameFamilyHistory.MaxNodes, "valid quota fixture not admitted");
            history.ObserveEvents(new ProcessChangeBatch(new[]
            {
                FamilyHistoryStart(FamilyHistoryProcess(50000, 0, 50000, profile.ExecutablePath), 0)
            }, false), 7, 2);
            FamilyHistoryRequire(history.RetainedNodeCount == 0, "quota exhaustion retained a partial permissive association");
        }

        private static void FamilyHistoryOverflow()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            FamilyHistoryCapture(history, profile, 0, chain);
            history.ObserveEvents(new ProcessChangeBatch(new ProcessChange[0], true), 7, 1);
            FamilyHistoryRequire(history.RetainedNodeCount == 0, "overflow retained potentially incomplete history");
            FamilyHistoryRequire(!FamilyHistoryHas(FamilyHistoryCapture(history, profile, 2, chain[2]),
                profile, chain[2]), "overflow invented a missing chain");
        }

        private static void FamilyHistoryLateStartBoundary()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 0, chain[2]);
            FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, chain[2]), "never-observed launcher guessed from sibling directory");
            FamilyHistoryRequire(ReferenceEquals(GameFamilyEvidence.Empty,
                history.Capture(null, new[] { profile }, 7, 1)), "null snapshot produced live evidence");
        }

        private static void FamilyHistoryRootAndLearnedSeeds()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            ProcEntry sameRoot = FamilyHistoryProcess(104, 10, 400, profile.Root + @"\helper.exe");
            FamilyHistoryRequire(FamilyHistoryHas(FamilyHistoryCapture(history, profile, 0, sameRoot),
                profile, sameRoot), "declared root no longer seeds membership");
            profile.Root = null; profile.LearnedExecutablePath = chain[2].Path;
            FamilyHistoryRequire(FamilyHistoryHas(FamilyHistoryCapture(history, profile, 1, chain[2]),
                profile, chain[2]), "learned exact path no longer seeds membership");
            profile.LearnedExecutablePath = null; profile.Root = @"C:\";
            FamilyHistoryRequire(!FamilyHistoryHas(FamilyHistoryCapture(history, profile, 2, chain[2]),
                profile, chain[2]), "volume root acted as universal family seed");
        }

        private static void FamilyHistoryIdentityConflict()
        {
            foreach (bool pathConflict in new[] { true, false })
            {
                var history = new GameFamilyHistory();
                GameProfile profile = FamilyHistoryProfile();
                ProcEntry[] chain = FamilyHistoryChain(profile);
                FamilyHistoryCapture(history, profile, 0, chain);
                ProcEntry corrupted = FamilyHistoryProcess(102, pathConflict ? 101 : 999, 200,
                    pathConflict ? @"D:\Other\changed.exe" : chain[1].Path);
                GameFamilyEvidence evidence = FamilyHistoryCapture(history, profile, 1, corrupted, chain[2]);
                FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, chain[2]), "contradictory immutable identity was reused");
                evidence = FamilyHistoryCapture(history, profile, 2, chain[1], chain[2]);
                FamilyHistoryRequire(!FamilyHistoryHas(evidence, profile, chain[2]), "conflicting identity was silently unpoisoned");
            }
        }

        private static void FamilyHistoryDelayedParentEvent()
        {
            var history = new GameFamilyHistory();
            GameProfile profile = FamilyHistoryProfile();
            ProcEntry[] chain = FamilyHistoryChain(profile);
            history.ObserveEvents(new ProcessChangeBatch(new[] { FamilyHistoryStart(chain[2], 200) }, false), 7, 0);
            history.ObserveEvents(new ProcessChangeBatch(new[] { FamilyHistoryStart(chain[1], 100) }, false), 7, 1);
            history.ObserveEvents(new ProcessChangeBatch(new[] { FamilyHistoryStart(chain[0], 0) }, false), 7, 2);
            FamilyHistoryRequire(FamilyHistoryHas(FamilyHistoryCapture(history, profile, 3, chain[2]),
                profile, chain[2]), "verified delayed parent identity could not complete the chain");
        }

        private static void FamilyHistorySafetyBoundary()
        {
            foreach (string path in new[]
            {
                @"D:\Security\AntiCheatBridge.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
            })
            {
                var history = new GameFamilyHistory();
                GameProfile profile = FamilyHistoryProfile();
                ProcEntry[] chain = FamilyHistoryChain(profile);
                chain[1].Path = path;
                chain[1].Name = Path.GetFileNameWithoutExtension(path);
                FamilyHistoryRequire(GameSessionDetector.ElectionVetoed(chain[1].Name, chain[1].Path),
                    "safety fixture is not vetoed by the existing detector");
                FamilyHistoryRequire(!FamilyHistoryHas(FamilyHistoryCapture(history, profile, 0, chain),
                    profile, chain[2]), "history bridged a vetoed system/security node");
                history.Clear();
                history.ObserveEvents(new ProcessChangeBatch(new[]
                {
                    FamilyHistoryStart(chain[0], 0), FamilyHistoryStart(chain[1], 100),
                    FamilyHistoryStart(chain[2], 200)
                }, false), 7, 1);
                FamilyHistoryRequire(!FamilyHistoryHas(FamilyHistoryCapture(history, profile, 2, chain[2]),
                    profile, chain[2]), "events bridged a vetoed system/security node");
            }
        }
    }
}
#endif
