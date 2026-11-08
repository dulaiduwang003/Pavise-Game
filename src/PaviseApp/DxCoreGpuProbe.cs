using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
	internal static class DxCoreGpuProbe
	{
		[ComImport]
		[Guid("78ee5945-c36e-4b13-a669-005dd11c0f06")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IDXCoreAdapterFactory
		{
			[PreserveSig]
			int CreateAdapterList(uint numAttributes, IntPtr filterAttributes, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXCoreAdapterList adapterList);
		}

		[ComImport]
		[Guid("526c7776-40e9-459b-b711-f32ad76dfc28")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IDXCoreAdapterList
		{
			[PreserveSig]
			int GetAdapter(uint index, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXCoreAdapter adapter);

			[PreserveSig]
			uint GetAdapterCount();
		}

		[ComImport]
		[Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IDXCoreAdapter
		{
			[PreserveSig]
			[return: MarshalAs(UnmanagedType.I1)]
			bool IsValid();

			[PreserveSig]
			[return: MarshalAs(UnmanagedType.I1)]
			bool IsAttributeSupported([In] ref Guid attributeGuid);

			[PreserveSig]
			[return: MarshalAs(UnmanagedType.I1)]
			bool IsPropertySupported(uint property);

			[PreserveSig]
			int GetProperty(uint property, UIntPtr bufferSize, IntPtr propertyData);
		}

		private const uint HardwareId = 3u;

		private const uint IsIntegrated = 12u;

		private const uint HardwareIdParts = 14u;

		private const int ProbeTimeoutMs = 3000;

		private static readonly Guid AdapterFactoryId = new Guid("78ee5945-c36e-4b13-a669-005dd11c0f06");

		private static readonly Guid AdapterListId = new Guid("526c7776-40e9-459b-b711-f32ad76dfc28");

		private static readonly Guid AdapterId = new Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");

		private static readonly Guid D3d11Graphics = new Guid("8c47866b-7583-450d-f0f0-6bada895af4b");

		private static readonly Guid D3d12Graphics = new Guid("0c9ece4d-2f6e-4f01-8c96-e89e331b47b1");

		private static readonly Guid D3d12CoreCompute = new Guid("248e2800-a793-4724-abaa-23a6de1be090");

		internal static Dictionary<string, bool> CollectIntegrated()
		{
			Dictionary<string, bool> result = null;
			ManualResetEvent done = new ManualResetEvent(false);
			Thread thread = new Thread((ThreadStart)delegate
			{
				try
				{
					result = CollectIntegratedCore();
				}
				catch
				{
				}
				finally
				{
					done.Set();
				}
			});
			thread.IsBackground = true;
			thread.Start();
			if (!done.WaitOne(3000))
			{
				Logger.Log(Lang.T("log.dxcoregpuprobe.1"));
				return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			}
			return result ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		}

		private static Dictionary<string, bool> CollectIntegratedCore()
		{
			Dictionary<string, bool> result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			IDXCoreAdapterFactory factory = null;
			try
			{
				Guid riid = AdapterFactoryId;
				if (DXCoreCreateAdapterFactory(ref riid, out factory) < 0 || factory == null)
				{
					return result;
				}
				CollectAttribute(factory, D3d11Graphics, result);
				CollectAttribute(factory, D3d12Graphics, result);
				CollectAttribute(factory, D3d12CoreCompute, result);
			}
			catch (DllNotFoundException)
			{
			}
			catch (EntryPointNotFoundException)
			{
			}
			catch
			{
			}
			finally
			{
				if (factory != null)
				{
					try
					{
						Marshal.FinalReleaseComObject(factory);
					}
					catch
					{
					}
				}
			}
			return result;
		}

		private static void CollectAttribute(IDXCoreAdapterFactory factory, Guid attribute, Dictionary<string, bool> result)
		{
			IDXCoreAdapterList adapterList = null;
			IntPtr intPtr = IntPtr.Zero;
			try
			{
				intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Guid)));
				Marshal.StructureToPtr(attribute, intPtr, false);
				Guid riid = AdapterListId;
				if (factory.CreateAdapterList(1u, intPtr, ref riid, out adapterList) < 0 || adapterList == null)
				{
					return;
				}
				uint adapterCount = adapterList.GetAdapterCount();
				for (uint iterationIndex = 0u; iterationIndex < adapterCount; iterationIndex++)
				{
					IDXCoreAdapter adapter = null;
					try
					{
						Guid riid2 = AdapterId;
						string key;
						if (adapterList.GetAdapter(iterationIndex, ref riid2, out adapter) < 0 || adapter == null || !adapter.IsValid() || !adapter.IsPropertySupported(12u) || !TryHardwareKey(adapter, out key))
						{
							continue;
						}
						IntPtr intPtr2 = Marshal.AllocHGlobal(1);
						try
						{
							Marshal.WriteByte(intPtr2, 0);
							if (adapter.GetProperty(12u, new UIntPtr(1u), intPtr2) >= 0)
							{
								result[key] = Marshal.ReadByte(intPtr2) != 0;
							}
						}
						finally
						{
							Marshal.FreeHGlobal(intPtr2);
						}
					}
					finally
					{
						if (adapter != null)
						{
							try
							{
								Marshal.FinalReleaseComObject(adapter);
							}
							catch
							{
							}
						}
					}
				}
			}
			finally
			{
				if (intPtr != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(intPtr);
				}
				if (adapterList != null)
				{
					try
					{
						Marshal.FinalReleaseComObject(adapterList);
					}
					catch
					{
					}
				}
			}
		}

		private static bool TryHardwareKey(IDXCoreAdapter adapter, out string key)
		{
			key = null;
			uint property;
			int hardwareKeyValue;
			if (adapter.IsPropertySupported(14u))
			{
				property = 14u;
				hardwareKeyValue = 20;
			}
			else
			{
				if (!adapter.IsPropertySupported(3u))
				{
					return false;
				}
				property = 3u;
				hardwareKeyValue = 16;
			}
			IntPtr intPtr = Marshal.AllocHGlobal(hardwareKeyValue);
			try
			{
				if (adapter.GetProperty(property, new UIntPtr((uint)hardwareKeyValue), intPtr) < 0)
				{
					return false;
				}
				uint int32 = (uint)Marshal.ReadInt32(intPtr, 0);
				uint int322 = (uint)Marshal.ReadInt32(intPtr, 4);
				if (int32 == 0 || int322 == 0)
				{
					return false;
				}
				key = "VEN_" + int32.ToString("X4") + "&DEV_" + int322.ToString("X4");
				return true;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		[DllImport("dxcore.dll", ExactSpelling = true)]
		private static extern int DXCoreCreateAdapterFactory([In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXCoreAdapterFactory factory);
	}
}
