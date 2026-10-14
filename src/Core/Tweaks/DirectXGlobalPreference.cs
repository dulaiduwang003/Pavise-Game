using System;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    // 两个图形选项共享注册表值，按字段串行修改。
    internal sealed class DirectXGlobalPreference
    {
        private const string GpuKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
        private const string ValueName = "DirectXUserGlobalSettings";
        private static readonly object gate = new object();
        internal static readonly DirectXGlobalPreference Windowed = new DirectXGlobalPreference("SwapEffectUpgradeEnable", 22621, true);
        internal static readonly DirectXGlobalPreference Vrr = new DirectXGlobalPreference("VRROptimizeEnable", 18362, false);
        private readonly string field, slot;
        private readonly int minBuild;
        private readonly bool defaultOn;
#if PAVISE_SELFTEST
        internal static Func<string> ReadForTest;
        internal static Action<string> WriteForTest;
        internal static int? OsBuildForTest;
#endif
        private DirectXGlobalPreference(string field, int minBuild, bool defaultOn)
        { this.field = field; this.slot = "DirectXOwned_" + field; this.minBuild = minBuild; this.defaultOn = defaultOn; }
        internal bool Supported
        {
            get
            {
#if PAVISE_SELFTEST
                if (OsBuildForTest.HasValue) return OsBuildForTest.Value >= minBuild;
#endif
                return Native.OsBuild() >= minBuild;
            }
        }
        internal bool HasResidue { get { return Settings.LoadStr(slot, "").Length > 0; } }
        internal bool TryReadState(out bool on)
        {
            on = false;
            try
            {
                string value = PrefFieldText.ReadField(Read(), field);
                if (value != null && value != "0" && value != "1") return false;
                on = value == "1" || (value == null && Supported && defaultOn);
                return true;
            }
            catch { return false; }
        }
        internal bool Enable()
        {
            lock (gate)
            {
                try
                {
                    if (!Supported) return false;
                    bool on;
                    if (!TryReadState(out on)) return false;
                    if (on) return true;
                    if (HasResidue && !Restore()) return false;
                    string current = Read();
                    string original = PrefFieldText.ReadField(current, field);
                    string receipt = original == null ? "absent" : "value:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(original));
                    if (!Settings.SaveStr(slot, receipt)) return false;
                    Write(PrefFieldText.MergeField(current, field, "1"));
                    return PrefFieldText.ReadField(Read(), field) == "1";
                }
                catch { return false; }
            }
        }
        internal bool Restore()
        {
            lock (gate)
            {
                try
                {
                    string receipt = Settings.LoadStr(slot, "");
                    if (receipt.Length == 0) return true;
                    string original;
                    if (receipt == "absent") original = null;
                    else if (receipt.StartsWith("value:", StringComparison.Ordinal))
                        original = Encoding.UTF8.GetString(Convert.FromBase64String(receipt.Substring(6)));
                    else return false;
                    string current = Read();
                    // 用户后续更改优先，不能整值覆盖。
                    if (PrefFieldText.ReadField(current, field) == "1")
                    {
                        string next = original == null ? PrefFieldText.RemoveField(current, field)
                            : PrefFieldText.MergeField(current, field, original);
                        Write(next.Length == 0 ? null : next);
                        if (PrefFieldText.ReadField(Read(), field) != original) return false;
                    }
                    return Settings.SaveStr(slot, "");
                }
                catch { return false; }
            }
        }
        private static string Read()
        {
#if PAVISE_SELFTEST
            return ReadForTest == null ? null : ReadForTest();
#else
            using (var key = Registry.CurrentUser.OpenSubKey(GpuKey))
            {
                object value = key == null ? null : key.GetValue(ValueName);
                if (value != null && !(value is string)) throw new InvalidOperationException("Unexpected graphics preference type");
                return value as string;
            }
#endif
        }
        private static void Write(string value)
        {
#if PAVISE_SELFTEST
            if (WriteForTest == null) throw new InvalidOperationException("Graphics writes must be mocked");
            WriteForTest(value);
#else
            using (var key = Registry.CurrentUser.CreateSubKey(GpuKey))
            {
                if (key == null) throw new InvalidOperationException("Graphics preferences unavailable");
                if (value == null) key.DeleteValue(ValueName, false);
                else key.SetValue(ValueName, value, RegistryValueKind.String);
            }
#endif
        }
    }
}
