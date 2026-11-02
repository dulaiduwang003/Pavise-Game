namespace PaviseApp
{
	internal sealed class IrqDevice
	{
		public string InstanceId = "";

		public string Name = "";

		public string Service = "";

		public string Bus = "";

		public string ClassGuid = "";

		public string DriverVersion = "";

		public int Policy;

		public ulong Mask;

		internal bool ConfigurationKnown;

		public int MessageCount;

		public bool MultiMessageRisk;

		public bool IsPinned
		{
			get
			{
				if (Policy == 4)
				{
					return Mask != 0;
				}
				return false;
			}
		}
	}
}
