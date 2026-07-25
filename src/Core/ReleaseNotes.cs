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
            new ReleaseNote("1.4.4", "2026-07-25", new[]
            {
                new[]{
                    "这是最终功能版本。功能开发到此为止，后续只做维护：跟进 Windows 更新、跟进反作弊厂商的进程变化、修复发现的问题，不再新增功能。",
                    "This is the final feature release. Feature work stops here; from now on Aegis only receives maintenance — keeping up with Windows updates and anti-cheat vendor changes, and fixing defects. No new features.",
                    "これが最終機能リリースです。機能開発はここで終了し、以降は保守のみ（Windows 更新およびアンチチート各社の変更への追従と不具合修正）を行います。新機能の追加はありません。" },
                new[]{
                    "安全修复：后台压制的反作弊豁免此前用的是精确名单，而检测器用的是含子串的宽判定。两者宽度不一致时，程序自己认定为反作弊的进程仍可能被压制——被压的反作弊心跳超时可能导致掉线。现已统一为同一套判定，并在冻结路径上加了独立的第二道拦截。",
                    "Security fix: background suppression exempted anti-cheat by exact-name list while detection used a broader substring match. Where the two disagreed, a process the app itself classified as anti-cheat could still be suppressed — and a throttled anti-cheat can time out its heartbeat and disconnect you. Both paths now share the broader check, with a second independent guard on the freeze path.",
                    "セキュリティ修正：バックグラウンド抑制のアンチチート除外が完全一致リストのみで、検出側の部分一致より狭く、アプリ自身がアンチチートと判定したプロセスが抑制され得ました（ハートビート切れによる切断の恐れ）。両者を同一判定に統一し、凍結経路にも独立した保護を追加しました。" },
                new[]{
                    "安全修复：向 PowerShell 传递目录与策略名时使用字符串拼接。PowerShell 也把排版引号（U+2019 等）当作定界符，因此转义 ASCII 单引号并不足够；本程序以管理员身份运行，构造特定名称的目录可导致以管理员权限执行任意命令。现改为经环境变量传参、脚本以 -EncodedCommand 传入且不再落临时文件。",
                    "Security fix: directory and policy names were concatenated into PowerShell scripts. PowerShell also treats typographic quotes (U+2019 and friends) as delimiters, so escaping ASCII apostrophes was not sufficient; since Aegis runs elevated, a specially named directory could lead to arbitrary command execution with administrator rights. Arguments now travel via environment variables and the script is passed with -EncodedCommand without a temporary file.",
                    "セキュリティ修正：ディレクトリ名やポリシー名を PowerShell スクリプトへ文字列連結していました。PowerShell は U+2019 等の引用符も区切りとして扱うため ASCII 引用符のエスケープでは不十分で、管理者権限での任意コマンド実行に繋がり得ました。引数は環境変数経由とし、スクリプトは一時ファイルを介さず -EncodedCommand で渡します。" },
                new[]{
                    "修复：游戏档案文件读取失败时会被当成「档案为空」，界面显示空游戏库，此时新增任何一个游戏都会把原档案整份覆盖。现在读取失败会被识别并拒绝保存，原文件受到保护。",
                    "Fixed: a failed read of the game profile file was indistinguishable from an empty library, so adding a single game afterwards overwrote the intact file. A read failure is now detected and blocks saving, protecting the original.",
                    "修正：ゲームプロファイルの読み取り失敗が「空のライブラリ」と区別できず、直後にゲームを 1 つ追加すると元ファイルを丸ごと上書きしていました。読み取り失敗を検出し保存を中止するようにしました。" },
                new[]{
                    "修复：主界面部分说明文字因换行条件从不成立而被截断，被截掉的往往正是风险提示；托盘菜单文字偏上；游戏库列表与标题栏在重复绘制时可能因共享字体被释放而反复报错。",
                    "Fixed: some descriptions in the UI were truncated because the word-wrap branch could never trigger — and the truncated part was often the risk disclaimer. Also fixed tray menu text sitting above centre, and repeated painting of the game library and title bar failing due to a shared font being disposed.",
                    "修正：折り返し条件が成立せず一部の説明文が省略され（多くは注意書きの部分）、トレイメニューの文字が上寄り、共有フォントの解放によりゲームライブラリとタイトルバーの再描画が失敗する問題を修正しました。" },
                new[]{
                    "还原可靠性：进程原有的 EcoQoS 设置、逐游戏图形选项中由 Windows 写入的其它字段、中断亲和改动过的设备名单，现在都会被完整保留并还原，不再被覆盖或遗漏。",
                    "Restore reliability: a process's own EcoQoS opt-in, the other fields Windows stores alongside per-game graphics preferences, and the list of devices whose interrupt affinity was modified are now all preserved and restored rather than overwritten or missed.",
                    "復元の信頼性：プロセス自身の EcoQoS 設定、ゲームごとのグラフィック設定に Windows が併記する他フィールド、割り込みアフィニティを変更したデバイス一覧を、上書き・取りこぼしなく保持・復元します。" },
                new[]{
                    "本次共修复 26 个问题，自测用例从 39 项扩充到 59 项。",
                    "26 defects fixed in this release; the built-in test suite grew from 39 to 59 cases.",
                    "本リリースで 26 件の不具合を修正し、内蔵テストは 39 件から 59 件に拡充されました。" },
            }),
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
