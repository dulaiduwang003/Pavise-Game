#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // 文件夹入库 唯一命中直接解析 多个候选交给列表 空文件夹给错误
        internal static void RunAddGameFolderRegressionTests()
        {
            AddGameFolderUniqueMainResolves();
            AddGameFolderAmbiguousListsCandidates();
            AddGameFolderEmptyAndMissing();
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
                // 图形证据排在 GUI 子系统前面
                Eq(game, list[0].Path);
                Eq(tool, list[1].Path);
                Eq(2, ExecutableCandidateProbe.Rank(list[0]));
                Eq(1, ExecutableCandidateProbe.Rank(list[1]));
                Eq(1, ExecutableCandidateProbe.ListCandidates(fixture.Root, 1).Count);

                string resolved, error, suggested;
                Eq(true, GameExecutableResolver.TryResolve(fixture.Root, out resolved, out error, out suggested));
                Eq(game, resolved);
                Eq(Path.GetFileName(fixture.Root), suggested);
                // 带尾斜杠和引号的文件夹路径同样接受
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
    }
}
#endif
