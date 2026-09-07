using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        internal const string ExtremeSnapKey = "ExtremePowerKnobSnap";
        private static readonly Guid RetiredIdleCheck = new Guid("c4581c31-89ab-4597-8e2b-9c9cab440e6b");

        private sealed class ExtremeSavedValue
        {
            internal Guid Setting;
            internal uint Ac, Dc;
            internal string Text { get { return Setting.ToString("N") + "="
                + Ac.ToString(CultureInfo.InvariantCulture) + "," + Dc.ToString(CultureInfo.InvariantCulture); } }
        }

        private static bool IsCurrentExtremeKnob(Guid setting)
        {
            foreach (Knob knob in ExtremeKnobs) if (knob.Setting == setting) return true;
            return false;
        }

        // v2 把收据绑定到方案。兼容旧版仅有 GUID=AC,DC 的托管方案收据。
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
                    || (!IsCurrentExtremeKnob(setting) && setting != IdleDemote && setting != RetiredIdleCheck)) return false;
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

        // 仅由托管方案删除成功的路径调用，不执行删除，不把别的方案收据一起清掉。
        private static bool ForgetDeletedExtremeSnapshot(Guid scheme)
        {
            List<ExtremeSavedValue> values;
            return ReadExtremeSnapshot(scheme, out values)
                && (values.Count == 0 || SaveExtremeSnapshot(scheme, new List<ExtremeSavedValue>()));
        }

        private static bool SnapshotExtremeKnobs(Guid scheme)
        {
            List<ExtremeSavedValue> values;
            if (!ReadExtremeSnapshot(scheme, out values)) return false;
            foreach (Knob knob in ExtremeKnobs)
            {
                if (values.Exists(delegate(ExtremeSavedValue v) { return v.Setting == knob.Setting; })) continue;
                uint? ac = ReadExtremeIndex(scheme, knob.Setting, true);
                uint? dc = ReadExtremeIndex(scheme, knob.Setting, false);
                if (!ac.HasValue && !dc.HasValue) continue;
                // 不再用 AC 冒充读取失败的 DC 原值；无完整收据就不准写。
                if (!ac.HasValue || !dc.HasValue) return false;
                values.Add(new ExtremeSavedValue { Setting = knob.Setting, Ac = ac.Value, Dc = dc.Value });
            }
            return SaveExtremeSnapshot(scheme, values);
        }

        // retiredOnly=true 时迁移撤回项，保留当前极限项的原始收据。
        // 每侧写后回读；部分失败留账，不能把“调用过恢复”当作“恢复完成”。
        private static bool RestoreExtremeKnobs(Guid scheme, bool retiredOnly)
        {
            List<ExtremeSavedValue> values;
            if (!ReadExtremeSnapshot(scheme, out values)) return false;
            if (values.Count == 0) return true;
            var pending = new List<ExtremeSavedValue>();
            bool restored = true;
            foreach (ExtremeSavedValue value in values)
            {
                if (retiredOnly && IsCurrentExtremeKnob(value.Setting)) { pending.Add(value); continue; }
                if (!RestoreExtremeIndex(scheme, value.Setting, true, value.Ac)
                    || !RestoreExtremeIndex(scheme, value.Setting, false, value.Dc))
                {
                    pending.Add(value); restored = false;
                }
            }
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
        internal static bool SnapshotExtremeForTest(Guid scheme) { return SnapshotExtremeKnobs(scheme); }
        internal static bool RestoreExtremeForTest(Guid scheme, bool retiredOnly) { return RestoreExtremeKnobs(scheme, retiredOnly); }
        internal static bool ForgetDeletedExtremeForTest(Guid scheme) { return ForgetDeletedExtremeSnapshot(scheme); }
#endif
    }
}
