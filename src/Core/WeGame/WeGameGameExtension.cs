// @author bdth 2074055628@qq.com
// 文件用途 通用 WeGame 脱壳接进扩展模块槽 经 WeGame 启动的非英雄联盟游戏在卡片下方挂脱壳区
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class WeGameGameExtension : GameExtensionModule
    {
        private readonly object gate = new object();
        private WeGameShellService service;

        public WeGameGameExtension()
        {
            Lang.Merge(WeGameLang.Table);
        }

        private WeGameShellService Service
        {
            get
            {
                lock (gate)
                {
                    if (service == null) service = new WeGameShellService();
                    return service;
                }
            }
        }

        // 英雄联盟有自己的模块 这里只认其余带 WeGame 启动链标记的目录
        internal static bool AppliesToProfile(GameProfile profile, out string gameRoot)
        {
            gameRoot = null;
            if (profile == null) return false;
            if (LolGameExtension.MentionsLol(profile.ExecutablePath)
                || LolGameExtension.MentionsLol(profile.Root)
                || LolGameExtension.MentionsLol(profile.Name)) return false;
            string executable = !string.IsNullOrEmpty(profile.ExecutablePath)
                ? profile.ExecutablePath : profile.LearnedExecutablePath;
            gameRoot = WeGameShell.ResolveGameRoot(profile.Root, executable);
            return gameRoot != null;
        }

        public override bool AppliesTo(GameProfile profile)
        {
            string root;
            return AppliesToProfile(profile, out root);
        }

        public override bool NeedsProcessIdentity(string name)
        {
            return LolRuntimeProcesses.IsWeGameDiscoveryCandidateName(name);
        }

        public override void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            WeGameShellService current;
            lock (gate) current = service;
            if (current != null) current.NotifyProcessChanges(batch);
        }

        public override void NotifySession(GameProfile profile, int rendererPid, long rendererCreation, bool active)
        {
            // 没有对局且服务还没建 不为了发一条"没对局"把服务建起来
            if (!active)
            {
                WeGameShellService current;
                lock (gate) current = service;
                if (current != null) current.NotifySession(null, 0, 0, false);
                return;
            }
            string root;
            if (!AppliesToProfile(profile, out root))
            {
                WeGameShellService current;
                lock (gate) current = service;
                if (current != null) current.NotifySession(null, 0, 0, false);
                return;
            }
            Service.NotifySession(profile, rendererPid, rendererCreation, true);
        }

        public override GameCardExtension CreateCard(GameProfile profile)
        {
            return new WeGameCardPanel(Service, profile);
        }

        public override void Shutdown(int waitMs)
        {
            WeGameShellService current;
            lock (gate) { current = service; service = null; }
            if (current != null) current.Dispose();
        }
    }

    internal static class WeGameLang
    {
        public static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>
        {
            { "wg.card.title", new[]{ "WeGame 脱壳", "WeGame shell removal", "WeGame シェル除去" } },
            { "wg.state.off", new[]{ "借壳启动已关", "Shell launch off", "シェル起動オフ" } },
            { "wg.state.fused", new[]{ "本机已停用 脱壳后游戏退出", "Disabled here: the game exited after shell removal", "本機では停止：シェル除去後にゲームが終了" } },
            { "wg.state.idle", new[]{ "等待游戏启动", "Waiting for the game", "ゲームの起動待ち" } },
            { "wg.state.armed", new[]{ "游戏运行中 {0} 秒后脱壳", "Game running; shell removed in {0} s", "ゲーム実行中、{0} 秒後にシェル除去" } },
            { "wg.state.cleaned", new[]{ "已脱壳 结束 {0} 个进程", "Shell removed; {0} processes ended", "シェル除去済み、{0} プロセスを終了" } },
            { "wg.state.circuit", new[]{ "壳进程反复重生 本局已暂停", "Shell keeps respawning; paused for this match", "シェルが再起動を繰り返すため、この対戦では停止" } },
            { "wg.cell.shell", new[]{ "壳进程", "Shell", "シェル" } },
            { "wg.tip.auto", new[]{ "游戏运行 30 秒后结束 WeGame、Cross 与腾讯附加进程；游戏若在 20 秒内退出则本机停用该游戏的自动脱壳，重新打开开关即恢复。不注入，不改内存，不碰游戏文件。", "30 seconds into a match, WeGame, Cross and Tencent add-on processes are ended. If the game exits within 20 seconds, automatic removal is disabled for this game on this machine; turning the switch back on re-arms it. No injection, no memory edits, no game files touched.", "対戦開始 30 秒後に WeGame・Cross・Tencent 付属プロセスを終了します。20 秒以内にゲームが終了した場合、このゲームの自動除去を本機で停止し、スイッチを入れ直すと再開します。注入もメモリ改変もゲームファイルへの操作もありません。" } },
            { "wg.act.launch", new[]{ "WeGame 脱壳 已发起 WeGame 启动", "WeGame shell: WeGame launch requested", "WeGame シェル: WeGame の起動を要求" } },
            { "wg.act.cleaned", new[]{ "WeGame 脱壳 结束 {0} 个壳进程", "WeGame shell: ended {0} shell processes", "WeGame シェル: {0} 個のシェルプロセスを終了" } },
            { "wg.act.none", new[]{ "WeGame 脱壳 没有可结束的壳进程", "WeGame shell: no shell processes to end", "WeGame シェル: 終了できるシェルプロセスはありません" } },
            { "wg.act.circuit", new[]{ "WeGame 脱壳 壳进程五分钟内三次重生 本局停止自动脱壳", "WeGame shell: shell respawned three times in five minutes; automatic removal stopped for this match", "WeGame シェル: 5 分間に 3 回再起動したため、この対戦では自動除去を停止" } },
            { "wg.act.fused", new[]{ "WeGame 脱壳 游戏在脱壳后 {0} 秒内退出 本机已停用该游戏的自动脱壳 重新打开开关可恢复", "WeGame shell: the game exited within {0} s of shell removal; automatic removal is disabled for this game on this machine. Turn the switch back on to re-arm.", "WeGame シェル: シェル除去後 {0} 秒以内にゲームが終了したため、本機ではこのゲームの自動除去を停止しました。スイッチを入れ直すと再開します。" } },
        };
    }
}
