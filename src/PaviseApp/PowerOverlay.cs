using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class PowerOverlay
    {
        internal const string SnapKey = "PowerOverlaySnap";

        internal static readonly Guid Max = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        private static readonly object lk = new object();

        private static bool owned;

        private static string settled;

        public static bool HasResidue
        {
            get
            {
                lock (lk)
                {
                    string text;
                    return !Load(out text) || !string.IsNullOrEmpty(text);
                }
            }
        }

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveOverlayScheme(Guid value);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActualOverlayScheme(out Guid value);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetEffectiveOverlayScheme(out Guid value);

        private static bool Read(bool effective, out Guid value)
        {
            value = Guid.Empty;
            try
            {
                return (effective ? PowerGetEffectiveOverlayScheme(out value) : PowerGetActualOverlayScheme(out value)) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static uint Set(Guid value)
        {
            return PowerSetActiveOverlayScheme(value);
        }

        private static bool Load(out string value)
        {
            return Settings.TryLoadStr("PowerOverlaySnap", out value);
        }

        private static bool Save(string value)
        {
            return Settings.SaveStr("PowerOverlaySnap", value);
        }

        private static bool SaveVerified(string value)
        {
            string text;
            if (Save(value) && Load(out text))
            {
                return text == value;
            }
            return false;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string text;
                if (!Load(out text) || text == null)
                {
                    return false;
                }
                if (text.Length == 0)
                {
                    owned = false;
                    settled = null;
                    return true;
                }
                if (settled == text)
                {
                    return Clear();
                }
                Guid result;
                Guid guid;
                if (!Guid.TryParse(text, out result) || !Read(false, out guid))
                {
                    return false;
                }
                if (!owned && guid != Max && guid != result)
                {
                    Logger.Info(Lang.T("log.poweroverlay.external", 0));
                    settled = text;
                    return Clear();
                }
                if (guid != result)
                {
                    try
                    {
                        if (Set(result) != 0)
                        {
                            return false;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                    Guid currentGuid;
                    if (!Read(false, out currentGuid) || currentGuid != result)
                    {
                        return false;
                    }
                }
                settled = text;
                Logger.Info(Lang.T("log.poweroverlay.6", 0));
                return Clear();
            }
        }

        private static bool Clear()
        {
            if (!SaveVerified(""))
            {
                return false;
            }
            owned = false;
            settled = null;
            return true;
        }

        public static void HealFromCrash()
        {
            if (HasResidue && Restore())
            {
                Logger.Info(Lang.T("log.poweroverlay.7", 0));
            }
        }
    }
}
