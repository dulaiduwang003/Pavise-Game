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
                    string value;
                    return !Load(out value) || !string.IsNullOrEmpty(value);
                }
            }
        }

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveOverlayScheme(Guid value);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActualOverlayScheme(out Guid value);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetEffectiveOverlayScheme(out Guid value);

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        // 自检构建不读也不改真实的电源叠加方案 都交给替身
        internal static Func<Guid?> ActualForTest = null, EffectiveForTest = null;
        internal static Func<Guid, uint> SetForTest = null;
#endif

        private static bool Read(bool effective, out Guid value)
        {
            value = Guid.Empty;
#if PAVISE_SELFTEST
            if ((effective ? EffectiveForTest : ActualForTest) == null) throw new InvalidOperationException("Unmocked power overlay read");
#endif
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                Func<Guid?> reader = effective ? EffectiveForTest : ActualForTest;
                Guid? result = reader == null ? null : reader();
                if (!result.HasValue) return false;
                value = result.Value; return true;
#else
                return (effective ? PowerGetEffectiveOverlayScheme(out value) : PowerGetActualOverlayScheme(out value)) == 0;
#endif
            }
            catch
            {
                return false;
            }
        }

        private static uint Set(Guid value)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
#if PAVISE_SELFTEST
            if (SetForTest == null) throw new InvalidOperationException("Unmocked power overlay write");
#endif
            return SetForTest == null ? 120u : SetForTest(value);
#else
            return PowerSetActiveOverlayScheme(value);
#endif
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
            string value2;
            if (Save(value) && Load(out value2))
            {
                return value2 == value;
            }
            return false;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string value;
                if (!Load(out value) || value == null)
                {
                    return false;
                }
                if (value.Length == 0)
                {
                    owned = false;
                    settled = null;
                    return true;
                }
                if (settled == value)
                {
                    return Clear();
                }
                Guid result;
                Guid value2;
                if (!Guid.TryParse(value, out result) || !Read(false, out value2))
                {
                    return false;
                }
                if (!owned && value2 != Max && value2 != result)
                {
                    Logger.Info(Lang.T("log.poweroverlay.external"));
                    settled = value;
                    return Clear();
                }
                if (value2 != result)
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
                    Guid value3;
                    if (!Read(false, out value3) || value3 != result)
                    {
                        return false;
                    }
                }
                settled = value;
                Logger.Info(Lang.T("log.poweroverlay.6"));
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
                Logger.Info(Lang.T("log.poweroverlay.7"));
            }
        }
    }
}
