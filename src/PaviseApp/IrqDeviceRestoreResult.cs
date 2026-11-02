namespace PaviseApp
{
	internal sealed class IrqDeviceRestoreResult
	{
		internal string DeviceId;

		internal bool AffinityRequested;

		internal bool PriorityRequested;

		internal bool Affinity;

		internal bool Priority;

		internal bool Success
		{
			get
			{
				if (!AffinityRequested || Affinity)
				{
					if (PriorityRequested)
					{
						return Priority;
					}
					return true;
				}
				return false;
			}
		}

		internal string AffinityResult
		{
			get
			{
				if (AffinityRequested)
				{
					if (!Affinity)
					{
						return "failed";
					}
					return "ok";
				}
				return "skipped";
			}
		}

		internal string PriorityResult
		{
			get
			{
				if (PriorityRequested)
				{
					if (!Priority)
					{
						return "failed";
					}
					return "ok";
				}
				return "skipped";
			}
		}
	}
}
