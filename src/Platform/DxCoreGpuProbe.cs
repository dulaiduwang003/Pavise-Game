// @author bdth 2074055628@qq.com
// 文件用途 通过 DXCore 读取驱动报告的显卡集成属性 避免用 PCI 总线号猜测核显

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class DxCoreGpuProbe
    {
        private const uint HardwareId = 3;
        private const uint IsIntegrated = 12;
        private const uint HardwareIdParts = 14;

        private static readonly Guid AdapterFactoryId =
            new Guid("78ee5945-c36e-4b13-a669-005dd11c0f06");
        private static readonly Guid AdapterListId =
            new Guid("526c7776-40e9-459b-b711-f32ad76dfc28");
        private static readonly Guid AdapterId =
            new Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
        private static readonly Guid D3d11Graphics =
            new Guid("8c47866b-7583-450d-f0f0-6bada895af4b");
        private static readonly Guid D3d12Graphics =
            new Guid("0c9ece4d-2f6e-4f01-8c96-e89e331b47b1");
        private static readonly Guid D3d12CoreCompute =
            new Guid("248e2800-a793-4724-abaa-23a6de1be090");

        internal static Dictionary<string, bool> CollectIntegrated()
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            IDXCoreAdapterFactory factory = null;
            try
            {
                Guid factoryId = AdapterFactoryId;
                if (DXCoreCreateAdapterFactory(ref factoryId, out factory) < 0 || factory == null)
                    return result;
                CollectAttribute(factory, D3d11Graphics, result);
                CollectAttribute(factory, D3d12Graphics, result);
                CollectAttribute(factory, D3d12CoreCompute, result);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch { }
            finally
            {
                if (factory != null) try { Marshal.FinalReleaseComObject(factory); } catch { }
            }
            return result;
        }

        private static void CollectAttribute(IDXCoreAdapterFactory factory, Guid attribute,
            Dictionary<string, bool> result)
        {
            IDXCoreAdapterList list = null;
            IntPtr attributes = IntPtr.Zero;
            try
            {
                attributes = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Guid)));
                Marshal.StructureToPtr(attribute, attributes, false);
                Guid listId = AdapterListId;
                if (factory.CreateAdapterList(1, attributes, ref listId, out list) < 0 || list == null)
                    return;

                uint count = list.GetAdapterCount();
                for (uint index = 0; index < count; index++)
                {
                    IDXCoreAdapter adapter = null;
                    try
                    {
                        Guid adapterId = AdapterId;
                        if (list.GetAdapter(index, ref adapterId, out adapter) < 0 || adapter == null)
                            continue;
                        if (!adapter.IsValid() || !adapter.IsPropertySupported(IsIntegrated)) continue;

                        string key;
                        if (!TryHardwareKey(adapter, out key)) continue;
                        IntPtr value = Marshal.AllocHGlobal(1);
                        try
                        {
                            Marshal.WriteByte(value, 0);
                            if (adapter.GetProperty(IsIntegrated, new UIntPtr(1), value) >= 0)
                                result[key] = Marshal.ReadByte(value) != 0;
                        }
                        finally { Marshal.FreeHGlobal(value); }
                    }
                    finally
                    {
                        if (adapter != null)
                            try { Marshal.FinalReleaseComObject(adapter); } catch { }
                    }
                }
            }
            finally
            {
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (list != null) try { Marshal.FinalReleaseComObject(list); } catch { }
            }
        }

        private static bool TryHardwareKey(IDXCoreAdapter adapter, out string key)
        {
            key = null;
            uint property;
            int size;
            if (adapter.IsPropertySupported(HardwareIdParts))
            {
                property = HardwareIdParts;
                size = 20;
            }
            else if (adapter.IsPropertySupported(HardwareId))
            {
                property = HardwareId;
                size = 16;
            }
            else return false;

            IntPtr value = Marshal.AllocHGlobal(size);
            try
            {
                if (adapter.GetProperty(property, new UIntPtr((uint)size), value) < 0) return false;
                uint vendor = unchecked((uint)Marshal.ReadInt32(value, 0));
                uint device = unchecked((uint)Marshal.ReadInt32(value, 4));
                if (vendor == 0 || device == 0) return false;
                key = "VEN_" + vendor.ToString("X4") + "&DEV_" + device.ToString("X4");
                return true;
            }
            finally { Marshal.FreeHGlobal(value); }
        }

        [ComImport, Guid("78ee5945-c36e-4b13-a669-005dd11c0f06"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXCoreAdapterFactory
        {
            [PreserveSig]
            int CreateAdapterList(uint numAttributes, IntPtr filterAttributes,
                [In] ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out IDXCoreAdapterList adapterList);
        }

        [ComImport, Guid("526c7776-40e9-459b-b711-f32ad76dfc28"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXCoreAdapterList
        {
            [PreserveSig]
            int GetAdapter(uint index, [In] ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out IDXCoreAdapter adapter);

            [PreserveSig]
            uint GetAdapterCount();
        }

        [ComImport, Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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

        [DllImport("dxcore.dll", ExactSpelling = true)]
        private static extern int DXCoreCreateAdapterFactory(
            [In] ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IDXCoreAdapterFactory factory);
    }
}
