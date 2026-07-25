// @author bdth 2074055628@qq.com
// 文件用途 集中维护界面多语言文本

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class Lang
    {
        public static int Cur;

        private static readonly Dictionary<string, string[]> M = new Dictionary<string, string[]>
        {
            { "nav.game", new[]{ "游戏模式", "Game Mode", "ゲームモード" } },
            { "nav.tame", new[]{ "反作弊", "Anti-Cheat", "アンチチート" } },
            { "nav.white", new[]{ "白名单", "Whitelist", "ホワイトリスト" } },
            { "nav.log", new[]{ "日志", "Log", "ログ" } },
            { "nav.set", new[]{ "设置", "Settings", "設定" } },
            { "nav.about", new[]{ "关于", "About", "情報" } },
            { "nav.overview", new[]{ "概览", "Overview", "概要" } },
            { "nav.library", new[]{ "游戏库", "Game Library", "ゲームライブラリ" } },
            { "nav.policy", new[]{ "优化策略", "Optimization", "最適化ポリシー" } },
            { "nav.reports", new[]{ "报告", "Reports", "レポート" } },
            { "v15.about.sub", new[]{ "版本、开源仓库与更新通道集中在这里。", "Version, source repository, and update channel in one place.", "バージョン、ソースリポジトリ、更新経路をここに集約。" } },
            { "v15.about.identity", new[]{ "单文件 Windows 性能守护", "SINGLE-FILE WINDOWS PERFORMANCE GUARDIAN", "単一ファイル Windows パフォーマンスガード" } },
            { "notes.title", new[]{ "版本说明", "Release notes", "リリースノート" } },
            { "notes.sub", new[]{ "当前安装版本 {0}，以下说明随程序内置，离线也能查看。", "Currently installed: {0}. These notes ship with the app and work offline.", "現在のインストール版：{0}。このノートは本体に同梱され、オフラインでも閲覧できます。" } },
            { "notes.current", new[]{ "当前版本", "CURRENT", "現行バージョン" } },
            { "notes.open", new[]{ "查看版本说明", "View release notes", "リリースノートを見る" } },
            { "notes.online", new[]{ "在 GitHub 查看全部", "View all on GitHub", "GitHub ですべて見る" } },
            { "notes.close", new[]{ "关闭", "Close", "閉じる" } },
            { "v15.about.update", new[]{ "版本更新", "VERSION UPDATE", "バージョン更新" } },
            { "v15.about.update.sub", new[]{ "只读取 GitHub Releases 版本号，不上传任何设备数据。", "Reads only the GitHub Releases version; no device data is uploaded.", "GitHub Releases の版番号のみ取得し、端末情報は送信しません。" } },
            { "v15.restore.title", new[]{ "恢复已记录调度", "RESTORE RECORDED SCHEDULING", "記録済み調度を復元" } },
            { "v15.restore.desc", new[]{ "撤销已记录的压制、提优、电源、网络与通知改动；Power Throttling 会交还系统管理；游戏仍在运行时策略可能再次接管。", "Undo recorded suppression, boost, power, network, and notification changes; Power Throttling returns to system management; an active game may re-engage policy.", "記録済みの抑制、提優、電源、ネット、通知を復元し Power Throttling はシステム管理へ戻します。ゲーム実行中は再適用される場合があります。" } },
            { "mode.source.global", new[]{ "全局默认", "Global default", "全体既定" } },
            { "mode.global.title", new[]{ "全局默认模式", "Global default mode", "全体の既定モード" } },
            { "mode.global.sub", new[]{ "唯一全局入口 · 所有游戏统一使用", "The single global entry for every game", "全ゲーム共通の唯一の設定入口" } },
            { "mode.policy.active", new[]{ "当前实际行为 · {0}", "Effective behavior · {0}", "現在の実動作・{0}" } },
            { "mode.tray.current", new[]{ "当前模式：{0}（打开面板切换）", "Current mode: {0} (open panel to change)", "現在のモード：{0}（パネルで変更）" } },
            { "mode.pick.standard", new[]{ "兼容优先 · 后台先 Eco，持续大户再升级", "Compatibility first · Eco, then escalate sustained contention", "互換性優先・Eco から継続競合のみ強化" } },
            { "mode.pick.competitive", new[]{ "无用户窗口后台立即核心隔离", "Immediate isolation for background processes without user windows", "ユーザー画面を持たない背景プロセスを即時分離" } },
            { "mode.pick.custom", new[]{ "完全按策略开关执行 · 高风险功能不自动开启", "Exact policy switches · risky features never auto-enable", "設定どおり・高リスク機能は自動有効化しない" } },
            { "v15.overview.sub", new[]{ "这里只显示当前真实状态与结果；模式统一从右上角切换，不再出现重复入口。", "This page shows real state and results only. Change mode from the single control at the top right.", "ここは実状態と結果のみ表示。モード変更は右上の唯一の入口から行います。" } },
            { "v15.guard.state", new[]{ "AEGIS 守护状态", "AEGIS GUARD STATUS", "AEGIS ガード状態" } },
            { "v15.effective.mode", new[]{ "实际生效模式", "EFFECTIVE MODE", "実際の有効モード" } },
            { "v15.master.short", new[]{ "筛选到最可能的游戏画面进程后接管，退出时恢复已记录状态", "Engages after selecting the most likely game process and restores recorded state on exit", "最も可能性の高いゲーム画面候補を選んで動作し 終了時に記録済み状態を復元" } },
            { "v15.tile.game", new[]{ "游戏资源", "Game resources", "ゲーム資源" } },
            { "v15.tile.game.sub", new[]{ "只提优启发式筛选的画面候选", "Only the heuristically selected game candidate is boosted", "ヒューリスティックで選んだゲーム候補だけを提優" } },
            { "v15.tile.background", new[]{ "后台边界", "Background boundary", "背景境界" } },
            { "v15.tile.background.sub", new[]{ "系统 / 音频 / 白名单硬豁免", "Hard safety exemptions", "安全除外を常時維持" } },
            { "v15.tile.environment", new[]{ "系统环境", "System environment", "システム環境" } },
            { "v15.tile.environment.sub", new[]{ "电源与调度按预设执行", "Preset-aware power and scheduling", "プリセット別の電源と調度" } },
            { "v15.library.sub", new[]{ "只从 EXE 或快捷方式添加；自动识别游戏族，只提优启发式筛选的画面候选。", "Add an EXE or shortcut. Aegis identifies the game family and boosts only the heuristically selected display candidate.", "EXE またはショートカットから追加。ゲーム一式を識別し ヒューリスティックで選んだ画面候補だけを提優します。" } },
            { "v15.library.empty", new[]{ "尚未添加游戏\r\n添加 EXE/LNK，或将文件拖到这里", "NO GAMES YET\r\nAdd an EXE/LNK or drop one here", "ゲームはまだありません\r\nEXE/LNK を追加またはここへドロップ" } },
            { "v15.library.add", new[]{ "＋ 添加游戏文件", "+ Add game file", "＋ ゲームファイルを追加" } },
            { "v15.library.drop", new[]{ "EXE / LNK · 支持拖放", "EXE / LNK · DRAG & DROP", "EXE / LNK · ドロップ対応" } },
            { "v15.library.running", new[]{ "运行中", "RUNNING", "実行中" } },
            { "v15.library.ready", new[]{ "已就绪", "READY", "準備完了" } },
            { "v15.policy.sub", new[]{ "这里只配置具体行为，不再选择模式；右上角始终显示唯一模式入口与实际来源。", "This page configures behavior only. The single mode entry and effective source stay at the top right.", "ここでは動作のみ設定。唯一のモード入口と実際の由来は常に右上に表示。" } },
            { "v15.policy.mode.hint", new[]{ "常规 / 竞技可能覆盖自定义项；强制生效时会明确标记并锁定", "Standard/Competitive may override custom items; forced behavior is labelled and locked", "標準/競技はカスタム項目を上書きする場合があり、強制時は明示して固定" } },
            { "v15.policy.core", new[]{ "全模式核心控制", "CORE CONTROLS · ALL MODES", "全モード共通の基本制御" } },
            { "v15.policy.custom", new[]{ "自定义预设细节", "CUSTOM PRESET DETAILS", "カスタムプリセット詳細" } },
            { "v15.policy.extreme", new[]{ "极限实验室 · 高风险", "EXTREME LAB · HIGH RISK", "極限ラボ・高リスク" } },
            { "v15.boost.sub", new[]{ "根据入口关系 窗口和路径筛选最可能的画面进程；启动器和平台不会获得游戏优先级", "Selects the most likely game process from entry relationships windows and paths; launchers and platforms are not boosted", "入口関係 ウィンドウ パスから最も可能性の高いゲーム候補を選び ランチャーとプラットフォームは提優しません" } },
            { "v15.plan.sub", new[]{ "游戏期间按当前预设应用可逆电源计划，退出后恢复原计划", "Applies a reversible preset-aware power plan during play and restores it on exit", "プレイ中のみプリセット別の可逆電源プランを適用し、終了時に復元" } },
            { "v15.notif.sub", new[]{ "游戏运行期间抑制系统通知，游戏退出后自动恢复", "Suppresses system notifications during play and restores them afterwards", "ゲーム中のみ通知を抑え、終了後に自動復元" } },
            { "v15.hz.sub", new[]{ "会话期间守护最高刷新率；显示配置异常时可关闭", "Keeps the highest refresh rate during the session; disable if a display misbehaves", "セッション中に最高リフレッシュレートを維持。表示異常時は無効化" } },
            { "v15.custom.override", new[]{ "用于自定义预设；竞技可能按固定策略强制启用", "Used by Custom; Competitive may force its fixed policy", "カスタム用。競技では固定ポリシーで強制される場合あり" } },
            { "v15.trim.sub", new[]{ "仅自定义重压时执行；可能触发后台重新读盘，默认关闭", "Custom heavy mode only; may cause background paging and stays off by default", "カスタム強抑制時のみ。再読込を起こすため既定オフ" } },
            { "v15.anticheat.sub", new[]{ "独立于游戏性能预设，只控制下方明确列出的反作弊用户态组件。", "Independent of performance presets and limited to the explicitly listed user-mode anti-cheat components.", "性能プリセットと独立し、下記のユーザー態アンチチートのみ制御。" } },
            { "v14.overview.sub", new[]{ "按入口关系 窗口和路径筛选最可能的游戏画面候选；启动器和平台进程不会被提优。", "Selects the most likely game candidate from entry relationships windows and paths; launchers and platform clients are not boosted.", "入口関係 ウィンドウ パスから最も可能性の高いゲーム候補を選び ランチャーとプラットフォームは提優しません。" } },
            { "v14.master", new[]{ "Aegis 智能守护", "Aegis Smart Guard", "Aegis スマートガード" } },
            { "v14.master.sub", new[]{ "全局总开关；所有游戏共享当前性能预设", "Global master; every game shares the active preset", "全体スイッチ。全ゲームで現在のプリセットを共有" } },
            { "v14.preset", new[]{ "性能预设", "Performance preset", "性能プリセット" } },
            { "preset.standard", new[]{ "常规", "Standard", "標準" } },
            { "preset.competitive", new[]{ "竞技", "Competitive", "競技" } },
            { "preset.custom", new[]{ "自定义", "Custom", "カスタム" } },
            { "v14.session", new[]{ "当前会话", "Current session", "現在のセッション" } },
            { "v14.cpu.topology", new[]{ "CPU 调度拓扑", "CPU scheduling topology", "CPU スケジューリング構成" } },
            { "v14.library.sub", new[]{ "选择任意游戏 exe 后自动推断安装根目录；同根目录组件只做家族豁免，只有筛选出的画面候选获得提优。", "Pick any game executable to infer its install root. Family components are exempted, while only the selected display candidate is boosted.", "任意のゲーム exe からルートを推定。同一ルートの構成要素は除外保護し 選択した画面候補だけを提優します。" } },
            { "v14.boost.status", new[]{ "游戏提权状态", "GAME BOOST STATUS", "ゲーム提優状態" } },
            { "v14.boost.disabled", new[]{ "游戏提优已关闭", "Game boost is disabled", "ゲーム提優は無効です" } },
            { "v14.boost.wait", new[]{ "等待游戏画面候选", "Waiting for a game-display candidate", "ゲーム画面候補を待機中" } },
            { "v14.boost.no.renderer", new[]{ "已检测到游戏入口，但尚未筛选到画面候选", "Game entry detected; no game-process candidate selected yet", "ゲーム入口を検出しましたが 画面候補はまだ選ばれていません" } },
            { "v14.boost.applying", new[]{ "正在验证并提优：{0}", "Applying and verifying: {0}", "検証して提優中：{0}" } },
            { "v14.boost.verified", new[]{ "已验证：{0}\r\n高优先级 + 高 I/O 已回读确认", "Verified: {0}\r\nHigh priority + high I/O confirmed by readback", "検証済み：{0}\r\n高優先度 + 高 I/O を読戻し確認" } },
            { "v14.boost.failed", new[]{ "提优未落地：{0}\r\nAegis 正在持续纠偏，请查看日志", "Boost did not stick: {0}\r\nAegis will keep correcting it; see the log", "提優未反映：{0}\r\nAegis が継続補正します。ログを確認" } },
            { "v14.policy.sub", new[]{ "先选择性能预设，再检查安全边界；反作弊控制已独立分区，不再与游戏调度混在一起。", "Choose a performance preset, then review its safety boundaries. Anti-cheat controls are separated from game scheduling.", "性能プリセットを選び、安全境界を確認。アンチチート制御はゲーム調度と分離しました。" } },
            { "v14.policy.performance", new[]{ "性能策略", "Performance policy", "性能ポリシー" } },
            { "v14.preset.choose", new[]{ "选择性能预设", "Choose a performance preset", "性能プリセットを選択" } },
            { "v14.policy.boundary", new[]{ "安全边界与额外开关", "Safety boundaries and overrides", "安全境界と追加設定" } },
            { "v14.bg.master", new[]{ "后台调度 · 总开关", "Background scheduling · master", "背景調度・メイン" } },
            { "v14.bg.master.sub", new[]{ "关闭后所有预设都不再压制普通后台；前台、带主窗口程序家族、游戏 PID 家族、Windows 核心、其它用户会话、Aegis 自身和白名单始终豁免", "Off leaves ordinary apps untouched; foreground and user-window process families, game PIDs, Windows core, other sessions, Aegis, and the whitelist stay exempt", "オフでは全プリセットの背景抑制を停止。前面と主画面を持つプロセス一式、ゲーム PID 群、Windows 中枢、他セッション、Aegis、許可リストは常に除外" } },
            { "v14.cpu.adaptive.sub2", new[]{ "竞技始终启用；常规/自定义可在此额外强制。无安全分区时会自动降级，不伪装成功", "Always on in Competitive; optional for Standard/Custom. Falls back safely when no valid partition exists", "競技では常時有効。標準/カスタムでは任意。安全な分離不可時は自動的に安全側へ縮退" } },
            { "v14.preset.forced", new[]{ "当前预设强制", "forced by preset", "現在のプリセットで強制" } },
            { "v14.anticheat.boundary", new[]{ "反作弊只由此处控制，不跟随性能预设", "Anti-cheat is controlled only here, independently of presets", "アンチチートはプリセットと独立し、ここだけで制御" } },
            { "v14.anticheat.master.sub", new[]{ "只影响下方明确列出的反作弊组件；不会把游戏平台或登录器当成反作弊", "Affects only the listed anti-cheat components; game clients and launchers are not treated as anti-cheat", "下記アンチチートのみ対象。ゲームクライアントやランチャーは対象外" } },
            { "v14.preset.standard.short", new[]{ "常规：优先兼容性；后台先轻压，只有持续争抢资源时才升级。", "Standard: compatibility first; light guard, escalating only sustained contention.", "標準：互換性優先。軽い抑制から、競合が続く場合のみ強化。" } },
            { "v14.preset.standard.for", new[]{ "推荐 · 大多数单机、网游与日常后台共存", "RECOMMENDED · MOST GAMES AND EVERYDAY MULTITASKING", "推奨・大半のゲームと普段使い" } },
            { "v14.preset.standard.title", new[]{ "常规｜稳妥释放性能", "Standard | Balanced performance", "標準｜安定した性能" } },
            { "v14.preset.standard.detail", new[]{ "画面候选提优；无用户窗口后台立即进入 Eco。只有持续 CPU / IO 大户才逐级压制，不自动启用严格核心分区。", "Boosts the selected game candidate and puts background processes without user windows in Eco immediately. Only sustained CPU/IO contention escalates; strict partitioning stays off.", "選択したゲーム候補を提優し ユーザー画面を持たない背景プロセスを直ちに Eco 化します。CPU / IO 競合が続く場合のみ段階強化し 厳格分離は自動有効化しません。" } },
            { "v14.preset.competitive.short", new[]{ "竞技：没有用户窗口的后台进程无需采样等待，立即隔离到专用核心。", "Competitive: background processes without a user-facing window are isolated immediately.", "競技：ユーザー画面を持たない背景プロセスを待機なしで即時分離。" } },
            { "v14.preset.competitive.for", new[]{ "适合明确存在 CPU 后台争抢并愿意使用激进策略的场景", "FOR SYSTEMS WITH MEASURED CPU CONTENTION AND USERS WHO ACCEPT AN AGGRESSIVE POLICY", "CPU の背景競合を確認済みで 強い方針を許容できる場合向け" } },
            { "v14.preset.competitive.title", new[]{ "竞技｜立即隔绝资源争抢", "Competitive | Immediate contention isolation", "競技｜リソース競合を即時分離" } },
            { "v14.preset.competitive.detail", new[]{ "游戏与无用户窗口后台立即分区；前台和带主窗口程序保持豁免，同时启用下载抑制、网络 / MMCSS 调度与 Game DVR 控制。", "Immediately partitions the game from background processes without user windows; foreground and user-facing apps stay exempt while download network/MMCSS and Game DVR controls are enabled.", "ゲームとユーザー画面を持たない背景プロセスを即時分離し 前面アプリと主画面を持つアプリは除外したまま ダウンロード ネットワーク MMCSS Game DVR を制御します。" } },
            { "v14.preset.custom.short", new[]{ "自定义：完全按高级策略开关执行；高风险功能不会被自动开启。", "Custom: follows advanced switches exactly; risky features are never enabled implicitly.", "カスタム：詳細設定どおりに動作。高リスク機能は自動有効化しません。" } },
            { "v14.preset.custom.for", new[]{ "适合 · 明确知道每个调度选项作用的高级用户", "FOR · ADVANCED USERS WHO WANT EXPLICIT CONTROL", "対象・各調度設定を理解し明示制御したい上級者" } },
            { "v14.preset.custom.title", new[]{ "自定义｜逐项决定行为", "Custom | Explicit per-feature control", "カスタム｜機能ごとの明示制御" } },
            { "v14.preset.custom.detail", new[]{ "按高级策略中的开关组合执行。严格分区、工作集修剪和深度冻结均需显式开启，不会由预设偷偷启用。", "Uses the exact advanced-policy switch combination. Strict partitioning, working-set trim, and deep freeze all require explicit opt-in.", "詳細設定の組み合わせどおりに実行。厳格分離、ワーキングセット削減、深度凍結は明示的な有効化が必要です。" } },
            { "v14.bg.staged", new[]{ "预设后台策略", "Preset background policy", "プリセット背景ポリシー" } },
            { "v14.bg.staged.sub", new[]{ "常规：无用户窗口后台轻压 · 竞技：同一边界内立即核心隔离", "Standard: light guard for background processes without user windows · Competitive: immediate CPU isolation inside the same boundary", "標準：ユーザー画面のない背景を軽く抑制・競技：同じ境界内で即時コア分離" } },
            { "v14.cpu.adaptive", new[]{ "严格 CPU 分区", "Strict CPU partition", "厳格な CPU 分離" } },
            { "v14.cpu.adaptive.sub", new[]{ "适配 SMT、混合架构、X3D 与多处理器组；竞技预设自动启用", "SMT, hybrid, X3D and multi-group aware; automatically enabled by Competitive", "SMT、ハイブリッド、X3D、複数グループ対応。競技で自動有効" } },
            { "v14.freeze", new[]{ "深度冻结（高风险）", "Deep freeze (high risk)", "ディープフリーズ（高リスク）" } },
            { "v14.freeze.sub", new[]{ "只冻结持续高干扰且通过全部豁免检查的用户进程；不会因竞技模式自动开启", "Freezes only sustained high-interference user processes after all exemptions; Competitive never enables it automatically", "全除外検査を通過した高干渉ユーザープロセスのみ凍結。競技でも自動有効化しません" } },
            { "v14.anticheat", new[]{ "反作弊专项", "Anti-cheat controls", "アンチチート制御" } },
            { "v14.manage.white", new[]{ "管理白名单", "Manage whitelist", "ホワイトリスト管理" } },
            { "v14.reports.sub", new[]{ "每局保存持续时间、提权回读结果、后台压制与冻结数量；不再运行帧采集器。", "Each session records duration, verified boost state, suppression, and freezes. No frame collector is run.", "各セッションの時間、提優読戻し、背景抑制、凍結数を保存。フレーム収集は行いません。" } },
            { "v14.open.report", new[]{ "打开报告文件", "Open report file", "レポートを開く" } },
            { "v14.cpu.multigroup", new[]{ "多处理器组 · CPU Sets 跨组调度 · 竞技模式物理核优先", "Multiple processor groups · cross-group CPU Sets · Competitive prefers physical cores", "複数プロセッサグループ · CPU Sets 跨グループ制御 · 競技は物理核優先" } },
            { "v14.cpu.hybrid", new[]{ "混合架构 · 后台能效核 · 竞技模式性能核优先", "Hybrid CPU · background on E-cores · Competitive prefers performance cores", "ハイブリッド CPU · 背景は E コア · 競技は性能コア優先" } },
            { "v14.cpu.x3d", new[]{ "非对称 L3 / X3D · 游戏大缓存 CCD · 后台隔离到另一 CCD", "Asymmetric L3 / X3D · game on large-cache CCD · background isolated elsewhere", "非対称 L3 / X3D · ゲームは大容量キャッシュ CCD · 背景は別 CCD へ分離" } },
            { "v14.cpu.generic", new[]{ "{0} 逻辑处理器 · 常规按需升级 · 竞技立即物理核隔离", "{0} logical processors · Standard escalates on demand · Competitive isolates immediately", "{0} 論理プロセッサ · 標準は必要時に強化 · 競技は即時物理核分離" } },
            { "detect.foreground", new[]{ "游戏根目录 + 前台窗口（评分 {0}）", "Game root + foreground window (score {0})", "ゲームルート + 前面ウィンドウ（スコア {0}）" } },
            { "detect.visible", new[]{ "游戏根目录 + 可见窗口（评分 {0}）", "Game root + visible window (score {0})", "ゲームルート + 表示ウィンドウ（スコア {0}）" } },
            { "detect.candidate", new[]{ "游戏根目录内的候选进程（评分 {0}）", "Candidate inside game root (score {0})", "ゲームルート内の候補（スコア {0}）" } },
            { "report.none", new[]{ "暂无游戏会话报告。完成一局游戏后会在这里留下调度结果。", "No game-session reports yet. A completed session will leave scheduling results here.", "ゲームセッションレポートはまだありません。終了後に調度結果が保存されます。" } },
            { "report.read.error", new[]{ "报告读取失败。", "Could not read the report.", "レポートを読み込めませんでした。" } },
            { "report.boost.ok", new[]{ "提权已回读验证", "boost verified by readback", "提優を読戻し確認" } },
            { "report.boost.missed", new[]{ "本局未验证提权", "boost not verified", "提優未検証" } },
            { "report.control", new[]{ "压制 {0} / 冻结 {1}", "restrained {0} / frozen {1}", "抑制 {0} / 凍結 {1}" } },
            { "report.unknown.game", new[]{ "未知游戏", "Unknown game", "不明なゲーム" } },
            { "btn.ok", new[]{ "保存", "Save", "保存" } },

            { "title.noelev", new[]{ "未提权 · 压制类功能无效", "Not elevated · suppression disabled", "権限なし・抑制機能は無効" } },
            { "title.admin", new[]{ "管理员权限", "Administrator", "管理者権限" } },
            { "title.guard", new[]{ "守护中（{0}）", "guarding ({0})", "稼働中（{0}）" } },
            { "title.idle", new[]{ "待命", "standby", "待機中" } },

            { "game.toggle", new[]{ "游戏模式 · 检测到游戏时把系统专供游戏", "Game Mode · dedicate the system to the running game", "ゲームモード・実行中のゲームにシステムを専有" } },
            { "game.toggle.n", new[]{ "后台降优先级 + 锁核 + 效率模式，游戏提优先级 + 高IO + 卓越电源；退出自动还原。", "Background: lower priority + core-lock + eco mode; game: boosted priority + high-IO + Ultimate power. Auto-restored on exit.", "背景は優先度低下 + コア固定 + 効率モード、ゲームは優先度アップ + 高IO + 高性能電源。終了で自動復元。" } },
            { "game.list", new[]{ "游戏列表（游戏本体和启动器都建议加入）", "Game list (add both the game and its launcher)", "ゲーム一覧（本体とランチャー両方を推奨）" } },
            { "btn.pick", new[]{ "＋ 从运行中选择", "＋ Pick from running", "＋ 実行中から選択" } },
            { "btn.browse", new[]{ "＋ 浏览 exe…", "＋ Browse exe…", "＋ exe を参照…" } },
            { "btn.remove", new[]{ "－ 移除选中", "－ Remove selected", "－ 選択を削除" } },

            { "st.off", new[]{ "已关闭", "Off", "オフ" } },
            { "st.mon", new[]{ "监控中，未检测到列表内的游戏", "Monitoring, no listed game detected", "監視中、一覧のゲーム未検出" } },
            { "st.active", new[]{ "激活中（{0}）· 已压制 {1} 个进程", "Active ({0}) · {1} processes throttled", "稼働中（{0}）・{1} プロセス抑制" } },
            { "st.boost", new[]{ " · 已提优 {0} · {1}", " · boosted {0} · {1}", " ・提優 {0}・{1}" } },
            { "st.hp", new[]{ "高性能电源", "high-perf power", "高性能電源" } },
            { "st.pr", new[]{ "电源就绪", "power ready", "電源待機" } },
            { "st.extreme", new[]{ " · 极限模式（冻结 {0}）", " · Extreme (frozen {0})", " ・極限モード（凍結 {0}）" } },

            { "tame.toggle", new[]{ "反作弊压制 · 总开关", "Anti-Cheat Suppression · master", "アンチチート抑制・メイン" } },
            { "tame.caveat", new[]{
                "压制 = 最低优先级 + 锁核 + 效率模式，不杀进程、不阻止检测、不改内存。\r\nVanguard / EAC / BattlEye 会监控自身，压得太狠可能心跳超时掉线；默认只开较稳的 ACE，其它按需开。",
                "Suppress = lowest priority + core-lock + efficiency mode. No killing / no bypassing detection / no memory edits.\r\nVanguard / EAC / BattlEye watch their own state; heavy suppression can cause a heartbeat-timeout disconnect. Only ACE is on by default — enable the rest as needed.",
                "抑制 = 最低優先度 + コア固定 + 効率モード。プロセス終了/検知妨害/メモリ改変なし。\r\nVanguard / EAC / BattlEye は自身を監視するため、強く抑えるとハートビート切れで切断されることがあります。既定は ACE のみ、他は必要に応じて。" } },
            { "tame.footer", new[]{ "只处理当前厂商列表中明确列出的用户态进程；关闭某项会恢复对应进程", "Only explicitly listed user-mode processes are handled; disabling an item restores its processes", "現在のベンダー一覧に明記したユーザーモードプロセスのみ対象 項目をオフにすると復元" } },

            { "gs.moff", new[]{ "总开关关闭", "Master off", "メインオフ" } },
            { "gs.noff", new[]{ "未开启", "Off", "オフ" } },
            { "gs.noproc", new[]{ "未检测到进程", "No process", "プロセス無し" } },
            { "gs.thr", new[]{ "已压制 {0}", "{0} throttled", "{0} 抑制" } },
            { "gs.prot", new[]{ "（{0} 个受内核保护压不动）", " ({0} kernel-protected)", "（{0} 件カーネル保護で不可）" } },

            { "white.desc", new[]{
                "始终豁免清单：这些进程不会进入任何后台策略。增删即时生效；运行中加白会立刻恢复。",
                "Always-exempt list: these processes never enter any background policy. Changes are immediate; adding one restores it at once.",
                "常時除外リスト：これらはどの背景ポリシーの対象にもなりません。変更は即時反映されます。" } },
            { "btn.reset", new[]{ "恢复默认预设", "Reset to preset", "既定に戻す" } },

            { "btn.openlog", new[]{ "打开日志文件", "Open log file", "ログを開く" } },
            { "btn.clearlog", new[]{ "清空日志", "Clear log", "ログをクリア" } },

            { "set.gpu", new[]{ "逐游戏高性能 GPU 偏好", "Per-game High-performance GPU preference", "ゲームごとの高パフォーマンス GPU 設定" } },
            { "set.gpu.n", new[]{ "双显卡笔记本强制走独显，下次启动游戏生效。", "Forces the dGPU on dual-GPU laptops; applies from the next game launch.", "デュアル GPU ノートで dGPU を強制、次回起動から有効。" } },
            { "set.dvr", new[]{ "关闭 Game DVR / Xbox 后台录制", "Disable Game DVR / Xbox recording", "Game DVR / Xbox 録画を無効化" } },
            { "set.fso", new[]{ "逐游戏关闭全屏优化 (FSO)", "Per-game disable Fullscreen Optimizations", "ゲームごとに全画面最適化(FSO)を無効化" } },
            { "set.fso.n", new[]{ "部分游戏降输入延迟，因游戏而异，可以试。", "Lower input latency for some games; varies by title — worth a try.", "一部ゲームで入力遅延を低減、ゲームによる。試す価値あり。" } },
            { "set.notif", new[]{ "游戏时免打扰", "Do-not-disturb during games", "ゲーム中は通知オフ" } },
            { "set.trim", new[]{ "自定义重压时清空后台工作集（可能触发读盘）", "Trim working sets in Custom heavy mode (may cause paging)", "カスタム強抑制時にメモリ解放（再読込あり）" } },
            { "set.hz", new[]{ "刷新率守护（切到最高刷新率）", "Refresh-rate guard (switch to max Hz)", "リフレッシュレート保護（最高 Hz へ）" } },
            { "set.plan", new[]{ "游戏时切换电源计划（卓越性能）", "Switch power plan during games (Ultimate)", "ゲーム中に電源プランを切替（究極のパフォーマンス）" } },
            { "gm.cfg", new[]{ "自定义优化策略", "Custom optimization policy", "カスタム最適化ポリシー" } },
            { "gm.suppress", new[]{ "后台策略（常规轻压 / 竞技隔离）", "Background policy (guard / isolate)", "背景ポリシー（軽減 / 分離）" } },
            { "gm.boost", new[]{ "游戏提优（高优先级 / 绑核 / 高IO / GPU优先）", "Boost the game (high priority / core pinning / high IO / GPU)", "ゲームを最適化（高優先度 / コア割当 / 高 IO / GPU）" } },
            { "gm.strict", new[]{ "严格核心隔离（自定义模式立即分区）", "Strict core isolation (immediate partition in Custom)", "厳格なコア分離（カスタムで即時分離）" } },
            { "gm.aggressive", new[]{ "竞技级压制范围", "Competitive-grade suppression scope", "競技級の抑制範囲" } },
            { "gm.aggressive.sub", new[]{ "开启后与竞技模式完全一致：前台窗口和可见窗口程序（浏览器/语音/IDE等）不再豁免后台压制，只有会话/认证/合成器/音频等核心系统服务还护着（不是整个 C:\\Windows 目录）；电源计划按竞技档调参。竞技始终启用；仅自定义可选，默认关闭", "Matches Competitive exactly: the foreground window and visible-window apps (browser/voice/IDE, etc.) lose their suppression exemption — only core system services (session/auth/compositor/audio, not the whole C:\\Windows tree) stay protected; power plan tuning also matches Competitive. Always on in Competitive; optional in Custom, off by default", "競技と完全に一致：前面ウィンドウと表示ウィンドウのアプリ（ブラウザ/ボイス/IDE等）は抑制対象になり セッション/認証/コンポジタ/オーディオ等の中核システムサービスのみ保護（C:\\Windows 全体ではない）。電源プランも競技相当に調整。競技では常時有効、カスタムでは任意（既定オフ）" } },
            { "gm.freeze", new[]{ "极限冻结（暂停持续高 CPU / IO 的用户后台）", "Extreme freeze (suspend sustained high CPU / IO apps)", "極限凍結（高 CPU / IO の背景アプリを停止）" } },
            { "gm.freeze.warn", new[]{ "极限冻结会暂停持续高 CPU / 磁盘 IO 的用户后台程序，它们在游戏结束前可能表现为无响应。前台与可见窗口程序家族、Windows 路径、游戏、白名单和 Aegis 会被排除；独立看门狗负责崩溃恢复。\r\n\r\n仍要开启？", "Extreme freeze suspends user apps with sustained CPU or disk activity; they may appear unresponsive until the game ends. Foreground and visible-window process families, Windows paths, games, the whitelist, and Aegis are excluded. An independent watchdog handles crash recovery.\r\n\r\nEnable it?", "極限凍結は CPU / ディスク負荷が続くユーザーアプリを停止し ゲーム終了まで応答しないように見える場合があります 前面と表示ウィンドウのプロセス一式 Windows パス ゲーム 許可リスト Aegis は除外し 独立した監視役がクラッシュ復元します\r\n\r\n有効にしますか？" } },
            { "gm.net", new[]{ "网络优化（关闭系统流控）", "Network tweak (disable throttling index)", "ネットワーク最適化（スロットリング無効）" } },
            { "gm.fgboost", new[]{ "前台调度加权（更长时间片给游戏）", "Foreground scheduling boost (longer quanta)", "前面プロセスのスケジューリング優先" } },
            { "gm.mmcss", new[]{ "MMCSS 多媒体调度优先", "MMCSS multimedia scheduling priority", "MMCSS マルチメディア優先" } },
            { "gm.pausedl", new[]{ "暂停后台下载（Windows 更新 / 传递优化）", "Pause background downloads (Windows Update / DO)", "背景ダウンロード停止（更新 / 配信の最適化）" } },
            { "gm.pausesvc", new[]{ "暂停索引 / 预取服务（机械盘收益明显）", "Pause indexing / prefetch services (helps HDDs most)", "インデックス / プリフェッチ停止（HDD で有効）" } },
            { "gm.hint", new[]{ "改动立即生效。极限功能默认关闭；冻结有独立看门狗，崩溃时自动恢复。", "Changes apply instantly. Extreme options default off; a watchdog restores frozen apps after a crash.", "変更は即時反映。極限機能は既定オフ、クラッシュ時は監視役が凍結を復元。" } },
            { "btn.done", new[]{ "完成", "Done", "完了" } },
            { "btn.gmcfg", new[]{ "高级策略配置", "Advanced policy", "詳細ポリシー設定" } },
            { "set.autostart", new[]{ "开机自启", "Start with Windows", "Windows 起動時に自動起動" } },
            { "set.autostart.n", new[]{ "走管理员权限的计划任务。", "Via an administrator scheduled task.", "管理者権限のタスクスケジューラ経由。" } },
            { "set.autohide", new[]{ "检测到游戏后自动收起窗口", "Auto-hide the window once a game starts", "ゲーム開始後にウィンドウを自動的に隠す" } },
            { "set.autohide.n", new[]{ "检测到游戏启动 10 秒后，把主窗口收回托盘。每局只收一次——之后你再打开它就不会又被收走，直到下一局开始。", "Ten seconds after a game is detected, the main window returns to the tray. Once per session only — if you open it again afterwards it stays open until the next game starts.", "ゲーム検出から 10 秒後にメインウィンドウをトレイに戻します。1 試合につき 1 回だけ——その後に開き直した場合は次の試合まで閉じられません。" } },
            { "set.hint", new[]{ "这里只保留持久系统设置和维护工具；游戏期间的调度行为统一放在「优化策略」。", "This page contains persistent system settings and maintenance only; in-game scheduling lives under Optimization.", "ここは永続システム設定と保守のみ。ゲーム中の調度は「最適化ポリシー」に集約。" } },
            { "set.lang", new[]{ "语言", "Language", "言語" } },
            { "sec.pergame", new[]{ "逐游戏", "PER-GAME", "ゲームごと" } },
            { "sec.system", new[]{ "系统", "SYSTEM", "システム" } },
            { "sec.maint", new[]{ "维护", "MAINTENANCE", "メンテナンス" } },
            { "btn.clean", new[]{ "清理", "Clean", "削除" } },
            { "log.sub", new[]{ "最近 200 行 · 自动刷新", "Last 200 lines · auto-refresh", "直近 200 行・自動更新" } },
            { "set.about", new[]{ "Aegis {0} · {1} · 本地配置与用户白名单", "Aegis {0} · {1} · local configuration and user whitelist", "Aegis {0} · {1} · ローカル設定とユーザー許可リスト" } },

            { "set.hags", new[]{ "开启 GPU 硬件调度 HAGS（需重启）", "Enable GPU Hardware Scheduling (needs reboot)", "GPU ハードウェアスケジューリング（要再起動）" } },
            { "set.hags.n", new[]{ "切换 Windows 的硬件 GPU 调度选项；效果取决于显卡驱动和游戏，部分帧生成功能可能要求开启。", "Toggles Windows hardware GPU scheduling. Results depend on the driver and game; some frame-generation features may require it.", "Windows のハードウェア GPU 調度を切り替えます 効果はドライバとゲーム次第で 一部のフレーム生成機能が必要とする場合があります" } },
            { "hags.reboot", new[]{ "已写入 HAGS 设置，重启电脑后生效。", "HAGS setting written — takes effect after a reboot.", "HAGS 設定を適用しました。再起動後に有効になります。" } },
            { "set.irqaffinity", new[]{ "GPU 中断亲和优化（需重启设备或电脑）", "GPU interrupt affinity tuning (needs a device or system restart)", "GPU 割り込みアフィニティ最適化（デバイスまたは再起動が必要）" } },
            { "set.irqaffinity.n", new[]{ "引导显卡中断靠近游戏所在的核心，减少跨核/跨缓存开销。多适配器或多处理器组系统可能只应用较轻的邻近策略。", "Steers GPU interrupt handling toward the cores your game runs on, reducing cross-core/cross-cache overhead. Multi-adapter or multi processor-group systems may only get the lighter proximity policy.", "GPU の割り込み処理をゲームが動作するコアに近づけ、コア/キャッシュをまたぐオーバーヘッドを減らします。複数アダプタや複数プロセッサグループ環境では軽量な近接ポリシーのみになる場合があります。" } },
            { "irqaffinity.reboot", new[]{ "已写入中断亲和设置，重启该设备或重启电脑后生效。", "Interrupt affinity setting written — takes effect after restarting the device or rebooting.", "割り込みアフィニティ設定を適用しました。デバイスの再起動または再起動後に有効になります。" } },
            { "set.netaffinity", new[]{ "游戏网络优先（需重启网卡或电脑）", "Game network priority (needs a NIC or system restart)", "ゲームネットワーク優先（NICまたはシステムの再起動が必要）" } },
            { "set.netaffinity.n", new[]{ "引导网卡中断靠近游戏所在的核心，并给游戏目录中已添加的可执行文件的流量打上 QoS 优先级标记，减少排队和抖动。", "Steers NIC interrupt handling toward your game's cores and tags traffic from executables already added to your game library with a QoS priority marking, reducing queueing and jitter.", "NIC の割り込み処理をゲームが動作するコアに近づけ、ゲームライブラリに追加済みの実行ファイルの通信に QoS 優先マーキングを付与し、キューイングとジッターを減らします。" } },
            { "netaffinity.reboot", new[]{ "网卡中断设置需要重启该网卡或重启电脑后生效；流量优先级标记会在新建立的网络连接上生效。", "The NIC interrupt setting takes effect after restarting the adapter or rebooting; the traffic priority marking applies to newly established connections.", "NIC の割り込み設定はアダプタの再起動または再起動後に有効になります。トラフィックの優先マーキングは新しく確立された接続に適用されます。" } },
            { "set.vbs", new[]{ "关闭 VBS + 停用 hypervisor（需重启）", "Disable VBS + stop the hypervisor (needs reboot)", "VBS 無効化 + hypervisor 停止（要再起動）" } },
            { "vbs.state.on", new[]{ "当前：VBS 正在运行 —— 关闭会降低系统安全性，性能影响需在本机单独测试", "Now: VBS is running — disabling it reduces system security and any performance effect must be measured on this machine.", "現在 VBS が動作中です 無効化は安全性を下げるため 性能差はこの PC で個別に測定してください" } },
            { "vbs.state.off", new[]{ "当前：VBS 未运行（本机已经没有这项开销）", "Now: VBS is not running (this overhead is already absent)", "現在：VBS は非稼働（このオーバーヘッドは既にありません）" } },
            { "vbs.state.pending", new[]{ "已设为关闭，重启电脑后生效", "Set to disabled — takes effect after you reboot", "無効に設定済み —— 再起動後に有効化されます" } },
            { "vbs.state.unknown", new[]{ "当前状态未知（无法查询 WMI）", "Current state unknown (WMI query unavailable)", "現在の状態は不明（WMI を照会できません）" } },
            { "vbs.warn", new[]{
                "关闭 VBS / 内存完整性，并停用 hypervisor。\r\n\r\n这会降低系统安全性，并让 WSL2 / Docker / Hyper-V 虚拟机 / Windows 沙盒无法使用。性能变化没有通用结论，Aegis 也没有真实游戏基准，请只在理解风险并准备自行对照测试时使用。\r\n\r\n需重启才生效，全机器生效，恢复后也需要再次重启。\r\n\r\n继续吗？",
                "Turn off VBS / Memory Integrity and stop the hypervisor.\r\n\r\nThis reduces system security and prevents WSL2 / Docker / Hyper-V VMs / Windows Sandbox from running. There is no universal performance result and Aegis has no real-game benchmark, so use it only if you understand the risk and will measure the result yourself.\r\n\r\nIt takes effect machine-wide after a reboot and recovery also requires another reboot.\r\n\r\nContinue?",
                "VBS とメモリ整合性を無効化し hypervisor を停止します。\r\n\r\nシステムの安全性が下がり WSL2 Docker Hyper-V VM Windows サンドボックスが使えなくなります。共通の性能結果はなく Aegis に実ゲームベンチマークもありません。リスクを理解し 自分で比較測定する場合だけ使用してください。\r\n\r\n再起動後に PC 全体へ反映され 復元後も再起動が必要です。\r\n\r\n続けますか？" } },
            { "vbs.needadmin", new[]{ "修改 VBS / hypervisor 需要管理员权限，请以管理员身份运行 Aegis。", "Changing VBS / the hypervisor requires administrator rights — run Aegis as administrator.", "VBS / hypervisor の変更には管理者権限が必要です。Aegis を管理者で実行してください。" } },
            { "vbs.done", new[]{ "已关闭 VBS 并停用 hypervisor。\r\n请重启电脑生效。重启后 WSL2 / Docker / Hyper-V / 沙盒将无法使用；要恢复请回到这里关掉此开关再重启。", "VBS disabled and hypervisor stopped.\r\nReboot to apply. After reboot, WSL2 / Docker / Hyper-V / Sandbox won't work — to undo, turn this switch back off here and reboot again.", "VBS を無効化し hypervisor を停止しました。\r\n再起動で有効になります。再起動後は WSL2 / Docker / Hyper-V / サンドボックスが使えません。戻すにはこのスイッチをオフにして再度再起動してください。" } },
            { "vbs.restored", new[]{ "已恢复 VBS 与 hypervisor（hypervisorlaunchtype → 原值）。\r\n请重启电脑生效，之后 WSL2 / Docker 等恢复可用。", "VBS and the hypervisor have been restored (hypervisorlaunchtype → original).\r\nReboot to apply; WSL2 / Docker etc. work again afterwards.", "VBS と hypervisor を復元しました（hypervisorlaunchtype → 元の値）。\r\n再起動で有効になり、その後 WSL2 / Docker などが再び使えます。" } },
            { "vbs.restorefail", new[]{ "还原未完全成功（bcdedit 可能失败），原状态快照已保留，可再试一次。详见日志。", "Restore did not fully succeed (bcdedit may have failed). The snapshot is kept — you can try again. See the log for details.", "復元が完全には成功しませんでした（bcdedit が失敗した可能性）。スナップショットは保持されています。もう一度お試しください。詳細はログを参照。" } },

            { "btn.shader", new[]{ "清理着色器缓存", "Clean shader caches", "シェーダーキャッシュ削除" } },
            { "set.shader.n", new[]{ "怀疑缓存损坏或不兼容时可尝试清理；随后会重新编译着色器，首次运行可能更卡。", "Try this only when cache corruption or incompatibility is suspected. Shaders will be rebuilt and the next run may stutter more.", "キャッシュ破損や非互換が疑われる場合だけ試してください 再コンパイルのため次回起動はカクつきが増える場合があります" } },
            { "shader.size", new[]{ "当前占用约 {0}（NVIDIA / AMD / Intel / DirectX）", "About {0} on disk (NVIDIA / AMD / Intel / DirectX)", "現在約 {0}（NVIDIA / AMD / Intel / DirectX）" } },
            { "shader.busy", new[]{ "正在清理…", "Cleaning…", "クリーンアップ中…" } },
            { "shader.confirm", new[]{
                "清空显卡着色器缓存（NVIDIA / AMD / Intel / DirectX）？\r\n\r\n当你怀疑驱动更新后存在损坏或不兼容缓存时可以把清理作为排查步骤，但 Aegis 无法判断卡顿原因，也不保证清理会改善性能。\r\n\r\n只删缓存文件并保留目录；清理前请退出游戏。之后游戏需要重新编译着色器，首次运行可能出现额外卡顿。\r\n\r\n继续吗？",
                "Clear the GPU shader caches (NVIDIA / AMD / Intel / DirectX)?\r\n\r\nThis can be a troubleshooting step when damaged or incompatible cache data is suspected after a driver update. Aegis cannot identify the cause of stutter and does not promise a performance improvement.\r\n\r\nOnly cache files are removed and folders remain. Close games first. Shaders must be recompiled afterwards, so the next run may stutter more.\r\n\r\nContinue?",
                "GPU シェーダーキャッシュ（NVIDIA / AMD / Intel / DirectX）を削除しますか？\r\n\r\nドライバ更新後に破損または非互換のキャッシュが疑われる場合の切り分け手段です Aegis はカクつきの原因を判定できず 性能改善も保証しません\r\n\r\nキャッシュファイルだけを削除し フォルダは残します ゲームを終了してから実行してください その後はシェーダー再コンパイルが必要なため 次回起動時に追加のカクつきが出る場合があります\r\n\r\n続行しますか？" } },
            { "shader.freed", new[]{ "已清理着色器缓存，释放 {0}。", "Shader caches cleaned — {0} freed.", "シェーダーキャッシュを削除し、{0} を解放しました。" } },
            { "shader.skip", new[]{ "{0} 个文件正被占用已跳过（关闭游戏后可再清一次）。", "{0} files were in use and skipped (close games and clean again).", "使用中のファイル {0} 件をスキップしました（ゲーム終了後に再実行を）。" } },
            { "shader.note", new[]{ "每个游戏下次启动都可能重新编译着色器，首次运行可能出现额外卡顿。", "Games may recompile shaders on the next launch, which can add first-run stutter.", "次回起動時にシェーダーを再コンパイルし 初回のカクつきが増える場合があります" } },

            { "btn.lolcross", new[]{ "清理 LOL Cross 组件", "Clean LoL CN Cross", "LOL Cross を削除" } },
            { "set.lolcross.n", new[]{ "国服客户端的电视台 / 英雄时刻等附加组件；社区有清理后更稳或帧数改善的经验，效果因版本和机器而异。", "Optional CN-client components such as TV / Hero Moments; community reports vary by client version and hardware.", "中国版クライアントの TV / ハイライト等の追加機能。改善例はコミュニティ経験で、版や環境により異なります。" } },
            { "lol.size", new[]{ "当前占用约 {0}（电视台 / 英雄时刻 / 赛后教练等）", "About {0} on disk (TV / Hero Moments / coach overlay)", "現在約 {0}（TV / ハイライト / コーチ等）" } },
            { "lol.running", new[]{ "检测到英雄联盟 / WeGame 相关进程正在运行。\r\n为避免删除正在使用或被立即修复的文件，请先退出游戏和登录器再试。", "League of Legends / WeGame processes are running.\r\nClose the game and launcher first to avoid deleting files in use or immediately repaired files.", "League of Legends / WeGame が実行中です。\r\n使用中または直ちに修復されるファイルを避けるため、ゲームとランチャーを終了してください。" } },
            { "lol.confirm", new[]{
                "清空国服英雄联盟的 Cross 组件（电视台 / 英雄时刻 / 赛后教练等附加功能）？\r\n\r\n社区长期有人反馈清理后更稳或帧数改善，但效果随客户端版本、机器和功能使用情况变化，Aegis 不承诺固定收益。按当前目录结构它不是 ACE 反作弊组件，但客户端后续版本可能调整。\r\n\r\n代价：英雄时刻等腾讯附加功能将不可用，也可能触发客户端修复或在更新后重新下载。只清内容，Cross 目录本身保留。\r\n\r\n继续吗？",
                "Clear the CN League of Legends \"Cross\" components (TV / Hero Moments / coach extras)?\r\n\r\nThe community has long reported smoother play or FPS improvements, but results vary by client version, hardware, and feature use; Aegis promises no fixed gain. In the current layout this is separate from ACE anti-cheat, though future client versions may change.\r\n\r\nCost: Tencent extras such as Hero Moments stop working, and the client may repair or re-download them. Only the contents are removed; the Cross folder itself is kept.\r\n\r\nContinue?",
                "中国版 League of Legends の Cross（TV / ハイライト / コーチ等）を削除しますか？\r\n\r\n安定性や FPS が改善したというコミュニティ経験はありますが、効果は版・環境・機能使用状況で変わり、固定の向上は保証しません。現行構成では ACE アンチチートとは別ですが、将来の版では変更される可能性があります。\r\n\r\n代償：追加機能が使えなくなり、クライアントが修復または再取得する場合があります。中身だけを削除し、Cross フォルダ自体は保持します。\r\n\r\n続行しますか？" } },
            { "lol.freed", new[]{ "已清理 LOL Cross 组件，释放 {0}。", "LoL Cross components cleaned — {0} freed.", "LOL Cross を削除し、{0} を解放しました。" } },
            { "lol.note", new[]{ "英雄时刻等腾讯附加功能将不可用；客户端更新时可能自动装回，届时再清一次即可。", "Tencent extras like Hero Moments are now off; the client may restore them after an update — just clean again.", "ハイライトなどの Tencent 付加機能は無効になります。更新で戻った場合は再削除してください。" } },

            { "tray.aclist", new[]{ "反作弊列表", "Anti-Cheat list", "アンチチート一覧" } },
            { "tray.reset", new[]{ "恢复默认配置", "Reset to defaults", "設定を既定に戻す" } },
            { "tray.resetask", new[]{ "把所有开关、反作弊选择和白名单恢复为默认？（游戏列表保留）", "Reset all toggles, anti-cheat selection, and the whitelist to defaults? (Your game list is kept.)", "すべてのスイッチ、アンチチート選択、ホワイトリストを既定に戻しますか？(ゲーム一覧は保持されます)" } },
            { "tm.gpu", new[]{ "高性能 GPU 偏好", "High-perf GPU preference", "高性能 GPU 設定" } },
            { "tm.dvr", new[]{ "关闭 Game DVR", "Disable Game DVR", "Game DVR を無効化" } },
            { "tm.fso", new[]{ "关闭全屏优化", "Disable Fullscreen Opt.", "全画面最適化を無効" } },
            { "tm.notif", new[]{ "游戏时免打扰", "Do-not-disturb in game", "ゲーム中は通知オフ" } },
            { "tm.trim", new[]{ "压制时清空后台内存", "Trim background memory", "背景メモリを解放" } },
            { "tm.plan", new[]{ "游戏时切换电源计划", "Switch power plan in game", "ゲーム中に電源プラン切替" } },
            { "tm.autostart", new[]{ "开机自启", "Start with Windows", "起動時に自動起動" } },

            { "about.desc", new[]{
                "管理游戏与无用户窗口后台资源边界的 Windows 工具。\r\n单文件 C# / WinForms · 无第三方依赖 · 为已记录改动提供恢复路径",
                "A Windows tool for managing the resource boundary between a game and background processes without user windows.\r\nSingle-file C# / WinForms · no third-party dependencies · recovery paths for recorded changes",
                "ゲームとユーザー画面を持たない背景プロセスの資源境界を管理する Windows ツール。\r\n単一ファイル C# / WinForms・外部依存なし・記録済み変更に復元経路を提供" } },
            { "about.author", new[]{ "作者", "Author", "作者" } },
            { "about.repo", new[]{ "开源仓库", "Repository", "リポジトリ" } },
            { "about.lic", new[]{ "开源协议", "License", "ライセンス" } },
            { "btn.checkupd", new[]{ "检查更新", "Check for updates", "更新を確認" } },
            { "btn.download", new[]{ "前往下载", "Open download page", "ダウンロード" } },
            { "upd.checking", new[]{ "正在检查更新…", "Checking for updates…", "更新を確認中…" } },
            { "upd.latest", new[]{ "已是最新版本（{0}）", "You are on the latest version ({0}).", "最新バージョンです（{0}）" } },
            { "upd.newver", new[]{ "发现新版本 {0}（当前 {1}），点击「前往下载」获取。", "New version {0} available (current {1}) — click \"Open download page\".", "新バージョン {0} が公開されています（現在 {1}）。「ダウンロード」からどうぞ。" } },
            { "upd.fail", new[]{ "检查更新失败：网络不可用或仓库暂未发布版本。", "Update check failed: network issue or no release published yet.", "更新確認に失敗：ネットワーク不通またはリリース未公開。" } },
            { "bal.update", new[]{ "发现新版本 {0}，请打开面板「关于」页查看。", "New version {0} available — see the About page for details.", "新バージョン {0} が利用可能です。「情報」ページをご確認ください。" } },

            { "dlg.pickproc", new[]{ "选择进程", "Select process", "プロセスを選択" } },
            { "btn.add", new[]{ "添加", "Add", "追加" } },
            { "btn.cancel", new[]{ "取消", "Cancel", "キャンセル" } },
            { "dlg.bgsvc", new[]{ "[后台服务]", "[background service]", "[バックグラウンド]" } },
            { "ofd.game", new[]{ "选择游戏 EXE 或 Windows 快捷方式", "Select a game EXE or Windows shortcut", "ゲーム EXE または Windows ショートカットを選択" } },
            { "ofd.white", new[]{ "选择要加入白名单的 exe", "Select an exe to whitelist", "ホワイトリストに追加する exe を選択" } },
            { "ofd.filter", new[]{ "游戏程序或快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|程序 (*.exe)|*.exe|快捷方式 (*.lnk)|*.lnk", "Game programs or shortcuts (*.exe;*.lnk)|*.exe;*.lnk|Programs (*.exe)|*.exe|Shortcuts (*.lnk)|*.lnk", "ゲームまたはショートカット (*.exe;*.lnk)|*.exe;*.lnk|プログラム (*.exe)|*.exe|ショートカット (*.lnk)|*.lnk" } },
            { "pick.busy", new[]{ "正在统计资源占用…", "Measuring resource usage…", "リソース使用量を集計中…" } },
            { "pick.count", new[]{ "共 {0} 个进程 · 按占用从高到低", "{0} processes · sorted by usage", "{0} 件 · 使用量の多い順" } },
            { "msg.taskfail", new[]{ "计划任务操作失败（需要管理员权限运行 Aegis）", "Scheduled-task operation failed (run Aegis as administrator)", "タスク操作に失敗（管理者で Aegis を実行してください）" } },

            { "tray.open", new[]{ "打开面板", "Open panel", "パネルを開く" } },
            { "btn.panic", new[]{ "一键恢复已记录项", "Restore recorded changes", "記録済み変更を復元" } },
            { "tray.exit", new[]{ "退出", "Exit", "終了" } },
            { "tray.active", new[]{ "Aegis - 守护中（{0}）", "Aegis - guarding ({0})", "Aegis - 稼働中（{0}）" } },
            { "tray.idle", new[]{ "Aegis - 待命，未检测到游戏", "Aegis - standby, no game detected", "Aegis - 待機中、ゲーム未検出" } },
            { "tray.noelev", new[]{ "Aegis - 未提权！压制类功能无效", "Aegis - not elevated! suppression disabled", "Aegis - 権限なし！抑制機能は無効" } },
            { "bal.noelev", new[]{ "当前未以管理员运行，压制类功能不会生效。", "Not running as administrator; suppression features are inactive.", "管理者で実行していないため、抑制機能は動作しません。" } },
            { "panic.done", new[]{ "已恢复能够确认的压制 / 提优 / 电源 / 网络 / 通知记录，Power Throttling 已交还系统管理。\r\n若游戏仍在运行，几秒后会重新进入压制；想彻底停用请关闭游戏模式与反作弊压制开关。", "Restored the recorded suppression, boost, power, network, and notification state that could be verified; Power Throttling was returned to system management.\r\nIf a game is still running they re-engage within seconds — turn off Game Mode / Anti-Cheat taming to stop them entirely.", "確認できた抑制 / 提優 / 電源 / ネット / 通知の記録を復元し Power Throttling はシステム管理へ戻しました。\r\nゲームが動作中なら数秒後に再適用されます。完全に止めるにはゲームモードとアンチチート抑制をオフにしてください。" } },
            { "panic.timeout", new[]{ "恢复流程等待超时。未确认还原的进程快照仍会保留并继续重试；如需彻底停用，请关闭游戏模式与反作弊压制开关。", "Restore timed out. Snapshots for unconfirmed processes were retained for retry; turn off Game Mode and Anti-Cheat taming to stop policy entirely.", "復元処理がタイムアウトしました。未確認のスナップショットは再試行用に保持されています。完全に停止するにはゲームモードとアンチチート抑制をオフにしてください。" } },
            { "rep.done", new[]{ "{0} 本局 {1}，压制 {2} 个后台进程，期间它们合计只用了 {3} CPU", "{0}: {1} played · {2} background processes throttled, together they used only {3} of CPU", "{0}：{1} プレイ・{2} 個を抑制、合計 CPU 使用は {3} のみ" } },
            { "rep.top", new[]{ "，占用最高 {0}（{1}）", "; top consumer: {0} ({1})", "、最多は {0}（{1}）" } },
            { "rep.freeze", new[]{ "，冻结 {0} 个持续高干扰后台", "; froze {0} sustained high-impact apps", "、高負荷背景 {0} 件を凍結" } },

            { "ac.ace.n", new[]{ "ACE 反作弊专家 (腾讯)", "ACE Anti-Cheat (Tencent)", "ACE アンチチート (Tencent)" } },
            { "ac.ace.d", new[]{ "英雄联盟国服 / 无畏契约国服 / 三角洲行动 / CF 等", "LoL & Valorant (CN) / Delta Force / CrossFire etc.", "LoL・Valorant(中国) / Delta Force / CF など" } },
            { "ac.tp.n", new[]{ "TenProtect / TP (腾讯)", "TenProtect / TP (Tencent)", "TenProtect / TP (Tencent)" } },
            { "ac.tp.d", new[]{ "ACE 前身，穿越火线等老游戏仍在用", "Predecessor of ACE, still used by CrossFire etc.", "ACE の前身、CrossFire などで現役" } },
            { "ac.vanguard.n", new[]{ "Vanguard (Riot)", "Vanguard (Riot)", "Vanguard (Riot)" } },
            { "ac.vanguard.d", new[]{ "无畏契约 / 英雄联盟 · vgk 为内核驱动无法压制", "Valorant / LoL · vgk is a kernel driver (can't throttle)", "Valorant / LoL・vgk はカーネルドライバで不可" } },
            { "ac.eac.n", new[]{ "EasyAntiCheat (Epic)", "EasyAntiCheat (Epic)", "EasyAntiCheat (Epic)" } },
            { "ac.eac.d", new[]{ "俗称「小蓝熊」· Apex / 堡垒之夜 / 永劫无间 / 幻兽帕鲁 等", "Apex / Fortnite / Naraka / Palworld etc.", "Apex / Fortnite / Naraka / パルワールド など" } },
            { "ac.battleye.n", new[]{ "BattlEye", "BattlEye", "BattlEye" } },
            { "ac.battleye.d", new[]{ "PUBG / 彩虹六号 / DayZ / 逃离塔科夫 · BEDaisy 为驱动", "PUBG / Rainbow Six / DayZ / Tarkov · BEDaisy is a driver", "PUBG / R6 / DayZ / タルコフ・BEDaisy はドライバ" } },
            { "ac.eaac.n", new[]{ "EA Javelin (EA 反作弊)", "EA Javelin (EA AntiCheat)", "EA Javelin (EA アンチチート)" } },
            { "ac.eaac.d", new[]{ "战地 2042 / 战地 6 / FC 等 EA 游戏 · 另有内核驱动", "Battlefield 2042/6 / FC etc. · also has a kernel driver", "Battlefield 2042/6 / FC など・カーネルドライバもあり" } },
            { "ac.gameguard.n", new[]{ "nProtect GameGuard", "nProtect GameGuard", "nProtect GameGuard" } },
            { "ac.gameguard.d", new[]{ "DNF 等部分韩系网游", "DNF and some Korean MMOs", "DNF など一部の韓国系オンラインゲーム" } },
            { "ac.faceit.n", new[]{ "FACEIT 反作弊", "FACEIT AC", "FACEIT アンチチート" } },
            { "ac.faceit.d", new[]{ "CS2 第三方竞技平台", "CS2 third-party platform", "CS2 サードパーティ対戦プラットフォーム" } },
            { "ac.neac.n", new[]{ "NEAC 反作弊 (网易)", "NEAC (NetEase)", "NEAC (NetEase)" } },
            { "ac.neac.d", new[]{ "部分网易游戏 · 另有内核组件", "some NetEase games · also has kernel parts", "一部の NetEase ゲーム・カーネル部品もあり" } },
        };

        public static void Init()
        {
            string v = Settings.LoadStr("Lang", "");
            if (v == "en") Cur = 1;
            else if (v == "ja") Cur = 2;
            else if (v == "zh") Cur = 0;
            else
            {
                string n = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                Cur = n == "ja" ? 2 : (n == "zh" ? 0 : 1);
            }
        }

        public static void Set(int i)
        {
            Cur = i < 0 ? 0 : (i > 2 ? 2 : i);
            Settings.SaveStr("Lang", Cur == 1 ? "en" : (Cur == 2 ? "ja" : "zh"));
        }

        public static string T(string key)
        {
            string[] a;
            if (M.TryGetValue(key, out a)) return a[Cur < a.Length ? Cur : 0];
            return key;
        }

        public static string F(string key, params object[] args) { return string.Format(T(key), args); }
    }


}
