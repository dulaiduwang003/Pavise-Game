using System;

namespace PaviseApp
{
    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new ReleaseNote[1]
        {
            new ReleaseNote("3.3.1.1", "2026-11-15", new string[][] {
                new[] { "修复反作弊逐线程核验使对局中 Pavise 自身 CPU 占用偏高，线程枚举改为一次系统查询。",
                    "Fix anti-cheat per-thread checks raising Pavise's own CPU use during matches; thread enumeration now uses a single system query." },
                new[] { "微软后台治理的目标自行退出时，不再记为失败或告警，也不占用重试次数。",
                    "A Microsoft background target that exits on its own is no longer logged as a failure or warning and uses no retry attempt." },
                new[] { "修正日志措辞：线程分配保护拒绝不再显示「原因未知」；反作弊新线程纳入不再记为漂移；核心范围未启用时注明状态；未开启提优的对局不再记为「游戏优先级已生效」。",
                    "Clarify logs: thread-placement refusals no longer read \"Unknown reason\"; newly admitted anti-cheat threads are no longer logged as drift; an unused game CPU range states that it is off; sessions without any boost no longer read \"Game priorities in effect\"." },
                new[] { "相同的渲染进程探测结论不再逐条重复；画面提交探测丢失事件时记录诊断读数。",
                    "Identical renderer-probe results are no longer repeated line by line; present-probe event loss now logs diagnostic readings." },
                new[] { "主导航「性能」更名为「调优」。",
                    "The main navigation item Performance is renamed Tuning." },
                new[] { "「对局期间限制自动维护触发」改为随后台治理自动执行，治理关闭时不执行；「多媒体调度」改为保护开启期间自动执行。两项不再单独提供开关，写入与恢复方式不变。",
                    "Limit maintenance triggers during matches now runs automatically with background governance and stays off when governance is off; Multimedia scheduling runs automatically while protection is on. Neither has its own switch any more; how they apply and restore is unchanged." },
                new[] { "下架「桌面合成调度」：实测它会让桌面合成线程每个周期有一段降到低于游戏线程的优先级，CPU 满载时可能延误画面合成，收益未经证实。旧开关自动清除，不留系统残留。",
                    "Remove Desktop compositor scheduling: measurements showed it dropped the compositor threads below game threads for part of every period, which can delay composition under full CPU load, and no benefit was established. The old switch is cleared automatically and nothing is left in the system." },
                new[] { "移除「反作弊」页：反作弊压制改为在游戏库深度调优中逐游戏开启并选择强度，仅支持含 ACE 的反作弊，只在该游戏对局期间生效；EAC、BattlEye 改为只保护不压制；保护范围与进程访问受限历史移到白名单页。之前全局开启的 ACE 设置作为未单独设置游戏的默认值保留。",
                    "Remove the Anti-cheat page: anti-cheat suppression is now enabled per game, with its strength, in Library advanced tuning, supports only games with ACE anti-cheat, and applies only during that game's matches; EAC and BattlEye are protected and never suppressed; the protection scope and process access restriction history moved to the Whitelist page. A previously enabled global ACE setting remains the default for games without their own setting." },
                new[] { "微软后台治理改为一次性操作：入局施加并读回一次，不再逐轮核验，也不再为每个目标启动独立守护进程；退局由主程序恢复。Pavise 异常退出时限制会保留到相应服务重启，下次启动会提示。",
                    "Microsoft background governance is now a one-time operation: limits are applied and read back once at match start, no longer re-verified every cycle, and no longer use a separate guardian process per target; Pavise restores them after the match. After an abnormal exit the limits remain until the affected service restarts, and the next start logs a notice." },
            }),

        };

        public static ReleaseNote Current
        {
            get
            {
                ReleaseNote[] all = All;
                foreach (ReleaseNote releaseNote in all)
                {
                    if (string.Equals(releaseNote.Version, App.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        return releaseNote;
                    }
                }
                return null;
            }
        }

        public static bool HasUnseen
        {
            get
            {
                return !string.Equals(Settings.LoadStr(SeenKey, ""), App.Version, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static bool MarkSeen()
        {
            string saved;
            return Settings.SaveStrDurable(SeenKey, App.Version)
                && Settings.TryLoadStr(SeenKey, out saved)
                && string.Equals(saved, App.Version, StringComparison.OrdinalIgnoreCase);
        }
    }
}
