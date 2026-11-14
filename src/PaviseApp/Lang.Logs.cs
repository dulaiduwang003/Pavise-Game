using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class Lang
    {
        private static void AddLogsMessages(Dictionary<string, string[]> messages)
        {
            messages.Add("log.englishinput.result", new string[2] { "进入游戏英文切换：{0} · {1}（代码 {2}）", "Game-entry English switch: {0} · {1} (code {2})" });
            messages.Add("log.latest.session", new string[3] { "最近一局", "Latest game", "直近の対局" });
            messages.Add("log.category.actions", new string[3] { "动作日志", "Action log", "動作ログ" });
            messages.Add("log.category.diagnostics", new string[3] { "诊断日志", "Diagnostic log", "診断ログ" });
            messages.Add("log.actions.hint", new string[3] { "动作预览 · 双击复制记录 · 导出完整会话", "Action preview · double-click to copy the entry · export the full session", "動作のプレビュー · ダブルクリックで記録をコピー · 対局全体を出力" });
            messages.Add("log.diagnostics.hint", new string[3] { "诊断预览 · 双击复制原文 · 导出完整原始日志", "Diagnostic preview · double-click to copy · export the full raw log", "診断のプレビュー · ダブルクリックで原文コピー · 元のログ全体を出力" });
            messages.Add("log.latest.empty", new string[3] { "暂无对局日志\r\n进入游戏后自动显示最近一局", "No game logs yet\r\nThe latest game appears automatically when you play", "対局の記録はまだありません\r\nゲームを開始すると直近の対局を自動表示します" });
            messages.Add("log.export.file", new string[3] { "导出文件", "Export file", "ファイル出力" });
            messages.Add("log.export.detail", new string[3] { "选择位置保存当前完整运行日志", "Choose where to save the complete current runtime log", "現在の実行ログ全体の保存先を選択" });
            messages.Add("log.export.title", new string[3] { "导出运行日志", "Export runtime log", "実行ログを出力" });
            messages.Add("log.export.actions.title", new string[3] { "导出当前或最近会话的动作日志", "Export actions for the current or latest session", "現在または直近の対局の動作ログを出力" });
            messages.Add("log.export.diagnostics.title", new string[3] { "导出完整诊断日志", "Export the complete diagnostic log", "診断ログ全体を出力" });
            messages.Add("log.export.actions.detail", new string[3] { "从原始日志导出当前或最近会话的完整动作记录，不受页面预览条数限制", "Export all recorded actions for the current or latest session from the source log, beyond the preview limit", "元のログから現在または直近の対局の動作記録を出力します。プレビューの件数制限は適用されません" });
            messages.Add("log.export.diagnostics.detail", new string[3] { "导出当前完整原始日志，包含其他会话和诊断细节", "Export the complete source log, including other sessions and diagnostic details", "他の対局や診断情報も含め、現在の元ログ全体を出力します" });
            messages.Add("log.export.actions.saved", new string[3] { "当前或最近会话的完整动作记录已导出：", "All recorded actions for the current or latest session were exported:", "現在または直近の対局の動作記録をすべて出力しました：" });
            messages.Add("log.export.diagnostics.saved", new string[3] { "完整原始诊断日志已导出：", "The complete raw diagnostic log was exported:", "元の診断ログ全体を出力しました：" });
            messages.Add("log.export.filter", new string[3] { "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt", "Log files (*.log)|*.log|Text files (*.txt)|*.txt", "ログファイル (*.log)|*.log|テキストファイル (*.txt)|*.txt" });
            messages.Add("log.export.saved", new string[3] { "日志已导出到：", "Log exported to:", "ログの保存先：" });
            messages.Add("log.export.failed", new string[3] { "日志导出失败：", "Could not export the log:", "ログを出力できませんでした：" });
            messages.Add("log.solo.2", new string[2] { "对局单屏 已切回原来的多屏布局", "Single display: switched back to the original multi-display layout" });
            messages.Add("log.adaptive.1", new string[2] { "自适应压制 CPU 达到70%并持续10秒 已临时扩大后台范围 当前周期第 ", "Adaptive suppression: CPU at or above 70% for 10 seconds; temporarily widened background control. Current-cycle episode " });
            messages.Add("log.adaptive.2", new string[2] { "智能后台治理：负载恢复并稳定，已恢复常规范围", "Smart background control: load recovered and stayed calm; restored the normal scope" });
            messages.Add("log.adaptive.3", new string[2] { "智能后台治理：已结束临时扩大范围，升档状态与采样已复位", "Smart background control: temporary wider scope ended; escalation state and sampling reset" });
            messages.Add("log.backdrop.1", new string[2] { "背景封面 复制图片失败", "Backdrop: copying the image failed" });
            messages.Add("log.backdrop.2", new string[2] { "背景封面 删除图片失败", "Backdrop: deleting the image failed" });
            messages.Add("log.backdrop.3", new string[2] { "背景封面 读取图片失败", "Backdrop: reading the image failed" });
            messages.Add("log.backdrop.4", new string[2] { "背景封面 裁切失败", "Backdrop: fitting the image failed" });
            messages.Add("log.renderer.unconfirmed", new string[2] { "渲染候选 暂未获得足够证据 仅保护当前候选并低频复查 不改写游戏库 ", "Renderer candidate: evidence not yet sufficient; protecting only this candidate and rechecking at low frequency, without changing the library: " });
            messages.Add("log.renderer.confirmed", new string[2] { "渲染交接 GPU 证据确认 ", "Renderer handoff confirmed by GPU evidence: " });
            messages.Add("log.maint.1", new string[2] { "自动维护暂停 写入失败", "Automatic maintenance pause: write failed" });
            messages.Add("log.maint.2", new string[2] { "自动维护触发配置已写入；已有任务未终止，退局恢复", "Maintenance trigger configuration written; existing tasks are not stopped; restores on match exit" });
            messages.Add("log.maint.3", new string[2] { "自动维护已写回", "Automatic maintenance written back" });
            messages.Add("log.maint.4", new string[2] { "自动维护写回失败 记录保留待重试", "Automatic maintenance write-back failed; the record is kept for retry" });
            messages.Add("log.logwrites.off", new string[2] { "运行日志记录已关闭 后续动作不再写入 重新开启即恢复", "Run log writing turned off; later actions are not recorded. Turn it back on to resume." });
            messages.Add("log.logwrites.on", new string[2] { "运行日志记录已开启", "Run log writing turned on" });
            messages.Add("log.program.1", new string[2] { "开机自启任务指向其他程序副本，不经其重新启动，以当前程序继续运行", "The autostart task points to a different copy of the program; not relaunching through it and continuing with the current program" });
            messages.Add("log.program.2", new string[2] { "检测到上次未还原的分级后台控制 已恢复 ", "Detected tiered background control left unrestored from last run; restored " });
            messages.Add("log.program.3", new string[2] { " 个进程", " processes" });
            messages.Add("log.program.6", new string[2] { "启动检查更新 发现新版本 ", "Startup update check found new version " });
            messages.Add("log.program.7", new string[2] { " 当前 ", " current " });
            messages.Add("log.program.8", new string[2] { "启动检查更新 已是最新版本 ", "Startup update check: already the latest version " });
            messages.Add("log.program.9", new string[2] { "启动检查更新失败 ", "Startup update check failed " });
            messages.Add("log.program.10", new string[2] { "系统通知区域不可用 托盘图标无处显示 已改为直接打开主面板 否则本次启动将没有任何入口", "The system notification area is unavailable and the tray icon has nowhere to appear, so the main panel was opened directly; otherwise this session would have had no entry point at all" });
            messages.Add("log.program.autostart.traywaiting", new string[3] { "登录自启已进入后台，通知区域暂未就绪，保持界面隐藏。", "Autostart is running in the background; the notification area is not ready, so the panel stays hidden.", "自動起動はバックグラウンドで動作中。通知領域の準備を待ち、画面は表示しません。" });
            messages.Add("log.program.11", new string[2] { "系统版本低于运行门槛 已还原历史改动并退出 当前内部版本 ", "The OS version is below the required baseline; historical changes were restored and startup was aborted. Current build " });
            messages.Add("log.crashguard.1", new string[2] { "检测到上次异常退出 按 PID 创建时间 映像名恢复 ", "Detected abnormal exit last time; recovering by PID, creation time and image name: " });
            messages.Add("log.crashguard.2", new string[2] { " 个进程的已记录状态", " processes' recorded state restored" });
            messages.Add("log.crashguard.3", new string[2] { "仍有 ", "Still " });
            messages.Add("log.crashguard.4", new string[2] { " 个提优进程暂时无法恢复 身份快照已保留待下次重试", " boosted processes cannot be recovered yet; identity snapshots kept for the next retry" });
            messages.Add("log.legacypurge.31", new string[2] { " 项未能还原 ", " items could not be restored " });
            messages.Add("log.legacypurge.37", new string[2] { " 先还原 Pavise 改过的全部系统项", " restoring all system items Pavise changed first" });
            messages.Add("log.legacypurge.38", new string[2] { " 已中止 ", " aborted " });
            messages.Add("log.legacypurge.39", new string[2] { " 没有删除任何文件", " no files deleted" });
            messages.Add("log.legacypurge.45", new string[2] { "进程状态记账仍有未能核实的项，恢复记录保留；相关进程退出或重启电脑后可重试。", "Some process state bookkeeping entries could not be verified. Recovery records are retained; retry after the affected processes exit or the PC restarts." });
            messages.Add("log.versionmigrations.41", new string[2] { "开启", "enabled" });
            messages.Add("log.kernelac.installed", new string[2] { "内核反作弊（本机已安装 {0}）", "kernel anti-cheat (installed on this machine: {0})" });
            messages.Add("log.atomicfile.1", new string[2] { "写入失败", "Write failed" });
            messages.Add("log.procnotify.1", new string[2] { "进程事件订阅失败 退回轮询 ", "Process event subscription failed, falling back to polling " });
            messages.Add("log.procnotify.2", new string[2] { "进程发现快速通道已启用 游戏相关进程免除送达与合并等待", "Process discovery fast path enabled; game-related processes skip delivery and merge waits" });
            messages.Add("log.procnotify.3", new string[2] { "进程发现快速通道不可用 沿用常规路径 ", "Process discovery fast path unavailable, staying on the regular path " });
            messages.Add("log.psrunner.1", new string[2] { " PowerShell 执行超时 ", " PowerShell execution timed out " });
            messages.Add("log.psrunner.2", new string[2] { " PowerShell 执行失败 exit ", " PowerShell execution failed, exit " });
            messages.Add("log.psrunner.3", new string[2] { " 无法执行 PowerShell ", " could not run PowerShell " });
            messages.Add("log.settings.2", new string[2] { "设置写入失败 ", "Settings write failed " });
            messages.Add("log.taskhelper.1", new string[2] { "开机自启任务读取失败 本次不修改", "Startup task read failed, not modified this time" });
            messages.Add("log.taskhelper.2", new string[2] { "开机自启任务迁移已跳过 当前程序位于临时或易失目录 ", "Startup task migration skipped, the program is in a temporary or volatile directory " });
            messages.Add("log.taskhelper.3", new string[2] { " 文件可能被清理导致自启失效 请把程序移到固定目录后再启动", " files there may be cleaned up and break autostart, move the program to a permanent directory and launch again" });
            messages.Add("log.taskhelper.8", new string[2] { "开机自启任务迁移失败 需要在设置页重新开一次自启 schtasks rc=", "Startup task migration failed; re-enable autostart on the settings page. schtasks rc=" });
            messages.Add("log.taskhelper.9", new string[2] { "开机自启任务参数刷新失败 需要在设置页重新开一次自启 schtasks rc=", "Startup task argument refresh failed; re-enable autostart on the settings page. schtasks rc=" });
            messages.Add("log.taskhelper.10", new string[2] { "开机自启任务缺少电池或运行时限保护设置 重建任务补齐", "Startup task missing battery or execution-time-limit protections, task rebuilt to add them" });
            messages.Add("log.taskhelper.11", new string[2] { "开机自启任务的电池与运行时限设置补不上 已停止重试 自启本身照常有效 schtasks rc=", "Could not add the battery and execution-time-limit settings to the startup task; retries stopped. Autostart itself still works. schtasks rc=" });
            messages.Add("log.taskhelper.13", new string[2] { "开机自启任务的电池与运行时限设置补齐失败 下次启动再试 schtasks rc=", "Failed to add the battery and execution-time-limit settings to the startup task; will retry at next launch. schtasks rc=" });
            messages.Add("log.updatechecker.5", new string[2] { "检查更新 ", "Update check " });
            messages.Add("log.updatechecker.6", new string[2] { " 条线路全部失败", " routes all failed" });
            messages.Add("log.updatechecker.7", new string[2] { "检查更新 线路 ", "Update check route " });
            messages.Add("log.updatechecker.8", new string[2] { " 远端 ", " remote " });
            messages.Add("log.panelform.6", new string[2] { "界面随窗口缩放 窗口 ", "UI scaled to window " });
            messages.Add("log.panelform.7", new string[2] { " 缩放 ", " scale " });
            messages.Add("log.panelform.11", new string[2] { "界面 DPI 变化 ", "UI DPI changed to " });
            messages.Add("log.panelform.8", new string[2] { "界面主题切换 ", "UI theme switched to " });
            messages.Add("log.panelform.9", new string[2] { "亮色", "Light" });
            messages.Add("log.panelform.10", new string[2] { "暗色", "Dark" });
            messages.Add("log.techlistbox.1", new string[2] { "列表行绘制异常 已用底色顶替 行 ", "List row draw error, filled with background color instead, row " });
            messages.Add("log.panelformauditpage.1", new string[2] { "体检修复项已还原 重新体检核对", "Checkup fix item restored, run checkup again to verify" });
            messages.Add("log.panelformauditpage.2", new string[2] { "体检修复项已执行 重新体检核对", "Checkup fix item applied, run checkup again to verify" });
            messages.Add("log.panelformauditpage.3", new string[2] { "体检一键修复 已执行 ", "Checkup one-click fix: applied " });
            messages.Add("log.panelformauditpage.4", new string[2] { " 项 重新体检核对", " item(s), run checkup again to verify" });
            messages.Add("log.panelformauditpage.5", new string[2] { "体检一键修复 无待修项", "Checkup one-click fix: nothing to fix" });
            messages.Add("log.panelformlogpage.1", new string[2] { "运行日志已手动清除", "Run log manually cleared" });
            messages.Add("log.panelformsettingspage.1", new string[2] { "着色器缓存清理 释放 ", "Shader cache cleanup: freed " });
            messages.Add("log.panelformsettingspage.2", new string[2] { " 个文件被占用已跳过", " file(s) in use skipped" });
        }
    }
}
