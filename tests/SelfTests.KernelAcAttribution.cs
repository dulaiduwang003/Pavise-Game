// 文件用途 内核反作弊归因回归 纯判定 不读注册表以外的东西 不启动进程 不改设置
#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunKernelAcAttributionRegressionTests()
        {
            Action[] tests =
            {
                KernelAcMatchesGameByExecutable,
                KernelAcDoesNotNameABystander,
                KernelAcUnknownGameStaysUnnamed
            };
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            return tests.Length;
        }

        // 认得出游戏的才点名 EA 系是这次补的 战地 6 之前落到兜底 报成本机装的那个反作弊
        private static void KernelAcMatchesGameByExecutable()
        {
            Eq("EA Javelin", KernelAntiCheat.MatchByExe("bf6"));
            Eq("EA Javelin", KernelAntiCheat.MatchByExe("bf6.exe"));
            Eq("EA Javelin", KernelAntiCheat.MatchByExe(
                @"C:\Program Files (x86)\Steam\steamapps\common\Battlefield 6\bf6.exe"));
            Eq("EA Javelin", KernelAntiCheat.MatchByExe("bf2042.exe"));
            // cod.exe 是 Call of Duty HQ 的真实主程序名 按整名相等匹配
            Eq("Ricochet", KernelAntiCheat.MatchByExe("cod.exe"));
            Eq("Ricochet", KernelAntiCheat.MatchByExe("cod"));
            Eq("Ricochet", KernelAntiCheat.MatchByExe("ModernWarfare.exe"));
            // 三个字母做前缀会命中无关游戏 所以这一条只认整名
            Eq(null, KernelAntiCheat.MatchByExe("CodeVein.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("codex.exe"));
            // 前缀不能短到误配无关程序 点错名和拿旁观者当肇事者是同一类错
            Eq(null, KernelAntiCheat.MatchByExe("nfsclient.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("fc2launcher.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("maddenhelper.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("bfgminer.exe"));
            Eq("HoYoKProtect", KernelAntiCheat.MatchByExe("YuanShen.exe"));
        }

        // 认不出游戏时不能拿本机安装清单当肇事者 那份清单跟当前跑什么游戏无关
        //   日志点错名会让用户去关一个根本没参与的反作弊分组
        private static void KernelAcDoesNotNameABystander()
        {
            string log = KernelAntiCheat.DescribeForLog("SomeUnknownGame.exe");
            if (log == null) return; // 本机没装任何内核反作弊 不点名即正确
            string installed = KernelAntiCheat.InstalledName();
            Eq(true, installed != null);
            // 兜底文本必须说明这是本机安装清单 而不是直接把名字当肇事者交出去
            Eq(Lang.F("log.kernelac.installed", installed), log);
            Eq(false, log == installed);
        }

        // 认得出的游戏走点名分支 两个入口对同一个游戏必须一致
        private static void KernelAcUnknownGameStaysUnnamed()
        {
            Eq("EA Javelin", KernelAntiCheat.DescribeForLog("bf6.exe"));
            Eq(KernelAntiCheat.MatchByExe("bf6.exe"), KernelAntiCheat.DescribeForLog("bf6.exe"));
            Eq(null, KernelAntiCheat.MatchByExe(null));
            Eq(null, KernelAntiCheat.MatchByExe(""));
        }
    }
}
#endif
