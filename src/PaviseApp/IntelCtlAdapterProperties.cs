using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
#pragma warning disable 0649
	internal struct IntelCtlAdapterProperties
	{
		public uint Size;

		public byte Version;

		public IntPtr DeviceId;

		public uint DeviceIdSize;

		public uint DeviceType;

		public uint SupportedFunctions;

		public ulong DriverVersion;

		public ulong FirmwareMajor;

		public ulong FirmwareMinor;

		public ulong FirmwareBuild;

		public uint VendorId;

		public uint PciDeviceId;

		public uint Revision;

		public uint Eus;

		public uint SubSlices;

		public uint Slices;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 100)]
		public byte[] Name;

		public uint AdapterFlags;

		public uint Frequency;

		public ushort SubsystemId;

		public ushort SubsystemVendorId;

		public byte Bus;

		public byte Device;

		public byte Function;

		public uint XeCores;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 108)]
		public byte[] Reserved;
	}
#pragma warning restore 0649
}
