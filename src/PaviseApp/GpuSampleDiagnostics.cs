using System.Globalization;

namespace PaviseApp
{
	internal sealed class GpuSampleDiagnostics
	{
		internal string FailureStage;

		internal uint FailureStatus;

		internal string ExceptionType;

		internal string UnavailableStage;

		internal uint UnavailableStatus;

		internal void Unavailable(string stage, uint status)
		{
			UnavailableStage = stage;
			UnavailableStatus = status;
		}

		internal int CollectedRounds;

		internal int ValidRounds;

		internal int Instances;

		internal int RejectedInstances;

		internal int TargetPid;

		internal int TargetInstances;

		internal int TargetRejectedInstances;

		internal uint TargetStatus;

		internal void Failure(string stage, uint status)
		{
			FailureStage = stage;
			FailureStatus = status;
		}

		public override string ToString()
		{
			return string.Format(CultureInfo.InvariantCulture, "lastFailure={0} code=0x{1:X8} collected={2} valid={3} instances={4} rejected={5}{6} targetInstances={7} targetRejected={8} targetCode=0x{9:X8} unavailable={10} unavailableCode=0x{11:X8}", FailureStage ?? "none", FailureStatus, CollectedRounds, ValidRounds, Instances, RejectedInstances, (ExceptionType == null) ? "" : (" exception=" + ExceptionType), TargetInstances, TargetRejectedInstances, TargetStatus, UnavailableStage ?? "none", UnavailableStatus);
		}
	}
}
