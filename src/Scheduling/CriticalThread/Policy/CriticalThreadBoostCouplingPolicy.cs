using System.Globalization;

namespace PaviseApp
{
    internal static class CriticalThreadBoostCouplingPolicy
    {

        internal static bool FrameAssociated(HardThreadChoice[] choices, CriticalThreadRoleFact[] roles)
        {
            if (choices == null || roles == null)
            {
                return false;
            }
            foreach (HardThreadChoice hardThreadChoice in choices)
            {
                if (hardThreadChoice == null || hardThreadChoice.Thread == null)
                {
                    continue;
                }
                foreach (CriticalThreadRoleFact criticalThreadRoleFact in roles)
                {
                    if (criticalThreadRoleFact != null && criticalThreadRoleFact.Tid == hardThreadChoice.Thread.Tid && criticalThreadRoleFact.Creation == hardThreadChoice.Thread.Creation)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        internal static string Text(CriticalThreadBoostCoupling value, int tid)
        {
            switch (value)
            {
            case CriticalThreadBoostCoupling.Unified:
                return ThreadLayoutDescriptions.Text("优先级与放置由统一游戏调度管理", "Priority and placement share the game scheduler", "優先度と配置は統合ゲームスケジューラーが管理します");
            case CriticalThreadBoostCoupling.Classic:
                return PerformancePolicy.Text("亲和性只做核心放置，线程优先级由这款游戏的游戏线程提优开关负责", "Affinity only places cores; thread priority comes from this game's Game thread boost switch");
            case CriticalThreadBoostCoupling.BoostOff:
                return PerformancePolicy.Text("游戏线程提优未对这款游戏运行，亲和性只做核心放置", "Game thread boost is not running for this game; affinity only places cores");
            case CriticalThreadBoostCoupling.BoostDisabled:
                return PerformancePolicy.Text("游戏资源优先级已关闭，线程提优不运行", "Game resource priority is off, so thread boost does not run");
            case CriticalThreadBoostCoupling.CandidateUnsupported:
                return PerformancePolicy.Text("高负载线程提优在掌机档不运行", "Busy-thread boost does not run in Handheld");
            case CriticalThreadBoostCoupling.CandidateSelected:
                return PerformancePolicy.Text("线程提优：高负载线程", "Thread boost: busy threads");
            case CriticalThreadBoostCoupling.FrameSelected:
                return PerformancePolicy.Text("线程提优：渲染关键线程", "Thread boost: render threads");
            case CriticalThreadBoostCoupling.BoostInactive:
                return PerformancePolicy.Text("高负载线程提优当前未作用于本游戏", "Busy-thread boost is not applied to this game now");
            case CriticalThreadBoostCoupling.SameThread:
            {
                string tidText = tid.ToString(CultureInfo.InvariantCulture);
                return PerformancePolicy.Text("TID " + tidText + " 同时被高负载线程提优到最高优先级；两者叠加的效果未经测试", "TID " + tidText + " is also at highest priority from busy-thread boost; the combined effect is untested");
            }
            case CriticalThreadBoostCoupling.OtherThread:
                return PerformancePolicy.Text("高负载线程提优作用于另一条线程", "Busy-thread boost is on a different thread");
            case CriticalThreadBoostCoupling.FrameAssociated:
                return PerformancePolicy.Text("渲染关键线程提优已开启，关联候选可能同时被提优；叠加效果未经测试", "Render-thread boost is on; associated candidates may also be boosted; the combined effect is untested");
            case CriticalThreadBoostCoupling.FrameOther:
                return PerformancePolicy.Text("渲染关键线程提优已开启，作用于它自己识别的线程", "Render-thread boost is on for the threads it identifies");
            default:
                return "";
            }
        }
    }
}
