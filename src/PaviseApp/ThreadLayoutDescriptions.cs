namespace PaviseApp
{
    internal static class ThreadLayoutDescriptions
    {
        internal static string Text(string zh, string en, string ja)
        { return Lang.Cur == 0 ? zh : Lang.Cur == 2 ? ja : en; }

        internal static string StrategyName(SchedulingStrategy strategy)
        {
            return strategy == SchedulingStrategy.GamePriority ? PerformancePolicy.Text("游戏优先", "Game priority")
                : strategy == SchedulingStrategy.RenderBoost ? PerformancePolicy.Text("渲染优先", "Render priority")
                : strategy == SchedulingStrategy.RenderCore ? PerformancePolicy.Text("渲染专核", "Dedicated rendering")
                : strategy == SchedulingStrategy.DependencyBoost ? PerformancePolicy.Text("依赖协同", "Dependency coordination")
                : strategy == SchedulingStrategy.RenderCoreTimeCritical ? PerformancePolicy.Text("专核强化", "Enhanced dedicated rendering")
                : PerformancePolicy.Text("多核协同", "Multicore coordination");
        }

        // 摘要按运行规则解析，避免显示已退役策略。
        internal static string StrategyName(CriticalThreadRule rule)
        {
            CriticalThreadRule effective = rule == null ? null : CriticalThreadRule.ParseForRuntime(rule.Encode());
            if (effective == null || effective.IsOff) return PerformancePolicy.Text("关闭", "Off");
            return StrategyName(effective.Strategy) + (effective.ClearCore ? PerformancePolicy.Text(" · 清核", " · clear core") : "");
        }

        internal static string StrategyHint(SchedulingStrategy strategy)
        {
            if (strategy == SchedulingStrategy.GamePriority)
                return PerformancePolicy.Text(
                    "提高整个游戏进程的 CPU 优先级，并固定、优先安排已识别的渲染线程，三项配合生效。CPU 满载时其他程序可能变慢。",
                    "Raises the whole game process CPU priority, and pins and prioritizes the identified render thread; the three work together. Other apps may slow down when the CPU is saturated.");
            if (strategy == SchedulingStrategy.RenderCoreTimeCritical)
                return PerformancePolicy.Text(
                    "在固定核心上进一步提高渲染优先级。须通过运行条件核验；条件失效时撤回提优。",
                    "Gives rendering stronger priority on a fixed core. Requires verified operating conditions; the boost is withdrawn if they fail.");
            if (strategy == SchedulingStrategy.DependencyBoost)
                return PerformancePolicy.Text(
                    "同时照顾渲染与已识别的关联任务，减少依赖等待。保留原有核心分配，不使用清核。",
                    "Prioritizes rendering and identified dependencies to reduce waits. Keeps existing CPU placement; clear core is unavailable.");
            if (strategy == SchedulingStrategy.RenderBoost)
                return PerformancePolicy.Text(
                    "优先安排已识别的渲染线程，减少 CPU 排队。不开清核时保留原有核心分配。",
                    "Prioritizes the identified render thread to reduce CPU queueing. Keeps existing CPU placement with clear core off.");
            if (strategy == SchedulingStrategy.RenderCore)
                return PerformancePolicy.Text(
                    "固定渲染核心，减少换核开销。可配合清核减少竞争，固定核心不代表独占。",
                    "Fixes the render core to reduce migration. Optional clear core reduces contention; fixed placement is not exclusive.");
            return PerformancePolicy.Text(
                "将已识别的关键任务分配到不同核心，减少相互竞争，为其他任务保留并行空间。",
                "Places identified critical tasks on separate cores to reduce contention and leave room for other work.");
        }

        internal static string StrategyHint(CriticalThreadRule rule)
        {
            CriticalThreadRule effective = rule == null ? null : CriticalThreadRule.ParseForRuntime(rule.Encode());
            return effective == null || effective.IsOff ? "" : StrategyHint(effective.Strategy);
        }

        // 辅助说明保留清核范围和生效条件。
        internal static string ClearCoreHint(SchedulingStrategy strategy, bool on)
        {
            if (strategy == SchedulingStrategy.DependencyBoost)
                return PerformancePolicy.Text("依赖协同保留游戏原有核心分配，不使用清核。",
                    "Dependency coordination keeps the game's CPU placement; clear core is unavailable.");
            if (!on)
                return strategy == SchedulingStrategy.RenderBoost
                    ? PerformancePolicy.Text("清核已关，保留原有核心分配。", "Clear core off; existing CPU placement is kept.")
                    : PerformancePolicy.Text("清核已关，其他游戏任务仍可使用关键核心。", "Clear core off; other game tasks can still use critical cores.");
            if (strategy == SchedulingStrategy.RenderCoreTimeCritical)
                return PerformancePolicy.Text(
                    "其他游戏任务避开渲染核心。须核验固定核心、完整清核覆盖及 Pavise 运行条件，失效时撤回提优。可能影响多线程游戏，系统任务仍可使用该核。",
                    "Moves other game tasks off the render core. Requires verified binding, full clear-core coverage and Pavise operating conditions; otherwise the boost is withdrawn. May slow heavily threaded games; system tasks can still use the core.");
            return PerformancePolicy.Text(
                "在原有范围内让其他游戏任务避开关键核心，无法调整的线程跳过。可能影响多线程游戏，系统任务仍可使用这些核心。",
                "Moves other game tasks off critical cores within their existing ranges, skipping threads that cannot be moved. May slow heavily threaded games; system tasks can still use these cores.");
        }

        internal static string Name(CriticalThreadLayout layout)
        {
            return layout == CriticalThreadLayout.Unified
                ? Text("游戏调度", "Game scheduling", "ゲームスケジュール")
                : Text("已退役布局（仅恢复）", "Retired layout (recovery only)", "廃止済み配置（復元のみ）");
        }

        internal static string Hint(CriticalThreadLayout layout)
        {
            if (layout != CriticalThreadLayout.Unified)
                return Text("旧布局不再运行，仅恢复已有记录。", "Legacy layouts no longer run; existing records are recovery-only.", "旧配置は実行せず、既存記録の復元のみ行います。");
            return PerformancePolicy.Text(
                "选择游戏优先、渲染优先、渲染专核、多核协同、依赖协同或专核强化；可单独设置清核和资源优先级。",
                "Choose a scheduling strategy, with optional clear core and resource priority.");
        }

        internal static string Help()
        {
            string separator = PerformancePolicy.Text("：", ": ");
            return StrategyName(SchedulingStrategy.GamePriority) + separator + StrategyHint(SchedulingStrategy.GamePriority)
                + "\n\n" + StrategyName(SchedulingStrategy.RenderBoost) + separator + StrategyHint(SchedulingStrategy.RenderBoost)
                + "\n\n" + StrategyName(SchedulingStrategy.RenderCore) + separator + StrategyHint(SchedulingStrategy.RenderCore)
                + "\n\n" + StrategyName(SchedulingStrategy.Orchestration) + separator + StrategyHint(SchedulingStrategy.Orchestration)
                + "\n\n" + StrategyName(SchedulingStrategy.DependencyBoost) + separator + StrategyHint(SchedulingStrategy.DependencyBoost)
                + "\n\n" + StrategyName(SchedulingStrategy.RenderCoreTimeCritical) + separator + StrategyHint(SchedulingStrategy.RenderCoreTimeCritical)
                + "\n\n" + PerformancePolicy.Text(
                "清核：让其他游戏任务避开关键核心，依赖协同不可用。可能影响多线程游戏，系统任务仍可使用这些核心。",
                "Clear core: moves other game tasks off critical cores; unavailable with Dependency coordination. May slow heavily threaded games; system tasks can still use these cores.")
                + "\n\n" + PerformancePolicy.Text(
                "资源优先级：提高游戏的 GPU 与磁盘 I/O 优先级，需要开启渲染线程调度。",
                "Resource priority: raises game GPU and disk I/O priority. Requires render thread scheduling.")
                + "\n\n" + PerformancePolicy.Text(
                "保存后下局生效，由渲染线程调度开关控制。关闭、退局或退出时恢复本方修改，保留外部后续改动。",
                "Saved changes apply next session through the scheduling switch. Disabling it, game end or app exit restores owned changes while preserving later external edits.")
                + "\n\n" + PerformancePolicy.Text(
                "效果因游戏而异，请在相同场景对照流畅度与响应。",
                "Results vary by game. Compare smoothness and response in the same scene.");
        }
    }
}
