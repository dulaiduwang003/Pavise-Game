// @author bdth 2074055628@qq.com
// 文件用途 安装范围解析的隔离回归 只用内存安装记录和自己的临时目录 不碰真实注册表
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestGameInstallScope()
        {
            RunGameInstallScopeRegressionTests();
        }

        internal static int RunGameInstallScopeRegressionTests()
        {
            Action[] tests =
            {
                InstallScopeExplicitLocation, InstallScopeSourceCorroboration,
                InstallScopeSourceAloneRejected, InstallScopeMismatchedSourceRejected,
                InstallScopeExplicitLocationWins, InstallScopeInvalidExplicitDoesNotFallback,
                InstallScopeNarrowestRoot, InstallScopePrefixBoundary,
                InstallScopeSiblingProduct, InstallScopeCommonAncestorRejected,
                InstallScopePlatformRootRejected, InstallScopeGameInsidePlatformAllowed,
                InstallScopeContainerNamesRejected, InstallScopeSystemRootsRejected,
                InstallScopeUnsafePathRejected, InstallScopeMissingRootRejected,
                InstallScopeReaderFailureRejected, InstallScopeInvalidPathsRejected,
                InstallScopeCanonicalPath, InstallScopeRecordDuplicates,
                InstallScopeQuotedUninstall, InstallScopeInvalidUninstall,
                InstallScopeNoMetadataFallback, InstallScopeBoundedRecords,
                InstallScopeReparseAncestors, InstallScopeAttributeFailure,
                InstallScopePathDepthBound,
                InstallScopeFilesystemSnapshot, InstallScopeSnapshotIsCopied,
                InstallScopeNestedSnapshots, InstallScopeSiblingRendererWithoutParents,
                InstallScopeFallbackPlatform, InstallScopeFallbackCommonRoots,
                InstallScopeFallbackSeparateInstall, InstallScopeFallbackNarrow,
                InstallScopeFallbackNestedGame, InstallScopeFallbackIncompleteSnapshot,
                InstallScopeFallbackUnverifiedRecord, InstallScopeFallbackContract
            };
            foreach (Action test in tests) test();
            return tests.Length;
        }

        private const string InstallScopeTitle = @"C:\PaviseInstallScopeTests\UnlistedTitle";
        private const string InstallScopeEntry = InstallScopeTitle + @"\RandomEntryFolder\Whatever.exe";
        private const string InstallScopeFallback = InstallScopeTitle + @"\RandomEntryFolder";

        private static GameInstallRecord InstallScopeRecord(string root)
        {
            return new GameInstallRecord { InstallLocation = root };
        }

        private static string InstallScopeSelect(params GameInstallRecord[] records)
        {
            return GameInstallScope.SelectRoot(InstallScopeEntry, InstallScopeFallback,
                records, new string[0], delegate { return true; }, delegate { return true; });
        }

        private static void InstallScopeExplicitLocation()
        {
            Eq(InstallScopeTitle, InstallScopeSelect(InstallScopeRecord(InstallScopeTitle)));
        }

        private static void InstallScopeSourceCorroboration()
        {
            Eq(InstallScopeTitle, InstallScopeSelect(new GameInstallRecord
            { InstallSource = InstallScopeTitle, UninstallString = InstallScopeTitle + @"\OldUninstaller.exe /quiet" }));
        }

        private static void InstallScopeSourceAloneRejected()
        {
            Eq(InstallScopeFallback, InstallScopeSelect(new GameInstallRecord { InstallSource = InstallScopeTitle }));
            Eq(InstallScopeFallback, InstallScopeSelect(new GameInstallRecord
            { UninstallString = InstallScopeTitle + @"\Uninstall.exe" }));
        }

        private static void InstallScopeMismatchedSourceRejected()
        {
            Eq(InstallScopeFallback, InstallScopeSelect(new GameInstallRecord
            { InstallSource = InstallScopeTitle, UninstallString = @"C:\SetupCache\Uninstall.exe" }));
        }

        private static void InstallScopeExplicitLocationWins()
        {
            Eq(InstallScopeTitle, InstallScopeSelect(new GameInstallRecord
            {
                InstallLocation = InstallScopeTitle,
                InstallSource = @"C:\SetupCache",
                UninstallString = @"C:\SetupCache\Uninstall.exe"
            }));
        }

        private static void InstallScopeInvalidExplicitDoesNotFallback()
        {
            Eq(InstallScopeFallback, InstallScopeSelect(new GameInstallRecord
            {
                InstallLocation = @"relative\bad",
                InstallSource = InstallScopeTitle,
                UninstallString = InstallScopeTitle + @"\Uninstall.exe"
            }));
        }

        private static void InstallScopeNarrowestRoot()
        {
            var broad = InstallScopeRecord(@"C:\PaviseInstallScopeTests");
            var narrow = InstallScopeRecord(InstallScopeTitle);
            Eq(InstallScopeTitle, InstallScopeSelect(broad, narrow));
            Eq(InstallScopeTitle, InstallScopeSelect(narrow, broad));
        }

        private static void InstallScopePrefixBoundary()
        {
            Eq(InstallScopeFallback, InstallScopeSelect(InstallScopeRecord(InstallScopeTitle + "-copy")));
            Eq(InstallScopeFallback, InstallScopeSelect(InstallScopeRecord(InstallScopeTitle.Substring(0, InstallScopeTitle.Length - 1))));
        }

        private static void InstallScopeSiblingProduct()
        {
            Eq(InstallScopeTitle, InstallScopeSelect(InstallScopeRecord(InstallScopeTitle),
                InstallScopeRecord(@"C:\PaviseInstallScopeTests\UnrelatedTitle")));
        }

        private static void InstallScopeCommonAncestorRejected()
        {
            Eq(InstallScopeFallback, InstallScopeSelect(InstallScopeRecord(@"C:\PaviseInstallScopeTests"),
                InstallScopeRecord(@"C:\PaviseInstallScopeTests\UnrelatedTitle")));
        }

        private static void InstallScopePlatformRootRejected()
        {
            Eq(InstallScopeFallback, GameInstallScope.SelectRoot(InstallScopeEntry, InstallScopeFallback,
                new[] { InstallScopeRecord(InstallScopeTitle) }, new[] { InstallScopeTitle },
                delegate { return true; }, delegate { return true; }));
            Eq(InstallScopeFallback, GameInstallScope.SelectRoot(InstallScopeEntry, InstallScopeFallback,
                new[] { InstallScopeRecord(@"C:\PaviseInstallScopeTests") },
                new[] { @"C:\PaviseInstallScopeTests\SharedRuntime" }, delegate { return true; }, delegate { return true; }));
        }

        private static void InstallScopeGameInsidePlatformAllowed()
        {
            Eq(InstallScopeTitle, GameInstallScope.SelectRoot(InstallScopeEntry, InstallScopeFallback,
                new[] { InstallScopeRecord(InstallScopeTitle) }, new[] { @"C:\PaviseInstallScopeTests" },
                delegate { return true; }, delegate { return true; }));
        }

        private static void InstallScopeContainerNamesRejected()
        {
            foreach (string name in new[] { "Games", "Game", "SteamLibrary", "steamapps", "common", "WeGameApps", "XboxGames", "WindowsApps" })
            {
                string container = @"C:\PaviseInstallScopeTests\" + name;
                string fallback = container + @"\SpecificTitle\Entry";
                Eq(fallback, GameInstallScope.SelectRoot(fallback + @"\Any.exe", fallback,
                    new[] { InstallScopeRecord(container) }, new string[0], delegate { return true; }, delegate { return true; }));
            }
        }

        private static void InstallScopeSystemRootsRejected()
        {
            var roots = new List<string> { @"C:\", @"C:\Users", @"C:\Downloads", Path.GetTempPath() };
            foreach (Environment.SpecialFolder folder in new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonDocuments,
                Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.CommonMusic, Environment.SpecialFolder.CommonPictures, Environment.SpecialFolder.CommonVideos
            })
            {
                string root = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(root)) roots.Add(root);
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows)) roots.Add(Path.Combine(windows, "ArbitrarySubfolder"));
            string commonProfile = Environment.GetEnvironmentVariable("PUBLIC");
            if (!string.IsNullOrEmpty(commonProfile)) roots.Add(commonProfile);
            foreach (string root in roots)
                Eq("unchanged", GameInstallScope.SelectRoot(Path.Combine(root, @"Chosen\Entry.exe"), "unchanged",
                    new[] { InstallScopeRecord(root) }, new string[0], delegate { return true; }, delegate { return true; }));
        }

        private static void InstallScopeUnsafePathRejected()
        {
            Eq(InstallScopeFallback, GameInstallScope.SelectRoot(InstallScopeEntry, InstallScopeFallback,
                new[] { InstallScopeRecord(InstallScopeTitle) }, new string[0], delegate { return true; },
                delegate(string path) { return !string.Equals(path, InstallScopeEntry, StringComparison.OrdinalIgnoreCase); }));
            Eq(InstallScopeFallback, GameInstallScope.SelectRoot(InstallScopeEntry, InstallScopeFallback,
                new[] { InstallScopeRecord(InstallScopeTitle) }, new string[0], delegate { return true; },
                delegate(string path) { return !string.Equals(path, InstallScopeTitle, StringComparison.OrdinalIgnoreCase); }));
        }

        private static void InstallScopeMissingRootRejected()
        {
            Eq(InstallScopeFallback, GameInstallScope.SelectRoot(InstallScopeEntry, InstallScopeFallback,
                new[] { InstallScopeRecord(InstallScopeTitle) }, new string[0], delegate { return false; }, delegate { return true; }));
        }

        private static void InstallScopeReaderFailureRejected()
        {
            Eq(InstallScopeFallback, GameInstallScope.SelectRoot(InstallScopeEntry, InstallScopeFallback,
                new[] { InstallScopeRecord(InstallScopeTitle) }, new string[0], delegate { throw new IOException("owned fixture"); }, delegate { return true; }));
            Eq(InstallScopeFallback, GameInstallScope.SelectRoot(InstallScopeEntry, InstallScopeFallback,
                new[] { InstallScopeRecord(InstallScopeTitle) }, new string[0], delegate { return true; }, delegate { throw new UnauthorizedAccessException(); }));
        }

        private static void InstallScopeInvalidPathsRejected()
        {
            foreach (string path in new[] { null, "", " ", @"relative\Entry.exe", @"C:Entry.exe", @"\\server\share\Entry.exe",
                @"\\?\C:\Title\Entry.exe", @"C:\Title\Entry.dll", @"C:\Title\Entry.exe:stream", "C:\\Title\n\\Entry.exe" })
                Eq(InstallScopeFallback, GameInstallScope.SelectRoot(path, InstallScopeFallback,
                    new[] { InstallScopeRecord(InstallScopeTitle) }, new string[0], delegate { return true; }, delegate { return true; }));
        }

        private static void InstallScopeCanonicalPath()
        {
            Eq(InstallScopeTitle, GameInstallScope.SelectRoot(
                "\"" + InstallScopeTitle.ToLowerInvariant() + @"\RandomEntryFolder\..\RandomEntryFolder\Whatever.EXE" + "\"",
                InstallScopeFallback, new[] { InstallScopeRecord(InstallScopeTitle + "\\") }, new string[0],
                delegate { return true; }, delegate { return true; }));
        }

        private static void InstallScopeRecordDuplicates()
        {
            Eq(InstallScopeTitle, InstallScopeSelect(null, InstallScopeRecord(InstallScopeTitle),
                InstallScopeRecord(InstallScopeTitle.ToUpperInvariant())));
        }

        private static void InstallScopeQuotedUninstall()
        {
            foreach (string command in new[]
            {
                "\"" + InstallScopeTitle + "\\Old Name.exe\" /S",
                InstallScopeTitle + "\\Old Name.exe /S",
                "\"" + InstallScopeTitle + "\\Old Name.EXE\""
            })
                Eq(InstallScopeTitle, InstallScopeSelect(new GameInstallRecord
                { InstallSource = InstallScopeTitle, UninstallString = command }));
        }

        private static void InstallScopeInvalidUninstall()
        {
            foreach (string command in new[] { null, "", "\"", "\"broken", "msiexec.exe /X {id}", "cmd.exe /c anything", @"relative\Uninstall.exe", InstallScopeTitle + @"\Uninstall.dll" })
                Eq(InstallScopeFallback, InstallScopeSelect(new GameInstallRecord
                { InstallSource = InstallScopeTitle, UninstallString = command }));
        }

        private static void InstallScopeNoMetadataFallback()
        {
            Eq(InstallScopeFallback, InstallScopeSelect());
            Eq<string>(null, GameInstallScope.SelectRoot(InstallScopeEntry, null,
                new GameInstallRecord[0], new string[0], delegate { return true; }, delegate { return true; }));
        }

        private static void InstallScopeBoundedRecords()
        {
            var records = new GameInstallRecord[8193];
            records[0] = InstallScopeRecord(InstallScopeTitle);
            Eq(InstallScopeFallback, GameInstallScope.SelectRoot(InstallScopeEntry, InstallScopeFallback,
                records, new string[0], delegate { return true; }, delegate { return true; }));
        }

        private static void InstallScopeReparseAncestors()
        {
            var visited = new List<string>();
            Eq(true, GameInstallScope.IsSafePath(InstallScopeEntry, delegate(string path)
            { visited.Add(path); return FileAttributes.Normal; }));
            Eq(true, visited.Contains(InstallScopeEntry));
            Eq(true, visited.Contains(InstallScopeTitle));
            Eq(true, visited.Contains(@"C:\PaviseInstallScopeTests"));
            Eq(true, visited.Contains(@"C:\"));
            foreach (string boundary in new[] { InstallScopeEntry, InstallScopeTitle, @"C:\PaviseInstallScopeTests" })
            {
                string reparse = boundary;
                Eq(false, GameInstallScope.IsSafePath(InstallScopeEntry, delegate(string path)
                { return string.Equals(path, reparse, StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.ReparsePoint : FileAttributes.Normal; }));
            }
        }

        private static void InstallScopeAttributeFailure()
        {
            Eq(false, GameInstallScope.IsSafePath(InstallScopeEntry,
                delegate { throw new UnauthorizedAccessException(); }));
            Eq(false, GameInstallScope.IsSafePath(InstallScopeEntry, null));
            Eq(false, GameInstallScope.IsSafePath(null, delegate { return FileAttributes.Normal; }));
        }

        private static void InstallScopePathDepthBound()
        {
            string path = @"C:\PaviseInstallScopeTests";
            for (int i = 0; i < 130; i++) path = Path.Combine(path, "x");
            int calls = 0;
            Eq(false, GameInstallScope.IsSafePath(path, delegate
            { calls++; return FileAttributes.Directory; }));
            // 老的 .NET Framework 宿主可能在显式深度预算用完之前
            // 就在 GetDirectoryName 这里把超过 260 字符的路径顶回来
            // 两种宿主都得失败关闭 而且不能超出那个预算
            Eq(true, calls >= 1 && calls <= 128);
        }

        private sealed class InstallScopeFixture : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "PaviseInstallScope-" + Guid.NewGuid().ToString("N"));
            internal readonly string Entry;
            internal readonly string Title;

            internal InstallScopeFixture()
            {
                Title = Path.Combine(Root, "PreviouslyUnknownTitle");
                Entry = Path.Combine(Title, @"UnfamiliarLoginFolder\Selected.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(Entry));
                File.WriteAllBytes(Entry, new byte[] { 0x4d, 0x5a });
                Directory.CreateDirectory(Path.Combine(Title, "DifferentRuntimeFolder"));
            }

            public void Dispose()
            {
                string root = Path.GetFullPath(Root).TrimEnd('\\');
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
                if (!string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("PaviseInstallScope-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing to remove a non-fixture install scope.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void InstallScopeFilesystemSnapshot()
        {
            using (var fixture = new InstallScopeFixture())
            using (GameInstallScope.UseSnapshotForTest(new[] { new GameInstallRecord
            { InstallSource = fixture.Title, UninstallString = "\"" + fixture.Title + "\\NoLongerPresent.exe\" /S" } }, new string[0]))
            {
                Eq(fixture.Title, GameInstallScope.Resolve(fixture.Entry, Path.GetDirectoryName(fixture.Entry)));
                Eq("missing", GameInstallScope.Resolve(Path.Combine(fixture.Title, "Absent.exe"), "missing"));
            }
        }

        private static void InstallScopeSnapshotIsCopied()
        {
            using (var fixture = new InstallScopeFixture())
            {
                var record = InstallScopeRecord(fixture.Title);
                string[] platforms = { Path.Combine(fixture.Root, "Platform") };
                using (GameInstallScope.UseSnapshotForTest(new[] { record }, platforms))
                {
                    record.InstallLocation = fixture.Root;
                    platforms[0] = fixture.Title;
                    Eq(fixture.Title, GameInstallScope.Resolve(fixture.Entry, "fallback"));
                }
            }
        }

        private static void InstallScopeNestedSnapshots()
        {
            using (var fixture = new InstallScopeFixture())
            using (GameInstallScope.UseSnapshotForTest(new[] { InstallScopeRecord(fixture.Title) }, new string[0]))
            {
                Eq(fixture.Title, GameInstallScope.Resolve(fixture.Entry, "fallback"));
                using (GameInstallScope.UseSnapshotForTest(null, null))
                    Eq("fallback", GameInstallScope.Resolve(fixture.Entry, "fallback"));
                Eq(fixture.Title, GameInstallScope.Resolve(fixture.Entry, "fallback"));
            }
        }

        private static void InstallScopeSiblingRendererWithoutParents()
        {
            string root = InstallScopeSelect(InstallScopeRecord(InstallScopeTitle));
            var profile = new GameProfile { Id = "scope-test", Name = "Never catalogued", Root = root, ExecutablePath = InstallScopeEntry };
            var renderer = new GameProcessSnapshot
            {
                Pid = 120, Creation = 1000, ParentPid = 70,
                Path = InstallScopeTitle + @"\DifferentRuntimeFolder\StillUnknown.exe", Name = "StillUnknown",
                Foreground = true, Visible = true, FullscreenLike = true
            };
            // 原始登录器和中间那些父进程全退了 只剩另一个目录里的前台游戏
            GameDetection hit = GameSessionDetector.DetectSnapshot(new[] { renderer }, new[] { profile });
            if (hit == null) throw new Exception("Trusted install scope did not admit its sibling renderer.");
            Eq(renderer.Path, hit.RendererPath);
            renderer.Path = @"C:\PaviseInstallScopeTests\UnrelatedTitle\StillUnknown.exe";
            Eq<GameDetection>(null, GameSessionDetector.DetectSnapshot(new[] { renderer }, new[] { profile }));
        }

        private static void InstallScopeFallbackPlatform()
        {
            using (GameInstallScope.UseSnapshotForTest(new GameInstallRecord[0], new[] { InstallScopeTitle }))
            {
                Eq<string>(null, GameInstallScope.RestrictFallback(InstallScopeEntry, InstallScopeTitle));
                Eq<string>(null, GameInstallScope.RestrictFallback(InstallScopeEntry, @"C:\PaviseInstallScopeTests"));
            }
        }

        private static void InstallScopeFallbackCommonRoots()
        {
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                foreach (string name in new[] { "Games", "Game", "SteamLibrary", "steamapps", "common", "WeGameApps", "XboxGames" })
                {
                    string root = @"C:\PaviseInstallScopeTests\" + name;
                    Eq<string>(null, GameInstallScope.RestrictFallback(root + @"\Chosen\Entry.exe", root));
                }
                string temporary = Path.GetTempPath();
                Eq<string>(null, GameInstallScope.RestrictFallback(Path.Combine(temporary, "Entry.exe"), temporary));
                Eq<string>(null, GameInstallScope.RestrictFallback(InstallScopeEntry, @"C:\"));
            }
        }

        private static void InstallScopeFallbackSeparateInstall()
        {
            using (GameInstallScope.UseSnapshotForTest(new[]
            { InstallScopeRecord(@"C:\PaviseInstallScopeTests\UnrelatedTitle") }, null))
            {
                Eq<string>(null, GameInstallScope.RestrictFallback(InstallScopeEntry, @"C:\PaviseInstallScopeTests"));
                Eq(InstallScopeTitle, GameInstallScope.RestrictFallback(InstallScopeEntry, InstallScopeTitle));
            }
        }

        private static void InstallScopeFallbackNarrow()
        {
            using (GameInstallScope.UseSnapshotForTest(new[] { InstallScopeRecord(InstallScopeTitle) }, null))
                Eq(InstallScopeFallback, GameInstallScope.RestrictFallback(InstallScopeEntry, InstallScopeFallback));
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                Eq(InstallScopeFallback, GameInstallScope.RestrictFallback(InstallScopeEntry, InstallScopeFallback));
                Eq<string>(null, GameInstallScope.RestrictFallback(InstallScopeEntry, InstallScopeTitle + "-different"));
                Eq<string>(null, GameInstallScope.RestrictFallback(InstallScopeEntry, null));
                Eq<string>(null, GameInstallScope.RestrictFallback(null, InstallScopeTitle));
            }
        }

        private static void InstallScopeFallbackNestedGame()
        {
            using (GameInstallScope.UseSnapshotForTest(new[] { InstallScopeRecord(InstallScopeTitle) },
                new[] { @"C:\PaviseInstallScopeTests" }))
                Eq(InstallScopeTitle, GameInstallScope.RestrictFallback(InstallScopeEntry, InstallScopeTitle));
        }

        private static void InstallScopeFallbackIncompleteSnapshot()
        {
            var records = new GameInstallRecord[8193];
            records[0] = InstallScopeRecord(@"C:\PaviseInstallScopeTests\UnrelatedTitle");
            records[1] = InstallScopeRecord(InstallScopeTitle);
            using (GameInstallScope.UseSnapshotForTest(records, null))
            {
                // 快照不完整就不能往正向扩 但已经明确的负向边界还得守
                Eq(InstallScopeFallback, GameInstallScope.Resolve(InstallScopeEntry, InstallScopeFallback));
                Eq<string>(null, GameInstallScope.RestrictFallback(InstallScopeEntry, @"C:\PaviseInstallScopeTests"));
                Eq(InstallScopeFallback, GameInstallScope.RestrictFallback(InstallScopeEntry, InstallScopeFallback));
            }
        }

        private static void InstallScopeFallbackUnverifiedRecord()
        {
            using (GameInstallScope.UseSnapshotForTest(new[] { new GameInstallRecord
            { InstallSource = @"C:\PaviseInstallScopeTests\PossibleInstallerCache" } }, null))
                Eq(@"C:\PaviseInstallScopeTests", GameInstallScope.RestrictFallback(InstallScopeEntry, @"C:\PaviseInstallScopeTests"));
        }

        private static void InstallScopeFallbackContract()
        {
            Eq(InstallScopeTitle, GameInstallScope.SelectRoot(InstallScopeEntry, InstallScopeTitle,
                new[] { InstallScopeRecord(InstallScopeTitle) }, new[] { InstallScopeTitle },
                delegate { return true; }, delegate { return true; }));
            using (GameInstallScope.UseSnapshotForTest(new[] { InstallScopeRecord(InstallScopeTitle) }, new[] { InstallScopeTitle }))
                Eq<string>(null, GameInstallScope.RestrictFallback(InstallScopeEntry, InstallScopeTitle));
        }
    }
}
#endif
