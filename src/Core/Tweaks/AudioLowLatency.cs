// @author bdth 2074055628@qq.com
// File purpose Opens a silent stream with the minimum shared buffer during the match so the audio engine runs at its minimum period; closing the stream restores automatically
using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class AudioLowLatency
    {
        // The shared-mode engine period is the minimum requested by all active streams; this stream writes no data, what mixes into the output is silence
        //   Once the stream is released with the process the engine returns to the default period; no registry writes, no cross-process residue
        private const int RenderFlow = 0;
        private const int ConsoleRole = 0;
        private const int ClsCtxAll = 0x17;
        private const long DriftCheckIntervalTicks = 5L * TimeSpan.TicksPerSecond;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object activated);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        // The three interface generations share one vtable; must be declared in full in IAudioClient IAudioClient2 IAudioClient3 order
        //   Before Win10 Activate can't get this interface, treated as unsupported
        [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient3
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration,
                long periodicity, IntPtr format, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
            [PreserveSig] int IsOffloadCapable(int category, out int capable);
            [PreserveSig] int SetClientProperties(IntPtr properties);
            [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven,
                out long minDuration, out long maxDuration);
            [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultFrames,
                out uint fundamentalFrames, out uint minFrames, out uint maxFrames);
            [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentFrames);
            [PreserveSig] int InitializeSharedAudioStream(int streamFlags, uint periodFrames,
                IntPtr format, IntPtr audioSessionGuid);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public int PropertyId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort Type;
            public ushort Reserved1, Reserved2, Reserved3;
            public IntPtr Pointer;
            public IntPtr Pointer2;
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        // PKEY_Device_EnumeratorName: Bluetooth endpoints sit under BTHENUM or BTHHFENUM
        private static readonly Guid DeviceEnumeratorNameFmt = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
        private const int DeviceEnumeratorNamePid = 24;
        private const int StgmRead = 0;
        private const ushort VtLpwstr = 31;

        private static string EnumeratorName(IMMDevice device)
        {
            IntPtr storePtr;
            if (device.OpenPropertyStore(StgmRead, out storePtr) != 0 || storePtr == IntPtr.Zero) return null;
            IPropertyStore store = null;
            try
            {
                store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePtr);
                var key = new PropertyKey { FormatId = DeviceEnumeratorNameFmt, PropertyId = DeviceEnumeratorNamePid };
                PropVariant value;
                if (store.GetValue(ref key, out value) != 0) return null;
                try
                {
                    return value.Type == VtLpwstr && value.Pointer != IntPtr.Zero
                        ? Marshal.PtrToStringUni(value.Pointer) : null;
                }
                finally { PropVariantClear(ref value); }
            }
            catch { return null; }
            finally
            {
                if (store != null) Marshal.ReleaseComObject(store);
                Marshal.Release(storePtr);
            }
        }

        // Bluetooth output buffering is set by the link; a changed engine period never reaches the ear; not applicable, not a failure
        internal static bool IsBluetoothEnumerator(string enumerator)
        {
            if (string.IsNullOrEmpty(enumerator)) return false;
            return string.Equals(enumerator, "BTHENUM", StringComparison.OrdinalIgnoreCase)
                || string.Equals(enumerator, "BTHHFENUM", StringComparison.OrdinalIgnoreCase)
                || string.Equals(enumerator, "BTHLEDEVICE", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly object lk = new object();
        private static IAudioClient3 client;
        private static string deviceId;
        private static bool active;
        private static long driftCheckedTicks;
        private static bool driftCached;
        private static bool loggedNoGain;
        private static bool loggedBluetooth;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active && !DriftedLocked()) return true;
                if (active) ReleaseLocked();
                string id;
                IMMDevice device = DefaultRenderDevice(out id);
                if (device == null) return false;
                IAudioClient3 opened = null;
                IntPtr fmt = IntPtr.Zero;
                try
                {
                    if (IsBluetoothEnumerator(EnumeratorName(device)))
                    {
                        if (!loggedBluetooth)
                        {
                            loggedBluetooth = true;
                            Logger.Log(Lang.T("log.audiolat.4"));
                        }
                        return true;
                    }
                    Guid iid = typeof(IAudioClient3).GUID;
                    object raw;
                    if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out raw) != 0 || raw == null)
                        return false;
                    opened = (IAudioClient3)raw;
                    if (opened.GetMixFormat(out fmt) != 0 || fmt == IntPtr.Zero) return false;
                    uint def, fund, min, max;
                    if (opened.GetSharedModeEnginePeriod(fmt, out def, out fund, out min, out max) != 0)
                        return false;
                    if (!WorthApplying(def, min))
                    {
                        // A device that can't do a smaller buffer is not applicable, not a failure; report success but keep no state
                        //   Reporting failure would go through retry and the breaker, guaranteeing warnings on machines with this enabled
                        //   No more probing this match; retry next match after the match-end restore; a device change re-evaluates naturally
                        if (!loggedNoGain)
                        {
                            loggedNoGain = true;
                            Logger.Log(Lang.T("log.audiolat.3"));
                        }
                        return true;
                    }
                    if (opened.InitializeSharedAudioStream(0, min, fmt, IntPtr.Zero) != 0) return false;
                    if (opened.Start() != 0) return false;
                    int rate = Marshal.ReadInt32(fmt, 4); // WAVEFORMATEX.nSamplesPerSec
                    client = opened;
                    opened = null;
                    deviceId = id;
                    active = true;
                    driftCached = false;
                    driftCheckedTicks = DateTime.UtcNow.Ticks;
                    Logger.Log(Lang.T("log.audiolat.1") + PeriodText(def, min, rate));
                    return true;
                }
                catch { return false; }
                finally
                {
                    if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                    if (opened != null) Marshal.ReleaseComObject(opened);
                    Marshal.ReleaseComObject(device);
                }
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (!active) return true;
                ReleaseLocked();
                Logger.Log(Lang.T("log.audiolat.2"));
                return true;
            }
        }

        // After the default device switches away the old stream can't pin the new device's engine; on drift close the old stream and reopen
        public static bool DeviceDrifted
        {
            get { lock (lk) { return active && DriftedLocked(); } }
        }

        private static bool DriftedLocked()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now - driftCheckedTicks < DriftCheckIntervalTicks) return driftCached;
            driftCheckedTicks = now;
            string current;
            IMMDevice device = DefaultRenderDevice(out current);
            if (device != null) Marshal.ReleaseComObject(device);
            driftCached = device == null || !string.Equals(current, deviceId, StringComparison.Ordinal);
            return driftCached;
        }

        private static void ReleaseLocked()
        {
            try { if (client != null) client.Stop(); }
            catch { }
            try { if (client != null) Marshal.ReleaseComObject(client); }
            catch { }
            client = null;
            deviceId = null;
            active = false;
        }

        private static IMMDevice DefaultRenderDevice(out string id)
        {
            id = null;
            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorCom();
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(RenderFlow, ConsoleRole, out device) != 0
                    || device == null) return null;
                if (device.GetId(out id) != 0 || string.IsNullOrEmpty(id))
                {
                    Marshal.ReleaseComObject(device);
                    return null;
                }
                return device;
            }
            catch { return null; }
            finally
            {
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        // When the device's minimum period isn't below the default, opening a stream is all cost and no gain
        internal static bool WorthApplying(uint defaultFrames, uint minFrames)
        {
            return minFrames != 0 && minFrames < defaultFrames;
        }

        internal static string PeriodText(uint defaultFrames, uint minFrames, int sampleRate)
        {
            if (sampleRate <= 0) return defaultFrames + "->" + minFrames;
            return FramesMs(defaultFrames, sampleRate) + "ms->" + FramesMs(minFrames, sampleRate) + "ms";
        }

        internal static string FramesMs(uint frames, int sampleRate)
        {
            double ms = frames * 1000.0 / sampleRate;
            return ms.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
