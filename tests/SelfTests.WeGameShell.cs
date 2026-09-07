#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // WeGame 脱壳 目录识别 时机规则 熔断 复合扩展槽的归属 以及清理名单在通用根下的边界
        internal static void RunWeGameShellRegressionTests()
        {
            WeGameShellRootDetection();
            WeGameShellRootWalk();
            WeGameShellTimingRules();
            WeGameShellCleanupTargetsOnGenericRoot();
            WeGameShellCompositeOwnership();
            WeGameShellProfileApplicability();
        }

        private sealed class WeGameFixture : IDisposable
        {
            internal readonly string Root;
            internal WeGameFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "PaviseWeGameScope-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }
            internal string Dir(string relative)
            {
                string p = Path.Combine(Root, relative);
                Directory.CreateDirectory(p);
                return p;
            }
            internal string File(string relative)
            {
                string p = Path.Combine(Root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                System.IO.File.WriteAllBytes(p, new byte[] { 0x4D, 0x5A });
                return p;
            }
            public void Dispose()
            {
                string root = Path.GetFullPath(Root);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                if (!root.StartsWith(temp + "PaviseWeGameScope-", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("refusing to remove a non-fixture directory");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void WeGameShellRootDetection()
        {
            using (var f = new WeGameFixture())
            {
                string tcls = f.Dir("CF"); f.Dir(@"CF\TCLS");
                string rail = f.Dir("Delta"); f.Dir(@"Delta\rail_files");
                string launcher = f.Dir("NZ"); f.Dir(@"NZ\WeGameLauncher");
                string plain = f.Dir("Other"); f.Dir(@"Other\Bin");
                string apps = f.Dir(@"WeGameApps\SomeGame");
                Eq(true, WeGameShell.IsWeGameGameRoot(tcls));
                Eq(true, WeGameShell.IsWeGameGameRoot(rail));
                Eq(true, WeGameShell.IsWeGameGameRoot(launcher));
                Eq(true, WeGameShell.IsWeGameGameRoot(apps));
                Eq(false, WeGameShell.IsWeGameGameRoot(plain));
                Eq(false, WeGameShell.IsWeGameGameRoot(Path.Combine(f.Root, "missing")));
                Eq(false, WeGameShell.IsWeGameGameRoot(null));
                Eq(false, WeGameShell.IsWeGameGameRoot(""));
                // WeGameApps 本身是仓库 不是游戏根 游戏内部更深的目录也不是
                Eq(false, WeGameShell.UnderAppsFolder(Path.Combine(f.Root, "WeGameApps")));
                Eq(true, WeGameShell.UnderAppsFolder(apps));
                Eq(false, WeGameShell.IsWeGameGameRoot(Path.Combine(f.Root, "WeGameApps")));
                Eq(false, WeGameShell.IsWeGameGameRoot(f.Dir(@"WeGameApps\SomeGame\Bin")));
            }
        }

        private static void WeGameShellRootWalk()
        {
            using (var f = new WeGameFixture())
            {
                string root = f.Dir("CF"); f.Dir(@"CF\TCLS");
                string exe = f.File(@"CF\Bin\Win64\crossfire.exe");
                // 档案 Root 可信时直接用 否则从 EXE 往上找标记
                Eq(root, WeGameShell.ResolveGameRoot(root, exe));
                Eq(root, WeGameShell.ResolveGameRoot(null, exe));
                Eq(root, WeGameShell.ResolveGameRoot(Path.Combine(f.Root, "nope"), exe));
                // WeGameApps 下没有标记的游戏 以 WeGameApps 的直接子目录为根
                string game = f.Dir(@"WeGameApps\Game");
                string deep = f.File(@"WeGameApps\Game\Bin\x64\game.exe");
                Eq(game, WeGameShell.ResolveGameRoot(null, deep));
                // 超过四层就不再往上爬
                string far = f.File(@"CF\a\b\c\d\e\deep.exe");
                Eq<string>(null, WeGameShell.ResolveGameRoot(null, far));
                // 普通目录里的游戏不是 WeGame 游戏
                string other = f.File(@"Other\game.exe");
                Eq<string>(null, WeGameShell.ResolveGameRoot(null, other));
                Eq<string>(null, WeGameShell.ResolveGameRoot(null, null));
            }
        }

        private static void WeGameShellTimingRules()
        {
            long s = TimeSpan.TicksPerSecond;
            long start = 100 * s;
            Eq(false, WeGameShell.ShouldClean(start, start + 10 * s, false, false, false, true));
            Eq(false, WeGameShell.ShouldClean(start, start + (WeGameShell.StabilizeSeconds - 1) * s, false, false, false, true));
            Eq(true, WeGameShell.ShouldClean(start, start + WeGameShell.StabilizeSeconds * s, false, false, false, true));
            // 已脱过 熔断 停手 开关关 任一为真都不再动手
            Eq(false, WeGameShell.ShouldClean(start, start + 60 * s, true, false, false, true));
            Eq(false, WeGameShell.ShouldClean(start, start + 60 * s, false, true, false, true));
            Eq(false, WeGameShell.ShouldClean(start, start + 60 * s, false, false, true, true));
            Eq(false, WeGameShell.ShouldClean(start, start + 60 * s, false, false, false, false));
            Eq(false, WeGameShell.ShouldClean(0, start + 60 * s, false, false, false, true));

            long cleaned = 500 * s;
            Eq(true, WeGameShell.ExitBlamesCleanup(cleaned, cleaned + 5 * s, false));
            Eq(true, WeGameShell.ExitBlamesCleanup(cleaned, cleaned + WeGameShell.ExitFuseSeconds * s, false));
            Eq(false, WeGameShell.ExitBlamesCleanup(cleaned, cleaned + (WeGameShell.ExitFuseSeconds + 1) * s, false));
            // 结束通知带宽限 窗口更长
            Eq(true, WeGameShell.ExitBlamesCleanup(cleaned, cleaned + WeGameShell.ExitFuseNotifySeconds * s, true));
            Eq(false, WeGameShell.ExitBlamesCleanup(cleaned, cleaned + (WeGameShell.ExitFuseNotifySeconds + 1) * s, true));
            Eq(false, WeGameShell.ExitBlamesCleanup(0, cleaned, false));
            Eq(false, WeGameShell.ExitBlamesCleanup(cleaned, cleaned - s, false));

            var cycles = new Queue<long>();
            long t = 1000 * s;
            Eq(false, WeGameShell.RegisterKillCycle(cycles, t));
            Eq(false, WeGameShell.RegisterKillCycle(cycles, t + 60 * s));
            Eq(true, WeGameShell.RegisterKillCycle(cycles, t + 120 * s));
            // 窗口外的旧记录滚出去后重新计数
            var again = new Queue<long>();
            WeGameShell.RegisterKillCycle(again, t);
            WeGameShell.RegisterKillCycle(again, t + 60 * s);
            Eq(false, WeGameShell.RegisterKillCycle(again, t + (WeGameShell.RespawnWindowSeconds + 61) * s));
            Eq(false, WeGameShell.RegisterKillCycle(null, t));

            Eq(WeGameShell.ExitFuseSeconds * 1000, WeGameShell.NextCheckDelayMs(true));
            Eq(WeGameShell.RespawnCheckSeconds * 1000, WeGameShell.NextCheckDelayMs(false));
        }

        // 通用根下 只有 WeGame 目录的壳 游戏目录的 Cross 与 TCLS 会话进程会被结束
        private static void WeGameShellCleanupTargetsOnGenericRoot()
        {
            using (var f = new WeGameFixture())
            {
                string game = f.Dir("CF");
                string weGame = f.Dir("WeGame");
                f.File(@"WeGame\wegame.exe");
                Func<string, bool> target = delegate(string relative)
                {
                    string path = Path.Combine(f.Root, relative);
                    return LolRuntimeProcesses.IsCleanupTarget(path, Path.GetFileName(path), game, weGame, false);
                };
                Eq(true, target(@"WeGame\wegame.exe"));
                Eq(true, target(@"WeGame\wegame_env.exe"));
                Eq(true, target(@"WeGame\rail.exe"));
                Eq(true, target(@"CF\Cross\crossproxy.exe"));
                Eq(true, target(@"CF\TCLS\rail.exe"));
                Eq(true, target(@"CF\TCLS\tcls_core.exe"));
                // 游戏本体 TCLS 启动器 反作弊 都不在名单里
                Eq(false, target(@"CF\crossfire.exe"));
                Eq(false, target(@"CF\Bin\Win64\crossfire.exe"));
                Eq(false, target(@"CF\TCLS\Client.exe"));
                Eq(false, target(@"CF\TenProtect\TenSafe_1.exe"));
                Eq(false, target(@"Elsewhere\wegame.exe"));
                // 下载器只有手动净化才收
                Eq(false, target(@"WeGame\teniodl.exe"));
                Eq(true, LolRuntimeProcesses.IsCleanupTarget(Path.Combine(f.Root, @"WeGame\teniodl.exe"), "teniodl.exe", game, weGame, true));
            }
        }

        private sealed class FakeExtension : GameExtensionModule
        {
            internal readonly string Prefix;
            internal int Sessions;
            internal GameProfile LastProfile;
            internal FakeExtension(string prefix) { Prefix = prefix; }
            public override bool AppliesTo(GameProfile profile)
            {
                return profile != null && profile.Name != null
                    && (Prefix == null || profile.Name.StartsWith(Prefix, StringComparison.Ordinal));
            }
            public override void NotifySession(GameProfile profile, int pid, long creation, bool active)
            {
                Sessions++;
                LastProfile = profile;
            }
        }

        private static void WeGameShellCompositeOwnership()
        {
            var a = new FakeExtension("A");
            var all = new FakeExtension(null);
            var composite = new CompositeGameExtension(a, null, all);
            Eq(2, composite.Modules.Length);
            var pa = new GameProfile { Id = "1", Name = "Alpha" };
            var pb = new GameProfile { Id = "2", Name = "Beta" };
            // 谁先认领归谁 前面的模块优先
            Eq(true, ReferenceEquals(a, composite.Owner(pa)));
            Eq(true, ReferenceEquals(all, composite.Owner(pb)));
            Eq(true, composite.AppliesTo(pa) && composite.AppliesTo(pb));
            Eq<GameExtensionModule>(null, composite.Owner(null));
            Eq(false, composite.AppliesTo(null));
            // 会话通知广播给全部模块
            composite.NotifySession(pb, 7, 9, true);
            Eq(1, a.Sessions); Eq(1, all.Sessions);
            Eq(true, ReferenceEquals(pb, a.LastProfile));
            composite.NotifySession(null, 0, 0, false);
            Eq(2, a.Sessions); Eq(2, all.Sessions);
        }

        private static void WeGameShellProfileApplicability()
        {
            using (var f = new WeGameFixture())
            {
                string root = f.Dir("CF"); f.Dir(@"CF\TCLS");
                string exe = f.File(@"CF\crossfire.exe");
                string lolRoot = f.Dir(@"WeGameApps\英雄联盟"); f.Dir(@"WeGameApps\英雄联盟\TCLS");
                string lolExe = f.File(@"WeGameApps\英雄联盟\LeagueClient\LeagueClient.exe");
                string got;
                Eq(true, WeGameGameExtension.AppliesToProfile(new GameProfile { Id = "cf", Name = "CF", ExecutablePath = exe, Root = root }, out got));
                Eq(root, got);
                // 英雄联盟归它自己的模块 通用模块不认
                Eq(false, WeGameGameExtension.AppliesToProfile(new GameProfile { Id = "lol", Name = "League of Legends", ExecutablePath = lolExe, Root = lolRoot }, out got));
                Eq(false, WeGameGameExtension.AppliesToProfile(new GameProfile { Id = "lol2", Name = "英雄联盟", ExecutablePath = lolExe }, out got));
                // 学习到的路径也能定位
                Eq(true, WeGameGameExtension.AppliesToProfile(new GameProfile { Id = "cf2", Name = "CF", LearnedExecutablePath = exe }, out got));
                Eq(false, WeGameGameExtension.AppliesToProfile(null, out got));
                Eq(false, WeGameGameExtension.AppliesToProfile(new GameProfile { Id = "x", Name = "Plain", ExecutablePath = f.File(@"Other\game.exe") }, out got));
            }
        }
    }
}
#endif
