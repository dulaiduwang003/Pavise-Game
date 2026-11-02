namespace PaviseApp
{
	internal sealed class GpuIrqSample
	{
		public string DeviceId = "";

		public string Name = "";

		public string Driver = "";

		public string DriverVersion = "";

		public ulong Seen;

		public long Events;

		public double P99Us;

		public double MaxUs;

		public bool MultiMessage;

		public bool ExternalPin;

		public bool ConfigurationKnown = true;
	}
}
