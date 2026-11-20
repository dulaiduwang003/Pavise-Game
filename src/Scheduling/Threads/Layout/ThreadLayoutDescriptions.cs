using System.Collections.Generic;

namespace PaviseApp
{
    internal static class ThreadLayoutDescriptions
    {
        internal static string Text(string zh, string en, string ja)
        { return Lang.Cur == 2 ? ja : Lang.Text(zh, en); }

        // 渲染线程亲和性的三种模式 自动 渲染单核 手动 其余枚举值只剩旧存档 按原名显示便于对照
        internal static string StrategyName(SchedulingStrategy strategy)
        {
            return strategy == SchedulingStrategy.Orchestration ? PerformancePolicy.Text("自动", "Automatic")
                : strategy == SchedulingStrategy.RenderCore ? PerformancePolicy.Text("渲染单核", "Single render core")
                : strategy == SchedulingStrategy.GamePriority ? PerformancePolicy.Text("游戏优先（已停用）", "Game priority (retired)")
                : strategy == SchedulingStrategy.RenderBoost ? PerformancePolicy.Text("渲染优先（已停用）", "Render priority (retired)")
                : strategy == SchedulingStrategy.DependencyBoost ? PerformancePolicy.Text("依赖协同（已停用）", "Dependency coordination (retired)")
                : strategy == SchedulingStrategy.RenderCoreTimeCritical ? PerformancePolicy.Text("专核强化（按渲染单核运行）", "Enhanced dedicated rendering (runs as single render core)")
                : PerformancePolicy.Text("已停用", "Retired");
        }

        // 摘要按运行规则解析 旧方案显示换算后的模式 手动显示所选 CPU
        internal static string StrategyName(CriticalThreadRule rule)
        {
            CriticalThreadRule effective = rule == null ? null : CriticalThreadRule.ParseForRuntime(rule.Encode());
            if (effective == null || effective.IsOff) return PerformancePolicy.Text("关闭", "Off");
            string avoid = PerformancePolicy.Text(" · 整核避让", " · whole-core avoidance");
            if (effective.IsManualPin)
                return PerformancePolicy.Text("手动 · ", "Manual · ") + "CPU" + CpuTopology.DescribeRanges(effective.ManualMask) + (effective.ClearCore ? avoid : "");
            // 自动不用整核避让 不加避让标注
            return StrategyName(effective.Strategy) + (effective.ClearCore && !effective.IsOrchestration ? avoid : "");
        }

        internal static string StrategyHint(SchedulingStrategy strategy)
        {
            if (strategy == SchedulingStrategy.Orchestration)
                return PerformancePolicy.Text(
                    "推荐。实测找出卡帧的关键线程，各指定一颗优先运行的物理核，不锁定，后台程序避开这些核。",
                    "Recommended. Measures the game to find the threads that gate frame time and gives each a preferred physical core without locking it; background apps stay off those cores.");
            if (strategy == SchedulingStrategy.RenderCore)
                return PerformancePolicy.Text(
                    "把渲染线程固定到一颗最合适的物理核，减少换核。GPU 瓶颈的游戏也会固定。",
                    "Pins the identified render thread to the most suitable physical core to reduce migration. It also pins in GPU-bound games.");
            return PerformancePolicy.Text("旧方案，按换算后的模式运行或关闭。", "A retired strategy; it runs as the converted mode or stays off.");
        }

        internal static string ManualHint()
        {
            return PerformancePolicy.Text(
                "把渲染线程固定到你选的一到四个逻辑 CPU 上。处理器或游戏核心范围变了需要重选。",
                "Pins the render thread to the one to four logical CPUs you choose. Choose again if the processor or the game's CPU range changes.");
        }

        internal static string StrategyHint(CriticalThreadRule rule)
        {
            CriticalThreadRule effective = rule == null ? null : CriticalThreadRule.ParseForRuntime(rule.Encode());
            if (effective == null || effective.IsOff) return "";
            return effective.IsManualPin ? ManualHint() : StrategyHint(effective.Strategy);
        }

