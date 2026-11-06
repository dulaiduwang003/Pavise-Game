using System;

namespace PaviseApp
{
#pragma warning disable 0649 // written by native marshaling, never by managed code
	internal struct IntelCtlInit
	{
		public uint Size;

		public byte Version;

		public uint AppVersion;

		public uint Flags;

		public uint SupportedVersion;

		public Guid ApplicationId;
	}
#pragma warning restore 0649
}
