using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private void AddAutomationInventory(Control parent, ref int y)
        {
            foreach (PerformanceAction action in PerformanceActionCatalog.All)
            {
                if (action.Environment) continue;
                PerformanceAction captured = action;
                AddAutomationName(parent, ref y, action.Key, action.Zh, action.En,
                    delegate { return captured.Key == PerformanceActionCatalog.VisualEffects ? EnvironmentVisualEffectsStatus()
                        : PerformanceTuning.Label(gameMode.PerformanceExecutionState(captured.Key)); }, action.Detail);
            }
            AddAutomationName(parent, ref y, "AutoAdd", "前台游戏自动入库", "Foreground game auto-add",
                delegate { return gameMode.AutoAddFullscreen ? AutoText("按条件识别", "Conditional detection") : AutoText("已关闭", "Off"); });
            AddAutomationName(parent, ref y, PolicyCatalog.KeyGameFullscreenOptimizations, "逐游戏禁用全屏优化", "Per-game fullscreen optimization override",
                delegate { return AutoText("按游戏设置 · 默认关闭", "Per-game setting · default off"); },
                Lang.T("lib.fso.tip"));
            AddAutomationName(parent, ref y, "Renderer", "游戏渲染进程识别", "Game renderer detection", null);
            AddAutomationName(parent, ref y, "RendererLearning", "游戏渲染活动观测", "Game rendering observation", null);
            AddAutomationName(parent, ref y, "Whitelist", "游戏白名单保护", "Game whitelist protection", null);
            AddAutomationName(parent, ref y, "AntiCheat", "反作弊进程保护", "Anti-cheat process protection", null);
            AddAutomationName(parent, ref y, "SelfYield", "Pavise 自身资源让位", "Pavise resource yielding", null);
            AddAutomationName(parent, ref y, "GameGpuPreference", "待命游戏高性能显卡预选", "Standby game GPU preference", null);
            AddAutomationName(parent, ref y, "BackgroundGpuPreference", "后台节能显卡登记", "Background power-saving GPU registration", null);
            AddAutomationName(parent, ref y, "GameIo", "游戏磁盘 I/O 提优", "Game disk I/O priority", null);
            AddAutomationName(parent, ref y, FrameCoordination.ActionKey, "游戏线程协同提优", "Game thread coordination",
                delegate { return !gameMode.FrameCoordinationEnabled ? AutoText("掌机 · 系统调度", "Handheld · system")
                    : FrameCoordination.State == 2 ? AutoText("已执行", "Active")
                    : FrameCoordination.State == 1 ? AutoText("正在判定", "Checking")
                    : FrameCoordination.State == 4 ? AutoText("本进程已跳过", "Skipped for this process")
                    : FrameCoordination.State == 3 ? AutoText("等待条件", "Waiting for conditions")
                    : AutoText("自动待命", "Automatic standby"); });
            AddAutomationName(parent, ref y, "GameGpu", "游戏 GPU 调度优先级", "Game GPU scheduling priority", null);
            AddAutomationName(parent, ref y, "GameEco", "游戏节能调度限制解除", "Game power-throttling release", null);
            AddAutomationName(parent, ref y, "BackgroundCpu", "后台 CPU 优先级让位", "Background CPU priority yielding", null);
            AddAutomationName(parent, ref y, "BackgroundIo", "后台磁盘 I/O 让位", "Background disk I/O yielding", null);
            AddAutomationName(parent, ref y, "BackgroundMemory", "后台内存页优先级让位", "Background memory-page yielding", null);
            AddAutomationName(parent, ref y, "BackgroundEco", "后台 EcoQoS 节能调度", "Background EcoQoS", null);
            AddAutomationName(parent, ref y, "GameDvr", "关闭系统游戏录制", "Disable system game recording",
                EnvironmentGameDvrStatus);
            AddAutomationName(parent, ref y, "Mmcss", "MMCSS 多媒体调度", "MMCSS multimedia scheduling",
                delegate { return GameMode.AutomationStateLabel(gameMode.AutomaticExecutionState("GmMmcss")); });
            AddAutomationName(parent, ref y, "CorePlan", "逐游戏核心调度", "Per-game core scheduling", PerGameAutomationLabel);
            AddAutomationName(parent, ref y, "CoreIsolation", "游戏核心独占", "Game core isolation", PerGameAutomationLabel);
            AddAutomationName(parent, ref y, "HardAffinity", "后台亲和性硬绑核", "Hard background affinity",
                delegate { return AutoText("随核心隔离自动启用", "Automatic with core isolation"); });
            AddAutomationName(parent, ref y, "Family", "游戏同族后台压制", "Game-family background suppression", PerGameAutomationLabel);
            AddAutomationName(parent, ref y, "Rebar", "逐游戏 ReBAR 强开", "Per-game forced ReBAR", PerGameAutomationLabel);
            AddAutomationPreference(parent, ref y, PolicyCatalog.KeyTimerTick, "计时器恒定节拍", "Constant timer tick");
            AddAutomationPreference(parent, ref y, PolicyCatalog.KeyGlobalTimer, "全局计时器分辨率", "Global timer resolution");
            AddAutomationPreference(parent, ref y, PolicyCatalog.KeyCacheWarm, "游戏资源缓存预热", "Game resource cache warm-up");
            AddAutomationPreference(parent, ref y, PolicyCatalog.KeyStandbyCleaner, "待机内存清理", "Standby memory cleaning");
            AddAutomationName(parent, ref y, "LowLatency", "游戏低延迟", "Game low latency",
                delegate { return AutomaticPolicy.GameLowLatency ? AutoText("按环境设置", "Environment preference") : AutoText("已关闭", "Off"); });
            AddAutomationName(parent, ref y, LaptopPerfMode.PreferenceKey, "笔记本厂商性能模式", "Laptop vendor performance mode",
                delegate { return PowerPlan.NoSwitchSelected ? AutoText("保留系统电源", "System power retained") : gameMode.LaptopPerformanceEnabled
                    ? AutoText("按设置 · 插电对局", "Settings · plugged-in games") : AutoText("已关闭", "Off"); });
            AddAutomationName(parent, ref y, AutomaticCoreLayout.PreferenceKey, "后台核心自动避让", "Automatic background core placement",
                delegate { return gameMode.AutomaticCoreLayoutEnabled
                    ? AutoText("按设置 · 后台避让", "Settings · background placement") : AutoText("已关闭", "Off"); });
        }

        private string PerGameAutomationLabel()
        { return AutoText("按游戏方案", "Per-game plan"); }

        private void AddAutomationPreference(Control parent, ref int y, string key, string zh, string en)
        {
            AddAutomationName(parent, ref y, key, zh, en, delegate {
                return AutomationPreferenceStatus(key);
            });
        }

        private string AutomationPreferenceStatus(string key)
        {
            if (key == PolicyCatalog.KeyTimerTick || key == PolicyCatalog.KeyGlobalTimer)
            {
                switch (gameMode.AutomaticExecutionState(key))
                {
                    case "pending-reboot": return AutoText("已配置 · 待重启", "Restart required");
                    case "restore-pending-reboot": return AutoText("已恢复 · 待重启", "Restored; restart required");
                    case "unverified": return AutoText("重启状态待验证", "Restart unverified");
                    case "ready": return AutoText("配置已就绪", "Configuration ready");
                    case "timer-requested": return AutoText("对局计时请求已执行", "Match timer request active");
                    case "timer-locked": return AutoText("0.5 ms 已生效", "0.5 ms active");
                    case "timer-limited": return AutoText("系统精度未达标", "Resolution limited");
                    case "timer-unverified": return AutoText("实际精度待验证", "Resolution unverified");
                    case "failed": return AutoText("执行受限 · 查看日志", "Failed; see logs");
                    case "restore-failed": return AutoText("还原未完成 · 查看日志", "Restore failed; see logs");
                    case "unsupported": return AutoText("系统不支持", "Not supported");
                    case "restored": return AutoText("已恢复", "Restored");
                }
                if (key == PolicyCatalog.KeyGlobalTimer && Native.OsBuild() < 20348)
                    return AutoText("系统不支持", "Not supported");
            }
            return AutomaticPolicy.EnvironmentPreference(key) ? AutoText("按环境设置", "Environment preference") : AutoText("已关闭", "Off");
        }

        private void AddAutomationName(Control parent, ref int y, string id, string zh, string en, Func<string> status, string detail = "")
        {
            var state = new Label { Name = "automationSupplement_" + id, Font = Theme.UI(8.5f, true),
                BackColor = Color.Transparent, ForeColor = Theme.Dim, TextAlign = ContentAlignment.MiddleRight,
                Size = new Size(Theme.S(260), Theme.S(30)) };
            int height;
            int minimum = detail.Length == 0 ? 48 : FullTextCardHeight(detail, ScrollContentW + RailW, state, 80);
            MakeAutoCard(parent, 6, y, ScrollContentW + RailW, minimum, AutoText(zh, en), detail, state, out height);
            y += height + 8;
            automationSync.Add(delegate { state.Text = status != null ? status() : AutoText("按条件参与", "When eligible"); });
        }
    }
}
