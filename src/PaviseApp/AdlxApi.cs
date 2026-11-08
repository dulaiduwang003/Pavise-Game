using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class AdlxApi
	{
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int FnInitialize(ulong version, out IntPtr system);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int FnTerminate();

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnIntProp(IntPtr self);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate uint FnUIntProp(IntPtr self);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnOutPtr(IntPtr self, out IntPtr value);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnGpuOutPtr(IntPtr self, IntPtr gpu, out IntPtr value);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnAtList(IntPtr self, uint location, out IntPtr item);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnOutByte(IntPtr self, out byte value);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnInByte(IntPtr self, byte value);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnOutInt(IntPtr self, out int value);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnInInt(IntPtr self, int value);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnOutDouble(IntPtr self, out double value);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnOutRange(IntPtr self, out AdlxIntRange range);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnAction(IntPtr self);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnQueryIface(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string interfaceId, out IntPtr value);

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnGpuOutByte(IntPtr self, IntPtr gpu, out byte value);

#pragma warning disable 0649
		private struct GpuLuid
		{
			internal uint Low;

			internal int High;
		}
#pragma warning restore 0649

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		private delegate int FnOutLuid(IntPtr self, out GpuLuid value);

		private const ulong FullVersion = 281496451547260uL;

		private const uint LoadSearchFlags = 7680u;

		public const int GpuTypeIntegrated = 1;

		public const int GpuTypeDiscrete = 2;

		private const int SysSlotGetGpus = 1;

		private const int SysSlotGet3DServices = 7;

		private const int SysSlotGetPerfServices = 9;

		private const int IfaceSlotRelease = 1;

		private const int IfaceSlotQuery = 2;

		private const int ListSlotBegin = 5;

		private const int ListSlotEnd = 6;

		private const int GpuListSlotAt = 11;

		private const int GpuSlotVendorId = 3;

		private const int GpuSlotType = 5;

		private const int GpuSlotName = 7;

		private const int GpuSlotPnp = 9;

		private const int SysSlotGetTuningServices = 8;

		private const int SvcSlotAntiLag = 3;

		private const int SvcSlotChill = 4;

		private const int SvcSlotImageSharpening = 6;

		private const int SvcSlotEnhancedSync = 7;

		private const int SvcSlotFrtc = 9;

		private const int SvcSlotRsr = 14;

		private const int SvcSlotResetShaderCache = 15;

		private const int Svc1SlotAfmf = 17;

		private const int RsrSlotSetEnabled = 5;

		private const int RsrSlotGetSharpness = 7;

		private const int RsrSlotSetSharpness = 8;

		private const int TuneSlotIsSupportedManualPower = 11;

		private const int TuneSlotGetManualPower = 17;

		private const int TuneSlotIsSupportedManualGfx = 8;

		private const int TuneSlotGetManualGfx = 14;

		private const int Tune1SlotGetSam = 18;

		private const string TuningServices1Iid = "IADLXGPUTuningServices1";

		private const string ManualGfx2Iid = "IADLXManualGraphicsTuning2";

		private const int Gfx2SlotGetMinRange = 3;

		private const int Gfx2SlotGetMin = 4;

		private const int Gfx2SlotSetMin = 5;

		private const int Gfx2SlotGetMax = 7;

		private const int PowerSlotGetRange = 3;

		private const int PowerSlotGetLimit = 4;

		private const int PowerSlotSetLimit = 5;

		private const string Services1Iid = "IADLX3DSettingsServices1";

		private const int FeatSlotIsSupported = 3;

		private const int FeatSlotIsEnabled = 4;

		private const int ToggleSlotSetEnabled = 5;

		private const int ChillSlotGetFpsRange = 5;

		private const int ChillSlotGetMinFps = 6;

		private const int ChillSlotGetMaxFps = 7;

		private const int ChillSlotSetEnabled = 8;

		private const int ChillSlotSetMinFps = 9;

		private const int ChillSlotSetMaxFps = 10;

		private const int FrtcSlotGetFpsRange = 5;

		private const int FrtcSlotGetFps = 6;

		private const int FrtcSlotSetEnabled = 7;

		private const int FrtcSlotSetFps = 8;

		private const int RisSlotGetRange = 5;

		private const int RisSlotGetSharpness = 6;

		private const int RisSlotSetEnabled = 7;

		private const int RisSlotSetSharpness = 8;

		private const int CacheSlotReset = 4;

		private const int PerfSlotCurrentGpuMetrics = 18;

		private const int PerfSlotSupportedGpuMetrics = 21;

		private const int Gpu2SlotLuid = 34;

		private static readonly object lk = new object();

		private static int state;

		private static IntPtr system;

		private static FnTerminate terminate;

		public static bool Available
		{
			get
			{
				lock (lk)
				{
					if (state != 0)
					{
						return state > 0;
					}
					state = (Probe() ? 1 : (-1));
					return state > 0;
				}
			}
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr LoadLibraryExW(string name, IntPtr reserved, uint flags);

		[DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
		private static extern IntPtr GetProcAddress(IntPtr module, string name);

		public static bool Succeeded(int result)
		{
			if (result >= 0)
			{
				return result <= 2;
			}
			return false;
		}

		private static bool Probe()
		{
			try
			{
				IntPtr intPtr = LoadLibraryExW("amdadlx64.dll", IntPtr.Zero, 7680u);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				IntPtr procAddress = GetProcAddress(intPtr, "ADLXInitialize");
				IntPtr procAddress2 = GetProcAddress(intPtr, "ADLXTerminate");
				if (procAddress == IntPtr.Zero || procAddress2 == IntPtr.Zero)
				{
					return false;
				}
				FnInitialize fnInitialize = (FnInitialize)Marshal.GetDelegateForFunctionPointer(procAddress, typeof(FnInitialize));
				terminate = (FnTerminate)Marshal.GetDelegateForFunctionPointer(procAddress2, typeof(FnTerminate));
				IntPtr intPtr2;
				int num = fnInitialize(281496451547260uL, out intPtr2);
				if (!Succeeded(num) || intPtr2 == IntPtr.Zero)
				{
					Logger.Warn(Lang.T("log.adlxapi.1") + num + Lang.T("log.adlxapi.2"));
					return false;
				}
				system = intPtr2;
				AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static void OnProcessExit(object sender, EventArgs e)
		{
			try
			{
				if (terminate != null)
				{
					terminate();
				}
			}
			catch
			{
			}
		}

		private static T VMethod<T>(IntPtr obj, int slot) where T : class
		{
			IntPtr ptr = Marshal.ReadIntPtr(obj);
			IntPtr ptr2 = Marshal.ReadIntPtr(ptr, slot * IntPtr.Size);
			return (T)(object)Marshal.GetDelegateForFunctionPointer(ptr2, typeof(T));
		}

		public static void Release(IntPtr iface)
		{
			if (iface == IntPtr.Zero)
			{
				return;
			}
			try
			{
				VMethod<FnIntProp>(iface, 1)(iface);
			}
			catch
			{
			}
		}

		public static IntPtr[] GetGpus()
		{
			if (!Available)
			{
				return null;
			}
			try
			{
				IntPtr value;
				if (!Succeeded(VMethod<FnOutPtr>(system, 1)(system, out value)) || value == IntPtr.Zero)
				{
					return null;
				}
				try
				{
					uint num = VMethod<FnUIntProp>(value, 5)(value);
					uint num2 = VMethod<FnUIntProp>(value, 6)(value);
					if (num2 <= num || num2 - num > 16)
					{
						return new IntPtr[0];
					}
					FnAtList fnAtList = VMethod<FnAtList>(value, 11);
					List<IntPtr> list = new List<IntPtr>();
					for (uint num3 = num; num3 < num2; num3++)
					{
						IntPtr item;
						if (Succeeded(fnAtList(value, num3, out item)) && item != IntPtr.Zero)
						{
							list.Add(item);
						}
					}
					return list.ToArray();
				}
				finally
				{
					Release(value);
				}
			}
			catch
			{
				return null;
			}
		}

		public static void ReleaseAll(IntPtr[] interfaces)
		{
			if (interfaces != null)
			{
				foreach (IntPtr iface in interfaces)
				{
					Release(iface);
				}
			}
		}

		public static string GpuName(IntPtr gpu)
		{
			try
			{
				IntPtr value;
				if (!Succeeded(VMethod<FnOutPtr>(gpu, 7)(gpu, out value)) || value == IntPtr.Zero)
				{
					return null;
				}
				return Marshal.PtrToStringAnsi(value);
			}
			catch
			{
				return null;
			}
		}

		public static int GpuType(IntPtr gpu)
		{
			try
			{
				int value;
				return Succeeded(VMethod<FnOutInt>(gpu, 5)(gpu, out value)) ? value : 0;
			}
			catch
			{
				return 0;
			}
		}

		public static string GpuPnpString(IntPtr gpu)
		{
			try
			{
				IntPtr value;
				if (!Succeeded(VMethod<FnOutPtr>(gpu, 9)(gpu, out value)) || value == IntPtr.Zero)
				{
					return null;
				}
				return Marshal.PtrToStringAnsi(value);
			}
			catch
			{
				return null;
			}
		}

		public static string GpuVendor(IntPtr gpu)
		{
			try
			{
				IntPtr value;
				if (!Succeeded(VMethod<FnOutPtr>(gpu, 3)(gpu, out value)) || value == IntPtr.Zero)
				{
					return null;
				}
				return Marshal.PtrToStringAnsi(value);
			}
			catch
			{
				return null;
			}
		}

		private static IntPtr Get3DServices()
		{
			try
			{
				IntPtr value;
				if (!Succeeded(VMethod<FnOutPtr>(system, 7)(system, out value)))
				{
					return IntPtr.Zero;
				}
				return value;
			}
			catch
			{
				return IntPtr.Zero;
			}
		}

		private static IntPtr GetFeature(int serviceSlot, IntPtr gpu)
		{
			if (!Available)
			{
				return IntPtr.Zero;
			}
			IntPtr intPtr = Get3DServices();
			if (intPtr == IntPtr.Zero)
			{
				return IntPtr.Zero;
			}
			try
			{
				IntPtr value;
				if (!Succeeded(VMethod<FnGpuOutPtr>(intPtr, serviceSlot)(intPtr, gpu, out value)))
				{
					return IntPtr.Zero;
				}
				return value;
			}
			catch
			{
				return IntPtr.Zero;
			}
			finally
			{
				Release(intPtr);
			}
		}

		private static bool FeatureFlags(IntPtr feature, out bool supported, out bool enabled)
		{
			supported = false;
			enabled = false;
			try
			{
				byte value;
				if (!Succeeded(VMethod<FnOutByte>(feature, 3)(feature, out value)))
				{
					return false;
				}
				supported = value != 0;
				if (!supported)
				{
					return true;
				}
				byte value2;
				if (!Succeeded(VMethod<FnOutByte>(feature, 4)(feature, out value2)))
				{
					return false;
				}
				enabled = value2 != 0;
				return true;
			}
			catch
			{
				return false;
			}
		}

		public static bool AntiLagGet(IntPtr gpu, out bool supported, out bool enabled)
		{
			supported = false;
			enabled = false;
			IntPtr feature = GetFeature(3, gpu);
			if (feature == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return FeatureFlags(feature, out supported, out enabled);
			}
			finally
			{
				Release(feature);
			}
		}

		public static bool AntiLagSet(IntPtr gpu, bool on)
		{
			IntPtr feature = GetFeature(3, gpu);
			if (feature == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return Succeeded(VMethod<FnInByte>(feature, 5)(feature, (byte)(on ? 1 : 0)));
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(feature);
			}
		}

		public static bool EnhancedSyncGet(IntPtr gpu, out bool supported, out bool enabled)
		{
			supported = false;
			enabled = false;
			IntPtr feature = GetFeature(7, gpu);
			if (feature == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return FeatureFlags(feature, out supported, out enabled);
			}
			finally
			{
				Release(feature);
			}
		}

		public static bool EnhancedSyncSet(IntPtr gpu, bool on)
		{
			IntPtr feature = GetFeature(7, gpu);
			if (feature == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return Succeeded(VMethod<FnInByte>(feature, 5)(feature, (byte)(on ? 1 : 0)));
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(feature);
			}
		}

		public static bool ChillGet(IntPtr gpu, out bool supported, out bool enabled, out int minFps, out int maxFps, out AdlxIntRange range)
		{
			supported = false;
			enabled = false;
			minFps = 0;
			maxFps = 0;
			range = default(AdlxIntRange);
			IntPtr feature = GetFeature(4, gpu);
			if (feature == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				if (!FeatureFlags(feature, out supported, out enabled))
				{
					return false;
				}
				if (!supported)
				{
					return true;
				}
				if (!Succeeded(VMethod<FnOutRange>(feature, 5)(feature, out range)))
				{
					return false;
				}
				if (!Succeeded(VMethod<FnOutInt>(feature, 6)(feature, out minFps)))
				{
					return false;
				}
				if (!Succeeded(VMethod<FnOutInt>(feature, 7)(feature, out maxFps)))
				{
					return false;
				}
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(feature);
			}
		}

		public static bool ChillSet(IntPtr gpu, bool on, int minFps, int maxFps)
		{
			IntPtr feature = GetFeature(4, gpu);
			if (feature == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				if (on)
				{
					bool flag = Succeeded(VMethod<FnInInt>(feature, 9)(feature, minFps)) && Succeeded(VMethod<FnInInt>(feature, 10)(feature, maxFps));
					if (!flag)
					{
						flag = Succeeded(VMethod<FnInInt>(feature, 10)(feature, maxFps)) && Succeeded(VMethod<FnInInt>(feature, 9)(feature, minFps));
					}
					if (!flag)
					{
						return false;
					}
				}
				return Succeeded(VMethod<FnInByte>(feature, 8)(feature, (byte)(on ? 1 : 0)));
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(feature);
			}
		}

		public static bool FrtcGet(IntPtr gpu, out bool supported, out bool enabled, out int fps, out AdlxIntRange range)
		{
			supported = false;
			enabled = false;
			fps = 0;
			range = default(AdlxIntRange);
			IntPtr feature = GetFeature(9, gpu);
			if (feature == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				if (!FeatureFlags(feature, out supported, out enabled))
				{
					return false;
				}
				if (!supported)
				{
					return true;
				}
				if (!Succeeded(VMethod<FnOutRange>(feature, 5)(feature, out range)))
				{
					return false;
				}
				if (!Succeeded(VMethod<FnOutInt>(feature, 6)(feature, out fps)))
				{
					return false;
				}
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(feature);
			}
		}

		public static bool FrtcSet(IntPtr gpu, bool on, int fps)
		{
			IntPtr feature = GetFeature(9, gpu);
			if (feature == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				if (on && !Succeeded(VMethod<FnInInt>(feature, 8)(feature, fps)))
				{
					return false;
				}
				return Succeeded(VMethod<FnInByte>(feature, 7)(feature, (byte)(on ? 1 : 0)));
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(feature);
			}
		}

		private static IntPtr GetAfmf()
		{
			if (!Available)
			{
				return IntPtr.Zero;
			}
			IntPtr intPtr = Get3DServices();
			if (intPtr == IntPtr.Zero)
			{
				return IntPtr.Zero;
			}
			try
			{
				IntPtr value;
				if (!Succeeded(VMethod<FnQueryIface>(intPtr, 2)(intPtr, "IADLX3DSettingsServices1", out value)) || value == IntPtr.Zero)
				{
					return IntPtr.Zero;
				}
				try
				{
					IntPtr value2;
					if (!Succeeded(VMethod<FnOutPtr>(value, 17)(value, out value2)))
					{
						return IntPtr.Zero;
					}
					return value2;
				}
				finally
				{
					Release(value);
				}
			}
			catch
			{
				return IntPtr.Zero;
			}
			finally
			{
				Release(intPtr);
			}
		}

		public static bool AfmfGet(out bool supported, out bool enabled)
		{
			supported = false;
			enabled = false;
			IntPtr afmf = GetAfmf();
			if (afmf == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return FeatureFlags(afmf, out supported, out enabled);
			}
			finally
			{
				Release(afmf);
			}
		}

		public static bool AfmfSet(bool on)
		{
			IntPtr afmf = GetAfmf();
			if (afmf == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return Succeeded(VMethod<FnInByte>(afmf, 5)(afmf, (byte)(on ? 1 : 0)));
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(afmf);
			}
		}

		private static IntPtr GetRsr()
		{
			if (!Available)
			{
				return IntPtr.Zero;
			}
			IntPtr intPtr = Get3DServices();
			if (intPtr == IntPtr.Zero)
			{
				return IntPtr.Zero;
			}
			try
			{
				IntPtr value;
				if (!Succeeded(VMethod<FnOutPtr>(intPtr, 14)(intPtr, out value)))
				{
					return IntPtr.Zero;
				}
				return value;
			}
			catch
			{
				return IntPtr.Zero;
			}
			finally
			{
				Release(intPtr);
			}
		}

		public static bool RsrGet(out bool supported, out bool enabled, out int sharpness)
		{
			supported = false;
			enabled = false;
			sharpness = 0;
			IntPtr rsr = GetRsr();
			if (rsr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				if (!FeatureFlags(rsr, out supported, out enabled))
				{
					return false;
				}
				if (!supported)
				{
					return true;
				}
				int value;
				if (Succeeded(VMethod<FnOutInt>(rsr, 7)(rsr, out value)))
				{
					sharpness = value;
				}
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(rsr);
			}
		}

		public static bool RsrSet(bool on, int sharpness)
		{
			IntPtr rsr = GetRsr();
			if (rsr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				if (on && sharpness >= 0 && !Succeeded(VMethod<FnInInt>(rsr, 8)(rsr, sharpness)))
				{
					return false;
				}
				return Succeeded(VMethod<FnInByte>(rsr, 5)(rsr, (byte)(on ? 1 : 0)));
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(rsr);
			}
		}

		private static IntPtr GetManualPowerTuning(IntPtr gpu, out bool supported)
		{
			supported = false;
			if (!Available)
			{
				return IntPtr.Zero;
			}
			IntPtr value;
			try
			{
				if (!Succeeded(VMethod<FnOutPtr>(system, 8)(system, out value)) || value == IntPtr.Zero)
				{
					return IntPtr.Zero;
				}
			}
			catch
			{
				return IntPtr.Zero;
			}
			try
			{
				byte value2;
				if (!Succeeded(VMethod<FnGpuOutByte>(value, 11)(value, gpu, out value2)) || value2 == 0)
				{
					return IntPtr.Zero;
				}
				supported = true;
				IntPtr value3;
				if (!Succeeded(VMethod<FnGpuOutPtr>(value, 17)(value, gpu, out value3)))
				{
					return IntPtr.Zero;
				}
				return value3;
			}
			catch
			{
				return IntPtr.Zero;
			}
			finally
			{
				Release(value);
			}
		}

		private static IntPtr GetTuningServices()
		{
			if (!Available)
			{
				return IntPtr.Zero;
			}
			try
			{
				IntPtr value;
				if (!Succeeded(VMethod<FnOutPtr>(system, 8)(system, out value)) || value == IntPtr.Zero)
				{
					return IntPtr.Zero;
				}
				return value;
			}
			catch
			{
				return IntPtr.Zero;
			}
		}

		private static IntPtr GetManualGfxTuning2(IntPtr gpu, out bool supported)
		{
			supported = false;
			IntPtr tuningServices = GetTuningServices();
			if (tuningServices == IntPtr.Zero)
			{
				return IntPtr.Zero;
			}
			try
			{
				byte value;
				if (!Succeeded(VMethod<FnGpuOutByte>(tuningServices, 8)(tuningServices, gpu, out value)) || value == 0)
				{
					return IntPtr.Zero;
				}
				IntPtr value2;
				if (!Succeeded(VMethod<FnGpuOutPtr>(tuningServices, 14)(tuningServices, gpu, out value2)) || value2 == IntPtr.Zero)
				{
					return IntPtr.Zero;
				}
				try
				{
					IntPtr value3;
					if (!Succeeded(VMethod<FnQueryIface>(value2, 2)(value2, "IADLXManualGraphicsTuning2", out value3)) || value3 == IntPtr.Zero)
					{
						return IntPtr.Zero;
					}
					supported = true;
					return value3;
				}
				finally
				{
					Release(value2);
				}
			}
			catch
			{
				return IntPtr.Zero;
			}
			finally
			{
				Release(tuningServices);
			}
		}

		public static bool GfxMinGet(IntPtr gpu, out bool supported, out int min, out int max, out AdlxIntRange minRange)
		{
			min = 0;
			max = 0;
			minRange = default(AdlxIntRange);
			IntPtr manualGfxTuning = GetManualGfxTuning2(gpu, out supported);
			if (manualGfxTuning == IntPtr.Zero)
			{
				return !supported;
			}
			try
			{
				if (!Succeeded(VMethod<FnOutRange>(manualGfxTuning, 3)(manualGfxTuning, out minRange)))
				{
					return false;
				}
				if (!Succeeded(VMethod<FnOutInt>(manualGfxTuning, 4)(manualGfxTuning, out min)))
				{
					return false;
				}
				if (!Succeeded(VMethod<FnOutInt>(manualGfxTuning, 7)(manualGfxTuning, out max)))
				{
					return false;
				}
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(manualGfxTuning);
			}
		}

		public static bool GfxMinSet(IntPtr gpu, int min)
		{
			bool supported;
			IntPtr manualGfxTuning = GetManualGfxTuning2(gpu, out supported);
			if (manualGfxTuning == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return Succeeded(VMethod<FnInInt>(manualGfxTuning, 5)(manualGfxTuning, min));
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(manualGfxTuning);
			}
		}

		private static IntPtr GetSam(IntPtr gpu)
		{
			IntPtr tuningServices = GetTuningServices();
			if (tuningServices == IntPtr.Zero)
			{
				return IntPtr.Zero;
			}
			try
			{
				IntPtr value;
				if (!Succeeded(VMethod<FnQueryIface>(tuningServices, 2)(tuningServices, "IADLXGPUTuningServices1", out value)) || value == IntPtr.Zero)
				{
					return IntPtr.Zero;
				}
				try
				{
					IntPtr value2;
					if (!Succeeded(VMethod<FnGpuOutPtr>(value, 18)(value, gpu, out value2)))
					{
						return IntPtr.Zero;
					}
					return value2;
				}
				finally
				{
					Release(value);
				}
			}
			catch
			{
				return IntPtr.Zero;
			}
			finally
			{
				Release(tuningServices);
			}
		}

		public static bool SamGet(IntPtr gpu, out bool supported, out bool enabled)
		{
			supported = false;
			enabled = false;
			IntPtr sam = GetSam(gpu);
			if (sam == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return FeatureFlags(sam, out supported, out enabled);
			}
			finally
			{
				Release(sam);
			}
		}

		public static bool SamSet(IntPtr gpu, bool on)
		{
			IntPtr sam = GetSam(gpu);
			if (sam == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return Succeeded(VMethod<FnInByte>(sam, 5)(sam, (byte)(on ? 1 : 0)));
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(sam);
			}
		}

		public static bool PowerLimitGet(IntPtr gpu, out bool supported, out int current, out int max)
		{
			supported = false;
			current = 0;
			max = 0;
			IntPtr manualPowerTuning = GetManualPowerTuning(gpu, out supported);
			if (manualPowerTuning == IntPtr.Zero)
			{
				return supported;
			}
			try
			{
				AdlxIntRange range;
				if (!Succeeded(VMethod<FnOutRange>(manualPowerTuning, 3)(manualPowerTuning, out range)))
				{
					return false;
				}
				if (!Succeeded(VMethod<FnOutInt>(manualPowerTuning, 4)(manualPowerTuning, out current)))
				{
					return false;
				}
				max = range.Max;
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(manualPowerTuning);
			}
		}

		public static bool PowerLimitSet(IntPtr gpu, int value)
		{
			bool supported;
			IntPtr manualPowerTuning = GetManualPowerTuning(gpu, out supported);
			if (manualPowerTuning == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return Succeeded(VMethod<FnInInt>(manualPowerTuning, 5)(manualPowerTuning, value));
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(manualPowerTuning);
			}
		}

		public static bool RisGet(IntPtr gpu, out bool supported, out bool enabled, out int sharpness, out AdlxIntRange range)
		{
			supported = false;
			enabled = false;
			sharpness = 0;
			range = default(AdlxIntRange);
			IntPtr feature = GetFeature(6, gpu);
			if (feature == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				if (!FeatureFlags(feature, out supported, out enabled))
				{
					return false;
				}
				if (!supported)
				{
					return true;
				}
				if (!Succeeded(VMethod<FnOutRange>(feature, 5)(feature, out range)))
				{
					return false;
				}
				if (!Succeeded(VMethod<FnOutInt>(feature, 6)(feature, out sharpness)))
				{
					return false;
				}
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(feature);
			}
		}

		public static bool RisSet(IntPtr gpu, bool on, int sharpness)
		{
			IntPtr feature = GetFeature(6, gpu);
			if (feature == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				if (on && !Succeeded(VMethod<FnInInt>(feature, 8)(feature, sharpness)))
				{
					return false;
				}
				return Succeeded(VMethod<FnInByte>(feature, 7)(feature, (byte)(on ? 1 : 0)));
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(feature);
			}
		}

		public static bool ResetShaderCache(IntPtr gpu)
		{
			IntPtr feature = GetFeature(15, gpu);
			if (feature == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				byte value;
				if (!Succeeded(VMethod<FnOutByte>(feature, 3)(feature, out value)) || value == 0)
				{
					return false;
				}
				return Succeeded(VMethod<FnAction>(feature, 4)(feature));
			}
			catch
			{
				return false;
			}
			finally
			{
				Release(feature);
			}
		}
	}
}