        // 整核避让的说明 只对渲染单核和手动有效 自动只设优先核心 不挪动其他线程
        internal static string ClearCoreHint(SchedulingStrategy strategy, bool on)
        {
            if (strategy == SchedulingStrategy.Orchestration)
                return PerformancePolicy.Text("自动模式只设优先核心，不挪动其他游戏线程，整核避让不适用。", "Automatic only sets preferred cores and does not move other game threads, so whole-core avoidance does not apply.");
            if (!on)
                return PerformancePolicy.Text("整核避让已关：渲染线程固定在所选核心上，但其他游戏线程仍可使用这颗核。", "Whole-core avoidance off: the render thread is pinned, but other game threads can still use that core.");
            return PerformancePolicy.Text(
                "整核避让：所选核心所在的整颗物理核（含同核超线程）只留给渲染线程，其他游戏线程在原范围内避开，无法调整的线程跳过。其余线程至少要剩两颗物理核，否则本局不避让。系统任务仍可使用这颗核。",
                "Whole-core avoidance: the whole physical core (including its SMT sibling) is left to the render thread; other game threads move off it within their existing ranges, skipping those that cannot be moved. At least two physical cores must remain, otherwise avoidance is skipped. System tasks can still use the core.");
        }

        internal static string Name(CriticalThreadLayout layout)
        {
            return layout == CriticalThreadLayout.Unified
                ? Text("渲染线程亲和性", "Render thread affinity", "Render thread affinity")
                : Text("已退役布局（仅恢复）", "Retired layout (recovery only)", "Retired layout (recovery only)");
        }

        internal static string Hint(CriticalThreadLayout layout)
        {
            if (layout != CriticalThreadLayout.Unified)
                return Text("旧布局不再运行，仅恢复已有记录。", "Legacy layouts no longer run; existing records are recovery-only.", "Legacy layouts no longer run; existing records are recovery-only.");
            return PerformancePolicy.Text(
                "选择自动（推荐）、渲染单核或手动。亲和性只决定线程在哪些核心上跑，不改优先级；优先级由深度调优里的性能优先级和游戏线程提优负责。",
                "Choose Automatic (recommended), Single render core or Manual. Affinity only decides which cores threads run on; priority comes from Performance priority and Game thread boost in Advanced tuning.");
        }

        internal static string Help()
        {
            string separator = PerformancePolicy.Text("：", ": ");
            var parts = new List<string>
            {
                StrategyName(SchedulingStrategy.Orchestration) + PerformancePolicy.Text("（推荐）", " (recommended)") + separator + StrategyHint(SchedulingStrategy.Orchestration),
                StrategyName(SchedulingStrategy.RenderCore) + separator + StrategyHint(SchedulingStrategy.RenderCore),
                PerformancePolicy.Text("手动", "Manual") + separator + ManualHint(),
                PerformancePolicy.Text(
                    "整核避让：让其他游戏线程避开渲染线程所在的整颗物理核，渲染单核和手动可选，自动始终开启。可能影响非常依赖多线程的游戏，系统任务仍可使用这些核心。",
                    "Whole-core avoidance: keeps other game threads off the render thread's whole physical core; optional for Single render core and Manual, always on for Automatic. May slow heavily threaded games; system tasks can still use these cores."),
                PerformancePolicy.Text(
                    "亲和性只做核心放置，不改线程和进程优先级。保存后下局生效；关闭、退局或退出时恢复本方修改，保留外部后续改动。",
                    "Affinity only places cores and never changes thread or process priority. Saved changes apply next session; disabling it, game end or app exit restores owned changes while preserving later external edits."),
                PerformancePolicy.Text(
                    "效果因游戏而异，请在相同场景对照 1% low 和流畅度。",
                    "Results vary by game. Compare 1% lows and smoothness in the same scene.")
            };
            return string.Join("\n\n", parts.ToArray());
        }
    }
}
