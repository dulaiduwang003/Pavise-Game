// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal static class IntelEndurance
    {
        internal const string SnapKey = "IntelEnduranceSnap";
        private const int ControlOff = 0;
        private static readonly object lk = new object();
        private static readonly IntelGraphicsApi api = new IntelGraphicsApi();

        public static bool HasResidue { get { return Settings.LoadStr(SnapKey, "").Length > 0; } }

        internal static bool Wanted(int control)
        {
            return control == 1 || control == 2;
        }

        private static long supportChecked;
        private static bool supportAvailable;
        public static bool Supported()
        {
            lock (lk)
            {
                if (DateTime.UtcNow.Ticks < supportChecked) return supportAvailable;
                int control, mode;
                supportAvailable = Native.HasSystemBattery() && Read(out control, out mode);
                supportChecked = DateTime.UtcNow.AddSeconds(30).Ticks;
                return supportAvailable;
            }
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (HasResidue) return Restore() && Activate();
                int control, mode;
                if (!Read(out control, out mode)) { Logger.Warn(Lang.T("log.intelend.1")); return false; }
                if (control == ControlOff) return true;
                if (!Wanted(control)) return false;
                string snap = control + "|" + mode;
                if (!Settings.SaveStrDurable(SnapKey, snap) || Settings.LoadStr(SnapKey, "") != snap) return false;
                int check, checkMode;
                if (!Write(ControlOff, mode) || !Read(out check, out checkMode)
                    || check != ControlOff)
                {
                    Logger.Log(Lang.T("log.intelend.2"));
                    return false;
                }
                Logger.Log(Lang.T("log.intelend.3"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string raw;
                if (!Settings.TryLoadStr(SnapKey, out raw)) return false;
                if (raw.Length == 0) return true;
                string[] parts = raw.Split('|');
                int original, originalMode;
                if (parts.Length != 2 || !int.TryParse(parts[0], out original) || !int.TryParse(parts[1], out originalMode) || !Wanted(original))
                { return false; }
                int now, nowMode;
                if (!Read(out now, out nowMode)) return false;
                if (now != ControlOff || nowMode != originalMode)
                {
                    return Settings.SaveStrDurable(SnapKey, "");
                }
                if (!Write(original, originalMode) || !Read(out now, out nowMode)
                    || now != original || nowMode != originalMode)
                { Logger.Log(Lang.T("log.intelend.4")); return false; }
                if (!Settings.SaveStrDurable(SnapKey, "")) return false;
                Logger.Log(Lang.T("log.intelend.5"));
                return true;
            }
        }

        private static bool Read(out int control, out int mode)
        {
#if PAVISE_SELFTEST
            if (ReadForTest == null) throw new InvalidOperationException("Unmocked Endurance read");
            return ReadForTest(out control, out mode);
#else
            return api.TryReadEndurance(out control, out mode);
#endif
        }
        private static bool Write(int control, int mode)
        {
#if PAVISE_SELFTEST
            if (WriteForTest == null) throw new InvalidOperationException("Unmocked Endurance write");
            return WriteForTest(control, mode);
#else
            return api.TryWriteEndurance(control, mode);
#endif
        }
#if PAVISE_SELFTEST
        internal delegate bool Reader(out int control, out int mode);
        internal static Reader ReadForTest;
        internal static Func<int, int, bool> WriteForTest;
#endif
        public static void HealFromCrash()
        {
            if (HasResidue && Restore()) Logger.Log(Lang.T("log.intelend.6"));
        }
    }
}
