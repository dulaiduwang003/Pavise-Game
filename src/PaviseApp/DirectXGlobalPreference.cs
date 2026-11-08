using System;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
	internal sealed class DirectXGlobalPreference
	{
		private const string GpuKey = "SOFTWARE\\Microsoft\\DirectX\\UserGpuPreferences";

		private const string ValueName = "DirectXUserGlobalSettings";

		private static readonly object gate = new object();

		internal static readonly DirectXGlobalPreference Windowed = new DirectXGlobalPreference("SwapEffectUpgradeEnable", 22621, true);

		internal static readonly DirectXGlobalPreference Vrr = new DirectXGlobalPreference("VRROptimizeEnable", 18362, false);

		private readonly bool defaultOn;

        private readonly IGraphicsPreferenceStore store;
        private readonly GraphicsPreferenceTransaction transaction;
        private readonly Func<bool> supported;

        private sealed class NativeStore : IGraphicsPreferenceStore
        {
            private readonly string field, slot;
            internal NativeStore(string field, string slot) { this.field = field; this.slot = slot; }
            public string ReadValue()
            {
                string value = PrefFieldText.ReadField(Read(), field);
                if (value != null && value != "0" && value != "1") throw new InvalidOperationException("Unknown graphics value");
                return value;
            }
            public bool WriteValue(string value)
            {
                if (value != null && value != "0" && value != "1") return false;
                string current = Read();
                string next = value == null ? PrefFieldText.RemoveField(current, field) : PrefFieldText.MergeField(current, field, value);
                Write(string.IsNullOrEmpty(next) ? null : next);
                return ReadValue() == value;
            }
            public string LoadReceipt()
            {
                string receipt = Settings.LoadStr(slot, "");
                if (receipt == "absent") return GraphicsPreferenceTransaction.Receipt(null, "1", 'A');
                if (receipt.StartsWith("value:", StringComparison.Ordinal))
                {
                    try { return GraphicsPreferenceTransaction.Receipt(Encoding.UTF8.GetString(Convert.FromBase64String(receipt.Substring(6))), "1", 'A'); }
                    catch { return receipt; }
                }
                return receipt;
            }
            public bool SaveReceipt(string value) { return Settings.SaveStrDurable(slot, value); }
        }

		internal bool Supported
		{
			get
			{
				return supported();
			}
		}

		internal bool HasResidue
		{
			get
			{
				return store.LoadReceipt().Length > 0;
			}
		}

		private DirectXGlobalPreference(string field, int minBuild, bool defaultOn)

            : this(field, defaultOn, delegate { return Native.OsBuild() >= minBuild; }, new NativeStore(field, "DirectXOwned_" + field))

        { }

        internal DirectXGlobalPreference(string field, bool defaultOn, Func<bool> supported, IGraphicsPreferenceStore store)
		{
			this.defaultOn = defaultOn;
            this.store = store;
            this.supported = supported;
            transaction = new GraphicsPreferenceTransaction(store);
		}

		internal bool TryReadState(out bool on)
		{
			on = false;
			try
			{
				string value = store.ReadValue();
				if (value != null && value != "0" && value != "1")
				{
					return false;
				}
				on = value == "1" || (value == null && Supported && defaultOn);
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal bool Enable()
		{
			return Set(true);
		}

        internal bool Disable() { return Set(false); }

        private bool Set(bool enabled)
        {
            lock (gate)
            {
                try
                {
                    bool on;
                    if (!Supported || !TryReadState(out on)) return false;
                    if (on == enabled && store.LoadReceipt().Length == 0) return true;
                    return transaction.Set(enabled ? "1" : "0");
                }
                catch { return false; }
            }
        }

        internal bool Restore() { lock (gate) { return transaction.Restore(); } }

		private static string Read()
		{
			using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\DirectX\\UserGpuPreferences"))
			{
				object value = ((registryKey == null) ? null : registryKey.GetValue("DirectXUserGlobalSettings"));
				if (value != null && !(value is string))
				{
					throw new InvalidOperationException("Unexpected graphics preference type");
				}
				return value as string;
			}
		}

		private static void Write(string value)
		{
			using (RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("SOFTWARE\\Microsoft\\DirectX\\UserGpuPreferences"))
			{
				if (registryKey == null)
				{
					throw new InvalidOperationException("Graphics preferences unavailable");
				}
				if (value == null)
				{
					registryKey.DeleteValue("DirectXUserGlobalSettings", false);
				}
				else
				{
					registryKey.SetValue("DirectXUserGlobalSettings", value, RegistryValueKind.String);
				}
			}
		}
	}
}
