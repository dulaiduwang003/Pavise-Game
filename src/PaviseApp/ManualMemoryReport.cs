using System.Collections.Generic;

namespace PaviseApp
{
	internal sealed class ManualMemoryReport
	{
		internal readonly List<ManualMemoryStepResult> Steps = new List<ManualMemoryStepResult>();

		internal ManualMemorySnapshot Before;

		internal ManualMemorySnapshot After;

		internal bool Busy;

		internal bool Cancelled;

		internal bool GameProtected;

		internal bool EscalatedToGameProtection;

		internal int ExpectedSteps = ManualMemoryCleaner.OrderedSteps.Length;

		internal bool Completed
		{
			get
			{
				if (Busy || Cancelled) return false;
				if (!GameProtected) return SucceededCount == ExpectedSteps;
				foreach (ManualMemoryStepResult step in Steps)
					if (step.Step == ManualMemoryStep.PaviseMemory && step.State == ManualMemoryStepState.Succeeded) return true;
				return false;
			}
		}

		internal long ElapsedMilliseconds;

		internal int SucceededCount
		{
			get
			{
				int processedCount = 0;
				foreach (ManualMemoryStepResult step in Steps)
				{
					if (step.State == ManualMemoryStepState.Succeeded)
					{
						processedCount++;
					}
				}
				return processedCount;
			}
		}

		internal long? AvailableChangeBytes
		{
			get
			{
				if (Before == null || After == null || !Before.IsValid || !After.IsValid || Before.TotalBytes != After.TotalBytes || Before.TotalBytes > long.MaxValue)
				{
					return null;
				}
				return (long)(After.AvailableBytes - Before.AvailableBytes);
			}
		}
	}
}
