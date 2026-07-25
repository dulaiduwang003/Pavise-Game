// @author bdth 2074055628@qq.com
// 文件用途 维护内置的三语版本说明并记录已读版本

using System;
using System.Collections.Generic;

namespace AegisApp
{
    internal sealed class ReleaseNote
    {
        public readonly string Version;
        public readonly string Date;
        private readonly string[][] items;

        public ReleaseNote(string version, string date, string[][] entries)
        {
            Version = version; Date = date; items = entries;
        }

        public string Tag { get { return "v" + Version; } }

        public int Count { get { return items == null ? 0 : items.Length; } }

        // 缺译时回退到中文，避免某种语言下条目变空白
        public string Item(int index)
        {
            if (items == null || index < 0 || index >= items.Length) return "";
            string[] row = items[index];
            if (row == null || row.Length == 0) return "";
            int lang = Lang.Cur;
            if (lang < 0 || lang >= row.Length || string.IsNullOrEmpty(row[lang])) return row[0];
            return row[lang];
        }
    }

    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new[]
        {
            new ReleaseNote("1.4.3", "2026-07-25", new[]
            {
                new[]{
                    "GPU 中断亲和优化：把显卡中断引导到游戏所在的核心附近，减少跨核/跨缓存开销。多适配器或多处理器组系统只应用较轻的邻近策略。需重启显卡设备或电脑后生效。",
                    "GPU interrupt affinity tuning: steers GPU interrupt handling toward the cores your game runs on, reducing cross-core/cross-cache overhead. Multi-adapter or multi processor-group systems get the lighter proximity policy only. Takes effect after restarting the device or rebooting.",
                    "GPU 割り込みアフィニティ最適化：GPU の割り込み処理をゲームが動作するコアに近づけ、コア/キャッシュをまたぐオーバーヘッドを減らします。複数アダプタや複数プロセッサグループ環境では軽量な近接ポリシーのみ適用されます。" },
                new[]{
                    "游戏网络优先：同样把网卡中断引导到游戏核心附近，并给游戏目录中已添加的可执行文件的流量打上 QoS 优先级标记，减少排队和抖动。",
                    "Game network priority: steers NIC interrupt handling toward your game's cores and tags traffic from executables in your game library with a QoS priority marking, reducing queueing and jitter.",
                    "ゲームネットワーク優先：NIC の割り込み処理をゲームのコアに近づけ、ゲームライブラリの実行ファイルの通信に QoS 優先マーキングを付与し、キューイングとジッターを減らします。" },
                new[]{
                    "压制豁免不再写死平台名：改为沿当前游戏渲染进程的父进程链向上识别启动器宿主，任何平台、任何游戏都自动适用；进程树够不到的常驻启动器外壳则按通用启动器类别在对局期间兜底豁免。",
                    "Suppression exemptions no longer hard-code platform names: the launcher host chain is now discovered by walking up the running game's parent processes, so it applies to any platform and any game. Long-lived launcher shells the process tree cannot reach fall back to a generic launcher-category exemption while a session is live.",
                    "抑制の除外がプラットフォーム名の直書きではなくなりました：実行中のゲームの親プロセスを辿って起動元ホストを特定するため、任意のプラットフォーム/ゲームに適用されます。プロセスツリーで辿れない常駐起動器はセッション中のみ汎用カテゴリで除外されます。" },
                new[]{
                    "新增开场动画：主窗口打开时淡入并轻微上浮，不再生硬地直接弹出。",
                    "Added an intro animation: the main window fades in with a subtle rise instead of appearing abruptly.",
                    "オープニングアニメーションを追加：メインウィンドウが軽くせり上がりながらフェードインします。" },
                new[]{
                    "新增「检测到游戏后自动收起窗口」开关（默认关闭）：检测到游戏 10 秒后把主窗口收回托盘，每局只收一次，之后手动打开不会再被收走。",
                    "New \"auto-hide the window once a game starts\" toggle (off by default): ten seconds after a game is detected the main window returns to the tray, once per session only — reopening it afterwards keeps it open.",
                    "「ゲーム開始後にウィンドウを自動的に隠す」トグルを追加（既定はオフ）：検出から 10 秒後にトレイへ戻します。1 試合につき 1 回のみで、その後開き直しても閉じられません。" },
                new[]{
                    "新增版本说明：程序内置三语更新说明，离线可查看；升级到新版本后第一次打开会有未读标记。",
                    "Added release notes: trilingual notes ship with the app and are viewable offline, with an unread marker the first time you open a new version.",
                    "リリースノートを追加：三言語のノートを本体に同梱し、オフラインでも閲覧できます。新バージョン初回起動時には未読マークが表示されます。" },
                new[]{
                    "修复：托盘右键菜单文字整体偏上约 6~7 像素，现已垂直居中。",
                    "Fixed: tray context-menu text sat roughly 6-7 pixels above center; it is now vertically centered.",
                    "修正：トレイのコンテキストメニューの文字が約 6〜7 ピクセル上寄りだったのを、垂直中央に修正しました。" },
                new[]{
                    "修复：注册表恢复机制此前无法正确处理二进制类型的值，比较时会退化成类型名比较而非内容比较。",
                    "Fixed: the registry restore helper could not handle binary values correctly — comparisons degraded to type-name matching instead of content matching.",
                    "修正：レジストリ復元処理がバイナリ値を正しく扱えず、内容ではなく型名の比較になっていました。" },
            }),
            new ReleaseNote("1.4.2", "2026-07-24", new[]
            {
                new[]{
                    "任意进程加速：不再局限于游戏，你手动添加的任何程序都能被识别、保护并提速，进程一跑起来就生效，不需要它有可见窗口或处于前台。",
                    "Any-process targeting: not just games — any executable you add is recognized, protected and boosted as soon as it runs, with no need for a visible window or foreground focus.",
                    "任意プロセスの高速化：ゲームに限らず、手動で追加した実行ファイルを認識・保護・高速化します。可視ウィンドウやフォアグラウンドである必要はありません。" },
                new[]{
                    "套壳启动器识别：把真正的主程序解压到临时目录再运行的启动器，现在也能被正确认出（app → app64 / app_x64 / app-v2 这类位数或版本后缀），同时拒绝 app → app_updater 这种会误伤第三方进程的匹配。",
                    "Bootstrap-launcher matching: launchers that extract and run their real binary elsewhere are now recognized via bitness/version suffixes (app → app64 / app_x64 / app-v2), while unrelated continuations such as app → app_updater are rejected.",
                    "ブートストラップ起動器の認識：実体を別の場所で実行する起動器も、ビット数/バージョン後置（app → app64 / app_x64 / app-v2）で認識します。app → app_updater のような無関係な一致は拒否します。" },
                new[]{
                    "粘性会话保护：一局确认之后，alt-tab 切到桌面或别的程序都不会再被误判成目标已退出，保护和加速会一直保持。",
                    "Sticky session protection: once a session is confirmed it survives focus changes — alt-tabbing to the desktop or another app is never mistaken for the target having exited.",
                    "スティッキーセッション保護：一度確定したセッションはフォーカス変更を跨いで維持され、alt-tab が終了と誤認されることはありません。" },
                new[]{
                    "竞技级压制范围：竞技模式（以及自定义模式下新增的同名开关）不再豁免前台和可见窗口，除真正的 Windows 核心服务外一律压制。",
                    "Competitive-grade suppression scope: Competitive mode (and a matching toggle in Custom) drops the foreground/visible-window exemption entirely, covering everything except genuine Windows core services.",
                    "競技グレードの抑制範囲：競技モード（およびカスタムの同等トグル）はフォアグラウンド/可視ウィンドウの除外を廃し、真の Windows コアサービス以外を対象にします。" },
                new[]{
                    "核心系统服务豁免收紧：从原来的「C:\\Windows 下一律不动」改成按进程名加安装路径双重匹配的精确白名单，只在竞技级强度下生效。",
                    "Tighter core-service exemption: replaced the blanket \"anything under C:\\Windows\" rule with an exact allow-list matched by process name and install path together, applied only at Competitive-grade intensity.",
                    "コアサービス除外の厳格化：「C:\\Windows 配下は一律対象外」を廃し、プロセス名とインストールパスの双方で一致する明示的な許可リストに変更しました。" },
                new[]{
                    "修复：竞技模式下 alt-tab 离开游戏，曾会导致游戏进程自己被当成后台压制，在有反作弊的游戏里可能掉线。",
                    "Fixed: alt-tabbing away in Competitive mode could background-suppress the game process itself, which caused disconnects in anti-cheat-protected titles.",
                    "修正：競技モードで alt-tab すると、ゲームプロセス自体が抑制され、アンチチート搭載タイトルで切断が発生することがありました。" },
                new[]{
                    "修复：位数/版本后缀匹配过去允许任意后续内容，可能让名字前缀撞车的无关第三方进程被当成你选定的目标而获得完全信任。",
                    "Fixed: the bitness/version suffix match previously accepted any continuation, which could grant an unrelated third-party process with a colliding name prefix full trust as your selected target.",
                    "修正：ビット数/バージョン後置の一致が任意の継続を許容していたため、名前が衝突する無関係なプロセスが対象として完全に信頼される恐れがありました。" },
                new[]{
                    "安全：反作弊进程无条件豁免压制，且改为按进程名加已知子串双重匹配，堵上「内置目录里没收录的反作弊变体可能被压制」这个缺口。",
                    "Security: anti-cheat processes are unconditionally exempt from suppression, now matched by name plus a set of known substrings, closing a gap where a catalog-absent anti-cheat variant could be suppressed.",
                    "セキュリティ：アンチチートプロセスは無条件で抑制対象外となり、プロセス名と既知の部分文字列の双方で照合します。" },
            }),
            new ReleaseNote("1.0", "2026-07-24", new[]
            {
                new[]{
                    "本仓库下的首个公开发布版本。",
                    "Initial public release under this repository.",
                    "本リポジトリでの最初の公開リリースです。" },
            }),
        };

        public static ReleaseNote Current
        {
            get
            {
                foreach (ReleaseNote n in All)
                    if (string.Equals(n.Version, App.Version, StringComparison.OrdinalIgnoreCase)) return n;
                return null;
            }
        }

        // 首次运行到某个版本时为未读；点开看过之后记下版本号，之后不再提示。
        public static bool HasUnseen
        {
            get { return !string.Equals(Settings.LoadStr(SeenKey, ""), App.Version, StringComparison.OrdinalIgnoreCase); }
        }

        public static void MarkSeen() { Settings.SaveStr(SeenKey, App.Version); }

        internal static List<string> MissingTranslations()
        {
            var bad = new List<string>();
            foreach (ReleaseNote n in All)
                for (int i = 0; i < n.Count; i++)
                {
                    int prev = Lang.Cur;
                    try
                    {
                        for (int lang = 0; lang < 3; lang++)
                        {
                            Lang.Cur = lang;
                            if (string.IsNullOrEmpty(n.Item(i))) bad.Add(n.Version + " #" + i + " lang" + lang);
                        }
                    }
                    finally { Lang.Cur = prev; }
                }
            return bad;
        }
    }
}
