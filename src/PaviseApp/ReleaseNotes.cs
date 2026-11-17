using System;

namespace PaviseApp
{
    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new ReleaseNote[1]
        {
            new ReleaseNote("3.3.1.2", "2026-11-16", new string[][] {
                new[] { "渲染线程调度默认开启，未选方案的游戏使用推荐的“游戏优先”；手动关过的游戏保持关闭。",
                    "Render thread scheduling is on by default; games without a chosen strategy use the recommended Game priority. Games you turned off stay off." },
                new[] { "调度开启时对局中锁定高性能 QoS；除专核强化外，各方案还会把游戏进程设为高优先级，退局恢复。",
                    "With scheduling on, the game keeps high-performance QoS during matches, and every strategy except Enhanced dedicated rendering also runs it at High; restored at game end." },
                new[] { "取消独立反作弊引擎：ACE 随普通后台治理，可在深度调优中逐游戏关闭；其他反作弊只保护。",
                    "The separate anti-cheat engine is gone: ACE is governed as ordinary background and can be turned off per game in advanced tuning; other anti-cheat software is only protected." },
                new[] { "微软后台和 WeGame 主进程并入普通后台治理；“激进”档会把后台收到少数核心上。",
                    "Microsoft background services and the WeGame main process are now ordinary background; Aggressive governance also confines background apps to a few cores." },
                new[] { "白名单新增内置语音软件（KOOK、YY、TeamSpeak、Mumble、Discord），并支持服务白名单。",
                    "Built-in whitelist for voice chat apps (KOOK, YY, TeamSpeak, Mumble, Discord), plus a service whitelist." },
                new[] { "恢复 AI 调优提示词（设置 → 常规）；多媒体调度恢复为独立开关，默认开启。",
                    "The AI tuning prompt is back (Settings → General); Multimedia scheduling is a separate switch again, on by default." },
                new[] { "不再恢复 1.x、2.x 版本及旧版反作弊、微软后台治理留下的改动，现值保持不变。",
                    "Changes left by 1.x/2.x builds and by the old anti-cheat and Microsoft background governance are no longer restored and keep their current values." },
                new[] { "降低运行开销，改进界面缩放。",
                    "Lower runtime overhead and better UI scaling." },
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
