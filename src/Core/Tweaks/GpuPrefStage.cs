// @author bdth 2074055628@qq.com
// 文件用途 待命阶段给游戏预选高性能显卡 双显卡机器专用 退场或撤防即还原
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class GpuPrefStage
    {
        private const string GpuKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
        private const string JournalKey = "GpuPrefStage";
        private static readonly object lk = new object();

        public static bool Supported { get { return GpuInventory.Hybrid; } }
        public static bool HasResidue { get { return Settings.LoadStr(JournalKey, "").Length > 0; } }

        public static bool Stage(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return true;
            lock (lk)
            {
                if (!Supported) return true;
                string stagedPath, stagedOriginal;
                if (DecodeJournal(Settings.LoadStr(JournalKey, ""), out stagedPath, out stagedOriginal))
                {
                    if (string.Equals(stagedPath, exePath, StringComparison.OrdinalIgnoreCase)) return true;
                    if (!RestoreLocked()) return false;
                }
                string shown = exePath;
                try { shown = System.IO.Path.GetFileName(exePath); } catch { }
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(GpuKey))
                    {
                        if (k == null) return false;
                        string cur = k.GetValue(exePath) as string;
                        string pref = PrefFieldText.ReadField(cur, "GpuPreference");
                        if (pref == "1" || pref == "2") return true;
                        string line = EncodeJournal(exePath, cur);
                        if (!Settings.SaveStr(JournalKey, line)
                            || Settings.LoadStr(JournalKey, "") != line) return false;
                        k.SetValue(exePath, PrefFieldText.MergeField(cur, "GpuPreference", "2"),
                            RegistryValueKind.String);
                        Logger.Log(Lang.T("log.gpuprefstage.1") + shown + Lang.T("log.gpuprefstage.2"));
                        return true;
                    }
                }
                catch { return false; }
            }
        }

        public static bool Restore()
        {
            lock (lk) return RestoreLocked();
        }

        public static void HealFromCrash()
        {
            lock (lk) if (HasResidue && RestoreLocked()) Logger.Log(Lang.T("log.gpuprefstage.5"));
        }

        private static bool RestoreLocked()
        {
            string exePath, original;
            if (!DecodeJournal(Settings.LoadStr(JournalKey, ""), out exePath, out original))
            {
                Settings.SaveStr(JournalKey, "");
                return true;
            }
            string shown = exePath;
            try { shown = System.IO.Path.GetFileName(exePath); } catch { }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(GpuKey))
                {
                    if (k == null) return false;
                    string cur = k.GetValue(exePath) as string;
                    string back = original != null
                        ? PrefFieldText.RestoreField(cur, original, "GpuPreference")
                        : PrefFieldText.RemoveField(cur, "GpuPreference");
                    if (back.Length == 0 && original == null) k.DeleteValue(exePath, false);
                    else k.SetValue(exePath, back, RegistryValueKind.String);
                }
                Settings.SaveStr(JournalKey, "");
                Logger.Log(Lang.T("log.gpuprefstage.3") + shown);
                return true;
            }
            catch
            {
                Logger.Log(Lang.T("log.gpuprefstage.4"));
                return false;
            }
        }

        internal static string EncodeJournal(string exePath, string original)
        {
            return B64(exePath) + "|" + (original == null ? "-" : B64(original));
        }

        internal static bool DecodeJournal(string raw, out string exePath, out string original)
        {
            exePath = null; original = null;
            if (string.IsNullOrEmpty(raw)) return false;
            string[] parts = raw.Split('|');
            if (parts.Length != 2) return false;
            try
            {
                exePath = UnB64(parts[0]);
                original = parts[1] == "-" ? null : UnB64(parts[1]);
            }
            catch { exePath = null; original = null; return false; }
            return !string.IsNullOrEmpty(exePath);
        }

        private static string B64(string s)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s ?? ""));
        }

        private static string UnB64(string s)
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
    }
}
