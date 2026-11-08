using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class HardwareSchedulingProbe
	{
		private sealed class WindowsBridge : IHardwareSchedulingNative, IDisposable
		{
			[UnmanagedFunctionPointer(CallingConvention.Winapi)]
			private delegate int OpenDelegate(ref HwsOpenAdapter args);

			[UnmanagedFunctionPointer(CallingConvention.Winapi)]
			private delegate int QueryDelegate(ref HwsFeatureQuery args);

			[UnmanagedFunctionPointer(CallingConvention.Winapi)]
			private delegate int CloseDelegate(ref HwsCloseAdapter args);

			[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
			private struct OsVersion
			{
				internal uint Size;

				internal uint Major;

				internal uint Minor;

				internal uint Build;

				internal uint PlatformId;

				[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
				internal string ServicePack;
			}

			private IntPtr module;

			private OpenDelegate open;

			private QueryDelegate query;

			private CloseDelegate close;

			private bool disposed;

			private void Admit()
			{
				if (disposed)
				{
					throw new ObjectDisposedException("WindowsBridge");
				}
			}

			public bool TryGetOsVersion(out uint major, out uint minor, out uint build)
			{
				Admit();
				major = (minor = (build = 0u));
				OsVersion osVersion = default(OsVersion);
				osVersion.Size = (uint)Marshal.SizeOf(typeof(OsVersion));
				OsVersion version = osVersion;
				if (version.Size != 276 || RtlGetVersion(ref version) != 0)
				{
					return false;
				}
				major = version.Major;
				minor = version.Minor;
				build = version.Build;
				return true;
			}

			public bool TryBindEntryPoints()
			{
				Admit();
				if (module != IntPtr.Zero)
				{
					if (open != null && query != null)
					{
						return close != null;
					}
					return false;
				}
				module = LoadLibraryEx("gdi32.dll", IntPtr.Zero, 2048u);
				if (module == IntPtr.Zero)
				{
					return false;
				}
				IntPtr procAddress = GetProcAddress(module, "D3DKMTOpenAdapterFromLuid");
				IntPtr procAddress2 = GetProcAddress(module, "D3DKMTIsFeatureEnabled");
				IntPtr procAddress3 = GetProcAddress(module, "D3DKMTCloseAdapter");
				if (procAddress == IntPtr.Zero || procAddress2 == IntPtr.Zero || procAddress3 == IntPtr.Zero)
				{
					return false;
				}
				open = (OpenDelegate)Marshal.GetDelegateForFunctionPointer(procAddress, typeof(OpenDelegate));
				query = (QueryDelegate)Marshal.GetDelegateForFunctionPointer(procAddress2, typeof(QueryDelegate));
				close = (CloseDelegate)Marshal.GetDelegateForFunctionPointer(procAddress3, typeof(CloseDelegate));
				return true;
			}

			public int OpenAdapter(ref HwsOpenAdapter args)
			{
				Admit();
				return open(ref args);
			}

			public int QueryFeature(ref HwsFeatureQuery args)
			{
				Admit();
				return query(ref args);
			}

			public int CloseAdapter(ref HwsCloseAdapter args)
			{
				Admit();
				return close(ref args);
			}

			public void Dispose()
			{
				if (disposed)
				{
					return;
				}
				disposed = true;
				if (module != IntPtr.Zero)
				{
					IntPtr intPtr = module;
					module = IntPtr.Zero;
					if (!FreeLibrary(intPtr))
					{
						throw new InvalidOperationException("FreeLibrary did not release the owned module");
					}
				}
			}

			[DllImport("ntdll.dll", ExactSpelling = true)]
			private static extern int RtlGetVersion(ref OsVersion version);

			[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW", ExactSpelling = true, SetLastError = true)]
			private static extern IntPtr LoadLibraryEx(string name, IntPtr reserved, uint flags);

			[DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
			private static extern IntPtr GetProcAddress(IntPtr module, string name);

			[DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			private static extern bool FreeLibrary(IntPtr module);
		}

		internal const uint HwsFeatureId = 0u;

		internal const ushort EnabledBit = 1;

		internal const ushort KnownFeatureBit = 2;

		internal const ushort DriverBit = 4;

		internal const ushort ConfigBit = 8;

		internal const ushort DocumentedResultBits = 63;

		internal const ushort SupportedEnabledVersion = 1;

		internal const uint MinimumOsBuild = 26100u;

		internal const string Source = "native:d3dkmt-isfeatureenabled-hwsch-exact-luid-v1";

		internal static HardwareSchedulingObservation Query(string verifiedRendererLuid)
		{
			return QueryWithBridge(verifiedRendererLuid, new WindowsBridge());
		}

		internal static HardwareSchedulingObservation QueryWithBridge(string verifiedRendererLuid, IHardwareSchedulingNative bridge)
		{
			if (bridge == null)
			{
				throw new ArgumentNullException("bridge");
			}
			uint ownedAdapter = 0u;
			HardwareSchedulingObservation hardwareSchedulingObservation = Unavailable(verifiedRendererLuid, HardwareSchedulingProbeReason.NativeFailure, "The native observation did not complete", null, null, null);
			try
			{
				hardwareSchedulingObservation = Observe(verifiedRendererLuid, bridge, ref ownedAdapter);
			}
			catch (Exception ex)
			{
				hardwareSchedulingObservation = Unavailable(verifiedRendererLuid, HardwareSchedulingProbeReason.NativeFailure, "Native HWSCH observation failed (" + ex.GetType().Name + ")", null, null, null);
			}
			finally
			{
				if (ownedAdapter != 0)
				{
					try
					{
						HwsCloseAdapter hwsCloseAdapter = default(HwsCloseAdapter);
						hwsCloseAdapter.Adapter = ownedAdapter;
						HwsCloseAdapter args = hwsCloseAdapter;
						int closeAdapterResult = bridge.CloseAdapter(ref args);
						if (closeAdapterResult != 0 || args.Adapter != ownedAdapter)
						{
							hardwareSchedulingObservation = Unavailable(verifiedRendererLuid, HardwareSchedulingProbeReason.CloseFailed, "Closing the exact adapter did not confirm success", hardwareSchedulingObservation.FeatureVersion, hardwareSchedulingObservation.RawResultBits, closeAdapterResult);
						}
					}
					catch (Exception ex2)
					{
						hardwareSchedulingObservation = Unavailable(verifiedRendererLuid, HardwareSchedulingProbeReason.CloseFailed, "Closing the exact adapter failed (" + ex2.GetType().Name + ")", hardwareSchedulingObservation.FeatureVersion, hardwareSchedulingObservation.RawResultBits, null);
					}
				}
				try
				{
					bridge.Dispose();
				}
				catch (Exception ex3)
				{
					hardwareSchedulingObservation = Unavailable(verifiedRendererLuid, HardwareSchedulingProbeReason.BridgeDisposeFailed, "Native bridge release failed (" + ex3.GetType().Name + ")", hardwareSchedulingObservation.FeatureVersion, hardwareSchedulingObservation.RawResultBits, null);
				}
			}
			return hardwareSchedulingObservation;
		}

		private static HardwareSchedulingObservation Observe(string luid, IHardwareSchedulingNative bridge, ref uint ownedAdapter)
		{
			HwsLuid luid2;
			if (!TryParseLuid(luid, out luid2))
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.InvalidRendererLuid, "Expected a nonzero 16-digit lowercase renderer LUID", null, null, null);
			}
			if (IntPtr.Size != 8)
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.ArchitectureUnavailable, "Only the verified x64 ABI is admitted", null, null, null);
			}
			uint major;
			uint minor;
			uint build;
			if (!bridge.TryGetOsVersion(out major, out minor, out build))
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.OsVersionUnavailable, "Unable to verify the native OS version", null, null, null);
			}
			if (major != 10 || minor != 0 || build < 26100)
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.OsVersionOutsideContract, "The OS is outside the Windows 11 24H2+ API contract", null, null, null);
			}
			if (!bridge.TryBindEntryPoints())
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.EntryPointsUnavailable, "The complete formal KMT entry point set is unavailable", null, null, null);
			}
			HwsOpenAdapter hwsOpenAdapter = default(HwsOpenAdapter);
			hwsOpenAdapter.AdapterLuid = luid2;
			hwsOpenAdapter.Adapter = 0u;
			HwsOpenAdapter args = hwsOpenAdapter;
			int adapter;
			try
			{
				adapter = bridge.OpenAdapter(ref args);
			}
			finally
			{
				ownedAdapter = args.Adapter;
			}
			if (adapter != 0)
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.OpenFailed, "Opening the renderer adapter failed", null, null, adapter);
			}
			if (ownedAdapter == 0)
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.InvalidAdapterHandle, "The renderer adapter open returned no handle", null, null, adapter);
			}
			if (args.AdapterLuid.LowPart != luid2.LowPart || args.AdapterLuid.HighPart != luid2.HighPart)
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.RendererLuidChanged, "The open did not retain the requested renderer LUID", null, null, null);
			}
			HwsFeatureQuery hwsFeatureQuery = default(HwsFeatureQuery);
			hwsFeatureQuery.Adapter = ownedAdapter;
			hwsFeatureQuery.FeatureId = 0u;
			HwsFeatureQuery args2 = hwsFeatureQuery;
			int feature = bridge.QueryFeature(ref args2);
			if (feature != 0)
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.QueryFailed, "The formal HWSCH query failed", null, null, feature);
			}
			if (args2.Adapter != ownedAdapter || args2.FeatureId != 0)
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.QueryIdentityChanged, "The HWSCH query changed its adapter or feature identity", null, null, null);
			}
			ushort bits = args2.Result.Bits;
			ushort version = args2.Result.Version;
			if ((bits & 0xFFFFFFC0u) != 0)
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.UnsupportedResultBits, "The result contains unreviewed flag bits", version, bits, feature);
			}
			if ((bits & 2) == 0)
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.FeatureUnknown, "The OS did not confirm a known HWSCH feature", version, bits, feature);
			}
			bool valuesMatch = (bits & 1) != 0;
			if (version > 1 || (valuesMatch && version != 1))
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.UnsupportedFeatureVersion, "The HWSCH feature version is outside the reviewed contract", version, bits, feature);
			}
			if (valuesMatch && (bits & 0xC) != 12)
			{
				return Unavailable(luid, HardwareSchedulingProbeReason.InconsistentFeatureResult, "Enabled HWSCH lacks its required driver/config support observations", version, bits, feature);
			}
			return new HardwareSchedulingObservation(valuesMatch ? HardwareSchedulingState.Enabled : HardwareSchedulingState.Disabled, HardwareSchedulingProbeReason.None, luid, "The exact renderer adapter reported HWSCH " + (valuesMatch ? "enabled" : "disabled"), version, bits, feature);
		}

		private static bool TryParseLuid(string value, out HwsLuid luid)
		{
			luid = default(HwsLuid);
			if (value == null || value.Length != 16)
			{
				return false;
			}
			ulong numericValue = 0uL;
			foreach (char c in value)
			{
				int remainingC = ((c >= '0' && c <= '9') ? (c - 48) : ((c >= 'a' && c <= 'f') ? (c - 97 + 10) : (-1)));
				if (remainingC < 0)
				{
					return false;
				}
				numericValue = (numericValue << 4) | (uint)remainingC;
			}
			if (numericValue == 0)
			{
				return false;
			}
			luid.LowPart = (uint)numericValue;
			luid.HighPart = (int)(numericValue >> 32);
			return true;
		}

		private static HardwareSchedulingObservation Unavailable(string luid, HardwareSchedulingProbeReason reason, string detail, ushort? version, ushort? bits, int? status)
		{
			return new HardwareSchedulingObservation(HardwareSchedulingState.ProbeUnavailable, reason, luid, detail, version, bits, status);
		}
	}
}
