using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class PowerOverlay
    {
        internal const string SnapKey = "PowerOverlaySnap";
        internal static readonly Guid Max = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid value);
        [DllImport("powrprof.dll")] private static extern uint PowerGetActualOverlayScheme(out Guid value);
        [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid value);
        private static readonly object lk = new object();
        private static bool owned;
        private static string settled;

        internal static bool ShouldActivate(bool laptop, bool onAc, bool competitive)
        {
            return laptop && onAc && competitive;
        }

        private static uint? Personality()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return PersonalityForTest == null ? null : PersonalityForTest();
#else
            return PowerPlan.ActivePersonality();
#endif
        }

        private static bool Read(bool effective, out Guid value)
        {
            value = Guid.Empty;
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
            catch { return false; }
        }

        private static uint Set(Guid value)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return SetForTest == null ? 120u : SetForTest(value);
#else
            return PowerSetActiveOverlayScheme(value);
#endif
        }

        internal static bool TryReadActive(out Guid value) { return Read(false, out value); }
        public static bool Supported()
        {
            try { Guid value; return Personality() == 2 && Read(true, out value) && Read(false, out value); }
            catch { return false; }
        }

        private static bool Load(out string value)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (LoadForTest != null) { value = LoadForTest(); return value != null; }
#endif
            return Settings.TryLoadStr(SnapKey, out value);
        }

        private static bool Save(string value)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (SaveForTest != null) return SaveForTest(value);
#endif
            return Settings.SaveStr(SnapKey, value);
        }

        private static bool SaveVerified(string value)
        {
            string after;
            return Save(value) && Load(out after) && after == value;
        }

        public static bool HasResidue
        {
            get { lock (lk) { string value; return !Load(out value) || !string.IsNullOrEmpty(value); } }
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (!Supported()) return false;
                string receipt;
                Guid before, effective, original;
                if (!Load(out receipt) || receipt == null || !Read(false, out before)) return false;
                if (!owned && receipt.Length > 0)
                {
                    if (!Restore() || !Read(false, out before)) return false;
                    receipt = "";
                }
                if (settled != null) { if (!Restore()) return false; receipt = ""; }
                if (receipt.Length == 0)
                {
                    receipt = before.ToString("D");
                    if (!SaveVerified(receipt)) { Logger.Info(Lang.T("log.poweroverlay.2")); return false; }
                }
                else if (!Guid.TryParse(receipt, out original)) return false;
                owned = true;
                if (before == Max && Read(true, out effective) && effective == Max) return true;
                uint status;
                try { status = Set(Max); } catch { return false; }
                Guid after;
                if (status != 0 || !Read(false, out after) || after != Max
                    || !Read(true, out effective) || effective != Max)
                {
                    Logger.Warn(Lang.T("log.poweroverlay.3") + status);
                    return false;
                }
                Logger.Info(Lang.T("log.poweroverlay.4"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string receipt;
                if (!Load(out receipt) || receipt == null) return false;
                if (receipt.Length == 0) { owned = false; settled = null; return true; }
                if (settled == receipt) return Clear();
                Guid original, current;
                if (!Guid.TryParse(receipt, out original) || !Read(false, out current)) return false;
                // 崩溃收据不覆盖新选择，存活对局仍负责恢复原方案。
                if (!owned && current != Max && current != original)
                {
                    Logger.Info(Lang.T("log.poweroverlay.external"));
                    settled = receipt; return Clear();
                }
                if (current != original)
                {
                    try { if (Set(original) != 0) return false; } catch { return false; }
                    Guid after;
                    if (!Read(false, out after) || after != original) return false;
                }
                settled = receipt;
                Logger.Info(Lang.T("log.poweroverlay.6"));
                return Clear();
            }
        }

        private static bool Clear()
        {
            if (!SaveVerified("")) return false;
            owned = false; settled = null; return true;
        }

        public static void HealFromCrash()
        {
            if (HasResidue && Restore()) Logger.Info(Lang.T("log.poweroverlay.7"));
        }

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static Func<uint?> PersonalityForTest;
        internal static Func<Guid?> ActualForTest, EffectiveForTest;
        internal static Func<Guid, uint> SetForTest;
        internal static Func<string> LoadForTest;
        internal static Func<string, bool> SaveForTest;
        internal static void ResetForTest()
        {
            owned = false; settled = null;
            PersonalityForTest = null; ActualForTest = null; EffectiveForTest = null;
            SetForTest = null; LoadForTest = null; SaveForTest = null;
        }
#endif
    }
}
