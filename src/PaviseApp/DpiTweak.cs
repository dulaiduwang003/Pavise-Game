using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class DpiTweak
    {

        private static readonly object sync = new object();

        internal static string[] TrackedExes()
        {
            lock (sync)
            {
                string value;
                if (!Settings.TryLoadStr("DpiExeList", out value))
                {
                    throw new InvalidOperationException("Cannot read the DPI recovery ledger");
                }
                return value.Split(new char[1] { '\u001f' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        public static bool HasResidue()
        {
            try
            {
                return TrackedExes().Length > 0;
            }
            catch
            {
                return true;
            }
        }

        public static bool RestoreAll()
        {
            lock (sync)
            {
                try
                {
                    bool allRestored = true;
                    foreach (string exePath in TrackedExes())
                    {
                        if (!SetForExe(exePath, false))
                        {
                            allRestored = false;
                        }
                    }
                    return allRestored && TrackedExes().Length == 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool ScalingActive()
        {
            try
            {
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return graphics.DpiX > 96.5f;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool Track(string exePath, bool aware)
        {
            List<string> entryTexts = new List<string>();
            foreach (string entryText in TrackedExes())
            {
                if (!string.Equals(entryText, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    entryTexts.Add(entryText);
                }
            }
            if (aware)
            {
                entryTexts.Add(exePath);
            }
            string joinResult = string.Join('\u001f'.ToString(), entryTexts.ToArray());
            string text;
            if (Settings.SaveStr("DpiExeList", joinResult) && Settings.TryLoadStr("DpiExeList", out text))
            {
                return text == joinResult;
            }
            return false;
        }

        private static bool IsTracked(string exePath)
        {
            foreach (string a in TrackedExes())
            {
                if (string.Equals(a, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string ReadLayer(string exePath)
        {
            using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers"))
            {
                object value = ((registryKey == null) ? null : registryKey.GetValue(exePath));
                if (value != null && !(value is string))
                {
                    throw new InvalidOperationException("Unexpected DPI compatibility-layer value type");
                }
                return value as string;
            }
        }

        private static void WriteLayer(string exePath, string value)
        {
            using (RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers"))
            {
                if (registryKey == null)
                {
                    throw new InvalidOperationException("Cannot open DPI compatibility layers");
                }
                if (value.Length == 0)
                {
                    registryKey.DeleteValue(exePath, false);
                }
                else
                {
                    registryKey.SetValue(exePath, value, RegistryValueKind.String);
                }
            }
        }

        private static void RollbackToken(string exePath)
        {
            try
            {
                string layer = ReadLayer(exePath);
                if (HasToken(layer))
                {
                    WriteLayer(exePath, RemoveToken(layer));
                }
                if (!HasToken(ReadLayer(exePath)))
                {
                    Track(exePath, false);
                }
            }
            catch
            {
            }
        }

        public static bool IsAwareForExe(string exePath)
        {
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }
            lock (sync)
            {
                try
                {
                    return HasToken(ReadLayer(exePath));
                }
                catch (Exception ex)
                {
                    Logger.Log(Lang.T("log.dpi.3", 0) + " " + ex.Message);
                    return false;
                }
            }
        }

        public static bool SetForExe(string exePath, bool aware)
        {
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }
            lock (sync)
            {
                bool wasDpiAware = false;
                try
                {
                    string layer = ReadLayer(exePath);
                    bool isTracked = IsTracked(exePath);
                    if (HasToken(layer) == aware)
                    {
                        return aware || !isTracked || Track(exePath, false);
                    }
                    if (aware && !isTracked && !Track(exePath, true))
                    {
                        return false;
                    }
                    wasDpiAware = aware;
                    WriteLayer(exePath, aware ? AddToken(layer) : RemoveToken(layer));
                    if (HasToken(ReadLayer(exePath)) != aware)
                    {
                        throw new InvalidOperationException("DPI compatibility-layer verification failed");
                    }
                    if (aware && !IsTracked(exePath))
                    {
                        throw new InvalidOperationException("DPI recovery ledger changed during apply");
                    }
                    if (!aware && isTracked && !Track(exePath, false))
                    {
                        return false;
                    }
                    Logger.Log(Lang.T(aware ? "log.dpi.1" : "log.dpi.2", 0) + " " + exePath);
                    return true;
                }
                catch (Exception ex)
                {
                    if (wasDpiAware)
                    {
                        RollbackToken(exePath);
                    }
                    Logger.Log(Lang.T("log.dpi.3", 0) + " " + ex.Message);
                    return false;
                }
            }
        }

        private static string[] Split(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return new string[0];
            }
            return s.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        internal static bool HasToken(string s)
        {
            foreach (string a in Split(s))
            {
                if (string.Equals(a, "HIGHDPIAWARE", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static List<string> OtherTokens(string cur)
        {
            List<string> entryTexts = new List<string>();
            foreach (string entryText in Split(cur))
            {
                if (entryText != "~" && !string.Equals(entryText, "HIGHDPIAWARE", StringComparison.OrdinalIgnoreCase))
                {
                    entryTexts.Add(entryText);
                }
            }
            return entryTexts;
        }

        internal static string AddToken(string cur)
        {
            List<string> texts = new List<string>();
            texts.Add("~");
            List<string> currentTexts = texts;
            currentTexts.AddRange(OtherTokens(cur));
            currentTexts.Add("HIGHDPIAWARE");
            return string.Join(" ", currentTexts.ToArray());
        }

        internal static string RemoveToken(string cur)
        {
            List<string> texts = OtherTokens(cur);
            if (texts.Count == 0)
            {
                return "";
            }
            List<string> currentTexts = new List<string>();
            currentTexts.Add("~");
            List<string> candidateTexts = currentTexts;
            candidateTexts.AddRange(texts);
            return string.Join(" ", candidateTexts.ToArray());
        }
    }
}
