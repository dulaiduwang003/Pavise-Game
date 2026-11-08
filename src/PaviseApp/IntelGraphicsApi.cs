using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
	internal sealed class IntelGraphicsApi
	{
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate uint FnInit(ref IntelCtlInit args, out IntPtr handle);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate uint FnClose(IntPtr handle);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate uint FnEnumerate(IntPtr handle, ref uint count, IntPtr devices);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate uint FnProperties(IntPtr device, ref IntelCtlAdapterProperties value);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate uint FnFeature(IntPtr device, ref IntelCtlFeatureValue value);

		private sealed class NativeAdapter
		{
			internal IntPtr Handle;

			internal IntelGraphicsAdapter Description;
		}

		internal const uint EnduranceFeature = 1u;

		internal const uint CustomValueType = 5u;

		private const int EnduranceBlockBytes = 8;

		private const uint SearchSystem32 = 2048u;

		private static readonly IntelGraphicsApi availabilityApi = new IntelGraphicsApi();

		private readonly object gate = new object();

		private bool attempted;

		private IntPtr module;

		private IntPtr api;

		private FnClose close;

		private FnEnumerate enumerate;

		private FnProperties properties;

		private FnFeature feature;

		private List<NativeAdapter> cachedDevices;

		private int cachedTick;

		internal static bool HasAvailable
		{
			get
			{
				IntelGraphicsAdapter[] adapters;
				if (!availabilityApi.TryGetAdapters(out adapters) || adapters == null)
				{
					return false;
				}
				IntelGraphicsAdapter[] adaptersSnapshot = adapters;
				foreach (IntelGraphicsAdapter intelGraphicsAdapter in adaptersSnapshot)
				{
					if (intelGraphicsAdapter != null && intelGraphicsAdapter.VendorId == 32902)
					{
						return true;
					}
				}
				return false;
			}
		}

		public bool TryGetAdapters(out IntelGraphicsAdapter[] adapters)
		{
			adapters = null;
			lock (gate)
			{
				try
				{
					RefuseNativeInTests();
					List<NativeAdapter> result = cachedDevices;
					if ((result == null || (uint)(Environment.TickCount - cachedTick) >= 5000u) && !Enumerate(out result))
					{
						return false;
					}
					List<IntelGraphicsAdapter> list = new List<IntelGraphicsAdapter>();
					foreach (NativeAdapter item in result)
					{
						IntelGraphicsAdapter description = item.Description;
						list.Add(new IntelGraphicsAdapter
						{
							Id = description.Id,
							Name = description.Name,
							VendorId = description.VendorId,
							Integrated = description.Integrated
						});
					}
					adapters = list.ToArray();
					return true;
				}
				catch
				{
					return false;
				}
			}
		}

		public bool TryReadEndurance(out int control, out int mode)
		{
			control = -1;
			mode = -1;
			lock (gate)
			{
				try
				{
					RefuseNativeInTests();
					List<NativeAdapter> result = cachedDevices;
					if ((result == null || (uint)(Environment.TickCount - cachedTick) >= 5000u) && !Enumerate(out result))
					{
						return false;
					}
					foreach (NativeAdapter item in result)
					{
						int outControl;
						int outMode;
						if (item.Description.VendorId == 32902 && EnduranceCall(item.Handle, false, 0, 0, out outControl, out outMode))
						{
							control = outControl;
							mode = outMode;
							return true;
						}
					}
					return false;
				}
				catch
				{
					return false;
				}
			}
		}

		public bool TryWriteEndurance(int control, int mode)
		{
			lock (gate)
			{
				try
				{
					RefuseNativeInTests();
					List<NativeAdapter> result;
					if (!Enumerate(out result))
					{
						return false;
					}
					bool result2 = false;
					foreach (NativeAdapter item in result)
					{
						int outControl;
						int outMode;
						if (item.Description.VendorId == 32902 && EnduranceCall(item.Handle, true, control, mode, out outControl, out outMode))
						{
							result2 = true;
						}
					}
					return result2;
				}
				catch
				{
					return false;
				}
			}
		}

		private bool EnduranceCall(IntPtr device, bool set, int control, int mode, out int outControl, out int outMode)
		{
			outControl = -1;
			outMode = -1;
			IntPtr intPtr = AllocateZeroed(8);
			try
			{
				if (set)
				{
					Marshal.WriteInt32(intPtr, 0, control);
					Marshal.WriteInt32(intPtr, 4, mode);
				}
				IntelCtlFeatureValue value = EnduranceRequest(set, intPtr);
				if (feature(device, ref value) != 0)
				{
					return false;
				}
				outControl = Marshal.ReadInt32(intPtr, 0);
				outMode = Marshal.ReadInt32(intPtr, 4);
				return outControl >= 0 && outControl <= 2;
			}
			catch
			{
				return false;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		internal static IntelCtlFeatureValue EnduranceRequest(bool set, IntPtr block)
		{
			IntelCtlFeatureValue result = default(IntelCtlFeatureValue);
			result.Size = (uint)Marshal.SizeOf(typeof(IntelCtlFeatureValue));
			result.FeatureType = 1u;
			result.ValueType = 5u;
			result.Set = set;
			result.CustomValueSize = 8;
			result.CustomValue = block;
			return result;
		}

		private bool Enumerate(out List<NativeAdapter> result)
		{
			result = null;
			cachedDevices = null;
			if (!Initialize())
			{
				return false;
			}
			uint count = 0u;
			if (enumerate(api, ref count, IntPtr.Zero) != 0 || count > 32)
			{
				return false;
			}
			result = new List<NativeAdapter>();
			if (count == 0)
			{
				cachedDevices = result;
				cachedTick = Environment.TickCount;
				return true;
			}
			IntPtr intPtr = AllocateZeroed(checked((int)count * IntPtr.Size));
			try
			{
				uint countSnapshot = count;
				if (enumerate(api, ref count, intPtr) != 0 || count > countSnapshot)
				{
					return false;
				}
				for (int i = 0; i < count; i++)
				{
					IntPtr intPtr2 = Marshal.ReadIntPtr(intPtr, i * IntPtr.Size);
					if (intPtr2 == IntPtr.Zero)
					{
						return false;
					}
					IntelCtlAdapterProperties info;
					if (!ReadProperties(intPtr2, out info))
					{
						return false;
					}
					if (info.DeviceType == 1)
					{
						string adapterIdentityResult = ((info.VendorId == 32902) ? AdapterIdentity(info) : ("other:" + info.VendorId.ToString("X", CultureInfo.InvariantCulture) + ":" + i));
						if (adapterIdentityResult == null)
						{
							return false;
						}
						List<NativeAdapter> resultSnapshot = result;
						NativeAdapter nativeAdapter = new NativeAdapter
						{
							Handle = intPtr2
						};
						IntelGraphicsAdapter intelGraphicsAdapter = new IntelGraphicsAdapter
						{
							Id = adapterIdentityResult
						};
						string @string = Encoding.ASCII.GetString(info.Name);
						char[] trimChars = new char[1];
						intelGraphicsAdapter.Name = @string.TrimEnd(trimChars);
						intelGraphicsAdapter.VendorId = info.VendorId;
						intelGraphicsAdapter.Integrated = (info.AdapterFlags & 1) != 0;
						nativeAdapter.Description = intelGraphicsAdapter;
						resultSnapshot.Add(nativeAdapter);
					}
				}
				cachedDevices = result;
				cachedTick = Environment.TickCount;
				return true;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		private bool ReadProperties(IntPtr handle, out IntelCtlAdapterProperties info)
		{
			info = default(IntelCtlAdapterProperties);
			IntPtr intPtr = AllocateZeroed(8);
			try
			{
				info = new IntelCtlAdapterProperties
				{
					Size = (uint)Marshal.SizeOf(typeof(IntelCtlAdapterProperties)),
					Version = 2,
					DeviceId = intPtr,
					DeviceIdSize = 8u,
					Name = new byte[100],
					Reserved = new byte[108]
				};
				return properties(handle, ref info) == 0 && info.Size == (uint)Marshal.SizeOf(typeof(IntelCtlAdapterProperties)) && info.Name != null && info.Name.Length == 100;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		internal static string AdapterIdentity(IntelCtlAdapterProperties info)
		{
			if (info.Version < 2 || info.VendorId == 0 || info.VendorId > 65535 || info.PciDeviceId == 0 || info.PciDeviceId > 65535 || info.Device > 31 || info.Function > 7 || info.DriverVersion == 0)
			{
				return null;
			}
			return string.Format(CultureInfo.InvariantCulture, "{0:X4}:{1:X4}:{2:X4}:{3:X4}:{4:X2}:{5:X2}:{6:X2}:{7:X16}", info.VendorId, info.PciDeviceId, info.SubsystemVendorId, info.SubsystemId, info.Bus, info.Device, info.Function, info.DriverVersion);
		}

		private bool Initialize()
		{
			RefuseNativeInTests();
			if (attempted)
			{
				return api != IntPtr.Zero;
			}
			attempted = true;
			module = LoadLibraryExW((IntPtr.Size == 8) ? "ControlLib.dll" : "ControlLib32.dll", IntPtr.Zero, 2048u);
			if (module == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				FnInit fnInit = Function<FnInit>("ctlInit");
				close = Function<FnClose>("ctlClose");
				enumerate = Function<FnEnumerate>("ctlEnumerateDevices");
				properties = Function<FnProperties>("ctlGetDeviceProperties");
				feature = Function<FnFeature>("ctlGetSet3DFeature");
				IntelCtlInit intelCtlInit = default(IntelCtlInit);
				intelCtlInit.Size = (uint)Marshal.SizeOf(typeof(IntelCtlInit));
				intelCtlInit.AppVersion = 65537u;
				IntelCtlInit args = intelCtlInit;
				IntPtr handle;
				if (fnInit(ref args, out handle) != 0 || handle == IntPtr.Zero)
				{
					return false;
				}
				api = handle;
				AppDomain.CurrentDomain.ProcessExit += CloseAtExit;
				return true;
			}
			finally
			{
				if (api == IntPtr.Zero)
				{
					FreeLibrary(module);
					module = IntPtr.Zero;
				}
			}
		}

		private T Function<T>(string name) where T : class
		{
			IntPtr procAddress = GetProcAddress(module, name);
			if (procAddress == IntPtr.Zero)
			{
				throw new MissingMethodException(name);
			}
			return (T)(object)Marshal.GetDelegateForFunctionPointer(procAddress, typeof(T));
		}

		private void CloseAtExit(object sender, EventArgs args)
		{
			lock (gate)
			{
				try
				{
					RefuseNativeInTests();
					if (api != IntPtr.Zero && close(api) == 0)
					{
						api = IntPtr.Zero;
						FreeLibrary(module);
						module = IntPtr.Zero;
					}
				}
				catch
				{
				}
			}
		}

		private static IntPtr AllocateZeroed(int bytes)
		{
			byte[] source = new byte[bytes];
			IntPtr intPtr = Marshal.AllocHGlobal(bytes);
			try
			{
				Marshal.Copy(source, 0, intPtr, bytes);
				return intPtr;
			}
			catch
			{
				Marshal.FreeHGlobal(intPtr);
				throw;
			}
		}

		private static void RefuseNativeInTests()
		{
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
		private static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);

		[DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
		private static extern IntPtr GetProcAddress(IntPtr library, string name);

		[DllImport("kernel32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool FreeLibrary(IntPtr library);
	}
}
