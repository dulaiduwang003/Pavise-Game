using System;

namespace PaviseApp
{
	internal sealed class CriticalThreadAllowedRange
	{
		internal readonly ulong Mask;

		internal readonly string Error;

		private CriticalThreadAllowedRange(ulong mask, string error)
		{
			Mask = mask;
			Error = error;
		}

		internal static CriticalThreadAllowedRange For(GameProfile profile)
		{
			if (profile == null)
			{
				return new CriticalThreadAllowedRange(0uL, PerformancePolicy.Text("游戏已移除", "The game was removed"));
			}
			ulong allMask = CpuTopology.AllMask;
			ulong reserved = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;
			bool trusted = IntPtr.Size == 8 && !CpuTopology.MultiGroup && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled;
			return FromProfile(profile, allMask, CpuTopology.PhysicalCoreMasks(), reserved, AutoCores.Supported, trusted);
		}

		internal static CriticalThreadAllowedRange FromProfile(GameProfile profile, ulong all, ulong[] physical, ulong reserved, bool supported, bool trusted)
		{
			if (profile == null)
			{
				return new CriticalThreadAllowedRange(0uL, PerformancePolicy.Text("游戏已移除", "The game was removed"));
			}
			PolicySnapshot policySnapshot = PolicyResolver.For(profile);
			return Evaluate(policySnapshot.ValueOf("GmAutoCoresV2") == "1", policySnapshot.ManualCores, all, physical, reserved, supported, trusted);
		}

		internal static CriticalThreadAllowedRange Evaluate(bool enabled, ManualCorePlan manual, ulong all, ulong[] physical, ulong reserved, bool supported, bool trusted)
		{
			if (!trusted)
			{
				return new CriticalThreadAllowedRange(0uL, PerformancePolicy.Text("处理器拓扑或处理器组暂不支持", "CPU topology or processor groups are unsupported"));
			}
			if (!enabled)
			{
				return new CriticalThreadAllowedRange(all, null);
			}
			if (manual == null)
			{
				return new CriticalThreadAllowedRange(0uL, PerformancePolicy.Text("已启用的游戏选核规则无效", "The enabled game processor rule is invalid"));
			}
			string validateResult = manual.Validate(all, physical, reserved, supported, trusted);
			if (validateResult != null)
			{
				return new CriticalThreadAllowedRange(0uL, PerformancePolicy.Text("请先修正或关闭原游戏选核规则：", "Correct or disable the original game processor rule first: ") + Lang.T(validateResult));
			}
			return new CriticalThreadAllowedRange(manual.GameMask, null);
		}
	}
}
