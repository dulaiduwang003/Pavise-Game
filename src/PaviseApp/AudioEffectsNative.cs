using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class AudioEffectsNative : IAudioEffectsNative
    {
        private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid PolicyClass = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
        private static AudioEffectsPropertyKey SysFx = new AudioEffectsPropertyKey("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E", 5);
        private static AudioEffectsPropertyKey FriendlyName = new AudioEffectsPropertyKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);

        public bool DefaultEndpoint(out AudioEffectsEndpoint endpoint, out string error)
        { return Query(null, true, out endpoint, out error); }
        public bool Read(string id, out AudioEffectsEndpoint endpoint, out string error)
        { return Query(id, false, out endpoint, out error); }

        private bool Query(string id, bool useDefault, out AudioEffectsEndpoint endpoint, out string error)
        {
            endpoint = null; error = "";
            if (IntPtr.Size != 8) { error = T("音效端点后端仅支持 64 位进程", "Audio-effects endpoint backend requires a 64-bit process", "音声効果のバックエンドには64ビットプロセスが必要です"); return false; }
            object owner = null; IAudioEffectsDevice device = null; IAudioEffectsPropertyStore properties = null, effects = null;
            try
            {
                owner = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass));
                var enumerator = (IAudioEffectsEnumerator)owner;
                int hr = useDefault ? enumerator.GetDefaultAudioEndpoint(0, 1, out device) : enumerator.GetDevice(id, out device);
                if (hr < 0) { error = HResult("Output endpoint", hr); return false; }
                string endpointId; uint state;
                if ((hr = device.GetId(out endpointId)) < 0 || (hr = device.GetState(out state)) < 0) { error = HResult("Endpoint identity", hr); return false; }
                if ((hr = device.OpenPropertyStore(0, out properties)) < 0) { error = HResult("Endpoint properties", hr); return false; }
                AudioEffectsVariant value;
                string name = endpointId;
                if (properties.GetValue(ref FriendlyName, out value) >= 0)
                {
                    try { if (value.Type == 31 && value.Pointer != IntPtr.Zero) name = Marshal.PtrToStringUni(value.Pointer); }
                    finally { PropVariantClear(ref value); }
                }
                // FX 属性必须通过专用接口读写；普通端点属性的读回不能证明效果已应用。
                // 此接口使用非公开 ABI，调用前需核验接口支持。
                var local = device as IAudioEffectsLocalEndpoint;
                bool policy = local == null;
                if (policy)
                {
                    if (!ReadPolicyFx(endpointId, out value, out error)) return false;
                }
                else
                {
                    if ((hr = local.OpenFXPropertyStore(0, out effects)) < 0) { error = HResult("Readable FX properties", hr); return false; }
                    if ((hr = effects.GetValue(ref SysFx, out value)) < 0) { error = HResult("FX system-effects property", hr); return false; }
                }
                AudioEffectsProperty property;
                try
                {
                    if (!DecodeProperty(value.Type, value.UInt32, out property))
                    { error = T("系统效果属性类型或值不受支持", "System-effects property type or value is unsupported", "システム効果のプロパティ型または値に対応していません"); return false; }
                }
                finally { PropVariantClear(ref value); }
                // VT_EMPTY is a valid original state; read the FX store for both apply and restore.
                if (!VerifyFxProperty(endpointId, property, out error)) return false;
                bool legacy, modern; string identity;
                if (!EffectsIdentity(endpointId, out legacy, out modern, out identity, out error)) return false;
                endpoint = new AudioEffectsEndpoint { Id = endpointId, Name = name ?? endpointId, Active = state == 1,
                    Property = property, LegacyEffects = legacy, ModernEffects = modern, EffectIdentity = identity };
                return true;
            }
            catch (Exception ex) { error = T("无法核验音效端点：", "Cannot verify audio-effects endpoint: ", "音声効果のエンドポイントを確認できません: ") + ex.GetType().Name; return false; }
            finally { Release(effects); Release(properties); Release(device); Release(owner); }
        }

        public bool Write(string id, AudioEffectsProperty property, out string error)
        {
            error = "";
            if (IntPtr.Size != 8) { error = T("音效端点后端仅支持 64 位进程", "Audio-effects endpoint backend requires a 64-bit process", "音声効果のバックエンドには64ビットプロセスが必要です"); return false; }
            if (property != AudioEffectsProperty.Empty && property != AudioEffectsProperty.Enabled && property != AudioEffectsProperty.Disabled)
            { error = "Unsupported audio-effects property"; return false; }
            object owner = null; IAudioEffectsDevice device = null; IAudioEffectsPropertyStore properties = null;
            try
            {
                owner = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass));
                int hr = ((IAudioEffectsEnumerator)owner).GetDevice(id, out device);
                if (hr < 0) { error = HResult("Output endpoint", hr); return false; }
                var local = device as IAudioEffectsLocalEndpoint;
                if (local == null) return WritePolicyFx(id, property, out error);
                if ((hr = local.OpenFXPropertyStore(2, out properties)) < 0) { error = HResult("Writable FX properties", hr); return false; }
                ushort variantType; uint variantNumber;
                EncodeProperty(property, out variantType, out variantNumber);
                var value = new AudioEffectsVariant { Type = variantType, UInt32 = variantNumber };
                if ((hr = properties.SetValue(ref SysFx, ref value)) < 0 || (hr = properties.Commit()) < 0)
                { error = HResult("System-effects commit", hr); return false; }
                return true;
            }
            catch (Exception ex) { error = T("音效设置失败：", "Audio-effects write failed: ", "音声効果の設定に失敗しました: ") + ex.GetType().Name; return false; }
            finally { Release(properties); Release(device); Release(owner); }
        }

        private static bool ReadPolicyFx(string id, out AudioEffectsVariant value, out string error)
        {
            value = new AudioEffectsVariant(); error = ""; object owner = null;
            try
            {
                owner = Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyClass));
                var policy = owner as IAudioEffectsPolicyConfig;
                if (policy == null) { error = UnsupportedPolicy(); return false; }
                int hr = policy.GetPropertyValue(id, true, ref SysFx, out value);
                if (hr < 0) { PropVariantClear(ref value); error = HResult("FX policy property", hr); return false; }
                return true;
            }
            catch (Exception ex) { PropVariantClear(ref value); error = UnsupportedPolicy() + " (" + ex.GetType().Name + ")"; return false; }
            finally { Release(owner); }
        }

        private static bool WritePolicyFx(string id, AudioEffectsProperty property, out string error)
        {
            error = ""; object owner = null; AudioEffectsVariant before;
            if (!ReadPolicyFx(id, out before, out error)) return false;
            try
            {
                AudioEffectsProperty current;
                if (!DecodeProperty(before.Type, before.UInt32, out current))
                { error = T("音效主开关类型或值不受支持", "FX master-switch type or value is unsupported", "効果マスタースイッチの型または値に対応していません"); return false; }
                if (!VerifyFxProperty(id, current, out error)) return false;
                owner = Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyClass));
                var policy = owner as IAudioEffectsPolicyConfig;
                if (policy == null) { error = UnsupportedPolicy(); return false; }
                // Set vt explicitly: an absent property remains VT_EMPTY until typed.
                // Restore absence with VT_EMPTY; restore explicit Enabled with DWORD 0.
                ushort variantType; uint variantNumber;
                EncodeProperty(property, out variantType, out variantNumber);
                var value = new AudioEffectsVariant { Type = variantType, UInt32 = variantNumber };
                int hr = policy.SetPropertyValue(id, true, ref SysFx, ref value);
                if (hr < 0) { error = HResult("FX policy write", hr); return false; }
                // Verify both scalar and APO-chain identity through the same FX route.
                return true;
            }
            catch (Exception ex) { error = UnsupportedPolicy() + " (" + ex.GetType().Name + ")"; return false; }
            finally { PropVariantClear(ref before); Release(owner); }
        }

        private static string UnsupportedPolicy()
        { return T("当前输出未提供已适配的音效配置接口", "Current output has no adapted FX configuration interface", "現在の出力に対応済みの音声効果設定インターフェイスがありません"); }

        private static bool VerifyFxProperty(string id, AudioEffectsProperty property, out string error)
        {
            error = ""; int start = id.LastIndexOf('{'); Guid endpoint;
            if (start < 0 || !Guid.TryParse(id.Substring(start), out endpoint)) { error = "Invalid FX endpoint identity"; return false; }
            string path = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\Render\\" + endpoint.ToString("B") + "\\FxProperties";
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path, false))
            {
                const string name = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
                if (key == null) { error = T("真实音效存储不可用", "Actual FX store is unavailable", "実際の効果ストアを利用できません"); return false; }
                bool exists = false;
                foreach (string valueName in key.GetValueNames()) if (valueName.Equals(name, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                object raw = exists ? key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
                bool dword = exists && raw is int && key.GetValueKind(name) == RegistryValueKind.DWord;
                if (FxPropertyMatches(property, exists, dword, dword ? (int)raw : 0)) return true;
            }
            error = T("音效接口与真实音效存储不一致，未确认修改", "FX interface and actual FX store do not match; change is unconfirmed", "効果インターフェイスと実際の効果ストアが一致しないため、変更を確認できません");
            return false;
        }
        internal static bool FxPropertyMatches(AudioEffectsProperty property, bool exists, bool dword, int raw)
        { return property == AudioEffectsProperty.Empty ? !exists : exists && dword && raw >= 0 && raw <= 1
            && (property == AudioEffectsProperty.Enabled || property == AudioEffectsProperty.Disabled)
            && (raw == 1) == (property == AudioEffectsProperty.Disabled); }

        internal static bool DecodeProperty(ushort type, uint number, out AudioEffectsProperty property)
        {
            property = AudioEffectsProperty.Empty;
            if (type == 0) return true;
            if (type != 19 || number > 1) return false;
            property = number == 0 ? AudioEffectsProperty.Enabled : AudioEffectsProperty.Disabled; return true;
        }
        internal static void EncodeProperty(AudioEffectsProperty property, out ushort type, out uint number)
        {
            if (property != AudioEffectsProperty.Empty && property != AudioEffectsProperty.Enabled && property != AudioEffectsProperty.Disabled)
                throw new ArgumentOutOfRangeException("property");
            type = property == AudioEffectsProperty.Empty ? (ushort)0 : (ushort)19;
            number = property == AudioEffectsProperty.Disabled ? 1u : 0u;
        }

        private static bool EffectsIdentity(string id, out bool legacy, out bool modern, out string identity, out string error)
        {
            legacy = modern = false; identity = ""; error = "";
            int start = id.LastIndexOf('{'); Guid endpoint;
            if (start < 0 || !Guid.TryParse(id.Substring(start), out endpoint)) { error = "Invalid endpoint identity"; return false; }
            string path = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\Render\\" + endpoint.ToString("B") + "\\FxProperties";
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path, false))
            {
                if (key == null) { identity = "none"; return true; }
                string[] names = key.GetValueNames(); Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                var stable = new StringBuilder();
                foreach (string name in names)
                {
                    // The leased scalar can live in FxProperties; it is separate from APO-chain identity.
                    if (name.Equals("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5", StringComparison.OrdinalIgnoreCase)) continue;
                    bool effectRegistration = false, processingModes = false;
                    foreach (int propertyId in new[] { 1, 2, 5, 6, 7 })
                        effectRegistration |= name.Equals("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}," + propertyId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
                    foreach (int propertyId in new[] { 5, 6, 7 })
                        processingModes |= name.Equals("{d3993a3f-99c2-4402-b5ec-a92a0367664b}," + propertyId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
                    // Bind recovery to the registered APO chain/modes; per-effect and UI values remain user-owned.
                    if (!effectRegistration && !processingModes) continue;
                    object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null) continue;
                    string formatted;
                    byte[] bytes = value as byte[]; string[] list = value as string[];
                    if (bytes != null) formatted = Convert.ToBase64String(bytes);
                    else if (list != null) formatted = string.Join("\n", list);
                    else formatted = Convert.ToString(value, CultureInfo.InvariantCulture);
                    stable.Append(name.ToUpperInvariant()).Append('|').Append((int)key.GetValueKind(name)).Append('|').Append(formatted).Append('\n');
                    if (name.Equals("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2", StringComparison.OrdinalIgnoreCase))
                    { Guid effect; if (value is string && Guid.TryParse((string)value, out effect) && effect != Guid.Empty) legacy = true; }
                    // Modern SFX/MFX use this same master flag in Microsoft's SysVAD
                    // GetCurrentEffectsSetting/notification sample. Registration is
                    // admission for a bypass *request*, not proof of a changed graph.
                    // https://github.com/microsoft/Windows-driver-samples/blob/main/audio/sysvad/APO/SwapAPO/swapapomfx.cpp
                    if (name.Equals("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7", StringComparison.OrdinalIgnoreCase))
                    { Guid effect; if (value is string && Guid.TryParse((string)value, out effect) && effect != Guid.Empty) modern = true; }
                }
                using (var hash = SHA256.Create()) identity = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(stable.ToString())));
                return true;
            }
        }
        private static string T(string zh, string en, string ja) { return LaboratoryActionText.Pick(zh, en, ja); }
        private static string HResult(string operation, int hr)
        { return operation + " (0x" + hr.ToString("X8", CultureInfo.InvariantCulture) + ")"; }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref AudioEffectsVariant value);

        [StructLayout(LayoutKind.Sequential)] private struct AudioEffectsPropertyKey
        { internal Guid Format; internal uint Id; internal AudioEffectsPropertyKey(string format, uint id) { Format = new Guid(format); Id = id; } }
        [StructLayout(LayoutKind.Explicit, Size = 24)] private struct AudioEffectsVariant
        { [FieldOffset(0)] internal ushort Type; [FieldOffset(8)] internal uint UInt32; [FieldOffset(8)] internal IntPtr Pointer; }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAudioEffectsEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IAudioEffectsDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IAudioEffectsDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAudioEffectsDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, out IntPtr instance);
            [PreserveSig] int OpenPropertyStore(uint access, out IAudioEffectsPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAudioEffectsPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out AudioEffectsPropertyKey key);
            [PreserveSig] int GetValue(ref AudioEffectsPropertyKey key, out AudioEffectsVariant value);
            [PreserveSig] int SetValue(ref AudioEffectsPropertyKey key, ref AudioEffectsVariant value);
            [PreserveSig] int Commit();
        }
        // ABI observed in the maintained ToggleSysFx implementation. QueryInterface
        // is mandatory; unsupported Windows/endpoints fail closed. No fallback writes
        // to ordinary properties or directly to MMDevices registry keys are allowed.
        // https://github.com/thebookisclosed/ToggleSysFx/blob/main/MMDeviceApi.cs
        [ComImport, Guid("67C5FC9C-29E1-4154-8307-84ED8EDB5A21"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAudioEffectsLocalEndpoint
        {
            [PreserveSig] int IsEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
            [PreserveSig] int GetInternalState(out uint state);
            [PreserveSig] int OpenFXPropertyStore(uint access, out IAudioEffectsPropertyStore effects);
        }
        // Private Windows 10 ABI with an explicit FX-store selector. It is never
        // substituted with IMMDevice's ordinary property bag. Exact vtable is
        // independently documented by the Win10 21H1 reproducer and maintained
        // AudioDeviceCmdlets source; capability/readback gates still apply.
        // https://learn.microsoft.com/en-us/answers/questions/669471/how-to-control-enable-audio-enhancements-with-code
        // https://github.com/frgnca/AudioDeviceCmdlets/blob/master/SOURCE/IPolicyConfig.cs
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAudioEffectsPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool defaults, out IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format, IntPtr mix);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool defaults, IntPtr defaultPeriod, IntPtr minimumPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fx, ref AudioEffectsPropertyKey key, out AudioEffectsVariant value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fx, ref AudioEffectsPropertyKey key, ref AudioEffectsVariant value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool visible);
        }
    }
}
