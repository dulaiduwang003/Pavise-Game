using System;

namespace PaviseApp
{
	internal struct IntelCtlInit
	{
		public uint Size;

		public byte Version;

		public uint AppVersion;

		public uint Flags;

		public uint SupportedVersion;

		public Guid ApplicationId;
	}
}
