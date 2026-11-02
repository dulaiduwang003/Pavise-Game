namespace PaviseApp
{
	internal sealed class HardwareSchedulingObservation
	{
		internal readonly HardwareSchedulingState State;

		internal readonly HardwareSchedulingProbeReason Reason;

		internal readonly string RendererLuid;

		internal readonly string Detail;

		internal readonly ushort? FeatureVersion;

		internal readonly ushort? RawResultBits;

		internal readonly int? NativeStatus;

		internal bool IsKnown
		{
			get
			{
				return State != HardwareSchedulingState.ProbeUnavailable;
			}
		}

		internal bool? SupportedByDriver
		{
			get
			{
				if (!IsKnown)
				{
					return null;
				}
				return (RawResultBits.Value & 4) != 0;
			}
		}

		internal bool? SupportedOnCurrentConfig
		{
			get
			{
				if (!IsKnown)
				{
					return null;
				}
				return (RawResultBits.Value & 8) != 0;
			}
		}

		internal HardwareSchedulingObservation(HardwareSchedulingState state, HardwareSchedulingProbeReason reason, string luid, string detail, ushort? version, ushort? bits, int? status)
		{
			State = state;
			Reason = reason;
			RendererLuid = luid;
			Detail = detail;
			FeatureVersion = version;
			RawResultBits = bits;
			NativeStatus = status;
		}
	}
}
