using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    // Create, use and release all COM objects on the single MTA audio worker.
    internal sealed class LowLatencyAudioNative : ILaboratoryAudioDriver
    {
        private readonly AutoResetEvent render = new AutoResetEvent(false);
        private readonly AutoResetEvent wake;
        private IMMDeviceEnumerator enumerator;
        private Notifications notifications;
        private IAudioClient3 client;
        private IAudioRenderClient renderer;
        private IntPtr format;
        private uint bufferFrames;
        private LaboratoryAudioPeriod period;
        private bool initialized, started, comInitialized, registered;
        private long revision;
        private volatile string watchedId;
        public WaitHandle RenderReady { get { return render; } }
        public long Revision { get { return Interlocked.Read(ref revision); } }

        internal LowLatencyAudioNative(AutoResetEvent wake)
        {
            this.wake = wake;
            try
            {
                if (Environment.OSVersion.Version.Major < 10) throw new NotSupportedException("Windows 10 or later is required");
                Check(CoInitializeEx(IntPtr.Zero, 0)); comInitialized = true;
                enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorCom();
                notifications = new Notifications(this);
                Check(enumerator.RegisterEndpointNotificationCallback(notifications)); registered = true;
            }
            catch { Dispose(); throw; }
        }

        public LaboratoryAudioEndpoint DefaultEndpoint()
        {
            IMMDevice endpoint = null;
            try
            {
                int hr = enumerator.GetDefaultAudioEndpoint(0, 0, out endpoint);
                if (hr == unchecked((int)0x80070490)) { watchedId = null; return null; }
                Check(hr);
                string id; Check(endpoint.GetId(out id)); watchedId = id;
                uint state; Check(endpoint.GetState(out state));
                if ((state & 1) == 0) return null;
                string transport = Transport(endpoint);
                return new LaboratoryAudioEndpoint {
                    Id = id, Bluetooth = LaboratoryAudioSession.IsBluetooth(transport),
                    ConnectionKnown = !string.IsNullOrEmpty(transport)
                };
            }
            finally { Release(endpoint); }
        }

        public LaboratoryAudioPeriod Open(string endpointId)
        {
            Close();
            IMMDevice endpoint = null;
            try
            {
                Check(enumerator.GetDefaultAudioEndpoint(0, 0, out endpoint));
                string actual; Check(endpoint.GetId(out actual));
                if (!string.Equals(actual, endpointId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Default playback endpoint changed");
                Guid iid = typeof(IAudioClient3).GUID;
                object activated; Check(endpoint.Activate(ref iid, 0x17, IntPtr.Zero, out activated));
                client = activated as IAudioClient3;
                if (client == null) { Release(activated); throw new NotSupportedException("IAudioClient3"); }
                // Other/default processing mode; no RAW, offload or format/period locking flags.
                var properties = new AudioClientProperties { Size = (uint)Marshal.SizeOf(typeof(AudioClientProperties)) };
                Check(client.SetClientProperties(ref properties));
                Check(client.GetMixFormat(out format));
                if (format == IntPtr.Zero) throw new InvalidOperationException("Missing audio mix format");
                Check(client.GetSharedModeEnginePeriod(format, out period.Default, out period.Fundamental,
                    out period.Minimum, out period.Maximum));
                period.SampleRate = Marshal.ReadInt32(format, 4);
                period.FormatIdentity = FormatIdentity(format);
                return period;
            }
            finally { Release(endpoint); }
        }

        public LaboratoryAudioPeriod Start(uint frames)
        {
            // InitializeSharedAudioStream supports EVENTCALLBACK, not the legacy NOPERSIST stream flag.
            Check(client.InitializeSharedAudioStream(0x40000, frames, format, IntPtr.Zero)); initialized = true;
            Check(client.SetEventHandle(render.SafeWaitHandle.DangerousGetHandle()));
            Check(client.GetBufferSize(out bufferFrames));
            if (bufferFrames == 0) throw new InvalidOperationException("Empty audio buffer");
            Guid iid = typeof(IAudioRenderClient).GUID;
            object service; Check(client.GetService(ref iid, out service));
            renderer = service as IAudioRenderClient;
            if (renderer == null) { Release(service); throw new NotSupportedException("IAudioRenderClient"); }
            RenderSilence();
            Check(client.Start()); started = true;
            // Give the newly started engine one render event before evaluating its instantaneous readback.
            if (render.WaitOne(250)) RenderSilence();
            return ReadPeriod();
        }

        public LaboratoryAudioPeriod ReadPeriod()
        {
            IntPtr currentFormat = IntPtr.Zero;
            try
            {
                uint current; Check(client.GetCurrentSharedModeEnginePeriod(out currentFormat, out current));
                if (currentFormat == IntPtr.Zero) throw new InvalidOperationException("Missing current audio format");
                var result = period;
                result.Current = current; result.SampleRate = Marshal.ReadInt32(currentFormat, 4);
                result.FormatIdentity = FormatIdentity(currentFormat);
                return result;
            }
            finally { if (currentFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(currentFormat); }
        }

        public void RenderSilence()
        {
            if (!initialized || renderer == null) return;
            uint padding; Check(client.GetCurrentPadding(out padding));
            if (padding > bufferFrames) throw new InvalidOperationException("Invalid audio padding");
            uint frames = bufferFrames - padding;
            if (frames == 0) return;
            IntPtr data; Check(renderer.GetBuffer(frames, out data));
            // AUDCLNT_BUFFERFLAGS_SILENT: contents of the returned buffer need not be touched.
            Check(renderer.ReleaseBuffer(frames, 2));
        }

        public void Close()
        {
            if (client != null && started) try { client.Stop(); } catch { }
            started = false; initialized = false;
            Release(renderer); renderer = null;
            Release(client); client = null;
            if (format != IntPtr.Zero) { Marshal.FreeCoTaskMem(format); format = IntPtr.Zero; }
            bufferFrames = 0; period = new LaboratoryAudioPeriod();
            while (render.WaitOne(0)) { }
        }

        public void Dispose()
        {
            Close();
            if (registered && enumerator != null)
                try { enumerator.UnregisterEndpointNotificationCallback(notifications); } catch { }
            registered = false;
            if (notifications != null) notifications.Detach();
            notifications = null;
            Release(enumerator); enumerator = null;
            render.Dispose();
            if (comInitialized) { CoUninitialize(); comInitialized = false; }
        }

        private static string Transport(IMMDevice endpoint)
        {
            string direct = StringProperty(endpoint, new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
            if (LaboratoryAudioSession.IsBluetooth(direct)) return direct;
            // Follow the MMDEVAPI endpoint connector to its hardware adapter; SWD enumeration alone does not identify the bus.
            object raw = null;
            IDeviceTopology topology = null;
            IConnector connector = null;
            IMMDeviceEnumerator lookup = null;
            IMMDevice adapter = null;
            try
            {
                Guid iid = typeof(IDeviceTopology).GUID;
                Check(endpoint.Activate(ref iid, 0x17, IntPtr.Zero, out raw));
                topology = (IDeviceTopology)raw; raw = null;
                Check(topology.GetConnector(0, out connector));
                string adapterId; Check(connector.GetDeviceIdConnectedTo(out adapterId));
                lookup = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorCom();
                Check(lookup.GetDevice(adapterId, out adapter));
                string bus = StringProperty(adapter, new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
                if (!string.IsNullOrEmpty(bus) && !string.Equals(bus, "SWD", StringComparison.OrdinalIgnoreCase)) return bus;
                string instance = StringProperty(adapter, new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
                if (LaboratoryAudioSession.IsBluetooth(instance)) return instance;
            }
            catch { }
            finally { Release(adapter); Release(lookup); Release(connector); Release(topology); Release(raw); }
            return !string.IsNullOrEmpty(direct) && !string.Equals(direct, "SWD", StringComparison.OrdinalIgnoreCase) ? direct : null;
        }

        private static string StringProperty(IMMDevice device, Guid formatId, uint id)
        {
            IPropertyStore store = null;
            PropVariant value = new PropVariant();
            try
            {
                Check(device.OpenPropertyStore(0, out store));
                var key = new PropertyKey { FormatId = formatId, Id = id };
                Check(store.GetValue(ref key, out value));
                return value.Type == 31 && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            catch { return null; }
            finally { PropVariantClear(ref value); Release(store); }
        }
        private void Changed(string id, bool isDefault)
        {
            if (!isDefault && watchedId != null && !string.Equals(id, watchedId, StringComparison.Ordinal)) return;
            Interlocked.Increment(ref revision);
            try { wake.Set(); } catch (ObjectDisposedException) { }
        }
        private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        private static string FormatIdentity(IntPtr value)
        {
            int size = 18 + (ushort)Marshal.ReadInt16(value, 16);
            if (size > 4096) throw new InvalidOperationException("Invalid audio format size");
            var bytes = new byte[size]; Marshal.Copy(value, bytes, 0, size);
            return Convert.ToBase64String(bytes);
        }
        private static void Release(object value)
        { if (value != null && Marshal.IsComObject(value)) try { Marshal.ReleaseComObject(value); } catch { } }

        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
        [StructLayout(LayoutKind.Sequential)] private struct AudioClientProperties
        { internal uint Size; internal int Offload, Category, Options; }
        [StructLayout(LayoutKind.Sequential)] private struct PropertyKey
        { internal Guid FormatId; internal uint Id; }
        [StructLayout(LayoutKind.Sequential)] private struct PropVariant
        { internal ushort Type, Reserved1, Reserved2, Reserved3; internal IntPtr Pointer, Pointer2; }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, int states, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient callback);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }
        [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient3
        {
            [PreserveSig] int Initialize(int mode, int flags, long duration, long periodicity, IntPtr format, IntPtr session);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
            [PreserveSig] int IsOffloadCapable(int category, out int capable);
            [PreserveSig] int SetClientProperties(ref AudioClientProperties properties);
            [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minimum, out long maximum);
            [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint normal, out uint fundamental, out uint minimum, out uint maximum);
            [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint current);
            [PreserveSig] int InitializeSharedAudioStream(int flags, uint period, IntPtr format, IntPtr session);
        }
        [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioRenderClient
        {
            [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
            [PreserveSig] int ReleaseBuffer(uint frames, int flags);
        }
        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }
        [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDeviceTopology
        {
            [PreserveSig] int GetConnectorCount(out uint count);
            [PreserveSig] int GetConnector(uint index, out IConnector connector);
        }
        [ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IConnector
        {
            [PreserveSig] int GetType(out int type);
            [PreserveSig] int GetDataFlow(out int flow);
            [PreserveSig] int ConnectTo(IConnector other);
            [PreserveSig] int Disconnect();
            [PreserveSig] int IsConnected(out int connected);
            [PreserveSig] int GetConnectedTo(out IConnector connected);
            [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }
        [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMNotificationClient
        {
            [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
            [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
        }
        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class Notifications : IMMNotificationClient
        {
            private LowLatencyAudioNative owner;
            internal Notifications(LowLatencyAudioNative owner) { this.owner = owner; }
            internal void Detach() { Interlocked.Exchange(ref owner, null); }
            public int OnDeviceStateChanged(string id, uint state) { var value = owner; if (value != null) value.Changed(id, false); return 0; }
            public int OnDeviceAdded(string id) { return 0; }
            public int OnDeviceRemoved(string id) { var value = owner; if (value != null) value.Changed(id, false); return 0; }
            public int OnDefaultDeviceChanged(int flow, int role, string id)
            { var value = owner; if (value != null && flow == 0 && role == 0) value.Changed(id, true); return 0; }
            public int OnPropertyValueChanged(string id, PropertyKey key) { return 0; }
        }
    }
}
