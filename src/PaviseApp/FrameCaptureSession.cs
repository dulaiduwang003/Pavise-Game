namespace PaviseApp
{
	internal sealed class FrameCaptureSession
	{
		internal readonly int Pid;

		internal readonly long Birth;

		internal readonly FrameCoordinationRetry Retry = new FrameCoordinationRetry();

		internal bool PresenterOnly;

		internal bool Stopped;

		internal bool NoChangeNeeded;

		internal int Failures;

		internal FrameCaptureSession(int pid, long birth)
		{
			Pid = pid;
			Birth = birth;
		}
	}
}
