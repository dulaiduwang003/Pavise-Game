using System.Collections.Generic;

namespace PaviseApp
{
	internal static class Lang
	{
		public static int Cur;

		internal static readonly object ChangeGate = new object();

		private static readonly Dictionary<string, string[]> M = new Dictionary<string, string[]>
		{
            { "background.governance.title", new[] { "后台治理", "Background governance", "バックグラウンド制御" } },
            { "background.governance.mode", new[] { "后台治理档位", "Background governance level", "バックグラウンド制御の段階" } },
            { "background.governance.off", new[] { "关闭", "Off", "無効" } },
            { "background.governance.normal", new[] { "正常", "Normal", "通常" } },
            { "background.governance.aggressive", new[] { "激进", "Aggressive", "積極的" } },
            { "background.governance.status.off", new[] { "后台治理已关闭", "Background governance is off", "バックグラウンド制御は無効です" } },
            { "background.governance.status.normal", new[] { "正常治理", "Normal governance", "通常の制御" } },
            { "background.governance.status.aggressive", new[] { "激进治理（含动态提优限制）", "Aggressive governance (including priority-boost limits)", "積極的な制御（一時的な優先度上昇も抑制）" } },
            { "background.governance.summary", new[] { "正常减少后台争用；激进再禁止后台动态提优", "Normal reduces contention; Aggressive also blocks background priority boosts", "通常は競合を軽減、積極的はバックグラウンドの優先度上昇も抑制" } },
            { "background.governance.detail", new[] { "正常减少后台争用；激进额外禁止 Windows 临时提升后台线程优先级，后台响应可能稍慢。\n智能切出释放，竞技持续治理；游戏和白名单受保护，停用或退局恢复。", "Normal reduces background contention. Aggressive also stops Windows from temporarily boosting background threads, so background apps may respond a little slower.\nSmart releases governance when switching out of a game; Competitive keeps it active. Games and whitelisted apps stay protected. Turning governance off or leaving the game restores owned changes.", "通常はバックグラウンドの競合を減らし、積極的ではWindowsによるバックグラウンドスレッドの一時的な優先度上昇も止めるため、応答が少し遅くなる場合があります。\nスマートはゲームから切り替えると解除し、競技は制御を継続。ゲームと許可リストは保護し、無効化・ゲーム終了で元に戻します。" } },
			{ "configpreset.label", new string[3] { "预设", "Preset", "プリセット" } },
			{ "configpreset.default", new string[3] { "作者同款", "Author’s setup", "作者の設定" } },
			{ "configpreset.extreme", new string[3] { "极限配置", "Extreme setup", "極限設定" } },
			{ "configpreset.guide", new string[3] { "一键配置", "One-click setup", "ワンクリック設定" } },
			{ "configpreset.guide.intro", new string[3] { "选择一套配置，点击即可应用。仅设置本机支持的项目。", "Choose a setup to apply it. Only settings supported by this PC are applied.", "設定を選ぶと適用します。このPCで対応する項目のみ設定します。" } },
			{ "configpreset.guide.author.body", new string[3] { "作者当前使用的游戏配置，兼顾日常使用与游戏体验。应用后需重启电脑。", "The author's current gaming setup, balancing everyday use and gaming. Restart the PC after applying.", "作者が現在使用している、普段使いとゲーム体験を両立する設定。適用後はPCを再起動してください。" } },
			{ "configpreset.guide.extreme.body", new string[3] { "采用更积极的性能设置，包含 GPU 频率与功耗上限配置。应用后需重启电脑。", "More aggressive performance settings, including maximum GPU clock and power-limit requests. Restart the PC after applying.", "GPUの最大クロックと電力上限の要求を含む、より積極的な性能設定。適用後はPCを再起動してください。" } },
			{ "configpreset.guide.recommended", new string[3] { "推荐", "Recommended", "おすすめ" } },
			{ "configpreset.guide.aggressive", new string[3] { "激进", "Aggressive", "積極的" } },
			{ "configpreset.guide.current", new string[3] { "当前配置", "Current setup", "現在の設定" } },
			{ "configpreset.guide.action", new string[3] { "点击应用", "Click to apply", "クリックして適用" } },
			{ "configpreset.guide.working", new string[3] { "正在应用配置，请稍候…", "Applying settings…", "設定を適用中…" } },
			{ "configpreset.guide.close", new string[3] { "关闭", "Close", "閉じる" } },
			{ "configpreset.custom", new string[3] { "自定义", "Custom", "カスタム" } },
			{ "configpreset.current", new string[3] { "当前：", "Current: ", "現在：" } },
			{ "configpreset.selected", new string[3] { "待应用：", "Selected: ", "適用候補：" } },
			{ "configpreset.choose", new string[3] { "选择配置预设", "Choose configuration preset", "構成プリセットを選択" } },
			{ "configpreset.apply", new string[3] { "应用", "Apply", "適用" } },
			{ "configpreset.applyfull", new string[3] { "应用所选配置预设", "Apply selected configuration preset", "選択したプリセットを適用" } },
			{ "configpreset.working", new string[3] { "应用中", "Applying", "適用中" } },
			{ "configpreset.description", new string[3] { "批量设置性能功能；不改变智能/电竞策略。仅应用本机支持的项目，不适用项自动跳过。", "Configures performance features without changing Smart/Esports. Only supported items are applied; ineligible items are skipped.", "スマート／eスポーツ方針を変えず性能機能を一括設定します。対応する項目のみ適用し、非対応項目はスキップします。" } },
			{ "configpreset.result", new string[3] { "预设应用结果", "Preset results", "プリセット適用結果" } },
			{ "configpreset.status.applied", new string[3] { "已设置", "Configured", "設定済み" } },
			{ "configpreset.status.unchanged", new string[3] { "保持", "Unchanged", "変更なし" } },
			{ "configpreset.status.skipped", new string[3] { "已跳过", "Skipped", "スキップ" } },
			{ "configpreset.status.failed", new string[3] { "未完成", "Incomplete", "未完了" } },
			{ "configpreset.busy", new string[3] { "当前有设置正在处理，请稍后重试。", "Settings are being processed. Try again shortly.", "設定処理中です。しばらくしてから再試行してください。" } },
			{ "configpreset.restart", new string[3] { "需重启后生效", "Restart required", "再起動後に反映" } },
			{
				"auto.framecoord",
				new string[3] { "渲染关键线程提优", "Render thread boost", "描画重要スレッドの優先度" }
			},
			{
				"auto.threadboost",
				new string[3] { "游戏线程提优", "Game thread boost", "ゲームスレッド優先度" }
			},
			{
				"autoignore.title",
				new string[3] { "自动入库忽略名单", "Automatic detection ignore list", "自動登録の除外リスト" }
			},
			{
				"autoignore.close",
				new string[3] { "关闭", "Close", "閉じる" }
			},
			{
				"autoignore.desc",
				new string[3] { "手动移除的游戏会留在这里。选择一条并移除，即可恢复自动识别；需开启自动入库。", "Manually removed games stay here. Remove an entry to allow automatic detection again. Automatic adding must be enabled.", "手動削除したゲームはここに記録されます。項目を除外から外すと再び自動検出できます。自動登録を有効にしてください。" }
			},
			{
				"autoignore.empty",
				new string[3] { "没有忽略记录", "No ignored entries", "除外された項目はありません" }
			},
			{
				"autoignore.emptyhint",
				new string[3] { "当前没有游戏被排除在自动识别之外。", "No games are currently excluded from automatic detection.", "自動検出から除外されたゲームはありません。" }
			},
			{
				"autoignore.select",
				new string[3] { "选择一款游戏，查看完整路径并恢复识别。", "Select a game to view its full path and allow detection again.", "ゲームを選択するとパスの確認と除外の解除ができます。" }
			},
			{
				"autoignore.path",
				new string[3] { "程序路径", "Executable path", "実行ファイルのパス" }
			},
			{
				"autoignore.resume",
				new string[3] { "移除并恢复识别", "Allow detection again", "除外を解除" }
			},
			{
				"autoignore.busy",
				new string[3] { "正在保存…", "Saving…", "保存中…" }
			},
			{
				"autoignore.failed",
				new string[3] { "未能保存，忽略记录已保留，请重试。", "Could not save. The ignored entry was retained. Please retry.", "保存できませんでした。除外記録は保持されています。再試行してください。" }
			},
			{
				"autoignore.removed",
				new string[3] { "已恢复自动识别；开启自动入库后生效。", "Automatic detection allowed again. Enable automatic adding to use it.", "自動検出を再開できます。自動登録を有効にしてください。" }
			},
			{
				"autoignore.count",
				new string[3] { "已忽略 {0} 项", "{0} ignored entries", "除外 {0} 件" }
			},
			{
				"background.scope",
				new string[3] { "后台范围：可纳入的独立服务 {0} 个；普通后台禁用动态优先级提升策略：{1}", "Background scope: {0} eligible dedicated services; policy disables ordinary background dynamic priority boosts: {1}", "対象の独立サービス：{0} 件、通常のバックグラウンドで動的優先度ブーストを無効にする方針：{1}" }
			},
			{
				"cpucap.title",
				new string[3] { "已下架的 CPU 限额记录", "Retired CPU cap records", "廃止されたCPU制限の記録" }
			},
			{
				"schedule.quick.all",
				new string[3] { "全选", "All cores", "すべて選択" }
			},
			{
				"schedule.quick.perf",
				new string[3] { "只选大核", "P-cores only", "Pコアのみ" }
			},
			{
				"schedule.quick.eff",
				new string[3] { "只选小核", "E-cores only", "Eコアのみ" }
			},
			{
				"schedule.quick.nosmt",
				new string[3] { "去除超线程", "One thread per core", "各コア1スレッド" }
			},
			{
				"schedule.quick.smt",
				new string[3] { "补全超线程", "Include sibling threads", "同一コアのスレッドを追加" }
			},
			{
				"schedule.manual.required",
				new string[3] { "尚未配置，当前由系统调度。选择至少一个逻辑核心后自动保存，再开启右上角的核心调度开关。", "Using system scheduling. Select at least one logical CPU to save automatically, then enable scheduling at the top right.", "現在はシステムが管理しています。論理CPUを1つ以上選ぶと自動保存されます。その後、右上のスイッチを有効にしてください。" }
			},
			{
				"schedule.manual.pickgame",
				new string[3] { "请从游戏库打开某个游戏的核心调度，按游戏保存手动配置。", "Open Core Scheduling for a game in the library to save its manual configuration.", "ライブラリでゲームのコア設定を開き、手動設定を保存してください。" }
			},
			{
				"schedule.manual.mode",
				new string[3] { "手动", "Manual", "手動" }
			},
			{
				"schedule.manual.selected",
				new string[3] { "已选", "Selected", "選択済み" }
			},
			{
				"schedule.manual.select",
				new string[3] { "选择游戏核心", "Select game cores", "ゲームコアを選択" }
			},
			{
				"schedule.manual.game",
				new string[3] { "{0} · 核心调度", "{0} · Core scheduling", "{0} · コアスケジューリング" }
			},
			{
				"schedule.manual.isolation",
				new string[3] { "核心隔离 · 为系统保留至少两个物理核心", "Core isolation · Leave at least two physical cores for the system", "コア分離 · システム用に物理コアを2つ以上残す" }
			},
			{
				"schedule.manual.help",
				new string[3] { "点击数字选择游戏可用的逻辑核心，点击 C 编号整组选择。隔离会覆盖所选核心的同组线程，范围外须保留两颗完整物理核心；CPU 0 所在核心也可选择。方向键移动，空格选择。", "Click a number to select a logical CPU, or a C header to select a whole core. Isolation includes sibling threads and must leave two whole physical cores outside. CPU 0's core can also be selected. Arrow keys move; Space selects.", "数字で論理CPU、C番号で物理コア全体を選択。分離は同一コアのスレッドを含み、対象外に物理コアを2つ残す必要があります。CPU 0のコアも選択できます。矢印キーで移動、スペースで選択。" }
			},
			{
				"schedule.manual.autosaved",
				new string[3] { "配置自动保存。游戏只使用所选逻辑核心，通过进程硬亲和性执行；本局修改下次启动游戏生效。", "Saved automatically. Process hard affinity restricts the game to the selected logical CPUs. In-session changes apply on the next game launch.", "自動保存。プロセスのハードアフィニティでゲームを選択した論理CPUに限定します。セッション中の変更は次回起動時に適用されます。" }
			},
			{
				"schedule.manual.savedoff",
				new string[3] { "配置已自动保存 · 核心调度未开启。开启右上角开关后使用；核心选择与隔离的修改在下次启动游戏时生效。", "Configuration saved automatically · Scheduling is off. Enable it at the top right; core and isolation changes apply next launch.", "自動保存済み · スケジューリングはオフです。右上で有効にしてください。コア選択と分離の変更は次回起動時に適用します。" }
			},
			{
				"schedule.manual.unsaved",
				new string[3] { "当前选择未保存：{0} 调整为有效选择后自动保存；离开页面将丢弃未保存的选择。", "Selection not saved: {0} A valid selection saves automatically; leaving this page discards unsaved choices.", "現在の選択は未保存です：{0} 有効な選択になると自動保存されます。画面を離れると未保存の選択は破棄されます。" }
			},
			{
				"schedule.manual.invalid",
				new string[3] { "已保存的配置当前不可用：{0}", "The saved configuration is currently unavailable: {0}", "保存済みの設定は現在使用できません：{0}" }
			},
			{
				"schedule.manual.autofailed",
				new string[3] { "本次修改未保存：{0} 请重新操作。", "This change was not saved: {0} Please try the change again.", "今回の変更は保存されませんでした：{0} もう一度操作してください。" }
			},
			{
				"schedule.manual.topology",
				new string[3] { "CPU 拓扑已变化或无法确认，请重新选择核心。", "CPU topology changed or could not be verified. Select the cores again.", "CPU構成が変化したか確認できません。コアを再選択してください。" }
			},
			{
				"schedule.manual.minimum",
				new string[3] { "至少选择一个有效的逻辑核心。", "Select at least one valid logical CPU.", "有効な論理CPUを1つ以上選択してください。" }
			},
			{
				"schedule.manual.allocated",
				new string[3] { "系统已有其他核心保留。手动隔离需先释放已有保留；仅设置亲和性时请避开被占用的核心。", "Other cores are already reserved. Release existing reservations before manual isolation; for affinity only, avoid reserved cores.", "他のコア予約があります。手動分離の前に既存の予約を解除してください。アフィニティのみの場合は予約済みコアを避けてください。" }
			},
			{
				"schedule.manual.spare",
				new string[3] { "隔离后须为系统留下至少两个可用物理核心，请减少选择。", "Leave at least two available physical cores outside isolation. Select fewer cores.", "分離の対象外に物理コアを2つ以上残してください。" }
			},
			{
				"schedule.manual.savefailed",
				new string[3] { "配置保存失败，请重试。", "Could not save the configuration. Please retry.", "設定を保存できませんでした。再試行してください。" }
			},
			{
				"schedule.manual.changed",
				new string[3] { "该游戏配置已被修改或移除，已同步最新配置。", "This game's configuration changed or was removed. The latest configuration has been loaded.", "ゲーム設定が変更または削除されたため、最新の設定を読み込みました。" }
			},
			{
				"schedule.manual.rule",
				new string[3] { "手动配置在本局保持固定，不进行自动扩核或学习；退出游戏后恢复。", "Manual selection stays fixed for this session, with no automatic expansion or learning. Restored when the game exits.", "このセッション中は手動設定を固定し、自動拡張や学習は行いません。終了時に復元します。" }
			},
			{
				"schedule.manual.plan",
				new string[3] { "手动游戏核心：{0}", "Manual game CPUs: {0}", "手動ゲームCPU：{0}" }
			},
			{
				"workflow.more",
				new string[3] { "更多操作", "More actions", "その他の操作" }
			},
			{
				"auto.nvshader",
				new string[3] { "NVIDIA 着色器缓存无上限", "NVIDIA unlimited shader cache", "NVIDIA シェーダーキャッシュ無制限" }
			},
			{
				"auto.fso",
				new string[3] { "禁用全屏优化", "Disable fullscreen optimizations", "全画面最適化を無効化" }
			},
			{
				"auto.fso.configured",
				new string[3] { "已配置 · 下次启动生效", "Set for next launch", "設定済み・次回起動時に適用" }
			},
			{
				"workflow.game.waiting",
				new string[3] { "等待游戏启动", "Waiting for a game", "ゲームの起動を待機中" }
			},
			{
				"workflow.attention.action.logs",
				new string[3] { "查看日志", "View logs", "ログを見る" }
			},
			{
				"irqcheck.btn.verify",
				new string[2] { "开始验收", "Verify now" }
			},
			{
				"irqcheck.hint.none",
				new string[2] { "还没有检测过。", "Not checked yet." }
			},
			{
				"irqcheck.hint.pending",
				new string[2] { "已安排调整，重启电脑后生效；重启后在系统体检中完成验收。", "An adjustment is scheduled and takes effect after a restart; verify it in System Checkup afterwards." }
			},
			{
				"irqcheck.hint.verify",
				new string[2] { "已重启，点「开始验收」确认调整生效。", "Restarted. Click Verify now to confirm the adjustment took effect." }
			},
			{
				"irqcheck.hint.applied",
				new string[2] { "调整已验收，显卡中断落在 CPU {0}。", "Verified: the GPU interrupt lands on CPU {0}." }
			},
			{
				"irqcheck.hint.clear",
				new string[2] { "上次检测时显卡中断没有碰到游戏核心，无需调整。", "At the last check the GPU interrupt stayed off the game cores; nothing to change." }
			},
			{
				"irqcheck.hint.noregion",
				new string[2] { "未获取到生效的游戏核心范围，暂不判断是否需要避让显卡中断。", "No active game core range was obtained; GPU interrupt avoidance cannot be assessed yet." }
			},
			{
				"irqcheck.hint.rolledback",
				new string[2] { "上次调整没有通过验收，已恢复：{0}", "The last adjustment failed verification and was reverted: {0}" }
			},
			{
				"irqcheck.hint.skipped",
				new string[2] { "上次检测没有调整：{0}", "No adjustment at the last check: {0}" }
			},
			{
				"irqcheck.gpu.title",
				new string[2] { "显卡中断", "GPU interrupt" }
			},
			{
				"irqcheck.state.none",
				new string[2] { "未检测", "Not checked" }
			},
			{
				"irqcheck.state.pending",
				new string[2] { "已安排，重启后生效", "Scheduled, applies after restart" }
			},
			{
				"irqcheck.state.verify",
				new string[2] { "等待验收", "Awaiting verification" }
			},
			{
				"irqcheck.state.applied",
				new string[2] { "已调整并验收", "Adjusted and verified" }
			},
			{
				"irqcheck.state.clear",
				new string[2] { "未碰到游戏核心", "Clear of the game cores" }
			},
			{
				"irqcheck.state.noregion",
				new string[2] { "未获取游戏核心范围", "Game core range unavailable" }
			},
			{
				"irqcheck.state.rolledback",
				new string[2] { "验收未通过，已恢复", "Failed verification, reverted" }
			},
			{
				"irqcheck.slow.title",
				new string[2] { "驱动延迟", "Driver latency" }
			},
			{
				"irqcheck.slow.row",
				new string[2] { "最长 {0} μs · p99 {1} μs", "max {0} μs · p99 {1} μs" }
			},
			{
				"irqcheck.slow.hint",
				new string[2] { "慢驱动只做提示，不做修改；建议更新或更换驱动版本。", "Reported only, never changed; consider a different driver version." }
			},
			{
				"irqcheck.restore.btn",
				new string[2] { "恢复系统默认", "Restore system defaults" }
			},
			{
				"irqcheck.restore.hint",
				new string[2] { "撤销 Pavise 做过的全部中断调整，重启后生效。", "Undo every interrupt adjustment Pavise made; takes effect after a restart." }
			},
			{
				"irqcheck.restore.confirm",
				new string[2] { "撤销 Pavise 做过的全部中断调整？\r\n\r\n需要重启电脑才会生效。", "Undo every interrupt adjustment Pavise made?\r\n\r\nA restart is needed for it to take effect." }
			},
			{
				"irqcheck.restore.done",
				new string[2] { "已恢复，重启电脑后生效。", "Restored; takes effect after a restart." }
			},
			{
				"irqcheck.restore.failed",
				new string[2] { "恢复没有完全成功，详见日志；可以稍后再试一次。", "Restoring did not fully succeed; see the log and try again later." }
			},
			{
				"irqcheck.restorefailed",
				new string[2] { "恢复没有完全成功，可以在系统体检的显卡中断配置项中重试还原", "Restoring did not fully succeed; retry restoring the GPU interrupt configuration in System Checkup" }
			},
			{
				"irqcheck.needadmin",
				new string[2] { "需要以管理员身份运行 Pavise 才能检测。", "Pavise must run as administrator to check." }
			},
			{
				"irqcheck.ingame",
				new string[2] { "请先退出游戏再检测。", "Exit the game before checking." }
			},
			{
				"irqcheck.lossy",
				new string[2] { "测量期间丢了事件，结果不可信，请再试一次。", "Events were lost during the measurement, so the result is unreliable; please try again." }
			},
			{
				"irqcheck.dialog.title",
				new string[2] { "显卡中断检测", "GPU interrupt check" }
			},
			{
				"irqcheck.dialog.hint",
				new string[2] { "正在测量显卡中断。请不要操作电脑，保持屏幕点亮，约 {0} 秒后自动结束。", "Measuring GPU interrupts. Leave the PC alone and keep the screen on; this ends by itself in about {0} seconds." }
			},
			{
				"irqcheck.dialog.caption",
				new string[2] { "剩余 {0} 秒", "{0} s left" }
			},
			{
				"irqcheck.dialog.analyzing",
				new string[2] { "正在分析…", "Analyzing…" }
			},
			{
				"irqcheck.dialog.stopping",
				new string[2] { "正在停止…", "Stopping…" }
			},
			{
				"irqcheck.out.failed",
				new string[2] { "检测没有完成：{0}", "The check did not complete: {0}" }
			},
			{
				"irqcheck.out.cancelled",
				new string[2] { "已取消，没有做任何修改。", "Cancelled; nothing was changed." }
			},
			{
				"irqcheck.out.awaiting",
				new string[2] { "上次安排的调整还没生效，重启电脑后再来这里完成验收。", "The scheduled adjustment has not taken effect yet; restart the computer, then come back here to verify it." }
			},
			{
				"irqcheck.out.noevidence",
				new string[2] { "这次只测到 {0} 次显卡中断，至少需要 {1} 次才能判断。请保持屏幕点亮、不要锁屏，稍后再试一次。", "Only {0} GPU interrupts were measured; at least {1} are needed. Keep the screen on and unlocked, then try again." }
			},
			{
				"irqcheck.out.noregion",
				new string[2] { "未获取到生效的游戏核心范围，本次仅展示显卡中断数据，暂不判断是否需要避让。", "No active game core range was obtained. This check only shows GPU interrupt data; whether avoidance is needed remains undetermined." }
			},
			{
				"irqcheck.out.restored",
				new string[2] { "游戏已不再使用专用核心，之前的显卡中断调整已撤销，重启后生效。", "The game no longer uses dedicated cores, so the earlier GPU interrupt adjustment was undone; takes effect after a restart." }
			},
			{
				"irqcheck.out.clear",
				new string[2] { "显卡中断落在 CPU {0}，没有碰到游戏核心 CPU {1}，无需调整。", "The GPU interrupt lands on CPU {0}, clear of the game cores CPU {1}; nothing to change." }
			},
			{
				"irqcheck.out.scheduled",
				new string[2] { "显卡中断落在 CPU {0}，和游戏核心 CPU {1} 重叠。\r\n\r\n已安排挪到 CPU {2}，重启电脑后生效。重启后在系统体检中完成验收；验收不通过会自动恢复。", "The GPU interrupt lands on CPU {0}, overlapping the game cores CPU {1}.\r\n\r\nIt is scheduled to move to CPU {2} and takes effect after a restart. Run System Checkup afterwards to verify; a failed verification is reverted automatically." }
			},
			{
				"irqcheck.out.verified",
				new string[2] { "验收通过：显卡中断已落在 CPU {0}，驱动没有异常。", "Verified: the GPU interrupt now lands on CPU {0} and the driver shows no problems." }
			},
			{
				"irqcheck.out.rolledback",
				new string[2] { "验收没有通过（{0}），已恢复原设置，重启后生效。这块显卡在当前驱动版本上不再尝试。", "Verification failed ({0}); the original setting was restored and applies after a restart. This GPU will not be tried again on the current driver version." }
			},
			{
				"irqcheck.out.skipped",
				new string[2] { "不做调整：{0}", "No adjustment: {0}" }
			},
			{
				"irqcheck.out.slow",
				new string[2] { "发现慢驱动（只提示，不修改）：", "Slow drivers found (reported only, never changed):" }
			},
			{
				"irqcheck.out.noslow",
				new string[2] { "没有发现慢驱动。", "No slow drivers found." }
			},
			{
				"irqcheck.reason.config",
				new string[2] { "显卡的中断配置读不出来", "the GPU interrupt configuration cannot be read" }
			},
			{
				"irqcheck.reason.multimessage",
				new string[2] { "这块显卡使用多路中断，固定核心会损失并行", "this GPU uses multiple interrupt messages; pinning would cost parallelism" }
			},
			{
				"irqcheck.reason.externalpin",
				new string[2] { "显卡中断已被其他设置固定，Pavise 不接管", "the GPU interrupt is already pinned by another setting; Pavise leaves it alone" }
			},
			{
				"irqcheck.reason.fused",
				new string[2] { "此前在当前驱动版本上验收没有通过", "a previous attempt failed verification on this driver version" }
			},
			{
				"irqcheck.reason.notarget",
				new string[2] { "游戏核心之外没有可用的性能核，不做调整。", "No performance cores are available outside the game cores; nothing is changed." }
			},
			{
				"irqcheck.reason.reset",
				new string[2] { "重启后出现过显卡驱动重置", "the display driver was reset after the restart" }
			},
			{
				"irqcheck.reason.ignored",
				new string[2] { "驱动没有按设置的核心投递中断", "the driver did not deliver interrupts to the configured cores" }
			},
			{
				"irqcheck.reason.slower",
				new string[2] { "显卡 DPC 耗时明显变长", "GPU DPC times got clearly longer" }
			},
			{
				"irqcheck.purge",
				new string[2] { "显卡中断自动调整", "Automatic GPU interrupt adjustment" }
			},
			{
				"log.irqcheck.scheduled",
				new string[2] { "显卡中断已安排调整 {0}：CPU {1} → CPU {2}，重启后生效", "GPU interrupt adjustment scheduled for {0}: CPU {1} → CPU {2}, takes effect after a restart" }
			},
			{
				"log.irqcheck.verified",
				new string[2] { "显卡中断验收通过 {0}：落在 CPU {1}", "GPU interrupt verified for {0}: lands on CPU {1}" }
			},
			{
				"log.irqcheck.rolledback",
				new string[2] { "显卡中断验收未通过，已恢复 {0}：{1}", "GPU interrupt failed verification and was reverted for {0}: {1}" }
			},
			{
				"irq.exact.unsupported",
				new string[2] { "精确选核不可用：仅支持单处理器组及有效掩码；未写入替代策略。", "Exact pinning requires one processor group and a valid mask; no substitute policy is written." }
			},
			{
				"irq.write.journalfailed",
				new string[2] { "亲和收据保存失败，已尝试回滚。", "Affinity receipt save failed; rollback attempted." }
			},
			{
				"irq.write.detail",
				new string[2] { "亲和成功 {0}，失败 {1}，已回滚 {2}，回滚失败 {3}", "Affinity: {0} succeeded, {1} failed, {2} rolled back, {3} rollback failures" }
			},
			{
				"irq.restore.result",
				new string[2] { "恢复亲和：{0}；恢复优先级：{1}", "Restore affinity: {0}; restore priority: {1}" }
			},
			{
				"irq.step.ok",
				new string[2] { "成功", "succeeded" }
			},
			{
				"irq.step.failed",
				new string[2] { "失败／未完成", "failed / incomplete" }
			},
			{
				"irq.step.skipped",
				new string[2] { "未请求", "not requested" }
			},
			{
				"irq.restore.readfailed",
				new string[2] { "恢复收据读取失败，本次未执行设备恢复。", "Restore receipts could not be read; no device restore was attempted." }
			},
			{
				"nav.corescheduling",
				new string[3] { "核心调度", "Core Scheduling", "コアスケジューリング" }
			},
			{
				"schedule.page.sub",
				new string[3] { "游戏核心与隔离按游戏手动配置，所有模式均可使用。", "Configure game cores and isolation per game, available in every mode.", "ゲームごとにコアと分離を手動設定できます。全モードで利用可能です。" }
			},
			{
				"schedule.auto.unsupported",
				new string[2] { "当前处理器组或进程位数无法安全表示完整核心拓扑，本次不落核。", "The processor groups or process bitness cannot safely represent the complete topology; no placement applied." }
			},
			{
				"schedule.auto.identity",
				new string[3] { "无法确认当前游戏进程身份，本局保持共享。", "The current game process identity could not be verified; cores stay shared for this session.", "現在のゲームプロセスの識別情報を確認できません。このセッションは共有を維持します。" }
			},
			{
				"schedule.auto.capture",
				new string[3] { "无法保存落核前的状态，本局保持共享。", "The original placement state could not be saved; cores stay shared for this session.", "配置変更前の状態を保存できません。このセッションは共有を維持します。" }
			},
			{
				"schedule.auto.placementunavailable",
				new string[3] { "游戏核心分配无法确认，本局停止落核尝试，保持共享。", "Game core placement could not be verified; placement attempts stop and cores stay shared for this session.", "ゲームのコア配置を確認できません。このセッションでは配置の試行を停止し共有を維持します。" }
			},
			{
				"schedule.auto.failed",
				new string[3] { "系统隔离没有生效，已撤回，本局保持共享。", "System isolation did not take effect and was rolled back; cores stay shared for this session.", "システムの隔離が有効にならなかったため元に戻しました。このセッションは共有のままにします。" }
			},
			{
				"schedule.auto.pending",
				new string[3] { "上一次隔离的还原还没确认，本局不再独占；请查看运行日志。", "Restoring the previous isolation is not confirmed yet, so no cores are dedicated this session. See the run log.", "前回の隔離の復元が未確認のため、このセッションは専有しません。実行ログを確認してください。" }
			},
			{
				"schedule.auto.placement",
				new string[3] { "未能确认游戏亲和性，本局停止落核；具体阶段与错误码见运行日志。", "Game affinity could not be verified; placement stops for this session. See the run log for the stage and error code.", "ゲームのアフィニティを確認できません。実行ログで失敗した段階とエラーコードを確認してください。" }
			},
			{
				"schedule.auto.affinityrange",
				new string[3] { "候选核心超出游戏现有亲和性范围，未修改游戏设置。", "Candidate cores exceed the game's existing affinity; no placement was written.", "候補コアが既存のアフィニティ範囲外のため変更していません。" }
			},
			{
				"schedule.auto.affinitywrite",
				new string[3] { "系统拒绝写入游戏亲和性；具体错误码见运行日志。", "The system rejected the game affinity write; see the run log for the error code.", "ゲームのアフィニティ書き込みが拒否されました。実行ログでエラーコードを確認してください。" }
			},
			{
				"schedule.auto.affinityread",
				new string[3] { "无法读取游戏亲和性，尚不能确认落核状态；请查看运行日志。", "Game affinity could not be read; placement is unconfirmed. See the run log.", "アフィニティを読み取れず配置を確認できません。実行ログを確認してください。" }
			},
			{
				"schedule.auto.restorehandle",
				new string[3] { "无法保留游戏恢复句柄，未写入亲和性。", "The restore handle could not be retained; no affinity was written.", "復元用ハンドルを保持できなかったためアフィニティを変更していません。" }
			},
			{
				"schedule.auto.external",
				new string[3] { "游戏核心相关性被改写，本局硬绑定已停用。隔离按原记录恢复，保留外部修改。", "Game affinity was changed externally. Hard binding is stopped for this session; isolation restores from its records while preserving external changes.", "ゲームのアフィニティが外部で変更されたため、このセッションの固定を停止しました。外部変更を保持して分離を復元します。" }
			},
			{
				"schedule.auto.state.blocked",
				new string[3] { "本局保持共享", "Shared for this session", "このセッションは共有" }
			},
			{
				"schedule.auto.allow",
				new string[3] { "开启核心调度", "Enable core scheduling", "コアスケジューリングを有効化" }
			},
			{
				"schedule.auto.live.game",
				new string[3] { "当前游戏", "Game", "ゲーム" }
			},
			{
				"schedule.auto.live.active",
				new string[2] { "活跃线程 / 可用核", "Busy threads / cores" }
			},
			{
				"schedule.auto.live.load",
				new string[3] { "线程负载", "Thread load", "スレッド負荷" }
			},
			{
				"schedule.auto.live.windows",
				new string[2] { "采样间隔", "Sample interval" }
			},
			{
				"schedule.auto.live.releases",
				new string[2] { "后台避让进程", "Background moved" }
			},
			{
				"schedule.auto.state.pending",
				new string[3] { "隔离还原未确认", "Isolation restore not confirmed", "隔離の復元が未確認" }
			},
			{
				"schedule.auto.reserved.suspect",
				new string[3] { "{0} 被内核保留核占着，数值和旧版 Pavise 的设置一致，但没有找到旧版的记录，也可能是你或其他工具设的。确认是旧版残留可以清除，重启后释放。", "{0} is held by a kernel core reservation that matches what an older Pavise set, but no record of it was found, so you or another tool may have set it. If it is left over from Pavise, clear it; the cores are released after a restart.", "{0} はカーネルのコア予約で使用中です。旧バージョンの Pavise の設定と一致しますが記録が見つからないため、ご自身や他のツールが設定した可能性もあります。Pavise の残りであれば削除でき、再起動後に解放されます。" }
			},
			{
				"schedule.auto.reserved.clear",
				new string[3] { "清除保留核", "Clear reserved cores", "予約コアを削除" }
			},
			{
				"schedule.auto.reserved.confirm",
				new string[3] { "将删除内核保留核设置（{0}），重启电脑后这些核心会释放给系统和游戏。只有确认它是旧版 Pavise 留下的才这样做。", "This deletes the kernel core reservation ({0}); after a restart the cores return to the system and games. Only do this if you are sure an older Pavise left it.", "カーネルのコア予約（{0}）を削除します。再起動後、これらのコアはシステムとゲームに戻ります。旧バージョンの Pavise が残したものだと確認できた場合のみ実行してください。" }
			},
			{
				"schedule.auto.reserved.failed",
				new string[3] { "删除失败，保留核设置没有变化。请查看运行日志。", "Could not delete it; the reservation is unchanged. See the run log.", "削除できませんでした。予約設定は変更されていません。実行ログを確認してください。" }
			},
			{
				"log.autocore.legacycorrupt",
				new string[3] { "旧版内核保留核的回执无法解析 当前值 {0} 未做改动", "The legacy kernel core reservation receipt could not be parsed; current value {0} left unchanged", "旧バージョンのカーネルコア予約の記録を解析できません。現在の値 {0} は変更していません" }
			},
			{
				"log.autocore.legacychanged",
				new string[3] { "内核保留核已被改成 {0} 不是旧版写入的值 未做改动", "The kernel core reservation was changed to {0}, which is not what the older version wrote; left unchanged", "カーネルのコア予約が {0} に変更されており、旧バージョンが書いた値ではないため変更していません" }
			},
			{
				"schedule.auto.reserved.legacy",
				new string[3] { "{0} 被旧版 Pavise 的内核保留核占着，设置已清除，重启后释放，独占核会多出这几颗。", "{0} is held by the kernel core reservation from an older Pavise; the setting is cleared and the cores are released after a restart, adding them to the exclusive cores.", "{0} は旧バージョンの Pavise によるカーネル予約で使用中です。設定は削除済みで、再起動後に解放され専有コアに加わります。" }
			},
			{
				"schedule.auto.reserved.other",
				new string[3] { "{0} 已被系统或其他工具保留，不参与独占。", "{0} is reserved by the system or another tool and is left out of the exclusive cores.", "{0} はシステムまたは他のツールが予約しているため、専有の対象外です。" }
			},
			{
				"log.autocore.unavailable",
				new string[3] { "自动核心独占 本局不用 {0}", "Auto exclusive cores not used this session: {0}", "自動コア専有はこのセッションで使用しません：{0}" }
			},
			{
				"log.autocore.allocated",
				new string[3] { "已被保留的核心 {0} 不参与独占", "Already reserved cores {0} are left out of the exclusive cores", "予約済みのコア {0} は専有の対象外" }
			},
			{
				"log.autocore.engaged",
				new string[3] { "核心独占已生效 {0} 游戏亲和性与系统隔离均已读回确认", "Exclusive cores in effect: {0}; game affinity and system isolation verified", "コア専有が有効：{0}。ゲームのアフィニティとシステムの隔離を確認済み" }
			},
			{
				"log.autocore.released",
				new string[3] { "核心独占已还原 系统隔离与游戏亲和性已恢复", "Exclusive cores released; system isolation and game affinity restored", "コア専有を解除。システムの隔離とゲームのアフィニティを復元" }
			},
			{
				"log.autocore.releasepending",
				new string[3] { "核心独占还原未完全确认 下次启动继续恢复", "Exclusive core release not fully confirmed; recovery continues at next launch", "コア専有の解除を完全には確認できません。次回起動時に復元を続行" }
			},
			{
				"log.autocore.blocked",
				new string[3] { "核心独占 本局停用 {0}", "Exclusive cores stopped for this session: {0}", "コア専有をこのセッションで停止：{0}" }
			},
			{
				"log.autocore.external",
				new string[3] { "其他程序改动了游戏亲和性 读回 0x{0} 已让出核心独占", "Another program changed the game's affinity (read back 0x{0}); exclusive cores withdrawn", "他のプログラムがゲームのアフィニティを変更（読み戻し 0x{0}）。コア専有を撤回" }
			},
			{
				"log.autocore.legacycleared",
				new string[3] { "已清除旧版内核保留核 {0} 重启后释放", "Cleared the legacy kernel core reservation {0}; released after restart", "旧バージョンのカーネルコア予約 {0} を削除。再起動後に解放" }
			},
			{
				"log.autocore.legacyfailed",
				new string[3] { "旧版内核保留核 {0} 清除失败 下次启动重试", "Could not clear the legacy kernel core reservation {0}; retrying at next launch", "旧バージョンのカーネルコア予約 {0} を削除できません。次回起動時に再試行" }
			},
			{
				"log.placement.unreadable",
				new string[3] { "暂时无法读取实际核心分配，当前状态未知。", "Actual core placement could not be read; its current state is unknown.", "実際のコア配置を読み取れないため、現在の状態は不明です。" }
			},
			{
				"log.placement.unconfirmed",
				new string[3] { "核心策略尚未完整确认。", "The core policy has not been fully verified.", "コアポリシーを完全には確認できていません。" }
			},
			{
				"log.placement.stopped",
				new string[3] { " 本局停止自动重试。", " Automatic retries stopped for this session.", " このセッションの自動再試行を停止しました。" }
			},
			{
				"schedule.isolation",
				new string[3] { "隔离范围", "Isolation range", "隔離範囲" }
			},
			{
				"schedule.isolation.idle",
				new string[3] { "隔离状态：当前未启用。", "Isolation: currently inactive.", "隔離：現在は未有効です。" }
			},
			{
				"schedule.isolation.active",
				new string[3] { "隔离状态：已生效，系统范围与游戏准入已读回确认。", "Isolation active; system range and game admission verified.", "隔離有効。システム範囲とゲームアクセスを確認済み。" }
			},
			{
				"schedule.isolation.failed",
				new string[3] { "隔离状态：未生效，已撤回隔离改动；请查看运行日志。", "Isolation inactive; isolation changes rolled back. See the runtime log.", "隔離は未有効。変更を復元しました。実行ログを確認してください。" }
			},
			{
				"schedule.isolation.pending",
				new string[3] { "隔离恢复尚未确认，请保留程序并查看日志；下次启动会继续恢复。", "Isolation restoration is unconfirmed. Keep the app and check the log; recovery retries at next launch.", "隔離の復元未確認。アプリを保持しログを確認。次回起動時に復元を再試行します。" }
			},
			{
				"gfx.common",
				new string[2] { "通用", "Common" }
			},
			{
				"gfx.common.sub",
				new string[2] { "Windows 应用显卡偏好", "Windows app graphics preferences" }
			},
			{
				"set.gfx.commononly",
				new string[2] { "厂商驱动接口不可用；通用页仍可管理 Windows 应用显卡偏好。", "Vendor driver APIs are unavailable. Windows app GPU preferences remain available on the Common tab." }
			},
			{
				"sec.intel",
				new string[2] { "Intel 驱动，全局会话设置", "Intel driver, global session settings" }
			},
			{
				"set.intel.none",
				new string[2] { "未检测到可用的 Intel 图形控制库。请安装适用的 Intel 显卡驱动。", "No supported Intel Graphics Control Library was found. Install the appropriate Intel graphics driver." }
			},
			{
				"gm.englishinput",
				new string[2] { "进入游戏切英文一次", "Switch to English once at game entry" }
			},
			{
				"log.englishinput.result",
				new string[2] { "进入游戏英文切换：{0} · {1}（代码 {2}）", "Game-entry English switch: {0} · {1} (code {2})" }
			},
			{
				"englishinput.changed",
				new string[2] { "已确认切为英文，本局不再干预", "English layout confirmed; no further intervention this session" }
			},
			{
				"englishinput.already",
				new string[2] { "已经是英文，本局不再干预", "Already using English; no further intervention this session" }
			},
			{
				"englishinput.unconfirmed",
				new string[2] { "请求已尝试但未确认切换，本局不重试", "Request attempted but the switch was not confirmed; no retry this session" }
			},
			{
				"englishinput.denied",
				new string[2] { "游戏拒绝或权限不足，本局不重试", "Request denied or blocked by permissions; no retry this session" }
			},
			{
				"englishinput.unavailable",
				new string[2] { "输入语言接口不可用，保留当前输入法", "Input-language interface unavailable; current input method preserved" }
			},
			{
				"englishinput.nolayout",
				new string[2] { "未找到可用的独立英文键盘布局；保留当前输入法，可在 Windows 语言设置添加", "No separate English keyboard layout is available. Current input method preserved; add one in Windows language settings" }
			},
			{
				"englishinput.targetchanged",
				new string[2] { "前台游戏已变化，取消本次切换", "Foreground game changed; switch canceled" }
			},
			{
				"englishinput.expired",
				new string[2] { "入场等待已超时，保留当前输入法", "Entry window expired; current input method preserved" }
			},
			{
				"englishinput.canceled",
				new string[2] { "设置或会话变化，取消本次切换", "Settings or session changed; switch canceled" }
			},
			{
				"englishinput.historyfull",
				new string[2] { "无法可靠去重本局，跳过切换", "Cannot safely deduplicate this session; switch skipped" }
			},
			{
				"set.apppref",
				new string[2] { "应用显卡偏好", "Application GPU preferences" }
			},
			{
				"apppref.nextlaunch",
				new string[2] { "下次启动生效 · 不移动或重启正在运行的应用", "Applies next launch · Running apps are not moved or restarted" }
			},
			{
				"nav.tame",
				new string[2] { "反作弊", "Anti-Cheat" }
			},
			{
				"nav.white",
				new string[3] { "白名单", "Whitelist", "ホワイトリスト" }
			},
			{
				"nav.log",
				new string[2] { "日志", "Logs" }
			},
			{
				"nav.set",
				new string[2] { "设置", "Settings" }
			},
			{
				"nav.about",
				new string[2] { "关于", "About" }
			},
			{
				"nav.overview",
				new string[2] { "概览", "Overview" }
			},
			{
				"nav.library",
				new string[2] { "游戏库", "Game Library" }
			},
			{
				"v15.about.identity",
				new string[2] { "Windows 性能守护", "Windows performance guardian" }
			},
			{
				"notes.title",
				new string[3] { "更新说明", "Release Notes", "更新履歴" }
			},
			{
				"notes.sub",
				new string[3] { "当前安装版本 {0}", "Currently installed version {0}", "インストール済みのバージョン {0}" }
			},
			{
				"notes.open",
				new string[3] { "更新说明", "Release notes", "更新履歴" }
			},
			{
				"notes.online",
				new string[3] { "官网更新日志", "Online release notes", "公式の更新履歴" }
			},
			{
				"notes.close",
				new string[2] { "关闭", "Close" }
			},
			{
				"v20.about.system",
				new string[2] { "PAVISE // SYSTEM IDENTITY", "PAVISE // SYSTEM IDENTITY" }
			},
			{
				"v20.about.build",
				new string[2] { "当前构建", "CURRENT BUILD" }
			},
			{
				"v20.about.platform",
				new string[2] { "目标平台", "TARGET PLATFORM" }
			},
			{
				"v20.about.state",
				new string[2] { "运行状态", "SYSTEM STATE" }
			},
			{
				"v20.about.ready",
				new string[2] { "已就绪", "READY" }
			},
			{
				"v20.about.versionstatus",
				new string[2] { "版本状态", "VERSION STATUS" }
			},
			{
				"v20.about.online",
				new string[2] { "在线", "ONLINE" }
			},
			{
				"v20.about.privacy",
				new string[2] { "隐私边界", "PRIVACY BOUNDARY" }
			},
			{
				"v20.about.privacy.value",
				new string[2] { "0 项设备数据上传", "0 DEVICE DATA UPLOADS" }
			},
			{
				"set.reset.desc",
				new string[2] { "清空设置、游戏库、白名单、忽略记录和日志后重新打开。保留程序、自启选择、系统环境开关与恢复记录。", "Clear preferences, library, whitelist, ignored games and logs, then reopen. Keep the app, startup choice, environment switches and recovery records." }
			},
			{
				"set.uninstall.desc",
				new string[2] { "尝试还原 Pavise 的改动，清理自启与数据，退出后删除当前程序和托管启动副本。", "Attempt to restore Pavise changes, clear startup and data, then delete this executable and managed startup copies after exit." }
			},
			{
				"wipe.confirm",
				new string[2] { "重置软件？\r\n\r\n游戏库、白名单、忽略记录、设置和日志将清空，完成后自动重新打开。\r\n\r\n保留程序、自启选择、系统环境开关和恢复记录。", "Reset the app?\r\n\r\nClear the library, whitelist, ignored games, preferences and logs, then reopen automatically.\r\n\r\nKeep the app, startup choice, environment switches and recovery records." }
			},
			{
				"wipe.ingame",
				new string[2] { "对局进行中，请先退出游戏再重置软件。", "A match is in progress. Exit the game before resetting the app." }
			},
			{
				"wipe.done",
				new string[2] { "软件数据已重置，将重新打开。", "App data has been reset. Reopening the app." }
			},
			{
				"wipe.failed",
				new string[2] { "重置未全部完成：{0}\r\n\r\n未能清理的项目已跳过，恢复记录保留。后台已停止时会重新打开，可关闭占用程序后再试。", "Reset was incomplete: {0}\r\n\r\nUnavailable items were skipped; recovery records are retained. The app reopens once its background tasks have stopped. Close programs holding the files and retry." }
			},
			{
				"wipe.progress.stop",
				new string[2] { "正在停止后台并完成运行收尾", "Stopping background tasks and finishing pending work" }
			},
			{
				"wipe.restartfail",
				new string[2] { "重置已处理，但重新打开失败，请手动启动 Pavise。", "Reset has finished, but reopening failed. Start Pavise manually." }
			},
			{
				"data.operation.title",
				new string[2] { "正在处理", "Processing" }
			},
			{
				"data.operation.note",
				new string[2] { "正在显示实际处理阶段，请稍候。", "The current processing phase is shown above. Please wait." }
			},
			{
				"wipe.stopfail",
				new string[2] { "停止阶段：后台任务尚未确认退出，未开始清理，恢复记录已保留。", "Stop phase: background tasks have not confirmed exit. Cleanup was not started; recovery records are retained." }
			},
			{
				"wipe.pathfail",
				new string[2] { "数据阶段：数据目录无效，未开始清理。", "Data phase: invalid data directory; cleanup was not started." }
			},
			{
				"wipe.cleanupfail",
				new string[2] { "清理阶段发生异常，未完成重置；请保留剩余配置。", "Cleanup encountered an error. Reset is incomplete; keep remaining configuration." }
			},
			{
				"uninstall.confirm",
				new string[2] { "卸载 Pavise？\r\n\r\n尝试还原记录中的系统改动，清理自启、游戏库、设置和日志。退出后自动删除当前程序与托管启动副本。\r\n\r\n失败项目跳过，未完成的恢复记录保留。", "Uninstall Pavise?\r\n\r\nAttempt to restore recorded system changes and clear startup, library, preferences and logs. After exit, delete this executable and managed startup copies automatically.\r\n\r\nSkip failed items and retain pending recovery records." }
			},
			{
				"uninstall.ingame",
				new string[2] { "对局进行中 请先退出游戏再卸载", "A match is in progress. Exit the game before uninstalling." }
			},
			{
				"uninstall.done",
				new string[2] { "还原与数据清理已完成。", "Restoration and data cleanup are complete." }
			},
			{
				"uninstall.partial",
				new string[2] { "已完成可执行的卸载清理，跳过 {0} 个未完成项目。未完成的恢复记录已保留。", "Available uninstall work is complete; {0} unfinished items were skipped. Pending recovery records are retained." }
			},
			{
				"uninstall.selfdelete",
				new string[2] { "关闭此提示后退出，本体与托管启动副本将自动删除。", "Close this message to exit. This executable and managed startup copies will be deleted automatically." }
			},
			{
				"uninstall.manualdelete",
				new string[2] { "未能安排本体删除，请退出后手动删除程序。原因：{0}", "Executable deletion could not be scheduled. Delete the app manually after exit. Reason: {0}" }
			},
			{
				"uninstall.failed",
				new string[2] { "卸载未开始清理：{0}\r\n\r\n后台尚未确认停止，数据和恢复记录保留。程序将退出。", "Uninstall cleanup did not start: {0}\r\n\r\nBackground shutdown was not confirmed. Data and recovery records are retained. The app will exit." }
			},
			{
				"uninstall.taskfail",
				new string[2] { "开机任务未能删除或确认：{0}", "Could not remove or verify the startup task: {0}" }
			},
			{
				"mode.source.global",
				new string[2] { "全局默认", "Global default" }
			},
			{
				"mode.source.game",
				new string[2] { "来自 {0} 的独立配置", "Per-game profile from {0}" }
			},
			{
				"mode.source.game.short",
				new string[2] { "游戏配置 · {0}", "Game profile · {0}" }
			},
			{
				"col.ux.closed",
				new string[2] { "已关闭", "Closed" }
			},
			{
				"col.proc.none",
				new string[2] { "未运行", "Not running" }
			},
			{
				"lib.config.tag",
				new string[2] { "独配 {0} 项", "{0} overrides" }
			},
			{
				"lib.rename",
				new string[2] { "重命名", "Rename" }
			},
			{
				"lib.rename.sub",
				new string[2] { "为 {0} 设置显示名称，仅影响列表与日志中的显示，不影响进程识别。", "Sets a display name for {0}. Affects display in lists and logs only; process detection is unaffected." }
			},
			{
				"store.savefatal.title",
				new string[2] { "配置不兼容或读写失败", "Incompatible configuration or read/write failure" }
			},
			{
				"store.savefatal.body",
				new string[2] { "旧配置与当前版本不兼容，或游戏库无法正常读写。\r\n\r\n系统改动已还原，旧配置、日志与注册表设置已自动清除：\r\n{0}\r\n\r\n程序将退出，请重新启动。", "The old configuration is incompatible with this version, or the game library could not be read or written.\r\n\r\nSystem changes were restored. Old configuration, logs and registry settings were automatically cleared:\r\n{0}\r\n\r\nThe app will exit. Please restart it." }
			},
			{
				"store.savefatal.deletefail",
				new string[2] { "旧配置与当前版本不兼容，或游戏库无法正常读写，未能安全完成重置：\r\n{0}\r\n\r\n{1}\r\n\r\n程序将退出。请保留剩余配置并重新启动以重试还原；文件被占用时先关闭占用程序。不要手动删除尚未还原的恢复记录。", "The old configuration is incompatible with this version, or the game library could not be read or written. Reset could not safely complete:\r\n{0}\r\n\r\n{1}\r\n\r\nThe app will exit. Keep remaining configuration and restart to retry restoration. Close any program holding the files. Do not manually delete pending recovery records." }
			},
			{
				"audit.fix",
				new string[2] { "一键修复", "One-click fix" }
			},
			{
				"audit.fixall",
				new string[2] { "一键修复 {0} 项", "Fix {0} items" }
			},
			{
				"audit.msi.confirm",
				new string[2] { "重新启用显卡 MSI？\n\n曾因此黑屏或死机请取消；重启生效，可还原。", "Re-enable GPU MSI?\n\nCancel if it caused black screens or freezes. Reboot required; reversible." }
			},
			{
				"audit.fixall.none",
				new string[2] { "无待修项", "Nothing to fix" }
			},
			{
				"audit.fix.revert",
				new string[2] { "还原修复", "Revert fix" }
			},
			{
				"cfg.back",
				new string[2] { "返回游戏库", "Back to game library" }
			},
			{
				"cfg.group.mode",
				new string[2] { "模式", "Mode" }
			},
			{
				"cfg.group.bg",
				new string[2] { "后台压制", "Background suppression" }
			},
			{
				"cfg.group.mempower",
				new string[2] { "内存与电源", "Memory & power" }
			},
			{
				"cfg.group.env",
				new string[2] { "系统环境", "System environment" }
			},
			{
				"cfg.group.gpu",
				new string[2] { "显卡", "GPU" }
			},
			{
				"cfg.mode",
				new string[2] { "运行模式", "Run mode" }
			},
			{
				"mode.global.title",
				new string[2] { "全局默认模式", "Global default mode" }
			},
			{
				"mode.global.sub",
				new string[2] { "所有游戏统一使用", "Used by all games" }
			},
			{
				"mode.policy.active",
				new string[2] { "当前实际行为 {0}", "Current effective behavior {0}" }
			},
			{
				"mode.pick.standard",
				new string[2] { "后台治理生效时：CPU 持续达到70%时扩围，恢复后降档；切出释放并保护前台。", "With governance active: sustained 70% CPU widens scope, recovery relaxes it; switch-out releases governance and foreground apps stay protected." }
			},
			{
				"mode.pick.competitive",
				new string[2] { "后台治理生效时：采用更广范围，切出或合格软件前台不解限；游戏、白名单及依赖仍保护。", "With governance active: wider scope; switch-out or eligible foreground apps do not release limits. Games, whitelists and dependencies stay protected." }
			},
			{
				"set.awake",
				new string[2] { "对局期间阻止息屏与睡眠", "Block display-off and sleep during matches" }
			},
			{
				"gm.dwmboost",
				new string[2] { "DWM 合成调度", "DWM composition scheduling" }
			},
			{
				"log.dwmboost.1",
				new string[2] { "已请求 DWM 参与多媒体调度（MMCSS）", "Requested DWM participation in multimedia scheduling (MMCSS)" }
			},
			{
				"log.dwmboost.2",
				new string[2] { "已撤销本程序的 DWM 多媒体调度请求", "Released this application's DWM multimedia scheduling request" }
			},
			{
				"gm.rsssteer",
				new string[2] { "网络收包引离游戏核", "Steer packet processing off game cores" }
			},
			{
				"log.rsssteer.5",
				new string[2] { "RSS 还原未完全成功 收据保留 下轮重试", "RSS restore incomplete; the receipt is kept for retry" }
			},
			{
				"log.rsssteer.6",
				new string[2] { "RSS 收包区间已按收据还原", "RSS processing range restored from the receipt" }
			},
			{
				"set.memcompress",
				new string[2] { "内存压缩设置", "Memory compression settings" }
			},
			{
				"log.memcompress.1",
				new string[2] { "读取内存压缩状态失败 未改动", "Reading memory compression state failed; nothing changed" }
			},
			{
				"log.memcompress.2",
				new string[2] { "内存压缩快照写入失败 未改动", "Writing the memory compression snapshot failed; nothing changed" }
			},
			{
				"log.memcompress.5",
				new string[2] { "还原内存压缩未通过校验 记录保留", "Restoring memory compression failed verification; the record is kept" }
			},
			{
				"log.memcompress.6",
				new string[2] { "内存压缩设置已按记录还原", "Memory compression settings restored from the record" }
			},
			{
				"v20.nav.report",
				new string[2] { "体检", "Checkup" }
			},
			{
				"v20.advanced.title",
				new string[2] { "高级", "Advanced" }
			},
			{
				"v20.admin.ready",
				new string[2] { "已就绪", "Ready" }
			},
			{
				"v20.log.sub",
				new string[3] { "仅显示最近一局的接管、运行与恢复记录", "Takeover, runtime and recovery records from the latest game only", "直近の対局の管理・実行・復元記録のみを表示" }
			},
			{
				"v20.log.hint",
				new string[3] { "单击展开 · 双击复制原文", "Click to expand · double-click to copy", "クリックで展開 · ダブルクリックで原文をコピー" }
			},
			{
				"log.latest.session",
				new string[3] { "最近一局", "Latest game", "直近の対局" }
			},
			{
				"log.category.actions",
				new string[3] { "动作日志", "Action log", "動作ログ" }
			},
			{
				"log.category.diagnostics",
				new string[3] { "诊断日志", "Diagnostic log", "診断ログ" }
			},
			{
				"log.actions.hint",
				new string[3] { "动作预览 · 双击复制记录 · 导出完整会话", "Action preview · double-click to copy the entry · export the full session", "動作のプレビュー · ダブルクリックで記録をコピー · 対局全体を出力" }
			},
			{
				"log.diagnostics.hint",
				new string[3] { "诊断预览 · 双击复制原文 · 导出完整原始日志", "Diagnostic preview · double-click to copy · export the full raw log", "診断のプレビュー · ダブルクリックで原文コピー · 元のログ全体を出力" }
			},
			{
				"log.latest.empty",
				new string[3] { "暂无对局日志\r\n进入游戏后自动显示最近一局", "No game logs yet\r\nThe latest game appears automatically when you play", "対局の記録はまだありません\r\nゲームを開始すると直近の対局を自動表示します" }
			},
			{
				"v20.log.refresh",
				new string[3] { "立即刷新", "Refresh now", "今すぐ更新" }
			},
			{
				"log.export.file",
				new string[3] { "导出文件", "Export file", "ファイル出力" }
			},
			{
				"log.export.detail",
				new string[3] { "选择位置保存当前完整运行日志", "Choose where to save the complete current runtime log", "現在の実行ログ全体の保存先を選択" }
			},
			{
				"log.export.title",
				new string[3] { "导出运行日志", "Export runtime log", "実行ログを出力" }
			},
			{
				"log.export.actions.title",
				new string[3] { "导出当前或最近会话的动作日志", "Export actions for the current or latest session", "現在または直近の対局の動作ログを出力" }
			},
			{
				"log.export.diagnostics.title",
				new string[3] { "导出完整诊断日志", "Export the complete diagnostic log", "診断ログ全体を出力" }
			},
			{
				"log.export.actions.detail",
				new string[3] { "从原始日志导出当前或最近会话的完整动作记录，不受页面预览条数限制", "Export all recorded actions for the current or latest session from the source log, beyond the preview limit", "元のログから現在または直近の対局の動作記録を出力します。プレビューの件数制限は適用されません" }
			},
			{
				"log.export.diagnostics.detail",
				new string[3] { "导出当前完整原始日志，包含其他会话和诊断细节", "Export the complete source log, including other sessions and diagnostic details", "他の対局や診断情報も含め、現在の元ログ全体を出力します" }
			},
			{
				"log.export.actions.saved",
				new string[3] { "当前或最近会话的完整动作记录已导出：", "All recorded actions for the current or latest session were exported:", "現在または直近の対局の動作記録をすべて出力しました：" }
			},
			{
				"log.export.diagnostics.saved",
				new string[3] { "完整原始诊断日志已导出：", "The complete raw diagnostic log was exported:", "元の診断ログ全体を出力しました：" }
			},
			{
				"log.export.filter",
				new string[3] { "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt", "Log files (*.log)|*.log|Text files (*.txt)|*.txt", "ログファイル (*.log)|*.log|テキストファイル (*.txt)|*.txt" }
			},
			{
				"log.export.saved",
				new string[3] { "日志已导出到：", "Log exported to:", "ログの保存先：" }
			},
			{
				"log.export.failed",
				new string[3] { "日志导出失败：", "Could not export the log:", "ログを出力できませんでした：" }
			},
			{
				"v20.log.system",
				new string[2] { "系统", "SYSTEM" }
			},
			{
				"v20.advanced.back",
				new string[2] { "返回主界面", "Back to Main" }
			},
			{
				"v20.advanced.nav.policy",
				new string[2] { "优化策略", "Policies" }
			},
			{
				"v20.advanced.nav.anticheat",
				new string[2] { "反作弊", "Anti-cheat" }
			},
			{
				"v20.advanced.nav.system",
				new string[2] { "环境", "Environment" }
			},
			{
				"v20.advanced.nav.irq",
				new string[2] { "设备中断", "Interrupts" }
			},
			{
				"v15.library.add",
				new string[2] { "添加游戏", "Add Game" }
			},
			{
				"v15.library.drop",
				new string[2] { "支持 EXE、LNK、Steam 快捷方式和拖入。所有游戏使用顶部模式。", "Supports EXE, LNK, Steam shortcuts and drag-and-drop. All games use the mode in the title bar." }
			},
			{
				"scan.title",
				new string[2] { "添加游戏", "Add Game" }
			},
			{
				"ui.scroll.vertical",
				new string[2] { "垂直滚动条", "Vertical scroll bar" }
			},
			{
				"scan.hint",
				new string[3] { "自动列出已安装游戏和运行中的程序；有多个入口时请逐项选择。未找到时，可浏览或拖入 EXE、快捷方式和游戏文件夹。", "Installed games and running apps appear here. Choose entries individually when needed, or browse/drop an EXE, shortcut or game folder.", "インストール済みゲームと実行中のアプリを表示します。候補が複数あれば個別に選択してください。EXE、ショートカット、ゲームフォルダーの参照・ドロップも可能です。" }
			},
			{
				"scan.browse",
				new string[2] { "浏览文件", "Browse Files" }
			},
			{
				"scan.folder",
				new string[3] { "浏览文件夹", "Browse Folder", "フォルダーを参照" }
			},
			{
				"scan.picked.tag",
				new string[3] { "手动添加", "Picked", "手動追加" }
			},
			{
				"scan.choice.tag",
				new string[3] { "请选择入口", "Choose entry", "起動先を選択" }
			},
			{
				"scan.folder.none",
				new string[3] { "这些文件夹里没有找到可运行的游戏程序：", "No runnable game program was found in these folders:", "これらのフォルダーにゲームの実行ファイルが見つかりません：" }
			},
			{
				"fbd.game",
				new string[3] { "选择游戏所在的文件夹", "Select the game's folder", "ゲームのフォルダーを選択" }
			},
			{
				"scan.running.tag",
				new string[2] { "运行中", "Running" }
			},
			{
				"scan.stopped.tag",
				new string[2] { "已退出", "Exited" }
			},
			{
				"scan.renderer.tag",
				new string[2] { "正在渲染 GPU {0}%", "Rendering, GPU {0}%" }
			},
			{
				"set.light",
				new string[2] { "亮色模式", "Light mode" }
			},
			{
				"set.light.n",
				new string[2] { "切换为浅色界面，立即生效，默认为暗色。", "Switches to the light UI. Takes effect immediately; dark is the default." }
			},
			{
				"scan.busy",
				new string[2] { "正在读取各平台的安装记录", "Reading install records from each platform" }
			},
			{
				"scan.count",
				new string[2] { "找到 {0} 个，其中 {1} 个尚未加入", "Found {0}, {1} of which are not yet added" }
			},
			{
				"scan.stage.running",
				new string[2] { "正在枚举有窗口的运行中程序", "Enumerating running programs with windows" }
			},
			{
				"scan.stage.gpu",
				new string[2] { "正在采样 GPU 以确定渲染进程，需要数秒", "Sampling the GPU to determine the rendering process; takes a few seconds" }
			},
			{
				"scan.stage.count",
				new string[2] { "{0}   已找到 {1} 个", "{0}   {1} found" }
			},
			{
				"scan.live",
				new string[2] { "每 3 秒自动刷新", "Auto-refreshes every 3s." }
			},
			{
				"scan.none",
				new string[2] { "未找到候选程序。启动游戏后会自动出现，也可浏览文件添加。", "No candidates found. Start a game or browse for an EXE or shortcut." }
			},
			{
				"scan.all",
				new string[2] { "全选", "Select all" }
			},
			{
				"scan.already",
				new string[2] { "已在库中", "Already in library" }
			},
			{
				"scan.add.n",
				new string[2] { "添加 {0} 个", "Add {0}" }
			},
			{
				"scan.added",
				new string[2] { "已加入 {0} 个游戏", "Added {0} games" }
			},
			{
				"v15.library.running",
				new string[2] { "运行中", "Running" }
			},
			{
				"v15.library.ready",
				new string[2] { "已就绪", "Ready" }
			},
			{
				"v15.library.force",
				new string[2] { "强制接管", "Forced takeover" }
			},
			{
				"v15.library.force.on",
				new string[2] { "强制接管 已开", "Forced takeover on" }
			},
			{
				"v15.library.forced.tag",
				new string[2] { "强制", "Forced" }
			},
			{
				"v15.library.force.confirm",
				new string[2] { "强制接管 {0}？\r\n\r\n进程存在即保持游戏模式。仅用于手动启停的程序，勿选常驻进程。", "Force takeover for {0}?\r\n\r\nGame mode stays active while it runs. Use only for manually controlled programs, never resident ones." }
			},
			{
				"v15.library.force.noexe",
				new string[2] { "缺少明确的 EXE，无法强制接管。请删除条目并重新选择 EXE。", "Forced takeover needs a specific EXE. Delete the entry and add the EXE again." }
			},
			{
				"v15.library.autoadd",
				new string[2] { "自动入库", "Auto add" }
			},
			{
				"v15.library.autoadd.settings",
				new string[3] { "自动入库设置", "Auto-add settings", "自動登録の設定" }
			},
			{
				"lib.family.compact",
				new string[3] { "压制家族后台", "Family background", "関連バックグラウンド" }
			},
			{
				"lib.cores.toggle.tip",
				new string[3] { "开启或关闭本游戏的手动核心配置；未配置时点击进入选核。关闭后恢复共享，不影响其他游戏。", "Enable or disable this game's manual core configuration. Click to configure if none is saved. Disabling restores shared cores without affecting other games.", "手動コア設定を切り替えます。未設定の場合はクリックして設定。オフで共有に戻り、他のゲームには影響しません。" }
			},
			{
				"lib.cores.tip",
				new string[3] { "为此游戏选择核心与核心隔离；硬亲和性随隔离自动启用。", "Choose this game's cores and isolation; hard affinity is automatic with isolation.", "このゲームのコアとコア分離を設定します。ハードアフィニティは分離と連動して自動で有効になります。" }
			},
			{
				"lib.family.suppress",
				new string[2] { "压制家族后台", "Suppress family background" }
			},
			{
				"lib.family.gated.body",
				new string[2] { "这个条目还没有观测到渲染 Pavise 不确定它是不是游戏本体 先进一局 看到已观测渲染标签后再开启家族后台压制", "Rendering has not been observed for this entry yet, so Pavise cannot tell whether it is the game itself. Play a match first; once the entry shows the observed badge, family suppression can be turned on." }
			},
			{
				"lib.family.tip",
				new string[2] { "仅对这个游戏的会话设置。关闭时保留家族及 Steam 平台辅助进程的自动保护；开启时允许符合后台规则的关联进程，以及 Steam 主程序、网页和覆盖层辅助进程被压制。游戏本体、候选、白名单及其他已确认的保护仍有效。\r\n\r\n例如用 Steam 启动 CS:GO：请选择游戏本体 EXE。需要保留 Steam 功能可将具体进程加白。OBS 等独立录屏与硬件、输入音频保护不随此项关闭。", "Applies during this game's sessions. Off retains family and Steam platform-helper protection. On allows eligible related processes, Steam, and its web/overlay helpers to be suppressed. The game itself, candidates, whitelist and other confirmed protections remain.\r\n\r\nFor example, when Steam launches CS:GO, select the game's EXE. Whitelist Steam processes you need. Independent recording tools such as OBS, hardware, input and audio protection stay enabled." }
			},
			{
				"lib.renderer.observed",
				new string[2] { "已观测渲染", "Render observed" }
			},
			{
				"lib.renderer.pending",
				new string[2] { "暂未确认", "Not confirmed" }
			},
			{
				"lib.renderer.observed.tip",
				new string[2] { "已在当前 EXE 上观测到 GPU 3D 渲染活动。这不证明它是游戏主渲染程序，也不保证压制其他关联进程一定安全。更换入口或程序文件变化后需要重新观测。", "GPU 3D render activity was observed for this EXE. This does not prove it is the main game renderer or guarantee that suppressing related processes is safe. A changed entry or executable requires a new observation." }
			},
			{
				"lib.renderer.pending.tip",
				new string[2] { "尚未记录当前 EXE 的渲染活动。建议先保持家族后台压制关闭，进入实际对局后再核对。", "No render activity has been recorded for this EXE yet. Keep family background suppression off, enter an actual match, then review the result." }
			},
			{
				"lib.family.warning.title",
				new string[2] { "不建议开启家族后台压制", "Family suppression is not recommended" }
			},
			{
				"lib.family.warning.target",
				new string[2] { "当前游戏入口 · 完整路径可复制", "Current game entry · full path can be copied" }
			},
			{
				"lib.family.warning.noexe",
				new string[2] { "尚未指定明确的 EXE", "No specific EXE has been selected" }
			},
			{
				"lib.family.warning.body",
				new string[2] { "通常保持关闭。开启后，关联后台以及 Steam 主程序、网页和覆盖层辅助进程不再享受这项自动豁免；仍须满足后台压制规则。可能导致加载变慢、平台聊天/浮层卡顿或依赖功能异常，每次开启都会提醒。\r\n\r\n英雄联盟应选择对局程序 League of Legends.exe。Steam 游戏以 CS:GO 为例，应选择游戏本体 EXE，而不是 Steam.exe 或大厅。即使观测到渲染，也不保证压制关联进程安全或有收益。\r\n\r\n游戏本体、候选、白名单和已确认的其他保护不变，OBS 等独立录屏及输入音频仍保护。需要保留 Steam 功能可加白；出现异常请关闭本项。", "Normally keep this off. Enabling it allows eligible related background processes, plus Steam and its web/overlay helpers, to be suppressed. Loading, platform chat, overlays or dependent features may slow down or malfunction. Confirmation is required every time.\r\n\r\nFor League of Legends, select League of Legends.exe; for Steam games such as CS:GO, select the game's EXE, not Steam.exe or a lobby. Observed rendering does not guarantee safety or performance gains.\r\n\r\nThe game itself, candidates, whitelist and other confirmed protections remain. Independent recording and input/audio protection stays enabled. Whitelist Steam processes you need; turn this off if issues occur." }
			},
			{
				"lib.family.warning.cancel",
				new string[2] { "保持关闭（推荐）", "Keep off (recommended)" }
			},
			{
				"lib.family.warning.enable",
				new string[2] { "仍要开启", "Enable anyway" }
			},
			{
				"lib.family.changed",
				new string[2] { "游戏入口刚刚发生变化。已保留当前设置，请核对新入口后重新操作。", "The game entry just changed. Its current setting was kept. Review the new entry and try again." }
			},
			{
				"lib.family.save.failed",
				new string[2] { "未能保存此设置。条目可能已更新，请核对后重试。", "This setting could not be saved. The entry may have changed; review it and try again." }
			},
			{
				"lib.family.whitelist.note",
				new string[2] { "# 家族后台压制由游戏库逐项设置；默认保护，白名单独立生效", "# Family background suppression is set per game in the library; protected by default, with the whitelist applied independently." }
			},
			{
				"lib.render.observation.failed",
				new string[2] { "渲染观察记录保存失败", "Render observation could not be saved" }
			},
			{
				"lib.render.observation.saved",
				new string[2] { "已观察渲染活动", "Render activity observed" }
			},
			{
				"lib.render.observation.trace",
				new string[3] { "渲染观测 {0}(pid {1}) {2}", "Render observation {0}(pid {1}): {2}", "描画観測 {0}(pid {1}): {2}" }
			},
			{
				"lib.render.observation.Started",
				new string[3] { "开始采样", "Sampling started", "サンプリング開始" }
			},
			{
				"lib.render.observation.Recorded",
				new string[3] { "已记录 GPU 3D 活动", "GPU 3D activity recorded", "GPU 3D 動作を記録済み" }
			},
			{
				"lib.render.observation.ForegroundWaiting",
				new string[3] { "等待游戏回到前台", "Waiting for the game to return to the foreground", "ゲームが前面に戻るのを待機" }
			},
			{
				"lib.render.observation.SamplingBusy",
				new string[3] { "等待其他 GPU 采样结束", "Waiting for another GPU sample to finish", "他の GPU サンプリングの完了を待機" }
			},
			{
				"lib.render.observation.Canceled",
				new string[3] { "会话已变化，本次采样取消", "Session changed; sample canceled", "セッション変更によりサンプリングを中止" }
			},
			{
				"lib.render.observation.IdentityUnavailable",
				new string[3] { "进程身份校验未通过", "Process identity validation failed", "プロセスの識別情報を確認できません" }
			},
			{
				"lib.render.observation.FileUnavailable",
				new string[3] { "无法读取 EXE 文件信息", "EXE file information is unavailable", "EXE ファイル情報を取得できません" }
			},
			{
				"lib.render.observation.GpuUnavailable",
				new string[3] { "GPU 采样未返回有效数据", "GPU sampling returned no usable data", "GPU の有効なサンプルを取得できません" }
			},
			{
				"lib.render.observation.PidMissing",
				new string[3] { "GPU 数据中没有目标进程", "Target process is absent from GPU data", "GPU データに対象プロセスがありません" }
			},
			{
				"lib.render.observation.ForegroundChanged",
				new string[3] { "采样结束时游戏不在前台，本次不记录", "Game was not foreground at sample end; observation discarded", "サンプル終了時にゲームが前面にないため記録しません" }
			},
			{
				"lib.render.observation.InvalidEvidence",
				new string[3] { "GPU 观测数据无效", "GPU observation data is invalid", "GPU 観測データが無効です" }
			},
			{
				"lib.render.observation.BelowThreshold",
				new string[3] { "GPU 3D 样本低于记录门槛", "GPU 3D sample is below the recording threshold", "GPU 3D サンプルが記録のしきい値未満です" }
			},
			{
				"lib.render.observation.ProfileChanged",
				new string[3] { "游戏条目或入口已变化，本次不记录", "Game profile or entry changed; observation discarded", "ゲーム登録または実行ファイルの変更により記録しません" }
			},
			{
				"lib.render.observation.ActiveChanged",
				new string[3] { "当前接管进程已变化，本次不记录", "Active game process changed; observation discarded", "現在のゲームプロセスの変更により記録しません" }
			},
			{
				"lib.render.observation.FileChanged",
				new string[3] { "EXE 文件已变化或不可读，本次不记录", "EXE changed or became unreadable; observation discarded", "EXE の変更または読み取り不能により記録しません" }
			},
			{
				"lib.render.observation.Closed",
				new string[3] { "观测存储已关闭，本次不记录", "Observation store is closed; observation discarded", "観測ストアが閉じているため記録しません" }
			},
			{
				"lib.render.observation.SaveFailed",
				new string[3] { "观测记录写入失败", "Observation could not be saved", "観測の保存に失敗しました" }
			},
			{
				"lib.render.observation.QueueFailed",
				new string[3] { "采样任务未能启动", "Sampling task could not start", "サンプリングを開始できませんでした" }
			},
			{
				"lib.render.observation.Error",
				new string[3] { "采样发生异常", "Sampling encountered an exception", "サンプリング中に例外が発生しました" }
			},
			{
				"log.powerplanschemes.47",
				new string[2] { "已清除托管方案里旧版遗留的处理器空闲禁用设置", "Cleared the legacy processor idle disable setting from the managed plan." }
			},
			{
				"v15.library.autoadd.desc",
				new string[3] { "自动识别并添加游戏。", "Detects and adds games automatically.", "ゲームを自動検出・登録。" }
			},
			{
				"log.autoadd.1",
				new string[2] { "自动入库已开启 根据前台窗口与 GPU 3D 活动识别游戏", "Auto add enabled; foreground window and GPU 3D activity identify game candidates" }
			},
			{
				"log.autoadd.2",
				new string[2] { "自动入库已关闭", "Auto add disabled" }
			},
			{
				"log.autoadd.3",
				new string[2] { "自动入库：前台窗口已确认，GPU 3D 活动（", "Auto add: foreground window confirmed, GPU 3D activity (" }
			},
			{
				"log.autoadd.4",
				new string[2] { "%）的游戏进程 ", "%): game process " }
			},
			{
				"log.autoadd.5",
				new string[2] { "，已加入游戏库（", " added to the library (" }
			},
			{
				"log.autoadd.6",
				new string[2] { "）；不需要的话在游戏库移除，之后不会再自动加入", "); remove it in the library if unwanted and it will never be auto-added again" }
			},
			{
				"autoadd.reason.GpuUnavailable",
				new string[2] { "GPU 活动暂不可读，稍后重试", "GPU activity temporarily unavailable; retrying later" }
			},
			{
				"autoadd.reason.CaptureUnavailable",
				new string[2] { "活动识别暂不可用，稍后重试", "Activity capture unavailable; retrying later" }
			},
			{
				"autoadd.reason.ActivityWaiting",
				new string[2] { "等待游戏产生足够 3D 活动", "Waiting for sufficient game 3D activity" }
			},
			{
				"autoadd.reason.ConfirmingActivity",
				new string[2] { "已发现前台 3D 活动，等待再次确认", "Foreground 3D activity found; awaiting confirmation" }
			},
			{
				"autoadd.preference.unsaved",
				new string[2] { "自动入库设置未能保存，本次运行仍遵从当前开关", "Auto-add preference could not be saved; this run still follows the current switch" }
			},
			{
				"t.autoadd.1",
				new string[2] { "# 从游戏库移除过的路径不再自动入库；重新手动添加会自动解除。", "# Paths removed from the library are never auto-added again; adding them back manually lifts this." }
			},
			{
				"gm.cachewarm",
				new string[2] { "缓存预热", "Cache warm-up" }
			},
			{
				"gm.cachewarm.sub",
				new string[2] { "默认关闭，可逐游戏覆盖。对局稳定 90 秒后，以后台低优先级读取游戏资源包，每局最多 2 GiB、每秒约 31 MiB。仅交流供电、内存充足且游戏位于固态盘时运行；开启待机内存清理时跳过，关守护、退局或内存不足时停止。可能改善后续资源读取，不保证提帧。", "Off by default, with per-game overrides. After 90 seconds of stable play, reads asset archives at background priority: up to 2 GiB per session and about 31 MiB/s. Requires AC power, ample memory and an SSD. Yields to standby cleanup; stops on protection off, session exit or memory pressure. May help later asset reads; no FPS gain is guaranteed." }
			},
			{
				"cachewarm.idle",
				new string[2] { "等待对局", "Waiting for a session" }
			},
			{
				"cachewarm.waiting",
				new string[2] { "等待对局稳定 90 秒", "Waiting 90 seconds" }
			},
			{
				"cachewarm.running",
				new string[2] { "正在预读资源", "Reading assets" }
			},
			{
				"cachewarm.done",
				new string[2] { "本局预热结束", "Warm-up finished" }
			},
			{
				"cachewarm.stopped",
				new string[2] { "预热已停止", "Warm-up stopped" }
			},
			{
				"cachewarm.skipped",
				new string[2] { "条件不足或资源不可读，已跳过", "Skipped: requirements unmet or assets unreadable" }
			},
			{
				"gm.solo",
				new string[2] { "对局单屏", "Single display in match" }
			},
			{
				"log.solo.2",
				new string[2] { "对局单屏 已切回原来的多屏布局", "Single display: switched back to the original multi-display layout" }
			},
			{
				"log.adaptive.1",
				new string[2] { "自适应压制 CPU 达到70%并持续10秒 已临时扩大后台范围 当前周期第 ", "Adaptive suppression: CPU at or above 70% for 10 seconds; temporarily widened background control. Current-cycle episode " }
			},
			{
				"log.adaptive.2",
				new string[2] { "智能后台治理：负载恢复并稳定，已恢复常规范围", "Smart background control: load recovered and stayed calm; restored the normal scope" }
			},
			{
				"log.adaptive.3",
				new string[2] { "智能后台治理：已结束临时扩大范围，升档状态与采样已复位", "Smart background control: temporary wider scope ended; escalation state and sampling reset" }
			},
			{
				"log.appgpuabandon.1",
				new string[2] { "重置 应用显卡偏好 ", "Reset: app GPU preference for " }
			},
			{
				"log.appgpuabandon.2",
				new string[2] { " 的记录无法确认归属 按仅移除记录处理 保留当前系统值", " cannot be claimed; treated as forget-record-only, current system value kept" }
			},
			{
				"irqauto.logprefix",
				new string[2] { "自动中断编排 ", "IRQ autopilot: " }
			},
			{
				"plan.pick.title",
				new string[2] { "对局电源计划", "Match Power Plan" }
			},
			{
				"plan.pick.auto",
				new string[2] { "由软件调度", "Automatic" }
			},
			{
				"plan.pick.pgcontrolled",
				new string[3] { "由 PG 控制", "PG controlled", "PG が制御" }
			},
			{
				"plan.button.hint",
				new string[2] { "对局电源计划", "Match power plan" }
			},
			{
				"power.name",
				new string[2] { "对局电源计划", "Match power plan" }
			},
			{
				"plan.fly.cur",
				new string[2] { "当前系统计划 {0}，对局中切换至所选计划，退出后自动切回。", "Current system plan {0}. Switches to the selected plan during matches and switches back automatically on exit." }
			},
			{
				"plan.pick.off",
				new string[3] { "不切换", "Don't switch", "切り替えない" }
			},
			{
				"plan.fly.off",
				new string[3] { "当前系统计划 {0}。保留当前电源设置，进出对局不切换。", "Current system plan {0}. Power settings are retained when entering or leaving matches.", "現在のシステムプラン {0}。対局の開始・終了時も電源設定を維持します。" }
			},
			{
				"log.powerplan.off",
				new string[3] { "POWER event=selection-saved selection=off", "POWER event=selection-saved selection=off", "POWER event=selection-saved selection=off" }
			},
			{
				"plan.pick.managed",
				new string[2] { "托管", "Managed" }
			},
			{
				"plan.pick.local",
				new string[2] { "本机", "Local" }
			},
			{
				"core.band.ccd",
				new string[3] { "CCD {0}", "CCD {0}", "CCD {0}" }
			},
			{
				"core.band.ccd.cache",
				new string[3] { "CCD {0} · 3D 缓存", "CCD {0} · 3D Cache", "CCD {0} · 3D キャッシュ" }
			},
			{
				"core.tag.cache",
				new string[3] { "缓存", "Cache", "キャッシュ" }
			},
			{
				"core.band.perf",
				new string[3] { "性能核", "P-cores", "P コア" }
			},
			{
				"core.band.eff",
				new string[3] { "能效核", "E-cores", "E コア" }
			},
			{
				"core.band.all",
				new string[3] { "处理器核心", "CPU cores", "CPU コア" }
			},
			{
				"core.band.meta.smt",
				new string[3] { "{0} 物理核 / {1} 线程 · 带超线程", "{0} physical cores / {1} threads, with Hyper-Threading", "物理コア {0} 個 / {1} スレッド · SMT あり" }
			},
			{
				"core.band.meta.solo",
				new string[3] { "{0} 物理核 / {1} 线程", "{0} physical cores / {1} threads", "物理コア {0} 個 / {1} スレッド" }
			},
			{
				"core.tag.exclusive",
				new string[3] { "独占", "Exclusive", "専有" }
			},
			{
				"core.topo.split",
				new string[3] { "  ·  本机拓扑读数自相矛盾 自动独占可能选错核心 详见运行日志", "  ·  this machine reports contradictory topology; automatic exclusive cores may pick the wrong cores, see the run log", "  ·  本機のトポロジー読み取りに矛盾があります。自動専有が誤ったコアを選ぶ可能性があります。詳細は実行ログをご覧ください" }
			},
			{
				"core.topo.line",
				new string[3] { "{0} 物理核 · {1} 逻辑核 · 其中 {2} 个带超线程", "{0} physical cores, {1} logical cores, {2} with Hyper-Threading", "物理コア {0} 個 · 論理コア {1} 個 · うち {2} 個が SMT 対応" }
			},
			{
				"core.topo.unknown",
				new string[3] { "未知处理器", "Unknown processor", "不明なプロセッサ" }
			},
			{
				"v14.master",
				new string[2] { "Pavise 智能守护", "Pavise Smart Guardian" }
			},
			{
				"preset.standard",
				new string[2] { "智能", "Smart" }
			},
			{
				"preset.extreme",
				new string[2] { "极限", "Extreme" }
			},
			{
				"extreme.dwm",
				new string[2] { "DWM 合成调度保护", "DWM scheduling protection" }
			},
			{
				"preset.competitive",
				new string[2] { "电竞", "Esports" }
			},
			{
				"preset.handheld",
				new string[2] { "掌机", "Handheld" }
			},
			{
				"mode.locked.ingame",
				new string[3] { "对局进行中，禁止切换模式；退出游戏后可切换。关闭守护不会提前解锁。", "Mode switching is locked during a game. Exit the game to switch; disabling protection does not unlock it.", "対局中はモードを変更できません。ゲーム終了後に変更できます。保護を停止しても解除されません。" }
			},
			{
				"sec.appearance",
				new string[3] { "视觉定制", "Personalization", "ビジュアル設定" }
			},
			{
				"backdrop.title",
				new string[2] { "背景封面", "Backdrop image" }
			},
			{
				"backdrop.dim",
				new string[2] { "封面浓度", "Backdrop strength" }
			},
			{
				"backdrop.dim.off",
				new string[2] { "关闭", "Off" }
			},
			{
				"backdrop.dim.1",
				new string[2] { "淡", "Light" }
			},
			{
				"backdrop.dim.2",
				new string[2] { "中", "Medium" }
			},
			{
				"backdrop.dim.3",
				new string[2] { "浓", "Heavy" }
			},
			{
				"btn.backdrop.set",
				new string[2] { "选择图片", "Choose image" }
			},
			{
				"btn.backdrop.change",
				new string[2] { "更换图片", "Change image" }
			},
			{
				"btn.backdrop.reset",
				new string[2] { "恢复默认", "Reset" }
			},
			{
				"backdrop.status.on",
				new string[2] { "已启用 · 当前浓度：{0}", "Enabled · Current strength: {0}" }
			},
			{
				"backdrop.status.off",
				new string[2] { "未设置背景图片，界面使用默认纯色背景。", "No image selected; the default solid background is in use." }
			},
			{
				"backdrop.preview.empty",
				new string[2] { "暂无封面", "NO BACKDROP" }
			},
			{
				"backdrop.pick",
				new string[2] { "选择背景封面", "Choose a backdrop image" }
			},
			{
				"backdrop.filter",
				new string[2] { "图片文件", "Image files" }
			},
			{
				"log.backdrop.1",
				new string[2] { "背景封面 复制图片失败", "Backdrop: copying the image failed" }
			},
			{
				"log.backdrop.2",
				new string[2] { "背景封面 删除图片失败", "Backdrop: deleting the image failed" }
			},
			{
				"log.backdrop.3",
				new string[2] { "背景封面 读取图片失败", "Backdrop: reading the image failed" }
			},
			{
				"log.backdrop.4",
				new string[2] { "背景封面 裁切失败", "Backdrop: fitting the image failed" }
			},
			{
				"sec.theme",
				new string[2] { "模式主题色", "Mode accent colors" }
			},
			{
				"theme.hint",
				new string[2] { "为各模式设置强调色；「默认」恢复内置配色。", "Set an accent color per mode; Default restores the built-in palette." }
			},
			{
				"v14.boost.disabled",
				new string[2] { "游戏提优已关闭", "Game boost is off" }
			},
			{
				"v14.boost.wait",
				new string[2] { "等待游戏画面输出", "Waiting for game frame output" }
			},
			{
				"v14.boost.no.renderer",
				new string[2] { "已识别启动器，等待游戏本体", "Launcher detected, waiting for the game itself" }
			},
			{
				"v14.boost.applying",
				new string[2] { "正在提速 {0}", "Boosting {0}" }
			},
			{
				"v14.boost.verified",
				new string[2] { "已生效 {0}\r\n提速已写入系统并通过回读确认", "Active {0}\r\nBoost written to the system and confirmed by read-back" }
			},
			{
				"v14.boost.protected",
				new string[2] { "{0} 受反作弊保护，无法修改优先级。后备提优已移除，不再通过启动配置提优。", "{0} is anti-cheat protected, so its priority cannot be changed. Fallback boost has been removed; no launch-time priority override is applied." }
			},
			{
				"v14.bg.master",
				new string[2] { "后台压制 总开关", "Background suppression master switch" }
			},
			{
				"v14.preset.forced.on",
				new string[2] { "预设强制开", "Forced on by preset" }
			},
			{
				"v14.preset.forced.off",
				new string[2] { "预设强制关", "Forced off by preset" }
			},
			{
				"lock.na",
				new string[2] { "本机不适用", "Not applicable here" }
			},
			{
				"lock.external",
				new string[2] { "系统已开启", "Already on in the system" }
			},
			{
				"v14.anticheat.groups.sub",
				new string[2] { "各分组开关直接控制专项压制，全部关闭即停止；反作弊后台豁免始终有效。「仅保护」与「只记录」分组不参与压制。", "Each group switch directly controls suppression; turning all groups off stops it. Background exemptions remain active. Protection-only and record-only groups are never suppressed." }
			},
			{
				"v14.anticheat",
				new string[2] { "反作弊", "Anti-cheat" }
			},
			{
				"detect.learned",
				new string[2] { "命中已确认的渲染进程", "Matched a confirmed renderer process" }
			},
			{
				"log.renderer.unconfirmed",
				new string[2] { "渲染候选 暂未获得足够证据 仅保护当前候选并低频复查 不改写游戏库 ", "Renderer candidate: evidence not yet sufficient; protecting only this candidate and rechecking at low frequency, without changing the library: " }
			},
			{
				"log.renderer.confirmed",
				new string[2] { "渲染交接 GPU 证据确认 ", "Renderer handoff confirmed by GPU evidence: " }
			},
			{
				"detect.fullscreen",
				new string[2] { "前台全屏画面", "Foreground fullscreen frame" }
			},
			{
				"detect.window",
				new string[2] { "所选 EXE 自带前台窗口", "Selected EXE has its own foreground window" }
			},
			{
				"detect.gpu",
				new string[2] { "GPU 3D 引擎主导 {0}%", "GPU 3D engine dominant at {0}%" }
			},
			{
				"detect.present",
				new string[2] { "画面提交 {0} 帧/秒", "Presenting {0} frames/s" }
			},
			{
				"detect.gpu.pending",
				new string[2] { "窗口化候选，等待 GPU 证据确认", "Windowed candidate, awaiting GPU evidence" }
			},
			{
				"detect.forced",
				new string[2] { "强制接管，进程存在即判定为对局", "Forced takeover: process presence counts as a match" }
			},
			{
				"title.noelev",
				new string[2] { "未提权，压制类功能无效", "Not elevated; suppression features are inactive" }
			},
			{
				"title.admin",
				new string[2] { "管理员权限", "Administrator" }
			},
			{
				"title.guard",
				new string[2] { "守护中 {0}", "Guarding {0}" }
			},
			{
				"btn.remove",
				new string[2] { "移除选中", "Remove selected" }
			},
			{
				"st.off",
				new string[2] { "已关闭", "Off" }
			},
			{
				"st.mon",
				new string[2] { "监控中，未检测到列表内的游戏", "Monitoring; no game from the list detected" }
			},
			{
				"st.armed",
				new string[2] { "已就位 {0}，进入对局画面自动接管", "Armed for {0}; takes over automatically when the match screen appears" }
			},
			{
				"st.grace",
				new string[2] { "{0} 已退出，剩余 {1} 秒宽限，后台已先行还原，重新进入游戏可无缝续接", "{0} exited; {1}s grace remaining; background already restored, re-entering the game resumes seamlessly" }
			},
			{
				"st.active",
				new string[2] { "激活中 {0}，已压制 {1} 个进程", "Active on {0}; {1} processes suppressed" }
			},
			{
				"st.sep",
				new string[2] { " · ", " · " }
			},
			{
				"st.boost",
				new string[2] { "已提优 {0} {1}", "Boosted {0} {1}" }
			},
			{
				"st.hp",
				new string[2] { "高性能电源", "High-performance power" }
			},
			{ "st.power.verified", new[] { "对局电源已验证", "Match power verified", "ゲーム用電源を確認済み" } },
			{ "st.power.pending", new[] { "对局电源待验证", "Match power pending verification", "ゲーム用電源を確認中" } },
			{ "st.power.stopped", new[] { "对局电源故障·已停用", "Match power failed; stopped", "ゲーム用電源でエラー・停止中" } },
			{ "st.power.restore.failed", new[] { "电源恢复未完成", "Power restoration incomplete", "電源の復元が未完了" } },
			{ "st.power.unchanged", new[] { "沿用系统电源", "Keeping system power plan", "システムの電源プランを維持" } },
			{ "st.power.waiting", new[] { "对局电源待命", "Match power standing by", "ゲーム用電源は待機中" } },
			{
				"st.pr",
				new string[2] { "电源就绪", "Power ready" }
			},
			{
				"tame.toggle",
				new string[2] { "反作弊分组压制", "Anti-cheat group suppression" }
			},
			{
				"set.nvmax",
				new string[2] { "NVIDIA 电源模式 最高性能优先", "NVIDIA power mode: prefer maximum performance" }
			},
			{
				"set.nvmax.n",
				new string[2] { "保持 GPU 高性能状态；功耗、温度上升，散热不足可能降频。", "Keeps the GPU in its high-performance state. Raises power and heat; weak cooling may throttle." }
			},
			{
				"set.gpuclock",
				new string[2] { "对局锁定显卡核心频率", "Lock GPU core clock during play" }
			},
			{
				"log.gpuclock.6",
				new string[2] { "显卡频率解锁失败 收据保留待重试", "GPU clock unlock failed; the receipt is kept for retry" }
			},
			{
				"log.gpuclock.7",
				new string[2] { "显卡频率已解锁", "GPU clock unlocked" }
			},
			{
				"log.gpuclock.8",
				new string[2] { "检测到上次未解锁的显卡频率 已解锁", "Detected a GPU clock lock left from last time; unlocked" }
			},
			{
				"set.nv.none",
				new string[2] { "未检测到 NVIDIA 驱动，本项停用", "No NVIDIA driver detected; this item is disabled" }
			},
			{
				"v211.link.failed",
				new string[2] { "打不开浏览器，请手动复制链接访问。", "Could not open a browser; please copy the link and open it manually." }
			},
			{
				"site.entry",
				new string[3] { "访问官网", "Visit website", "公式サイト" }
			},
			{
				"site.update.available",
				new string[3] { "v{0} · 查看更新", "v{0} · What's new", "v{0} · 更新内容" }
			},
			{
				"site.download",
				new string[3] { "官网下载", "Website download", "公式サイトで入手" }
			},
			{
				"site.about.originalSub",
				new string[3] { "项目身份、版本状态与官方网站", "Project identity, version status, and official website", "プロジェクト情報・バージョン・公式サイト" }
			},
			{
				"site.about.source",
				new string[3] { "更新来源", "UPDATE SOURCE", "更新元" }
			},
			{
				"site.about.wait",
				new string[3] { "尚未检查", "Not checked yet", "未確認" }
			},
			{
				"site.about.latest",
				new string[3] { "已是最新版本", "Up to date", "最新バージョン" }
			},
			{
				"site.about.found",
				new string[3] { "发现 v{0}", "v{0} available", "v{0} を利用可能" }
			},
			{
				"donate.entry",
				new string[3] { "捐赠", "Donate", "寄付" }
			},
			{
				"donate.loading",
				new string[3] { "二维码加载中…", "Loading QR code…", "QR コードを読み込み中…" }
			},
			{
				"donate.title",
				new string[3] { "请作者喝一杯奶茶", "Buy the author a milk tea", "作者にミルクティーを一杯" }
			},
			{
				"donate.body",
				new string[3] { "Pavise 免费，也不收任何费用。用得顺手的话，微信扫码请作者喝杯奶茶，多少随意，不捐也一样用。", "Pavise is free and charges nothing. If it serves you well, scan with WeChat to buy the author a milk tea. Any amount, and it works the same either way.", "Pavise は無料で、料金は一切いただきません。気に入ったら WeChat でスキャンして作者にミルクティーを一杯。金額は自由で、寄付しなくても同じように使えます。" }
			},
			{
				"donate.hint",
				new string[3] { "微信扫一扫", "Scan with WeChat", "WeChat でスキャン" }
			},
			{
				"donate.failed",
				new string[3] { "二维码加载失败，请检查网络后重试。", "Could not load the QR code. Check your connection and try again.", "QR コードを読み込めませんでした。ネットワークを確認して再試行してください。" }
			},
			{
				"set.nvll",
				new string[2] { "NVIDIA 低延迟", "NVIDIA low latency" }
			},
			{
				"set.nvll.n",
				new string[2] { "限制预渲染队列。仅 DX9/DX11；DX12/Vulkan 自管，Reflex 优先。", "Limits the pre-render queue. DX9/DX11 only; DX12/Vulkan self-manage, and Reflex takes priority." }
			},
			{
				"nvll.on",
				new string[2] { "低延迟", "Low latency" }
			},
			{
				"nvll.ultra",
				new string[2] { "超低延迟 Ultra", "Ultra low latency" }
			},
			{
				"set.nvsmooth",
				new string[2] { "NVIDIA Smooth Motion 驱动插帧", "NVIDIA Smooth Motion driver frame generation" }
			},
			{
				"set.nvsmooth.n",
				new string[2] { "AI 插帧近翻倍显示帧率，但增加延迟。适合 60+ FPS 单机，不建议竞技游戏。", "AI frame generation nearly doubles displayed FPS but adds latency. Best for 60+ FPS single-player; avoid competitive play." }
			},
			{
				"set.nvsmooth.nogpu",
				new string[2] { "需要 RTX 40 或 50 系显卡，本机显卡不支持驱动插帧", "Requires an RTX 40 or 50 series GPU; this machine's GPU does not support driver frame generation" }
			},
			{
				"set.nvsmooth.old",
				new string[2] { "驱动版本过低，需要 {0} 以上，升级后可用", "Driver too old; requires {0} or later, available after upgrading" }
			},
			{
				"set.nvsmooth.unavailable",
				new string[3] { "当前驱动未提供 Smooth Motion 设置，本项不可用", "The current driver does not expose Smooth Motion; this item is unavailable.", "現在のドライバーは Smooth Motion 設定を提供していないため、利用できません。" }
			},
			{
				"set.nvshader",
				new string[2] { "NVIDIA 着色器缓存无上限", "NVIDIA shader cache without size limit" }
			},
			{
				"set.nvshader.n",
				new string[2] { "取消该游戏的着色器缓存上限，减少重复编译卡顿；会占用更多磁盘空间。", "Removes this game's shader-cache cap to reduce recompilation stutter, at the cost of more disk space." }
			},
			{
				"frl.off",
				new string[2] { "关", "Off" }
			},
			{
				"dlss.latest",
				new string[2] { "最新", "Latest" }
			},
			{
				"set.nvdlss",
				new string[2] { "DLSS 换新算法", "DLSS model swap-in" }
			},
			{
				"set.nvdlss.n",
				new string[2] { "以驱动最新版替换 DLSS 模型；花屏或无法启动时关闭。", "Overrides the DLSS model with the latest driver version; disable if rendering or launch breaks." }
			},
			{
				"set.nvdlss.old",
				new string[2] { "驱动版本过低，需要 566.14 以上，升级后可用", "Driver too old; requires 566.14 or later, available after upgrading" }
			},
			{
				"set.nvdlss.nogpu",
				new string[2] { "仅 RTX 显卡支持 DLSS，本机不可用。", "DLSS requires an RTX GPU and is unavailable here." }
			},
			{
				"set.nvrebar",
				new string[2] { "ReBAR 强制开启", "Force ReBAR on" }
			},
			{
				"lib.rebar",
				new string[2] { "ReBAR 强开", "Force ReBAR" }
			},
			{
				"lib.fso",
				new string[3] { "禁用全屏优化", "Disable fullscreen optimizations", "全画面最適化を無効化" }
			},
			{
				"lib.fso.tip",
				new string[3] { "默认关闭；仅在游戏有兼容问题时尝试，下次启动游戏生效。", "Off by default. Try it for game compatibility issues; takes effect on the next game launch.", "既定はオフ。ゲームの互換性に問題がある場合に試してください。次回のゲーム起動時に適用されます。" }
			},
			{
				"lib.fso.failed",
				new string[3] { "全屏优化设置未能保存或应用，请重试。", "The fullscreen optimization setting could not be saved or applied. Please try again.", "全画面最適化の設定を保存または適用できませんでした。もう一度お試しください。" }
			},
			{
				"lib.rebar.warn",
				new string[2] { "ReBAR 强开不一定适合所有游戏。部分游戏开启后可能掉帧、卡顿或出现画面异常。\n\n建议按游戏单独测试，出现问题时关闭。正在运行的游戏可能需要重启后才生效。\n\n是否为当前游戏开启？", "Forcing ReBAR is not suitable for every game. Some games may lose frame rate, stutter, or show visual glitches.\n\nTest it for each game and turn it off if problems occur. A running game may need to be restarted for the change to take effect.\n\nEnable it for this game?" }
			},
			{
				"log.power.retry.stopped",
				new string[2] { "POWER event=apply-failed execution=stopped retry=manual", "POWER event=apply-failed execution=stopped retry=manual" }
			},
			{
				"lib.rebar.bios",
				new string[2] { "未检测到 NVIDIA 显卡的大容量显存映射，ReBAR 可能尚未在 BIOS 中开启，或硬件不支持。\n\n请在 BIOS 中确认 Resizable BAR 与 Above 4G Decoding 已开启，重启后再试。Pavise 无法代替 BIOS 开启此功能。\n\n本次未开启，游戏设置保持关闭。", "No large memory aperture was detected on the NVIDIA GPU. ReBAR may be disabled in BIOS or unsupported by the hardware.\n\nCheck Resizable BAR and Above 4G Decoding in BIOS, restart, then try again. Pavise cannot enable the BIOS feature.\n\nThe game setting remains off." }
			},
			{
				"lib.rebar.unknown",
				new string[2] { "无法确认 NVIDIA 显卡的 ReBAR 硬件状态，因此本次未开启。\n\n请先在 NVIDIA 控制面板的系统信息中确认 Resizable BAR 为“是”，并检查显卡驱动。检测失败不代表 BIOS 一定未开启。", "The NVIDIA GPU's ReBAR hardware state could not be verified. The setting remains off.\n\nCheck that Resizable BAR reads Yes in NVIDIA Control Panel > System Information, and check the graphics driver. A failed detection does not prove it is disabled in BIOS." }
			},
			{
				"lib.rebar.driver",
				new string[2] { "未检测到可用的 NVIDIA 驱动接口。此开关用于 NVIDIA 游戏驱动配置，不能用于 AMD 或 Intel 显卡。\n\n本次未开启；如使用 NVIDIA 显卡，请检查驱动安装。", "No available NVIDIA driver interface was detected. This switch configures NVIDIA game profiles, not AMD or Intel GPUs.\n\nThe setting remains off. If you use an NVIDIA GPU, check the driver installation." }
			},
			{
				"lib.rebar.tip",
				new string[2] { "仅对本游戏请求 NVIDIA ReBAR 强制开启；默认关闭。需兼容的 NVIDIA 显卡、驱动及 BIOS 已开启 ReBAR，不能代替 BIOS 开启。开关表示个人选择，不代表已生效或必然增帧。运行中更改后可能需要重启游戏；关闭或退局还原 Pavise 写入的驱动项。", "Request NVIDIA ReBAR for this game only; off by default. Requires compatible NVIDIA hardware and driver, with ReBAR enabled in BIOS. This switch records your preference, not verified activation or an FPS gain. Changes may require restarting the game. Turning off or ending the session restores driver settings owned by Pavise." }
			},
			{
				"set.nvrebar.n",
				new string[2] { "强制 ReBAR。可能增帧、掉帧或花屏；需 BIOS 开启，逐游戏测试。", "Forces ReBAR. It may help, hurt, or glitch; BIOS support is required. Test per game." }
			},
			{
				"set.nvrebar.det.on",
				new string[2] { "本机已开启，窗口 {0}", "Enabled on this machine, window {0}" }
			},
			{
				"set.nvrebar.det.off",
				new string[2] { "本机未开启，窗口 {0}，需在 BIOS 中开启后才有效", "Not enabled on this machine, window {0}; must be enabled in the BIOS to take effect" }
			},
			{
				"v16.spec.cpu",
				new string[2] { "处理器", "Processor" }
			},
			{
				"v16.spec.gpu",
				new string[2] { "显示适配器", "Display adapter" }
			},
			{
				"v16.spec.mem",
				new string[2] { "系统内存", "System memory" }
			},
			{
				"v16.device.hags.on",
				new string[2] { "已开启", "On" }
			},
			{
				"rep.log.none",
				new string[2] { "日志为空", "Log is empty" }
			},
			{
				"rep.clear.log",
				new string[3] { "清除日志", "Clear log", "ログを消去" }
			},
			{
				"rep.clear.ask",
				new string[2] { "清空 Pavise.log？归档日志不受影响。", "Clear Pavise.log? Archived logs are unaffected." }
			},
			{
				"gm.pausewu",
				new string[2] { "对局期间暂停 Windows 更新", "Pause Windows Update during matches" }
			},
			{
				"gm.pausemaint",
				new string[2] { "对局期间限制自动维护触发", "Restrict automatic maintenance triggers during matches" }
			},
			{
				"log.maint.1",
				new string[2] { "自动维护暂停 写入失败", "Automatic maintenance pause: write failed" }
			},
			{
				"log.maint.2",
				new string[2] { "自动维护触发配置已写入；已有任务未终止，退局恢复", "Maintenance trigger configuration written; existing tasks are not stopped; restores on match exit" }
			},
			{
				"log.maint.3",
				new string[2] { "自动维护已写回", "Automatic maintenance written back" }
			},
			{
				"log.maint.4",
				new string[2] { "自动维护写回失败 记录保留待重试", "Automatic maintenance write-back failed; the record is kept for retry" }
			},
			{
				"log.dpi.1",
				new string[2] { "已声明 DPI 感知", "DPI awareness declared for" }
			},
			{
				"log.dpi.2",
				new string[2] { "已撤销 DPI 感知声明", "DPI awareness declaration removed for" }
			},
			{
				"log.dpi.3",
				new string[2] { "DPI 兼容层操作失败", "DPI compatibility-layer operation failed" }
			},
			{
				"set.amdsam",
				new string[2] { "AMD Smart Access Memory", "AMD Smart Access Memory" }
			},
			{
				"t.amdsam.unsupported",
				new string[2] { "本机显卡或平台不支持", "Not supported by this GPU or platform" }
			},
			{
				"t.amdsam.byus",
				new string[2] { "已由 Pavise 开启，重启后生效，关闭开关即关回", "Enabled by Pavise; effective after a restart, and switching off turns it back off" }
			},
			{
				"t.amdsam.external",
				new string[2] { "系统中已开启，无需处理", "Already enabled in the system; no action required" }
			},
			{
				"t.amdsam.reverted",
				new string[2] { "开关已开启，但驱动值被外部改回。关闭后重新开启可重新写入", "The switch is on but the driver value was reverted externally; turn it off and on again to rewrite" }
			},
			{
				"t.amdsam.off",
				new string[2] { "未开启", "Not enabled" }
			},
			{
				"log.amdsam.1",
				new string[2] { "AMD SAM 本机不支持 未改动", "AMD SAM: not supported on this machine; nothing changed" }
			},
			{
				"log.amdsam.2",
				new string[2] { "AMD SAM 本机已是开启状态 不改动", "AMD SAM is already on for this machine, left untouched" }
			},
			{
				"log.amdsam.3",
				new string[2] { "AMD SAM 写入失败", "AMD SAM: write failed" }
			},
			{
				"log.amdsam.4",
				new string[2] { "AMD SAM 已开启 重启后生效", "AMD SAM enabled, takes effect after a restart" }
			},
			{
				"log.amdsam.5",
				new string[2] { "AMD SAM 关回失败 记录保留待重试", "AMD SAM switch-off failed; the record is kept for retry" }
			},
			{
				"log.amdsam.6",
				new string[2] { "AMD SAM 已关回", "AMD SAM turned back off" }
			},
			{
				"t.adlxtweaks.gfxmin",
				new string[2] { "最低核心频率", "minimum core clock" }
			},
			{
				"set.nvvrr",
				new string[2] { "G-SYNC 补齐窗口模式", "G-SYNC in windowed mode" }
			},
			{
				"set.nvvrr.n",
				new string[2] { "对局中把 G-SYNC 从仅全屏改为全屏加窗口，并允许该游戏使用可变刷新率；退局写回。部分面板在窗口模式下会闪烁。", "During play switches G-SYNC from fullscreen-only to fullscreen and windowed and allows the game to use variable refresh; written back at match end. Some panels flicker in windowed mode." }
			},
			{
				"set.nvvrr.na",
				new string[2] { "本机 G-SYNC 未开启或已含窗口模式，这一项没有可补的内容", "G-SYNC is off or already covers windowed mode on this machine, so there is nothing to add" }
			},
			{
				"log.nvvrr.1",
				new string[2] { "G-SYNC 窗口模式 读取驱动值失败", "G-SYNC windowed: reading the driver value failed" }
			},
			{
				"log.nvvrr.2",
				new string[2] { "G-SYNC 窗口模式 写入失败", "G-SYNC windowed: write failed" }
			},
			{
				"log.nvvrr.3",
				new string[2] { "G-SYNC 已补成全屏加窗口 退局写回", "G-SYNC set to fullscreen and windowed; written back at match end" }
			},
			{
				"log.nvvrr.4",
				new string[2] { "G-SYNC 窗口模式写回失败 记录保留待重试", "G-SYNC windowed write-back failed; the record is kept for retry" }
			},
			{
				"log.nvvrr.5",
				new string[2] { "G-SYNC 模式已被外部改动 不写回 只清账", "G-SYNC mode was changed externally; not written back, record cleared" }
			},
			{
				"log.nvvrr.6",
				new string[2] { "G-SYNC 模式已写回", "G-SYNC mode written back" }
			},
			{
				"log.nvvrr.7",
				new string[2] { "检测到上次未写回的 G-SYNC 模式 已写回", "Detected a G-SYNC mode left from last time; written back" }
			},
			{
				"set.intel.endurance",
				new string[2] { "对局关闭 Endurance Gaming", "Turn off Endurance Gaming during play" }
			},
			{
				"set.intel.endurance.n",
				new string[2] { "电池供电时 Intel 会按面板刷新率的分数封顶帧率；对局中关掉它，退局写回。续航缩短。", "On battery Intel caps the frame rate to a fraction of the panel refresh; this turns it off during play and writes it back at match end. Battery life drops." }
			},
			{
				"set.intel.endurance.desktop",
				new string[2] { "本机没有电池，Endurance Gaming 在此不起作用", "This machine has no battery, so Endurance Gaming has no effect here" }
			},
			{
				"log.intelend.1",
				new string[2] { "Endurance Gaming 读取失败 未改动", "Endurance Gaming: read failed; nothing changed" }
			},
			{
				"log.intelend.2",
				new string[2] { "Endurance Gaming 写入失败", "Endurance Gaming: write failed" }
			},
			{
				"log.intelend.3",
				new string[2] { "Endurance Gaming 已关闭 退局写回", "Endurance Gaming turned off; written back at match end" }
			},
			{
				"log.intelend.4",
				new string[2] { "Endurance Gaming 写回失败 记录保留待重试", "Endurance Gaming write-back failed; the record is kept for retry" }
			},
			{
				"log.intelend.5",
				new string[2] { "Endurance Gaming 已写回", "Endurance Gaming written back" }
			},
			{
				"log.intelend.6",
				new string[2] { "检测到上次未写回的 Endurance Gaming 已写回", "Detected an Endurance Gaming setting left from last time; written back" }
			},
			{
				"gm.laptopperf",
				new string[2] { "对局切换厂商性能档", "Switch to the vendor performance mode during play" }
			},
			{
				"log.laptopperf.4",
				new string[2] { "厂商性能档切回失败 记录保留待重试", "Vendor performance mode switch-back failed; the record is kept for retry" }
			},
			{
				"log.laptopperf.5",
				new string[2] { "厂商性能档已被外部改动 不切回 只清账", "Vendor performance mode was changed externally; not switched back, record cleared" }
			},
			{
				"log.laptopperf.6",
				new string[2] { "厂商性能档已切回", "Vendor performance mode switched back" }
			},
			{
				"log.laptopperf.7",
				new string[2] { "检测到上次未切回的厂商性能档 已切回", "Detected a vendor performance mode left from last time; switched back" }
			},
			{
				"t.linkmetric.name",
				new string[2] { "有线与无线并存时的默认路由", "Default route with wired and wireless both up" }
			},
			{
				"t.linkmetric.good",
				new string[2] { "走有线", "Wired" }
			},
			{
				"t.linkmetric.bad",
				new string[2] { "走无线", "Wireless" }
			},
			{
				"linkmetric.ok",
				new string[2] { "默认路由已在有线口", "The default route is already on the wired adapter" }
			},
			{
				"linkmetric.wireless",
				new string[2] { "有线口带网关且已连通，但默认路由落在无线口。Wi-Fi 6E 和 7 的自动跃点与千兆有线打平。修复把有线口跃点设为 5", "The wired adapter has a gateway and is up, yet the default route sits on wireless: the automatic metric of Wi-Fi 6E/7 ties with gigabit wired. The repair sets the wired metric to 5" }
			},
			{
				"linkmetric.repaired",
				new string[2] { "已由 Pavise 把有线口跃点设为 5，撤销即恢复自动跃点", "Pavise set the wired metric to 5; reverting restores the automatic metric" }
			},
			{
				"linkmetric.nowired",
				new string[2] { "没有带网关的有线口", "No wired adapter with a gateway" }
			},
			{
				"linkmetric.ambiguous",
				new string[2] { "多张有线口都带网关，不猜出口，不修", "More than one wired adapter has a gateway; the exit is not guessed, so nothing is repaired" }
			},
			{
				"log.linkmetric.1",
				new string[2] { "有线跃点写入失败", "Wired metric write failed" }
			},
			{
				"log.linkmetric.2",
				new string[2] { "有线口跃点已设为 5 默认路由改走有线", "Wired metric set to 5; the default route now prefers wired" }
			},
			{
				"log.linkmetric.3",
				new string[2] { "有线跃点恢复失败 记录保留待重试", "Wired metric restore failed; the record is kept for retry" }
			},
			{
				"log.linkmetric.4",
				new string[2] { "有线口已恢复自动跃点", "Wired adapter back on the automatic metric" }
			},
			{
				"gm.pausesvc",
				new string[2] { "暂停非必要系统服务", "Pause nonessential system services" }
			},
			{
				"standbycleaner.switch.savefailed",
				new string[2] { "开关设置未能保存，本次未启动或已停止清理，请检查配置写入权限。", "Could not save the switch. Cleanup was not started or has been stopped for this run. Check settings write access." }
			},
			{
				"gm.standbycleaner",
				new string[2] { "待机内存清理", "Standby memory cleanup" }
			},
			{
				"gm.standbycleaner.cfgsub",
				new string[2] { "只覆盖此游戏的开关，开启优先于全局关闭；参数在“优化策略 → 会话附加”统一设置。对局中满足双阈值才清理待机缓存；清理瞬间会卡顿一下，两次清理至少间隔约一分钟。", "For this game only; On overrides global Off. Shared parameters are in Optimization policy → Session extras. During play, both thresholds must pass; the purge itself stutters the game briefly, and purges are at least about a minute apart." }
			},
			{
				"gm.standbycleaner.needadmin",
				new string[2] { "开启待机内存清理需要管理员权限；已开启时仍可关闭。参数可以先保存，不会自动开启。", "Administrator rights are required to enable standby cleanup. An existing opt-in can still be turned off. Saving parameters does not enable it." }
			},
			{
				"standbycleaner.warn",
				new string[2] { "开启待机内存清理？\r\n\r\n仅在对局中、两项内存阈值同时满足时清空系统待机缓存。清理调用不可中断，可能引起瞬时卡顿，缓存随后可能需要重新读盘，不保证提升性能。两次清理至少间隔约一分钟。\r\n\r\n由环境页开关控制，模式切换不改变选择；参数可通过「设置参数」调整。", "Enable standby memory cleanup?\r\n\r\nPurges system standby cache during matches only when both memory thresholds pass. The call cannot be interrupted and may cause a hitch and later disk re-reads; performance gains are not guaranteed. Purges are at least about a minute apart.\r\n\r\nControlled by the Environment switch, independently of mode. Use Parameters to adjust thresholds." }
			},
			{
				"standbycleaner.config.invalid",
				new string[2] { "待机清理参数损坏，已停止清理。请先在“设置参数”核对并保存，再手动开启；已显示开启的项目需先关闭再开。", "Parameters are invalid; cleanup has stopped. Review and save Parameters, then enable cleanup manually. If a switch already shows On, turn it off and on again." }
			},
			{
				"standbycleaner.options",
				new string[2] { "设置参数", "Parameters" }
			},
			{
				"standbycleaner.options.title",
				new string[2] { "待机内存清理参数", "Standby cleanup parameters" }
			},
			{
				"standbycleaner.options.hint",
				new string[2] { "参数对所有游戏共享。保存不会开启功能，也不会立即清理。\r\n仅在对局中且开关开启时检查；两项阈值必须同时满足（且）：", "Parameters are shared across games. Saving does not enable or immediately run cleanup.\r\nChecks only during play while enabled; BOTH conditions must be met (AND):" }
			},
			{
				"standbycleaner.options.repair",
				new string[2] { "原参数损坏，下面暂以 Pavise 默认值预填，尚未写入。请核对后保存；之后仍需手动关闭并重新开启待机清理。", "The stored parameters are invalid. Pavise defaults are shown below but have not been saved. Review and save them, then turn standby cleanup off and on manually." }
			},
			{
				"standbycleaner.options.list",
				new string[2] { "缓存列表 ≥（MB）", "Cache list >= (MB)" }
			},
			{
				"standbycleaner.options.list.sub",
				new string[2] { "列表 = 待机缓存 + 系统工作集（SystemCache），不是仅待机缓存。", "List = standby cache + system working set (SystemCache), not standby alone." }
			},
			{
				"standbycleaner.options.free",
				new string[2] { "空闲内存 <（MB）", "Free memory < (MB)" }
			},
			{
				"standbycleaner.options.free.sub",
				new string[2] { "空闲 = 空闲页 + 零页，不含待机缓存；不是任务管理器中的“可用”。", "Free = free + zeroed pages, excluding standby; this is not Task Manager's Available value." }
			},
			{
				"standbycleaner.options.poll",
				new string[2] { "检查间隔（毫秒）", "Check interval (ms)" }
			},
			{
				"standbycleaner.options.poll.sub",
				new string[2] { "间隔越短，检查越频繁；不会调整系统计时器精度。", "A shorter interval checks more often; it does not change system timer resolution." }
			},
			{
				"standbycleaner.options.units",
				new string[2] { "MB 按 1,048,576 字节计算。两项阈值范围 0–1,048,576 MB；间隔范围 250–300,000 毫秒。成功清理一次后至少冷却约一分钟（不少于 8 个检查间隔）才会再次清理。", "MB means 1,048,576 bytes. Both thresholds accept 0–1,048,576 MB; the interval accepts 250–300,000 ms. After a successful purge, cleanup cools down for at least about a minute (no less than eight intervals) before purging again." }
			},
			{
				"standbycleaner.options.preset",
				new string[2] { "快速设置：点击填入下方数值，保存后生效", "Quick setup: click to fill the fields below; takes effect after saving" }
			},
			{
				"standbycleaner.options.preset.light",
				new string[2] { "保守", "Conservative" }
			},
			{
				"standbycleaner.options.preset.standard",
				new string[2] { "标准", "Standard" }
			},
			{
				"standbycleaner.options.preset.heavy",
				new string[2] { "激进", "Aggressive" }
			},
			{
				"standbycleaner.options.save",
				new string[2] { "保存参数", "Save" }
			},
			{
				"standbycleaner.options.invalid",
				new string[2] { "请输入有效整数：两项阈值为 0–1,048,576 MB，检查间隔为 250–300,000 毫秒。", "Enter whole numbers: both thresholds must be 0–1,048,576 MB and the interval must be 250–300,000 ms." }
			},
			{
				"standbycleaner.options.savefailed",
				new string[2] { "参数未能保存，本次修改未生效。请检查配置写入权限，或稍后重试。", "Parameters could not be saved; this change did not take effect. Check configuration write permissions or try again later." }
			},
			{
				"netthrottle.ok",
				new string[2] { "当前值 {0} 在正常范围内，无需校正", "Current value {0} is within the normal range; no correction needed" }
			},
			{
				"netthrottle.broken",
				new string[2] { "当前值 {0} 不属于有效配置（1–70 或 0xFFFFFFFF），可修复到系统默认值 10；如网络异常可撤回", "Current value {0} is not valid (1–70 or 0xFFFFFFFF); it can be restored to the system default of 10. Revert if network issues occur" }
			},
			{
				"netthrottle.disabled",
				new string[2] { "当前值 0xFFFFFFFF 表示已关闭网络节流，属于有效配置，保留现状，无需修复", "Current value 0xFFFFFFFF disables network throttling; this is valid and will be preserved, with no repair needed" }
			},
			{
				"netthrottle.invalid",
				new string[2] { "配置值无效", "Invalid setting" }
			},
			{
				"netthrottle.absent",
				new string[2] { "当前未设置该值，即为系统默认，无需校正", "The value is not set, which is the system default; no correction needed" }
			},
			{
				"quantum.absent",
				new string[2] { "当前未设置该值，即为系统默认，无需校正", "The value is not set, which is the system default; no correction needed" }
			},
			{
				"quantum.short",
				new string[2] { "短时间片", "short slice" }
			},
			{
				"quantum.long",
				new string[2] { "长时间片", "long slice" }
			},
			{
				"quantum.variable",
				new string[2] { "可变", "variable" }
			},
			{
				"quantum.fixed",
				new string[2] { "定长", "fixed" }
			},
			{
				"fth.off",
				new string[2] { "本机容错堆功能已关闭，系统不会给任何程序挂堆垫片。", "The fault tolerant heap is disabled on this machine, so the system will not attach a heap shim to any program." }
			},
			{
				"fth.clean",
				new string[2] { "未发现被挂堆垫片的程序，无需处理。", "No program was found carrying a heap shim; no action required." }
			},
			{
				"fth.cleared",
				new string[2] { "已由 Pavise 解除，垫片文件保留在原处未删除，可随时还原。", "Already removed by Pavise. The shim file is left in place and untouched, so this can be reverted at any time." }
			},
			{
				"fth.hit",
				new string[2] { "系统已给 {0} 挂上容错堆垫片。", "The system has attached a fault-tolerant-heap shim to {0}." }
			},
			{
				"fth.hit.why",
				new string[2] { "{0} 在 {1} 分钟内崩溃 {2} 次，已挂载 FTH 垫片。", "{0} crashed {2} times within {1} minutes, so an FTH shim was attached." }
			},
			{
				"audit.fth.confirm",
				new string[2] { "解除 FTH 垫片？\r\n\r\n可能减少开销，也可能恢复崩溃；文件保留，可还原。", "Remove the FTH shim?\r\n\r\nIt may reduce overhead or restore crashes. The file remains; reversible." }
			},
			{
				"gs.moff",
				new string[2] { "总开关关闭", "Master switch off" }
			},
			{
				"gs.noff",
				new string[2] { "未开启", "Not enabled" }
			},
			{
				"gs.noproc",
				new string[2] { "未检测到进程", "No process detected" }
			},
			{
				"gs.thr",
				new string[2] { "已压制 {0}", "Suppressed {0}" }
			},
			{
				"gs.prot",
				new string[2] { "另有 {0} 个受内核保护，无法压制", "{0} more are kernel-protected and cannot be suppressed" }
			},
			{
				"white.platform.badge",
				new string[3] { "平台保护", "Platform", "プラットフォーム" }
			},
			{
				"white.platform.detected",
				new string[3] { "本机平台：{0}", "Detected platforms: {0}", "検出したプラットフォーム：{0}" }
			},
			{
				"white.platform.restore",
				new string[3] { "恢复平台保护", "Restore platform protection", "プラットフォーム保護を復元" }
			},
			{
				"white.platform.hint",
				new string[3] { "删除平台保护项后，Pavise 可按后台规则压制该平台，重启或更新后保持选择。\r\n\r\n游戏本体、手动白名单和其他保护独立生效。点击「恢复平台保护」可重新启用。", "Remove a platform entry to allow background suppression. Deletions persist.\r\n\r\nGames, your whitelist and other protections stay independent. Restore platform protection brings the entries back.", "保護項目を削除すると 通常のバックグラウンド規則が適用されます。再起動や更新後も選択を保持します。\r\n\r\nゲーム本体と手動の許可リストは別に保護されます。保護の復元で再び有効にできます。" }
			},
			{
				"white.auto.badge",
				new string[2] { "自动规则", "Automatic" }
			},
			{
				"white.auto.state",
				new string[2] { "查看全部", "View all" }
			},
			{
				"white.auto.system",
				new string[3] { "Windows 系统进程", "Windows system processes", "Windows システムプロセス" }
			},
			{
				"white.auto.system.list",
				new string[3] { "系统预置进程：{0}", "System presets: {0}", "システム既定プロセス：{0}" }
			},
			{
				"white.auto.details.system.note",
				new string[3] { "系统预置项集中展示，可搜索具体进程名。手动白名单和平台保护单独显示。", "System presets are grouped here. Search by process name. Manual rules and platform protection are listed separately.", "システムの既定項目をまとめて表示し、プロセス名で検索できます。手動の許可規則とプラットフォーム保護は別に表示します。" }
			},
			{
				"white.auto.accel",
				new string[2] { "网游加速器", "Game network accelerators" }
			},
			{
				"white.auto.accel.list",
				new string[2] { "明确进程：{0}", "Explicit processes: {0}" }
			},
			{
				"white.auto.hardware",
				new string[2] { "显卡驱动与硬件功耗散热控制", "Graphics drivers and hardware power/thermal control" }
			},
			{
				"white.auto.hardware.list",
				new string[2] { "明确进程：{0}", "Explicit processes: {0}" }
			},
			{
				"white.auto.anticheat",
				new string[2] { "反作弊与游戏保护组件", "Anti-cheat and game protection" }
			},
			{
				"white.auto.anticheat.list",
				new string[2] { "已识别：{0}", "Recognized: {0}" }
			},
			{
				"white.auto.peripheral",
				new string[2] { "输入、音频与外设驱动链", "Input, audio, and peripheral chains" }
			},
			{
				"white.auto.peripheral.list",
				new string[2] { "进程名关键词：{0}", "Process-name keywords: {0}" }
			},
			{
				"white.auto.streaming",
				new string[3] { "直播与推流工具", "Live streaming and broadcast tools", "ライブ配信・送出ツール" }
			},
			{
				"white.auto.streaming.list",
				new string[3] { "直播工具：{0}", "Broadcast tools: {0}", "配信ツール：{0}" }
			},
			{
				"white.auto.details.products",
				new string[3] { "直播工具目录", "Broadcast tool catalog", "配信ツール一覧" }
			},
			{
				"white.auto.details.streaming.note",
				new string[3] { "自动识别直播主程序及目录中列出的专用组件，连同身份核验通过的子孙进程一起保护；已确认且仍存活的家族成员在父进程退出后继续保护。独立运行的组件须命中自身识别规则。游戏家族后台治理选项不取消这项保护。", "Recognized broadcast applications and listed dedicated components protect their identity-verified descendants. Confirmed living family members stay protected after their parent exits. Independently launched components must match their own recognition rules. Game-family background governance does not disable this protection.", "認識した配信アプリと一覧の専用コンポーネント、および識別を確認した子孫プロセスを保護します。確認済みで実行中のメンバーは親の終了後も保護します。独立起動したコンポーネントには個別の照合規則が必要です。ゲーム関連のバックグラウンド制御を有効にしても、この保護は維持されます。" }
			},
			{
				"white.auto.details.body",
				new string[2] { "以下内容直接来自当前版本实际使用的识别目录；点击文本可选择和复制。", "This list is generated directly from the recognition catalogs used by this version. Select the text to copy it." }
			},
			{
				"white.auto.details.processes",
				new string[2] { "明确进程名（不含 .exe）", "Explicit process names (without .exe)" }
			},
			{
				"white.auto.details.peripheral.note",
				new string[3] { "名称、文件描述、产品名或公司名命中输入、音频与外设关键词，或命中本机在场设备派生的厂商词时自动保护。", "Names, file descriptions, product names, or company names matching input/audio/peripheral keywords, or vendor tokens from present devices, are protected automatically.", "名前・ファイル説明・製品名・会社名の入出力や周辺機器キーワード、または接続デバイスのメーカー名で自動保護します。" }
			},
			{
				"white.auto.details.tokens",
				new string[2] { "模糊识别关键词（进程名中包含即命中）", "Fuzzy-match tokens (a process-name substring is enough)" }
			},
			{
				"white.auto.details.hardware.note",
				new string[3] { "显卡驱动容器及功耗、睿频、风扇工具自动保护；名称关键词采用子串匹配。", "GPU driver containers and power, boost, and fan tools are protected using process-name substring matching.", "GPUドライバーのコンテナーと電力・ブースト・ファン制御ツールを自動保護します。プロセス名に含まれるキーワードで照合します。" }
			},
			{
				"white.auto.details.groups",
				new string[2] { "反作弊分组与进程名（不含 .exe）", "Anti-cheat groups and process names (without .exe)" }
			},
			{
				"white.auto.details.anticheatdirs",
				new string[2] { "反作弊专用目录（完整目录段匹配，含目录内辅助进程）", "Anti-cheat directories (complete directory segments, including helper processes)" }
			},
			{
				"white.auto.details.namekeywords",
				new string[2] { "进程名关键词", "Process-name keywords" }
			},
			{
				"white.auto.details.descriptionwords",
				new string[2] { "文件描述、产品名与公司名关键词", "Keywords in file descriptions, product names, and company names" }
			},
			{
				"white.auto.details.vendors",
				new string[2] { "本机在场设备派生的厂商关键词", "Vendor tokens derived from devices currently present" }
			},
			{
				"white.badge.family",
				new string[2] { "连子进程", "With children" }
			},
			{
				"white.badge.name.sub",
				new string[3] { "按进程名匹配入口，并保护已验证的子进程", "Matches anchors by process name and protects verified descendants", "プロセス名で起点を照合し、確認済みの子孫を保護" }
			},
			{
				"white.state.running",
				new string[2] { "{0} 个运行中", "{0} running" }
			},
			{
				"white.state.idle",
				new string[2] { "未运行", "Not running" }
			},
			{
				"white.required.locked",
				new string[2] { "系统必需的内置项，不可删除", "Built-in entry required by the system, cannot be removed" }
			},
			{
				"white.browse.filter",
				new string[2] { "程序或快捷方式|*.exe;*.lnk|程序|*.exe|快捷方式|*.lnk", "Programs or shortcuts|*.exe;*.lnk|Programs|*.exe|Shortcuts|*.lnk" }
			},
			{
				"white.pick.title",
				new string[2] { "选运行中的程序", "Pick a running program" }
			},
			{
				"white.pick.sub",
				new string[2] { "列出当前会话的应用，包含最小化和托盘应用；不含系统进程和反作弊。", "Lists apps in this session, including minimized and tray apps; excludes system processes and anti-cheat." }
			},
			{
				"white.pick.busy",
				new string[2] { "正在枚举运行中的程序", "Enumerating running programs" }
			},
			{
				"white.pick.failed",
				new string[2] { "读取程序失败，请重新打开窗口重试。", "Could not read programs. Reopen this window to retry." }
			},
			{
				"white.pick.none",
				new string[2] { "未找到可添加的应用，可通过“添加 EXE”手动选择。", "No apps available to add. Use Add EXE to select one manually." }
			},
			{
				"white.pick.count",
				new string[2] { "找到 {0} 个可添加的程序", "Found {0} programs to add" }
			},
			{
				"white.pick.add",
				new string[2] { "加入白名单", "Add to whitelist" }
			},
			{
				"white.pick.add.n",
				new string[2] { "加入 {0} 个", "Add {0}" }
			},
			{
				"white.pick.procs",
				new string[2] { "{0} 进程", "{0} processes" }
			},
			{
				"white.kind.family",
				new string[2] { "应用家族", "App family" }
			},
			{
				"white.kind.path",
				new string[3] { "EXE 路径家族", "EXE path family", "EXEパス家族" }
			},
			{
				"white.kind.name",
				new string[2] { "进程名", "Process name" }
			},
			{
				"white.matches",
				new string[2] { "当前匹配 {0} 个", "Currently matching {0}" }
			},
			{
				"white.matches.pending",
				new string[2] { "正在核对当前匹配", "Checking current matches" }
			},
			{
				"white.duplicate",
				new string[2] { "该规则已存在，或输入的不是有效的 EXE 与进程名。", "This rule already exists, or the input is not a valid EXE or process name." }
			},
			{
				"white.save.failed",
				new string[2] { "白名单未能安全写入磁盘，本次修改未生效。请检查数据目录权限或磁盘状态。", "The whitelist could not be safely written to disk, so this change did not take effect. Check the data directory permissions or disk status." }
			},
			{
				"white.required",
				new string[2] { "Windows 稳定运行所必需的内置保护，不可删除，仅匹配受信任的系统路径。", "Built-in protection required for Windows to run stably; cannot be removed, matches trusted system paths only." }
			},
			{
				"white.required.badge",
				new string[2] { "内置保护", "Built-in protection" }
			},
			{
				"white.reset.confirm",
				new string[2] { "恢复默认预设将删除全部自定义白名单规则，且无法撤销。", "Restoring the default preset will delete all custom whitelist rules and cannot be undone." }
			},
			{
				"set.dvr",
				new string[2] { "关闭 Game DVR 与 Xbox 后台录制", "Disable Game DVR and Xbox background recording" }
			},
			{
				"env.dvr.waiting",
				new string[2] { "已开启 · 等待守护启动", "On; waiting for the guard" }
			},
			{
				"env.dvr.active",
				new string[2] { "系统游戏录制已关闭", "Game recording disabled" }
			},
			{
				"env.dvr.restored",
				new string[2] { "原录制设置已恢复", "Recording settings restored" }
			},
			{
				"gm.gpudemote",
				new string[2] { "后台 GPU 优先级降级", "Background GPU priority demotion" }
			},
			{
				"gm.boost",
				new string[2] { "性能优先级", "Performance priority" }
			},
			{
				"gm.boost.cpu",
				new string[2] { "CPU 高优先级", "High CPU priority" }
			},
			{
				"gm.boost.memory",
				new string[2] { "内存保留优先级", "Memory retention priority" }
			},
			{
				"gm.boost.io",
				new string[2] { "磁盘 I/O 高优先级", "High disk I/O priority" }
			},
			{
				"gm.boost.gpu",
				new string[2] { "GPU 高优先级", "High GPU priority" }
			},
			{
				"gm.lane",
				new string[2] { "高负载线程提优", "Busy CPU thread boost" }
			},
			{
				"gm.lane.sub",
				new string[2] { "默认开启，可逐游戏关闭。按 CPU 耗时选候选，不代表帧关键线程；误选可能拖慢游戏。已生效的候选提优会维持游戏高优先级，请按相同场景对照测试。", "On by default and can be disabled per game. CPU time identifies a busy candidate, not a frame-critical thread; a wrong choice can slow the game. An active candidate boost keeps the game at High priority. Compare the same scene before keeping it enabled." }
			},
			{
				"ac.roster",
				new string[2] { "反作弊相容名单", "Anti-cheat compatibility list" }
			},
			{
				"ac.roster.empty",
				new string[2] { "名单为空", "The list is empty" }
			},
			{
				"ac.roster.confirm",
				new string[2] { "清空后，下局重新尝试提优；若仍被拒绝会再次记录。确认？", "Clear the list and retry boost next match? Refused games will be recorded again." }
			},
			{
				"log.tamer.pin.1",
				new string[3] { "反作弊绑核 ", "Anti-cheat pinning ", "アンチチートのコア固定 " }
			},
			{
				"log.tamer.pin.2",
				new string[3] { " 已限定到核心 ", " confined to cores ", " コアを限定 " }
			},
			{
				"log.tamer.pin.3",
				new string[3] { " 写入被拒 本次运行不再尝试", " write refused; not retried this run", " 書き込みが拒否されました。今回の実行では再試行しません" }
			},
			{
				"btn.roster.clear",
				new string[2] { "清空", "Clear" }
			},
			{
				"gm.lane.unsupported",
				new string[3] { "掌机档和物理核不足 6 个的机器不提供：可运行线程多于可用核心时，被提优的线程会抢占它自己正在等的线程，帧率反而下降。", "Not available in Handheld or on machines with fewer than 6 physical cores: when runnable threads outnumber available cores, the boosted thread preempts the very threads it is waiting on and frame rate drops instead.", "携帯機モードと物理コア 6 未満の環境では提供しません。実行可能スレッドが利用可能コアを上回ると、昇格したスレッドが自らの待ち相手を横取りし、かえってフレームレートが下がります。" }
			},
			{
				"gm.pausedl",
				new string[2] { "暂停后台下载与传递优化", "Pause background downloads and Delivery Optimization" }
			},
			{
				"set.logwrites",
				new string[3] { "记录运行日志", "Write the run log", "実行ログを記録" }
			},
			{
				"log.logwrites.off",
				new string[2] { "运行日志记录已关闭 后续动作不再写入 重新开启即恢复", "Run log writing turned off; later actions are not recorded. Turn it back on to resume." }
			},
			{
				"log.logwrites.on",
				new string[2] { "运行日志记录已开启", "Run log writing turned on" }
			},
			{
				"v20.log.paused",
				new string[3] { "日志记录已关闭 下方为关闭前的记录", "Log writing is off; the entries below predate it", "ログの記録は停止中です。停止前の記録を表示しています" }
			},
			{
				"set.autostart",
				new string[2] { "开机自启", "Launch at startup" }
			},
			{
				"set.autostart.n",
				new string[3] { "管理员任务后台启动，不弹界面；原 EXE 改名不影响自启。", "Starts silently via an admin task; renaming the original EXE keeps autostart working.", "管理者タスクで画面を表示せず起動。元の EXE を改名しても自動起動できます。" }
			},
			{
				"set.autohide",
				new string[2] { "检测到游戏后自动收起窗口", "Auto-hide window when a game is detected" }
			},
			{
				"set.autohide.n",
				new string[2] { "检测到游戏 10 秒后收回托盘；每局仅一次。", "Hides to tray 10 seconds after game detection, once per match." }
			},
			{
				"set.tab.app.h",
				new string[2] { "管理方式、启动与语言", "Control mode, startup and language" }
			},
			{
				"set.tab.maint.h",
				new string[2] { "清除配置与着色器缓存", "Reset configuration and shader cache" }
			},
			{
				"set.tab.appearance.h",
				new string[2] { "主题色 封面 特效", "Theme color, backdrop, effects" }
			},
			{
				"set.lowlatency",
				new string[2] { "游戏低延迟", "Game low latency" }
			},
			{
				"set.lowlatency.savefail",
				new string[2] { "未能保存游戏低延迟设置，请重试。", "Could not save the game low latency preference. Please try again." }
			},
			{
				"set.hint",
				new string[2] { "管理方式、外观与日常偏好。", "Control mode, appearance and everyday preferences." }
			},
			{
				"sec.pergame",
				new string[2] { "逐游戏，对局开始时下发，结束后还原", "Per-game, applied at match start and restored afterwards" }
			},
			{
				"sec.amd",
				new string[2] { "AMD 显卡，对局开始时全局生效，结束后还原", "AMD GPUs, applied globally at match start and restored afterwards" }
			},
			{
				"gfx.tab.common",
				new string[2] { "通用", "General" }
			},
			{
				"set.amd.nosup",
				new string[2] { "本机 GPU 或驱动版本不支持该功能，本项停用", "This machine's GPU or driver version does not support this feature; this option is disabled" }
			},
			{
				"sec.app",
				new string[2] { "常规", "General" }
			},
			{
				"dlg.ok",
				new string[2] { "知道了", "Got it" }
			},
			{
				"dlg.confirm",
				new string[2] { "确定", "OK" }
			},
			{
				"dlg.cancel",
				new string[2] { "取消", "Cancel" }
			},
			{
				"dlg.tag.info",
				new string[2] { "提示", "Info" }
			},
			{
				"dlg.tag.warn",
				new string[2] { "注意", "Notice" }
			},
			{
				"dlg.tag.danger",
				new string[2] { "警告", "Warning" }
			},
			{
				"dlg.tag.done",
				new string[2] { "完成", "Done" }
			},
			{
				"sec.maint",
				new string[2] { "维护", "Maintenance" }
			},
			{
				"nav.graphics",
				new string[2] { "显卡", "Graphics" }
			},
			{
				"audit.input.devices",
				new string[2] { "输入设备", "Input devices" }
			},
			{
				"nav.audit",
				new string[2] { "系统体检", "System Checkup" }
			},
			{
				"irqmove.logprefix",
				new string[2] { "中断亲和性调整 ", "IRQ affinity " }
			},
			{
				"nav.irq",
				new string[2] { "设备中断", "Device Interrupts" }
			},
			{
				"t.interruptattribution.7",
				new string[2] { "内核会话丢失 {0} 个事件 {1} 个缓冲 本轮观测作废", "The kernel session dropped {0} events and {1} buffers; this round of observation is void" }
			},
			{
				"log.interruptattribution.lossy",
				new string[2] { "中断观测 内核会话丢失 {0} 个事件 {1} 个缓冲 本轮数据不予采信 丢失事件集中于负载最高的核心与驱动 会低估应被指认的设备", "Interrupt observation: the kernel session dropped {0} events and {1} buffers, so this round is discarded. Dropped events cluster on the busiest cores and drivers, which understates the device that should be flagged" }
			},
			{
				"irq.probe.busy",
				new string[2] { "中断观测正在被另一个会话使用。", "Interrupt capture is already in use by another session." }
			},
			{
				"irq.state.needadmin",
				new string[2] { "需要管理员权限启动 ETW 中断采样。", "Administrator rights required for ETW interrupt capture." }
			},
			{
				"irqmove.nosession",
				new string[2] { "无法启动中断采样。", "Cannot start interrupt capture." }
			},
			{
				"irq.fail.nokernel",
				new string[2] { "本机不支持独立内核会话 做不了中断归因。", "This system does not support a standalone kernel session, so interrupt attribution is unavailable." }
			},
			{
				"irq.fail.denied",
				new string[2] { "中断采样被系统拒绝。常见于安全软件拦截，或低于 Windows 10 1703 的系统。", "Interrupt capture was denied by the system. Common causes: security software, or Windows older than 10 1703." }
			},
			{
				"irq.fail.start",
				new string[2] { "中断采样会话创建失败 代码 ", "Interrupt capture session could not be created, code " }
			},
			{
				"irq.fail.open",
				new string[2] { "中断采样实时会话打开失败 代码 ", "Interrupt capture real-time session could not be opened, code " }
			},
			{
				"contact.title",
				new string[2] { "反馈与交流", "Feedback & Community" }
			},
			{
				"contact.sub",
				new string[2] { "Bug 反馈、功能需求和使用问题均可通过以下方式联系。", "Bug reports, feature requests and usage questions are all welcome via the channels below." }
			},
			{
				"contact.qq",
				new string[2] { "QQ 一群 已满", "QQ Group 1 full" }
			},
			{
				"contact.qq2",
				new string[2] { "QQ 二群 已满", "QQ Group 2 full" }
			},
			{
				"contact.qq3",
				new string[2] { "QQ 三群 已满", "QQ Group 3 full" }
			},
			{
				"contact.qq4",
				new string[2] { "QQ 四群", "QQ Group 4" }
			},
			{
				"contact.qq5",
				new string[2] { "QQ 五群", "QQ Group 5" }
			},
			{
				"contact.douyin",
				new string[2] { "作者抖音号 功能需求和 BUG 反馈", "Author's Douyin, for feature requests and bug reports" }
			},
			{
				"contact.open",
				new string[2] { "打开", "Open" }
			},
			{
				"contact.update",
				new string[2] { "版本更新", "Updates" }
			},
			{
				"contact.copy",
				new string[2] { "复制", "Copy" }
			},
			{
				"contact.copied",
				new string[2] { "已复制", "Copied" }
			},
			{
				"contact.copyfail",
				new string[2] { "复制失败", "Copy failed" }
			},
			{
				"contact.free.n",
				new string[3] { "GPLv3 允许商用及再分发。官方版本在官网免费提供，捐赠自愿。", "GPLv3 permits commercial use and redistribution. Official downloads are free; donations are optional.", "GPLv3 に基づく商用利用・再配布が可能です。公式版は無料、寄付は任意です。" }
			},
			{
				"contact.close",
				new string[2] { "关闭", "Close" }
			},
			{
				"audit.sub",
				new string[2] { "检查系统状态与显卡中断；根据体检结果处理和验收。", "Check system status and GPU interrupts; apply and verify changes from the results." }
			},
			{
				"audit.mem.title",
				new string[2] { "内存压缩", "Memory compression" }
			},
			{
				"audit.mem.on",
				new string[2] { "开启", "On" }
			},
			{
				"audit.mem.healthy",
				new string[2] { "已开启 · 正常", "Enabled · Healthy" }
			},
			{
				"audit.mem.todo",
				new string[2] { "已关闭 · 建议开启", "Disabled · Enable recommended" }
			},
			{
				"audit.mem.unknown",
				new string[2] { "无法确认内存压缩状态，本次不执行修复。", "Memory compression state could not be verified; repair is unavailable." }
			},
			{
				"audit.mem.owned",
				new string[2] { "保留了 Pavise 改动前的还原记录。", "A restore record for Pavise's changes is retained." }
			},
			{
				"audit.mem.policy",
				new string[2] { "建议开启内存压缩；点击开启或一键修复执行，仅处理压缩，不联动页合并。", "Enable memory compression here or with Fix all. Page combining is left unchanged." }
			},
			{
				"audit.mem.keep",
				new string[2] { "内存压缩已开启，无需修复；保留当前页合并设置。", "Memory compression is enabled; no repair is needed. The current page-combining setting is retained." }
			},
			{
				"audit.mem.capacity",
				new string[2] { "安装内存 {0} GB。", "Installed memory: {0} GB." }
			},
			{
				"audit.mem.states",
				new string[2] { "内存压缩：{0}；页合并：{1}。", "Compression: {0}; page combining: {1}." }
			},
			{
				"audit.mem.enable",
				new string[2] { "开启内存压缩", "Enable compression" }
			},
			{
				"audit.mem.enabled",
				new string[2] { "内存压缩已开启并通过回读校验，页合并设置未改动。", "Memory compression is enabled and verified. Page combining was left unchanged." }
			},
			{
				"audit.mem.failed",
				new string[2] { "处理未完成，请查看日志；已有还原记录会保留。", "The operation did not complete. Check the log; existing restore records are retained." }
			},
			{
				"audit.irq.overlap",
				new string[2] { "与游戏核心重叠", "Overlaps game cores" }
			},
			{
				"audit.irq.proposal",
				new string[2] { "可调整到 CPU {0}；处理前重新采样确认，写入后需重启并验收。", "Can move to CPU {0}; resampled before applying, then requires a restart and verification." }
			},
			{
				"audit.irq.skipped",
				new string[2] { "本次未调整", "Not adjusted" }
			},
			{
				"audit.irq.insufficient",
				new string[2] { "样本不足", "Insufficient samples" }
			},
			{
				"audit.irq.check.hint",
				new string[2] { "本轮已持续采样 {0} 秒，显卡中断样本仍不足，暂不作判断。请保持屏幕点亮、不要锁屏，稍后重新体检。", "This {0}-second capture still collected too few GPU interrupt samples to judge. Keep the screen on and unlocked, then re-run the checkup." }
			},
			{
				"audit.irq.unavailable",
				new string[2] { "未完成检测", "Check unavailable" }
			},
			{
				"audit.irq.sample",
				new string[2] { "{0} · CPU {1} · {2} 次 DPC · P99 {3} μs", "{0} · CPU {1} · {2} DPCs · P99 {3} μs" }
			},
			{
				"audit.irq.apply",
				new string[2] { "调整中断", "Adjust routing" }
			},
			{
				"audit.irq.saved",
				new string[2] { "显卡中断配置", "GPU interrupt configuration" }
			},
			{
				"audit.irq.owned",
				new string[2] { "已保留还原记录", "Restore record available" }
			},
			{
				"audit.start",
				new string[2] { "开始体检", "Start checkup" }
			},
			{
				"audit.idle.title",
				new string[2] { "还没有体检结果", "No checkup results yet" }
			},
			{
				"audit.idle.hint",
				new string[2] { "持续采样 {0} 秒，随后汇总结论；仅读取，不修改设置，可随时取消", "Samples for {0} seconds, then summarizes the results; read-only and cancellable" }
			},
			{
				"audit.phase.sampling",
				new string[2] { "正在测量显卡中断和整机负载 · 剩余 {0} 秒", "Measuring GPU interrupts and system load · {0} s remaining" }
			},
			{
				"audit.cancelled",
				new string[2] { "体检已取消，未修改任何设置", "Checkup cancelled; no settings changed" }
			},
			{
				"audit.phase.capability",
				new string[2] { "正在读取本机写入能力", "Reading this machine's write capabilities" }
			},
			{
				"audit.phase.persistent",
				new string[2] { "正在读取持久系统设置", "Reading persistent system settings" }
			},
			{
				"audit.phase.verdict",
				new string[2] { "正在汇总结论", "Summarizing conclusions" }
			},
			{
				"audit.rerun",
				new string[2] { "重新体检 {0} 秒", "Re-run, {0} s" }
			},
			{
				"audit.failed",
				new string[2] { "体检失败，接口不可用", "Checkup failed, interface unavailable" }
			},
			{
				"audit.sec.capability",
				new string[2] { "写入能力，本机可用的功能范围", "Write capability — the feature range available on this machine" }
			},
			{
				"audit.sec.machine",
				new string[2] { "本机实测，这台机器的实际情况", "Measured on this machine — this machine's actual state" }
			},
			{
				"audit.sec.persistent",
				new string[2] { "持久系统设置，重启生效，卸载后保留", "Persistent system settings — take effect after reboot, survive uninstall" }
			},
			{
				"audit.sec.verdicts",
				new string[2] { "结论清单，每条均标注依据", "Verdict list — every entry cites its evidence" }
			},
			{
				"audit.evidence.format",
				new string[2] { "依据：{0}", "Evidence: {0}" }
			},
			{
				"audit.evidence.local",
				new string[2] { "本机实测", "Measured locally" }
			},
			{
				"audit.evidence.bench",
				new string[2] { "台架实测", "Bench-tested" }
			},
			{
				"audit.evidence.mechanism",
				new string[2] { "机制明确", "Mechanism-based" }
			},
			{
				"audit.evidence.unverified",
				new string[2] { "未验证", "Unverified" }
			},
			{
				"audit.footer",
				new string[2] { "依据：本机实测、台架实测、机制明确、未验证。", "Evidence: measured here, bench-tested, mechanism clear, or unverified." }
			},
			{
				"v16.graphics.sub",
				new string[2] { "通用应用偏好与 NVIDIA / AMD / Intel 驱动选项；各项标注临时或持续生效范围。", "Windows app preferences and NVIDIA / AMD / Intel driver options. Each item states whether it is temporary or persistent." }
			},
			{
				"btn.clean",
				new string[2] { "清理", "Clean" }
			},
			{
				"btn.wipe",
				new string[2] { "重置软件", "Reset app" }
			},
			{
				"btn.uninstall",
				new string[2] { "卸载 Pavise", "Uninstall Pavise" }
			},
			{
				"set.about",
				new string[2] { "Pavise {0} {1} 本地配置与用户白名单", "Pavise {0} {1} local settings and user whitelist" }
			},
			{
				"set.hags",
				new string[2] { "GPU 硬件加速调度 HAGS", "GPU hardware-accelerated scheduling HAGS" }
			},
			{
				"env.windowed",
				new string[2] { "窗口化游戏优化", "Optimizations for windowed games" }
			},
			{
				"env.vrr",
				new string[2] { "Windows 可变刷新率优化", "Windows variable refresh rate optimization" }
			},
			{
				"env.graphics.failed",
				new string[2] { "未能完成更改或还原。恢复记录会保留，请查看日志后重试。", "Could not complete the change or restoration. Recovery records are kept; check the logs and retry." }
			},
			{
				"env.graphics.unknown",
				new string[2] { "暂时无法读取系统状态", "System state unavailable" }
			},
			{
				"env.graphics.owned",
				new string[2] { "Pavise 已接管 · 关闭可还原", "Managed by Pavise · turn off to restore" }
			},
			{
				"env.graphics.off",
				new string[2] { "当前未开启", "Currently off" }
			},
			{
				"hags.reboot",
				new string[2] { "已写入 HAGS 设置，重启电脑后生效。", "HAGS setting written; takes effect after restarting the PC." }
			},
			{
				"vbs.blocked.policy",
				new string[2] { "VBS 由组策略或 MDM 强制，策略刷新会覆盖本机改动；请先解除策略。", "VBS is enforced by Group Policy or MDM, which will overwrite local changes. Remove that policy first." }
			},
			{
				"vbs.blocked.lock",
				new string[2] { "UEFI 已锁定 VBS/内存完整性，Pavise 不执行修改。", "UEFI locks VBS/Memory Integrity, so Pavise will not change it." }
			},
			{
				"vbs.restart.title",
				new string[3] { "必须重启电脑", "Restart required", "再起動が必要です" }
			},
			{
				"vbs.restart.body",
				new string[3] { "关闭 VBS 的设置已写入，重启后才会生效。\r\n\r\n请先保存其他应用中的工作，再点击「立即重启」。重启会强制关闭应用，未保存的内容将丢失。", "The VBS disable settings have been saved. A restart is required to apply them.\r\n\r\nSave your work in other apps, then click Restart now. Apps will be forcibly closed and unsaved work will be lost.", "VBS無効化の設定を保存しました。適用には再起動が必要です。\r\n\r\n他のアプリの作業を保存してから「今すぐ再起動」を押してください。アプリは強制終了され、未保存の内容は失われます。" }
			},
			{
				"vbs.restart.now",
				new string[3] { "立即重启", "Restart now", "今すぐ再起動" }
			},
			{
				"vbs.restart.failed",
				new string[3] { "未能确认重启请求成功，请重试，或通过 Windows 开始菜单重启电脑。", "The restart request could not be confirmed. Retry, or restart through the Windows Start menu.", "再起動要求を確認できませんでした。再試行するか、Windowsのスタートメニューから再起動してください。" }
			},
			{
				"env.performance.needadmin",
				new string[2] { "修改系统设置需要管理员权限。", "Administrator rights are required to change system settings." }
			},
			{
				"env.timertick.manual",
				new string[2] { "手动配置时钟与节拍启动参数，开关后需重启电脑。切换模式不改动此设置；关闭时恢复原配置。", "Manually configures clock and tick boot parameters; restart Windows after changing it. Mode changes leave this setting alone; switching off restores the original configuration." }
			},
			{
				"env.gtimer.manual",
				new string[2] { "开启或还原配置后需重启电脑。仅在对局中请求 0.5 ms，退局释放；实际精度由系统决定，可能增加功耗。", "Restart Windows after enabling or restoring this configuration. Requests 0.5 ms during matches and releases it afterwards; actual resolution depends on Windows. May increase power use." }
			},
			{
				"env.cachewarm.manual",
				new string[2] { "手动开启后，仅在游戏待命且桌面空闲时低优先级预读；进入对局或关闭开关立即取消。仅交流供电、内存充足且游戏位于固态盘时运行，不保证提升帧率。", "When enabled manually, pre-reads at low priority only while a game is armed and the desktop is idle. Cancels on match start or when switched off. Requires AC power, ample memory and an SSD; no FPS gain is guaranteed." }
			},
			{
				"env.performance.savefail",
				new string[2] { "设置未能保存，已保留原来的选择。", "Could not save the setting; your previous choice was preserved." }
			},
			{
				"env.performance.off",
				new string[2] { "已关闭", "Off" }
			},
			{
				"env.performance.pending",
				new string[2] { "已开启 · 等待应用", "On; awaiting application" }
			},
			{
				"env.performance.reboot",
				new string[2] { "已配置 · 待重启", "Restart required" }
			},
			{
				"env.performance.restore.reboot",
				new string[2] { "已恢复 · 待重启", "Restored; restart required" }
			},
			{
				"env.performance.failed",
				new string[2] { "执行受限 · 查看日志", "Could not apply or restore; see logs" }
			},
			{
				"env.performance.requested",
				new string[2] { "对局计时请求已执行", "Match timer request active" }
			},
			{
				"env.performance.ready",
				new string[2] { "配置已就绪", "Configuration ready" }
			},
			{
				"set.timertick",
				new string[2] { "计时器恒定节拍", "Constant timer tick" }
			},
			{
				"set.gtimer",
				new string[2] { "全局计时器分辨率", "Global timer resolution" }
			},
			{
				"btn.shader",
				new string[2] { "清理着色器缓存", "Clear shader caches" }
			},
			{
				"set.shader.n",
				new string[2] { "驱动更新后花屏或卡顿时清理。之后各游戏首次启动会重新编译并更卡。", "Clear after driver-update corruption or stutter. Each game's next launch recompiles and may stutter more." }
			},
			{
				"shader.busy",
				new string[2] { "正在清理", "Cleaning" }
			},
			{
				"shader.confirm",
				new string[2] { "清空 NVIDIA、AMD、Intel、DirectX 着色器缓存？\r\n\r\n先退出游戏；首次启动会重新编译并更卡。", "Clear NVIDIA, AMD, Intel, and DirectX shader caches?\r\n\r\nExit games first; first launches will recompile and stutter more." }
			},
			{
				"shader.freed",
				new string[2] { "已清理着色器缓存，释放 {0}", "Shader caches cleared, freed {0}" }
			},
			{
				"shader.ingame",
				new string[2] { "游戏正在运行，清理会立即造成卡顿；请退出游戏后重试。", "A game is running; cleaning would cause immediate stutter. Exit the game first." }
			},
			{
				"shader.skip",
				new string[2] { "{0} 个项目未能清理，已跳过（可能被占用、权限不足或包含目录链接）。", "{0} items could not be cleaned and were skipped (in use, access denied or linked paths)." }
			},
			{
				"shader.note",
				new string[2] { "每个游戏下次启动都可能重新编译着色器，首次运行可能额外卡顿。", "Each game may recompile shaders on its next launch; the first run may stutter extra." }
			},
			{
				"tm.autostart",
				new string[2] { "开机自启", "Launch at startup" }
			},
			{
				"about.desc",
				new string[2] { "面向 Windows 的游戏资源调度工具", "A Windows resource scheduling tool for games" }
			},
			{
				"about.author",
				new string[2] { "作者", "Author" }
			},
			{
				"about.douyin",
				new string[2] { "抖音", "Douyin" }
			},
			{
				"about.repo",
				new string[3] { "开源仓库地址", "Source repository", "ソースリポジトリ" }
			},
			{
				"about.lic",
				new string[2] { "许可协议", "License" }
			},
			{
				"about.lic.value",
				new string[3] { "GPL-3.0-only ↗", "GPL-3.0-only ↗", "GPL-3.0-only ↗" }
			},
			{
				"about.free",
				new string[3] { "永久免费", "Free forever", "永久無料" }
			},
			{
				"about.refund.hint",
				new string[3] { "Pavise 永久免费。如您通过任何渠道付费购买本软件，请向卖家或交易平台申请退款。", "Pavise is free forever. If you paid for it through any channel, request a refund from the seller or purchase platform.", "Pavise は永久無料です。有料で購入された場合は、販売者または購入先のプラットフォームに返金を申請してください。" }
			},
			{
				"about.contact.hint",
				new string[2] { "Bug、建议和使用问题可通过抖音私信反馈。", "Report bugs, suggestions, or usage issues via Douyin direct message." }
			},
			{
				"bal.update",
				new string[2] { "发现新版本 {0}，请打开面板的关于页查看", "New version {0} available; see the About page in the panel" }
			},
			{
				"btn.add",
				new string[2] { "添加", "Add" }
			},
			{
				"btn.cancel",
				new string[2] { "取消", "Cancel" }
			},
			{
				"ofd.game",
				new string[2] { "选择游戏 EXE 或 Windows 快捷方式", "Select a game EXE or Windows shortcut" }
			},
			{
				"ofd.filter",
				new string[2] { "游戏程序或快捷方式 (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|程序 (*.exe)|*.exe|快捷方式 (*.lnk;*.url)|*.lnk;*.url", "Game program or shortcut (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|Programs (*.exe)|*.exe|Shortcuts (*.lnk;*.url)|*.lnk;*.url" }
			},
			{
				"msg.taskfail",
				new string[2] { "计划任务操作失败。若 Pavise 未以管理员身份运行，请提权后重试。", "Scheduled task operation failed. If Pavise is not running as administrator, elevate and try again." }
			},
			{
				"tray.exit",
				new string[2] { "退出", "Exit" }
			},
			{
				"tray.active",
				new string[2] { "Pavise 守护中 {0}", "Pavise guarding {0}" }
			},
			{
				"tray.idle",
				new string[2] { "Pavise 待命，未检测到游戏", "Pavise standing by, no game detected" }
			},
			{
				"tray.armed",
				new string[2] { "Pavise 已就位 {0}，进入对局自动接管", "Pavise ready {0}, will take over when a match starts" }
			},
			{
				"tray.noelev",
				new string[2] { "Pavise 未提权，压制类功能无效", "Pavise not elevated, suppression features inactive" }
			},
			{
				"bal.noelev",
				new string[2] { "当前非管理员运行，压制类功能不会生效", "Not running as administrator; suppression features will not take effect" }
			},
			{
				"rep.done",
				new string[2] { "{0} 本局 {1}，压制 {2} 个后台进程，合计占用 {3} CPU", "{0} this match {1}, suppressed {2} background processes, {3} CPU combined" }
			},
			{
				"rep.top",
				new string[2] { " · 最高 {0} {1}", " · top {0} {1}" }
			},
			{
				"rep.brief",
				new string[2] { "{0} · {1} · 压制 {2} 个后台", "{0} · {1} · {2} background processes" }
			},
			{
				"rep.pavise.cpu",
				new string[2] { " · Pavise 自身平均 CPU {0}%", " · Pavise itself averaged {0}% CPU" }
			},
			{
				"ac.mode",
				new string[2] { "压制强度", "Suppression strength" }
			},
			{
				"ac.mode.gentle",
				new string[2] { "温和", "Gentle" }
			},
			{
				"ac.mode.balanced",
				new string[2] { "均衡", "Balanced" }
			},
			{
				"ac.mode.isolated",
				new string[2] { "强力", "Strong" }
			},
			{
				"ac.mode.disclaimer",
				new string[2] { "任何档位都不保证不被封号：压制反作弊属于对其进程的干预，厂商如何判定不由本程序决定。", "No tier guarantees you will not be banned: suppressing an anti-cheat is interference with its process, and how a vendor judges that is not up to this program." }
			},
			{
				"ac.mode.gentle.d",
				new string[2] { "CPU 低于正常优先级、磁盘 IO 低、内存页优先级 3，启用 EcoQoS；保留动态提优与计时器请求。对局中可核验的独立服务共享最多 4 个完整能效核或其他缓存域的核；同构辅助核为 2 个。CPU Sets 保持原值，有限纠偏，退局恢复绑核，关闭分组恢复资源设置。", "BelowNormal CPU, low disk IO, memory-page priority 3 and EcoQoS; preserves dynamic boosting and timer requests. During games, verified dedicated services share up to four complete efficiency or alternate-cache cores, or two homogeneous auxiliary cores. CPU Sets stay unchanged. Corrections are bounded; game exit restores affinity and disabling a group restores resource settings." }
			},
			{
				"ac.mode.balanced.d",
				new string[2] { "CPU 低于正常优先级、磁盘 IO 极低、内存页优先级 2，启用 EcoQoS 并禁用 Windows 动态优先级提升；保留计时器请求。对局中可核验的独立服务共享 2 个完整能效核或辅助物理核。CPU Sets 保持原值，有限纠偏，退局恢复绑核，关闭或降档按原记录恢复。", "BelowNormal CPU, very-low disk IO, memory-page priority 2 and EcoQoS; disables Windows dynamic priority boosting while preserving timer requests. During games, verified dedicated services share two complete efficiency or auxiliary physical cores. CPU Sets stay unchanged. Corrections are bounded; game exit restores affinity and disabling or lowering the tier restores owned settings as appropriate." }
			},
			{
				"ac.mode.isolated.d",
				new string[2] { "CPU Idle 优先级、磁盘 IO 极低、内存页优先级 1，启用 EcoQoS、禁用 Windows 动态优先级提升，系统支持时忽略该进程的高精度计时器请求。对局中可核验的独立服务共享 1 个完整能效核或辅助物理核，包含其 SMT 线程。每秒核验、有限纠偏，保留 CPU Sets；退局恢复绑核，关闭或降档按原记录恢复。过强压制可能增加游戏等待。", "Idle CPU, very-low disk IO, memory-page priority 1 and EcoQoS. Disables Windows dynamic priority boosting and ignores the process's high-resolution timer requests where supported. During games, verified dedicated services share one complete efficiency or auxiliary physical core, including SMT siblings. Checks once per second with bounded corrections and unchanged CPU Sets. Game exit restores affinity; disabling or lowering the tier restores owned settings as appropriate. Heavy suppression can increase game waits." }
			},
			{
				"ac.protectonly",
				new string[2] { "仅保护 · 会反制第三方工具 不压制", "Protection only · fights back against third-party tools, never suppressed" }
			},
			{
				"ac.recordonly",
				new string[2] { "只记录 · 不压制、不绑核", "Record only · no suppression or core pinning" }
			},
			{
				"log.tamer.recordonly",
				new string[2] { "反作弊只记录 {0} · {1} PID {2} · 未执行压制", "Anti-cheat record only: {0} · {1} PID {2} · no suppression applied" }
			},
			{
				"log.tamer.recordonly.retired",
				new string[2] { "反作弊分组 {0} 已改为只记录，原有压制设置已停用", "Anti-cheat group {0} is now record only; previous suppression settings disabled" }
			},
			{
				"log.tamer.unpin",
				new string[2] { "反作弊压制 降档后已解除 {0} 个进程的绑核", "Anti-cheat suppression: core pinning released on {0} processes after lowering the tier" }
			},
			{
				"log.tamer.mode",
				new string[2] { "反作弊压制强度 已切到 {0}", "Anti-cheat suppression strength switched to {0}" }
			},
			{
				"log.tamer.retired",
				new string[2] { "反作弊分组 {0} 已改为仅保护 原有的压制开关已关闭", "Anti-cheat group {0} is now protection only; its suppression switch has been turned off" }
			},
			{
				"ac.ace.n",
				new string[2] { "ACE 反作弊专家 腾讯", "ACE Anti-Cheat Expert, Tencent" }
			},
			{
				"ac.ace.d",
				new string[2] { "英雄联盟国服、无畏契约国服、三角洲行动、CF 等", "League of Legends CN, VALORANT CN, Delta Force, CF, etc." }
			},
			{
				"ac.tp.n",
				new string[2] { "TenProtect TP 腾讯", "TenProtect TP, Tencent" }
			},
			{
				"ac.tp.d",
				new string[2] { "ACE 前身，穿越火线等老游戏仍在使用", "Predecessor of ACE, still used by older games like CrossFire" }
			},
			{
				"ac.vanguard.n",
				new string[2] { "Vanguard Riot", "Vanguard Riot" }
			},
			{
				"ac.vanguard.d",
				new string[2] { "无畏契约、英雄联盟，vgk 为内核驱动，无法压制", "VALORANT and League of Legends; vgk is a kernel driver and cannot be suppressed" }
			},
			{
				"ac.eac.n",
				new string[2] { "EasyAntiCheat Epic", "EasyAntiCheat Epic" }
			},
			{
				"ac.eac.d",
				new string[2] { "Apex、堡垒之夜、永劫无间、幻兽帕鲁等", "Apex, Fortnite, Naraka: Bladepoint, Palworld, etc." }
			},
			{
				"ac.battleye.n",
				new string[2] { "BattlEye", "BattlEye" }
			},
			{
				"ac.battleye.d",
				new string[2] { "PUBG、彩虹六号、DayZ、逃离塔科夫，BEDaisy 为驱动", "PUBG, Rainbow Six, DayZ, Escape from Tarkov; BEDaisy is the driver" }
			},
			{
				"ac.eaac.n",
				new string[2] { "EA Javelin EA 反作弊", "EA Javelin EA anti-cheat" }
			},
			{
				"ac.eaac.d",
				new string[2] { "战地 2042、战地 6、FC 等 EA 游戏，另有内核驱动", "Battlefield 2042, Battlefield 6, FC and other EA games; also has a kernel driver" }
			},
			{
				"ac.gameguard.n",
				new string[2] { "nProtect GameGuard", "nProtect GameGuard" }
			},
			{
				"ac.gameguard.d",
				new string[2] { "DNF 等部分韩系网游", "DNF and some other Korean online games" }
			},
			{
				"ac.faceit.n",
				new string[2] { "FACEIT 反作弊", "FACEIT anti-cheat" }
			},
			{
				"ac.faceit.d",
				new string[2] { "CS2 第三方竞技平台", "CS2 third-party competitive platform" }
			},
			{
				"ac.neac.n",
				new string[2] { "NEAC 反作弊 网易", "NEAC anti-cheat, NetEase" }
			},
			{
				"ac.neac.d",
				new string[2] { "部分网易游戏，另有内核组件", "Some NetEase games; also has kernel components" }
			},
			{
				"ac.protectiononly",
				new string[2] { "仅保护 · 不压制、不绑核", "Protection only · no suppression or core pinning" }
			},
			{
				"ac.punkbuster.n",
				new string[2] { "PunkBuster Even Balance", "PunkBuster Even Balance" }
			},
			{
				"ac.punkbuster.d",
				new string[2] { "战地 4 等游戏的 PnkBstr 服务；仅保护，不提供压制开关", "PnkBstr services used by Battlefield 4 and other games; protection only, no suppression switch" }
			},
			{
				"ac.ngs.n",
				new string[2] { "Nexon Game Security", "Nexon Game Security" }
			},
			{
				"ac.ngs.d",
				new string[2] { "BlackCipher / BlackXchg 与同目录辅助组件，含 .aes 进程；仅保护", "BlackCipher / BlackXchg and helpers in their directories, including .aes processes; protection only" }
			},
			{
				"ac.wellbia.n",
				new string[2] { "Wellbia XIGNCODE3 / UNCHEATER", "Wellbia XIGNCODE3 / UNCHEATER" }
			},
			{
				"ac.pwarena.n",
				new string[2] { "完美世界竞技平台 PAC", "Perfect World Arena PAC" }
			},
			{
				"ac.pwarena.d",
				new string[2] { "PAC 反作弊内嵌在平台客户端里 CS2 对局中平台进程不压制 仅保护", "PAC lives inside the platform client; the platform process is left alone during CS2 matches, protection only" }
			},
			{
				"ac.5e.n",
				new string[2] { "5E 对战平台", "5E Arena" }
			},
			{
				"ac.5e.d",
				new string[2] { "5E 反作弊内嵌在 5EClient 里 对局中不压制 仅保护", "The 5E anti-cheat lives inside 5EClient; not suppressed during matches, protection only" }
			},
			{
				"ac.b5.n",
				new string[2] { "B5 对战平台 BBI", "B5 Arena BBI" }
			},
			{
				"ac.b5.d",
				new string[2] { "B5AntiCheat 与 B5GameService 是用户态进程 另有内核驱动 仅保护", "B5AntiCheat and B5GameService are user-mode processes with a kernel driver alongside; protection only" }
			},
			{
				"ac.wellbia.d",
				new string[2] { "按专用目录与名称前缀识别，覆盖 ucldr_ 变体；仅保护", "Recognized by dedicated directories and name prefixes, including ucldr_ variants; protection only" }
			},
			{
				"log.program.1",
				new string[2] { "开机自启任务指向其他程序副本，不经其重新启动，以当前程序继续运行", "The autostart task points to a different copy of the program; not relaunching through it and continuing with the current program" }
			},
			{
				"log.program.2",
				new string[2] { "检测到上次未还原的分级后台控制 已恢复 ", "Detected tiered background control left unrestored from last run; restored " }
			},
			{
				"log.program.3",
				new string[2] { " 个进程", " processes" }
			},
			{
				"log.program.6",
				new string[2] { "启动检查更新 发现新版本 ", "Startup update check found new version " }
			},
			{
				"log.program.7",
				new string[2] { " 当前 ", " current " }
			},
			{
				"log.program.8",
				new string[2] { "启动检查更新 已是最新版本 ", "Startup update check: already the latest version " }
			},
			{
				"log.program.9",
				new string[2] { "启动检查更新失败 ", "Startup update check failed " }
			},
			{
				"log.program.10",
				new string[2] { "系统通知区域不可用 托盘图标无处显示 已改为直接打开主面板 否则本次启动将没有任何入口", "The system notification area is unavailable and the tray icon has nowhere to appear, so the main panel was opened directly; otherwise this session would have had no entry point at all" }
			},
			{
				"log.program.autostart.traywaiting",
				new string[3] { "登录自启已进入后台，通知区域暂未就绪，保持界面隐藏。", "Autostart is running in the background; the notification area is not ready, so the panel stays hidden.", "自動起動はバックグラウンドで動作中。通知領域の準備を待ち、画面は表示しません。" }
			},
			{
				"log.program.11",
				new string[2] { "系统版本低于运行门槛 已还原历史改动并退出 当前内部版本 ", "The OS version is below the required baseline; historical changes were restored and startup was aborted. Current build " }
			},
			{
				"os.old.body",
				new string[2] { "当前系统内部版本 {0}，低于运行所需的 Windows 10 2004（{1}），Pavise 无法启动。\r\n\r\n后台压制依赖的能效模式、逐进程计时器精度隔离在更早的系统上不存在，调度不会生效。推荐 Windows 11 24H2（{2}）。\r\n\r\n此前版本留下的系统改动已经还原。", "This system is build {0}, below the Windows 10 2004 ({1}) required to run, so Pavise cannot start.\r\n\r\nThe efficiency mode and per-process timer isolation that background suppression relies on do not exist on earlier builds, so scheduling would have no effect. Windows 11 24H2 ({2}) is preferred.\r\n\r\nSystem changes left by earlier versions have been restored." }
			},
			{
				"os.old.restorefail",
				new string[2] { "\r\n\r\n部分改动还原失败，详见日志。", "\r\n\r\nSome changes could not be restored; see the log." }
			},
			{
				"log.crashguard.1",
				new string[2] { "检测到上次异常退出 按 PID 创建时间 映像名恢复 ", "Detected abnormal exit last time; recovering by PID, creation time and image name: " }
			},
			{
				"log.crashguard.2",
				new string[2] { " 个进程的已记录状态", " processes' recorded state restored" }
			},
			{
				"log.crashguard.3",
				new string[2] { "仍有 ", "Still " }
			},
			{
				"log.crashguard.4",
				new string[2] { " 个提优进程暂时无法恢复 身份快照已保留待下次重试", " boosted processes cannot be recovered yet; identity snapshots kept for the next retry" }
			},
			{
				"log.gamemodeboost.3",
				new string[2] { "游戏提优 ", "Game boost " }
			},
			{
				"log.gamemodeboost.4",
				new string[2] { " 句柄未开放 本体提优跳过 后台压制不受影响", " handle not available; boosting the game itself is skipped, background suppression unaffected" }
			},
			{
				"log.gamemodeboost.5",
				new string[2] { "游戏提优 无法读取 ", "Game boost: " }
			},
			{
				"log.gamemodeboost.6",
				new string[2] { " 的创建时间 已按安全边界跳过", " creation time unreadable, skipped as a safety boundary" }
			},
			{
				"log.gamemodeboost.7",
				new string[2] { "游戏提优 renderer 身份已变化 跳过 pid ", "Game boost: renderer identity changed, skipping pid " }
			},
			{
				"log.gamemodeboost.8",
				new string[2] { " 的全部写入", " (all writes)" }
			},
			{
				"log.gamemodeboost.9",
				new string[2] { "游戏提优 无法读取原 CPU Sets 已按安全边界跳过 ", "Game boost: cannot read original CPU Sets, skipped as a safety boundary " }
			},
			{
				"log.gamemodeboost.10",
				new string[2] { "游戏提优 崩溃恢复快照无法持久化 已取消修改 ", "Game boost: crash-recovery snapshot could not be persisted, change cancelled " }
			},
			{
				"t.gamemodeboost.16",
				new string[2] { " 个核", " cores" }
			},
			{
				"t.gamemodeboost.17",
				new string[2] { " 不限核", " no core limit" }
			},
			{
				"t.gamemodeboost.18",
				new string[2] { " 硬绑 ", " hard-pinned " }
			},
			{
				"log.gamemodeboost.19",
				new string[2] { "游戏核心策略 ", "Game core policy " }
			},
			{
				"log.gamemodeboost.23",
				new string[2] { "游戏核心策略未完整生效 ", "Game core policy not fully in effect " }
			},
			{
				"log.gamemodeboost.24",
				new string[3] { " 下一轮重试", " will retry next round", " 次回に再試行します" }
			},
			{
				"log.gamemodeboost.27",
				new string[3] { "游戏优先级已生效 ", "Game priorities in effect ", "ゲーム優先度適用済み " }
			},
			{
				"log.gamemodeboost.28",
				new string[2] { "高优先级", "high priority" }
			},
			{
				"log.gamemodeboost.29",
				new string[2] { "普通优先级 智能保帧降档", "normal priority, smart frame guard downshift" }
			},
			{
				"log.gamemodeboost.30",
				new string[2] { " 高读写优先级", " high I/O priority" }
			},
			{
				"log.gamemodeboost.31",
				new string[2] { " 显卡高优先级", " high GPU priority" }
			},
			{
				"t.gamemodeboost.32",
				new string[2] { " 已退出省电模式", " power-saving mode exited" }
			},
			{
				"t.gamemodeboost.38",
				new string[2] { " EcoQoS=cleared timerExemption=unconfirmed", " EcoQoS=cleared timerExemption=unconfirmed" }
			},
			{
				"t.gamemodeboost.33",
				new string[2] { " 省电模式未清除", " power-saving mode not cleared" }
			},
			{
				"log.gamemodeboost.34",
				new string[2] { " 句柄写入权限被", " handle write access stripped by " }
			},
			{
				"log.gamemodeboost.35",
				new string[2] { "剥离 授予 0x", ", granted 0x" }
			},
			{
				"log.gamemodeboost.36",
				new string[2] { " 本体提优已停止 后台压制不受影响", " boosting the game itself stopped, background suppression unaffected" }
			},
			{
				"t.gamemodeboost.37",
				new string[2] { " 读取失败", " read failed" }
			},
			{
				"log.gamemodeboost.38",
				new string[2] { "提优还原 ", "Boost restore " }
			},
			{
				"log.gamemodeboost.39",
				new string[2] { " restore=pending snapshot=retained retry=scheduled", " restore=pending snapshot=retained retry=scheduled" }
			},
			{
				"log.gamemodeboost.46",
				new string[2] { "免压制名单已清空 ", "Suppression-exempt list cleared: " }
			},
			{
				"log.gamemodeboost.47",
				new string[2] { " 项 下次对局重新探测这些进程", " entries; these processes will be re-probed next match" }
			},
			{
				"log.gamemodeboost.52",
				new string[2] { "SESSION event=end reason=", "SESSION event=end reason=" }
			},
			{
				"log.gamemodeboost.53",
				new string[2] { " restoredProcessCount=", " restoredProcessCount=" }
			},
			{
				"log.gamemodeboost.54",
				new string[2] { " countScope=session includes=mid-session,grace-period", " countScope=session includes=mid-session,grace-period" }
			},
			{
				"t.gamemodeenv.1",
				new string[2] { "后台下载暂停", "Background download pause" }
			},
			{
				"t.gamemodeenv.3",
				new string[2] { "Windows 更新暂停", "Windows Update pause" }
			},
			{
				"t.gamemodeenv.4",
				new string[2] { "无输入降级关闭", "No-input throttling off" }
			},
			{
				"t.gamemodeenv.5",
				new string[2] { "息屏防护", "Keep-display-awake" }
			},
			{
				"t.gamemodeenv.6",
				new string[2] { "显卡功耗墙", "GPU power limit" }
			},
			{
				"t.gamemodeenv.8",
				new string[2] { "AMD 流体运动帧", "AMD Fluid Motion Frames" }
			},
			{
				"log.gamemodeenv.9",
				new string[2] { "环境项 ", "Environment item " }
			},
			{
				"log.gamemodeenv.10",
				new string[2] { " 连续 ", " failed " }
			},
			{
				"log.gamemodeenv.11",
				new string[2] { " 次写入失败 已自动关闭对应开关并停用 重新打开该开关即恢复尝试", " consecutive writes; the corresponding switch was automatically turned off and disabled, turn it back on to resume attempts" }
			},
			{
				"t.gamemodeenv.29",
				new string[2] { "会话未关", "Session not closed" }
			},
			{
				"t.gamemodeenv.30",
				new string[2] { " 项", " items" }
			},
			{
				"t.gamemodeenv.31",
				new string[2] { "下载暂停", "Download pause" }
			},
			{
				"t.gamemodeenv.32",
				new string[2] { "更新暂停", "Update pause" }
			},
			{
				"t.gamemodeenv.33",
				new string[2] { "降级豁免", "Throttling exemption" }
			},
			{
				"t.gamemodeenv.34",
				new string[2] { "防熄屏", "Keep display awake" }
			},
			{
				"t.gamemodeenv.35",
				new string[2] { "电源计划", "Power plan" }
			},
			{
				"t.gamemodeenv.36",
				new string[2] { "计时器精度", "Timer resolution" }
			},
			{
				"t.gamemodeenv.37",
				new string[2] { "状态位残留", "State flag residue" }
			},
			{
				"log.gamemodeenv.38",
				new string[2] { "游戏模式残留待恢复 ", "Game mode leftovers pending restore " }
			},
			{
				"log.gamemodeenv.39",
				new string[2] { " retry=scheduled", " retry=scheduled" }
			},
			{
				"log.gamemodeenv.40",
				new string[2] { "游戏模式残留已全部恢复", "All game mode leftovers restored" }
			},
			{
				"log.gamemodeenv.41",
				new string[2] { " 累计重试 ", " total retries " }
			},
			{
				"t.gamemodeenv.41",
				new string[2] { "后台压制已关闭", "Background suppression turned off" }
			},
			{
				"log.gamemodeenv.42",
				new string[2] { " releasedProcessCount=", " releasedProcessCount=" }
			},
			{
				"log.gamemodeenv.43",
				new string[2] { " scope=background", " scope=background" }
			},
			{
				"t.gamemodelibrary.1",
				new string[2] { "该游戏已在列表中", "This game is already in the list" }
			},
			{
				"log.gamemodelibrary.2",
				new string[2] { "已确认 ", "Confirmed " }
			},
			{
				"log.gamemodelibrary.3",
				new string[2] { " 的实际渲染进程是 ", " renders through " }
			},
			{
				"log.gamemodelibrary.4",
				new string[2] { " 档案已更新 之后可直接按它识别 ", " — profile updated, future detection can match it directly " }
			},
			{
				"log.gamemodelibrary.5",
				new string[2] { "已开启强制接管 ", "Force takeover enabled " }
			},
			{
				"log.gamemodelibrary.6",
				new string[2] { "已关闭强制接管 ", "Force takeover disabled " }
			},
			{
				"log.gamemodelibrary.7",
				new string[2] { "，此后该程序的进程一经启动即进入对局，不再等待前台窗口与渲染证据", " — its process now enters a match as soon as it starts, without waiting for foreground window and rendering evidence" }
			},
			{
				"log.gamemodelibrary.8",
				new string[2] { " 恢复成按前台窗口和渲染证据自动判定", " — back to automatic detection by foreground window and rendering evidence" }
			},
			{
				"log.gamemodelibrary.9",
				new string[2] { "独立配置 已清除 ", "Per-game profile cleared for " }
			},
			{
				"log.gamemodelibrary.10",
				new string[2] { " 的全部 ", " — all " }
			},
			{
				"log.gamemodelibrary.11",
				new string[2] { " 项覆盖 回到跟随全局", " overrides removed, back to following global settings" }
			},
			{
				"log.gamemodelibrary.14",
				new string[2] { "游戏库 已把 ", "Game library renamed " }
			},
			{
				"log.gamemodelibrary.15",
				new string[2] { " 重命名为 ", " to " }
			},
			{
				"log.gamemodelibrary.16",
				new string[2] { " 只改显示名 识别不受影响", " (display name only; detection unaffected)" }
			},
			{
				"log.gamemodelibrary.17",
				new string[2] { "白名单新增 ", "Whitelist added " }
			},
			{
				"log.gamemodelibrary.18",
				new string[2] { " 当前匹配 ", " — currently matches " }
			},
			{
				"log.gamemodelibrary.19",
				new string[2] { " 个 立即恢复 ", " processes; immediately restored " }
			},
			{
				"log.gamemodelibrary.20",
				new string[2] { " 个后台压制", " suppressed background processes" }
			},
			{
				"log.gamemodelibrary.21",
				new string[2] { "白名单已恢复为预设 ", "Whitelist restored to preset " }
			},
			{
				"log.gamemodelibrary.22",
				new string[2] { " 项 当前匹配 ", " entries, currently matching " }
			},
			{
				"t.gamemodelibrary.23",
				new string[2] { "# Pavise 后台策略豁免规则 旧版一行一个进程名的文件仍可直接读取", "# Pavise background policy exemption rules. Legacy one-process-name-per-line files can still be read directly" }
			},
			{
				"t.gamemodelibrary.24",
				new string[3] { "# V4 N=进程名锚点 P/F=EXE 路径锚点；非公共宿主保护已验证子孙", "# V4: N=name anchor, P/F=EXE path anchor; non-shared hosts protect verified descendants", " # V4: N=名前起点、P/F=EXEパス起点。共有ホスト以外は確認済み子孫を保護" }
			},
			{
				"t.gamemodelibrary.25",
				new string[2] { "# Windows 核心另有安全边界 这里也保留必要项并允许用户追加明确例外", "# Windows core components have their own safety boundary; essential entries are kept here too, and users may append explicit exceptions" }
			},
			{
				"log.gamemodelibrary.26",
				new string[2] { "保存游戏模式白名单失败", "Failed to save game mode whitelist" }
			},
			{
				"log.gamemodesession.1",
				new string[2] { "本局结束 ", "Match ended " }
			},
			{
				"log.gamemodesettings.7",
				new string[2] { " backgroundCores=", " backgroundCores=" }
			},
			{
				"log.gamemodesweep.1",
				new string[2] { "后台压制 ", "Background suppression " }
			},
			{
				"t.gamemodesweep.2",
				new string[2] { " 强力压制", " aggressive suppression" }
			},
			{
				"t.gamemodesweep.3",
				new string[2] { " 省电压制", " power-saving suppression" }
			},
			{
				"t.gamemodesweep.5",
				new string[2] { " 只放行白名单", " whitelist only" }
			},
			{
				"log.gamemodesweep.6",
				new string[2] { " backgroundGpuPolicy=demote", " backgroundGpuPolicy=demote" }
			},
			{
				"log.gamemodesweep.7",
				new string[2] { " 首轮 ", " first pass " }
			},
			{
				"log.gamemodesweep.8",
				new string[2] { " 个", " processes" }
			},
			{
				"log.gamemodesweep.9",
				new string[2] { " 个待重试", " pending retry" }
			},
			{
				"log.gamemodesweep.10",
				new string[2] { " 个受保护未压制", " protected, not suppressed" }
			},
			{
				"t.gamemodesweep.11",
				new string[2] { " 个在免压名单内", " on the suppression-exempt list" }
			},
			{
				"log.gamemodesweep.12",
				new string[2] { " 已恢复 ", " restored " }
			},
			{
				"log.gamemode.1",
				new string[2] { "处理器 混合架构 大核 ", "CPU hybrid architecture: P-cores " }
			},
			{
				"log.gamemode.2",
				new string[2] { " 小核 ", " / E-cores " }
			},
			{
				"log.gamemode.3",
				new string[2] { " 选只用大核时给 ", " — P-core-only mode gives " }
			},
			{
				"log.gamemode.4",
				new string[2] { "处理器 带 3D 缓存 默认全核 选单 CCD 时给大缓存那块 ", "CPU has 3D V-Cache; default is all cores, single-CCD mode gives the large-cache CCD " }
			},
			{
				"log.gamemode.5",
				new string[2] { "处理器 多 CCD 默认全核 选单 CCD 时给 ", "CPU has multiple CCDs; default is all cores, single-CCD mode gives " }
			},
			{
				"log.gamemode.countsplit",
				new string[3] { "CPU 拓扑 系统报出的逻辑核数 {0} 与枚举到的对不上 已收窄到 {1} 个 自动核心独占可能选错核心 请把这条日志反馈给作者", "CPU topology: the system reports {0} logical processors, which does not match what was enumerated; the usable set was narrowed to {1}. Automatic exclusive cores may pick the wrong cores. Please report this log line.", "CPU トポロジー: システムが報告する論理プロセッサ数 {0} が列挙結果と一致しません。使用可能な範囲を {1} に絞り込みました。自動コア専有が誤ったコアを選ぶ可能性があります。このログ行を作者へご報告ください。" }
			},
			{
				"log.gamemode.sourcesplit",
				new string[3] { "CPU 拓扑 两套枚举报出的物理核数不一致 {0} 掩码取自前者 独占核计划取自后者 自动核心独占可能选错核心 请把这条日志反馈给作者", "CPU topology: the two enumerations report different physical core counts ({0}). Masks come from the first and the exclusive-core plan from the second, so automatic exclusive cores may pick the wrong cores. Please report this log line.", "CPU トポロジー: 2 つの列挙が報告する物理コア数が一致しません（{0}）。マスクは前者、専有コアの計画は後者に由来するため、自動コア専有が誤ったコアを選ぶ可能性があります。このログ行を作者へご報告ください。" }
			},
			{
				"log.gamemode.6",
				new string[2] { "处理器 单一架构 游戏不限核", "CPU uniform architecture, no core restriction for the game" }
			},
			{
				"log.gamemode.8",
				new string[2] { "处理器 分区与大小核判定矛盾 已弃用该分区", "CPU partition conflicts with the P/E-core detection; partition discarded" }
			},
			{
				"log.gamemode.9",
				new string[2] { "CPU 拓扑 绑核目标未通过校验 已退回不限核", "CPU topology: core-affinity target failed validation, reverted to unrestricted cores" }
			},
			{
				"t.gamemode.10",
				new string[2] { "无法创建默认白名单", "Cannot create default whitelist" }
			},
			{
				"t.gamemode.11",
				new string[2] { "白名单版本头位置无效", "Whitelist version header in invalid position" }
			},
			{
				"t.gamemode.12",
				new string[2] { "白名单完整性尾标位置无效", "Whitelist integrity trailer in invalid position" }
			},
			{
				"t.gamemode.13",
				new string[2] { "白名单完整性尾标后仍有规则", "Rules found after whitelist integrity trailer" }
			},
			{
				"t.gamemode.14",
				new string[2] { "白名单 V2 规则格式无效", "Invalid whitelist V2 rule format" }
			},
			{
				"t.gamemode.15",
				new string[2] { "白名单缺少 V2 版本头", "Whitelist missing V2 version header" }
			},
			{
				"t.gamemode.16",
				new string[2] { "旧版白名单规则无效", "Invalid legacy whitelist rule" }
			},
			{
				"t.gamemode.17",
				new string[2] { "白名单含重复规则", "Whitelist contains duplicate rules" }
			},
			{
				"t.gamemode.18",
				new string[2] { "白名单完整性校验失败", "Whitelist integrity check failed" }
			},
			{
				"t.gamemode.19",
				new string[2] { "白名单 V3 缺少完整性尾标", "Whitelist V3 missing integrity trailer" }
			},
			{
				"t.gamemode.20",
				new string[2] { "白名单缺少完整性尾标", "Whitelist missing integrity trailer" }
			},
			{
				"t.gamemode.21",
				new string[2] { "白名单为空且没有 V2 版本头", "Whitelist is empty and has no V2 version header" }
			},
			{
				"log.gamemode.24",
				new string[2] { "白名单旧格式迁移失败 本次规则已安全载入 下次将重试", "Whitelist legacy-format migration failed; rules loaded safely this time, will retry next time" }
			},
			{
				"log.gamemode.25",
				new string[2] { "白名单加载失败 本次运行改用预置白名单 用户自定义项本次不生效 ", "Whitelist failed to load; using the preset whitelist for this run, custom user entries are not in effect this time " }
			},
			{
				"t.gamemode.26",
				new string[2] { "# Pavise 智能守护白名单 这些进程始终不进入后台控制", "# Pavise smart guardian whitelist; these processes are never placed under background control" }
			},
			{
				"t.gamemode.27",
				new string[2] { "# V3 规则支持进程名 精确路径和应用家族 并带完整性尾标防止截断", "# V3 rules support process names, exact paths and app families, with an integrity trailer to guard against truncation" }
			},
			{
				"t.gamemode.28",
				new string[2] { "# Windows 核心 其它会话和 Pavise 自身受安全保护 其余例外只来自本白名单", "# Windows core components, other sessions, and Pavise itself are safety-protected; all other exceptions come only from this whitelist" }
			},
			{
				"t.gamemode.29",
				new string[2] { "# Steam Epic EA 育碧 战网 GOG R星 Riot WeGame Xbox HoYoPlay 等", "# Steam, Epic, EA, Ubisoft, Battle.net, GOG, Rockstar, Riot, WeGame, Xbox, HoYoPlay, etc." }
			},
			{
				"t.gamemode.31",
				new string[2] { "白名单预置", "Whitelist preset" }
			},
			{
				"log.gamemode.34",
				new string[2] { "本局使用 ", "This match uses the per-game profile for " }
			},
			{
				"log.gamemode.35",
				new string[2] { " 的独立配置 覆盖 ", " — overriding " }
			},
			{
				"log.gamemode.36",
				new string[2] { " 项 对局中的修改下局生效", " items; mid-match changes take effect next match" }
			},
			{
				"t.gamemode.42",
				new string[2] { "紧急恢复", "Emergency restore" }
			},
			{
				"t.gamemode.43",
				new string[2] { "手动关闭游戏模式", "Game mode closed manually" }
			},
			{
				"log.gamemode.44",
				new string[2] { "宽限期内重新确认游戏会话；保持会话并重新应用后台策略", "Game session reconfirmed during the grace period; session retained and background policy reapplied" }
			},
			{
				"log.gamemode.45",
				new string[2] { "游戏会话激活 target=", "Game session activated target=" }
			},
			{
				"log.gamemode.46",
				new string[2] { "游戏会话检测目标更新 target=", "Game session detection target updated target=" }
			},
			{
				"log.gamemode.47",
				new string[2] { "游戏会话未确认；进入退出宽限 graceSeconds=", "Game session unconfirmed; exit grace started graceSeconds=" }
			},
			{
				"log.gamemode.48",
				new string[2] { " backgroundRestore=requested", " backgroundRestore=requested" }
			},
			{
				"t.gamemode.49",
				new string[2] { "游戏退出宽限", "Game exit grace period" }
			},
			{
				"t.gamemode.50",
				new string[2] { "游戏已退出", "Game exited" }
			},
			{
				"t.gamemode.51",
				new string[2] { "残留恢复重试", "Leftover restore retry" }
			},
			{
				"log.gamemode.52",
				new string[2] { "游戏模式异常 ", "Game mode exception " }
			},
			{
				"t.gamemode.53",
				new string[2] { "Pavise 退出", "Pavise exit" }
			},
			{
				"log.gamemode.54",
				new string[2] { "游戏识别待命 target=", "Game detection pending target=" }
			},
			{
				"log.gamemode.55",
				new string[2] { "；等待满足对局接管条件", "; waiting for match activation criteria" }
			},
			{
				"log.gamemode.56",
				new string[2] { "游戏识别待命解除 target=", "Game detection no longer pending target=" }
			},
			{
				"log.gamemode.57",
				new string[2] { "；未再检测到候选进程", "; candidate process no longer detected" }
			},
			{
				"log.gamemode.62",
				new string[2] { " 触发进程 ", "; triggered by " }
			},
			{
				"log.gamemodeboost.57",
				new string[2] { "游戏提优 目标进程已退出 记录已清理 ", "Game boost: target process has exited, records cleaned " }
			},
			{
				"log.gamemodeboost.58",
				new string[2] { "游戏提优残留 句柄始终无法打开 已达重试上限 丢弃记录 进程级状态随进程退出自然消失 ", "Game boost leftovers: the handle never opened and the retry limit was reached, so records were dropped. Per-process state disappears with the process anyway " }
			},
			{
				"log.gamemodeboost.59",
				new string[2] { "本机不接受计时器分辨率豁免位 已停止再尝试 提优其余部分照常 该位只影响窗口被遮挡时游戏能否维持高精度计时 ", "This machine does not accept the timer-resolution exemption bit, so it will no longer be attempted; the rest of the boost is unaffected. The bit only governs whether a game keeps high-resolution timing while its window is occluded " }
			},
			{
				"log.gamemodeboost.60",
				new string[2] { "游戏提优首次生效 detectionLatency=", "Game priority first applied detectionLatency=" }
			},
			{
				"log.gamemodeboost.61",
				new string[2] { "ms", "ms" }
			},
			{
				"log.fthtweak.1",
				new string[2] { "容错堆排除名单写入失败 未摘任何垫片指针 系统保持原样", "Failed to write the fault-tolerant-heap exclusion list; no shim pointer was removed and the system is unchanged" }
			},
			{
				"log.fthtweak.2",
				new string[2] { "容错堆垫片指针摘除失败 ", "Failed to remove the fault-tolerant-heap shim pointer " }
			},
			{
				"log.fthtweak.3",
				new string[2] { "容错堆垫片已解除 {0} 项 已加入排除名单 {1} 垫片文件保留未删除", "Removed {0} fault-tolerant-heap shim(s) and added {1} to the exclusion list; the shim files were kept, not deleted" }
			},
			{
				"log.fthtweak.4",
				new string[2] { "容错堆改动已全部还原 垫片指针与排除名单回到原值", "All fault-tolerant-heap changes were restored; the shim pointers and exclusion list are back to their original values" }
			},
			{
				"log.fthtweak.5",
				new string[2] { "容错堆改动还原不完整 请查看上方逐项结果", "The fault-tolerant-heap changes were not fully restored; see the per-item results above" }
			},
			{
				"log.fthtweak.6",
				new string[2] { "容错堆排除名单类型异常 已被改成 MULTI_SZ 以外的类型 不做改动免得丢掉原值 当前类型 ", "The fault-tolerant-heap exclusion list has an unexpected type — it was changed to something other than MULTI_SZ. Leaving it alone so the original value is not lost. Current type: " }
			},
			{
				"t.legacypurge.1",
				new string[2] { "Pavise 建的卓越性能计划", "Ultimate Performance plan created by Pavise" }
			},
			{
				"t.legacypurge.2",
				new string[2] { "Pavise 托管电源方案", "Pavise managed power plan" }
			},
			{
				"t.legacypurge.3",
				new string[2] { "核心停泊", "Core parking" }
			},
			{
				"t.legacypurge.6",
				new string[2] { "无输入降级", "No-input throttling" }
			},
			{
				"t.legacypurge.7",
				new string[2] { "电源滑块", "Power slider" }
			},
			{
				"t.legacypurge.8",
				new string[2] { "服务暂停", "Service pause" }
			},
			{
				"t.legacypurge.10",
				new string[2] { "网络优化", "Network optimization" }
			},
			{
				"t.legacypurge.11",
				new string[2] { "时间片校正", "Time-slice correction" }
			},
			{
				"t.legacypurge.14",
				new string[2] { "辅助功能拦截", "Accessibility interception" }
			},
			{
				"t.legacypurge.16",
				new string[2] { "旧键鼠队列设置恢复", "Legacy keyboard/mouse queue recovery" }
			},
			{
				"t.legacypurge.21",
				new string[2] { "MSI 修复", "MSI fix" }
			},
			{
				"t.legacypurge.22",
				new string[2] { "后备提优 IFEO", "Fallback boost IFEO" }
			},
			{
				"t.legacypurge.28",
				new string[2] { "未完成的进程状态记账", "Unfinished process state bookkeeping" }
			},
			{
				"t.legacypurge.29",
				new string[2] { "设备中断页亲和性设置", "Device Interrupts page affinity" }
			},
			{
				"log.legacypurge.31",
				new string[2] { " 项未能还原 ", " items could not be restored " }
			},
			{
				"log.legacypurge.37",
				new string[2] { " 先还原 Pavise 改过的全部系统项", " restoring all system items Pavise changed first" }
			},
			{
				"log.legacypurge.38",
				new string[2] { " 已中止 ", " aborted " }
			},
			{
				"log.legacypurge.39",
				new string[2] { " 没有删除任何文件", " no files deleted" }
			},
			{
				"log.legacypurge.45",
				new string[2] { "进程状态记账仍有未能核实的项，恢复记录保留；相关进程退出或重启电脑后可重试。", "Some process state bookkeeping entries could not be verified. Recovery records are retained; retry after the affected processes exit or the PC restarts." }
			},
			{
				"t.peripheralcatalog.9",
				new string[2] { "蓝牙", "Bluetooth" }
			},
			{
				"log.interruptattribution.9",
				new string[2] { "中断观测 已有别的会话在用 本次让开", "Interrupt observation: another session already owns the probe; standing down" }
			},
			{
				"t.steamshortcut.1",
				new string[2] { "无法读取快捷方式文件", "Unable to read the shortcut file" }
			},
			{
				"t.steamshortcut.2",
				new string[2] { "不是 Steam 游戏快捷方式：未找到 steam://rungameid 链接", "Not a Steam game shortcut: no steam://rungameid link was found" }
			},
			{
				"t.steamshortcut.3",
				new string[2] { "未找到 Steam 安装：注册表中无 SteamPath", "Steam installation not found: no SteamPath in the registry" }
			},
			{
				"t.steamshortcut.4",
				new string[2] { "Steam 清单中找不到该游戏 appid ", "Game not found in Steam manifests, appid " }
			},
			{
				"t.steamshortcut.5",
				new string[2] { "，可能是非 Steam 游戏的快捷方式，或该游戏未安装", ", possibly a shortcut to a non-Steam game, or the game is not installed" }
			},
			{
				"t.steamshortcut.6",
				new string[2] { "在游戏目录里没有找到合适的主程序 ", "No suitable main executable found in the game directory " }
			},
			{
				"log.tamer.1",
				new string[2] { "反作弊分组 ", "Anti-cheat group " }
			},
			{
				"log.tamer.3",
				new string[2] { "开启压制", "enable suppression" }
			},
			{
				"log.tamer.4",
				new string[2] { "关闭并恢复", "disable and restore" }
			},
			{
				"log.tamer.5",
				new string[2] { "反作弊压制引擎启动 保留原核心分配", "Anti-cheat suppression engine started; original core placement preserved" }
			},
			{
				"t.tamer.6",
				new string[2] { "紧急恢复冷却", "Emergency restore cooldown" }
			},
			{
				"log.tamer.7",
				new string[2] { "反作弊压制异常 ", "Anti-cheat suppression exception " }
			},
			{
				"log.tamer.8",
				new string[2] { "压制 ", "Suppress " }
			},
			{
				"log.tamer.9",
				new string[2] { "打开 ", "Open " }
			},
			{
				"log.tamer.10",
				new string[2] { " 未压制：句柄受内核保护", " not suppressed: the handle is kernel-protected" }
			},
			{
				"log.tamer.11",
				new string[2] { " 未完全生效", " not fully applied" }
			},
			{
				"log.tamer.12",
				new string[2] { " 失败环节 ", " failed step " }
			},
			{
				"log.tamer.13",
				new string[2] { " 已保留快照 将按退避计划重试", " snapshot kept, will retry per backoff schedule" }
			},
			{
				"log.tamer.14",
				new string[2] { "反作弊压制解除 ", "Anti-cheat suppression lifted " }
			},
			{
				"t.versionmigrations.2",
				new string[2] { "未知", "unknown" }
			},
			{
				"t.versionmigrations.8",
				new string[2] { "网卡中断亲和", "NIC interrupt affinity" }
			},
			{
				"log.versionmigrations.41",
				new string[2] { "开启", "enabled" }
			},
			{
				"t.gameexecutableresolver.1",
				new string[2] { "未选择文件", "No file selected" }
			},
			{
				"t.gameexecutableresolver.2",
				new string[2] { "文件路径无效", "Invalid file path" }
			},
			{
				"t.gameexecutableresolver.3",
				new string[2] { "文件不存在", "File does not exist" }
			},
			{
				"t.gameexecutableresolver.4",
				new string[2] { "快捷方式没有指向有效的 EXE", "The shortcut does not point to a valid EXE" }
			},
			{
				"t.gameexecutableresolver.5",
				new string[2] { "仅支持 EXE、LNK 和 Steam URL 快捷方式", "Only EXE, LNK, and Steam URL shortcuts are supported" }
			},
			{
				"t.gameexecutableresolver.6",
				new string[2] { "快捷方式目标路径无效", "Invalid shortcut target path" }
			},
			{
				"t.gameexecutableresolver.7",
				new string[2] { "目标必须是本机存在的有效 EXE 文件", "The target must be a valid EXE file that exists on this machine" }
			},
			{
				"t.gameexecutableresolver.8",
				new string[3] { "这个文件夹里找不到唯一的游戏程序，请在添加游戏窗口里选择具体的 EXE。", "No single game program could be identified in this folder; pick the exact EXE in the Add Game window.", "このフォルダーからゲームの実行ファイルを一意に特定できません。ゲームを追加ウィンドウで EXE を選んでください。" }
			},
			{
				"log.gameprofiles.1",
				new string[2] { "游戏档案读取失败 本次已标记为致命保存故障 原文件未改写 主程序将强制清空数据并退出", "Game profiles failed to load. This run is now marked with a fatal save failure; the original file was not modified, and the app will forcibly clear its data and exit" }
			},
			{
				"log.gameprofiles.3",
				new string[2] { "游戏档案保存失败", "Failed to save game profiles" }
			},
			{
				"log.gameprofiles.busy",
				new string[2] { "游戏档案暂时被占用，重试未成功；旧档保留，可稍后重试", "Game profiles are temporarily busy; bounded retries exhausted. The old file is retained and saving can be retried later" }
			},
			{
				"log.gameprofiles.9",
				new string[2] { "游戏档案读取失败 已保护现有文件不被覆盖", "Failed to read game profiles; the existing file is protected from being overwritten" }
			},
			{
				"t.gputhrottleprobe.5",
				new string[3] { " 次）", " samples)", " 回）" }
			},
			{
				"t.gputhrottleprobe.6",
				new string[3] { "未报告降频标记", "No performance-decrease flag reported", "性能低下フラグの報告なし" }
			},
			{
				"t.gputhrottleprobe.7",
				new string[3] { "温度保护标记", "Thermal protection flag", "温度保護フラグ" }
			},
			{
				"t.gputhrottleprobe.8",
				new string[3] { "功耗或供电标记", "Power control or supply flag", "電力制御または供給フラグ" }
			},
			{
				"t.gputhrottleprobe.9",
				new string[3] { "AC/BATT 电源策略标记", "AC/BATT power policy flag", "AC/BATT 電源ポリシーフラグ" }
			},
			{
				"t.gputhrottleprobe.10",
				new string[3] { "软件触发标记", "API-triggered flag", "API 起因フラグ" }
			},
			{
				"t.gputhrottleprobe.11",
				new string[3] { "其他驱动标记 0x", "Other driver flags 0x", "その他のドライバーフラグ 0x" }
			},
			{
				"t.inputchainprobe.1",
				new string[2] { "USB 或 2.4G 接收器", "USB or 2.4G receiver" }
			},
			{
				"t.inputchainprobe.2",
				new string[2] { "低功耗蓝牙", "Bluetooth Low Energy" }
			},
			{
				"t.inputchainprobe.3",
				new string[2] { "虚拟设备", "Virtual device" }
			},
			{
				"t.inputchainprobe.4",
				new string[2] { "内置", "Built-in" }
			},
			{
				"log.interruptattribution.1",
				new string[2] { "中断来源 本机系统不支持独立内核会话 跳过归因", "Interrupt source: this system does not support standalone kernel sessions, attribution skipped" }
			},
			{
				"log.interruptattribution.2",
				new string[2] { "中断来源 会话创建失败 ", "Interrupt source: session creation failed " }
			},
			{
				"log.interruptattribution.3",
				new string[2] { "中断来源 打开实时会话失败 ", "Interrupt source: failed to open real-time session " }
			},
			{
				"t.interruptattribution.4",
				new string[2] { "未启动", "Not started" }
			},
			{
				"t.interruptattribution.5",
				new string[2] { "采集线程未按时退出，本次归因作废", "The capture thread did not exit in time; this attribution is discarded" }
			},
			{
				"t.interruptattribution.6",
				new string[2] { "窗口内未采集到中断事件", "No interrupt events were captured within the window" }
			},
			{
				"log.peripheralvendorprobe.1",
				new string[2] { "外设词条 提取到 ", "Peripheral terms: extracted " }
			},
			{
				"log.peripheralvendorprobe.2",
				new string[2] { " 条 ", " entries " }
			},
			{
				"log.peripheralvendorprobe.3",
				new string[2] { "外设词条 提取失败 ", "Peripheral terms: extraction failed " }
			},
			{
				"t.systemaudithardware.1",
				new string[2] { "内存频率", "Memory frequency" }
			},
			{
				"t.systemaudithardware.2",
				new string[2] { " 标称 ", " rated " }
			},
			{
				"t.systemaudithardware.3",
				new string[2] { "读取失败", "Read failed" }
			},
			{
				"t.systemaudithardware.4",
				new string[2] { "JEDEC 默认频率，未达标称值；可在 BIOS 开启 XMP/EXPO。板载内存不适用。", "Running at JEDEC default, below rated speed. Enable XMP/EXPO in BIOS; not for soldered memory." }
			},
			{
				"t.systemaudithardware.5",
				new string[2] { "已接近标称频率，或本机读数不足以判断，无需处理。", "Close to the rated frequency, or the local readings are insufficient to judge; no action required." }
			},
			{
				"t.systemaudithardware.6",
				new string[2] { "内存通道", "Memory channels" }
			},
			{
				"t.systemaudithardware.7",
				new string[2] { " 条内存", " memory module(s)" }
			},
			{
				"t.systemaudithardware.8",
				new string[2] { "单条内存仅有单通道带宽，会影响 1% Low。可加装同规格内存组成双通道；板载内存不适用。", "One module provides single-channel bandwidth and can hurt 1% lows. Add a matching module for dual channel; not applicable to soldered memory." }
			},
			{
				"t.systemaudithardware.9",
				new string[2] { "两条及以上，通常已组成双通道，无需处理。", "Two or more modules, normally already running dual-channel; no action required." }
			},
			{
				"t.systemaudithardware.10",
				new string[2] { "多屏刷新率", "Multi-monitor refresh rates" }
			},
			{
				"t.systemaudithardware.11",
				new string[2] { "显示器刷新率不一致，旧版桌面合成器可能掉帧。更新至 24H2，或统一刷新率。", "Mismatched monitor refresh rates can make older desktop compositors drop frames. Update to 24H2 or match the rates." }
			},
			{
				"t.systemaudithardware.12",
				new string[2] { "刷新率不一致，但当前系统版本已包含混合刷新率改进，一般无影响。", "The refresh rates differ, but this Windows version already includes the mixed-refresh-rate improvements, so there is generally no impact." }
			},
			{
				"t.systemaudithardware.13",
				new string[2] { "各显示器一致，无需处理。", "All displays match; no action required." }
			},
			{
				"t.systemaudithardware.14",
				new string[2] { "CPU 降频事件", "CPU throttling events" }
			},
			{
				"t.systemaudithardware.15",
				new string[2] { "近 7 天 ", "Last 7 days " }
			},
			{
				"t.systemaudithardware.16",
				new string[2] { "近 7 天无记录", "No records in the last 7 days" }
			},
			{
				"t.systemaudithardware.17",
				new string[2] { "系统日志存在 CPU 降频记录，通常由散热或供电不足引起。请清灰、改善风道或检查适配器后复查。", "CPU throttling appears in the system log, usually from cooling or power limits. Clean dust, improve airflow, or check the adapter, then rescan." }
			},
			{
				"t.systemaudithardware.18",
				new string[2] { "系统日志中无处理器降频记录，散热与供电当前不构成瓶颈。", "The system log contains no processor throttling records; cooling and power delivery are not a bottleneck at present." }
			},
			{
				"t.systemaudithardware.19",
				new string[2] { "灯效常驻软件", "Resident RGB lighting software" }
			},
			{
				"t.systemaudithardware.21",
				new string[2] { "Ryzen 分支预测优化", "Ryzen branch prediction optimization" }
			},
			{
				"t.systemaudithardware.22",
				new string[2] { "系统版本较旧", "Windows version is old" }
			},
			{
				"t.systemaudithardware.23",
				new string[2] { "24H2 已含", "Included in 24H2" }
			},
			{
				"t.systemaudithardware.24",
				new string[2] { "Ryzen 建议更新至 Windows 11 24H2；23H2 需 2024-08 后更新。", "Ryzen: use Windows 11 24H2, or 23H2 with post-August-2024 updates." }
			},
			{
				"t.systemaudithardware.25",
				new string[2] { "当前系统已包含 AMD 分支预测优化，无需处理。", "This Windows version already includes the AMD branch prediction optimizations; no action required." }
			},
			{
				"t.systemaudithardware.28",
				new string[2] { "显卡 MSI 中断", "GPU MSI interrupts" }
			},
			{
				"t.systemaudithardware.29",
				new string[2] { " 个设备被关闭", " device(s) disabled" }
			},
			{
				"t.systemaudithardware.31",
				new string[2] { "显卡 MSI 已关闭。若从未因此黑屏/死机，可修复并重启。", "GPU MSI is disabled. If it never caused black screens or freezes, repair and reboot." }
			},
			{
				"t.systemaudithardware.32",
				new string[2] { "消息信号中断未被干预，现代驱动默认即为 MSI，无需处理。", "Message-signaled interrupts have not been tampered with; modern drivers use MSI by default. No action required." }
			},
			{
				"t.systemaudithardware.33",
				new string[2] { "3D V-Cache 优化驱动", "3D V-Cache optimizer driver" }
			},
			{
				"t.systemaudithardware.34",
				new string[2] { "已安装，游戏模式开启", "Installed, Game Mode on" }
			},
			{
				"t.systemaudithardware.35",
				new string[2] { "已安装，游戏模式关闭", "Installed, Game Mode off" }
			},
			{
				"t.systemaudithardware.36",
				new string[2] { "未安装", "Not installed" }
			},
			{
				"t.systemaudithardware.37",
				new string[2] { "AMD 优化驱动已负责 CCD 分配，核心策略保持全核。", "The AMD optimizer handles CCD placement; keep the core strategy on all cores." }
			},
			{
				"t.systemaudithardware.38",
				new string[2] { "AMD 优化驱动存在，但游戏模式已关闭。先开启；CCD 仍错误再手选。", "AMD optimizer is installed, but Game Mode is off. Enable it first; choose a CCD only if still wrong." }
			},
			{
				"t.systemaudithardware.39",
				new string[2] { "缺少 AMD 3D V-Cache 优化驱动。建议安装；否则手选 CCD 并比较 1% Low。", "AMD 3D V-Cache optimizer is missing. Install it, or choose a CCD and compare 1% lows." }
			},
			{
				"t.systemauditverdicts.1",
				new string[2] { "主屏刷新率", "Primary display refresh rate" }
			},
			{
				"t.systemauditverdicts.2",
				new string[2] { "强烈建议处理", "Strongly recommend fixing" }
			},
			{
				"t.systemauditverdicts.3",
				new string[2] { "当前 ", "Current " }
			},
			{
				"t.systemauditverdicts.4",
				new string[2] { "Hz 可用 ", "Hz, available " }
			},
			{
				"t.systemauditverdicts.5",
				new string[2] { "Hz。刷新率差值会直接限制帧率上限。请在设置 → 显示 → 高级显示中改为 ", "Hz. The gap directly limits frame-rate ceiling. In Settings → Display → Advanced display, change it to " }
			},
			{
				"t.systemauditverdicts.6",
				new string[2] { "Hz。该项为持久设置，修改一次后长期生效，Pavise 不代为修改。", "Hz. This is a persistent setting: change it once and it stays. Pavise does not change it for you." }
			},
			{
				"t.systemauditverdicts.7",
				new string[2] { "筛选键", "Filter Keys" }
			},
			{
				"t.systemauditverdicts.8",
				new string[2] { "筛选键已在输入路径上插入 ", "Filter Keys is inserting " }
			},
			{
				"t.systemauditverdicts.9",
				new string[2] { " 毫秒接受延迟，远高于键鼠外设常见的 1–5 毫秒。请在系统环境页开启「辅助功能按键拦截」。", " ms of acceptance delay, far above the usual 1–5 ms peripheral path. Enable Accessibility key interception on System Environment." }
			},
			{
				"t.systemauditverdicts.10",
				new string[2] { "辅助功能热键", "Accessibility hotkeys" }
			},
			{
				"t.systemauditverdicts.11",
				new string[2] { "建议关闭", "Recommend disabling" }
			},
			{
				"t.systemauditverdicts.12",
				new string[2] { "触发热键仍会在全屏游戏中弹窗。请在系统环境页开启「辅助功能按键拦截」。", "Accessibility hotkeys can still raise dialogs over fullscreen games. Enable Accessibility key interception on System Environment." }
			},
			{
				"t.systemauditverdicts.13",
				new string[2] { "蓝牙键鼠", "Bluetooth keyboard/mouse" }
			},
			{
				"t.systemauditverdicts.14",
				new string[2] { "建议换连接方式", "Recommend switching connection type" }
			},
			{
				"t.systemauditverdicts.15",
				new string[2] { "实测蓝牙 10.4ms，2.4G 3.6ms。改用 2.4G 或有线；无软件解法。", "Measured: Bluetooth 10.4 ms, 2.4G 3.6 ms. Use 2.4G or wired; no software fix." }
			},
			{
				"t.systemauditverdicts.24",
				new string[2] { "建议开启", "Recommend enabling" }
			},
			{
				"t.systemauditverdicts.25",
				new string[2] { "合成台架六轮配对：1% Low 中位提升 90.7%，且未随时间恶化。", "Six paired synthetic runs: median 1% low improved 90.7%, with no time-based degradation." }
			},
			{
				"t.systemauditverdicts.60",
				new string[2] { "本次体检", "This audit" }
			},
			{
				"t.systemauditverdicts.61",
				new string[2] { "各项检查均未发现需要处理的问题；上方分区仍列出实测状态供参考。", "No check found anything that needs action; the sections above still list the measured state for reference." }
			},
			{
				"t.systemauditverdicts.62",
				new string[2] { "没有需要处理的项", "Nothing needs action" }
			},
			{
				"t.systemauditverdicts.43",
				new string[2] { "Game DVR 后台录制", "Game DVR background recording" }
			},
			{
				"t.systemauditverdicts.44",
				new string[2] { "后台录制持续占用资源；不用 Xbox Game Bar 录制时建议关闭。", "Background recording continuously uses resources; disable it if Xbox Game Bar recording is unused." }
			},
			{
				"t.systemauditverdicts.45",
				new string[2] { "内存余量", "Memory headroom" }
			},
			{
				"t.systemauditverdicts.46",
				new string[2] { "只剩 ", "Only " }
			},
			{
				"t.systemauditverdicts.47",
				new string[2] { " GB 可用。内存不足会回退到磁盘并造成卡顿。请关闭高占用后台；长期不足需加内存。", " GB free. Memory pressure falls back to disk and stutters. Close heavy background apps; add RAM if persistent." }
			},
			{
				"t.systemauditverdicts.53",
				new string[2] { "页面文件", "Page file" }
			},
			{
				"t.systemauditverdicts.54",
				new string[2] { "建议恢复", "Recommend restoring" }
			},
			{
				"t.systemauditverdicts.55",
				new string[2] { "关闭页面文件不提帧，只会让内存不足时直接崩溃；部分游戏和反作弊还依赖它。", "Disabling the page file does not raise FPS; it turns memory pressure into crashes, and some games and anti-cheat require it." }
			},
			{
				"t.systemauditverdicts.56",
				new string[2] { "允许系统按需停泊", "System-managed parking allowed" }
			},
			{
				"t.systemauditverdicts.57",
				new string[2] { "当前供电策略允许按需停泊；这不代表核心此刻已停泊，也不能据此判定性能下降。AMD 平台保留驱动与系统的原有策略。", "The current power policy permits parking; it does not show which cores are parked or prove a performance loss. AMD platforms retain the original driver and system policy." }
			},
			{
				"t.systemaudit.3",
				new string[2] { "仅检测到核显，显卡高级调优不适用；后台压制、核心绑定和电源计划仍有效。", "Only integrated graphics was detected, so advanced GPU tuning is unavailable; background suppression, core binding, and power plans still work." }
			},
			{
				"t.systemaudit.4",
				new string[2] { "已检测到独立显卡。显卡页的驱动项是否可用，取决于下方两项接口检测结果。", "A discrete GPU is present. Whether the driver items on the Graphics page are usable depends on the two interface checks below." }
			},
			{
				"log.energymeter.1",
				new string[2] { "处理器功耗通道 ", "Processor power channels: " }
			},
			{
				"t.energymeter.1",
				new string[2] { "本机没有能量计量接口 读不到处理器功耗", "This machine exposes no energy meter interface; processor power is unreadable" }
			},
			{
				"t.energymeter.2",
				new string[2] { "有能量计量接口但没有可识别的处理器功耗通道", "An energy meter interface exists but exposes no recognizable processor power rail" }
			},
			{
				"t.energymeter.3",
				new string[2] { "能量计量接口在场但读取失败", "The energy meter interface is present but could not be read" }
			},
			{
				"t.systemaudit.5",
				new string[2] { "NVIDIA 驱动接口", "NVIDIA driver interface" }
			},
			{
				"t.systemaudit.6",
				new string[2] { "可用", "Available" }
			},
			{
				"t.systemaudit.7",
				new string[2] { "不可用", "Unavailable" }
			},
			{
				"t.systemaudit.8",
				new string[2] { "驱动级电源、低延迟和着色器缓存等设置可用；写入失败会停用对应开关。", "Driver power, low-latency, and shader-cache settings are available; failed writes disable the related switch." }
			},
			{
				"t.systemaudit.9",
				new string[2] { "NVIDIA 驱动接口初始化失败，可能是驱动过旧或精简版；NVIDIA 调优已停用。", "NVIDIA driver initialization failed, likely from an old or stripped driver; NVIDIA tuning is disabled." }
			},
			{
				"t.systemaudit.10",
				new string[2] { "本机没有 NVIDIA 显卡，显卡页 NVIDIA 区整体停用。", "No NVIDIA GPU is present; the NVIDIA section of the Graphics page is disabled." }
			},
			{
				"t.systemaudit.16",
				new string[2] { "CPU Sets 分区", "CPU Sets partitioning" }
			},
			{
				"t.systemaudit.17",
				new string[2] { "可将后台进程限定到独立核心，为游戏保留其余核心。", "Can confine background processes to separate cores, reserving the remaining cores for the game." }
			},
			{
				"t.systemaudit.18",
				new string[2] { "核心数量不足以划分分区，压制仅降低优先级。实测表明该步骤已占绝大部分收益。", "There are too few cores to partition, so suppression only lowers priorities. Measurements show that step alone accounts for most of the gain." }
			},
			{
				"t.systemaudit.19",
				new string[2] { "效率模式 EcoQoS", "Efficiency mode (EcoQoS)" }
			},
			{
				"t.systemaudit.20",
				new string[2] { "不支持", "Not supported" }
			},
			{
				"t.systemaudit.21",
				new string[2] { "支持", "Supported" }
			},
			{
				"t.systemaudit.22",
				new string[2] { "接口可用", "Interface available" }
			},
			{
				"t.systemaudit.23",
				new string[2] { "本机无法查询该状态，温和档将跳过该手段。", "This state cannot be queried on this machine, so the Gentle tier skips this technique." }
			},
			{
				"t.systemaudit.24",
				new string[2] { "温和档可使系统对后台进程降频，并将其迁移至能效核心。", "The Gentle tier lets the system down-clock background processes and move them to efficiency cores." }
			},
			{
				"t.systemaudit.25",
				new string[2] { "Windows 10 可用后台压制，但不如 Windows 11 彻底。", "Background suppression works on Windows 10, though less completely than on Windows 11." }
			},
			{
				"t.systemaudit.26",
				new string[2] { "操作系统", "Operating system" }
			},
			{
				"t.systemaudit.27",
				new string[2] { "系统版本决定可用的优化手段，同一开关在不同版本上的实际行为并不相同。", "The Windows version determines which techniques are available; the same switch behaves differently across versions." }
			},
			{
				"t.systemaudit.1",
				new string[2] { "未被修改", "Unmodified" }
			},
			{
				"t.systemaudithardware.30",
				new string[2] { "正常", "Normal" }
			},
			{
				"t.systemauditverdicts.35",
				new string[2] { "建议处理", "Recommend fixing" }
			},
			{
				"t.systemaudit.28",
				new string[2] { "混合架构（P 核 + E 核）。默认全核，可手动切换为仅性能核。", "Hybrid architecture (P-cores plus E-cores). All cores by default; can be switched manually to performance cores only." }
			},
			{
				"t.systemaudit.29",
				new string[2] { "非对称缓存（X3D）。默认全核，可手动切换至大缓存 CCD。", "Asymmetric cache (X3D). All cores by default; can be switched manually to the large-cache CCD." }
			},
			{
				"t.systemaudit.30",
				new string[2] { "对称多 CCD。默认全核，可手动切换至单个完整 CCD。", "Symmetric multi-CCD. All cores by default; can be switched manually to one full CCD." }
			},
			{
				"t.systemaudit.31",
				new string[2] { "同构架构。默认全核，可手动切换至游戏核心分区。", "Homogeneous architecture. All cores by default; can be switched manually to a game core partition." }
			},
			{
				"t.systemaudit.32",
				new string[2] { "同构", "Homogeneous" }
			},
			{
				"t.systemaudit.33",
				new string[2] { "CPU 拓扑", "CPU topology" }
			},
			{
				"t.systemaudit.34",
				new string[2] { " 物理核 ", " physical cores, " }
			},
			{
				"t.systemaudit.35",
				new string[2] { " 线程", " threads" }
			},
			{
				"t.systemaudit.36",
				new string[2] { " 手动游戏分区 0x", " manual game partition 0x" }
			},
			{
				"t.systemaudit.37",
				new string[2] { " 本机不划分后台核心", " background cores not partitioned on this machine" }
			},
			{
				"t.systemaudit.38",
				new string[2] { "整机 CPU 占用", "Total CPU usage" }
			},
			{
				"t.systemaudit.39",
				new string[2] { "体检窗口 ", "Checkup window " }
			},
			{
				"t.systemaudit.40",
				new string[2] { " 秒", " s" }
			},
			{
				"t.systemaudit.41",
				new string[2] { " 毫秒", " ms" }
			},
			{
				"t.systemaudit.42",
				new string[2] { " 内的平均占用", " — average usage within this window" }
			},
			{
				"t.systemaudit.54",
				new string[2] { " 已是最高", " is already the highest" }
			},
			{
				"t.systemaudit.55",
				new string[2] { " 可用 ", " available " }
			},
			{
				"t.systemaudit.56",
				new string[2] { "无法读取显示模式，远程桌面与部分虚拟显示器上会出现该情况，本次不对该项作出判断。", "The display mode could not be read; this occurs over Remote Desktop and on some virtual displays. This item is not evaluated this time." }
			},
			{
				"t.systemaudit.57",
				new string[2] { "当前分辨率下没有更高的刷新率可选", "No higher refresh rate is available at the current resolution" }
			},
			{
				"t.systemaudit.58",
				new string[2] { "当前刷新率 ", "Current refresh rate " }
			},
			{
				"t.systemaudit.59",
				new string[2] { "Hz，该显示器在相同分辨率下支持 ", "Hz; at the same resolution this display supports " }
			},
			{
				"t.systemaudit.60",
				new string[2] { "Hz。差值会直接损失帧率。请在设置 → 显示 → 高级显示中改为 ", "Hz. The difference directly costs FPS. In Settings → Display → Advanced display, change it to " }
			},
			{
				"t.systemaudit.61",
				new string[2] { "Hz。该项为持久设置，修改一次后长期生效。", "Hz. This is a persistent setting: change it once and it stays." }
			},
			{
				"t.systemaudit.62",
				new string[3] { "NVIDIA 驱动降频标记", "NVIDIA performance-decrease flags", "NVIDIA 性能低下フラグ" }
			},
			{
				"t.systemaudit.63",
				new string[3] { "驱动报告了上述标记；可能来自读取到的任一 NVIDIA 显卡。仅凭标记不能确认游戏瓶颈或实际电池供电，需结合游戏显卡的负载、频率及供电状态判断。", "The driver reported these flags on a queried NVIDIA GPU. Flags alone do not establish a game bottleneck or battery operation; correlate the game GPU's load, clocks and power source.", "照会した NVIDIA GPU のドライバーが上記フラグを報告しました。これだけではゲームのボトルネックやバッテリー駆動を判断できません。ゲーム用 GPU の負荷、クロック、電源状態と合わせて確認してください。" }
			},
			{
				"t.systemaudit.64",
				new string[3] { "当前未报告降频标记。对局中采样命中的标记会在退局时汇总到运行日志；比例不代表帧率损失。", "No performance-decrease flag is currently reported. Match sample hits are summarized in the log at match end; percentages are not FPS loss.", "現在、性能低下フラグの報告はありません。対局中に検出したフラグは終了時にログへ集計されます。割合は FPS の損失率ではありません。" }
			},
			{
				"t.systemaudit.65",
				new string[2] { "节能模式", "Energy saver mode" }
			},
			{
				"t.systemaudit.66",
				new string[2] { "开着", "On" }
			},
			{
				"t.systemaudit.67",
				new string[2] { "关着", "Off" }
			},
			{
				"t.systemaudit.68",
				new string[2] { "系统节能模式会限制功耗并锁定电源模式，使 Pavise 电源优化失效。请先在系统设置中关闭。", "Windows Energy Saver limits power and locks power mode, blocking Pavise power tuning. Disable it in Settings first." }
			},
			{
				"t.systemaudit.69",
				new string[2] { "未拦截电源优化", "Not blocking power optimizations" }
			},
			{
				"t.systemaudit.70",
				new string[2] { "生效中", "Active" }
			},
			{
				"t.systemaudit.71",
				new string[2] { "长时间无键鼠输入时，前台程序不会被降级。", "The foreground app is not demoted after a long stretch without keyboard or mouse input." }
			},
			{
				"t.systemaudit.72",
				new string[2] { "长时间无键鼠输入会降低前台 QoS，影响手柄游戏和过场。可在优化策略页关闭。", "Long periods without keyboard/mouse input lower foreground QoS, affecting controller play and cutscenes. Disable it on Optimization Policy." }
			},
			{
				"t.systemaudit.73",
				new string[2] { "ReBAR 显存直通", "ReBAR VRAM direct access" }
			},
			{
				"t.systemaudit.74",
				new string[2] { " 窗口 ", " window " }
			},
			{
				"t.systemaudit.75",
				new string[2] { "显卡页的 ReBAR 强制开启可用。", "ReBAR force-enable on the Graphics page is available." }
			},
			{
				"t.systemaudit.76",
				new string[2] { "未在 BIOS 中启用时，ReBAR 强制开启无效，需先在 BIOS 中开启。", "While it is disabled in the BIOS, ReBAR force-enable has no effect; enable it in the BIOS first." }
			},
			{
				"t.systemaudit.77",
				new string[2] { "AMD 平台称为 SAM，驱动侧已自动受益，无需 Pavise 干预。", "On AMD platforms this is called SAM; the driver side already benefits automatically and needs no intervention from Pavise." }
			},
			{
				"t.systemaudit.78",
				new string[2] { "AMD 称为 SAM；需在 BIOS 开启 Above 4G Decoding 与 ReBAR。", "AMD calls it SAM; enable Above 4G Decoding and ReBAR in BIOS." }
			},
			{
				"t.systemaudit.79",
				new string[2] { "内存", "Memory" }
			},
			{
				"t.systemaudit.80",
				new string[2] { " GB 已用 ", " GB, used " }
			},
			{
				"t.systemaudit.81",
				new string[2] { "可用内存偏低，游戏可能回退到磁盘并卡顿。请关闭后台，或启用待机内存清理。", "Available memory is low, so games may fall back to disk and stutter. Close background apps or enable standby-memory purge." }
			},
			{
				"t.systemaudit.82",
				new string[2] { "内存余量充足", "Plenty of memory headroom" }
			},
			{
				"t.systemaudit.83",
				new string[2] { "已被关闭", "Disabled" }
			},
			{
				"t.systemaudit.84",
				new string[2] { "页面文件已关闭，内存不足时游戏会直接崩溃。建议改回系统管理。", "The page file is disabled, so games may crash under memory pressure. Restore system-managed size." }
			},
			{
				"t.systemaudit.85",
				new string[2] { "由系统管理或大小正常，无需处理。", "System-managed or normally sized; no action required." }
			},
			{
				"t.systemaudit.86",
				new string[2] { "网络链路", "Network link" }
			},
			{
				"t.systemaudit.87",
				new string[2] { "有线", "Wired" }
			},
			{
				"t.systemaudit.88",
				new string[2] { "无线 Wi-Fi", "Wireless (Wi-Fi)" }
			},
			{
				"t.systemaudit.89",
				new string[2] { "有线加无线同时在线", "Wired and wireless both online" }
			},
			{
				"t.systemaudit.90",
				new string[2] { "Wi-Fi 延迟和突刺高于有线。条件允许时优先使用有线。", "Wi-Fi has more latency spikes than wired. Prefer wired where possible." }
			},
			{
				"t.systemaudit.91",
				new string[2] { "有线链路，延迟稳定性最佳。", "Wired link, best latency stability." }
			},
			{
				"t.systemaudit.92",
				new string[2] { "Wi-Fi 与有线同时连接，流量取决于系统路由。要确保走有线，可临时关闭 Wi-Fi。", "Wi-Fi and Ethernet are both connected; routing chooses the path. Turn off Wi-Fi to ensure Ethernet." }
			},
			{
				"t.systemaudit.93",
				new string[2] { "供电方式", "Power source" }
			},
			{
				"t.systemaudit.94",
				new string[2] { "外接电源", "AC power" }
			},
			{
				"t.systemaudit.95",
				new string[2] { "电池供电", "On battery" }
			},
			{
				"t.systemaudit.96",
				new string[2] { "笔记本已接入外接电源，性能不受电池策略限制。", "The laptop is on AC power; performance is not limited by battery policy." }
			},
			{
				"t.systemaudit.97",
				new string[2] { "电池供电会限制 CPU/GPU 功耗，调度优化无法补回。请接电后对比。", "Battery power limits CPU/GPU power and cannot be recovered by scheduling tweaks. Compare while plugged in." }
			},
			{
				"t.systemaudit.98",
				new string[2] { "后台占用前三", "Top 3 background CPU consumers" }
			},
			{
				"t.systemaudit.99",
				new string[2] { "最近 ", "In the last " }
			},
			{
				"t.systemaudit.100",
				new string[2] { " 秒内 CPU 占用最高的程序。压制可回收其资源；需保护请加入白名单。", " seconds. These apps used the most CPU; suppression can reclaim it. Whitelist anything that needs protection." }
			},
			{
				"t.systemaudit.101",
				new string[2] { " 加 ", " plus " }
			},
			{
				"t.systemaudit.102",
				new string[2] { "鼠标 ", "Mouse " }
			},
			{
				"t.systemaudit.103",
				new string[2] { "键盘 ", "Keyboard " }
			},
			{
				"t.systemaudit.104",
				new string[2] { "同一鼠标实测：蓝牙约 10.4ms，2.4G 约 3.6ms。改用 2.4G 或有线；无软件解法。", "Same-mouse test: Bluetooth ≈10.4 ms, 2.4G ≈3.6 ms. Use 2.4G or wired; no software fix." }
			},
			{
				"t.systemaudit.105",
				new string[2] { "同时检测到蓝牙和更快连接。若实际使用蓝牙，改用 2.4G 或有线可减少约 7ms。", "Bluetooth and a faster link were both found. If Bluetooth is active, 2.4G or wired saves about 7 ms." }
			},
			{
				"t.systemaudit.106",
				new string[2] { "USB 和 2.4G 外设延迟约 1–5ms，已接近下限；继续降低需换硬件。", "USB and 2.4G peripheral latency is about 1–5 ms, near the floor; lower requires different hardware." }
			},
			{
				"t.systemaudit.107",
				new string[2] { "筛选键正开着", "Filter Keys is on" }
			},
			{
				"t.systemaudit.108",
				new string[2] { "热键激活", "Hotkey armed" }
			},
			{
				"t.systemaudit.109",
				new string[2] { "筛选键按微软的定义即让键盘忽略短促或重复的击键。当前每次击键需等待 ", "Filter Keys, by Microsoft's definition, makes the keyboard ignore brief or repeated keystrokes. At present every keystroke waits " }
			},
			{
				"t.systemaudit.110",
				new string[2] { " 毫秒接受延迟，是键鼠项中唯一可挽回的两位数毫秒。请开启「辅助功能按键拦截」。", " ms of acceptance delay—the only keyboard/mouse item here with double-digit recovery. Enable Accessibility key interception." }
			},
			{
				"t.systemaudit.111",
				new string[2] { "辅助功能触发热键仍可能在全屏游戏中弹窗；可在 Windows 辅助功能设置中检查。", "Accessibility hotkeys may still open dialogs in fullscreen games; review them in Windows Accessibility settings." }
			},
			{
				"t.systemaudit.112",
				new string[2] { "筛选键、粘滞键与切换键均未开启，触发热键也未激活，不会拦截击键。", "Filter Keys, Sticky Keys, and Toggle Keys are all off and no trigger hotkeys are armed; keystrokes are not intercepted." }
			},
			{
				"t.systemaudit.120",
				new string[2] { "ThreadDpcEnable 被写成 ", "ThreadDpcEnable was set to " }
			},
			{
				"t.systemaudit.121",
				new string[2] { "第三方键鼠改动", "Third-party keyboard/mouse modifications" }
			},
			{
				"t.systemaudit.124",
				new string[2] { "ThreadDpcEnable 无可信对照数据；如需清除，删除 HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel 下的 ThreadDpcEnable。", "ThreadDpcEnable lacks credible controlled data. To remove it, delete ThreadDpcEnable under HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel." }
			},
			{
				"t.systemaudit.125",
				new string[2] { "HAGS 硬件加速 GPU 调度", "HAGS hardware-accelerated GPU scheduling" }
			},
			{
				"t.systemaudit.126",
				new string[2] { "HAGS 对平均帧影响通常在误差内，但驱动级帧生成等功能依赖它；修改需重启。", "HAGS usually has negligible average-FPS impact, but driver frame generation depends on it. Reboot after changes." }
			},
			{
				"t.systemaudit.132",
				new string[2] { "MPO 多平面叠加", "MPO multi-plane overlay" }
			},
			{
				"t.systemaudit.133",
				new string[2] { "已被禁用", "Disabled" }
			},
			{
				"t.systemaudit.134",
				new string[2] { "系统默认", "System default" }
			},
			{
				"t.systemaudit.135",
				new string[2] { "OverlayTestMode 已禁用 MPO，可能影响录屏、直播和远程抓屏。删除 HKLM\\SOFTWARE\\Microsoft\\Windows\\Dwm 下的 OverlayTestMode 并重启可恢复。", "OverlayTestMode disabled MPO and may affect capture, streaming, and remote apps. Delete OverlayTestMode under HKLM\\SOFTWARE\\Microsoft\\Windows\\Dwm and reboot." }
			},
			{
				"t.systemaudit.136",
				new string[2] { "保持系统默认，本项 Pavise 不再提供开关。", "At the system default; Pavise no longer provides a switch for this item." }
			},
			{
				"t.systemaudit.137",
				new string[2] { "Xbox Game Bar 正在后台录制并占用资源，建议在优化策略页关闭。", "Xbox Game Bar is recording in the background and using resources. Disable it on Optimization Policy." }
			},
			{
				"t.systemaudit.138",
				new string[2] { "已关闭，无需处理。", "Disabled; no action required." }
			},
			{
				"t.systemaudit.139",
				new string[2] { "当前电源计划", "Current power plan" }
			},
			{
				"t.systemaudit.140",
				new string[2] { "启用电源计划后，对局自动切至高性能/卓越性能，退场还原。", "With power-plan control enabled, matches switch to High/Ultimate Performance and restore afterwards." }
			},
			{
				"t.systemaudit.144",
				new string[2] { "前台时间片", "Foreground time slice" }
			},
			{
				"t.systemaudit.145",
				new string[2] { "加权不足", "Boost below documented max" }
			},
			{
				"t.systemaudit.148",
				new string[2] { "网络限流值", "Network throttling index" }
			},
			{
				"t.systemaudit.149",
				new string[2] { "NetworkThrottlingIndex 超出有效配置范围。", "NetworkThrottlingIndex is outside the valid settings." }
			},
			{
				"t.systemaudit.150",
				new string[2] { "保留现有网络节流配置，无需修复。", "Preserve the existing network throttling setting; no repair required." }
			},
			{
				"t.systemaudit.151",
				new string[2] { "容错堆垫片", "Fault tolerant heap shim" }
			},
			{
				"t.systemaudit.152",
				new string[2] { "已挂载", "Attached" }
			},
			{
				"t.systemaudit.153",
				new string[2] { "程序已挂载 FTH 垫片。修复会移除引用、保留文件；可还原。", "An FTH shim is attached. Repair removes its reference and keeps the file; reversible." }
			},
			{
				"t.systemaudit.154",
				new string[2] { "FTH 仅在程序于时间窗内多次因堆损坏或 ntdll 异常崩溃后触发。", "FTH triggers only after repeated heap-corruption or ntdll crashes within its time window." }
			},
			{
				"t.threadlaneprobe.1",
				new string[2] { "Render Lane 线程探针：只读，不写入任何调度参数", "Render Lane thread probe: read-only, writes no scheduling parameters" }
			},
			{
				"t.threadlaneprobe.2",
				new string[2] { "目标 ", "Target " }
			},
			{
				"t.threadlaneprobe.3",
				new string[2] { "错误 ", "Error " }
			},
			{
				"t.threadlaneprobe.4",
				new string[2] { "线程总数 ", "Total threads " }
			},
			{
				"t.threadlaneprobe.5",
				new string[2] { " 成功采样 ", " successfully sampled " }
			},
			{
				"t.threadlaneprobe.6",
				new string[2] { " 采样轮次 ", " sampling rounds " }
			},
			{
				"t.threadlaneprobe.7",
				new string[2] { " 实际时长 ", " actual duration " }
			},
			{
				"t.threadlaneprobe.8",
				new string[2] { "进程整体占用 ", "Overall process usage " }
			},
			{
				"t.threadlaneprobe.9",
				new string[2] { " 核", " cores" }
			},
			{
				"t.threadlaneprobe.10",
				new string[2] { "主导线程连续稳定轮次 ", "Dominant thread consecutive stable rounds " }
			},
			{
				"t.threadlaneprobe.11",
				new string[2] { " 共 ", " of " }
			},
			{
				"t.threadlaneprobe.12",
				new string[2] { "按 CPU 占用排序的线程 Top", "Top threads by CPU usage" }
			},
			{
				"t.threadlaneprobe.13",
				new string[2] { "TID 占进程CPU% 读句柄 写句柄 THREAD_SET_LIMITED_INFORMATION", "TID %ProcCPU read-handle write-handle THREAD_SET_LIMITED_INFORMATION" }
			},
			{
				"t.threadlaneprobe.14",
				new string[2] { " 读句柄被拒 错误 ", " read handle denied, error " }
			},
			{
				"t.threadlaneprobe.15",
				new string[2] { "% 可", "% OK" }
			},
			{
				"t.threadlaneprobe.16",
				new string[2] { "可", "OK" }
			},
			{
				"t.threadlaneprobe.17",
				new string[2] { "拒 错误 ", "denied, error " }
			},
			{
				"t.threadlaneprobe.18",
				new string[2] { "结论 ", "Conclusion " }
			},
			{
				"t.threadlaneprobe.19",
				new string[2] { "线程调度句柄可获取，Render Lane 在该游戏上技术可行", "Thread scheduling handles are obtainable; Render Lane is technically feasible for this game" }
			},
			{
				"t.threadlaneprobe.20",
				new string[2] { "部分线程可写入，部分被拒绝，需逐线程降级处理", "Some threads are writable and some are denied; per-thread fallback handling is required" }
			},
			{
				"t.threadlaneprobe.21",
				new string[2] { "线程写句柄被拒绝，该游戏不做线程级提优", "Thread write handles were refused; thread-level boosting is not used for this game" }
			},
			{
				"t.backgroundpressurecontroller.1",
				new string[2] { "原因未知", "Unknown reason" }
			},
			{
				"t.backgroundpressurecontroller.2",
				new string[2] { "写入被拒绝，通常为安全软件的自我保护机制所致", "The write was denied, typically by security software protecting itself" }
			},
			{
				"t.backgroundpressurecontroller.3",
				new string[2] { "写入后回读不一致，该值已被其他程序改回", "The read-back after writing does not match; another program reverted the value" }
			},
			{
				"t.backgroundpressurecontroller.4",
				new string[2] { "写入被拒", "Write denied" }
			},
			{
				"t.backgroundpressurecontroller.5",
				new string[2] { "省电", "Power saving" }
			},
			{
				"t.backgroundpressurecontroller.6",
				new string[2] { "限流", "Throttling" }
			},
			{
				"t.backgroundpressurecontroller.7",
				new string[2] { "隔离", "Isolation" }
			},
			{
				"t.backgroundpressurecontroller.9",
				new string[2] { "放行", "Release" }
			},
			{
				"log.mmcss.3",
				new string[2] { "MMCSS 多媒体调度已还原", "MMCSS multimedia scheduling restored" }
			},
			{
				"log.mmcss.1",
				new string[2] { "MMCSS 多媒体调度应用失败，已尝试还原", "MMCSS scheduling apply failed; restoration attempted" }
			},
			{
				"log.mmcss.2",
				new string[2] { "MMCSS 多媒体调度已应用", "MMCSS multimedia scheduling applied" }
			},
			{
				"auto.mmcss",
				new string[2] { "MMCSS 多媒体调度", "MMCSS multimedia scheduling" }
			},
			{
				"log.mmcss.4",
				new string[2] { "MMCSS 多媒体调度还原未完成 下次启动继续", "MMCSS multimedia scheduling restore incomplete; it will continue on the next launch" }
			},
			{
				"log.protectedgameroster.1",
				new string[2] { "反作弊相容 ", "Anti-cheat compatibility " }
			},
			{
				"log.protectedgameroster.2",
				new string[2] { " 本体写入被拒 已记入相容名单 优先级与 IO 不再反复重试 显卡调度与帧线程照常尝试 换版本自动重来", " write to the game itself denied, recorded in the compatibility list; priority and I/O are no longer retried in a loop, while GPU scheduling and the frame thread are still attempted, and a new version starts over" }
			},
			{
				"log.protectedgameroster.3",
				new string[2] { "反作弊相容 已移出相容名单 ", "Anti-cheat compatibility: removed from the compatibility list " }
			},
			{
				"log.protectedgameroster.4",
				new string[2] { "反作弊相容 名单已清空 条目 ", "Anti-cheat compatibility: list cleared, entries " }
			},
			{
				"log.renderlane.changed",
				new string[2] { "候选线程已退出、已变化或身份无法确认，未写入，稍后重新采样", "Candidate thread exited, changed or could not be verified; nothing written, will resample" }
			},
			{
				"log.renderlane.sample.idle",
				new string[2] { "候选线程采样暂无可比较的 CPU 活动，稍后复查", "Candidate sampling: no comparable CPU activity yet; will retry" }
			},
			{
				"log.renderlane.sample.threads",
				new string[2] { "候选线程采样暂无可读取的线程，稍后复查", "Candidate sampling: no readable threads yet; will retry" }
			},
			{
				"log.renderlane.sample.process",
				new string[2] { "候选线程采样暂时无法读取进程，稍后复查", "Candidate sampling: process temporarily unreadable; will retry" }
			},
			{
				"log.renderlane.sample.exited",
				new string[2] { "候选线程采样目标进程已退出，停止本次跟踪", "Candidate sampling: target process exited; stopping this tracking session" }
			},
			{
				"log.renderlane.sample.identity",
				new string[2] { "候选线程采样目标进程身份已变化，停止本次跟踪", "Candidate sampling: target process identity changed; stopping this tracking session" }
			},
			{
				"log.renderlane.3",
				new string[2] { "渲染主权域 ", "Render sovereignty domain " }
			},
			{
				"log.renderlane.4",
				new string[2] { " 尚未确认连续稳定的候选线程；最近采样最高占比 ", " no consistently dominant candidate confirmed yet; latest top-thread share " }
			},
			{
				"log.renderlane.5",
				new string[2] { "% 共 ", "% across " }
			},
			{
				"log.renderlane.6",
				new string[2] { " 线程 暂不介入，稍后复查", " threads; no boost yet, checking again later" }
			},
			{
				"log.renderlane.7",
				new string[2] { "候选线程访问失败，本次跟踪停止；系统错误码", "Candidate thread access failed; stopping this tracking session; system error" }
			},
			{
				"log.renderlane.8",
				new string[2] { "渲染主权域 读不到线程原优先级 未做任何写入", "Render sovereignty domain: cannot read original thread priority, nothing was written" }
			},
			{
				"log.renderlane.9",
				new string[2] { " 的候选线程已有最高常规优先级，无需介入", " candidate thread already has the highest normal priority; no boost needed" }
			},
			{
				"log.renderlane.10",
				new string[2] { "渲染主权域 记账无法持久化 已放弃写入", "Render sovereignty domain: ledger cannot be persisted, write abandoned" }
			},
			{
				"log.renderlane.11",
				new string[2] { "渲染主权域 写入线程优先级失败 已清账", "Render sovereignty domain: failed to write thread priority, ledger cleared" }
			},
			{
				"log.renderlane.12",
				new string[2] { "渲染主权域 回读不符 ", "Render sovereignty domain: read-back mismatch " }
			},
			{
				"log.renderlane.13",
				new string[2] { " 已还原", " restored" }
			},
			{
				"log.renderlane.14",
				new string[2] { "渲染主权域 建立期间开关已关闭或已撤销 本次写入已回退", "Render sovereignty domain: switch turned off or revoked during setup, this write rolled back" }
			},
			{
				"log.renderlane.15",
				new string[2] { "渲染主权域已建立 ", "Render sovereignty domain established " }
			},
			{
				"log.renderlane.16",
				new string[2] { " 候选线程 ", " candidate thread " }
			},
			{
				"log.renderlane.17",
				new string[2] { " 占进程 CPU ", " takes process CPU " }
			},
			{
				"log.renderlane.18",
				new string[2] { " 线程 优先级 ", " threads, priority " }
			},
			{
				"log.renderlane.19",
				new string[2] { "渲染主权域已撤销 线程 ", "Render sovereignty domain revoked, thread " }
			},
			{
				"log.renderlane.20",
				new string[2] { " 优先级还原为 ", " priority restored to " }
			},
			{
				"log.renderlane.21",
				new string[2] { "渲染主权域 线程 ", "Render sovereignty domain: thread " }
			},
			{
				"log.renderlane.22",
				new string[2] { " 还原失败 记账保留待下次重试", " restore failed, ledger kept for next retry" }
			},
			{
				"log.renderlane.23",
				new string[2] { "渲染主权域 崩溃前的线程 ", "Render sovereignty domain: pre-crash thread " }
			},
			{
				"log.renderlane.24",
				new string[2] { " 优先级已还原为 ", " priority has been restored to " }
			},
			{
				"log.renderlane.25",
				new string[2] { "渲染主权域 复核发现已钉线程 ", "Render sovereignty domain: re-check found pinned thread " }
			},
			{
				"log.renderlane.26",
				new string[2] { " 不再主导 已解钉重新识别 当前主导线程 ", " no longer dominant; unpinned and re-identifying. Current dominant thread " }
			},
			{
				"log.renderlane.27",
				new string[2] { "渲染主权域 ", "Render sovereignty domain: " }
			},
			{
				"log.renderlane.28",
				new string[2] { " 反复错钉 本局不再介入", " was mis-pinned repeatedly; no further attempts this match" }
			},
			{
				"log.suppressioncoreapply.1",
				new string[2] { "进程 ", "Process " }
			},
			{
				"log.suppressioncoreapply.2",
				new string[2] { " 拒绝全部策略写入且状态未被改动 自保护驱动 按句柄受保护处理", " rejected all policy writes with no state changed — self-protecting driver, treated as handle-protected" }
			},
			{
				"log.suppressioncoreapply.3",
				new string[2] { " 已记入免压制名单 后续对局直接跳过", " added to the suppression-exempt list, skipped directly in future matches" }
			},
			{
				"log.suppressioncorerecovery.1",
				new string[2] { "压制恢复日志写入失败 已阻止新的进程状态修改", "Suppression recovery log write failed, new process state changes blocked" }
			},
			{
				"log.suppressioncorerecovery.2",
				new string[2] { "压制恢复日志读取失败", "Suppression recovery log read failed" }
			},
			{
				"log.suppressioncorerecovery.3",
				new string[2] { "压制恢复日志存在无法解析的记录 已原样保留 第 ", "Suppression recovery log contains an unparseable record, kept as-is, at line " }
			},
			{
				"log.suppressioncorerecovery.4",
				new string[2] { " 行", " of the log" }
			},
			{
				"log.suppressioncorerecovery.5",
				new string[2] { "压制崩溃恢复失败 pid ", "Suppression crash recovery failed, pid " }
			},
			{
				"t.suppressioncorerecovery.6",
				new string[2] { "压制恢复日志", "Suppression recovery log" }
			},
			{
				"log.selfyield.1",
				new string[2] { "Pavise 后台调度策略已应用", "Pavise background scheduling policy applied" }
			},
			{
				"log.selfyield.2",
				new string[2] { "Pavise 调度恢复操作已执行", "Pavise scheduling restoration executed" }
			},
			{
				"log.suppressioncore.1",
				new string[2] { "还原 ", "Restore " }
			},
			{
				"log.suppressioncore.2",
				new string[2] { " restore=pending status=protected snapshot=retained retry=scheduled", " restore=pending status=protected snapshot=retained retry=scheduled" }
			},
			{
				"log.suppressioncore.3",
				new string[2] { " restore=pending periodicRetry=stopped recoveryRecord=retained restartRetry=scheduled", " restore=pending periodicRetry=stopped recoveryRecord=retained restartRetry=scheduled" }
			},
			{
				"log.suppressioncore.4",
				new string[2] { "延迟恢复完成 ", "Deferred restoration completed " }
			},
			{
				"log.suppressioncore.5",
				new string[2] { "；历史恢复受限次数=", "; previous restoration-limited attempts=" }
			},
			{
				"log.suppressioncore.6",
				new string[2] { "后台策略 ", "Background policy " }
			},
			{
				"log.suppressioncore.7",
				new string[2] { " 检测到新建 GPU 上下文 纳入 GPU 调度让位", " new GPU context detected, added to GPU scheduling yield" }
			},
			{
				"log.suppressioncore.8",
				new string[2] { "后台策略核验已生效 ", "Background policy verified in effect " }
			},
			{
				"log.suppressioncore.9",
				new string[2] { " 此前写入未完全生效 ", " earlier write had not fully taken effect " }
			},
			{
				"log.suppressioncore.10",
				new string[2] { "后台策略重试已生效 ", "Background policy retry now in effect " }
			},
			{
				"log.suppressioncore.giveup.1",
				new string[3] { "反作弊压制 ", "Anti-cheat suppression ", "アンチチート抑制 " }
			},
			{
				"log.suppressioncore.giveup.2",
				new string[3] { " 写入被拒 本次运行不再尝试 失败环节 ", " write refused, not retried this run; failed step ", " 書き込み拒否 今回の実行では再試行しない 失敗箇所 " }
			},
			{
				"log.suppressioncore.giveup.3",
				new string[3] { " 已写入的项已退回原值", "; values already written were restored", " 書き込み済みの項目は元に戻した" }
			},
			{
				"log.suppressioncore.giveup.4",
				new string[3] { " rollback=pending exitRetry=scheduled", " rollback=pending exitRetry=scheduled", " rollback=pending exitRetry=scheduled" }
			},
			{
				"log.suppressioncore.11",
				new string[2] { " 按退避重试", " retrying with backoff" }
			},
			{
				"log.displayawake.1",
				new string[2] { "息屏防护 游戏期间不熄屏 不睡眠", "Keep-display-awake: no display off or sleep during the game" }
			},
			{
				"log.displayawake.2",
				new string[2] { "息屏防护已解除", "Keep-display-awake released" }
			},
			{
				"log.displayguard.7",
				new string[2] { "旧版刷新率守护残留已作废 当年为动态改动重启即失效 不再写回旧值 若当前刷新率不对请在系统显示设置调整一次", "Old refresh-rate-guardian residue discarded; the original change was dynamic and expired on reboot, the old value will not be written back. If the current refresh rate looks wrong, adjust it once in Windows display settings" }
			},
			{
				"log.dotweak.1",
				new string[3] { "DoSvc 暂停记录未能确认，已中止本轮服务暂停；未完成的恢复记录会保留", "The DoSvc pause record could not be verified. This pause attempt was aborted; pending recovery records are retained.", "DoSvc の一時停止記録を確認できなかったため、今回の停止処理を中断しました。未完了の復元記録は保持します。" }
			},
			{
				"log.dotweak.2",
				new string[2] { "后台下载已暂停 ", "Background downloads paused " }
			},
			{
				"log.dotweak.3",
				new string[2] { "传递优化带宽 1 KB/s", "Delivery Optimization bandwidth 1 KB/s" }
			},
			{
				"log.dotweak.4",
				new string[2] { "带宽策略写入失败", "Bandwidth policy write failed" }
			},
			{
				"log.dotweak.5",
				new string[2] { " DoSvc 已停止", " DoSvc stopped" }
			},
			{
				"log.dotweak.6",
				new string[2] { "后台下载策略写入失败且 DoSvc 未停止 本轮未启用", "Background download policy write failed and DoSvc not stopped; not enabled this round" }
			},
			{
				"log.dotweak.7",
				new string[3] { "DoSvc 恢复尚未确认，恢复记录已保留，请核实服务状态", "DoSvc recovery is not yet confirmed. Recovery records are retained; check the service state.", "DoSvc の復元をまだ確認できません。復元記録を保持しているため、サービスの状態を確認してください。" }
			},
			{
				"log.dotweak.8",
				new string[2] { "后台下载已还原", "Background downloads restored" }
			},
			{
				"log.dotweak.9",
				new string[2] { "本机已加域 传递优化策略键归组策略管理 跳过写入 仅暂停 DoSvc 服务", "This machine is domain-joined; the Delivery Optimization policy key is managed by Group Policy. Write skipped, only the DoSvc service is paused" }
			},
			{
				"log.pausesvc.stop",
				new string[2] { "已请求暂停非必要服务：{0}", "Requested pause of nonessential services: {0}" }
			},
			{
				"log.pausesvc.restore",
				new string[2] { "本次暂停的服务已恢复运行：{0}", "Services paused by Pavise are running again: {0}" }
			},
			{
				"log.pausesvc.pending",
				new string[2] { "非必要服务恢复尚未完成，保留记录等待重试：{0}", "Nonessential service recovery is incomplete; records kept for retry: {0}" }
			},
			{
				"log.pausesvc.unowned",
				new string[2] { "服务操作归属或结果未确认，保留记录且不自动启动：{0}", "Service operation ownership or outcome is unconfirmed; record retained without an automatic start: {0}" }
			},
			{
				"log.pausesvc.changed",
				new string[2] { "服务状态或配置已被外部改变，不再恢复：{0}", "The service state or configuration changed externally; it will not be restarted: {0}" }
			},
			{
				"log.pausesvc.ledger",
				new string[2] { "非必要服务恢复记录无法可靠读写，已停止新的暂停操作。", "Nonessential service recovery records cannot be read or written reliably; further pause attempts are blocked." }
			},
			{
				"log.pausesvc.boot",
				new string[2] { "系统已重新启动，已清除上次开机的服务暂停记录，未启动任何服务。", "Windows has restarted. Service pause records from the previous boot were cleared; no services were started." }
			},
			{
				"log.pausesvc.running",
				new string[2] { "停止请求未观察到生效，服务持续运行，无需恢复，未启动任何服务；正在结清暂停记录：{0}", "The stop request was never observed to take effect and the service kept running; nothing to restore and no service was started. Settling the pause record: {0}" }
			},
			{
				"log.pausesvc.unknownboot",
				new string[2] { "旧版服务暂停记录无法确认所属开机，已保留记录，未启动服务：{0}", "The boot session of legacy service pause records cannot be verified; records were retained without starting services: {0}" }
			},
			{
				"log.gamedvr.1",
				new string[2] { "Game DVR 后台录制与捕获已关闭", "Game DVR background recording and capture disabled" }
			},
			{
				"log.gamedvr.2",
				new string[2] { "Game DVR 已关闭 后台捕获项写入失败 下轮重试", "Game DVR disabled; background capture entry write failed, will retry next round" }
			},
			{
				"log.gamedvr.3",
				new string[2] { "Game DVR 写入或回读失败 本轮未关闭", "Game DVR write or read-back failed; not disabled this round" }
			},
			{
				"log.gamedvr.4",
				new string[2] { "Game DVR 设置已还原", "Game DVR settings restored" }
			},
			{
				"log.adlxapi.1",
				new string[2] { "ADLX 初始化失败 (ADLX_RESULT ", "ADLX initialization failed (ADLX_RESULT " }
			},
			{
				"log.adlxapi.2",
				new string[2] { ")，AMD 调优不可用", "), AMD tuning unavailable" }
			},
			{
				"log.adlxtweaks.1",
				new string[2] { "：快照无法持久化，本轮未启用", ": snapshot cannot be persisted, not enabled this round" }
			},
			{
				"log.adlxtweaks.11",
				new string[2] { "AMD 流体运动帧：本机驱动或 GPU 不支持", "AMD Fluid Motion Frames: not supported by this machine's driver or GPU" }
			},
			{
				"log.adlxtweaks.12",
				new string[2] { "AMD 流体运动帧：快照无法持久化，本轮未启用", "AMD Fluid Motion Frames: snapshot cannot be persisted, not enabled this round" }
			},
			{
				"log.adlxtweaks.13",
				new string[2] { "AMD 流体运动帧：写入失败", "AMD Fluid Motion Frames: write failed" }
			},
			{
				"log.adlxtweaks.14",
				new string[2] { "AMD 流体运动帧：已开启（驱动级插帧，退出恢复）", "AMD Fluid Motion Frames: enabled (driver-level frame generation, restored on exit)" }
			},
			{
				"log.adlxtweaks.15",
				new string[2] { "AMD 流体运动帧：写入报成功但回读不符", "AMD Fluid Motion Frames: write reported success but read-back mismatched" }
			},
			{
				"log.adlxtweaks.16",
				new string[2] { "AMD 流体运动帧还原失败，快照保留，下次启动继续尝试", "AMD Fluid Motion Frames restore failed; snapshot kept, will retry on next launch" }
			},
			{
				"log.adlxtweaks.17",
				new string[2] { "AMD 流体运动帧已还原", "AMD Fluid Motion Frames restored" }
			},
			{
				"log.adlxtweaks.18",
				new string[2] { "RSR 驱动级升格：本机驱动或 GPU 不支持", "RSR driver-level upscaling: not supported by this machine's driver or GPU" }
			},
			{
				"log.adlxtweaks.19",
				new string[2] { "RSR 驱动级升格：快照无法持久化，本轮未启用", "RSR driver-level upscaling: snapshot cannot be persisted, not enabled this round" }
			},
			{
				"log.adlxtweaks.20",
				new string[2] { "RSR 驱动级升格：写入失败", "RSR driver-level upscaling: write failed" }
			},
			{
				"log.adlxtweaks.21",
				new string[2] { "RSR 驱动级升格：已开启（需游戏内选低一档分辨率才生效，退出还原）", "RSR driver-level upscaling: enabled (only takes effect when a one-step-lower resolution is selected in game, restored on exit)" }
			},
			{
				"log.adlxtweaks.22",
				new string[2] { "RSR 驱动级升格还原失败，快照保留，下次启动继续尝试", "RSR driver-level upscaling restore failed; snapshot kept, will retry on next launch" }
			},
			{
				"log.adlxtweaks.23",
				new string[2] { "RSR 驱动级升格已还原", "RSR driver-level upscaling restored" }
			},
			{
				"log.adlxtweaks.24",
				new string[2] { "：写入或回读核验失败 (", ": write or read-back verification failed (" }
			},
			{
				"log.adlxtweaks.25",
				new string[2] { " 个 GPU)", " GPU(s))" }
			},
			{
				"log.adlxtweaks.26",
				new string[2] { "：本机 GPU 均不支持该功能", ": no GPU on this machine supports this feature" }
			},
			{
				"log.adlxtweaks.27",
				new string[2] { "：已开启（驱动级，退出恢复）", ": enabled (driver-level, restored on exit)" }
			},
			{
				"log.adlxtweaks.28",
				new string[2] { "：", ": " }
			},
			{
				"log.adlxtweaks.29",
				new string[2] { " 条快照对应的 GPU 已不在本机，丢弃", " snapshot entries reference GPUs no longer in this machine, discarded" }
			},
			{
				"log.adlxtweaks.30",
				new string[2] { " 还原失败，快照保留，下次启动继续尝试", " restore failed; snapshot kept, will retry on next launch" }
			},
			{
				"log.adlxtweaks.31",
				new string[2] { "AMD 着色器缓存重置：", "AMD shader cache reset: " }
			},
			{
				"log.adlxtweaks.32",
				new string[2] { " 个 GPU 已执行", " GPU(s) done" }
			},
			{
				"log.adlxtweaks.33",
				new string[2] { "检测到上次未还原的 AMD 驱动设置，已恢复", "Detected AMD driver settings not restored last time; recovered" }
			},
			{
				"log.adlxtweaks.34",
				new string[2] { "AMD 运行时暂不可用但显卡仍在本机，快照保留，本次清除中止", "AMD runtime temporarily unavailable but the GPU is still in this machine; snapshot kept, cleanup aborted this time" }
			},
			{
				"log.adlxtweaks.35",
				new string[2] { "AMD 运行时不可用，快照对应的驱动设置已无从访问，丢弃残留快照", "AMD runtime unavailable and the snapshotted driver settings are no longer reachable; discarding the stale snapshot" }
			},
			{
				"log.adlxtweaks.36",
				new string[2] { "AMD 对局功能跳过 游戏由其他厂商独显渲染 写 AMD 核显无效果", "AMD in-session features skipped: the game renders on another vendor's discrete GPU, writing to the AMD iGPU has no effect" }
			},
			{
				"rsr.warn",
				new string[2] { "开启前请确认：本项以画质换取帧率。\n\n需在游戏内将分辨率下调一档（如原生的 77%）才会生效；画面整体变软，细节可能闪烁，1080p 屏幕上尤为明显。竞技类游戏追求帧率时可以开启，画质优先的游戏不建议。\n\n确认开启？", "Before enabling, note that this trades image quality for frame rate.\n\nIt only takes effect if the in-game resolution is set one step below native (for example 77% of native); the picture softens overall and details may shimmer, especially on 1080p displays. Reasonable for competitive titles chasing frame rate, not recommended for visually rich games.\n\nEnable it?" }
			},
			{
				"set.amd.none",
				new string[2] { "未检测到 AMD 显卡驱动，本项停用", "No AMD GPU driver detected; this option is disabled" }
			},
			{
				"set.amdafmf",
				new string[2] { "AMD 流体运动帧 驱动插帧", "AMD Fluid Motion Frames driver frame generation" }
			},
			{
				"set.amdafmf.n",
				new string[2] { "驱动级插帧 AFMF，在两张真实画面之间推算一帧，帧率视觉上接近翻倍，不需要游戏支持。代价是输入延迟明显增加，实测竞技场景可能翻倍，对战游戏不要开启，适合单机大作追求观感。基础帧率低于 60 时瑕疵明显，建议配合可变刷新率屏幕。对 DX11、DX12、Vulkan 和 OpenGL 游戏生效。退出对局恢复原值。", "Driver-level frame generation (AFMF) interpolates one frame between two real frames, visually near-doubling the frame rate with no game support required. The cost is a clear increase in input latency — measured to potentially double in competitive scenarios. Do not enable it for PvP games; it suits big single-player titles that prioritize visual smoothness. Artifacts are obvious when the base frame rate is below 60; pairing with a variable-refresh-rate display is recommended. Works with DX11, DX12, Vulkan and OpenGL games. Restored to the original value when the match ends." }
			},
			{
				"set.amdalag",
				new string[2] { "AMD Anti-Lag 低延迟", "AMD Anti-Lag low latency" }
			},
			{
				"set.rsr",
				new string[2] { "RSR 驱动级升格", "RSR driver-level upscaling" }
			},
			{
				"set.rsr.n",
				new string[2] { "Radeon Super Resolution：游戏内选低一档分辨率渲染，驱动放大回原生分辨率，几乎全游戏通用。这是拿画质换帧率的交易，画面会整体变软，1080p 屏幕损失明显。对局开始时开启，退出还原。", "Radeon Super Resolution: pick a resolution one step below native in-game and the driver upscales it back to native — works in almost every game. This trades image quality for frame rate: the picture softens overall, and the loss is noticeable on 1080p displays. Enabled at match start, restored on exit." }
			},
			{
				"t.adlxtweaks.4",
				new string[2] { "锐化", "Sharpening" }
			},
			{
				"t.adlxtweaks.5",
				new string[2] { "AMD 锐化：RIS 已开启，强度 ", "AMD sharpening: RIS enabled, strength " }
			},
			{
				"t.adlxtweaks.8",
				new string[2] { "帧率上限", "Frame cap" }
			},
			{
				"t.legacypurge.19",
				new string[2] { "AMD 驱动调优", "AMD driver tuning" }
			},
			{
				"log.gpupowermax.7",
				new string[2] { " 原 ", " was " }
			},
			{
				"log.hagstweak.1",
				new string[2] { "GPU 硬件调度 HAGS 写入或回读失败 未标记为已开启", "GPU hardware scheduling HAGS write or read-back failed; not marked as enabled" }
			},
			{
				"log.hagstweak.2",
				new string[2] { "HAGS 状态标志无法持久化 已还原注册表修改", "HAGS state flag cannot be persisted; registry change reverted" }
			},
			{
				"log.hagstweak.3",
				new string[2] { "GPU 硬件调度 HAGS 已开启 重启后生效", "GPU hardware scheduling HAGS enabled, takes effect after reboot" }
			},
			{
				"log.hagstweak.4",
				new string[2] { "GPU 硬件调度 HAGS 已关闭 重启后生效", "GPU hardware scheduling HAGS disabled, takes effect after reboot" }
			},
			{
				"log.hagstweak.5",
				new string[2] { "本机 GPU 或驱动不支持硬件加速 GPU 调度 未写入", "This GPU or driver does not support hardware-accelerated GPU scheduling, nothing written" }
			},
			{
				"log.hagstweak.6",
				new string[2] { "GPU 硬件调度 HAGS 已还原为改动前的原值 重启后生效", "GPU hardware scheduling HAGS restored to its pre-change value, takes effect after reboot" }
			},
			{
				"log.kernelac.installed",
				new string[2] { "内核反作弊（本机已安装 {0}）", "kernel anti-cheat (installed on this machine: {0})" }
			},
			{
				"log.cfgofftweak.6",
				new string[2] { "已还原控制流保护策略 ", "Restored Control Flow Guard policy for " }
			},
			{
				"log.cfgofftweak.8",
				new string[2] { "关闭控制流保护 部分本体还原失败 登记保留待下次重试", "Disable CFG: some executables failed to restore, record kept for next retry" }
			},
			{
				"log.ifeoboost.8",
				new string[2] { "后备提优已撤销 ", "Fallback boost revoked " }
			},
			{
				"log.ifeoboost.9",
				new string[2] { "后备提优 部分 IFEO 还原失败 快照保留待下次重试", "Fallback boost: some IFEO entries failed to restore, snapshot kept for next retry" }
			},
			{
				"log.inputmythtweak.11",
				new string[2] { "键鼠队列长度 已还原原值", "Keyboard/mouse queue length: original values restored" }
			},
			{
				"log.inputmythtweak.12",
				new string[2] { "键鼠队列长度 还原失败 快照保留待下次重试", "Keyboard/mouse queue length: restore failed, snapshot kept for next retry" }
			},
			{
				"log.irqaffinityengine.1",
				new string[2] { "中断设置 ", "Interrupt setting " }
			},
			{
				"log.irqaffinityengine.2",
				new string[2] { " 未声明 MSI 支持 可能使用传统线中断", " does not declare MSI support, may be using legacy line-based interrupts" }
			},
			{
				"log.irqaffinityengine.3",
				new string[2] { " 的 MSI 被显式关闭 MSISupported=0", " has MSI explicitly disabled, MSISupported=0" }
			},
			{
				"log.irqaffinityengine.4",
				new string[2] { " 未找到可用设备 未作修改", " no usable devices found, no changes made" }
			},
			{
				"log.irqaffinityengine.8",
				new string[2] { " 设备名单无法持久化 已撤销本轮注册表修改", " device list cannot be persisted, this round's registry changes reverted" }
			},
			{
				"log.irqaffinityengine.9",
				new string[2] { "状态标志无法持久化 已还原注册表修改", "State flag cannot be persisted; registry change reverted" }
			},
			{
				"log.irqaffinityengine.10",
				new string[2] { " 已对 ", " wrote to " }
			},
			{
				"log.irqaffinityengine.11",
				new string[2] { " 个设备写入", " device(s) with " }
			},
			{
				"log.irqaffinityengine.12",
				new string[2] { "指定处理器策略 掩码 0x", "specified-processor policy, mask 0x" }
			},
			{
				"log.irqaffinityengine.14",
				new string[2] { " 需要重启该设备或重启电脑后生效", " takes effect after restarting the device or rebooting the PC" }
			},
			{
				"log.irqaffinityengine.22",
				new string[2] { " 已还原 ", " restored " }
			},
			{
				"log.irqaffinityengine.23",
				new string[2] { " 个设备 需要重启该设备或重启电脑后生效", " device(s), takes effect after restarting the device or rebooting the PC" }
			},
			{
				"log.irqaffinityengine.24",
				new string[2] { " 仍有 ", " still has " }
			},
			{
				"log.irqaffinityengine.25",
				new string[2] { " 个设备未能还原 已保留记录待下次重试", " device(s) not yet restored, records kept for next retry" }
			},
			{
				"log.mpotweak.1",
				new string[2] { "多平面叠加 MPO 设置已恢复 重启或重新登录后生效", "Multi-plane overlay MPO settings restored, takes effect after a reboot or re-login" }
			},
			{
				"log.msimodetweak.1",
				new string[2] { "MSI 修复 本机显卡的消息信号中断均未被关闭 无需改动", "MSI fix: message-signaled interrupts on this machine's GPUs are not disabled, no changes needed" }
			},
			{
				"log.msimodetweak.2",
				new string[2] { "MSI 修复 写入失败 ", "MSI fix: write failed " }
			},
			{
				"log.msimodetweak.3",
				new string[2] { "MSI 修复 清单无法持久化 已全部还原", "MSI fix: list cannot be persisted, all reverted" }
			},
			{
				"log.msimodetweak.4",
				new string[2] { "MSI 修复 已为 ", "MSI fix: for " }
			},
			{
				"log.msimodetweak.5",
				new string[2] { " 个显卡设备写回 MSISupported=1 重启后生效", " GPU device(s) wrote back MSISupported=1, takes effect after reboot" }
			},
			{
				"log.nettweak.1",
				new string[2] { "网络限流校正 当前值正常 无需改动", "Network throttling fix: current value is normal, no changes needed" }
			},
			{
				"log.nettweak.2",
				new string[2] { "网络限流校正 写入或回读失败 未改动", "Network throttling fix: write or read-back failed, unchanged" }
			},
			{
				"log.nettweak.3",
				new string[2] { "网络限流校正 已改回系统默认 ", "Network throttling fix: changed back to the system default " }
			},
			{
				"log.nettweak.4",
				new string[2] { " 重启后生效", " takes effect after reboot" }
			},
			{
				"log.nettweak.5",
				new string[2] { "网络限流校正 已还原为改动前的值", "Network throttling fix: restored to the pre-change value" }
			},
			{
				"log.networkaffinitytweak.1",
				new string[2] { "枚举网卡设备失败 ", "Failed to enumerate NIC devices " }
			},
			{
				"t.networkaffinitytweak.2",
				new string[2] { "网络优先级", "Network priority" }
			},
			{
				"log.networkaffinitytweak.3",
				new string[2] { "网络优先级 ", "Network priority " }
			},
			{
				"log.networkaffinitytweak.4",
				new string[2] { " 的 QoS 策略创建失败", " QoS policy creation failed" }
			},
			{
				"log.networkaffinitytweak.5",
				new string[2] { "网络优先级 策略名无法持久化 已撤回本轮创建的 QoS 策略", "Network priority: policy name cannot be persisted, QoS policies created this round withdrawn" }
			},
			{
				"log.notif.1",
				new string[2] { "通知弹窗已还原", "Notification popups restored" }
			},
			{
				"log.nvdrstweaks.1",
				new string[2] { "Smooth Motion 插帧 需要 RTX 40 或 50 系显卡 该项跳过", "Smooth Motion frame generation requires an RTX 40 or 50 series GPU; item skipped" }
			},
			{
				"log.nvdrstweaks.2",
				new string[2] { "Smooth Motion 插帧 需要 ", "Smooth Motion frame generation requires driver " }
			},
			{
				"log.nvdrstweaks.3",
				new string[2] { " 及以上驱动 本机 ", " or newer; this machine has " }
			},
			{
				"log.nvdrstweaks.4",
				new string[2] { " 该项跳过", " — item skipped" }
			},
			{
				"log.nvdrstweaks.5",
				new string[2] { "DLSS 覆写 本机显卡不是 RTX 卡 没有 DLSS 超分能力 该项跳过", "DLSS override: this machine's GPU is not an RTX card and has no DLSS upscaling capability; item skipped" }
			},
			{
				"log.nvdrstweaks.6",
				new string[2] { "DLSS 覆写 需要 566.14 及以上驱动 本机 ", "DLSS override requires driver 566.14 or newer; this machine has " }
			},
			{
				"t.nvdrstweaks.7",
				new string[2] { "未知版本", "unknown version" }
			},
			{
				"t.nvdrstweaks.23",
				new string[2] { "DLSS 覆写为最新", "DLSS override to latest" }
			},
			{
				"t.nvdrstweaks.24",
				new string[2] { "DLSS 覆写为预设 J", "DLSS override to preset J" }
			},
			{
				"t.nvdrstweaks.25",
				new string[2] { "DLSS 覆写为预设 K", "DLSS override to preset K" }
			},
			{
				"t.nvdrstweaks.26",
				new string[2] { "DLSS 覆写", "DLSS override" }
			},
			{
				"log.nvdrstweaks.34",
				new string[2] { "NVIDIA 显卡已不在本机 DRS 配置随驱动库一同消失 已作废 ", "The NVIDIA GPU is no longer in this machine and its DRS profiles went with the driver store; discarded " }
			},
			{
				"log.nvdrstweaks.35",
				new string[2] { " 个游戏的驱动调优快照", " games' driver tuning snapshots" }
			},
			{
				"log.nvdrstweaks.48",
				new string[2] { "NVIDIA 驱动调优 本代驱动已移除超低延迟键 已降级为低延迟 预渲染1 更换驱动版本后自动重试", "NVIDIA driver tuning: this driver no longer has the Ultra Low Latency keys; downgraded to low latency with pre-render 1. A driver change retries automatically." }
			},
			{
				"log.timerticktweak.1",
				new string[2] { "计时器节拍 读取启动配置失败 未做改动", "Timer tick: failed to read the boot configuration, nothing changed" }
			},
			{
				"log.timerticktweak.2",
				new string[2] { "计时器节拍 快照无法持久化 未做改动", "Timer tick: snapshot could not be persisted, nothing changed" }
			},
			{
				"log.timerticktweak.3",
				new string[2] { "计时器节拍 写入失败 ", "Timer tick: write failed " }
			},
			{
				"log.timerticktweak.4",
				new string[2] { "计时器节拍 三项已写入 重启后生效", "Timer tick: all three values written, takes effect after reboot" }
			},
			{
				"log.timerticktweak.5",
				new string[2] { "计时器节拍 已还原 重启后生效", "Timer tick: restored, takes effect after reboot" }
			},
			{
				"log.timerticktweak.6",
				new string[2] { "计时器节拍 部分项未能还原 快照保留可重试", "Timer tick: some values could not be restored; snapshot kept for retry" }
			},
			{
				"log.gtimer.1",
				new string[2] { "全局计时器 注册表写入失败 未做改动", "Global timer: registry write failed, nothing changed" }
			},
			{
				"log.gtimer.2",
				new string[2] { "全局计时器 已写入 重启后生效 对局中将代为请求高精度计时", "Global timer: written, takes effect after reboot; high-precision timing will be requested during matches" }
			},
			{
				"log.gtimer.3",
				new string[2] { "全局计时器 还原失败 快照保留可重试", "Global timer: restore failed; snapshot kept for retry" }
			},
			{
				"log.gtimer.4",
				new string[2] { "全局计时器 已还原 重启后生效", "Global timer: restored, takes effect after reboot" }
			},
			{
				"log.gamemode.favored",
				new string[2] { "调度评级最高的核 ", "Highest scheduling class cores " }
			},
			{
				"log.gamemode.favored.detail",
				new string[2] { " 各核 能效档/调度评级 ", " per core efficiency/scheduling class " }
			},
			{
				"t.bypassio.1",
				new string[2] { "DirectStorage 直通", "DirectStorage BypassIO" }
			},
			{
				"t.bypassio.2",
				new string[2] { "无阻断", "No blockers" }
			},
			{
				"t.bypassio.3",
				new string[2] { "被 {0} 阻断", "Blocked by {0}" }
			},
			{
				"t.bypassio.4",
				new string[2] { "该卷上游戏可用直通读取，资源流式加载走最快路径。", "Games on this volume can use bypass reads; streamed assets take the fastest path." }
			},
			{
				"t.bypassio.5",
				new string[2] { "过滤驱动阻止 BypassIO，DirectStorage 退回慢路径。原因：", "A filter driver blocks BypassIO, forcing DirectStorage onto the slow path. Reason:" }
			},
			{
				"t.memchannel.1",
				new string[2] { "内存通道", "Memory channels" }
			},
			{
				"t.memchannel.2",
				new string[2] { "单条 单通道", "single module, single channel" }
			},
			{
				"t.memchannel.3",
				new string[2] { "对称 双通道", "matched, dual channel" }
			},
			{
				"t.memchannel.4",
				new string[2] { "非对称 {0}GB 双通道 + {1}GB 单通道混合区", "asymmetric: {0} GB dual-channel + {1} GB single-channel flex zone" }
			},
			{
				"t.memchannel.5",
				new string[2] { "非对称混合", "asymmetric mix" }
			},
			{
				"t.memchannel.6",
				new string[2] { "单通道内存带宽减半，对核显和高帧场景影响明显。加装一条同容量内存能组成双通道。", "Single-channel memory halves bandwidth, which hurts iGPUs and high-FPS scenarios noticeably. Adding a matching module would enable dual channel." }
			},
			{
				"t.memchannel.7",
				new string[2] { "混合区数据仅有单通道带宽；这是硬件布局说明，不代表建议换内存。", "Flex-zone data has single-channel bandwidth. This describes the hardware layout, not a memory-upgrade recommendation." }
			},
			{
				"irqpin.prio.name",
				new string[2] { "设备中断优先级", "Device interrupt priority" }
			},
			{
				"log.gpuprefstage.1",
				new string[2] { "独显预选 已为 ", "GPU pre-select: wrote the high-performance preference for " }
			},
			{
				"log.gpuprefstage.2",
				new string[2] { " 预写高性能显卡偏好 游戏启动时即用独显 退场还原", " so this launch uses the discrete GPU; restored when the session ends" }
			},
			{
				"log.gpuprefstage.3",
				new string[2] { "独显预选 已还原 ", "GPU pre-select: restored " }
			},
			{
				"log.gpuprefstage.4",
				new string[2] { "独显预选 还原失败 台账保留待下次重试", "GPU pre-select: restore failed; the ledger is kept for a later retry" }
			},
			{
				"log.gpuprefstage.5",
				new string[2] { "独显预选 崩溃前的显卡偏好已还原", "GPU pre-select: the pre-crash GPU preference has been restored" }
			},
			{
				"log.gpuprefstage.6",
				new string[2] { "独显预选 整体重置放弃无法认领的记录 ", "GPU pre-select: full reset abandoned an unclaimable record for " }
			},
			{
				"log.gpuprefstage.7",
				new string[2] { " 该程序的显卡偏好维持现值 可在 Windows 图形设置中调整", "; its GPU preference keeps the current value and can be changed in Windows graphics settings" }
			},
			{
				"set.gpupref",
				new string[2] { "待命预选高性能显卡", "Pre-select the high-performance GPU on standby" }
			},
			{
				"set.gpupref.n",
				new string[2] { "双显卡机器待命时预选高性能 GPU；已有手动设置则跳过，退场还原。", "Preselects the high-performance GPU on dual-GPU systems; skips manual choices and restores on exit." }
			},
			{
				"set.gpupref.single",
				new string[2] { "本机只有一块显卡，没有可选的落点，此项不适用。", "This machine has a single GPU, so there is nothing to choose between; this option does not apply." }
			},
			{
				"log.poweroverlay.2",
				new string[2] { "电源滑块 快照无法持久化 本轮未切换", "Power slider: snapshot could not be persisted, not switched this round" }
			},
			{
				"log.poweroverlay.3",
				new string[2] { "电源滑块 切最佳性能失败 状态 ", "Power slider: switch to Best Performance failed, status " }
			},
			{
				"log.poweroverlay.4",
				new string[2] { "电源滑块 已切到最佳性能 退出电竞档还原", "Power slider switched to Best Performance, restores on leaving the Esports tier" }
			},
			{
				"log.poweroverlay.6",
				new string[2] { "电源滑块已还原", "Power slider restored" }
			},
			{
				"log.poweroverlay.7",
				new string[2] { "检测到上次未还原的电源滑块设置 已恢复", "Detected a power slider setting left unrestored from last time, now recovered" }
			},
			{
				"log.poweroverlay.external",
				new string[2] { "电源滑块已由其他软件或用户更改，保留当前选择", "Power slider changed externally; keeping the current selection" }
			},
			{
				"log.powerplancorepark.3",
				new string[2] { "核心停泊还原写回失败 快照保留待下次重试", "Core parking restore write-back failed, snapshot kept for next retry" }
			},
			{
				"log.powerplancorepark.4",
				new string[2] { "核心停泊已还原电源方案原值", "Core parking restored to power plan original values" }
			},
			{
				"log.powerplancorepark.8",
				new string[2] { "旧版核心停泊多次还原失败，原值记录已保留，等待后续重试。", "Legacy core-parking restoration failed repeatedly. Original values are retained for a later retry." }
			},
			{
				"t.powerplanschemes.1",
				new string[2] { "最小处理器状态", "Minimum processor state" }
			},
			{
				"t.powerplanschemes.2",
				new string[2] { "最大处理器状态", "Maximum processor state" }
			},
			{
				"t.powerplanschemes.3",
				new string[2] { "核心停放最小核心数", "Core parking minimum cores" }
			},
			{
				"t.powerplanschemes.4",
				new string[2] { "核心停放最大核心数", "Core parking maximum cores" }
			},
			{
				"t.powerplanschemes.5",
				new string[2] { "睿频模式", "Turbo boost mode" }
			},
			{
				"t.powerplanschemes.6",
				new string[2] { "允许节流状态", "Allow throttle states" }
			},
			{
				"t.powerplanschemes.7",
				new string[2] { "系统散热方式", "System cooling policy" }
			},
			{
				"t.powerplanschemes.8",
				new string[2] { "PCIe 链接电源管理", "PCIe link power management" }
			},
			{
				"t.powerplanschemes.9",
				new string[2] { "USB 选择性暂停", "USB selective suspend" }
			},
			{
				"t.powerplanschemes.10",
				new string[2] { "关闭硬盘时间", "Hard disk off timeout" }
			},
			{
				"t.powerplanschemes.11",
				new string[2] { "电源计划类型", "Power plan type" }
			},
			{
				"t.powerplanschemes.12",
				new string[2] { "能源性能首选项", "Energy performance preference" }
			},
			{
				"t.powerplanschemes.13",
				new string[2] { "睿频策略", "Turbo boost policy" }
			},
			{
				"t.powerplanschemes.14",
				new string[2] { "升频策略", "Performance increase policy" }
			},
			{
				"t.powerplanschemes.15",
				new string[2] { "降频策略", "Performance decrease policy" }
			},
			{
				"t.powerplanschemes.16",
				new string[2] { "升频时间", "Performance increase time" }
			},
			{
				"t.powerplanschemes.17",
				new string[2] { "降频时间", "Performance decrease time" }
			},
			{
				"t.powerplanschemes.18",
				new string[2] { "升频阈值", "Performance increase threshold" }
			},
			{
				"t.powerplanschemes.19",
				new string[2] { "降频阈值", "Performance decrease threshold" }
			},
			{
				"t.powerplanschemes.20",
				new string[2] { "延迟敏感性能", "Latency sensitivity performance" }
			},
			{
				"t.powerplanschemes.21",
				new string[2] { "延迟敏感解除停放", "Latency sensitivity unparking" }
			},
			{
				"t.powerplanschemes.22",
				new string[2] { "处理器忙闲度", "Processor duty cycling" }
			},
			{
				"t.powerplanschemes.23",
				new string[2] { "处理器最大频率", "Maximum processor frequency" }
			},
			{
				"t.powerplanschemes.24",
				new string[2] { "无线适配器节能", "Wireless adapter power saving" }
			},
			{
				"t.powerplanschemes.25",
				new string[2] { "E核最小处理器状态", "E-core minimum processor state" }
			},
			{
				"t.powerplanschemes.26",
				new string[2] { "E核最大处理器状态", "E-core maximum processor state" }
			},
			{
				"t.powerplanschemes.27",
				new string[2] { "E核停放最小核心数", "E-core parking minimum cores" }
			},
			{
				"t.powerplanschemes.28",
				new string[2] { "E核停放最大核心数", "E-core parking maximum cores" }
			},
			{
				"t.powerplanschemes.29",
				new string[2] { "E核能源性能首选项", "E-core energy performance preference" }
			},
			{
				"t.powerplanschemes.30",
				new string[2] { "异类线程调度策略", "Heterogeneous thread scheduling policy" }
			},
			{
				"t.powerplanschemes.31",
				new string[2] { "异类短线程调度策略", "Heterogeneous short-thread scheduling policy" }
			},
			{
				"log.powerplanschemes.32",
				new string[2] { "电源项 ", "Power setting " }
			},
			{
				"log.powerplanschemes.33",
				new string[2] { " 写入失败", " write failed" }
			},
			{
				"t.powerplanschemes.35",
				new string[2] { "NVMe 主盘电源态延迟容忍", "Primary NVMe latency tolerance" }
			},
			{
				"t.powerplanschemes.36",
				new string[2] { "NVMe 从盘电源态延迟容忍", "Secondary NVMe latency tolerance" }
			},
			{
				"t.powerplanschemes.37",
				new string[2] { "AHCI 链路电源管理", "AHCI link power management" }
			},
			{
				"log.powerplanschemes.extremeOrphan",
				new string[3] { "空闲策略收据认的方案已不在本机 收据作废", "The plan named by the idle-policy receipt no longer exists on this machine; the receipt is dropped.", "アイドルポリシーの記録が指すプランが本機に存在しません。記録を破棄しました。" }
			},
			{
				"log.powerplanschemes.extremePending",
				new string[3] { "空闲策略原值尚未全部写回 保留恢复记录 其余参数照常配置 下次配置时重试", "Some idle-policy values have not been written back yet. The recovery record is retained; other parameters proceed, and the write-back is retried on the next configuration.", "アイドルポリシーの元の値の一部がまだ書き戻されていません。復元記録を保持し、他のパラメーターは設定を続け、次回の設定時に再試行します。" }
			},
			{
				"t.powerplanschemes.43",
				new string[2] { "USB 3 链路电源管理", "USB 3 link power management" }
			},
			{
				"t.powerplanschemes.44",
				new string[2] { "Intel 核显电源计划", "Intel graphics power plan" }
			},
			{
				"t.powerplanschemes.45",
				new string[2] { "E核睿频策略", "E-core turbo boost policy" }
			},
			{
				"t.powerplanschemes.46",
				new string[2] { "E核升频策略", "E-core performance increase policy" }
			},
			{
				"t.powerplanschemes.47",
				new string[2] { "E核降频策略", "E-core performance decrease policy" }
			},
			{
				"t.powerplanschemes.48",
				new string[2] { "E核升频时间", "E-core performance increase time" }
			},
			{
				"t.powerplanschemes.49",
				new string[2] { "E核降频时间", "E-core performance decrease time" }
			},
			{
				"t.powerplanschemes.50",
				new string[2] { "E核升频阈值", "E-core performance increase threshold" }
			},
			{
				"t.powerplanschemes.51",
				new string[2] { "E核降频阈值", "E-core performance decrease threshold" }
			},
			{
				"t.powerplanschemes.52",
				new string[2] { "E核延迟敏感性能", "E-core latency sensitivity performance" }
			},
			{
				"t.powerplanschemes.53",
				new string[2] { "E核延迟敏感解除停放", "E-core latency sensitivity unparking" }
			},
			{
				"t.powerplanschemes.54",
				new string[2] { "切换显卡全局策略", "Switchable graphics global policy" }
			},
			{
				"log.powerplanschemes.35",
				new string[2] { "托管电源方案有 ", "Managed power plan has " }
			},
			{
				"log.powerplanschemes.36",
				new string[2] { " 项未能写入 未把本轮标记为成功", " settings that failed to write, this round not marked as successful" }
			},
			{
				"log.powerplanschemes.37",
				new string[2] { "托管电源方案 ", "Managed power plan " }
			},
			{
				"log.powerplanschemes.38",
				new string[2] { "电竞档", "Esports tier" }
			},
			{
				"log.powerplanschemes.39",
				new string[2] { "智能档", "Smart tier" }
			},
			{
				"log.powerplanschemes.46",
				new string[2] { "掌机档", "Handheld tier" }
			},
			{
				"log.powerplanschemes.40",
				new string[2] { " 按 ", " per " }
			},
			{
				"log.powerplanschemes.41",
				new string[2] { " 写入 ", " wrote " }
			},
			{
				"log.powerplanschemes.42",
				new string[2] { " 核心停放保留给AMD驱动", " core parking left to the AMD driver" }
			},
			{
				"log.powerplanschemes.43",
				new string[2] { " 本机不支持 ", " this machine does not support " }
			},
			{
				"log.powerplanschemes.44",
				new string[2] { " 项 ", " settings " }
			},
			{
				"t.powerplan.1",
				new string[2] { "卓越性能", "Ultimate Performance" }
			},
			{
				"t.powerplan.2",
				new string[2] { "由软件调度 ", "Scheduled by Pavise " }
			},
			{
				"t.powerplan.3",
				new string[2] { "高性能", "High Performance" }
			},
			{
				"t.powerplan.4",
				new string[2] { "平衡", "Balanced" }
			},
			{
				"t.powerplan.5",
				new string[2] { "节能", "Power Saver" }
			},
			{
				"t.powerplan.6",
				new string[2] { "自定义计划", "Custom plan" }
			},
			{
				"log.powerplan.7",
				new string[2] { "POWER selection=managed plan=", "POWER selection=managed plan=" }
			},
			{
				"log.powerplan.8",
				new string[2] { " event=selection-saved routing=processor-specific", " event=selection-saved routing=processor-specific" }
			},
			{
				"log.powerplan.9",
				new string[2] { "POWER event=selection-saved selection=managed plan=", "POWER event=selection-saved selection=managed plan=" }
			},
			{
				"log.powerplan.10",
				new string[2] { "POWER selection=existing plan=", "POWER selection=existing plan=" }
			},
			{
				"log.powerplan.11",
				new string[2] { " event=selection-saved parameterWrite=disabled", " event=selection-saved parameterWrite=disabled" }
			},
			{
				"log.powerplan.15",
				new string[2] { "POWER event=managed-plan-deleted plan=", "POWER event=managed-plan-deleted plan=" }
			},
			{
				"log.powerplan.16",
				new string[2] { "本机没有卓越性能电源计划 已创建一份 ", "This machine has no Ultimate Performance power plan, created one " }
			},
			{
				"log.powerplan.17",
				new string[2] { " 各项保持系统模板原样 Pavise 不改写", " with settings kept as the system template, Pavise does not rewrite them" }
			},
			{
				"log.powerplan.18",
				new string[2] { "本机既无卓越性能也无法创建 电源计划切换本轮无目标", "This machine has no Ultimate Performance plan and one could not be created, power plan switch has no target this round" }
			},
			{
				"log.powerplan.19",
				new string[2] { "POWER event=owned-system-plan-deleted source=ultimate-performance", "POWER event=owned-system-plan-deleted source=ultimate-performance" }
			},
			{
				"log.powerplan.23",
				new string[2] { "POWER selection=existing plan=", "POWER selection=existing plan=" }
			},
			{
				"log.powerplan.24",
				new string[2] { " event=target-resolved parameterWrite=disabled", " event=target-resolved parameterWrite=disabled" }
			},
			{
				"log.powerplan.25",
				new string[2] { "POWER event=activation-cancelled reason=snapshot-persistence-failed", "POWER event=activation-cancelled reason=snapshot-persistence-failed" }
			},
			{
				"log.powerplan.26",
				new string[2] { "POWER event=activation-verified plan=", "POWER event=activation-verified plan=" }
			},
			{
				"log.powerplan.27",
				new string[2] { "POWER event=activation-failed", "POWER event=activation-failed" }
			},
			{
				"log.powerplan.28",
				new string[2] { "POWER event=reactivation-verified target=", "POWER event=reactivation-verified target=" }
			},
			{
				"log.powerplan.29",
				new string[2] { "POWER event=restore-complete", "POWER event=restore-complete" }
			},
			{
				"log.powerplan.30",
				new string[2] { "POWER event=restore-failed reason=original-plan-missing", "POWER event=restore-failed reason=original-plan-missing" }
			},
			{
				"log.powerplan.31",
				new string[2] { "POWER event=restore-failed snapshot=retained retry=next-start", "POWER event=restore-failed snapshot=retained retry=next-start" }
			},
			{
				"log.powerplan.32",
				new string[2] { "POWER event=orphan-plan-recovered target=system", "POWER event=orphan-plan-recovered target=system" }
			},
			{
				"log.powerplan.36",
				new string[2] { "POWER event=restore-skipped reason=external-change activePlan=", "POWER event=restore-skipped reason=external-change activePlan=" }
			},
			{
				"t.powerplanprofile.1",
				new string[2] { "Intel大小核", "Intel hybrid cores" }
			},
			{
				"t.powerplanprofile.2",
				new string[2] { "AMD密集核", "AMD dense cores" }
			},
			{
				"t.powerplanprofile.3",
				new string[2] { "X3D大缓存", "X3D large cache" }
			},
			{
				"t.powerplanprofile.4",
				new string[2] { "双CCD", "Dual CCD" }
			},
			{
				"log.presenceqos.1",
				new string[2] { "无输入降级已关闭", "No-input throttling disabled" }
			},
			{
				"log.presenceqos.2",
				new string[2] { "无输入降级开关写入或回读失败 本轮未启用", "No-input throttling switch write or read-back failed, not enabled this round" }
			},
			{
				"log.presenceqos.3",
				new string[2] { "无输入降级开关已还原", "No-input throttling switch restored" }
			},
			{
				"log.quantumtweak.7",
				new string[2] { "时间片校正 已还原为改动前的值", "Time slice correction: restored to the pre-change value" }
			},
			{
				"log.reversiblereg.1",
				new string[2] { " 原值类型异常 ", " original value has unexpected type " }
			},
			{
				"log.reversiblereg.2",
				new string[2] { " 跳过此项调整", " skipping this adjustment" }
			},
			{
				"log.reversiblereg.3",
				new string[2] { "无法持久化 ", "Could not persist " }
			},
			{
				"log.reversiblereg.4",
				new string[2] { " 原值快照 已取消写入", " original-value snapshot, write cancelled" }
			},
			{
				"log.reversiblereg.5",
				new string[2] { "注册表快照损坏 放弃还原 ", "Registry snapshot corrupted, restore abandoned " }
			},
			{
				"log.reversiblereg.6",
				new string[2] { " 二进制快照格式不符", " binary snapshot format mismatch" }
			},
			{
				"log.reversiblereg.7",
				new string[2] { " 二进制快照解码失败", " binary snapshot decode failed" }
			},
			{
				"log.reversiblereg.8",
				new string[2] { " 记录值 \"", " recorded value \"" }
			},
			{
				"log.reversiblereg.9",
				new string[2] { " 当前值已被其它程序改过 尊重现值 跳过还原并清除快照", " current value was changed by another program, respecting it, skipping restore and clearing the snapshot" }
			},
			{
				"log.reversiblereg.10",
				new string[2] { " 后回读不一致 快照保留待下次重试", " read-back mismatch afterwards, snapshot kept for next retry" }
			},
			{
				"log.reversiblereg.11",
				new string[2] { " 失败 通常是权限不足 快照保留待下次重试", " failed, most likely insufficient permissions, snapshot kept for next retry" }
			},
			{
				"log.svcpause.1",
				new string[3] { "服务暂停记录未能确认，已中止本轮服务暂停；未完成的恢复记录会保留", "The service pause record could not be verified. This pause attempt was aborted; pending recovery records are retained.", "サービスの一時停止記録を確認できなかったため、今回の停止処理を中断しました。未完了の復元記録は保持します。" }
			},
			{
				"log.svcpause.2",
				new string[2] { "已暂停索引和预取服务 ", "Paused indexing and prefetch services " }
			},
			{
				"log.svcpause.3",
				new string[2] { "已请求停止索引和预取服务 尚未确认停止 ", "Requested stop of indexing and prefetch services, stop not yet confirmed " }
			},
			{
				"log.svcpause.4",
				new string[2] { "索引和预取服务已恢复", "Indexing and prefetch services resumed" }
			},
			{
				"log.svcpause.5",
				new string[3] { "部分服务恢复尚未确认：", "Recovery is not yet confirmed for some services: ", "一部のサービスの復元をまだ確認できません：" }
			},
			{
				"log.svcpause.6",
				new string[3] { "；恢复记录会保留，请核实服务状态", "; recovery records are retained; check the service state", "。復元記録を保持しているため、サービスの状態を確認してください。" }
			},
			{
				"log.updatepause.1",
				new string[3] { "更新服务暂停记录未能确认，已中止本轮服务暂停；未完成的恢复记录会保留", "The update service pause record could not be verified. This pause attempt was aborted; pending recovery records are retained.", "更新サービスの一時停止記録を確認できなかったため、今回の停止処理を中断しました。未完了の復元記録は保持します。" }
			},
			{
				"log.updatepause.2",
				new string[2] { "已暂停 Windows 更新服务 ", "Paused Windows Update services " }
			},
			{
				"log.updatepause.3",
				new string[2] { "Windows 更新服务已恢复", "Windows Update services resumed" }
			},
			{
				"log.updatepause.4",
				new string[3] { "部分更新服务恢复尚未确认：", "Recovery is not yet confirmed for some update services: ", "一部の更新サービスの復元をまだ確認できません：" }
			},
			{
				"log.vbstweak.1",
				new string[2] { "无法读取 hypervisorlaunchtype 原状态 已取消修改", "Could not read the original hypervisorlaunchtype state, change cancelled" }
			},
			{
				"log.vbstweak.2",
				new string[2] { "关闭 VBS 和内存完整性写入或回读失败 已回滚", "Disabling VBS and Memory Integrity: write or read-back failed, rolled back" }
			},
			{
				"log.vbstweak.3",
				new string[2] { "停用 hypervisor 失败 bcdedit rc ", "Disabling hypervisor failed, bcdedit rc " }
			},
			{
				"log.vbstweak.4",
				new string[2] { " 已回滚注册表改动", " registry changes rolled back" }
			},
			{
				"log.vbstweak.5",
				new string[2] { "无法持久化 VBS 修改状态 已回滚本轮设置", "Could not persist VBS change state, this round's settings rolled back" }
			},
			{
				"log.vbstweak.6",
				new string[2] { "VBS 和内存完整性已关 hypervisor 已停用 重启后生效 WSL2 Docker Hyper-V 将失效", "VBS and Memory Integrity disabled, hypervisor off, takes effect after reboot. WSL2, Docker, and Hyper-V will stop working" }
			},
			{
				"log.vbstweak.7",
				new string[2] { "关闭 VBS 和 hypervisor 失败 ", "Disabling VBS and hypervisor failed " }
			},
			{
				"log.vbstweak.8",
				new string[2] { "系统设置已还原 但状态标志写入失败 下次启动将再次校正", "System settings restored, but the state flag write failed, will correct again at next startup" }
			},
			{
				"log.vbstweak.9",
				new string[2] { "已还原 VBS 和内存完整性 + hypervisorlaunchtype ", "Restored VBS and Memory Integrity + hypervisorlaunchtype " }
			},
			{
				"log.vbstweak.10",
				new string[2] { "已还原 VBS 和内存完整性 hypervisor 未经 Pavise 修改 未触碰", "Restored VBS and Memory Integrity, hypervisor was not modified by Pavise, untouched" }
			},
			{
				"log.vbstweak.11",
				new string[2] { "还原 VBS 和 hypervisor 未完全成功 bcdedit rc ", "Restoring VBS and hypervisor did not fully succeed, bcdedit rc " }
			},
			{
				"log.vbstweak.12",
				new string[2] { " 快照已保留，可重试一次", " snapshot preserved; one retry is possible" }
			},
			{
				"log.vbstweak.13",
				new string[2] { "还原 VBS 和 hypervisor 失败 ", "Restoring VBS and hypervisor failed " }
			},
			{
				"log.vbstweak.14",
				new string[2] { "bcdedit 超过 15 秒未返回 已终止 ", "bcdedit did not return within 15 seconds, terminated " }
			},
			{
				"log.fso.1",
				new string[2] { "已禁用全屏优化", "Fullscreen optimizations disabled for" }
			},
			{
				"log.fso.2",
				new string[2] { "已恢复全屏优化", "Fullscreen optimizations restored for" }
			},
			{
				"log.fso.3",
				new string[2] { "全屏优化设置回读核验失败", "Fullscreen optimizations write verification failed for" }
			},
			{
				"log.atomicfile.1",
				new string[2] { "写入失败", "Write failed" }
			},
			{
				"t.cputopology.1",
				new string[3] { "无", "None", "なし" }
			},
			{
				"t.cputopology.2",
				new string[3] { " 个 核 ", " cores: ", " コア: " }
			},
			{
				"t.etwprocesswatcher.1",
				new string[2] { "StartTrace 失败 rc ", "StartTrace failed rc " }
			},
			{
				"t.etwprocesswatcher.2",
				new string[2] { "EnableTraceEx2 失败 rc ", "EnableTraceEx2 failed rc " }
			},
			{
				"t.etwprocesswatcher.3",
				new string[2] { "OpenTrace 失败 win32 ", "OpenTrace failed win32 " }
			},
			{
				"t.gpuinventory.1",
				new string[2] { "核显", "iGPU" }
			},
			{
				"t.gpuinventory.2",
				new string[2] { "独显", "dGPU" }
			},
			{
				"log.gpuinventory.3",
				new string[2] { "在场显卡枚举失败 ", "Present GPU enumeration failed " }
			},
			{
				"log.gpuinventory.4",
				new string[2] { "显卡枚举 跳过 ", "GPU enumeration: skipped " }
			},
			{
				"log.gpuinventory.5",
				new string[2] { " 块已经不在机器上的卡 驱动类键留着历史记录 拔掉不会自己删", " card(s) no longer in this machine, the driver class keys keep historical entries and are not removed on unplug" }
			},
			{
				"log.gpuinventory.6",
				new string[2] { "显卡枚举 DXCore 未报告核显独显属性 退回总线号与显存启发式判断 首个受影响适配器 ", "GPU enumeration: DXCore did not report integrated/discrete, falling back to bus-number and VRAM heuristics; first affected adapter " }
			},
			{
				"log.dxcoregpuprobe.1",
				new string[2] { "DXCore 显卡探测超时 放弃驱动报告 使用备用判定", "DXCore GPU probe timed out, dropping the driver report and using the fallback heuristic" }
			},
			{
				"log.presentdevices.1",
				new string[2] { "设备父链读取失败 ", "Device parent chain read failed " }
			},
			{
				"log.presentdevices.2",
				new string[2] { "在场设备枚举失败 ", "Present device enumeration failed " }
			},
			{
				"log.procnotify.1",
				new string[2] { "进程事件订阅失败 退回轮询 ", "Process event subscription failed, falling back to polling " }
			},
			{
				"log.procnotify.2",
				new string[2] { "进程发现快速通道已启用 游戏相关进程免除送达与合并等待", "Process discovery fast path enabled; game-related processes skip delivery and merge waits" }
			},
			{
				"log.procnotify.3",
				new string[2] { "进程发现快速通道不可用 沿用常规路径 ", "Process discovery fast path unavailable, staying on the regular path " }
			},
			{
				"log.psrunner.1",
				new string[2] { " PowerShell 执行超时 ", " PowerShell execution timed out " }
			},
			{
				"log.psrunner.2",
				new string[2] { " PowerShell 执行失败 exit ", " PowerShell execution failed, exit " }
			},
			{
				"log.psrunner.3",
				new string[2] { " 无法执行 PowerShell ", " could not run PowerShell " }
			},
			{
				"t.settings.1",
				new string[2] { "注册表配置键无法创建", "Could not create the registry settings key" }
			},
			{
				"log.settings.2",
				new string[2] { "设置写入失败 ", "Settings write failed " }
			},
			{
				"log.taskhelper.1",
				new string[2] { "开机自启任务读取失败 本次不修改", "Startup task read failed, not modified this time" }
			},
			{
				"log.taskhelper.2",
				new string[2] { "开机自启任务迁移已跳过 当前程序位于临时或易失目录 ", "Startup task migration skipped, the program is in a temporary or volatile directory " }
			},
			{
				"log.taskhelper.3",
				new string[2] { " 文件可能被清理导致自启失效 请把程序移到固定目录后再启动", " files there may be cleaned up and break autostart, move the program to a permanent directory and launch again" }
			},
			{
				"t.taskhelper.4",
				new string[2] { "开机自启任务迁移 ", "Startup task migration " }
			},
			{
				"t.taskhelper.5",
				new string[2] { "未知目标", "Unknown target" }
			},
			{
				"t.taskhelper.6",
				new string[2] { "开机自启任务参数刷新 旧任务缺少 ", "Startup task argument refresh: old task missing the " }
			},
			{
				"t.taskhelper.7",
				new string[2] { " 静默启动参数 重建任务补齐", " silent-launch argument, task rebuilt to add it" }
			},
			{
				"log.taskhelper.8",
				new string[2] { "开机自启任务迁移失败 需要在设置页重新开一次自启 schtasks rc=", "Startup task migration failed; re-enable autostart on the settings page. schtasks rc=" }
			},
			{
				"log.taskhelper.9",
				new string[2] { "开机自启任务参数刷新失败 需要在设置页重新开一次自启 schtasks rc=", "Startup task argument refresh failed; re-enable autostart on the settings page. schtasks rc=" }
			},
			{
				"log.taskhelper.10",
				new string[2] { "开机自启任务缺少电池或运行时限保护设置 重建任务补齐", "Startup task missing battery or execution-time-limit protections, task rebuilt to add them" }
			},
			{
				"log.taskhelper.11",
				new string[2] { "开机自启任务的电池与运行时限设置补不上 已停止重试 自启本身照常有效 schtasks rc=", "Could not add the battery and execution-time-limit settings to the startup task; retries stopped. Autostart itself still works. schtasks rc=" }
			},
			{
				"t.taskhelper.12",
				new string[2] { " 笔记本用电池开机时可能不会自启", " on a laptop running on battery, autostart may not fire" }
			},
			{
				"log.taskhelper.13",
				new string[2] { "开机自启任务的电池与运行时限设置补齐失败 下次启动再试 schtasks rc=", "Failed to add the battery and execution-time-limit settings to the startup task; will retry at next launch. schtasks rc=" }
			},
			{
				"t.taskhelper.14",
				new string[2] { "无法确定当前用户身份 没有生成任务定义", "Could not determine the current user identity, task definition not generated" }
			},
			{
				"t.taskhelper.15",
				new string[2] { "schtasks 超时未退出", "schtasks timed out without exiting" }
			},
			{
				"t.taskhelper.16",
				new string[2] { " 原因 ", " reason " }
			},
			{
				"t.updatechecker.4",
				new string[2] { "没有连上更新服务 检查网络后重试", "Could not reach the update service; check the network and retry" }
			},
			{
				"log.updatechecker.5",
				new string[2] { "检查更新 ", "Update check " }
			},
			{
				"log.updatechecker.6",
				new string[2] { " 条线路全部失败", " routes all failed" }
			},
			{
				"log.updatechecker.7",
				new string[2] { "检查更新 线路 ", "Update check route " }
			},
			{
				"log.updatechecker.8",
				new string[2] { " 远端 ", " remote " }
			},
			{
				"t.contactdialog.1",
				new string[2] { "Pavise 完全免费", "Pavise is completely free" }
			},
			{
				"t.contactdialog.2",
				new string[3] { "GPL-3.0 开源", "GPL-3.0 open source", "GPL-3.0 オープンソース" }
			},
			{
				"t.panelform.1",
				new string[2] { "导航项数量 ", "Navigation item count " }
			},
			{
				"t.panelform.2",
				new string[2] { " 与 PageId.Count ", " and PageId.Count " }
			},
			{
				"t.panelform.3",
				new string[2] { " 不一致", " do not match" }
			},
			{
				"t.panelform.4",
				new string[2] { "导航视觉排序含越界项 ", "Navigation visual order contains an out-of-range item " }
			},
			{
				"t.panelform.5",
				new string[2] { "导航视觉排序重复了 ", "Navigation visual order duplicates " }
			},
			{
				"log.panelform.8",
				new string[2] { "界面主题切换 ", "UI theme switched to " }
			},
			{
				"log.panelform.9",
				new string[2] { "亮色", "Light" }
			},
			{
				"log.panelform.10",
				new string[2] { "暗色", "Dark" }
			},
			{
				"log.techlistbox.1",
				new string[2] { "列表行绘制异常 已用底色顶替 行 ", "List row draw error, filled with background color instead, row " }
			},
			{
				"log.panelformauditpage.1",
				new string[2] { "体检修复项已还原 重新体检核对", "Checkup fix item restored, run checkup again to verify" }
			},
			{
				"log.panelformauditpage.2",
				new string[2] { "体检修复项已执行 重新体检核对", "Checkup fix item applied, run checkup again to verify" }
			},
			{
				"log.panelformauditpage.3",
				new string[2] { "体检一键修复 已执行 ", "Checkup one-click fix: applied " }
			},
			{
				"log.panelformauditpage.4",
				new string[2] { " 项 重新体检核对", " item(s), run checkup again to verify" }
			},
			{
				"log.panelformauditpage.5",
				new string[2] { "体检一键修复 无待修项", "Checkup one-click fix: nothing to fix" }
			},
			{
				"log.panelformlogpage.1",
				new string[2] { "运行日志已手动清除", "Run log manually cleared" }
			},
			{
				"log.panelformsettingspage.1",
				new string[2] { "着色器缓存清理 释放 ", "Shader cache cleanup: freed " }
			},
			{
				"log.panelformsettingspage.2",
				new string[2] { " 个文件被占用已跳过", " file(s) in use skipped" }
			},
			{
				"t.panelformsettingspage.3",
				new string[2] { "手动清除全部配置", "Manually clear all configuration" }
			},
			{
				"set.lang",
				new string[2] { "界面语言", "Language" }
			},
			{
				"env.performance.unverified",
				new string[2] { "配置已记录 · 重启状态待验证", "Configuration recorded; restart status unverified" }
			},
			{
				"auto.log.module",
				new string[2] { "自动执行", "AUTOMATIC" }
			},
			{
				"auto.log.session",
				new string[2] { "本次对局", "Game session" }
			},
			{
				"auto.log.empty",
				new string[2] { "暂无执行状态", "No execution state recorded" }
			},
			{
				"auto.log.started",
				new string[2] { "已开始接管", "Session management started" }
			},
			{
				"auto.log.applied",
				new string[2] { "已应用", "Applied" }
			},
			{
				"auto.log.configured",
				new string[2] { "驱动配置已写入", "Driver settings written" }
			},
			{
				"auto.log.prestage",
				new string[3] { "启动器待命 · 为下次游戏预配置", "Launcher standby: pre-configuring the next game", "ランチャー待機中：次回のゲームを事前設定" }
			},
			{
				"auto.log.restored",
				new string[2] { "已还原", "Restored" }
			},
			{
				"auto.log.restoring",
				new string[2] { "还原中", "Restoring" }
			},
			{
				"auto.log.failed",
				new string[2] { "应用失败", "Could not apply" }
			},
			{
				"auto.log.restore-failed",
				new string[2] { "还原失败", "Could not restore" }
			},
			{
				"auto.log.pending-reboot",
				new string[2] { "设置已写入，重启后生效", "Settings written; restart required" }
			},
			{
				"auto.log.restore-pending-reboot",
				new string[2] { "还原设置已写入，重启后生效", "Restore settings written; restart required" }
			},
			{
				"auto.log.ready",
				new string[2] { "环境已就绪", "Environment ready" }
			},
			{
				"auto.log.timer-requested",
				new string[2] { "已申请计时器精度", "Timer resolution requested" }
			},
			{
				"auto.log.timer-locked",
				new string[2] { "对局计时器已达到 0.5 ms", "Match timer reached 0.5 ms" }
			},
			{
				"auto.log.timer-limited",
				new string[2] { "已请求 0.5 ms · 系统当前未达到", "0.5 ms requested; system has not reached it" }
			},
			{
				"auto.log.timer-unverified",
				new string[2] { "已请求 0.5 ms · 当前精度待验证", "0.5 ms requested; current resolution unverified" }
			},
			{
				"env.gtimer.readback",
				new string[2] { "目标 {0} ms · 当前 {1} ms · 系统最细 {2} ms", "Target {0} ms · Current {1} ms · System finest {2} ms" }
			},
			{
				"env.gtimer.query-unverified",
				new string[2] { "计时器请求已持有，暂时无法读取实际精度", "Timer request is held; actual resolution cannot currently be read" }
			},
			{
				"env.gtimer.state.ready",
				new string[2] { "配置已生效，等待进入对局；目前未持有精度请求。", "Configuration is ready. Waiting for a match; no timer request is currently held." }
			},
			{
				"env.gtimer.state.locked",
				new string[2] { "已请求 0.5 ms，最近一次读回已达标；表示当前精度，不代表永久锁定。", "The 0.5 ms request is held and the latest reading meets the target. This is a current reading, not a permanent lock." }
			},
			{
				"env.gtimer.state.limited",
				new string[2] { "已请求 0.5 ms，但当前读回值未达标；“系统最细”表示系统支持的最小计时间隔。", "The 0.5 ms request is held, but the current reading misses the target. System finest is the smallest interval supported by Windows." }
			},
			{
				"env.gtimer.state.query-unverified",
				new string[2] { "已发出请求，但无法验证实际精度；暂时不能确认达到 0.5 ms。", "A request is held, but the actual resolution cannot be verified. The 0.5 ms target is not confirmed." }
			},
			{
				"env.gtimer.state.pending-reboot",
				new string[2] { "配置已写入，等待重启电脑；尚未开始对局精度请求。", "Configuration was written. Restart Windows before match timer requests can begin." }
			},
			{
				"env.gtimer.state.restore-pending-reboot",
				new string[2] { "原配置已还原，等待重启电脑生效；不会再为对局申请 0.5 ms。", "The original configuration was restored and needs a Windows restart. No new match timer requests will be made." }
			},
			{
				"env.gtimer.state.unverified",
				new string[2] { "暂时无法确认配置是否已经过重启生效；此状态不代表已达到 0.5 ms。", "Cannot yet confirm whether the configuration took effect after a restart. This does not confirm 0.5 ms." }
			},
			{
				"env.gtimer.state.failed",
				new string[2] { "配置、精度申请或释放未完成，可能仍有请求或设置待恢复；请查看日志。", "Configuration, request or release did not complete. A request or setting may still need recovery; check the log." }
			},
			{
				"env.gtimer.state.stopped",
				new string[2] { "本阶段执行已停止，恢复记录已保留；请查看日志。", "Execution stopped for this phase. Recovery records are retained; check the log." }
			},
			{
				"env.gtimer.state.restore-failed",
				new string[2] { "原配置尚未完全还原，恢复记录已保留供重试；请查看日志。", "The original configuration was not fully restored. Recovery records are retained for retry; check the log." }
			},
			{
				"env.gtimer.state.restored",
				new string[2] { "Pavise 的配置已恢复，当前未持有请求；其他程序仍可能维持较高精度。", "Pavise's configuration is restored and no request is held. Other applications may still keep a finer resolution." }
			},
			{
				"env.gtimer.state.unsupported",
				new string[2] { "当前 Windows 版本不支持此全局计时配置，Pavise 不会据此申请 0.5 ms。", "This Windows version does not support this global timer configuration, so Pavise will not request 0.5 ms through it." }
			},
			{
				"env.gtimer.state.requested",
				new string[2] { "已执行对局精度请求，仍需实际读回才能确认是否达到 0.5 ms。", "A match timer request was made. An actual reading is still needed to confirm 0.5 ms." }
			},
			{
				"env.gtimer.state.pending",
				new string[2] { "已选择开启，正在等待配置确认；尚未确认 0.5 ms 生效。", "Enabled in settings and awaiting configuration confirmation. The 0.5 ms target is not yet confirmed." }
			},
			{
				"env.gtimer.state.off",
				new string[2] { "功能已关闭，Pavise 当前未持有请求；其他程序仍可能维持较高精度。", "The feature is off and Pavise holds no timer request. Other applications may still keep a finer resolution." }
			},
			{
				"env.gtimer.state.release-pending",
				new string[2] { "关闭已提交，但当前精度请求尚未释放。", "Turning off was requested, but the timer request has not yet been released." }
			},
			{
				"auto.log.unsupported",
				new string[2] { "当前系统不支持，已跳过", "Unsupported on this system; skipped" }
			},
			{
				"auto.log.unchanged",
				new string[2] { "当前策略未启用此项", "Not enabled by the current policy" }
			},
			{
				"auto.log.stopped",
				new string[2] { "本阶段执行已停止，请查看日志", "Execution stopped for this phase; check the log" }
			},
			{
				"set.exitgrace",
				new string[2] { "退局确认等待时间", "Game exit confirmation delay" }
			},
			{
				"set.exitgrace.seconds",
				new string[2] { "{0} 秒", "{0} s" }
			},
			{
				"set.exitgrace.n",
				new string[2] { "默认 8 秒，修改立即生效。0 秒：首次确认游戏消失即退局；短等待可能误判进程交接。后台压制在首次检测到退局时先解除，不等待倒计时。", "Default: 8 s; changes apply immediately. 0 s ends the session on the first confirmed absence; short delays may misidentify process handoffs. Background suppression is released before this countdown." }
			},
			{
				"set.exitgrace.fail",
				new string[2] { "退局等待时间保存失败，已保留原设置。", "Could not save the exit delay. The previous setting was retained." }
			},
			{
				"set.lang.n",
				new string[2] { "选择语言后立即生效 界面与新产生的日志提示一并切换", "Select a language to apply it immediately; the UI and newly written log messages switch together." }
			},
			{
				"lang.zh",
				new string[2] { "中文", "中文" }
			},
			{
				"lang.en",
				new string[2] { "English", "English" }
			}
		};

		public static void Init()
		{
			Cur = ((Settings.LoadStr("Lang", "zh") == "en") ? 1 : 0);
		}

		public static void Set(int i)
		{
			lock (ChangeGate)
			{
				Cur = ((i == 1) ? 1 : 0);
				Settings.SaveStr("Lang", (Cur == 1) ? "en" : "zh");
			}
		}

		public static string T(string key)
		{ return T(key, Cur); }

		public static string T(string key, int language)
		{
			string[] value;
			if (M.TryGetValue(key, out value))
			{
				return value[(language >= 0 && language < value.Length) ? language : 0];
			}
			return key;
		}

		public static string F(string key, params object[] args)
		{
			return string.Format(T(key), args);
		}

		public static bool HasKey(string key)
		{
			if (key != null)
			{
				return M.ContainsKey(key);
			}
			return false;
		}

		public static void Merge(IDictionary<string, string[]> extra)
		{
			if (extra == null)
			{
				return;
			}
			foreach (KeyValuePair<string, string[]> item in extra)
			{
				if (item.Key != null && item.Value != null && item.Value.Length > 0)
				{
					M[item.Key] = item.Value;
				}
			}
		}
	}
}
