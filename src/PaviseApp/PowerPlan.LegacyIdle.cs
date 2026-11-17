using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        private sealed class ExtremeSavedValue
        {
            internal Guid Setting;

            internal uint Ac;

            internal uint Dc;

            internal string Text
            {
                get
                {
                    return Setting.ToString("N") + "=" + Ac.ToString(CultureInfo.InvariantCulture) + "," + Dc.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        internal static bool IdlePolicyHasResidue
        {
            get
            {
                string value;
                if (Settings.TryLoadStr("ExtremePowerKnobSnap", out value))
                {
                    return !string.IsNullOrEmpty(value);
                }
                return true;
            }
        }

        internal static bool ExtremeTunePending
        {
            get
            {
                return extremeTunePending;
            }
        }

        private static bool RestoreOwnedIdlePolicy()
        {
            string value;
            if (!Settings.TryLoadStr("ExtremePowerKnobSnap", out value))
            {
                return false;
            }
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }
            string[] valueParts = value.Split('|');
            Guid result;
            if (valueParts.Length == 3 && valueParts[0] == "2")
            {
                if (!Guid.TryParse(valueParts[1], out result) || result == Guid.Empty)
                {
                    return false;
                }
            }
            else
            {
                if (valueParts.Length != 1)
                {
                    return false;
                }
                if (!Guid.TryParse(Settings.LoadStr("PgPlanGuid", ""), out result) || result == Guid.Empty)
                {
                    return false;
                }
            }
            if (!RestoreExtremeKnobs(result))
            {
                return false;
            }
            tuneState = -1;
            return true;
        }

        private static bool IsRetiredIdleKnob(Guid setting)
        {
            if (setting != IdlePromote && setting != IdleDemote && setting != IdleScaling)
            {
                return setting == RetiredIdleCheck;
            }
            return true;
        }

        private static bool ReadExtremeSnapshot(Guid scheme, out List<ExtremeSavedValue> values)
        {
            values = new List<ExtremeSavedValue>();
            string value;
            if (!Settings.TryLoadStr("ExtremePowerKnobSnap", out value))
            {
                return false;
            }
            if (value.Length == 0)
            {
                return true;
            }
            if (value.IndexOf('|') >= 0)
            {
                string[] valueParts = value.Split('|');
                Guid result;
                if (valueParts.Length != 3 || valueParts[0] != "2" || !Guid.TryParse(valueParts[1], out result) || result == Guid.Empty || result != scheme)
                {
                    return false;
                }
                value = valueParts[2];
            }
            HashSet<Guid> hashSet = new HashSet<Guid>();
            string[] valueParts2 = value.Split(';');
            foreach (string entryText in valueParts2)
            {
                string[] entryTextParts = entryText.Split('=');
                Guid result2;
                if (entryTextParts.Length != 2 || !Guid.TryParse(entryTextParts[0], out result2) || !hashSet.Add(result2) || !IsRetiredIdleKnob(result2))
                {
                    return false;
                }
                string[] parts = entryTextParts[1].Split(',');
                uint result3;
                uint result4;
                if (parts.Length != 2 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out result3) || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out result4))
                {
                    return false;
                }
                values.Add(new ExtremeSavedValue
                {
                    Setting = result2,
                    Ac = result3,
                    Dc = result4
                });
            }
            return true;
        }

        private static bool SaveExtremeSnapshot(Guid scheme, List<ExtremeSavedValue> values)
        {
            List<string> list = new List<string>();
            foreach (ExtremeSavedValue value in values)
            {
                list.Add(value.Text);
            }
            string encodedIdleReceipt = ((list.Count == 0) ? "" : ("2|" + scheme.ToString("N") + "|" + string.Join(";", list.ToArray())));
            return Settings.SaveStrDurable("ExtremePowerKnobSnap", encodedIdleReceipt);
        }

        private static bool ForgetDeletedExtremeSnapshot(Guid scheme)
        {
            List<ExtremeSavedValue> values;
            if (ReadExtremeSnapshot(scheme, out values))
            {
                if (values.Count != 0)
                {
                    return SaveExtremeSnapshot(scheme, new List<ExtremeSavedValue>());
                }
                return true;
            }
            return false;
        }

        private static bool DropOrphanExtremeSnapshot(Guid configuringScheme)
        {
            string value;
            if (!Settings.TryLoadStr("ExtremePowerKnobSnap", out value) || value.Length == 0)
            {
                return false;
            }
            if (value.IndexOf('|') < 0)
            {
                return false;
            }
            string[] valueParts = value.Split('|');
            Guid result;
            if (valueParts.Length != 3 || valueParts[0] != "2" || !Guid.TryParse(valueParts[1], out result) || result == Guid.Empty || result == configuringScheme)
            {
                return false;
            }
            List<Guid> list;
            if (!TryEnumerateSchemes(out list) || list.Contains(result))
            {
                return false;
            }
            if (!SaveExtremeSnapshot(result, new List<ExtremeSavedValue>()))
            {
                return false;
            }
            Logger.Log(Lang.T("log.powerplanschemes.extremeOrphan", 0));
            return true;
        }

        private static bool PrepareExtremeKnobs(Guid scheme)
        {
            bool restoreExtremeKnobsSucceeded = RestoreExtremeKnobs(scheme);
            if (!restoreExtremeKnobsSucceeded && DropOrphanExtremeSnapshot(scheme))
            {
                restoreExtremeKnobsSucceeded = RestoreExtremeKnobs(scheme);
            }
            extremeTunePending = !restoreExtremeKnobsSucceeded;
            return restoreExtremeKnobsSucceeded;
        }

        private static bool RestoreExtremeKnobs(Guid scheme)
        {
            List<ExtremeSavedValue> values;
            if (!ReadExtremeSnapshot(scheme, out values))
            {
                return false;
            }
            if (values.Count == 0)
            {
                return true;
            }
            List<ExtremeSavedValue> list = new List<ExtremeSavedValue>();
            bool result = true;
            foreach (ExtremeSavedValue item in values)
            {
                if (!RestoreExtremeIndex(scheme, item.Setting, true, item.Ac) || !RestoreExtremeIndex(scheme, item.Setting, false, item.Dc))
                {
                    list.Add(item);
                    result = false;
                }
            }
            Guid? guid = Current();
            if (!guid.HasValue || (guid.Value == scheme && !Set(scheme)))
            {
                return false;
            }
            if (SaveExtremeSnapshot(scheme, list))
            {
                return result;
            }
            return false;
        }

        private static bool RestoreExtremeIndex(Guid scheme, Guid setting, bool ac, uint original)
        {
            uint? extremeIndex = ReadExtremeIndex(scheme, setting, ac);
            if (!extremeIndex.HasValue)
            {
                return false;
            }
            if (extremeIndex.Value == original)
            {
                return true;
            }
            if (!(ac ? WriteAc(scheme, SubProcessor, setting, original) : WriteDc(scheme, SubProcessor, setting, original)))
            {
                return false;
            }
            return ReadExtremeIndex(scheme, setting, ac) == original;
        }

        private static uint? ReadExtremeIndex(Guid scheme, Guid setting, bool ac)
        {
            uint value;
            if (!(ac ? ReadAc(scheme, SubProcessor, setting, out value) : ReadDc(scheme, SubProcessor, setting, out value)))
            {
                return null;
            }
            return value;
        }
    }
}
