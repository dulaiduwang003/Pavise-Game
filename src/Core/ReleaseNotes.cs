// @author bdth 2074055628@qq.com
// 文件用途 维护内置的三语版本说明并记录已读版本
using System;
using System.Collections.Generic;

namespace PaviseApp
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

        public string Item(int index)
        {
            if (items == null || index < 0 || index >= items.Length) return "";
            string[] row = items[index];
            if (row == null || row.Length == 0) return "";
            int lang = Lang.Cur;
            if (lang < 0 || lang >= row.Length || string.IsNullOrEmpty(row[lang])) return row[0];
            return row[lang];
        }

#if PAVISE_SELFTEST
        // 缺译检查必须看原始行 Item 在缺译时回落到中文 永远查不出缺的那一列
        internal string RawItem(int index, int lang)
        {
            if (items == null || index < 0 || index >= items.Length) return null;
            string[] row = items[index];
            if (row == null || lang < 0 || lang >= row.Length) return null;
            return row[lang];
        }

        internal int RawLanguages(int index)
        {
            if (items == null || index < 0 || index >= items.Length) return 0;
            string[] row = items[index];
            return row == null ? 0 : row.Length;
        }
#endif
    }

    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new[]
        {
            new ReleaseNote("2.2.1.3", "2026-09-07", new[]
            {
                new[]{ "改进 托管电源方案中的 Intel 核显电源项统一平衡 所有设备与档位的插电和电池取值一致", "Intel integrated graphics uses Balanced in managed power plans on all devices, in every mode and on both AC and battery power.", "管理対象の電源プランでは、すべてのデバイス・モードで Intel 内蔵グラフィックスをバランスに統一。AC 接続時とバッテリー使用時の両方に適用します。" },
                new[]{ "下架 语音软件固定豁免与麦克风采集会话动态豁免 KOOK YY Oopz TeamSpeak Mumble QQ TIM 微信回归通用后台规则", "Removed fixed exemptions for voice apps and dynamic exemptions based on microphone capture sessions. KOOK, YY, Oopz, TeamSpeak, Mumble, QQ, TIM and WeChat now follow the general background rules.", "音声アプリの固定除外とマイクのキャプチャセッションによる動的除外を廃止。KOOK、YY、Oopz、TeamSpeak、Mumble、QQ、TIM、WeChat は通常のバックグラウンド規則に従います。" },
                new[]{ "新增 公告 随更新检查取回 有未读时概览页底栏的公告按钮亮红点", "Added announcements, fetched with the update check. The Announcements button in the Overview footer shows a dot while one is unread.", "お知らせを追加。更新確認と同時に取得し、未読があれば概要フッターのお知らせボタンに赤い点が付きます。" },
                new[]{ "改进 候选线程提优默认开 极限档强制开 掌机档和物理核不足 6 个的机器不提供", "Candidate-thread boost is on by default and forced on in Extreme; not available in Handheld or on machines with fewer than 6 physical cores.", "候補スレッド昇格は既定オン、極限では強制オン。携帯機モードと物理コア 6 未満の環境では提供しません。" },
                new[]{ "改进 概览页底栏的教程 问卷 Bug 反馈三个入口收进帮助与反馈一个按钮", "The guide, survey and bug-report entries in the Overview footer are now one Help & feedback button.", "概要フッターのチュートリアル・アンケート・バグ報告の 3 入口を「ヘルプとフィードバック」1 つにまとめました。" },
                new[]{ "改进 反作弊压制写入被拒即放弃 本次运行不再重试 已写入的项退回原值", "Anti-cheat suppression gives up as soon as a write is refused: no retries for the rest of the run, and values already written are restored.", "アンチチート抑制は書き込みを拒否された時点で放棄。今回の実行では再試行せず、書き込み済みの値は元に戻します。" },
                new[]{ "下架 对局切换厂商性能档 旧版开启过的机器启动时和清除全部配置按收据切回原档位", "Retired vendor performance mode. On machines where an older version enabled it, startup and Wipe all configuration switch the profile back from its receipt.", "対戦中のベンダー性能モードを廃止。旧バージョンで有効にした環境では、起動時と全設定の消去でレシートに従い元のプロファイルへ戻します。" },
                new[]{ "修复 游戏退出后提优记录清理不再记成残留警告 每局开头调度保护不再连刷两条", "Boost records left by an exited game are no longer logged as a leftover warning, and the scheduling guard no longer writes two lines at the start of every match.", "終了したゲームの昇格記録を残留警告として記録しなくなり、スケジューリング保護も対戦開始時に 2 行連続で記録しなくなりました。" },
                new[]{ "修复 英文界面下帮助与反馈显示成 Help _feedback", "Fixed Help & feedback rendering as Help _feedback in English.", "英語表示で Help & feedback が Help _feedback と表示される問題を修正。" },
            }),
            new ReleaseNote("2.2.1.1", "2026-09-06", new[]
            {
                new[]{ "新增 WeGame 脱壳推广到全部经 WeGame 启动的游戏 CF 逆战 三角洲等卡片下方与英雄联盟一样有借壳启动开关 脱壳启动与立即净化", "Shell-free launch now covers every game started through WeGame: CF, Ni Zhan, Delta Force and the rest get the same shell-launch switch, shell-free launch and clean-now controls under their library card as League of Legends.", "WeGame 経由で起動する全ゲームに脱殻起動を拡大。CF、逆戦、Delta Force などのカード下に、リーグ・オブ・レジェンドと同じ殻起動スイッチ、脱殻起動、即時クリーンを表示。" },
                new[]{ "改进 对局中正在用麦克风的程序不压制 不分档位 QQ 微信 TIM 通话时放行 挂着不说话照常压制", "Programs actively using the microphone are no longer suppressed during a match, in every tier. QQ, WeChat and TIM are let through while on a call and suppressed as usual otherwise.", "対戦中にマイクを使用中のプログラムは、どのモードでも抑制しません。QQ、WeChat、TIM は通話中のみ通し、それ以外は従来どおり抑制。" },
                new[]{ "改进 KOOK YY Oopz TeamSpeak Mumble 与手柄映射器进保护名单 DS4Windows reWASD XOutput JoyToKey x360ce DualSenseX BetterJoy AntiMicro InputMapper Keysticks 不再被压制", "KOOK, YY, Oopz, TeamSpeak, Mumble and gamepad mappers (DS4Windows, reWASD, XOutput, JoyToKey, x360ce, DualSenseX, BetterJoy, AntiMicro, InputMapper, Keysticks) join the protected list and are no longer suppressed.", "KOOK、YY、Oopz、TeamSpeak、Mumble とゲームパッドマッパー（DS4Windows、reWASD、XOutput、JoyToKey、x360ce、DualSenseX、BetterJoy、AntiMicro、InputMapper、Keysticks）を保護リストに追加。抑制対象外になります。" },
                new[]{ "改进 智能档自适应升档改为开关 默认关 策略页与逐游戏页各一行 候选线程提优改为默认关的实验项", "Adaptive escalation in Standard mode is now a switch, off by default, with a row on the Policy page and the per-game page. Candidate-thread boost is now an experimental item, off by default.", "スマートモードの自動昇格をスイッチ化（既定オフ）。ポリシーページとゲーム別ページに各 1 行。候補スレッド昇格は既定オフの実験項目に変更。" },
                new[]{ "改进 游戏高优先级只在核域完整且 CPU 有余量时给 限核 饱和或核域读不出时保持普通优先级并撤回候选线程提优", "The game gets High priority only when its core domain is complete and the CPU has headroom; with restricted cores, saturation or an unreadable domain it stays at Normal and candidate-thread boost is withdrawn.", "ゲームの高優先度は、コア域が完全で CPU に余裕があるときだけ付与。コア制限、飽和、コア域不明の場合は通常優先度を保ち、候補スレッド昇格も撤回します。" },
                new[]{ "改进 电竞档 Intel 大小核线程调度交回系统自动 台式机核心停泊与笔记本插电同口径放开", "In Esports mode, Intel hybrid thread scheduling is handed back to the system's automatic policy, and desktop core parking follows the same rule as a plugged-in laptop.", "eスポーツモードで Intel ハイブリッドのスレッド配分をシステム自動に戻し、デスクトップのコアパーキングはノート PC の AC 接続時と同じ扱いに。" },
                new[]{ "改进 功耗让路 读数缺失不进决策 验证期长时间没有证据自动撤回", "Power yield: missing readings no longer enter the decision, and a verification phase without evidence for too long is reverted automatically.", "電力譲渡：欠測値は判定に使わず、検証中に長く証拠が得られなければ自動で撤回。" },
                new[]{ "改进 自动后台节能显卡按程序路径保护可见程序 同一程序的隐藏子进程也不登记", "Auto background power-saving GPU protects visible programs by executable path, so hidden child processes of the same program are not registered either.", "自動バックグラウンド省電力 GPU は可視プログラムを実行ファイルのパスで保護。同じプログラムの非表示子プロセスも登録しません。" },
                new[]{ "改进 极限电源参数按方案记账 AC DC 分开备份与恢复 最低处理器状态按平台接口证据决定", "Extreme power knobs are recorded per plan with AC and DC backed up and restored separately, and the minimum processor state follows the platform interface evidence.", "極限の電源パラメーターはプラン単位で記録し、AC と DC を別々にバックアップ・復元。最小プロセッサ状態はプラットフォームのインターフェイス証拠に基づいて決定。" },
                new[]{ "改进 反作弊目录补国内平台 完美世界竞技平台 5E B5 只保护不压制 网易补 NeacClient OWNeacClient 腾讯补 TP3Helper ACE-Service64 米哈游驱动进内核识别", "Anti-cheat catalog now covers Chinese arenas: Perfect World Arena, 5E and B5 are protected without a suppression switch; NetEase gains NeacClient and OWNeacClient, Tencent gains TP3Helper and ACE-Service64, and HoYoverse drivers join kernel detection.", "アンチチート目録に中国のアリーナを追加。完美世界競技プラットフォーム、5E、B5 は保護のみで抑制スイッチなし。NetEase に NeacClient と OWNeacClient、Tencent に TP3Helper と ACE-Service64 を追加し、HoYoverse のドライバーをカーネル検出に加えました。" },
                new[]{ "改进 卸载 Pavise 收进程序本身 设置页一键完成还原与清理 不再需要 Pavise-Uninstall.cmd","Uninstall Pavise now runs entirely inside the app: one click on the Settings page restores and cleans up, with no Pavise-Uninstall.cmd needed.", "Pavise のアンインストールをアプリ内に統合。設定ページのワンクリックで復元とクリーンアップが完了し、Pavise-Uninstall.cmd は不要になりました。" },
                new[]{ "改进 运行开销 压制扫描逐进程判定按进程身份记住 白名单评估复用 共享显存查询移到后台","Runtime overhead: per-process checks in the suppression sweep are remembered by process identity, whitelist evaluation is reused, and the shared-VRAM query runs in the background.", "実行時オーバーヘッド：抑制スキャンのプロセス別判定をプロセス識別で記憶、ホワイトリスト評価を再利用、共有 VRAM の照会をバックグラウンドへ。" },
                new[]{ "移除 缓存预热 不再在对局稳定后预读游戏资产", "Removed cache warm-up; game assets are no longer pre-read after the match settles.", "キャッシュ予熱を削除。対戦安定後のゲーム資産の先読みは行いません。" },
                new[]{ "修复 反作弊绑核被进程改回且写入被拒后不再每分钟重试并记异常 本次运行放弃绑核 其余压制照常","Anti-cheat core pinning no longer retries every minute and logs an error after the process reverts it and refuses the rewrite; pinning is dropped for the run while the rest of the suppression stays.", "アンチチートのコア固定がプロセスに戻され再書き込みを拒否された後、毎分の再試行と異常記録を行わないよう修正。今回の実行では固定を放棄し、他の抑制は継続します。" },
                new[]{ "修复 游戏库与自动入库的忽略名单改为事务写入 档案被占用时旧档保留 可稍后重试","The library and auto-add ignore lists are now written transactionally; when the profile is busy the old profile is kept and you can retry later.", "ライブラリと自動追加の無視リストをトランザクション書き込みに変更。プロファイル使用中は旧プロファイルを保持し、後で再試行できます。" },
            }),
            new ReleaseNote("2.2.0.2", "2026-09-04", new[]
            {
                new[]{ "新增 重压后台绑核 实验性 默认关 可逐游戏覆盖 对局中把连续 10 秒占用超过半个核心的后台限定到游戏不用的最少核心 空闲后台不动 负载回落 30 秒解除 退局还原", "Added heavy background core squeeze (experimental, off by default, per-game overridable): during a match, background processes using more than half a core for 10 seconds straight are confined to the fewest cores the game does not use. Idle background is left alone, the limit lifts 30 seconds after the load drops, and everything is restored on exit.", "高負荷バックグラウンドのコア収縮を追加（実験的、既定オフ、ゲーム別に上書き可）。対戦中、10 秒連続で半コア以上を使うバックグラウンドをゲームが使わない最少のコアに限定します。アイドルなものには触れず、負荷が下がって 30 秒で解除、退出時に復元。" },
                new[]{ "新增 反作弊绑核 被压制的反作弊进程限定到游戏不用的末尾核心 写入被拒时跳过 不重试", "Added anti-cheat core pinning: suppressed anti-cheat processes are confined to the trailing cores the game does not use, and refused writes are skipped without retry.", "アンチチートのコア固定を追加。抑制中のアンチチートプロセスをゲームが使わない末尾のコアに限定し、拒否された書き込みはスキップして再試行しません。" },
                new[]{ "新增 添加游戏支持文件夹与拖放 文件夹能挑出唯一主程序就直接加 多个候选列出来选", "Add Game now accepts folders and drag-and-drop: a folder with a single identifiable main program is added directly, and multiple candidates are listed for you to pick.", "ゲームの追加がフォルダーとドラッグ＆ドロップに対応。主プログラムを一意に特定できるフォルダーはそのまま追加し、候補が複数ある場合は一覧から選べます。" },
                new[]{ "改进 托管电源方案改事件驱动 第三方切走立即拉回 成功后不再定时巡检", "The managed power plan is now event-driven: a switch by another program is pulled back immediately, and there is no periodic audit after a successful apply.", "管理電源プランをイベント駆動に変更。他のプログラムによる切り替えは即座に引き戻し、適用成功後の定期巡回は行いません。" },
                new[]{ "改进 退局还原 本次会话接管过方案的 第三方在退出前切走也还原原方案", "Restore on exit: if this session took over the plan, the original plan is restored even when another program switched away just before exit.", "退出時の復元：本セッションでプランを引き継いでいれば、退出直前に他のプログラムが切り替えても元のプランへ復元します。" },
                new[]{ "改进 自主调频开启时最低处理器状态写温和值 不再锁 100", "With autonomous frequency scaling on, the minimum processor state is written to the calm value instead of being locked at 100.", "自律的な周波数制御が有効な場合、最小プロセッサ状態は 100 固定ではなく穏やかな値を書き込みます。" },
                new[]{ "改进 功耗让路绑定渲染进程与渲染卡 进程或显卡变化即作废基线并还原 EPP", "Power yield is bound to the renderer process and its rendering adapter; a change to either voids the baseline and restores EPP.", "電力譲渡をレンダラープロセスと描画アダプターに紐付け。いずれかが変われば基準値を破棄して EPP を復元します。" },
                new[]{ "改进 顶栏模式按钮显示逐游戏配置来源 Ctrl+F 打开全局搜索", "The mode button in the title bar shows which per-game profile it comes from, and Ctrl+F opens the global search.", "タイトルバーのモードボタンにゲーム別設定の出所を表示。Ctrl+F でグローバル検索を開きます。" },
                new[]{ "改进 运行开销 主循环不再有阻塞采样 功耗让路改持久查询 首页动画只在前台跑 日志页无新内容不读文件", "Runtime overhead: no blocking sampling on the main loop, power yield uses a persistent query, the home animation runs only while Pavise is in the foreground, and the log page does not read the file when there is nothing new.", "実行時オーバーヘッド：メインループでのブロッキングサンプリングを廃止、電力譲渡は持続的なクエリに変更、ホーム画面のアニメーションは前面時のみ、ログページは新規内容がなければファイルを読みません。" },
                new[]{ "修复 极限档强制锁与解析层对齐 环境页强制锁过资格门 切走档位后开关恢复", "Fixed the Extreme forced locks to match the resolver, the Environment page forced locks now pass the eligibility gate, and switches recover after leaving the tier.", "極限の強制ロックを解決層と一致させ、環境ページの強制ロックは資格ゲートを通過するように修正。ティアを離れるとスイッチが復帰します。" },
                new[]{ "修复 显卡页与 Intel 页的按键与本机支持判定 NVIDIA 低延迟选择器在极限档加锁", "Fixed key handling and local-support detection on the Graphics and Intel pages, and the NVIDIA low-latency selector is locked in Extreme.", "グラフィックスページと Intel ページのキー操作と本機サポート判定を修正。NVIDIA 低遅延セレクターは極限でロックします。" },
                new[]{ "修复 英文输入与 Intel 低延迟在极限会话真正生效 Intel 低延迟熔断进退出集", "English input and Intel low latency now really take effect in an Extreme session, and the Intel low-latency fuse joins the exit set.", "英語入力と Intel 低遅延が極限セッションで実際に有効になるよう修正。Intel 低遅延のヒューズは終了セットに加わります。" },
                new[]{ "修复 功耗让路 各卡都是 0% 时不再误判为渲染卡迁移", "Power yield no longer mistakes an all-zero utilization reading across adapters for a rendering-adapter migration.", "電力譲渡：全アダプターの使用率が 0% のとき、描画アダプターの移行と誤判定しなくなりました。" },
            }),
            new ReleaseNote("2.2.0.1", "2026-09-03", new[]
            {
                new[]{ "新增 英雄联盟增强回归 游戏库卡片内置 脱壳启动 立即净化 恢复界面 借壳启动 对局真无头 附加层删除", "Added League of Legends enhancement back, built into the library card: shell-free launch, clean now, restore UI, shell launch, headless match, add-on deletion.", "リーグ・オブ・レジェンド強化が復帰。ライブラリカードに内蔵：シェルなし起動、即時クリーン、UI 復元、シェル起動、対局ヘッドレス、アドオン削除。" },
                new[]{ "新增 设置页卸载 Pavise 按钮", "Added an Uninstall Pavise button on the Settings page.", "設定ページに Pavise をアンインストールを追加。" },
                new[]{ "改进 卸载脚本 先由程序自身按收据还原 再清任务 方案 设置 数据与旧版残留", "Uninstall script: the app restores from receipts first, then the task, plan, settings, data and old leftovers are removed.", "アンインストールスクリプト：先にアプリが記録どおり復元し、タスク、プラン、設定、データ、旧版残留物を削除。" },
                new[]{ "改进 设置页分标签 极限模式 应用自身 维护 窗口外观", "Settings page split into tabs: Extreme, the app itself, maintenance, window appearance.", "設定ページをタブ化：極限、アプリ自身、メンテナンス、ウィンドウ外観。" },
                new[]{ "改进 家族后台压制 未观测到渲染的条目不能打开", "Family suppression cannot be switched on for an entry whose rendering has not been observed.", "レンダリング未観測のエントリはファミリー抑制をオンにできない。" },
                new[]{ "改进 核显电源方案 独显渲染的机器写平衡", "Integrated-graphics power plan stays Balanced on machines rendering on a discrete GPU.", "独立 GPU 描画のマシンでは内蔵グラフィックス電源プランをバランスに。" },
                new[]{ "改进 极限档 VBS 关闭不再被 Hyper-V 残留服务键挡住", "Extreme VBS off is no longer blocked by a leftover Hyper-V service key.", "極限の VBS 無効化が Hyper-V の残留サービスキーに阻まれなくなった。" },
                new[]{ "改进 厂商性能档默认开 不再由档位强制", "Vendor performance mode on by default, no longer forced by tier.", "ベンダー性能モードは既定オン、ティアによる強制なし。" },
                new[]{ "改进 全局计时器分辨率退出极限自动清单 两项计时器默认关", "Global timer resolution left the Extreme auto list; both timer items off by default.", "グローバルタイマー分解能を極限自動リストから除外。タイマー 2 項目は既定オフ。" },
                new[]{ "改进 极限档电源空闲三旋钮因部分机型有提升重新上线", "The three Extreme power-idle knobs are back because some machines gain from them.", "極限の電源アイドル 3 項目は一部機種で効果があるため復帰。" },
                new[]{ "下架 显卡频率锁定 旧版收据启动时解锁", "Removed GPU clock lock; receipts from older versions are released at startup.", "GPU クロック固定を廃止。旧版の記録は起動時に解除。" },
                new[]{ "下架 网络收包引离游戏核 旧版收据启动时写回", "Removed packet-processing steering; receipts from older versions are written back at startup.", "受信パケット処理退避を廃止。旧版の記録は起動時に書き戻す。" },
            }),
            new ReleaseNote("2.2.0.0", "2026-09-02", new[]
            {
                new[]{ "新增 显卡频率锁定 对局中 NVIDIA 钉在最高频率 AMD 抬高最低频率 台式机电竞与极限档强制 退局解锁", "Added GPU clock lock: NVIDIA pinned at maximum, AMD minimum raised, during a match. Forced in Esports and Extreme on desktops, unlocked after the match.", "GPU クロックロックを追加。対局中は NVIDIA を最大に固定、AMD は最低を引き上げ。デスクトップの eスポーツと極限で強制、退局で解除。" },
                new[]{ "新增 笔记本厂商性能档 Lenovo Legion 与 ASUS ROG 对局中切到性能档 退局切回 默认开启 可按需关闭", "Added the laptop vendor performance mode for Lenovo Legion and ASUS ROG, switched during a match and back afterwards. Enabled by default and can be turned off.", "ノート PC のベンダー性能モードを追加。Lenovo Legion と ASUS ROG を対局中に性能モードへ切替、退局で戻す。既定でオン、必要に応じてオフにできます。" },
                new[]{ "新增 NVIDIA 窗口化 G-SYNC 只对全屏生效的 G-SYNC 对局中扩到窗口化 退局写回", "Added NVIDIA windowed G-SYNC: full-screen-only G-SYNC is extended to windowed during a match and written back afterwards.", "NVIDIA ウィンドウ G-SYNC を追加。全画面限定の G-SYNC を対局中はウィンドウにも適用し、退局で戻す。" },
                new[]{ "新增 关闭 Intel Endurance Gaming 电池供电不再封顶帧率 默认关 极限档强制", "Added Intel Endurance Gaming off so the frame rate is no longer capped on battery. Off by default, forced in Extreme.", "Intel Endurance Gaming 無効化を追加。バッテリー駆動時のフレームレート制限をなくす。既定オフ、極限で強制。" },
                new[]{ "新增 暂停自动维护 对局中挂起 Windows 自动维护 默认开", "Added automatic-maintenance pause during a match. On by default.", "対局中の自動メンテナンス一時停止を追加。既定オン。" },
                new[]{ "新增 逐游戏 DPI 兼容层 与全屏优化兼容层并存", "Added a per-game high-DPI compatibility layer alongside the full-screen-optimization layer.", "ゲーム別の高 DPI 互換レイヤーを追加。全画面最適化レイヤーと共存。" },
                new[]{ "新增 有线跃点修复 有线网关在线但默认路由走无线时改回自动跃点 体检页有修复入口", "Added the wired-metric repair for a default route over wireless while a wired gateway is up, with a repair entry on the audit page.", "有線メトリック修復を追加。有線ゲートウェイ稼働中に既定経路が無線を通る場合に自動メトリックへ戻す。診断ページに修復入口。" },
                new[]{ "新增 计时器精度 0.5 毫秒档", "Added a 0.5 ms timer resolution tier.", "タイマー精度に 0.5 ms 段階を追加。" },
                new[]{ "新增 疑似恶意进程提醒 无窗口后台进程长时间占满核心或自行解除核心限制时 记异常日志并弹气泡提示可能感染挖矿病毒", "Added the suspected-malware alert: a windowless background process that saturates cores for a long time or lifts its own core restriction is logged as an error with a tray warning about a possible miner.", "不審プロセス警告を追加。ウィンドウのない常駐プロセスが長時間コアを占有するか自らコア制限を解除した場合、エラー記録とトレイ通知でマイナー感染の可能性を知らせる。" },
                new[]{ "新增 系统环境页三项持久项 可变刷新率优化 关闭网卡链路节能 AMD Smart Access Memory 备份后写入 可还原", "Added three persistent System Environment items: variable refresh rate optimization, NIC link power saving off, and AMD Smart Access Memory, each backed up and restorable.", "システム環境ページに持続項目 3 つを追加。可変リフレッシュレート最適化、NIC リンク省電力オフ、AMD Smart Access Memory。バックアップ付きで復元可能。" },
                new[]{ "新增 托管电源方案十二项 USB3 链路电源管理 核显电源方案 可切换显卡策略 与九项一类核旋钮", "Added twelve managed power plan items: USB3 link power management, the integrated graphics power plan, the switchable graphics policy, and nine class-1 core knobs.", "管理電源プランに 12 項目を追加。USB3 リンク電源管理、内蔵 GPU 電源プラン、切替グラフィックスポリシー、クラス 1 コア項目 9 つ。" },
                new[]{ "改进 日志分级 环境限制类记录改为警告 只有真正的功能故障计入异常", "Improved log severity: environment-limited records are warnings; only real functional failures count as errors.", "ログ分級を改善。環境制限による記録は警告とし、実際の機能障害のみエラーに数える。" },
                new[]{ "改进 卸载脚本 删除收据前先按收据还原系统改动 托管方案按现名匹配", "Improved the uninstall script: system changes are restored from their receipts before the receipts are deleted, and the managed plan is matched by its current name.", "アンインストールスクリプトを改善。記録を削除する前にシステム変更を記録から復元し、管理プランは現在の名前で照合。" },
                new[]{ "改进 极限档强制项加硬件资格门 低延迟强制降为开 工作集修剪加内存门 保留核八物理核起 禁止 CPU 空闲 AMD 不提供 缓存预热跳过 NVMe 硬件调度下不写显卡降级 蓝牙输出跳过音频低延迟", "Improved the hardware gates on Extreme forced items: low latency forced to On, a memory gate on working-set trimming, reserved cores from eight physical cores, no CPU idle disabling on AMD, cache warm-up skipping NVMe, no GPU demotion under hardware scheduling, low-latency audio skipping Bluetooth outputs.", "極限の強制項目にハードウェア資格ゲートを追加。低遅延は On に強制、ワーキングセット削減にメモリ条件、予約コアは物理 8 コア以上、AMD では CPU アイドル禁止なし、キャッシュ予熱は NVMe をスキップ、ハードウェアスケジューリング時は GPU 降格を書かず、Bluetooth 出力では低遅延オーディオをスキップ。" },
                new[]{ "新增 极限崩溃保险丝 解锁后系统两次非正常重启 启动时自动关闭解锁并按账本还原环境项", "Added the Extreme crash fuse: two abnormal restarts after the unlock turn the unlock off at startup and restore environment items from the ledger.", "極限クラッシュヒューズを追加。解錠後に異常再起動が 2 回あれば起動時に解錠を自動オフにし、台帳から環境項目を復元。" },
                new[]{ "调整 计时器恒定节拍不再由极限档自动写入 保留手动开关", "Adjusted: the constant timer tick is no longer written automatically by Extreme; the manual switch stays.", "タイマー固定ティックは極限が自動書き込みしなくなりました。手動スイッチは残ります。" },
                new[]{ "撤回 极限档电源空闲策略 三项空闲旋钮不再写入 旧版写过的按快照写回原值", "Withdrew the Extreme power idle policy: the three idle knobs are no longer written, and values written by the previous version are restored from their snapshot.", "極限の電源アイドルポリシーを撤回。3 つのアイドル項目は書き込まず、旧版が書いた値はスナップショットから復元。" },
                new[]{ "改名 托管电源方案改为 由软件调度 加机器码 去掉 PG 前缀", "Renamed the managed power plan to \"Scheduled by Pavise\" plus the machine code, dropping the PG prefix.", "管理電源プランを「Scheduled by Pavise」＋マシンコードに改名し PG 接頭辞を廃止。" },
                new[]{ "改进 极限解锁期间 环境页已开着的清单项锁成预设强制开 启动时补写没开上的项 停用只走关闭解锁", "Improved: while Extreme is unlocked, environment items on the list that are on are locked as forced by preset, items not yet on are written at startup, and stopping goes through turning the unlock off only.", "極限解錠中は環境ページで有効なリスト項目をプリセット強制としてロックし、未有効の項目は起動時に書き込み、停止は解錠オフからのみ。" },
                new[]{ "改进 锁定标签统一为 预设强制开 预设强制关 本机不适用 系统已开启", "Unified the lock badges: Forced on, Forced off, Not applicable here, Already on in the system.", "ロックバッジを「強制オン」「強制オフ」「本機では対象外」「システムで有効」に統一。" },
            }),
            new ReleaseNote("2.1.3.4", "2026-09-01", new[]
            {
                new[]{ "重做 网卡中断合并策略 不再由极限档统一关闭 旧版写入启动时按原收据还原 新实验仅处理当前公网 IPv4 探测路由命中的唯一物理有线出口 标准值严格限制为 Off 与驱动管理 写前记录 NetCfg GUID 与 PnP 实例双重身份并逐次回读 身份变化 探测路由落在 VPN 虚拟聚合接口 多活动有线网卡与未知驱动值全部跳过", "Reworked NIC interrupt moderation: Extreme no longer disables it globally, and legacy writes are restored from their original receipts at startup. The new experiment targets only the single physical wired adapter selected by the current public-IPv4 route probe, strictly limits the standard value to Off or driver-managed, journals both NetCfg GUID and PnP instance identity before writing, and verifies every read-back. Identity changes, a probe route through VPN/virtual/teamed interfaces, multiple active wired adapters, and unknown driver values are all skipped.", "NIC 割り込みモデレーション戦略を再設計。Extreme による一括無効化をやめ、旧版の変更は起動時に元の記録から復元します。新しい実験は現在の公開 IPv4 経路プローブで選ばれた唯一の物理有線 NIC のみを対象とし、標準値を Off／ドライバー管理に限定、NetCfg GUID と PnP インスタンス ID の両方を先に記録して再読込検証します。識別情報の変化、VPN・仮想・チーミング経由のプローブ経路、複数の稼働中有線 NIC、未知のドライバー値はすべてスキップします。" },
                new[]{ "新增 极限档 以电竞档为底 一次开启本机全部通过资格检查的可选优化项 压制范围与力度和电竞完全相同 默认隐藏 需在设置页解锁并重启电脑后才出现 解锁写入的环境项按账本记录 关闭解锁并重启即完全还原", "Added the Extreme tier: Esports as the base with every eligible optional optimization on this machine enabled at once, its suppression scope and strength identical to Esports. Hidden by default and revealed only after unlocking in Settings and restarting; environment items written at unlock are recorded in a ledger and fully restored when the unlock is turned off and the machine restarts.", "極限ティアを追加。eスポーツを土台に、本機で条件を満たすオプション最適化項目を一度にすべて有効化します。抑制の範囲と強度は eスポーツと完全に同一です。既定では非表示で、設定で解錠して再起動すると現れます。解錠時に書き込んだシステム環境項目は台帳に記録し、解錠をオフにして再起動すれば完全に復元します。" },
                new[]{ "改名 专注档改为电竞档 轻载档改为掌机档 智能档与自定义档名称不变", "Renamed tiers: Focus is now Esports and Light is now Handheld; Smart and Custom keep their names.", "ティア名を変更。集中は eスポーツへ、軽負荷は携帯機へ。スマートとカスタムはそのままです。" },
                new[]{ "新增 音频低延迟 以系统支持的最小音频缓冲开一条静音流 音频引擎随之按最小周期运行 关流自动还原无任何残留 极限档专属", "Added low-latency audio: a silent stream opened at the smallest audio buffer the system supports makes the whole audio engine run at its minimum period, and closing the stream restores everything with no residue. Extreme tier only.", "低遅延オーディオを追加。システムが対応する最小オーディオバッファで無音ストリームを開き、オーディオエンジンを最小周期で動作させます。ストリームを閉じれば残留なく自動復帰します。極限ティア専用。" },
                new[]{ "新增 DWM 合成低延迟 对局中把桌面合成线程注册进多媒体实时档 无边框和窗口化游戏在 CPU 满载时合成不被抢占 独占全屏无作用 极限档专属", "Added low-latency DWM composition: the desktop compositor's threads are registered into the multimedia real-time tier during a match so borderless and windowed games keep compositing under full CPU load. No effect in exclusive fullscreen. Extreme tier only.", "低遅延 DWM コンポジションを追加。対局中にデスクトップコンポジターのスレッドをマルチメディアリアルタイムティアへ登録し、ボーダーレスやウィンドウモードのゲームが CPU 満載時でもコンポジションを維持します。排他フルスクリーンでは効果がありません。極限ティア専用。" },
                new[]{ "新增 网络收包引离游戏核 把物理有线网卡的 RSS 收包处理区间改到压制核 与既有的中断钉核形成收包全链路引导 逐网卡记账退局还原 极限档专属", "Added packet-processing steering: physical wired adapters' RSS range is moved onto the suppressed cores, which together with the existing interrupt pinning steers the whole receive path away from the game cores. Recorded per adapter and restored at match end. Extreme tier only.", "受信パケット処理の退避を追加。物理有線アダプターの RSS 範囲を抑制コアへ移し、既存の割り込みピン留めと合わせて受信経路全体をゲームコアから遠ざけます。アダプターごとに記帳し、対局終了時に復元します。極限ティア専用。" },
                new[]{ "新增 压制后台工作集修剪 后台被隔离后清空一次其物理内存工作集 页面转入待机列表腾给游戏 每个进程实例只清一次 反作弊进程不清 极限档专属", "Added suppressed working-set trimming: an isolated background process has its physical-memory working set emptied once and the pages move to the standby list for the game. Each process instance is trimmed once and anti-cheat processes never are. Extreme tier only.", "抑制中ワーキングセットの削減を追加。隔離したバックグラウンドプロセスの物理メモリワーキングセットを一度だけ空にし、ページをスタンバイリストへ移してゲームに回します。プロセスインスタンスごとに一度だけで、アンチチートは対象外です。極限ティア専用。" },
                new[]{ "新增 关闭内存压缩与页合并 省下压缩线程与页合并扫描的后台处理器开销 内存 24 GB 起提供 重启彻底生效 关闭时只回启原本开着的项 极限档专属", "Added disabling of memory compression and page combining, removing the background CPU cost of the compression thread and page-combining scans. Offered from 24 GB of memory, fully effective after a restart, and switching off re-enables only what was on before. Extreme tier only.", "メモリ圧縮とページ結合の無効化を追加。圧縮スレッドとページ結合スキャンのバックグラウンド CPU 消費をなくします。メモリ 24 GB 以上で提供し、再起動で完全に有効化され、オフにすると元々有効だった項目のみ戻します。極限ティア専用。" },
                new[]{ "新增 内核保留核 预留两个物理核 系统线程与普通程序全天避开 只有经核心分区对局的游戏用得上 跳过 CPU0 所在物理核 混合架构只选性能核 八物理核起且单处理器组才提供 重启生效 极限档专属", "Added kernel reserved cores: two physical cores are set aside that system threads and ordinary programs avoid at all times, usable only by games in a core-partitioned match. The core holding CPU0 is skipped, hybrid CPUs use performance cores, and it is offered only on single-group machines with at least eight physical cores. Effective after a restart. Extreme tier only.", "カーネル予約コアを追加。2 つの物理コアを確保し、システムスレッドと通常のプログラムは常時回避します。コア分割対局中のゲームのみが使用でき、CPU0 を含む物理コアは除外、ハイブリッド構成では性能コアを選びます。単一プロセッサーグループかつ物理 8 コア以上の環境でのみ提供し、再起動で有効になります。極限ティア専用。" },
                new[]{ "新增 极限档电源空闲策略 进入深度空闲门槛拉满 退出门槛压到最低 空闲检查周期取本机下限 关闭门槛负载缩放 只在极限档写入 写入前快照现值 切回其它档位按快照还原 未暴露的项自动跳过", "Added extreme-tier idle policy for the managed plan: the promote threshold is maxed, the demote threshold minimized, the idle check period set to the machine minimum and threshold scaling disabled. Written only in the Extreme tier, with current values snapshotted first and restored when switching to any other tier; settings not exposed on the plan are skipped.", "極限ティアの電源アイドルポリシーを追加。深いアイドルへの昇格しきい値を最大に、離脱しきい値を最小にし、アイドル判定周期を本機の下限へ、しきい値の負荷スケーリングを無効にします。極限ティアでのみ書き込み、書き込み前に現在値をスナップショットし、他のティアへ切り替えるとスナップショットから復元します。プランに公開されていない項目はスキップします。" },
                new[]{ "新增 托管电源方案的存储电源项 对局中把 NVMe 电源态延迟容忍清零并保持 AHCI 链路 Active 防止固态盘从低功耗态唤醒造成的偶发卡顿 电池上放开 NVMe 侧", "Added storage power items to the managed power plan: during a match the NVMe power-state latency tolerances are zeroed and the AHCI link is kept Active, preventing the occasional hitch of an SSD waking from a low-power state. The NVMe side is relaxed on battery.", "管理電源プランにストレージ電源項目を追加。対局中は NVMe 電源状態の遅延許容値をゼロにし、AHCI リンクを Active に保って、SSD の低電力状態からの復帰による突発的なカクつきを防ぎます。バッテリー駆動時は NVMe 側を緩めます。" },
                new[]{ "改进 MMCSS 多媒体调度增加关闭懒惰检测档 消掉百毫秒周期的空闲检测循环 已注册线程的调度不再变钝", "Improved MMCSS scheduling by disabling the lazy idle-check tier, removing the hundred-millisecond idle-detection loop that dulled scheduling for registered threads.", "MMCSS マルチメディアスケジューリングにレイジー検出ティアの無効化を追加し、登録済みスレッドのスケジューリングを鈍らせていた 100 ミリ秒周期のアイドル検出ループをなくしました。" },
                new[]{ "改进 禁止 CPU 空闲改为全部电源方案与全部电源来源可用 写入当前活动方案的交流与电池两侧值 对局中方案被切走时先按收据还原旧方案再钉新方案", "Improved CPU idle disabling: it now works on any power plan and any power source, writing both the AC and DC values of the currently active plan. When the plan is switched away mid-match, the old plan is restored from its receipt before the new one is pinned.", "CPU アイドルの無効化を改善。すべての電源プランと電源ソースで利用でき、現在アクティブなプランの AC/DC 両方の値に書き込みます。対局中にプランが切り替わった場合は、レシートに従って旧プランを復元してから新プランを固定します。" },
                new[]{ "改进 系统版本低于 Windows 10 2004 时不再启动 提示前先按记录还原此前版本留下的系统改动", "Improved the version baseline: on builds below Windows 10 2004 Pavise no longer starts, and it restores any system changes left by earlier versions from their records before reporting this.", "バージョン基準を改善。Windows 10 2004 未満のビルドでは起動せず、通知の前に以前のバージョンが残したシステム変更を記録に従って復元します。" },
                new[]{ "移除 实验功能标签 显存驻留与缓存预热等项并入正式功能分区 界面不再区分实验与正式", "Removed the experimental label: video memory residency, cache warm-up and the other items moved into the regular feature sections, and the interface no longer distinguishes experimental from regular features.", "実験機能ラベルを削除。ビデオメモリ常駐やキャッシュ予熱などは通常の機能セクションに統合し、画面上で実験機能と正式機能を区別しなくなりました。" },
            }),
            new ReleaseNote("2.1.3.3", "2026-08-31", new[]
            {
                new[]{ "新增 缓存预热 实验功能 对局稳定后以最低磁盘优先级将游戏资产预读进系统待机缓存 纯读取无残留 仅在交流供电 内存充足且游戏盘为固态时执行 与待机清理互斥", "Added experimental cache warm-up: after the match settles, game assets are pre-read into the system standby cache at the lowest disk priority. Reads only, with no residue; runs on AC power with sufficient memory and an SSD game drive, and is mutually exclusive with standby cleanup." },
                new[]{ "新增 内存驻留 实验功能 物理内存持续吃紧时锁定游戏工作集下限 避免游戏页被修剪 写入回读核实 验证失败撤销并停止尝试 退局还原 物理内存低于 16 GB 不参与", "Added experimental memory residency: under sustained memory pressure a hard working-set minimum is pinned for the game so trimming leaves its pages alone. Writes are read back and verified, a failed verification reverts and stops further attempts, everything is restored at match end, and machines with less than 16 GB do not participate." },
                new[]{ "新增 对局单屏 对局中切换为仅主屏 避免误点副屏导致游戏失焦 退局恢复原多屏布局 快照先行 回读核实 异常退出后下次启动补切 远程会话不动作", "Added single display in match: switches to primary-only during the match so stray clicks on a second screen cannot take the game's focus, and restores the original layout afterwards. Snapshot-first with read-back verification, recovery at the next launch after an abnormal exit, and no action inside remote sessions." },
                new[]{ "新增 关闭网卡中断合并 系统环境页持久项 仅修改物理有线网卡的标准化开关 收包立即触发中断以降低网络延迟 重启生效 备份可还原", "Added NIC interrupt moderation disabling as a persistent System Environment tweak. Only the standardized setting on physical wired adapters is modified, raising an interrupt per packet to lower network latency; effective after a restart, backed up and revertible." },
                new[]{ "新增 智能档自适应升档 CPU 持续饱和时临时启用专注档的压制范围与电源激进列 稳定两分钟降回 一局最多三次 智能档内置 与智能保帧同级 无需开关", "Added adaptive escalation to Standard mode: sustained CPU saturation temporarily enables Focus-mode suppression scope and the aggressive power column, stepping back down after two stable minutes, at most three times per match. Built into Standard mode alongside frame guard, with no switch." },
                new[]{ "新增 自动中断编排 实验功能 依据多局实测将冲突设备的中断钉离游戏核 写入收据 重启生效 再累计三局自动验收 无效则回滚并对该驱动版本停止尝试 网卡 存储 显卡 音频与键鼠链路不在范围内", "Added experimental automatic interrupt orchestration: devices identified by multi-match measurements are pinned off the game cores with full receipts, effective after a restart, then verified over three further matches with automatic rollback and no further attempts for that driver version when ineffective. NICs, storage, display, audio and the input chain are out of scope." },
                new[]{ "新增 自动后台节能显卡 对局稳定后将仍在游戏渲染卡上执行 3D 负载的后台程序登记为下次启动使用节能显卡 带可见窗口的程序不在范围内 占用率取三轮采样最小值 每个程序仅登记一次", "Added automatic power-saving GPU for background apps still running 3D work on the game's render GPU, applied at their next launch. Apps with a visible window are out of scope, utilization takes the minimum of three samples, and each app is enrolled only once." },
                new[]{ "新增 日志页运行日志记录开关 关闭后不再写入新记录 已有内容保留可查看可清空 崩溃转储不受影响", "Added a run-log switch on the Log page: when off, no new entries are written; existing content is kept, viewable and clearable, and crash dumps are unaffected." },
                new[]{ "改进 功耗让路 无法读取功耗的笔记本降级使用平台频率验证 两级验证分别记账 验证通过后持续监测瓶颈 瓶颈移回处理器即归还预算 显卡再次饱和可重新让出 一局最多三次", "Improved power yield: laptops without readable package power fall back to platform-frequency verification with separate accounting, and a passed verification now keeps monitoring the bottleneck — the budget is returned when it moves back to the processor and can be yielded again, up to three times per match." },
                new[]{ "改进 对局中断观测降本 仅订阅 DPC 事件量约减半 证据饱和且无待验收钉核后每四局观测一局 重启或拓扑变化自动恢复全量", "Improved match interrupt observation cost: a DPC-only subscription roughly halves event volume, and once evidence is saturated with no pins awaiting verification, observation runs one match in four, resuming in full after a restart or topology change." },
                new[]{ "修复 功耗让路验证窗恰逢过场或加载时可能被误判为无收益而停止尝试 现按负载漂移判定为无法判定 退回后下局重试", "Fixed power yield stopping attempts when its verification window landed on a cutscene or loading screen; heavy load shifts are now judged indeterminate, reverting without stopping and retrying next match." },
                new[]{ "修复 清除全部配置可能被无法确认归属的恢复记录无限期拦住 此类记录现按仅移除记录结清并保留系统现状 其余还原失败仍保留恢复记录等待重试", "Fixed Clear All Configuration being blocked indefinitely by recovery records whose ownership cannot be confirmed; such records are now settled as forget-record-only with the system state kept, while other restoration failures still preserve their recovery records for retry." },
                new[]{ "修复 游戏提优日志把反作弊拒写和磁盘 IO 优先级未被接受误记为提优失败 现分别按预期结果与提示记录", "Fixed the game-boost log recording anti-cheat write refusals and unaccepted disk I/O priority as boost failures; they are now logged as expected results and notices respectively." },
                new[]{ "修复 启动欢迎窗不显示在任务栏 被其它窗口遮住后无法找回", "Fixed the startup welcome window missing from the taskbar and becoming unreachable when covered by other windows." },
                new[]{ "调整 反作弊压制移除温和均衡强力三档 构成固定为扫描安全 低于正常优先级加极低磁盘 IO 加小核限频 不压最低优先级不封定时器不降内存页 扫描期间游戏线程被挂起 压得过狠会把短暂卡顿拉长为卡死", "Adjusted: anti-cheat suppression drops its Gentle/Balanced/Strong tiers for one fixed scan-safe profile — below-normal priority, very low disk I/O and efficiency-core capping, never the lowest priority, sealed timers or lowered page priority. Game threads are suspended during a scan; starving the scanner stretches a brief stutter into a freeze." },
                new[]{ "调整 深度调优更名为高级", "Renamed Deep Tuning to Advanced." },
                new[]{ "调整 体检结论区仅保留需要处理的项 全部通过时明示无需处理", "Adjusted the audit verdicts section to keep only items that need action, stating plainly when nothing does." },
                new[]{ "调整 界面文案全面修订 去除口语与自解释表述 统一术语", "Revised the interface copy throughout, removing colloquial and self-justifying wording and unifying terminology." },
                new[]{ "修正 高分屏顶栏控件边框对齐 反作弊卡片说明截断 模式选择描述截断等界面细节", "Corrected interface details including top-bar border alignment on high-DPI displays and truncated descriptions on anti-cheat cards and the mode picker." },
                new[]{ "说明 以上标注实验的功能默认关闭 开启前需确认 每一步写入均回读核实 退局或关闭后还原 建议逐项开启并在同一场景下对照评估", "Note: the features marked experimental above are off by default and require confirmation before enabling; every write is read back and verified, and changes are restored at match end or on disable. Enable them one at a time and evaluate each against a comparable scene." },
            }),
            new ReleaseNote("2.1.3.1", "2026-08-28", new[]
            {
                new[]{ "修复 压制家族后台时新出现的同游戏大厅及辅助进程可能被可见窗口规则误豁免 按当前进程身份补齐家族 白名单 游戏渲染本体及原有安全保护不变", "Fixed newly started game clients and helpers bypassing family suppression through visible-window protection. Family membership now uses current process identities; the whitelist, game renderer and existing safety protections remain unchanged.", "修正 関連プロセスの抑制中、新しく起動した同じゲームのロビーや補助プロセスがウィンドウ保護で除外される問題。現在のプロセス識別情報で関連範囲を補い、ホワイトリスト、ゲーム描画プロセス、既存の安全保護は維持します。" },
                new[]{ "调整 录屏与覆盖层保护复用进程快照 不再读取游戏模块 Steam及网页和覆盖层辅助进程遵守逐游戏家族压制选择 独立录屏与用户白名单保护保留", "Changed capture and overlay protection to reuse process snapshots without reading game modules. Steam and its web/overlay helpers honor each game's family-suppression choice; independent recording and user whitelist protection remain.", "変更 録画・オーバーレイ保護は既存のプロセス一覧を利用し、ゲームのモジュールを読み取りません。Steam とウェブ・オーバーレイ補助プロセスはゲーム別の関連プロセス抑制設定に従い、独立した録画ツールとホワイトリストの保護は維持します。" },
                new[]{ "新增 待机内存清理按双阈值规则工作 列表大小与真正空闲内存同时达到阈值才清理 参数可配置 默认关闭 不裁剪游戏或后台工作集", "Added an optional standby memory cleaner using dual-threshold rules. Both list size and true free memory must meet configured thresholds. Off by default; game and background working sets are not trimmed.", "追加 二重しきい値ルールに基づくスタンバイメモリ整理。リスト量と実際の空きメモリの両条件を満たした場合だけ整理し、閾値を設定できます。既定はオフで、ゲームやバックグラウンドのワーキングセットは削減しません。" },
                new[]{ "新增 进入游戏切英文一次 默认关闭 支持逐游戏覆盖 用户随后切中文不再干预 缺少英文布局或游戏拒绝时跳过 退出不恢复输入法", "Added one English input request at game entry, off by default with per-game overrides. Later manual input changes are left alone. Missing English layouts or rejected requests are skipped; input is not restored on exit.", "追加 ゲーム開始時に一度だけ英語入力を要求する機能。既定はオフでゲーム別設定に対応します。その後の手動変更には干渉せず、英語配列がない場合や拒否時はスキップし、終了時も入力方法を戻しません。" },
                new[]{ "新增 显卡页加入 Intel 页签 提供游戏期间的全局基础低延迟策略 默认关闭 开启前确认作用范围 仅在驱动明确支持时生效", "Added an Intel graphics tab with optional global basic low latency during games. Off by default, with a scope warning before enabling; available only when the driver reports support.", "追加 Intel タブとゲーム中のグローバルな基本低遅延ポリシー。既定はオフで、有効化前に作用範囲を確認します。ドライバーが対応を報告する場合のみ利用できます。" },
                new[]{ "新增 显卡通用页的应用显卡偏好 可管理后台应用的节能显卡选择 下次启动应用生效 不迁移或重启正在运行的程序 移除时保留用户外部修改", "Added application GPU preferences under Graphics / Common. Managed apps can prefer the power-saving GPU on their next launch; running apps are not moved or restarted. Removal preserves detected external changes.", "追加 「グラフィックス / 共通」にアプリごとの GPU 設定。省電力 GPU の選択は次回起動時に反映され、実行中のアプリは移動・再起動しません。登録解除時は検出した外部変更を維持します。" },
                new[]{ "恢复 禁止 CPU 空闲策略 放在会话附加 默认关闭 开启必须确认 仅用于交流供电下的 Pavise 托管方案 退局或拔电还原本次修改", "Restored the Disable CPU Idle policy under Session Extras. Off by default and requires confirmation; limited to Pavise-managed plans on AC power. Session changes are restored when the game ends or AC is disconnected.", "復帰 セッション追加機能の CPU アイドル無効化ポリシー。既定はオフで確認が必要です。AC 電源で Pavise 管理プランを使う場合に限定し、ゲーム終了時や AC 切断時に今回の変更を戻します。" },
                new[]{ "新增 游戏期间暂停打印 索引等非必要系统服务策略 默认关闭 只恢复本工具本次暂停的服务 用户原本停用的服务保持不动", "Added an optional policy to pause nonessential services such as printing and indexing during games. Off by default; only services paused by Pavise are restored. Services already stopped or disabled are left alone.", "追加 ゲーム中に印刷・検索インデックスなどの不要なサービスを一時停止する任意ポリシー。既定はオフで、Pavise が停止したサービスのみ復元します。元から停止・無効化されているサービスは変更しません。" },
                new[]{ "改进 添加游戏自动合并安装记录与正在运行的程序 保留程序图标 统一列表滚动条并完善导航和页面内容过渡", "Improved Add Game by merging installed and running programs into one list with application icons. Unified list scrollbars and refined navigation and page-content transitions.", "改善 ゲーム追加でインストール情報と実行中のプログラムをアイコン付きの一覧に統合しました。リストのスクロールバーを統一し、ナビゲーションとページ内容の切り替えを調整しました。" },
                new[]{ "修复 AMD 显卡 显卡偏好预写入和部分系统策略恢复失败后漏掉重试 有恢复记录时不再误报清理完成 合法待命预写入仍保持有效", "Fixed skipped recovery retries for AMD settings, GPU preference staging and several session policies. Remaining recovery records are no longer reported as a completed cleanup; valid standby staging is preserved.", "修正 AMD 設定・GPU 設定の事前適用・一部のセッションポリシーで復元の再試行が抜ける問題。復元記録が残る場合は完了と報告せず、有効な待機中の事前適用は維持します。" },
                new[]{ "修复 CPU 空闲记录区分准备与确认写入 电源计划恢复前核对归属 不确定结果保留原值 本次运行已恢复后只重试清账 不再重复切换", "Fixed CPU idle records to distinguish preparation from confirmed writes. Power-plan recovery checks ownership and retains unverified originals. Once recovery is confirmed within a run, cleanup retries do not switch plans again.", "修正 CPU アイドルの記録で準備と確認済み書き込みを区別します。電源プランの帰属を確認し、不明な結果では元の記録を保持します。同じ起動中に復元を確認した後は、記録の消去だけを再試行し、プランを再切り替えしません。" },
                new[]{ "修复 全局和逐游戏手动选择全部核心后仍继承限核的问题 界面显示与实际核心策略保持一致", "Fixed manual All Cores selections still inheriting core restrictions in global and per-game settings. Displayed selection and effective policy now agree.", "修正 全体設定とゲーム別設定で手動の全コア選択後もコア制限が引き継がれる問題。表示と実際のコアポリシーを一致させました。" },
                new[]{ "修复 功耗让路验证停止后开关被锁住 已开启项目始终可以关闭 满足条件时可由用户明确重新开启验证 全局和逐游戏行为一致", "Fixed Power Yield controls becoming locked after validation stops the feature. An enabled option can be switched off, and eligible users can explicitly enable it again to retry, in both global and per-game settings.", "修正 電力配分の検証停止後にスイッチが操作できなくなる問題。有効な項目はオフにでき、条件を満たす場合はユーザーの明示的な再有効化で再検証できます。全体・ゲーム別設定で動作を統一しました。" },
                new[]{ "修复 辅助键只完成部分修改时恢复入口消失的问题 以实际恢复记录显示状态 仍有待恢复项目时保持可操作", "Fixed the recovery control disappearing after only some accessibility-key changes succeeded. Its state now follows the actual recovery records and remains available while restoration is pending.", "修正 アクセシビリティキーの変更が一部だけ成功した際に復元操作が消える問題。実際の復元記録に基づいて状態を表示し、未復元項目がある間は操作を維持します。" },
                new[]{ "修复 显存保护配置保存失败时仍解除验证停止状态的问题 开启失败保留原状态 取消确认不改变现有选择", "Fixed VRAM protection clearing its validation stop state after a failed settings save. Failed enable requests preserve the previous state, and cancelling confirmation keeps the current selection.", "修正 VRAM 保護の設定保存に失敗しても検証停止状態が解除される問題。有効化に失敗した場合は元の状態を保ち、確認のキャンセルでも現在の選択を維持します。" },
                new[]{ "修复 显卡能力检测失败或不支持时已开启选项无法关闭的问题 保留真实选择与关闭入口 不允许借此启用不支持的档位", "Fixed enabled graphics options becoming impossible to disable when capability checks fail or report no support. Current selections and the off action remain available without allowing unsupported modes to be enabled.", "修正 GPU 機能の検出失敗や非対応時に、有効な項目をオフにできなくなる問題。現在の選択とオフ操作を維持し、非対応モードの有効化は許可しません。" },
                new[]{ "修复 设置页与白名单迟到的异步刷新覆盖新状态的问题 保留用户刚完成的操作与最新列表 页面重建后不再应用旧回调", "Fixed late asynchronous refreshes overwriting newer startup-task UI state and whitelist contents. Recent user actions and list updates are preserved, and stale callbacks are ignored after page replacement.", "修正 遅れて完了した非同期更新が、新しい自動起動の表示やホワイトリストを上書きする問題。直近の操作と最新の一覧を維持し、ページ再作成後の古いコールバックは無視します。" },
                new[]{ "修复 旧服务与传递优化暂停在恢复记录未确认时仍继续执行或恢复失败后误报完成的问题 停服务前验证记录 未确认归属的旧记录保留核查", "Fixed legacy service and Delivery Optimization pauses proceeding without verified recovery records or reporting completion after failed recovery. Records are verified before stopping services; uncertain legacy records are retained without automatically starting services.", "修正 旧サービス停止処理と配信最適化の停止が、復元記録未確認のまま実行されたり、復元失敗後に完了と報告されたりする問題。停止前に記録を確認し、帰属不明の旧記録はサービスを自動起動せず保持します。" },
                new[]{ "移除 后备提优 IFEO 和逐游戏关闭 CFG 的开关与主动写入 不做旧字段兼容 含已移除逐游戏字段的旧游戏库按现有流程自动还原清理 弹窗并退出 清理失败保留恢复记录", "Removed fallback boost (IFEO) and per-game CFG disabling, including controls and active writes. No retired-field compatibility is added. Libraries with retired per-game fields follow the existing restoration, automatic cleanup, dialog and exit flow; failed cleanup retains recovery records.", "削除 フォールバック昇格（IFEO）とゲームごとの CFG 無効化の操作項目および書き込み処理。旧項目の互換処理は追加せず、該当するゲーム別項目を含む旧ライブラリは既存の復元・自動消去・ダイアログ表示・終了の処理を行います。失敗時は復元記録を保持します。" },
            }),
            new ReleaseNote("2.1.3.0", "2026-08-28", new[]
            {
                new[]{ "调整 反作弊管控总开关和所有分组默认关闭 包括 ACE 已保存的手动选择保持不变", "Changed anti-cheat control to be off by default for the master switch and every group, including ACE. Existing saved choices are preserved.", "変更 アンチチート制御のメインスイッチと ACE を含むすべてのグループを初期状態でオフにしました。保存済みの設定は維持されます。" },
                new[]{ "改进 深度调优改为主窗口内的常驻分类导航 可直接切换五个功能区 记住上次分类和浏览位置 返回不再强制跳概览", "Improved Deep Tuning with persistent in-window category navigation, direct switching between five sections, remembered section/view positions, and return to the previous main page instead of always Overview." },
                new[]{ "修复 不限核或未开启游戏提优时设备中断观测没有记录 现在独立记录系统中断 不把未知游戏核域冒充挪核证据", "Fixed missing interrupt records with unrestricted cores or game boosting off. System interrupts are now captured independently, without treating an unknown game core domain as relocation evidence." },
                new[]{ "修复 首局无建议不刷新中断页 采样失败 丢事件和账本读取失败现在说明原因", "Fixed the interrupt page not refreshing after a first match without suggestions. Capture failures, lost events and ledger read errors now show their cause." },
                new[]{ "调整 中断实测展示不再受一分钟建议门槛限制 核域验证不能完成时有限等待后转系统观测 每局采集失败原因写入该局结束记录", "Changed interrupt measurements to remain visible below the one-minute advice threshold. Unconfirmed core placement falls back to system observation after bounded initialization; capture failures are included in that match's end record." },
                new[]{ "修复 PRESENT 订阅在进入缓冲前仅保留目标事件 且只随已验证核域的中断采样启停", "Fixed PRESENT subscription to filter the target event before buffering and run only with a verified-core interrupt capture." },
                new[]{ "移除 对局禁用处理器空闲 开关与功能一并下架 托管方案写回允许空闲", "Removed the disable-processor-idle option entirely; the managed power plan is written back to allow idle." },
                new[]{ "新增 轻载模式 适合掌机 轻薄本等带电池设备 后台管控与专注一致 无电池设备不可选", "Added Light mode for battery-powered handhelds and thin-and-light laptops, with the same background control as Focus. Unavailable when no battery is detected." },
                new[]{ "新增 掌机厂商的整机管理软件加入免压名单", "Added handheld vendor system management apps to the never-suppressed list." },
                new[]{ "新增 显存驻留 实验功能 开关在优化策略页 默认关闭 显存吃紧时给游戏声明一份最低显存预留 减少纹理被挤出显存再调回造成的长帧 退局撤销", "Added video memory residency as an experimental feature, switched on from the Optimization Strategy page and off by default. When VRAM runs tight it declares a minimum reservation for the game to reduce long frames caused by textures being evicted and paged back, revoked when the match ends." },
                new[]{ "说明 显存驻留不增加也不锁定显存 用系统自带接口 不装驱动不注入 写入后回读核实 不通过即撤销并不再尝试", "Note: it neither adds nor locks video memory. It uses Windows' own interface with no driver and no injection, and every write is read back to verify — if verification fails it is revoked with no further attempts." },
                new[]{ "新增 自动入库 开关在游戏库页 默认关闭 开启后前台全屏且 GPU 3D 占用主导的陌生程序自动加入游戏库 不必手动添加", "Added automatic library addition, switched on from the Game Library page and off by default. Unknown programs running fullscreen in the foreground while dominating GPU 3D usage join the library on their own, with no manual adding." },
                new[]{ "说明 自动入库只在没有对局时判断 启动器 更新器 反作弊和游戏平台不会被认成游戏 从库里移除过的程序永久不再自动加入 重新手动添加即解除", "Note: auto add only evaluates when no match is running, and launchers, updaters, anti-cheat and game platforms are never mistaken for a game. Anything you remove from the library is never auto-added again unless you add it back by hand." },
                new[]{ "移除 设备中断页的一键体检与自造内存带宽负载 避免整机严重卡顿 中断结论只采用真实对局观测", "Removed the Device Interrupts one-click checkup and its synthetic memory-bandwidth load to avoid severe system stalls; interrupt conclusions now use only real in-match observations." },
                new[]{ "调整 高级设置更名为深度调优 打开时会提示该页面面向熟悉系统调优的用户 可勾选不再提示", "Changed Advanced Settings to Deep Tuning. Opening it now warns that the page is intended for users familiar with system tuning, with a Don't show again option." },
                new[]{ "修复 游戏库保存失败与手动清除统一安全重置 先确认后台任务停止并还原系统改动 还原未完成时保留恢复记录 不再直接强删数据", "Fixed library save failures and manual cleanup to share a safe reset: confirm background tasks have stopped and restore system changes first. Pending recovery records are preserved instead of force-deleting data." },
                new[]{ "修复 文件占用或注册表清理失败不再误报成功 清理后阻止迟到任务重建配置与日志 便携版保留程序和无关文件", "Fixed locked files and registry cleanup failures being reported as successful resets. Late callbacks cannot recreate cleared configuration or logs; portable executables and unrelated files are preserved." },
                new[]{ "修复 A 卡机器打开显卡页 整页横幅报红说未检测到 NVIDIA 驱动本项停用 而 AMD 那一栏其实可用 现在两家任意一家可用即为就绪", "Fixed the Graphics page banner turning red with \"no NVIDIA driver detected\" on AMD machines even though the AMD tab was fully usable. The page now reports ready whenever either vendor's driver interface is present." },
                new[]{ "新增 概览页底部三个入口 教程与故障排查 问卷调查 Bug 反馈 点击用浏览器打开", "Added three entries at the bottom of the Overview page — guide and fixes, survey, and bug report — each opening in your browser." },
                new[]{ "修复 概览页最近一局始终显示暂无数据 那两行从来没有接上数据源 现在每局结束写入游戏名 时长与压制进程数 重启后仍然保留 短于一分钟的对局不计入", "Fixed \"Last session\" on the Overview page always reading \"No data yet\" — those two lines were never wired to a data source. It now records the game, duration and suppressed process count at the end of each match and survives a restart; matches under a minute are not counted." },
                new[]{ "移除 对局冻结 程序不再具备挂起进程的能力 压制只到隔离为止", "Removed match freeze. Pavise no longer has any ability to suspend processes; suppression stops at isolation." },
                new[]{ "调整 游戏家族豁免默认开启 平台客户端 启动器 游戏目录进程和游戏子进程在对局中不再压制 手动关闭后只放行游戏本体与白名单", "Changed game family exemption to be on by default. Platform clients, launchers, game-folder processes and game child processes are left alone during matches; turn it off to exempt only the game itself and your whitelist." },
                new[]{ "说明 关闭游戏家族豁免后降优先级也不等于结束进程 把客户端当作授权校验的平台不受影响 反作弊 网游加速器 输入音频与外设链 硬件控制工具 系统核心服务始终不碰", "Note: with game family exemption off, lowering priority still does not terminate a process, so platforms that use their client for entitlement checks remain functional. Anti-cheat, network accelerators, the input/audio/peripheral chain, hardware control tools and core system services are never touched." },
                new[]{ "调整 白名单页的平台提示跟随游戏家族豁免开关 开启时无需手动加入 关闭后可按需加入白名单", "Changed the platform notice on the Whitelist page to follow the game family exemption switch: no manual entry is needed while it is on; after turning it off, add platforms to the whitelist as needed." },
                new[]{ "调整 反作弊页总开关与相容名单收成一条工具条 不再与下方分组同款卡片 编号也不再重复", "Adjusted the anti-cheat page so the master switch and compatibility roster share one compact bar instead of cards identical to the groups below, which also removes the duplicated numbering." },
                new[]{ "移除 反作弊目录中的内核驱动名 该目录只收用户态进程 驱动写在其中永远扫不到", "Removed kernel driver names from the anti-cheat catalog; it only lists user-mode processes, and drivers listed there could never be matched." },
            }),
            new ReleaseNote("2.1.0.0", "2026-08-24", new[]
            {
                new[]{ "新增 AMD 显卡优化回归 Anti-Lag 流体运动帧与 RSR 驱动级升格 退出对局还原", "Added AMD GPU optimizations back: Anti-Lag, Fluid Motion Frames and RSR driver-level upscaling, restored when the match ends." },
                new[]{ "新增 AMD 两项驱动优化支持逐游戏独立配置 与 NVIDIA 一致", "Added per-game profile support for the two AMD driver options, matching NVIDIA." },
                new[]{ "新增 显卡功耗墙重新支持 AMD 显卡", "Added AMD GPU support back to the GPU power limit lift." },
                new[]{ "修复 部分锐龙机型读不到处理器功耗 导致功耗让路无法启用", "Fixed processor power readings failing on some Ryzen machines, which kept the power-yield feature from enabling." },
                new[]{ "调整 设置页清除全部配置时把整个数据目录一并删除 不再只删已知文件", "Adjusted the Settings-page full wipe to delete the entire data directory instead of only known files." },
                new[]{ "修复 概览页状态行两段文字连在一起 且过长时被截断", "Fixed the overview status line running two segments together and getting truncated when long." },
                new[]{ "修复 后台隔离不再关闭 Windows 动态优先级提升 游戏等待被隔离进程回应时偶发约一秒的卡顿已消除", "Fixed background isolation no longer disabling Windows dynamic priority boosts; the occasional one-second stall while the game waited on an isolated process is gone." },
                new[]{ "新增 模块已注入游戏进程的覆盖层宿主自动豁免后台压制 开局点名后即时生效", "Added automatic suppression exemption for overlay hosts whose modules are injected into the game process, effective as soon as the post-launch scan names them." },
                new[]{ "修复 开机自启任务的保护设置补齐从未成功 每次启动都误报需重新开关 现在补齐可以落地并附带失败原因", "Fixed the startup task protection settings never being successfully rebuilt, causing a false re-enable prompt at every launch; the rebuild now lands and failures carry the actual reason." },
                new[]{ "调整 中断挪核重启回来后主动提示还差打一局验证 对局观测未开时会点破打局也不会记录", "Adjusted IRQ relocation to announce after the reboot that one match is still needed for verification, and to call out that playing records nothing while in-match observation is off." },
            }),
            new ReleaseNote("2.0", "2026-08-24", new[]
            {
                new[]{ "移除 后台绑核收缩与后台移核 后台不再被限定核心", "Removed background core shrinking and background core relocation; background work is no longer confined to specific cores." },
                new[]{ "移除 后台压制的省电与克制两档 以及等待热度确认的观测", "Removed the eco and restrained background tiers along with the wait for measured pressure." },
                new[]{ "调整 对局一开始就直接隔离全部后台 不再等它们先抢资源", "Adjusted background suppression to isolate everything from the moment a match starts, instead of waiting for contention." },
                new[]{ "调整 对局中的后台扫描改由进程变动驱动 新出现的进程被压制得更快", "Adjusted in-match background scanning to be driven by process-set changes, so newly appearing processes are suppressed sooner." },
                new[]{ "调整 笔记本用电池时专注档不再照搬插电的电源参数 只放开纯省电项", "Adjusted the Focus tier on laptop battery to stop copying the plugged-in power settings, relaxing only the pure power-saving items." },
                new[]{ "调整 笔记本专注档不再强制所有核心不停泊", "Adjusted the Focus tier on laptops to stop forcing every core unparked." },
                new[]{ "新增 笔记本插电打专注档时把电源滑块拨到最佳性能 退出对局还原", "Added switching the power slider to Best Performance for Focus-tier matches on laptop AC power, restored when the match ends." },
                new[]{ "新增 体检可读出处理器封装与核心功耗 不需要安装驱动", "Added processor package and core power readings to the checkup, with no driver install required." },
                new[]{ "新增 笔记本对局中把共享功耗预算从 CPU 让给显卡 默认关闭 实验项", "Added an experimental, off-by-default option that yields shared power budget from the CPU to the GPU during laptop matches." },
                new[]{ "移除 独立配置页的套用到其它游戏按钮", "Removed the apply-to-other-games button from the per-game profile page." },
                new[]{ "移除 界面动画期间提升全局系统计时器精度", "Removed the global system timer precision boost during UI animation." },
                new[]{ "修复 无超线程的混合架构上显卡中断被投到低频能效核", "Fixed GPU interrupts being placed on low-clock efficiency cores on hybrid CPUs without hyper-threading." },
                new[]{ "修复 换了文件名的另一个 Pavise 构建会被当作后台进程压制", "Fixed another Pavise build with a different file name being suppressed as a background process." },
            }),
            new ReleaseNote("1.9.8.9", "2026-08-22", new[]
            {
                new[]{ "新增 游戏进程发现快速通道 游戏启动后更快被识别与接管", "Added a fast path for game process discovery; games are recognized and taken over sooner after launch." },
                new[]{ "新增 待命阶段提前写入 NVIDIA 逐游戏调优 本次启动即可生效 不再等下一局", "Added pre-staging of NVIDIA per-game tuning while on standby, so it takes effect on this launch instead of the next." },
                new[]{ "新增 双显卡机器待命阶段预选高性能显卡 避免游戏偶发跑核显 退场还原", "Added standby pre-selection of the high-performance GPU on dual-GPU machines, preventing games from occasionally launching on the integrated GPU; restored when the session ends." },
                new[]{ "新增 系统环境新增全局计时器分辨率实验开关 重启后生效", "Added an experimental global timer resolution switch under System Environment; takes effect after a reboot." },
                new[]{ "新增 体检检测 DirectStorage 直通是否被阻断 并点名阻断的驱动", "Added a checkup item that detects whether DirectStorage bypass reads are blocked and names the blocking driver." },
                new[]{ "新增 对局中观测 CPU 性能限制 撞功率墙或温度墙会写明程度与时长", "Added in-match CPU performance limit observation; hitting a power or thermal wall is reported with its depth and duration." },
                new[]{ "新增 开局后扫描注入游戏进程的覆盖层并在日志点名", "Added a post-launch scan that names overlay injections found in the game process in the log." },
                new[]{ "新增 体检说明内存通道布局 单通道与非对称混合会被指出", "Added a checkup note on memory channel layout; single-channel and asymmetric mixes are called out." },
                new[]{ "新增 中断选核弹窗可选把该设备的中断优先级提到 High 撤销时一并还原", "Added an option in the interrupt core picker to raise the device's interrupt priority to High, restored together with the revert." },
                new[]{ "调整 托盘右键菜单精简为守护开关 开机启动与退出 其余功能双击图标进面板", "Adjusted the tray menu down to the guard switch, autostart and exit; everything else lives in the panel, opened by double-clicking the icon." },
                new[]{ "新增 系统环境内核调度新增计时器恒定节拍开关 重启后生效", "Added a constant timer tick switch under System Environment kernel scheduling; takes effect after a reboot." },
                new[]{ "移除 体检页平台时钟校正项 时钟相关改动统一归系统环境的计时器开关管理", "Removed the platform timer correction item from the checkup page; clock-related changes are now managed by the timer switch under System Environment." },
                new[]{ "移除 AMD 显卡相关优化与体检项 显卡设置交还 AMD 软件本身", "Removed AMD GPU optimizations and audit items; GPU settings are handed back to AMD's own software." },
                new[]{ "移除 禁止 Ansel 注入游戏 残留由设置页清除按钮还原", "Removed blocking Ansel injection into games; residue is restored via the cleanup button on the Settings page." },
                new[]{ "移除 全部旧版本数据迁移与残留清理逻辑 程序不再自动改动任何历史配置", "Removed all legacy data migration and residue cleanup logic; the app no longer touches historical configuration on its own." },
                new[]{ "移除 专注档资源牢笼 后台压制不再使用 Job 硬性限速", "Removed the Focus resource cage; background suppression no longer uses hard Job rate caps." },
                new[]{ "修复 驱动调优遇驱动临时忙碌时本局不再重试", "Fixed driver tuning never retrying within the session after hitting a temporarily busy driver." },
                new[]{ "修复 对局在电源方案切换完成前结束时 机器可能停留在调优方案上", "Fixed the machine possibly staying on the tuned power plan when a match ended before the plan switch finished." },
                new[]{ "调整 MMCSS 多媒体调度不再写入两项系统并不读取的参数", "Adjusted MMCSS multimedia scheduling to stop writing two parameters the system does not read." },
                new[]{ "新增 体检给出建议目标核心 不适合改动的设备会说明原因", "Added suggested target cores from the checkup, with a stated reason for every device it advises against changing." },
                new[]{ "新增 显卡驱动容器与硬件功耗散热控制工具自动豁免后台压制", "Added automatic suppression exemption for graphics driver containers and hardware power/thermal control utilities." },
                new[]{ "调整 恢复智能保帧 CPU 持续吃满十秒且帧线程未接管时游戏本体让回普通优先级 旧版一秒即降的过敏已修掉", "Adjusted: the frame guard is back - when the CPU stays saturated for ten seconds with no render-thread takeover, the game process yields to normal priority; the old one-second hair trigger is gone." },
                new[]{ "修复 笔记本对局中核心停泊未解除 智能档插电只放开一半核心", "Fixed core parking staying active in-match on laptops; the Smart tier only unparked half the cores on AC power." },
                new[]{ "修复 反作弊相容名单的游戏仍被写入显卡调度优先级 名单语义恢复为完全零接触", "Fixed roster-protected anti-cheat games still receiving GPU scheduling priority writes; the roster once again means zero contact." },
                new[]{ "调整 帧线程识别开局数秒内完成 并定期复核 钉错的线程会被自动解除并重新识别", "Adjusted frame thread identification to complete within seconds of a match starting, with periodic re-checks that automatically unpin and re-identify a wrongly pinned thread." },
                new[]{ "修复 游戏退出后仍显示待命 WeGame 等平台常驻进程不再触发就绪", "Fixed the standby label lingering after a game exits; resident platform processes such as WeGame no longer count as a ready signal." },
                new[]{ "修复 游戏平台装在自定义目录时豁免失效 卸载记录缺少安装位置也能认出", "Fixed platform exemptions failing for custom install locations; platforms are now recognized even when their uninstall entry lacks an install path." },
                new[]{ "修复 网卡在中断页整行空白 现在按设备类归并框架驱动的数据", "Fixed network adapters showing an empty row on the Interrupts page; framework driver data now folds into the matching device class." },
                new[]{ "修复 中断观测丢事件时仍采信数据 现在整轮作废", "Fixed interrupt observation trusting data from a round that dropped events; such a round is now discarded." },
                new[]{ "修复 智能预设首轮不压制任何进程 一局下来收益接近于零", "Fixed the Smart preset suppressing nothing on its first sweep, leaving a match with almost no gain." },
                new[]{ "修复 电源计划调优值超出本机取值范围时整项放弃 现在夹到范围内再写", "Fixed a power plan value being abandoned when it fell outside this machine's allowed range; it is now clamped into range and written." },
                new[]{ "修复 电源计划写入失败只说失败不说原因 现在给出实际错误码", "Fixed power plan write failures reporting no reason; the actual error code is now shown." },
                new[]{ "修复 电源计划一项写入失败就整轮判失败 两轮后开关会被自动关掉", "Fixed a single failed power plan write failing the whole round, which switched the toggle off after two rounds." },
                new[]{ "修复 已经是目标值的项目仍被写入并认领 退出时又写回一次", "Fixed items already at the target value still being written and claimed, then written back again on exit." },
                new[]{ "修复 窗口化游戏优化 硬件加速 游戏模式在系统已开启时被重复写入", "Fixed windowed game optimization, hardware acceleration, and Game Mode being written again when the system already had them on." },
                new[]{ "修复 VBS 项原本不存在时被写成 auto 而不是删除", "Fixed the VBS entry being written as auto instead of deleted when it did not exist originally." },
                new[]{ "修复 NVIDIA 未知驱动键被误当成帧率上限处理", "Fixed unknown NVIDIA driver keys being treated as the frame rate cap." },
                new[]{ "修复 反作弊进程打不开时崩溃台账被提前清掉", "Fixed the crash ledger being cleared early when an anti-cheat process could not be opened." },
                new[]{ "修复 环境项启用失败时先关开关后还原 可能留下残留", "Fixed an environment item failing to enable turning its switch off before restoring, which could leave residue." },
                new[]{ "修复 开机自启刷新失败时提示不实 现在给出实际错误码", "Fixed the misleading message when refreshing the startup task failed; the actual error code is now shown." },
                new[]{ "调整 挪核弹窗不再预选核心 移除使用推荐值按钮", "Adjusted the pin dialog: no cores are preselected and the recommended-value button is gone." },
                new[]{ "调整 中断页改为显示 p99 区间 不再显示单次最长", "Adjusted the Interrupts page to show a p99 band instead of the single longest run." },
                new[]{ "调整 多队列网卡与存储控制器在挪核前给出风险提示", "Adjusted multi-queue network adapters and storage controllers to warn before pinning." },
                new[]{ "调整 一键恢复改为只保留重置 不再还原历史版本写过的项", "Adjusted one-click restore down to reset only; items written by older versions are no longer restored." },
                new[]{ "移除 三代已退役的中断亲和空壳代码", "Removed the shell code of three retired generations of interrupt affinity switches." },
                new[]{ "移除 游戏内帧率小窗 取帧要常驻内核会话 部分机器直接失败 另一些每局中止 拿不到数据还照付开销", "Removed the in-game frame rate overlay: capturing frames needed a resident kernel session that failed outright on some machines and aborted every match on others, costing overhead with no data to show." },
                new[]{ "新增 体检页检出容错堆 可一键解除 可还原", "Added a fault-tolerant heap check to the Checkup page, removable in one click and reversible." },
                new[]{ "移除 内存吃紧时清理待机内存 实测触发条件无法自解除 每 45 秒重复且单次只释放 0.1MB", "Removed purging standby memory when memory runs low: the trigger condition could never clear itself, so it repeated every 45 seconds while releasing 0.1MB per run." },
                new[]{ "调整 后台压制只压真抢资源的进程 闲置进程智能不碰 专注只降省电档", "Adjusted: background suppression now targets only processes actually taking resources; Smart leaves idle ones alone, Focus only drops them to eco." },
                new[]{ "修复 对局让位过早把优化器自己饿死 首轮清扫极慢 重负载下升不了档", "Fixed: yielding too early starved the optimizer itself, making the first sweep crawl and heavy load impossible to escalate." },
                new[]{ "修复 电源方案下发不再卡主循环", "Fixed: power plan enforcement no longer blocks the main loop." },
                new[]{ "修复 中断页重启后仍显示待重启 新增待验证状态 区分没重启和没观测到中断", "Fixed: the Interrupts page still showed pending-reboot after a reboot. A new unverified state separates not-yet-rebooted from not-yet-observed." },
                new[]{ "调整 重压后台绑核收缩的门槛从 7 个物理核降回 5 个 此前被关闭的请到设置页自行打开", "Adjusted: the core-squeeze threshold drops from 7 physical cores back to 5; if an earlier build turned it off, re-enable it on the Settings page." },
                new[]{ "修复 一键清除时中断亲和的还原顺序反了 会把两份备份一起打光", "Fixed: the interrupt affinity restore order during a one-click cleanup was backwards and could spend both backups at once." },
                new[]{ "修复 中断页钉设备前没查退役台账 可能把残留值当成原值", "Fixed: pinning a device did not check the retired ledgers, so a leftover value could be recorded as the original." },
                new[]{ "调整 中断页排版 按钮并到顶部 设备列表撑满剩余高度", "Adjusted the Interrupts page layout: buttons on one top row, the device list fills the remaining height." },
                new[]{ "调整 显卡驱动接口不可用时日志写明原因 快照留到下次启动继续还原", "Adjusted: when the graphics driver interface is unavailable the log says so, and snapshots are kept for the next startup to retry." },
                new[]{ "修复 启动时会把调好的设置整套还原 有一项还原失败就每次开机重来一遍", "Fixed: startup could restore every applied setting at once, and one failed step made it repeat on every launch." },
                new[]{ "调整 启动与版本更新不再改动机器上任何已经生效的值 还原只由设置页那颗清除按钮触发", "Adjusted: startup and version upgrades no longer change any value already in effect on the machine; restoring happens only via the cleanup button on the Settings page." },
                new[]{ "调整 NVIDIA 逐游戏驱动写入改为对局结束即还原 驱动里不留常驻值 手动改过的键不覆盖", "Adjusted: per-game NVIDIA driver writes now restore when the match ends, leaving no resident values in the driver; manually changed keys are never overwritten." },
                new[]{ "修复 新驱动已移除超低延迟键时不再每局报错 自动降级为低延迟 换驱动后自动重试", "Fixed: when the driver no longer has the Ultra Low Latency keys, no more errors every match; it downgrades to low latency and retries after a driver change." },
                new[]{ "新增 对局禁用处理器空闲改为可选开关 默认关闭 只在专注档插电时生效 升级时重置为关闭", "Added: disabling processor idle during a match is back as an opt-in switch, off by default, Focus on AC only, reset to off on upgrade." },
                new[]{ "移除 前台时间片的三档选择 两个写入值在内核里行为相同 判据交给体检页", "Removed the three-way foreground time slice picker; the two written values behaved identically, judgment moves to the Checkup page." },
                new[]{ "调整 前台时间片判据只看微软有文档的低两位 修复也只改这两位 其余位保留", "Adjusted: the foreground time-slice check and fix now touch only the two documented bits; all other bits are preserved." },
                new[]{ "调整 体检里这一项的说明只讲机制不讲收益", "Adjusted: this checkup item's wording describes the mechanism only, never a benefit." },
                new[]{ "新增 中断页 列出所有可改中断亲和的设备 可扫描测量并钉到指定核 全程可撤销", "Added an Interrupts page: every pinnable device listed, scan on demand, pin to chosen cores, fully reversible." },
                new[]{ "重要 中断亲和写入后需重启才生效 已写入与已生效分开显示", "Important: interrupt affinity takes effect after a reboot; written and effective are shown separately." },
                new[]{ "新增 中断页给出判读微秒数的尺子 每台设备标出分档与超时次数", "Added a yardstick for the microsecond numbers, with a grade and over-threshold count per device." },
                new[]{ "新增 每台设备可单独选中断落到哪几个核 预选按处理器架构给出推荐值", "Added a per-device core picker with an architecture-based pre-selection." },
                new[]{ "调整 中断页交互重做 扫一次 挑一台 钉住三步 表格七列减到四列", "Adjusted: the Interrupts page reworked into scan, pick, pin, with the table cut from seven columns to four." },
                new[]{ "新增 可能带着键鼠的设备会被标出来 钉住主控会把整条总线的输入中断一起挪走", "Added a marker for devices that may carry your keyboard and mouse; pinning the controller moves the whole bus." },
                new[]{ "修复 中断亲和的可撤销性 改成先记名单再动手 中途崩溃也能还原", "Fixed interrupt affinity reversibility: the ledger is written before the registry, so a crash can always be undone." },
                new[]{ "移除 体检页的中断分布与中断负载两项 判据交给中断页", "Removed the interrupt distribution and load rows from the Checkup page; the Interrupts page judges instead." },
                new[]{ "移除 环境页的显卡中断亲和开关 并入中断页 旧值升级时自动还原", "Removed the Environment page GPU interrupt affinity switch; it lives on the Interrupts page now, old values restored on upgrade." },
                new[]{ "移除 帧卡顿溯源整维下架 设置与残留升级后自动清除", "Removed frame stutter attribution in full; settings and residue are cleared on upgrade." },
                new[]{ "移除 对局禁用处理器空闲状态 该项彻底下架 旧残留升级后自动清除", "Removed disabling processor idle during a match for good; residue is cleared on upgrade." },
                new[]{ "调整 源码主体注释全部移除 每个文件只保留作者与文件用途两行", "Adjusted: all in-body source comments were removed; each file keeps only its author and purpose header." },
                new[]{ "重要 升级到本版会清除此前所有旧版本数据 游戏库 白名单 配置与设置一并重置 请重新添加游戏 全新安装不受影响", "Important: upgrading to this version wipes all data from any earlier version — game library, whitelist, configuration and settings are all reset, so you'll need to re-add your games. Fresh installs are unaffected." },
                new[]{ "调整 智能档后台压制重做 按每个进程自己的热度逐级升档 前台家族不动", "Adjusted: Smart suppression rebuilt to escalate per-process by heat; the foreground family is never touched." },
                new[]{ "新增 帧线程接管后本体让位开关", "Added a switch for the game process to yield once the frame thread is boosted." },
                new[]{ "调整 被反作弊拒绝写入的游戏只跳过必然失败的项 其余照常尝试", "Adjusted: anti-cheat-protected games now skip only the writes certain to fail." },
                new[]{ "新增 MMCSS 多媒体调度重新上线 关闭或退出即还原 需管理员权限", "Added MMCSS multimedia scheduling back; restored on switch-off or exit, requires admin." },
                new[]{ "新增 前台时间片可选前台加权 三档自选", "Added a foreground-weighted option for the foreground time slice with three choices." },
                new[]{ "新增 对局禁用处理器空闲重新上线 默认关闭 专注档插电时生效 低于睿频基线自动关掉", "Added disabling processor idle back, off by default, Focus on AC only, auto-off if turbo falls below baseline." },
                new[]{ "调整 智能档在台式机上把处理器能效偏好写回最偏性能 并关掉插电时的时钟占空比调制 瞬时升频比此前积极", "Adjusted: on desktops the Smart preset writes the processor energy-performance preference back to the most performance-biased value and turns off clock duty cycling on AC power, so it ramps up more eagerly than before." },
                new[]{ "新增 专注档资源牢笼 持续重负载的后台硬限到系统 CPU 的一成 意外退出自动解除", "Added the Focus resource cage: sustained heavy background hard-capped at ten percent CPU, lifted automatically on unexpected exit." },
                new[]{ "调整 重压后台绑核收缩在 6 核及以下机器上强制关闭并置灰", "Adjusted: core squeezing is forced off and greyed out on machines with 6 or fewer physical cores." },
                new[]{ "调整 设置页语言选择改为中文与 English 同时显示 点击目标语言直接切换 不再用单按钮来回轮换", "Adjusted: the Settings language selector now shows 中文 and English at the same time. Select the target language directly instead of cycling with a single button." },
                new[]{ "优化 通用弹窗顶部 TAG 增加上下留白 并统一顺延标题 正文与内容区 不再与标题贴得过紧", "Improved: the shared dialog TAG now has balanced spacing above and below, with the title, body, and content area shifted consistently so the header no longer feels cramped." },
                new[]{ "优化 白名单中只有进程名而没有 EXE 路径的内置保护项改用统一的盾牌进程图标 不再留下空白图标位", "Improved: built-in whitelist protections that have only a process name and no EXE path now use a unified shield-and-process icon instead of leaving the icon slot blank." },
                new[]{ "新增 白名单页直接展示四类自动豁免规则 并标出本机检测到的平台", "Added: the Whitelist page exposes the four automatic exemption categories and detected platforms." },
                new[]{ "新增 系统环境页可按游戏本体关闭控制流保护 CFG 只改该缓解位保留其它设置 下次启动游戏生效 关闭即逐个还原", "Added: the System Environment page can disable Control Flow Guard per game executable; it touches only that mitigation bit and preserves other settings, takes effect on the next game launch, and restores each one when turned off." },
                new[]{ "移除驱动级帧率上限 NVIDIA 与 AMD 一并下架 升级后自动还原驱动原值", "Removed the driver-level frame rate cap for both NVIDIA and AMD; original driver values are restored automatically on upgrade." },
                new[]{ "修复 对局核心解停泊覆盖在还原路径死循环 该覆盖与托管方案重复 已移除", "Fixed the in-match core-unparking override looping on restore; it duplicated the managed plan and was removed." },
                new[]{ "移除 USB 与硬盘控制器中断亲和 升级自动还原到系统默认", "Removed USB and storage controller interrupt affinity; restored to system default on upgrade." },
                new[]{ "移除 竞技档在台式机上禁用处理器空闲状态 托管方案恒不禁 idle 升级后自动还原", "Removed the competitive preset's disabling of C-states on desktops; the managed plan never disables idle, restored on upgrade." },
                new[]{ "修复 对局中切换模式或改全局设置要退出游戏才生效", "Fixed mode or global setting changes during a match not applying until the game closed." },
                new[]{ "移除 逐游戏强制独显偏好 已写过的升级时还原", "Removed the per-game forced discrete-GPU preference; written values restored on upgrade." },
                new[]{ "改名 常规模式更名为智能 竞技模式更名为专注 行为不变", "Renamed Standard to Smart and Competitive to Focus; behavior unchanged." },
                new[]{ "调整 专注档不再放行前台窗口族 只有白名单例外", "Adjusted: Focus no longer exempts the foreground window family; only the whitelist is exempt." },
                new[]{ "AMD Anti-Lag 不再被帧率上限互斥抑制 开关即生效", "AMD Anti-Lag is no longer suppressed by the frame cap conflict; the switch now always takes effect." },
                new[]{ "修复新装用户重启后配置被清空", "Fixed configuration being wiped after a restart on fresh installs." },
                new[]{ "修复进程状态记账残留把清除配置永久卡死", "Fixed leftover process state bookkeeping permanently blocking the configuration wipe." },
                new[]{ "修复恢复全部系统改动只还原运行态 持久改动一并还原", "Fixed Restore All System Changes only reverting runtime state; persistent changes are now restored too." },
                new[]{ "修复 X3D 双缓存平台对局中被解除核心停泊 手动指定频率核时才解除", "Fixed core parking being lifted on X3D dual-cache platforms during matches; now only lifted when the frequency CCD is chosen manually." },
                new[]{ "修复硬件加速 GPU 调度能力探测在所有机器上失败 不支持的机器现在置灰", "Fixed hardware-accelerated GPU scheduling capability detection failing on every machine; unsupported machines are now greyed out." },
                new[]{ "修复电源方案 功耗墙 驱动调优在用户手动改动后被强行写回", "Fixed the power plan, power limit and driver tuning overwriting values the user changed manually." },
                new[]{ "修复显卡 MSI 中断一键修复无确认 可能改坏刻意关闭的机器", "Fixed the GPU MSI interrupt one-click fix applying without confirmation on machines where it was disabled on purpose." },
                new[]{ "修复开机自启任务运行 72 小时后被任务计划强制结束", "Fixed the startup task being force-terminated by Task Scheduler after 72 hours." },
                new[]{ "调整中断亲和按核心布局选核 少性能核机型改投能效核", "Adjusted interrupt affinity core selection by topology; machines with few performance cores now target efficiency cores." },
                new[]{ "调整逐游戏 Game DVR 开关下架 全局开关不变", "Removed the per-game Game DVR switch; the global switch is unchanged." },
            }),

            new ReleaseNote("1.8.0.2", "2026-08-15", new[]
            {
                new[]{ "新增英文界面 设置页一键切换", "Added an English UI with one-click switching in Settings." },
                new[]{ "新增竞技档插电时禁用处理器闲置 电池上不启用", "Added: Competitive tier disables processor idle when plugged in; not on battery." },
                new[]{ "新增体检页顶部一键修复总按钮", "Added a master one-click fix button at the top of the Checkup page." },
                new[]{ "修复自定义主题色后启动即崩", "Fixed a startup crash after setting a custom mode accent color." },
                new[]{ "修复穿越火线等 TP/内核反作弊游戏对局中掉帧卡死", "Fixed frame drops and freezes in TP/kernel anti-cheat games such as CrossFire." },
                new[]{ "修复升级清数据在部分机器上无限中止", "Fixed the upgrade data purge aborting endlessly on some machines." },
                new[]{ "修复对局电源方案在退出后偶发滞留", "Fixed the match power plan occasionally lingering after exit." },
                new[]{ "修复 svchost 等后台进程被误判为外设而豁免压制", "Fixed background processes like svchost being misidentified as peripherals and exempted from suppression." },
                new[]{ "移除英雄联盟专栏", "Removed the League of Legends Spotlight." },
                new[]{ "移除极限模式 竞技即最高档", "Removed Extreme mode; Competitive is the top tier." },
                new[]{ "移除暂停搜索索引与预读服务 旧版本停掉的自动恢复", "Removed search-indexing/prefetch service pausing; services stopped by older versions are restored automatically." },
                new[]{ "移除电源滑块最佳性能 已切换过的自动还原", "Removed the Best Performance power slider setting; machines already switched are restored automatically." },
                new[]{ "移除服务让路与后台上传让位", "Removed service yielding and background upload yielding." },
                new[]{ "调整常规模式压制升压提速 重负载后台更快隔离", "Adjusted Standard mode to escalate suppression faster on heavy background load." },
                new[]{ "调整 Game DVR 关闭为游戏模式期间常驻 退出还原", "Adjusted Game DVR disabling to stay applied while game mode is on, restored on exit." },
                new[]{ "调整电源计划为设一次 不再每 30 秒强制拉回", "Adjusted power plan to set once instead of forcing it back every 30 seconds." },
                new[]{ "调整待机内存清理为内存吃紧时触发 默认开启", "Adjusted standby memory cleanup to trigger only when memory is tight; on by default." },
                new[]{ "调整 AMD 帧率上限走 Radeon Chill 老卡自动回退", "Adjusted AMD frame rate cap to use Radeon Chill, falling back automatically on older cards." },
                new[]{ "本版配置结构大改 升级首次启动清空旧版本全部数据并还原系统改动", "Configuration overhaul: first launch after upgrading wipes all old data and restores system changes." },
            }),
            new ReleaseNote("1.8.0.1", "2026-08-14", new[]
            {
                new[]{ "因为呼声过高 游戏专栏回归 目前仅有英雄联盟专栏", "By popular demand, game Spotlight is back. Currently only a League of Legends Spotlight." },
                new[]{ "新增 AMD 显卡调优", "Added AMD GPU tuning" },
                new[]{ "新增逐游戏独立配置 未覆盖跟随全局 生效值在对局激活瞬间定格 对局中的任何修改含自定义核心都是下一局生效", "Added per-game profiles. Games without one follow global settings. Effective values are frozen the moment a match activates; any change during a match, including custom cores, takes effect next match." },
                new[]{ "概览页和托盘标注实际生效模式来自哪个游戏", "Overview page and tray now show which game the actual active mode comes from" },
                new[]{ "游戏库条目支持重命名 英雄联盟条目带专栏小标", "Game library entries can be renamed; the League of Legends entry carries a Spotlight badge" },
                new[]{ "右上角搜索覆盖全部页面含独立配置二级页 命中直达定位 覆盖计数可点击逐项跳转", "Top-right search now covers every page, including per-game profile sub-pages. Hits jump straight to their location, and the coverage count is clickable to step through items one by one." },
                new[]{ "体检页新增一键修复 MSI 中断 前台时间片 平台时钟 网络限流 键鼠队列检出异常直接修 修完自动复检 可还原", "Checkup page adds one-click fix: detected anomalies in MSI interrupts, foreground time slice, platform clock, network throttling, and keyboard/mouse queues are fixed directly, auto-rechecked afterward, and reversible." },
                new[]{ "驱动级帧率上限改为滑块 30 到 500 滚轮逐帧微调 NVIDIA 与 AMD 同步", "Driver-level frame rate cap is now a slider from 30 to 500, with per-frame mouse-wheel fine-tuning. NVIDIA and AMD in sync." },
                new[]{ "对局电源计划默认 PG 托管方案 首次对局自动创建 可在策略页选回自己的计划", "Match power plan defaults to the PG managed plan, created automatically on the first match. You can switch back to your own plan on the strategy page." },
                new[]{ "窗口化游戏优化已修复 目前也是回归", "Windowed game optimization is fixed; this too is a returning feature." },
                new[]{ "游戏档案升到 V5 驱动写入连续失败自动关开关时该游戏的对应覆盖一并清除", "Game profiles upgraded to V5. When consecutive driver write failures auto-disable a switch, that game's corresponding overrides are cleared as well." },
                new[]{ "修复清除数据时 NVIDIA 驱动项恢复失败仍删快照 现在任何一项还原失败整体中止", "Fixed data clearing still deleting the snapshot when restoring NVIDIA driver items failed; now any single restore failure aborts the whole operation." },
                new[]{ "修复关闭驱动调优失败后无人重试 启动时自动补还原残留快照", "Fixed nothing retrying after disabling driver tuning failed; leftover snapshots are now restored automatically at startup." },
                new[]{ "修复关闭 VBS 失败残留快照 之后清数据会改写用户自己设置的 hypervisor 启动项", "Fixed a failed VBS disable leaving a stale snapshot, which made a later data clear overwrite the user's own hypervisor boot setting." },
                new[]{ "修复便携标志丢失时数据被搬进本机并删源 现在只复制不删除不覆盖", "Fixed data being moved into the local machine with the source deleted when the portable flag was lost; now it only copies, never deletes or overwrites." },
                new[]{ "修复档案首行损坏被当空库 下次保存覆盖掉可恢复内容 现在只读保护并备份", "Fixed a corrupted first line in the profile file being treated as an empty library, letting the next save overwrite recoverable content; now it becomes read-only protected and backed up." },
                new[]{ "修复扫描 Program Files 这类目录会把整个目录当成一款游戏入库", "Fixed scanning directories like Program Files adding the entire directory to the library as one game" },
                new[]{ "修复体检 MSI 统计到已拔掉的历史显卡 只统计在场设备", "Fixed checkup MSI stats counting historical GPUs that were already unplugged; only present devices are counted now" },
                new[]{ "修复 59Hz 与 60Hz 同一物理模式被判为刷新率损失", "Fixed 59Hz and 60Hz of the same physical mode being flagged as a refresh rate loss" },
                new[]{ "修复对局中增删白名单和关闭上传限速会卡住界面 改为后台执行", "Fixed the UI freezing when adding/removing whitelist entries or disabling upload throttling mid-match; now runs in the background" },
                new[]{ "修复联系方式弹窗打开期间空转一个核心", "Fixed one core spinning idle while the contact dialog was open" },
                new[]{ "修复 GPU 降频探测在驱动重启后永久失效 体检中断会话在程序被杀后残留", "Fixed the GPU throttling probe permanently breaking after a driver restart, and checkup interrupt sessions lingering after the app was killed" },
                new[]{ "修复若干位图泄漏 亮暗切换会退出独立配置页 托盘菜单不随缩放调整", "Fixed several bitmap leaks, light/dark switching exiting the per-game profile page, and the tray menu not adjusting with scaling" },
                new[]{ "移除 旧版本兼容 V4 及更早档案不再读取 旧档案只读保护不覆写", "Removed legacy compatibility: V4 and earlier archives are no longer read and stay read-only." },
                new[]{ "移除两个开关 禁止前台无输入降级与逐游戏强制独显改为始终开启", "Removed two switches: prevent-foreground-no-input-demotion and per-game forced discrete GPU are now always on." },
                new[]{ "移除环境页五个修复型开关 对应能力并入体检页一键修复", "Removed five fix-type switches from the environment page; their capabilities merged into the checkup page's one-click fix." },
                new[]{ "移除体检页 NVIDIA 写入实测和鼠标回报率实测", "Removed the checkup page's NVIDIA write test and mouse polling rate test" },
            }),
            new ReleaseNote("1.7.1", "2026-08-13", new[]
            {
                new[]{ "新增关闭指针精度增强 默认关 关掉系统那条鼠标加速曲线 立即生效 可还原", "Added disabling Enhance Pointer Precision, off by default. Turns off the system's mouse acceleration curve. Takes effect immediately; reversible." },
                new[]{ "新增辅助功能按键拦截 清掉筛选键 粘滞键 切换键 键鼠里唯一能救回两位数毫秒的软件项 可还原", "Added removal of accessibility key interception: clears Filter Keys, Sticky Keys, and Toggle Keys — the only software item in the keyboard/mouse chain that can win back double-digit milliseconds. Reversible." },
                new[]{ "新增禁止键鼠设备选择性暂停 治空闲一会儿之后第一下操作发飘 只碰键鼠 不碰 U 盘声卡 可还原", "Added blocking selective suspend for keyboard/mouse devices, curing the floaty first input after a short idle. Touches only keyboard and mouse — not USB drives or sound cards. Reversible." },
                new[]{ "新增键鼠队列校正 把被第三方工具改过的队列长度改回系统默认", "Added keyboard/mouse queue correction: queue lengths changed by third-party tools are set back to system defaults" },
                new[]{ "USB 中断避让改成开启前先弹窗说明代价 只有 4K 以上回报率的鼠标值得开 1000Hz 及以下基本没收益", "USB interrupt avoidance now shows a dialog explaining the cost before enabling. Only mice with 4K+ polling rates are worth it; 1000Hz and below gains basically nothing." },
                new[]{ "USB 中断避让新增开机自检 换过 CPU 或在 BIOS 改过核心数 失效的掩码会自动还原", "USB interrupt avoidance adds a boot-time self-check: if you swapped the CPU or changed core counts in BIOS, invalidated masks are restored automatically." },
                new[]{ "体检新增键鼠链路整段 鼠标回报率实测 延迟量级说明 蓝牙键鼠会被点名 只读不改", "Checkup adds a full keyboard/mouse chain section: measured mouse polling rate, latency magnitude notes, and Bluetooth keyboards/mice called out by name. Read-only, changes nothing." },
                new[]{ "新增重压后台绑核收缩 默认开 治后台抢内存带宽 台架实测 33 帧到 95 帧", "Added heavy-suppression background core shrinking, on by default. Cures background processes stealing memory bandwidth; bench-verified 33 fps to 95 fps." },
                new[]{ "收缩落点跟着你划的游戏核实时算 多 CCD 会整个躲开游戏核那块 L3 这项不需要你设置", "The shrink target is computed in real time from the game cores you assigned. On multi-CCD chips it steers clear of the entire L3 block holding the game cores. Nothing for you to configure." },
                new[]{ "新增对局稳定后回收后台工作集 默认关 代价是切回那些程序头一下会顿", "Added reclaiming background working sets once the match stabilizes, off by default. The cost: those programs stutter the first moment you switch back to them." },
                new[]{ "新增待机列表超阈值时清空 默认关", "Added purging the standby list when it exceeds a threshold, off by default." },
                new[]{ "对局前清理低优先级待机内存重新上线 默认关 内存三项不再被极限模式强制", "Pre-match low-priority standby memory cleanup is back online, off by default. The three memory items are no longer forced by Extreme mode." },
                new[]{ "对局电源计划默认改成卓越性能 本机没有会自动建一份 有就直接用你那份", "Match power plan now defaults to Ultimate Performance. Created automatically if this machine doesn't have it; if it does, yours is used directly." },
                new[]{ "托管方案降成下拉最后一项 改名 PG 竞技 带本机签名 一键还原会连它删掉", "The managed plan is demoted to the last item in the dropdown, renamed PG Competitive with a local-machine signature. One-click restore deletes it as well." },
                new[]{ "升级时旧的 Pavise 竞技方案自动删除 正在用它会先切走再删", "The old Pavise Competitive plan is deleted automatically on upgrade; if it's in use, we switch away first, then delete." },
                new[]{ "检查更新改成九条线路并发赛跑 直连不通自动落到 ghproxy 和 jsDelivr 镜像", "Check-for-updates now races nine routes concurrently. If direct connection fails, it falls back to the ghproxy and jsDelivr mirrors." },
                new[]{ "下载地址跟着通的那条线路走 新增官方网盘入口 只允许打开可信域名", "The download URL follows whichever route got through. Added an official cloud drive entry; only trusted domains are allowed to open." },
                new[]{ "游戏库新增强制接管 模拟器 云游戏这类识别不到的 进程一起来就进对局 开启前会说明代价", "Game library adds forced takeover for emulators, cloud gaming, and other unrecognizable titles: the moment the process starts, a match begins. The cost is explained before enabling." },
                new[]{ "添加游戏改成实时 每 3 秒自动补上新开的程序 扫描和 GPU 采样各阶段都有进度提示", "Add-game is now live: newly launched programs are picked up automatically every 3 seconds, with progress hints through the scanning and GPU sampling stages." },
                new[]{ "核心分配手动档补了操作说明 已划核数挪到矩阵上方 新增一行重压后台落点", "Manual core allocation gains usage notes, the assigned-core count moved above the matrix, and a new row shows where heavily suppressed background lands." },
                new[]{ "策略页被预设锁住的开关写明是强制开还是强制关 暂停索引和预取的说明原来写反了", "Switches locked by a preset on the strategy page now state whether they're forced on or forced off; the descriptions for pausing indexing and prefetch had been swapped." },
                new[]{ "打开主界面不再自动弹窗 版本说明和反馈交流都改成到关于页手动打开", "Opening the main window no longer auto-pops dialogs; release notes and feedback chat are now opened manually from the About page." },
                new[]{ "修复大小核机型的后台压制等于没做 现在挤核叠在核心分区之上", "Fixed background suppression amounting to nothing on hybrid (P/E-core) machines; core squeezing now stacks on top of core partitioning." },
                new[]{ "修复挤核会挤到你划给游戏的核上 现在只在后台能待的区域里收缩", "Fixed core squeezing pushing background onto the cores you assigned to the game; it now shrinks only within the region where background is allowed to stay." },
                new[]{ "修复后台档位在门槛上反复抖 一局 135 次降到 4 次", "Fixed the background tier flapping back and forth at the threshold: from 135 flips per match down to 4." },
                new[]{ "修复开局最要紧的十秒被停服务占住 停服务挪到对局稳定 20 秒后", "Fixed the crucial first ten seconds of a match being occupied by stopping services; service stops now wait until 20 seconds after the match stabilizes." },
                new[]{ "修复服务让路在核心不多的机器上从来没生效 暂停索引预取与它互相抵消", "Fixed service yielding never taking effect on machines with few cores; pausing indexing/prefetch was cancelling it out." },
                new[]{ "修复常规档在电池上会打开 USB 选择性暂停 现在四档都不打开它", "Fixed the Standard tier enabling USB selective suspend on battery; none of the four tiers enable it now." },
                new[]{ "修复核心矩阵三处看不清 后台落点标不出来 无超线程的大小核不分带 快捷选核出现重复按钮", "Fixed three legibility issues in the core matrix: background landing spots couldn't be marked, hybrid chips without hyper-threading had no band separation, and quick core-select showed duplicate buttons." },
                new[]{ "修复版本说明弹窗打开时卡顿 改成整卡自绘 控件从 293 个降到 17 个", "Fixed stutter when opening the release notes dialog; it's now a fully self-drawn card, down from 293 controls to 17." },
                new[]{ "对局中托盘提示的刷新从 1.5 秒放宽到 6 秒", "In-match tray tooltip refresh relaxed from 1.5 seconds to 6 seconds" },
                new[]{ "移除窗口化游戏优化 交回系统设置里由你自己决定 升级后自动还原", "Removed windowed game optimization, handing it back to system settings for you to decide. Auto-restored after upgrade." },
                new[]{ "移除后台硬限帧 升级后自动还原", "Removed background hard frame cap. Auto-restored after upgrade." },
                new[]{ "移除游戏文件预热", "Removed game file preheating" },
            }),
            new ReleaseNote("1.7.0.6", "2026-08-11", new[]
            {
                new[]{ "新增核心分配 全核 只用大核 手动三选一 手动可以逐个逻辑核挑 只在对局中下发 退出自动还原", "Added core allocation: all cores, P-cores only, or manual — pick one. Manual lets you choose logical cores individually. Applied only during a match, auto-restored on exit." },
                new[]{ "修复上传让位一直没生效 写的是旧版策略格式 现在的系统不认 升级后才真的开始限速", "Fixed upload yielding never having worked: it wrote the legacy policy format that current systems don't recognize. Throttling only truly starts after this upgrade." },
                new[]{ "修复 Steam 覆盖层被当成普通后台压制 平台豁免改成按安装目录识别", "Fixed the Steam overlay being suppressed like an ordinary background process; platform exemption now identifies by install directory." },
                new[]{ "移除对局内存驻留", "Removed in-match memory pinning" },
                new[]{ "移除对局前清理待机内存", "Removed pre-match standby memory cleanup" },
                new[]{ "移除游戏时降级桌面视觉效果 升级后自动还原", "Removed downgrading desktop visual effects while gaming. Auto-restored after upgrade." },
                new[]{ "日志改成全中文 不再输出内部代号和十六进制掩码", "Logs are now entirely in Chinese; internal codenames and hex masks are no longer printed" },
            }),
            new ReleaseNote("1.7.0.5", "2026-08-11", new[]
            {
                new[]{ "移除 后台赶去集显 升级后自动还原写过的显卡偏好", "Removed pushing background apps to the iGPU; written GPU preferences restored on upgrade." },
                new[]{ "移除 禁用 MPO 排查开关 升级后自动还原 Pavise 写过的值", "Removed the MPO-disable switch; values Pavise wrote are restored on upgrade." },
            }),
            new ReleaseNote("1.7.0.4", "2026-08-11", new[]
            {
                new[]{ "游戏核心范围可以选绑哪块 双 CCD 机型两块任选 切换即时生效", "The game core range can now pick its CCD on dual-CCD machines, effective immediately." },
                new[]{ "电源计划改成一个下拉选完 用托管方案或你自己的计划", "The power plan is now one dropdown: the managed plan or your own." },
                new[]{ "优化策略页按 核心控制 预设细节 会话附加 分成三个标签 不再一列滚到底 设置搜索直达时自动切到对应标签", "Optimization strategy page is split into three tabs — Core Control, Preset Details, Session Extras — no more scrolling one long column. Settings search jumps auto-switch to the matching tab." },
                new[]{ "设置页新增清除全部配置 升级后遇到卡顿掉帧可以试试 把配置环境全部删除", "Settings page adds Clear All Configuration. If you hit stutter or frame drops after upgrading, try it: it deletes the entire configuration environment." },
                new[]{ "移除后台限速", "Removed background CPU throttling" },
                new[]{ "移除全屏游戏自动入库", "Removed automatic library addition of fullscreen games" },
                new[]{ "移除刷新率守护", "Removed refresh rate guardian" },
                new[]{ "移除自定义重压时清空后台内存", "Removed clearing background memory on custom heavy suppression" },
            }),
            new ReleaseNote("1.7.0.3", "2026-08-10", new[]
            {
                new[]{ "标题栏新增设置搜索 跨页面匹配 点击直达", "Title bar adds settings search: matches across pages, click to jump straight there" },
                new[]{ "移除禁用 CPU 空闲状态 升级后电源计划自动写回空闲允许", "Removed disabling CPU idle states; the power plan is auto-rewritten to allow idle after upgrade" },
                new[]{ "移除硬盘中断避让 升级后自动还原写入的值", "Removed disk interrupt avoidance; written values auto-restored after upgrade" },
                new[]{ "收到反馈 极限模式 不如 竞技模式 此版本进行了紧急修正", "Feedback came in that Extreme mode was worse than Competitive mode; this release ships an emergency correction." },
            }),
            new ReleaseNote("1.7.0.2", "2026-08-08", new[]
            {
                new[]{ "修复 对局中和挂机后按键鼠标可能变卡", "Fixed keys and mouse turning sluggish during or after a match." },
                new[]{ "游戏退出后如果有设置没还原成功 不再每 20 秒刷一条日志 改成写明卡在哪一项 静默重试", "If some settings fail to restore after a game exits, no more log line every 20 seconds; it now states exactly which item is stuck and retries silently." },
                new[]{ "新增 极限模式 在竞技之上把 GPU 让位和后台策略一次拉到位", "Added Extreme mode: GPU yield and background policy all-in above Competitive." },
                new[]{ "新增 后台限速 被强力压制的后台整体限到 5% CPU 极限模式强制开启", "Added a background cap at 5% CPU for strongly suppressed processes, forced on in Extreme." },
                new[]{ "竞技电源计划按 CPU 类别分方案 日志写明用的哪套", "The competitive power plan now varies by CPU class; the log names the set in use." },
                new[]{ "新增 后台赶去集显 双显卡本把被压制后台的显卡程序改走集显 默认关", "Added pushing suppressed background GPU apps to the iGPU on dual-GPU laptops, off by default." },
                new[]{ "核心分区改成独立手动选择 全核和单 CCD 随时切换", "Core partitioning is now a standalone manual choice between all cores and one CCD." },
                new[]{ "新增游戏文件预热 默认关闭 对局开始后把游戏文件悄悄读进缓存 加载和切图更顺 内存不足自动放弃", "Added game file preheating, off by default: after a match starts, game files are quietly read into cache for smoother loading and map transitions. Backs off automatically when memory is low." },
                new[]{ "新增对局内存驻留 默认关闭 内存紧张时把游戏内存钉住不被换出 内存充裕时不动作 退出解除", "Added in-match memory pinning, off by default: when memory is tight, game memory is pinned so it can't be paged out. Does nothing when memory is plentiful; released on exit." },
                new[]{ "新增后台上传让位 默认关闭 对局时限制被压制后台的上行带宽 网盘同步和 P2P 做种不再拖高延迟", "Added background upload yielding, off by default: limits the upstream bandwidth of suppressed background during a match, so cloud drive sync and P2P seeding no longer drive latency up." },
                new[]{ "系统环境页新增前台时间片校正 被老优化教程改过的时间片值修回系统默认 正常的不动", "System environment page adds foreground time-slice correction: time-slice values altered by old optimization guides are set back to system defaults; normal ones are left alone." },
                new[]{ "PUBG 塔科夫这类被反作弊挡住提优的游戏 概览页现在直接指路开 后备提优 不再只写进日志", "For games like PUBG and Tarkov where anti-cheat blocks the boost, the overview page now points you straight to enabling fallback boost instead of only writing it to the log." },
                new[]{ "大改体验页 自行查看", "Major overhaul of the experience page — see for yourself" },
                new[]{ "添加游戏支持直接从正在运行的程序里选 和 白名单一样", "Add-game now supports picking directly from running programs, same as the whitelist" },
            }),
            new ReleaseNote("1.7.0", "2026-08-08", new[]
            {
                new[]{ "智能保帧上线 默认开启 单独抬高决定帧数的那个线程", "Smart frame guard is live by default, boosting the frame-deciding thread alone." },
                new[]{ "后台 GPU 让位重新上线 后台吃满显卡时中位帧时间减半 尾部帧改善约四成", "Background GPU yielding is back online: when background saturates the GPU, median frame time halves and 1% lows improve about 40%." },
                new[]{ "移除 AMD 驱动调优 部分 A 卡有副作用 升级后自动还原相关驱动设置", "Removed AMD driver tuning: some AMD cards had side effects. Related driver settings are auto-restored after upgrade." },
                new[]{ "新增 QQ 交流二群 1101249532", "Added second QQ chat group: 1101249532" },
            }),
            new ReleaseNote("1.6.8.1", "2026-08-07", new[]
            {
                new[]{ "保帧线程重新上线 开关在策略页 沿用你之前的设置", "The frame-guard thread is back; the switch is on the Policy page with your previous setting kept." },
                new[]{ "竞技模式禁用 CPU 空闲状态重新上线 不适用的平台自动置灰", "Disabling CPU idle states in Competitive is back, greyed out on unsuitable platforms." },
                new[]{ "暂停索引和预取服务重新上线 机械盘建议开 SSD 不用", "Pausing indexing and prefetch services is back online: recommended for HDDs, unnecessary on SSDs" },
                new[]{ "这三项启动时不再被自动清理", "These three items are no longer auto-cleaned at startup" },
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

        public static bool HasUnseen
        {
            get { return !string.Equals(Settings.LoadStr(SeenKey, ""), App.Version, StringComparison.OrdinalIgnoreCase); }
        }

        public static void MarkSeen() { Settings.SaveStr(SeenKey, App.Version); }

#if PAVISE_SELFTEST
        // 历史条目是双语时代写的 不回填 所以只查两件事
        //   每条至少有中英两列 声明了第三列的不许留空
        //   新版本必须三语齐全那条由当前版本的用例单独守 别在这里比版本号
        internal static List<string> MissingTranslations()
        {
            var bad = new List<string>();
            foreach (ReleaseNote n in All)
                for (int i = 0; i < n.Count; i++)
                {
                    int declared = n.RawLanguages(i);
                    int required = declared >= 3 ? 3 : 2;
                    for (int lang = 0; lang < required; lang++)
                        if (string.IsNullOrEmpty(n.RawItem(i, lang)))
                            bad.Add(n.Version + " #" + i + " lang" + lang);
                }
            return bad;
        }
#endif
    }
}
