// @author bdth 2074055628@qq.com
// 文件用途 纯快照验证独立前台渲染候选 不启动进程 不采 GPU 不读写游戏库
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // 由总自测入口注册这一项即可；子项不依赖主窗口、设置或本机安装目录。
        private static void TestRendererCandidateDetection()
        {
            TestRendererCandidateWindowedChallenge();
            TestRendererCandidateHardEvidence();
            TestRendererCandidateForceSafetyOnly();
            TestRendererCandidateAssociation();
            TestRendererCandidateIdentityBoundary();
            TestRendererCandidateRoleBoundary();
            TestRendererCandidateBoundedAncestry();
            TestRendererCandidateCrossProfile();
            TestRendererCandidateExactOwnership();
            TestRendererCandidateNarrowRoot();
            TestRendererCandidateAmbiguousOwnership();
        }

        private static GameProfile RendererCandidateProfile()
        {
            return new GameProfile
            {
                Id = "renderer-candidate-test",
                Name = "Renderer candidate test",
                Root = @"C:\PaviseCandidateTests\Title",
                ExecutablePath = @"C:\PaviseCandidateTests\Title\GameLauncher.exe"
            };
        }

        private static GameProcessSnapshot RendererCandidateProcess(
            int pid, string path, int parent, long creation, bool foreground)
        {
            return new GameProcessSnapshot
            {
                Pid = pid,
                ParentPid = parent,
                Creation = creation,
                Path = path,
                Name = Path.GetFileNameWithoutExtension(path),
                Visible = true,
                Foreground = foreground
            };
        }

        private static GameDetection RendererCandidateIncumbent(
            GameProfile profile, GameProcessSnapshot identity)
        {
            return new GameDetection
            {
                Profile = profile.Clone(),
                RendererPid = identity.Pid,
                RendererCreation = identity.Creation,
                RendererName = identity.Name,
                RendererPath = identity.Path,
                RendererForeground = identity.Foreground,
                RendererCandidateSelected = true,
                RendererUserSelected = true
            };
        }

        private static GameDetection RequireRendererCandidate(
            IList<GameProcessSnapshot> snapshot, GameProfile profile,
            GameDetection incumbent, int expectedPid)
        {
            GameDetection candidate = GameSessionDetector.FindForegroundCandidateSnapshot(
                snapshot, profile, incumbent);
            if (candidate == null) throw new Exception("foreground renderer candidate missing");
            Eq(expectedPid, candidate.RendererPid);
            Eq(true, candidate.RendererForeground);
            Eq(true, candidate.FamilyPids.Contains(expectedPid));
            return candidate;
        }

        private static void TestRendererCandidateWindowedChallenge()
        {
            GameProfile profile = RendererCandidateProfile();
            var old = RendererCandidateProcess(100, profile.ExecutablePath, 1, 1000, false);
            var game = RendererCandidateProcess(101,
                Path.Combine(profile.Root, @"Game\ActualGame.exe"), 100, 2000, true);
            GameDetection incumbent = RendererCandidateIncumbent(profile, old);
            GameDetection candidate = RequireRendererCandidate(
                new[] { old, game }, profile, incumbent, 101);
            Eq(true, candidate.RequiresGpuConfirm);
            Eq(false, candidate.RendererCandidateSelected);
            Eq(false, candidate.RendererUserSelected);
            Eq(false, candidate.RendererSafetyOnly);
            Eq(true, candidate.RendererLearnable);
            Eq(true, candidate.FamilyPids.Contains(100));
            Eq(100, incumbent.RendererPid);
            Eq(null, profile.LearnedExecutablePath);
            Eq(false, object.ReferenceEquals(profile, candidate.Profile));
            Eq(false, old.Foreground);

            // 人为构造旧 learned 验证挑战者不会被其吞掉，不声称生产中自动学错。
            profile.LearnedExecutablePath = old.Path;
            candidate = RequireRendererCandidate(new[] { old, game }, profile, incumbent, 101);
            Eq(true, candidate.RequiresGpuConfirm);
            Eq(old.Path, profile.LearnedExecutablePath);
            Eq(100, incumbent.RendererPid);
        }

        private static void TestRendererCandidateHardEvidence()
        {
            GameProfile profile = RendererCandidateProfile();
            var game = RendererCandidateProcess(101,
                Path.Combine(profile.Root, "ActualGame.exe"), 1, 2000, true);
            game.FullscreenLike = true;
            GameDetection candidate = RequireRendererCandidate(new[] { game }, profile, null, 101);
            Eq(false, candidate.RequiresGpuConfirm);
            Eq(true, candidate.RendererCandidateSelected);
            Eq(true, candidate.RendererLearnable);
            Eq(false, candidate.RendererUserSelected);

            game.FullscreenLike = false;
            profile.ExecutablePath = game.Path;
            candidate = RequireRendererCandidate(new[] { game }, profile, null, 101);
            Eq(false, candidate.RequiresGpuConfirm);
            Eq(true, candidate.RendererCandidateSelected);
            Eq(true, candidate.RendererUserSelected);
            Eq(false, candidate.RendererLearnable);
            Eq(3, candidate.RendererMatchRank);

            profile.ExecutablePath = Path.Combine(profile.Root, "GameLauncher.exe");
            profile.LearnedExecutablePath = game.Path;
            candidate = RequireRendererCandidate(new[] { game }, profile, null, 101);
            Eq(false, candidate.RequiresGpuConfirm);
            Eq(true, candidate.RendererCandidateSelected);
            Eq(true, candidate.RendererUserSelected);
            Eq(false, candidate.RendererLearnable);
            Eq(2, candidate.RendererMatchRank);

            // 前台本身也是窗口证据；仅可见、未在前台的进程不是新挑战者。
            game.Visible = false;
            RequireRendererCandidate(new[] { game }, profile, null, 101);
            game.Visible = true;
            game.Foreground = false;
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, profile, null));
        }

        private static void TestRendererCandidateForceSafetyOnly()
        {
            GameProfile profile = RendererCandidateProfile();
            profile.ForceTrigger = true;
            var game = RendererCandidateProcess(101,
                Path.Combine(profile.Root, "ActualGame.exe"), 1, 2000, true);
            for (int fullscreen = 0; fullscreen <= 1; fullscreen++)
            {
                game.FullscreenLike = fullscreen != 0;
                GameDetection candidate = RequireRendererCandidate(new[] { game }, profile, null, 101);
                Eq(true, candidate.RendererSafetyOnly);
                Eq(false, candidate.RequiresGpuConfirm);
                Eq(false, candidate.RendererCandidateSelected);
                Eq(false, candidate.RendererUserSelected);
                Eq(false, candidate.RendererLearnable);
                Eq(null, candidate.Evidence);
                Eq(null, profile.LearnedExecutablePath);
            }

            profile.LearnedExecutablePath = game.Path;
            GameDetection learned = RequireRendererCandidate(new[] { game }, profile, null, 101);
            Eq(false, learned.RendererSafetyOnly);
            Eq(true, learned.RendererCandidateSelected);
            Eq(true, learned.RendererUserSelected);
            Eq(false, learned.RequiresGpuConfirm);
            Eq(false, learned.RendererLearnable);

            // 强制云游戏精确路径保留既有语义，不把允许的浏览器目标改成 SafetyOnly。
            var chrome = RendererCandidateProcess(102,
                Path.Combine(profile.Root, "chrome.exe"), 1, 3000, true);
            profile.ExecutablePath = chrome.Path;
            GameDetection exact = RequireRendererCandidate(new[] { chrome }, profile, null, 102);
            Eq(true, exact.RendererCandidateSelected);
            Eq(false, exact.RendererSafetyOnly);
            Eq(false, exact.RendererLearnable);
            profile.ExecutablePath = Path.Combine(profile.Root, "GameLauncher.exe");
            GameDetection unknown = RequireRendererCandidate(new[] { chrome }, profile, null, 102);
            Eq(true, unknown.RendererSafetyOnly);
            Eq(false, unknown.RendererCandidateSelected);

            var antiCheat = RendererCandidateProcess(103,
                Path.Combine(profile.Root, "EasyAntiCheat.exe"), 1, 3000, true);
            profile.ExecutablePath = antiCheat.Path;
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { antiCheat }, profile, null));
        }

        private static void TestRendererCandidateAssociation()
        {
            GameProfile profile = RendererCandidateProfile();
            var parent = RendererCandidateProcess(100, profile.ExecutablePath, 1, 1000, false);
            var game = RendererCandidateProcess(101,
                @"C:\PaviseCandidateTests\OtherTitle\ActualGame.exe", 100, 2000, true);
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, profile, null));
            RequireRendererCandidate(new[] { parent, game }, profile, null, 101);

            // 父链基于实际身份，而不是启动器或客户端的名称名单。
            parent.Path = Path.Combine(profile.Root, "steam.exe");
            parent.Name = "steam";
            RequireRendererCandidate(new[] { parent, game }, profile, null, 101);
            var bridge = RendererCandidateProcess(102,
                @"C:\PaviseCandidateTests\Bridge\Worker.exe", 100, 1500, false);
            game.ParentPid = bridge.Pid;
            RequireRendererCandidate(new[] { parent, bridge, game }, profile, null, 101);
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { parent, game }, profile, null));
            bridge.Creation = game.Creation + 1;
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { parent, bridge, game }, profile, null));
            bridge.Creation = 1500;
            bridge.ParentPid = game.Pid;
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { parent, bridge, game }, profile, null));

            profile.LearnedExecutablePath = game.Path;
            RequireRendererCandidate(new[] { game }, profile, null, 101);
            profile.LearnedExecutablePath = null;
            game.Path = Path.Combine(profile.Root, @"Game\ActualGame.exe");
            RequireRendererCandidate(new[] { game }, profile, null, 101);
            game.Path = profile.Root + @"-backup\ActualGame.exe";
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, profile, null));
        }

        private static void TestRendererCandidateIdentityBoundary()
        {
            GameProfile profile = RendererCandidateProfile();
            string path = Path.Combine(profile.Root, "ActualGame.exe");
            var game = RendererCandidateProcess(101, path, 1, 2000, true);
            GameDetection incumbent = RendererCandidateIncumbent(profile, game);
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, profile, incumbent));
            incumbent.RendererPath = path.ToUpperInvariant();
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, profile, incumbent));
            game.Creation++;
            RequireRendererCandidate(new[] { game }, profile, incumbent, 101);

            var duplicate = RendererCandidateProcess(101, path, 1, 3000, false);
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game, duplicate }, profile, null));
            duplicate.Creation = 0;
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game, duplicate }, profile, null));
            var otherForeground = RendererCandidateProcess(102,
                @"C:\Unrelated\Other.exe", 1, 3000, true);
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game, otherForeground }, profile, null));

            Action<GameProcessSnapshot>[] invalidate =
            {
                delegate(GameProcessSnapshot p) { p.Pid = 0; },
                delegate(GameProcessSnapshot p) { p.Creation = 0; },
                delegate(GameProcessSnapshot p) { p.Name = null; },
                delegate(GameProcessSnapshot p) { p.Path = null; },
                delegate(GameProcessSnapshot p) { p.Name = "OtherImage"; },
                delegate(GameProcessSnapshot p) { p.Path = "ActualGame.exe"; },
                delegate(GameProcessSnapshot p) { p.Path = profile.Root + @"\..\Elsewhere\ActualGame.exe"; }
            };
            foreach (Action<GameProcessSnapshot> invalid in invalidate)
            {
                var broken = RendererCandidateProcess(101, path, 1, 2000, true);
                invalid(broken);
                Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                    new[] { broken }, profile, null));
            }
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(null, profile, null));
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, (GameProfile)null, null));
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new GameProcessSnapshot[] { null }, profile, null));
        }

        private static void TestRendererCandidateRoleBoundary()
        {
            GameProfile profile = RendererCandidateProfile();
            string[] roles = { "steam", "chrome", "notepad", "CrashReportClient", "telemetryWorker", "LeagueClientUxRender", "UnrealCEFSubProcess" };
            foreach (string role in roles)
            {
                var process = RendererCandidateProcess(101,
                    Path.Combine(profile.Root, role + ".exe"), 1, 2000, true);
                GameDetection pending = RequireRendererCandidate(new[] { process }, profile, null, 101);
                Eq(true, pending.RequiresGpuConfirm);
                process.FullscreenLike = true;
                GameDetection window = RequireRendererCandidate(new[] { process }, profile, null, 101);
                Eq(true, window.RendererCandidateSelected);
                Eq(0L, window.RendererGpuProofExpiresMs);
            }
            var antiCheat = RendererCandidateProcess(102,
                Path.Combine(profile.Root, "EasyAntiCheat.exe"), 1, 2000, true);
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { antiCheat }, profile, null));
            // 不把名字里带 Launcher 的普通游戏当成已知平台，亦不靠名字猜关联。
            var generic = RendererCandidateProcess(101,
                Path.Combine(profile.Root, "SomeGameLauncher.exe"), 1, 2000, true);
            RequireRendererCandidate(new[] { generic }, profile, null, 101);
            generic.Path = @"C:\Other\SomeGameLauncher.exe";
            profile.Entries.Add(generic.Name);
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { generic }, profile, null));
        }

        private static void TestRendererCandidateBoundedAncestry()
        {
            GameProfile profile = RendererCandidateProfile();
            var snapshot = new List<GameProcessSnapshot>();
            snapshot.Add(RendererCandidateProcess(700, profile.ExecutablePath, 1, 1000, false));
            for (int depth = 1; depth <= 25; depth++)
                snapshot.Add(RendererCandidateProcess(700 + depth,
                    @"C:\PaviseCandidateTests\Outside\Worker" + depth + ".exe",
                    699 + depth, 1000 + depth, depth == 25));
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(snapshot, profile, null));
            snapshot.RemoveAt(snapshot.Count - 1);
            snapshot[snapshot.Count - 1].Foreground = true;
            RequireRendererCandidate(snapshot, profile, null, 724);

            // 重复 PID 的父锚不能用最后一项覆盖后继续串起候选。
            snapshot.Add(RendererCandidateProcess(700, profile.ExecutablePath, 1, 1000, false));
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(snapshot, profile, null));
        }

        private static GameDetection RequireLibraryRendererCandidate(
            IList<GameProcessSnapshot> snapshot, IList<GameProfile> profiles,
            GameDetection incumbent, int expectedPid, string expectedProfileId)
        {
            GameDetection candidate = GameSessionDetector.FindForegroundCandidateSnapshot(
                snapshot, profiles, incumbent);
            if (candidate == null) throw new Exception("library foreground candidate missing");
            Eq(expectedPid, candidate.RendererPid);
            Eq(expectedProfileId, candidate.Profile.Id);
            Eq(true, candidate.FamilyPids.Contains(expectedPid));
            return candidate;
        }

        private static void TestRendererCandidateCrossProfile()
        {
            GameProfile a = RendererCandidateProfile();
            a.Id = "profile-a";
            a.LearnedExecutablePath = a.ExecutablePath;
            GameProfile b = RendererCandidateProfile();
            b.Id = "profile-b";
            b.Root = @"C:\PaviseCandidateTests\AnotherTitle";
            b.ExecutablePath = Path.Combine(b.Root, "OtherLauncher.exe");
            var old = RendererCandidateProcess(100, a.ExecutablePath, 1, 1000, false);
            var game = RendererCandidateProcess(101,
                Path.Combine(b.Root, @"Game\ActualGame.exe"), 1, 2000, true);
            for (int forced = 0; forced <= 1; forced++)
            {
                a.ForceTrigger = forced != 0;
                GameDetection incumbent = RendererCandidateIncumbent(a, old);
                GameDetection candidate = RequireLibraryRendererCandidate(
                    new[] { old, game }, new[] { a, b }, incumbent, 101, b.Id);
                Eq(true, candidate.RequiresGpuConfirm);
                Eq(false, candidate.RendererCandidateSelected);
                Eq(false, candidate.RendererSafetyOnly);
                RequireLibraryRendererCandidate(new[] { old, game }, new[] { b, a }, incumbent, 101, b.Id);
                Eq(100, incumbent.RendererPid);
                Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                    new[] { old, game }, a, incumbent));
            }

            game.Foreground = false;
            old.Foreground = true;
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { old, game }, new[] { a, b }, RendererCandidateIncumbent(a, old)));
        }

        private static void TestRendererCandidateExactOwnership()
        {
            GameProfile broad = RendererCandidateProfile();
            broad.Id = "broad";
            GameProfile exact = broad.Clone();
            exact.Id = "exact";
            var game = RendererCandidateProcess(101,
                Path.Combine(broad.Root, "ActualGame.exe"), 1, 2000, true);
            exact.ExecutablePath = game.Path;
            broad.LearnedExecutablePath = game.Path;
            GameDetection candidate = RequireLibraryRendererCandidate(
                new[] { game }, new[] { broad, exact }, null, 101, exact.Id);
            Eq(3, candidate.RendererMatchRank);
            RequireLibraryRendererCandidate(new[] { game }, new[] { exact, broad }, null, 101, exact.Id);

            exact.ExecutablePath = Path.Combine(exact.Root, "AnotherEntry.exe");
            RequireLibraryRendererCandidate(new[] { game }, new[] { exact, broad }, null, 101, broad.Id);
            broad.LearnedExecutablePath = null;
            broad.Root = @"C:\PaviseCandidateTests\ExternalLauncher";
            broad.ExecutablePath = Path.Combine(broad.Root, "Entry.exe");
            var parent = RendererCandidateProcess(100, broad.ExecutablePath, 1, 1000, false);
            game.ParentPid = parent.Pid;
            candidate = RequireLibraryRendererCandidate(new[] { parent, game },
                new[] { broad, exact }, null, 101, exact.Id);
            Eq(1, candidate.RendererMatchRank);

            exact.ExecutablePath = game.Path;
            RequireLibraryRendererCandidate(new[] { game }, new[] { exact, exact.Clone() }, null, 101, exact.Id);
        }

        private static void TestRendererCandidateNarrowRoot()
        {
            GameProfile broad = RendererCandidateProfile();
            broad.Id = "broad-root";
            GameProfile narrow = broad.Clone();
            narrow.Id = "narrow-root";
            narrow.Root = Path.Combine(broad.Root, "SpecificTitle");
            narrow.ExecutablePath = Path.Combine(narrow.Root, "GameLauncher.exe");
            var game = RendererCandidateProcess(101,
                Path.Combine(narrow.Root, @"Binaries\ActualGame.exe"), 1, 2000, true);
            RequireLibraryRendererCandidate(new[] { game }, new[] { broad, narrow }, null, 101, narrow.Id);
            RequireLibraryRendererCandidate(new[] { game }, new[] { narrow, broad }, null, 101, narrow.Id);

            GameProfile overlapping = narrow.Clone();
            overlapping.Id = "same-root-different-profile";
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, new[] { broad, narrow, overlapping }, null));
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, new[] { overlapping, narrow, broad }, null));

            GameProfile exact = narrow.Clone();
            exact.Id = "exact-over-ambiguous-roots";
            exact.ExecutablePath = game.Path;
            RequireLibraryRendererCandidate(new[] { game }, new[] { narrow, overlapping, exact }, null, 101, exact.Id);
            RequireLibraryRendererCandidate(new[] { game }, new[] { exact, overlapping, narrow }, null, 101, exact.Id);
        }

        private static void TestRendererCandidateAmbiguousOwnership()
        {
            GameProfile a = RendererCandidateProfile();
            a.Id = "owner-a";
            GameProfile b = a.Clone();
            b.Id = "owner-b";
            var game = RendererCandidateProcess(101,
                Path.Combine(a.Root, "ActualGame.exe"), 1, 2000, true);
            a.ExecutablePath = game.Path;
            b.ExecutablePath = game.Path;
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, new[] { a, b }, null));
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, new[] { b, a }, null));
            a.ExecutablePath = Path.Combine(a.Root, "EntryA.exe");
            b.ExecutablePath = Path.Combine(b.Root, "EntryB.exe");
            a.LearnedExecutablePath = game.Path;
            b.LearnedExecutablePath = game.Path;
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, new[] { a, b }, null));
            a.LearnedExecutablePath = null;
            b.LearnedExecutablePath = null;
            game.Path = @"C:\PaviseCandidateTests\Outside\ActualGame.exe";
            var parentA = RendererCandidateProcess(100, a.ExecutablePath, 1, 1000, false);
            var parentB = RendererCandidateProcess(102, b.ExecutablePath, 100, 1500, false);
            game.ParentPid = parentB.Pid;
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { parentA, parentB, game }, new[] { a, b }, null));

            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, (IList<GameProfile>)null, null));
            Eq<GameDetection>(null, GameSessionDetector.FindForegroundCandidateSnapshot(
                new[] { game }, new GameProfile[0], null));
            Eq<GameDetection>(null, GameSessionDetector.CaptureForegroundCandidate(
                null, 1, new[] { a, b }, null));
        }
    }
}
