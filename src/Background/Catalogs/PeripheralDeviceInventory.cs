using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    [Flags]
    internal enum PeripheralKind
    {
        Keyboard = 1,
        Mouse = 2,
        Headset = 4
    }

    internal sealed class PeripheralDevice
    {
        internal string Name;
        internal PeripheralKind Kind;
        internal bool Connected;
        internal string[] ServiceNames;
    }

    internal static class PeripheralDeviceInventory
    {
        private static readonly Guid MediaClass = new Guid("4d36e96c-e325-11ce-bfc1-08002be10318");
        private static readonly Guid SystemContainer = new Guid("00000000-0000-0000-ffff-ffffffffffff");
        private const uint Active = 1u;
        private const uint AudioStates = 15u;
        private const uint HeadphonesFormFactor = 3u;
        private const uint HeadsetFormFactor = 5u;
        private const ushort VariantUInt = 19;
        private const ushort VariantString = 31;
        private const ushort VariantGuid = 72;

        private sealed class Group
        {
            internal string Name;
            internal bool ModelName;
            internal PeripheralKind Kind;
            internal bool Connected;
            internal readonly HashSet<string> Services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            internal void AddService(string service)
            {
                if (!string.IsNullOrEmpty(service)) Services.Add(service);
            }

            internal void UseName(string name, bool modelName)
            {
                if (!string.IsNullOrWhiteSpace(name) && (Name == null || modelName && !ModelName))
                {
                    Name = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    ModelName = modelName;
                }
            }
        }

        internal static PeripheralDevice[] Discover()
        {
            var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            foreach (InputDevice input in InputChainProbe.Devices())
            {
                if (input.Transport == InputTransport.Virtual) continue;
                Guid container = PresentDevices.ContainerId(input.InstanceId);
                Group group = GetGroup(groups, DeviceKey(container, input.InstanceId));
                group.Connected = true;
                group.Kind |= input.IsMouse ? PeripheralKind.Mouse : PeripheralKind.Keyboard;
                group.UseName(InputModelName(input.InstanceId, container), true);
                if (!string.Equals(input.Name, input.InstanceId, StringComparison.OrdinalIgnoreCase)) group.UseName(input.Name, false);
                if (input.Transport != InputTransport.Unknown) group.AddService(PresentDevices.ServiceName(input.InstanceId));
            }

            AddHeadsets(groups);
            var devices = new List<PeripheralDevice>(groups.Count);
            foreach (Group group in groups.Values)
            {
                var services = new string[group.Services.Count];
                group.Services.CopyTo(services);
                Array.Sort(services, StringComparer.OrdinalIgnoreCase);
                devices.Add(new PeripheralDevice { Name = group.Name ?? "", Kind = group.Kind, Connected = group.Connected, ServiceNames = services });
            }
            devices.Sort(delegate(PeripheralDevice first, PeripheralDevice second)
            {
                int kindOrder = first.Kind.CompareTo(second.Kind);
                return kindOrder != 0 ? kindOrder : StringComparer.OrdinalIgnoreCase.Compare(first.Name, second.Name);
            });
            return devices.ToArray();
        }

        private static bool IsDeviceContainer(Guid value)
        {
            return value != Guid.Empty && value != SystemContainer;
        }

        private static string DeviceKey(Guid container, string node)
        {
            return IsDeviceContainer(container) ? container.ToString("D") : node;
        }

        private static Group GetGroup(Dictionary<string, Group> groups, string key)
        {
            Group group;
            if (!groups.TryGetValue(key, out group))
            {
                group = new Group();
                groups.Add(key, group);
            }
            return group;
        }

        private static string InputModelName(string device, Guid container)
        {
            string name = PresentDevices.BusReportedDesc(device);
            if (name != null || !IsDeviceContainer(container)) return name;

            // 复合设备的型号可能只写在同一容器内的父节点上
            foreach (string parent in PresentDevices.ParentChain(device))
            {
                if (PresentDevices.ContainerId(parent) != container) break;
                name = PresentDevices.BusReportedDesc(parent);
                if (name != null) return name;
            }
            return null;
        }

        private static Dictionary<Guid, Group> ReadMediaDevices()
        {
            var media = new Dictionary<Guid, Group>();
            foreach (string id in PresentDevices.ByClass(MediaClass))
            {
                // 内置声卡和虚拟音频的驱动不能用来推断某副耳机的配套程序
                if (!id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)
                    && !id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)
                    && !id.StartsWith("BTHHFENUM\\", StringComparison.OrdinalIgnoreCase)
                    && !id.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase)) continue;

                Guid container = PresentDevices.ContainerId(id);
                if (!IsDeviceContainer(container)) continue;
                Group device;
                if (!media.TryGetValue(container, out device))
                {
                    device = new Group();
                    media.Add(container, device);
                }
                device.UseName(PresentDevices.FriendlyName(id), true);
                device.AddService(PresentDevices.ServiceName(id));
            }
            return media;
        }

        private static void AddHeadsets(Dictionary<string, Group> groups)
        {
            IAudioEnumerator enumerator = null;
            IAudioCollection devices = null;
            try
            {
                enumerator = (IAudioEnumerator)new AudioEnumerator();
                if (enumerator.EnumAudioEndpoints(2, AudioStates, out devices) < 0 || devices == null) return;
                uint count;
                if (devices.GetCount(out count) < 0) return;
                Dictionary<Guid, Group> media = ReadMediaDevices();
                for (uint index = 0; index < count; index++)
                {
                    ReadHeadset(groups, media, devices, index);
                }
            }
            catch (COMException) { }
            finally
            {
                Release(devices);
                Release(enumerator);
            }
        }

        private static void ReadHeadset(Dictionary<string, Group> groups, Dictionary<Guid, Group> media, IAudioCollection devices, uint index)
        {
            IAudioDevice device = null;
            IAudioProperties properties = null;
            try
            {
                if (devices.Item(index, out device) < 0 || device == null) return;
                uint state;
                if (device.GetState(out state) < 0 || device.OpenPropertyStore(0u, out properties) < 0 || properties == null) return;
                object containerValue = ReadAudioProperty(properties, EndpointContainerKey, VariantGuid);
                Guid container = containerValue is Guid ? (Guid)containerValue : Guid.Empty;
                Group mediaDevice;
                bool hasMediaDevice = media.TryGetValue(container, out mediaDevice);
                object formFactor = ReadAudioProperty(properties, FormFactorKey, VariantUInt);
                if (!object.Equals(formFactor, HeadphonesFormFactor) && !object.Equals(formFactor, HeadsetFormFactor)
                    && !(hasMediaDevice && HasHeadsetName(mediaDevice.Name))) return;
                bool connected = state == Active;
                if (!connected && !hasMediaDevice) return;
                string id;
                if (device.GetId(out id) < 0 || string.IsNullOrEmpty(id)) return;
                // 同一 USB 复合设备里的键鼠仍可用 耳机连接状态单独按音频端点判断
                Group group = GetGroup(groups, "audio:" + DeviceKey(container, id));
                group.Kind |= PeripheralKind.Headset;
                group.Connected |= connected;
                if (hasMediaDevice)
                {
                    group.UseName(ReadAudioProperty(properties, EndpointAdapterKey, VariantString) as string ?? mediaDevice.Name, true);
                    if (connected) group.Services.UnionWith(mediaDevice.Services);
                }
                group.UseName(ReadAudioProperty(properties, EndpointNameKey, VariantString) as string, false);
            }
            catch (COMException) { }
            finally
            {
                Release(properties);
                Release(device);
            }
        }

        private static bool HasHeadsetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("headset", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("headphone", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("耳机", StringComparison.Ordinal) >= 0
                || name.IndexOf("耳麦", StringComparison.Ordinal) >= 0
                || name.IndexOf("fones de ouvido", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("auscultadores", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object ReadAudioProperty(IAudioProperties properties, AudioPropertyKey key, ushort type)
        {
            AudioValue value;
            if (properties.GetValue(ref key, out value) < 0) return null;
            try
            {
                if (value.Type != type) return null;
                if (type == VariantUInt) return value.Number;
                if (value.Pointer == IntPtr.Zero) return null;
                if (type == VariantString) return Marshal.PtrToStringUni(value.Pointer);
                return Marshal.PtrToStructure(value.Pointer, typeof(Guid));
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }

        private static void Release(object value)
        {
            if (value != null) Marshal.ReleaseComObject(value);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref AudioValue value);

        private static readonly AudioPropertyKey FormFactorKey = new AudioPropertyKey("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 0);
        private static readonly AudioPropertyKey EndpointContainerKey = new AudioPropertyKey("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
        private static readonly AudioPropertyKey EndpointNameKey = new AudioPropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        private static readonly AudioPropertyKey EndpointAdapterKey = new AudioPropertyKey("026e516e-b814-414b-83cd-856d6fef4822", 2);

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioPropertyKey
        {
            internal Guid Format;
            internal uint Id;

            internal AudioPropertyKey(string format, uint id)
            {
                Format = new Guid(format);
                Id = id;
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct AudioValue
        {
            [FieldOffset(0)] internal ushort Type;
            [FieldOffset(8)] internal uint Number;
            [FieldOffset(8)] internal IntPtr Pointer;
        }

        [ComImport, Guid("bcde0395-e52f-467c-8e3d-c4579291692e")]
        private class AudioEnumerator { }

        [ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint states, out IAudioCollection devices);
        }

        [ComImport, Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IAudioDevice device);
        }

        [ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, out IntPtr instance);
            [PreserveSig] int OpenPropertyStore(uint access, out IAudioProperties properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioProperties
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out AudioPropertyKey property);
            [PreserveSig] int GetValue(ref AudioPropertyKey property, out AudioValue value);
        }
    }
}
