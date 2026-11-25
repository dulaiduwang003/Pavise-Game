using System.Collections.Generic;

namespace PaviseApp
{
    internal static class ThreadLayoutDescriptions
    {
        internal static string Text(string zh, string en, string ja)
        { return Lang.Cur == 2 ? ja : Lang.Text(zh, en); }

        internal static string SingleCoreName()
        {
            return PerformancePolicy.Text("渲染单核", "Single render core");
        }

        // 摘要按运行规则解析 旧方案显示换算后的模式 手动显示所选 CPU
        internal static string StrategyName(CriticalThreadRule rule)
        {
            CriticalThreadRule effective = rule == null ? null : CriticalThreadRule.ParseForRuntime(rule.Encode());
            if (effective == null || effective.IsOff) return PerformancePolicy.Text("关闭", "Off");
            string avoid = PerformancePolicy.Text(" · 整核避让", " · whole-core avoidance");
            if (effective.IsManualPin)
                return PerformancePolicy.Text("手动 · ", "Manual · ") + "CPU" + CpuTopology.DescribeRanges(effective.ManualMask) + (effective.ClearCore ? avoid : "");
            return SingleCoreName() + (effective.ClearCore ? avoid : "");
        }

        internal static string SingleCoreHint()
        {
            return PerformancePolicy.Text(
                "从画面提交事件识别渲染线程，把它固定到一颗最合适的物理核，减少换核。GPU 瓶颈的游戏也会固定。",
                "Identifies the render thread from frame presentation events and pins it to the most suitable physical core to reduce migration. This also applies to GPU-bound games.");
        }

        internal static string ManualHint()
        {
            return PerformancePolicy.Text(
                "从画面提交事件识别渲染线程并固定到你选的一到四个逻辑 CPU 上。处理器或游戏核心范围变了需要重选。",
                "Identifies the render thread from frame presentation events and pins it to the one to four logical CPUs you choose. Choose again if the processor or the game's CPU range changes.");
        }

        internal static string StrategyHint(CriticalThreadRule rule)
        {
            CriticalThreadRule effective = rule == null ? null : CriticalThreadRule.ParseForRuntime(rule.Encode());
            if (effective == null || effective.IsOff) return "";
            return effective.IsManualPin ? ManualHint() : SingleCoreHint();
        }

        // 整核避让只移动游戏线程
        internal static string ClearCoreHint(bool on)
        {
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
                "选择渲染单核或手动。亲和性只决定线程在哪些核心上跑，不改优先级；优先级由深度调优里的性能优先级和游戏线程提优负责。",
                "Choose Single render core or Manual. Affinity only decides which cores threads run on; priority comes from Performance priority and Game thread boost in Advanced tuning.");
        }

        internal static string Help()
        {
            string separator = PerformancePolicy.Text("：", ": ");
            var parts = new List<string>
            {
                SingleCoreName() + separator + SingleCoreHint(),
                PerformancePolicy.Text("手动", "Manual") + separator + ManualHint(),
                PerformancePolicy.Text(
                    "整核避让：让其他游戏线程避开渲染线程所在的整颗物理核，渲染单核和手动均可开启。可能影响非常依赖多线程的游戏，系统任务仍可使用这些核心。",
                    "Whole-core avoidance: keeps other game threads off the render thread's whole physical core; optional for Single render core and Manual. May slow heavily threaded games; system tasks can still use these cores."),
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
