using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class AppGpuPreferences
    {
        private sealed class SettingsAppGpuPreferenceLedger : IAppGpuPreferenceLedger
        {
            public bool TryRead(out string value)
            {
                return Settings.TryLoadStr("AppGpuPreferencesV1", out value);
            }

            public bool TryWrite(string value)
            {
                return Settings.SaveStr("AppGpuPreferencesV1", value);
            }
        }

        private sealed class WindowsAppGpuPreferenceControl : IAppGpuPreferenceControl
        {

            public bool Supported
            {
                get
                {
                    RefuseNativeInTests();
                    return ConfirmedHybrid(GpuInventory.Adapters());
                }
            }

            public bool IsExecutablePresent(string path)
            {
                RefuseNativeInTests();
                return File.Exists(path);
            }

            public bool TryGetStagedOriginal(string path, out bool staged, out string original)
            {
                RefuseNativeInTests();
                return GpuPrefStage.TryGetStagedOriginal(path, out staged, out original);
            }

            public bool TryReleaseStage(string path)
            {
                RefuseNativeInTests();
                return GpuPrefStage.TryReleaseForManualPreference(path);
            }

            public bool TryRead(string path, out string value)
            {
                RefuseNativeInTests();
                value = null;
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\DirectX\\UserGpuPreferences"))
                    {
                        return Read(key, path, out value);
                    }
                }
                catch
                {
                    return false;
                }
            }

            private static bool Read(RegistryKey key, string path, out string value)
            {
                value = null;
                if (key == null)
                {
                    return true;
                }
                object instance = key.GetValue(path, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (instance == null)
                {
                    return true;
                }
                if (!(instance is string) || key.GetValueKind(path) != RegistryValueKind.String)
                {
                    return false;
                }
                value = (string)instance;
                return true;
            }

            public AppGpuPreferenceWriteResult CompareExchange(string path, string expected, string replacement)
            {
                RefuseNativeInTests();
                bool writeIssued = false;
                try
                {
                    using (RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("SOFTWARE\\Microsoft\\DirectX\\UserGpuPreferences"))
                    {
                        if (registryKey == null)
                        {
                            return AppGpuPreferenceWriteResult.NotIssued;
                        }
                        string text;
                        if (!Read(registryKey, path, out text))
                        {
                            return AppGpuPreferenceWriteResult.NotIssued;
                        }
                        if (!string.Equals(text, expected, StringComparison.Ordinal))
                        {
                            return AppGpuPreferenceWriteResult.Changed;
                        }
                        writeIssued = true;
                        if (replacement == null)
                        {
                            registryKey.DeleteValue(path, false);
                        }
                        else
                        {
                            registryKey.SetValue(path, replacement, RegistryValueKind.String);
                        }
                        return AppGpuPreferenceWriteResult.Written;
                    }
                }
                catch
                {
                    return (!writeIssued) ? AppGpuPreferenceWriteResult.NotIssued : AppGpuPreferenceWriteResult.Unknown;
                }
            }

            private static void RefuseNativeInTests()
            {
            }
        }

        internal const string LedgerKey = "AppGpuPreferencesV1";

        private static readonly AppGpuPreferenceManager shared = new AppGpuPreferenceManager(new WindowsAppGpuPreferenceControl(), new SettingsAppGpuPreferenceLedger());

        public static AppGpuPreferenceManager Shared
        {
            get
            {
                return shared;
            }
        }

        public static bool HasResidue
        {
            get
            {
                return Shared.HasResidue;
            }
        }

        public static bool HasManagedPath(string path)
        {
            return Shared.HasManagedPath(path);
        }

        public static bool RestoreAll()
        {
            return Shared.RestoreAll();
        }

        public static bool AbandonUnprovableForReset()
        {
            return Shared.AbandonUnprovableForReset();
        }

        public static bool HealFromCrash()
        {
            return Shared.HealFromCrash();
        }

        internal static bool ConfirmedHybrid(IEnumerable<GpuAdapter> adapters)
        {
            if (adapters == null)
            {
                return false;
            }
            HashSet<string> textsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> currentTextsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GpuAdapter adapter in adapters)
            {
                if (adapter != null && adapter.IntegratedKnown && !string.IsNullOrEmpty(adapter.HardwareId))
                {
                    (adapter.Integrated ? textsSet : currentTextsSet).Add(adapter.HardwareId);
                }
            }
            foreach (string candidateTextsSet in textsSet)
            {
                foreach (string resolvedTextsSet in currentTextsSet)
                {
                    if (!string.Equals(candidateTextsSet, resolvedTextsSet, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
