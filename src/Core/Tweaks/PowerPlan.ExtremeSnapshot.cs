using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        internal const string ExtremeSnapKey = "ExtremePowerKnobSnap";
        private static readonly Guid IdlePromote = new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c");
        private static readonly Guid IdleDemote = new Guid("4b92d758-5a24-4851-a470-815d78aee119");
        private static readonly Guid IdleScaling = new Guid("6c2993b0-8f48-481f-bcc6-00dd2742aa06");
        private static readonly Guid RetiredIdleCheck = new Guid("c4581c31-89ab-4597-8e2b-9c9cab440e6b");

        private sealed class ExtremeSavedValue
        {
            internal Guid Setting;
            internal uint Ac, Dc;
            internal string Text { get { return Setting.ToString("N") + "="
                + Ac.ToString(CultureInfo.InvariantCulture) + "," + Dc.ToString(CultureInfo.InvariantCulture); } }
        }

        private static bool IsRetiredIdleKnob(Guid setting)
        {
            return setting == IdlePromote || setting == IdleDemote
                || setting == IdleScaling || setting == RetiredIdleCheck;
        }

        private static bool ReadExtremeSnapshot(Guid scheme, out List<ExtremeSavedValue> values)
        {
            values = new List<ExtremeSavedValue>();
            string text;
            if (!Settings.TryLoadStr(ExtremeSnapKey, out text)) return false;
            if (text.Length == 0) return true;
            if (text.IndexOf('|') >= 0)
            {
                string[] header = text.Split('|');
                Guid owner;
                if (header.Length != 3 || header[0] != "2" || !Guid.TryParse(header[1], out owner)
                    || owner == Guid.Empty || owner != scheme) return false;
                text = header[2];
            }
            var seen = new HashSet<Guid>();
            foreach (string part in text.Split(';'))
            {
                string[] pair = part.Split('=');
                Guid setting;
                if (pair.Length != 2 || !Guid.TryParse(pair[0], out setting) || !seen.Add(setting)
                    || !IsRetiredIdleKnob(setting)) return false;
                string[] indices = pair[1].Split(',');
                uint ac, dc;
                if (indices.Length != 2 || !uint.TryParse(indices[0], NumberStyles.None, CultureInfo.InvariantCulture, out ac)
                    || !uint.TryParse(indices[1], NumberStyles.None, CultureInfo.InvariantCulture, out dc)) return false;
                values.Add(new ExtremeSavedValue { Setting = setting, Ac = ac, Dc = dc });
            }
            return true;
        }

        private static bool SaveExtremeSnapshot(Guid scheme, List<ExtremeSavedValue> values)
        {
            var parts = new List<string>();
            foreach (ExtremeSavedValue value in values) parts.Add(value.Text);
            string text = parts.Count == 0 ? "" : "2|" + scheme.ToString("N") + "|" + string.Join(";", parts.ToArray());
#if PAVISE_SELFTEST
            if (ExtremeSaveSnapshotForTest != null) return ExtremeSaveSnapshotForTest(text);
#endif
            return Settings.SaveStrDurable(ExtremeSnapKey, text);
        }

        private static bool ForgetDeletedExtremeSnapshot(Guid scheme)
        {
            List<ExtremeSavedValue> values;
            return ReadExtremeSnapshot(scheme, out values)
                && (values.Count == 0 || SaveExtremeSnapshot(scheme, new List<ExtremeSavedValue>()));
        }

        // 完整枚举确认方案不存在后才删除收据。
        private static bool DropOrphanExtremeSnapshot(Guid configuringScheme)
        {
            string text;
            if (!Settings.TryLoadStr(ExtremeSnapKey, out text) || text.Length == 0) return false;
            if (text.IndexOf('|') < 0) return false;
            string[] header = text.Split('|');
            Guid owner;
            if (header.Length != 3 || header[0] != "2"
                || !Guid.TryParse(header[1], out owner) || owner == Guid.Empty
                || owner == configuringScheme) return false;
            List<Guid> schemes;
            if (!TryEnumerateSchemes(out schemes) || schemes.Contains(owner)) return false;
            if (!SaveExtremeSnapshot(owner, new List<ExtremeSavedValue>())) return false;
            Logger.Log(Lang.T("log.powerplanschemes.extremeOrphan"));
            return true;
        }

        private static bool PrepareExtremeKnobs(Guid scheme)
        {
            bool ready = RestoreExtremeKnobs(scheme);
            if (!ready && DropOrphanExtremeSnapshot(scheme))
                ready = RestoreExtremeKnobs(scheme);
            extremeTunePending = !ready;
            return ready;
        }

        // 两侧分别回读，部分恢复失败时保留记录。
        private static bool RestoreExtremeKnobs(Guid scheme)
        {
            List<ExtremeSavedValue> values;
            if (!ReadExtremeSnapshot(scheme, out values)) return false;
            if (values.Count == 0) return true;
            var pending = new List<ExtremeSavedValue>();
            bool restored = true;
            foreach (ExtremeSavedValue value in values)
            {
                if (!RestoreExtremeIndex(scheme, value.Setting, true, value.Ac)
                    || !RestoreExtremeIndex(scheme, value.Setting, false, value.Dc))
                {
                    pending.Add(value); restored = false;
                }
            }
            #if !PAVISE_SELFTEST && !PAVISE_PERFLAB
            Guid? current = Current();
            if (!current.HasValue || (current.Value == scheme && !Set(scheme)))
                return false;
#endif
            return SaveExtremeSnapshot(scheme, pending) && restored;
        }

        private static bool RestoreExtremeIndex(Guid scheme, Guid setting, bool ac, uint original)
        {
            uint? current = ReadExtremeIndex(scheme, setting, ac);
            if (!current.HasValue) return false;
            if (current.Value == original) return true;
#if PAVISE_SELFTEST
            if (ExtremeWriteIndexForTest == null) throw new InvalidOperationException("Unmocked extreme power write");
            if (!ExtremeWriteIndexForTest(scheme, setting, ac, original)) return false;
#else
            if (!(ac ? WriteAc(scheme, SubProcessor, setting, original) : WriteDc(scheme, SubProcessor, setting, original))) return false;
#endif
            return ReadExtremeIndex(scheme, setting, ac) == original;
        }

        private static uint? ReadExtremeIndex(Guid scheme, Guid setting, bool ac)
        {
#if PAVISE_SELFTEST
            if (ExtremeReadIndexForTest == null) throw new InvalidOperationException("Unmocked extreme power read");
            return ExtremeReadIndexForTest(scheme, setting, ac);
#else
            uint value;
            return (ac ? ReadAc(scheme, SubProcessor, setting, out value)
                : ReadDc(scheme, SubProcessor, setting, out value)) ? (uint?)value : null;
#endif
        }

#if PAVISE_SELFTEST
        internal static Func<Guid, Guid, bool, uint?> ExtremeReadIndexForTest;
        internal static Func<Guid, Guid, bool, uint, bool> ExtremeWriteIndexForTest;
        internal static Func<string, bool> ExtremeSaveSnapshotForTest;
        internal static bool PrepareExtremeForTest(Guid scheme) { return PrepareExtremeKnobs(scheme); }
        internal static bool DropOrphanExtremeForTest(Guid scheme) { return DropOrphanExtremeSnapshot(scheme); }
        internal static bool RestoreExtremeForTest(Guid scheme) { return RestoreExtremeKnobs(scheme); }
        internal static bool ForgetDeletedExtremeForTest(Guid scheme) { return ForgetDeletedExtremeSnapshot(scheme); }
#endif
    }
}
