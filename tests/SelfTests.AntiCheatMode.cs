// 文件用途 反作弊三档强度与分类回归 纯判定 不写进程 不改真实设置
#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunAntiCheatModeRegressionTests()
        {
            Action[] tests =
            {
                AcModeRoundTripsAndFallsBack,
                AcModeMapsToExistingLevels,
                AcModeKeepsEffectiveIngredientsInEveryTier,
                AcModePinsCoresOnlyAtTheTop,
                AcModeNeverStarvesAnAntiCheat,
                AcModeLoweringReleasesExistingPins,
                AcCategorySplitsVanguardOut,
                AcCategoryKeepsProtectOnlyGroupsExempt
            };
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            return tests.Length;
        }

        private static void AcModeRoundTripsAndFallsBack()
        {
            foreach (AntiCheatMode mode in new[] { AntiCheatMode.Gentle, AntiCheatMode.Balanced, AntiCheatMode.Isolated })
                Eq(mode, AntiCheatModes.Parse(AntiCheatModes.Token(mode)));
            // 读不出或读到不认识的值都退回默认 不猜用户想要哪一档
            Eq(AntiCheatModes.Default, AntiCheatModes.Parse(null));
            Eq(AntiCheatModes.Default, AntiCheatModes.Parse(""));
            Eq(AntiCheatModes.Default, AntiCheatModes.Parse("whatever"));
            // 默认必须是隔离 它等于本功能一直以来的行为 升级不改变已生效的设置
            Eq(AntiCheatMode.Isolated, AntiCheatModes.Default);
        }

        private static void AcModeMapsToExistingLevels()
        {
            Eq(SuppressionLevel.Eco, AntiCheatModes.LevelOf(AntiCheatMode.Gentle));
            Eq(SuppressionLevel.Restrained, AntiCheatModes.LevelOf(AntiCheatMode.Balanced));
            Eq(SuppressionLevel.Isolated, AntiCheatModes.LevelOf(AntiCheatMode.Isolated));
        }

        // 有效成分是 EcoQoS 小核限频与磁盘 IO 降级 三档一档都不能少
        //   递进的只是介入深度 温和不动调度优先级 均衡降到低于正常
        private static void AcModeKeepsEffectiveIngredientsInEveryTier()
        {
            const uint original = Native.NORMAL_PRIORITY_CLASS;
            foreach (AntiCheatMode mode in new[] { AntiCheatMode.Gentle, AntiCheatMode.Balanced, AntiCheatMode.Isolated })
            {
                SuppressionLevel level = AntiCheatModes.LevelOf(mode);
                // 磁盘 IO 每一档都降 扫盘才是主要伤害
                Eq(true, SuppressionCore.DesiredIoPriority(level) < 2);
            }
            // 温和不动调度优先级 均衡与隔离降到低于正常
            Eq(original, SuppressionCore.DesiredPriority(
                AntiCheatModes.LevelOf(AntiCheatMode.Gentle), original, true));
            Eq(Native.BELOW_NORMAL_PRIORITY_CLASS, SuppressionCore.DesiredPriority(
                AntiCheatModes.LevelOf(AntiCheatMode.Balanced), original, true));
            Eq(Native.BELOW_NORMAL_PRIORITY_CLASS, SuppressionCore.DesiredPriority(
                AntiCheatModes.LevelOf(AntiCheatMode.Isolated), original, true));
            // 只有隔离把磁盘 IO 压到极低
            Eq(1, SuppressionCore.DesiredIoPriority(AntiCheatModes.LevelOf(AntiCheatMode.Gentle)));
            Eq(1, SuppressionCore.DesiredIoPriority(AntiCheatModes.LevelOf(AntiCheatMode.Balanced)));
            Eq(0, SuppressionCore.DesiredIoPriority(AntiCheatModes.LevelOf(AntiCheatMode.Isolated)));
        }

        private static void AcModePinsCoresOnlyAtTheTop()
        {
            Eq(false, AntiCheatModes.PinsCores(AntiCheatMode.Gentle));
            Eq(false, AntiCheatModes.PinsCores(AntiCheatMode.Balanced));
            Eq(true, AntiCheatModes.PinsCores(AntiCheatMode.Isolated));
        }

        // 扫描型反作弊挂起游戏线程时自己分不到时间片 挂起窗口会从几百毫秒拖到几秒
        //   所以任何一档都不能给反作弊喂上饿死那三样 见 SuppressionCore.Apply 顶部注释
        private static void AcModeNeverStarvesAnAntiCheat()
        {
            foreach (AntiCheatMode mode in new[] { AntiCheatMode.Gentle, AntiCheatMode.Balanced, AntiCheatMode.Isolated })
            {
                SuppressionLevel level = AntiCheatModes.LevelOf(mode);
                Eq(false, SuppressionCore.DesiredPriority(level, Native.NORMAL_PRIORITY_CLASS, true)
                    == Native.IDLE_PRIORITY_CLASS);
                Eq(3, SuppressionCore.DesiredPagePriority(level, true));
                Eq(false, SuppressionCore.DesiredTimerSeal(level, true));
            }
            // 普通后台不受这条保护 隔离档照旧喂满 这三项的差别就是 antiCheat 标志
            Eq(Native.IDLE_PRIORITY_CLASS, SuppressionCore.DesiredPriority(
                SuppressionLevel.Isolated, Native.NORMAL_PRIORITY_CLASS, false));
            Eq(1, SuppressionCore.DesiredPagePriority(SuppressionLevel.Isolated, false));
            Eq(true, SuppressionCore.DesiredTimerSeal(SuppressionLevel.Isolated, false));
        }

        // 降档只是不再新增绑核 已经绑上的必须主动放回去
        //   SqueezeAff 不清零的话 DesiredAffinity 会一直返回落点 每轮对账又写回来
        //   反作弊会被钉在末尾核上直到进程退出 而界面上那一档写着不绑核
        //   这里测的是决策本身 Tamer 拿它决定要不要调 ClearSqueezes
        //   真实落点要先 Acquire 才有 那会改到本进程 隔离测试不做
        private static void AcModeLoweringReleasesExistingPins()
        {
            // 从绑核档降到不绑核档 必须释放
            Eq(true, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Isolated, AntiCheatMode.Gentle));
            Eq(true, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Isolated, AntiCheatMode.Balanced));
            // 本来就不绑核的档之间来回切 没有落点可释放
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Gentle, AntiCheatMode.Balanced));
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Balanced, AntiCheatMode.Gentle));
            // 升档只是允许重新绑 不需要释放 也不该自己补绑
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Gentle, AntiCheatMode.Isolated));
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Balanced, AntiCheatMode.Isolated));
            // 同档不动
            foreach (AntiCheatMode mode in new[] { AntiCheatMode.Gentle, AntiCheatMode.Balanced, AntiCheatMode.Isolated })
                Eq(false, AntiCheatModes.ShouldReleasePins(mode, mode));
        }

        // Vanguard 会阻止访问低层系统功能的第三方程序加载文件 压它的失败模式是游戏起不来
        //   判据不是有没有内核驱动 BattlEye 和 EAC 都有 .sys 但它们的用户态服务压起来安全
        private static void AcCategorySplitsVanguardOut()
        {
            AcGroup vanguard = null, battleye = null, eac = null, ace = null;
            foreach (AcGroup g in AntiCheatCatalog.Groups)
            {
                if (g.Key == "vanguard") vanguard = g;
                if (g.Key == "battleye") battleye = g;
                if (g.Key == "eac") eac = g;
                if (g.Key == "ace") ace = g;
            }
            Eq(true, vanguard != null && battleye != null && eac != null && ace != null);
            Eq(AcCategory.ProtectOnly, vanguard.Category);
            Eq(false, vanguard.Suppressible);
            // 有内核驱动但用户态服务可压的仍然可压 这是实测结论不是推断
            Eq(true, battleye.Suppressible);
            Eq(true, eac.Suppressible);
            Eq(true, ace.Suppressible);
        }

        // 改判仅保护只是不进压制目标 进程名照旧进豁免名单 后台压制仍要跳过它
        private static void AcCategoryKeepsProtectOnlyGroupsExempt()
        {
            foreach (AcGroup g in AntiCheatCatalog.Groups)
            {
                if (g.Suppressible) continue;
                foreach (string name in g.Procs)
                {
                    Eq(true, AntiCheatCatalog.IsKnownProcess(name));
                    Eq(true, AntiCheatCatalog.IsAntiCheatLikeName(name + ".exe"));
                }
            }
        }
    }
}
#endif
