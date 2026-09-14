#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // Adding a folder to the library: a single hit resolves directly, multiple candidates go to the list, an empty folder is an error
        internal static void RunAddGameFolderRegressionTests()
        {
            AddGameFolderUniqueMainResolves();
            AddGameFolderAmbiguousListsCandidates();
            AddGameFolderEmptyAndMissing();
            AddGameFolderLockedExecutableKeepsReadableCandidates();
            AddGameFolderDeeplyNestedExecutableResolves();
        }

        private static void AddGameFolderUniqueMainResolves()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string game = fixture.WriteExecutable(@"Binaries\Win64\Game.exe", "d3d11.dll");
                string tool = fixture.WriteExecutable("tool.exe", "kernel32.dll");
                Eq(game, ExecutableCandidateProbe.PickMainExecutable(fixture.Root));

                List<ExecutableCandidateFacts> list = ExecutableCandidateProbe.ListCandidates(fixture.Root, 24);
                Eq(2, list.Count);
                // Graphics evidence ranks ahead of the GUI subsystem
                Eq(game, list[0].Path);
                Eq(tool, list[1].Path);
                Eq(2, ExecutableCandidateProbe.Rank(list[0]));
                Eq(1, ExecutableCandidateProbe.Rank(list[1]));
                Eq(1, ExecutableCandidateProbe.ListCandidates(fixture.Root, 1).Count);

                string resolved, error, suggested;
                Eq(true, GameExecutableResolver.TryResolve(fixture.Root, out resolved, out error, out suggested));
                Eq(game, resolved);
                Eq(Path.GetFileName(fixture.Root), suggested);
                // Folder paths with trailing slash and quotes are accepted too
                Eq(true, GameExecutableResolver.TryResolve("\"" + fixture.Root + "\\\"", out resolved, out error));
                Eq(game, resolved);
            }
        }

        private static void AddGameFolderAmbiguousListsCandidates()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string a = fixture.WriteExecutable("Alpha.exe", "d3d12.dll");
                string b = fixture.WriteExecutable("Beta.exe", "d3d11.dll");
                Eq<string>(null, ExecutableCandidateProbe.PickMainExecutable(fixture.Root));
                List<ExecutableCandidateFacts> list = ExecutableCandidateProbe.ListCandidates(fixture.Root, 24);
                Eq(2, list.Count);
                Eq(a, list[0].Path);
                Eq(b, list[1].Path);

                string recommended;
                Eq(1, ExecutableCandidateProbe.ListCandidates(fixture.Root, 1, out recommended).Count);
                Eq<string>(null, recommended);

                string resolved, error;
                Eq(false, GameExecutableResolver.TryResolve(fixture.Root, out resolved, out error));
                Eq(Lang.T("t.gameexecutableresolver.8"), error);
                Eq<string>(null, resolved);
            }
        }

        private static void AddGameFolderEmptyAndMissing()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                Directory.CreateDirectory(Path.Combine(fixture.Root, "Data"));
                File.WriteAllText(Path.Combine(fixture.Root, "readme.txt"), "x");
                Eq(0, ExecutableCandidateProbe.ListCandidates(fixture.Root, 24).Count);
                string resolved, error;
                Eq(false, GameExecutableResolver.TryResolve(fixture.Root, out resolved, out error));
                Eq(Lang.T("t.gameexecutableresolver.8"), error);

                string missing = Path.Combine(fixture.Root, "nope");
                Eq(false, GameExecutableResolver.TryResolve(missing, out resolved, out error));
                Eq(Lang.T("t.gameexecutableresolver.3"), error);
                Eq(0, ExecutableCandidateProbe.ListCandidates(missing, 24).Count);
                Eq(0, ExecutableCandidateProbe.ListCandidates(null, 24).Count);
            }
        }

        private static void AddGameFolderLockedExecutableKeepsReadableCandidates()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string locked = fixture.WriteExecutable("Unreadable.exe", "kernel32.dll");
                string game = fixture.WriteExecutable(@"Binaries\Win64\Game.exe", "d3d11.dll");
                using (var file = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    bool complete;
                    Eq<List<ExecutableCandidateFacts>>(null, ExecutableCandidateProbe.CollectFacts(fixture.Root, out complete));
                    Eq(false, complete);
                    Eq<string>(null, ExecutableCandidateProbe.PickMainExecutable(fixture.Root));
                    string recommended;
                    List<ExecutableCandidateFacts> list = ExecutableCandidateProbe.ListCandidates(fixture.Root, 24, out recommended);
                    Eq(1, list.Count); Eq(game, list[0].Path);
                    Eq<string>(null, recommended);
                    Eq(1, ExecutableCandidateProbe.ListCandidates(fixture.Root, 24).Count);
                }
                string unlockedRecommendation;
                Eq(2, ExecutableCandidateProbe.ListCandidates(fixture.Root, 24, out unlockedRecommendation).Count);
                Eq(game, unlockedRecommendation);
            }
        }

        private static void AddGameFolderDeeplyNestedExecutableResolves()
        {
            using (var fixture = new GenericExecutableFixture())
            {
                string game = fixture.WriteExecutable(@"Client\Content\Releases\Current\Game\Binaries\Win64\Game.exe", "d3d12.dll");
                string recommended;
                List<ExecutableCandidateFacts> list = ExecutableCandidateProbe.ListCandidates(fixture.Root, 24, out recommended);
                Eq(1, list.Count); Eq(game, list[0].Path); Eq(game, recommended);
                Eq(game, ExecutableCandidateProbe.PickMainExecutable(fixture.Root));
                string resolved, error;
                Eq(true, GameExecutableResolver.TryResolve(fixture.Root, out resolved, out error));
                Eq(game, resolved);
            }
        }
    }
}
#endif
