using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
#pragma warning disable 0649
	internal struct IntelCtlFeatureValue
	{
		public uint Size;

		public byte Version;

		public uint FeatureType;

		public IntPtr ApplicationName;

		public sbyte ApplicationNameLength;

		[MarshalAs(UnmanagedType.I1)]
		public bool Set;

		public uint ValueType;

		public IntelCtlPropertyValue Value;

		public int CustomValueSize;

		public IntPtr CustomValue;
	}
#pragma warning restore 0649
}
