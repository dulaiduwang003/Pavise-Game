using System.Globalization;

namespace PaviseApp
{
	internal static class CriticalThreadBoostCouplingPolicy
	{
		internal static CriticalThreadBoostCoupling Classify(bool boostEnabled, string mode, bool laneEligible, int pid, long birth, CriticalThreadLaneThread lane, HardThreadIdentity[] candidates, bool candidateFrameAssociated, out int sameTid)
		{
			sameTid = 0;
			if (mode == "off")
			{
				return CriticalThreadBoostCoupling.BoostOff;
			}
			if (mode != "candidate" && mode != "frame")
			{
				return CriticalThreadBoostCoupling.Unknown;
			}
			if (!boostEnabled)
			{
				return CriticalThreadBoostCoupling.BoostDisabled;
			}
			bool allConditionsMet = candidates != null && candidates.Length > 0;
			if (mode == "frame")
			{
				if (allConditionsMet)
				{
					if (!candidateFrameAssociated)
					{
						return CriticalThreadBoostCoupling.FrameOther;
					}
					return CriticalThreadBoostCoupling.FrameAssociated;
				}
				return CriticalThreadBoostCoupling.FrameSelected;
			}
			if (!laneEligible)
			{
				return CriticalThreadBoostCoupling.CandidateUnsupported;
			}
			if (!allConditionsMet)
			{
				return CriticalThreadBoostCoupling.CandidateSelected;
			}
			if (lane == null || pid <= 0 || birth <= 0 || lane.Pid != pid || lane.ProcessBirth != birth)
			{
				return CriticalThreadBoostCoupling.BoostInactive;
			}
			if (lane.Tid <= 0 || lane.ThreadBirth <= 0)
			{
				return CriticalThreadBoostCoupling.Unknown;
			}
			foreach (HardThreadIdentity hardThreadIdentity in candidates)
			{
				if (hardThreadIdentity != null && hardThreadIdentity.Tid == lane.Tid && hardThreadIdentity.Creation == lane.ThreadBirth)
				{
					sameTid = hardThreadIdentity.Tid;
					return CriticalThreadBoostCoupling.SameThread;
				}
			}
			return CriticalThreadBoostCoupling.OtherThread;
		}

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
			case CriticalThreadBoostCoupling.BoostOff:
				return PerformancePolicy.Text("线程提优：关闭", "Thread boost: off");
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
