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

        private enum CpuIdlePhase
        {
            Prepared,
            Owned,
            Restoring,
            Settled
        }

        private sealed class CpuIdleReceipt
        {
            internal Guid Scheme;

            internal CpuIdlePhase Phase;

            // -1 表示旧记录；v3 的位 1、2 只授权恢复仍由本方持有且值为 1 的 AC、DC 设置。
            internal int PendingMask = -1;

            internal string Text(CpuIdlePhase phase)
            {
                object instance;
                switch (phase)
                {
                default:
                    instance = "S";
                    break;
                case CpuIdlePhase.Restoring:
                    instance = "R";
                    break;
                case CpuIdlePhase.Owned:
                    instance = "O";
                    break;
                case CpuIdlePhase.Prepared:
                    instance = "P";
                    break;
                }
                string message = (string)instance;
                return PendingMask < 0 ? "2|" + Scheme.ToString("D") + "|0|" + message
                    : "3|" + Scheme.ToString("D") + "|0|" + message + "|" + PendingMask.ToString(CultureInfo.InvariantCulture);
            }
        }

        internal static bool IdlePolicyHasResidue
        {
            get
            {
                string text;
                if (Settings.TryLoadStr("ExtremePowerKnobSnap", out text))
                {
                    return !string.IsNullOrEmpty(text);
                }
                return true;
            }
        }

        internal static bool LegacyCpuIdleHasResidue
        {
            get
            {
                lock (lk)
                {
                    if (cpuIdleReceipt != null)
                    {
                        return true;
                    }
                    try
                    {
                        string message;
                        return !CpuIdleReadLedger(out message) || message == null || message.Length != 0;
                    }
                    catch
                    {
                        return true;
                    }
                }
            }
        }

        private static bool RestoreOwnedIdlePolicy()
        {
            string text;
            if (!Settings.TryLoadStr("ExtremePowerKnobSnap", out text))
            {
                return false;
            }
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }
            string[] valueParts = text.Split('|');
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
            string text;
            if (!Settings.TryLoadStr("ExtremePowerKnobSnap", out text))
            {
                return false;
            }
            if (text.Length == 0)
            {
                return true;
            }
            if (text.IndexOf('|') >= 0)
            {
                string[] valueParts = text.Split('|');
                Guid result;
                if (valueParts.Length != 3 || valueParts[0] != "2" || !Guid.TryParse(valueParts[1], out result) || result == Guid.Empty || result != scheme)
                {
                    return false;
                }
                text = valueParts[2];
            }
            HashSet<Guid> guidsSet = new HashSet<Guid>();
            foreach (string entryText in text.Split(';'))
            {
                string[] entryTextParts = entryText.Split('=');
                Guid result2;
                if (entryTextParts.Length != 2 || !Guid.TryParse(entryTextParts[0], out result2) || !guidsSet.Add(result2) || !IsRetiredIdleKnob(result2))
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
            List<string> texts = new List<string>();
            foreach (ExtremeSavedValue value in values)
            {
                texts.Add(value.Text);
            }
            string encodedIdleReceipt = ((texts.Count == 0) ? "" : ("2|" + scheme.ToString("N") + "|" + string.Join(";", texts.ToArray())));
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
            List<ExtremeSavedValue> extremeSavedValues = new List<ExtremeSavedValue>();
            bool result = true;
            foreach (ExtremeSavedValue savedValue in values)
            {
                if (!RestoreExtremeIndex(scheme, savedValue.Setting, true, savedValue.Ac) || !RestoreExtremeIndex(scheme, savedValue.Setting, false, savedValue.Dc))
                {
                    extremeSavedValues.Add(savedValue);
                    result = false;
                }
            }
            Guid? guid = Current();
            if (!guid.HasValue || (guid.Value == scheme && !Set(scheme)))
            {
                return false;
            }
            if (SaveExtremeSnapshot(scheme, extremeSavedValues))
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

        internal static bool RestoreLegacyCpuIdle()
        {
            lock (lk)
            {
                try
                {
                    if (!LoadCpuIdleReceipt())
                    {
                        return false;
                    }
                    CpuIdleReceipt cpuIdleReceipt = PowerPlan.cpuIdleReceipt;
                    if (cpuIdleReceipt == null)
                    {
                        return true;
                    }
                    if (cpuIdleReceipt.Phase == CpuIdlePhase.Settled)
                    {
                        return ClearCpuIdleReceipt();
                    }
                    uint ac;
                    uint dc;
                    if (cpuIdleReceipt.PendingMask < 0)
                    {
                        if (!CpuIdleReadValues(cpuIdleReceipt.Scheme, out ac, out dc)) return false;
                        if (ac == 0 && dc == 0)
                        {
                            cpuIdleReceipt.PendingMask = 0;
                        }
                        else if (cpuIdleReceipt.Phase == CpuIdlePhase.Owned && ac == 1 && dc == 1)
                        {
                            cpuIdleReceipt.PendingMask = 3;
                        }
                        else if (ac == 1 || dc == 1)
                        {
                            // 旧版 Prepared、Restoring 记录无法区分部分恢复与外部修改；保留债务，但不宣称仍为 1 的值归本方所有。

                            return false;
                        }
                        else
                        {
                            cpuIdleReceipt.Phase = CpuIdlePhase.Settled;
                            return ClearCpuIdleReceipt();
                        }
                        cpuIdleReceipt.Phase = CpuIdlePhase.Restoring;
                    }
                    // 写入任一供电来源前，先持久保存并读回权限，包括此前进度保存失败的情况。
                    if (!WriteCpuIdleLedgerVerified(cpuIdleReceipt.Text(CpuIdlePhase.Restoring))
                        || !RestoreCpuIdleSide(cpuIdleReceipt, true)
                        || !RestoreCpuIdleSide(cpuIdleReceipt, false)
                        || !CpuIdleReadValues(cpuIdleReceipt.Scheme, out ac, out dc)) return false;
                    // 已完成恢复的供电来源可能又被其他所有者修改，不能重新应用旧策略。
                    if (ac == 0 && dc == 0 && !ReapplyCpuIdle(cpuIdleReceipt.Scheme))
                    {
                        return false;
                    }
                    cpuIdleReceipt.Phase = CpuIdlePhase.Settled;
                    return ClearCpuIdleReceipt();
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool LoadCpuIdleReceipt()
        {
            if (PowerPlan.cpuIdleReceipt != null)
            {
                return true;
            }
            string message;
            if (!CpuIdleReadLedger(out message) || message == null)
            {
                return false;
            }
            if (message.Length == 0)
            {
                return true;
            }
            if (message.Length > 80)
            {
                return false;
            }
            string[] messageParts = message.Split('|');
            Guid result;
            bool legacy = messageParts.Length == 4 && (messageParts[0] == "1" || messageParts[0] == "2");
            bool current = messageParts.Length == 5 && messageParts[0] == "3";
            if ((!legacy && !current) || messageParts[2] != "0" || !Guid.TryParseExact(messageParts[1], "D", out result) || result == Guid.Empty
                || (messageParts[0] == "1" && messageParts[3] != "A" && messageParts[3] != "R" && messageParts[3] != "S")
                || (messageParts[0] == "2" && messageParts[3] != "P" && messageParts[3] != "O" && messageParts[3] != "R" && messageParts[3] != "S")
                || (current && ((messageParts[3] != "R" && messageParts[3] != "S") || messageParts[4].Length != 1 || messageParts[4][0] < '0' || messageParts[4][0] > '3' || (messageParts[3] == "S" && messageParts[4] != "0"))))
            {
                return false;
            }
            CpuIdlePhase cpuIdlePhase = ((messageParts[3] == "R") ? CpuIdlePhase.Restoring : ((messageParts[3] == "S") ? CpuIdlePhase.Settled : ((messageParts[3] == "O") ? CpuIdlePhase.Owned : CpuIdlePhase.Prepared)));
            CpuIdleReceipt cpuIdleReceipt = new CpuIdleReceipt();
            cpuIdleReceipt.Scheme = result;
            cpuIdleReceipt.Phase = cpuIdlePhase;
            cpuIdleReceipt.PendingMask = current ? messageParts[4][0] - '0' : -1;
            PowerPlan.cpuIdleReceipt = cpuIdleReceipt;
            return true;
        }

        private static bool ClearCpuIdleReceipt()
        {
            if (cpuIdleReceipt == null || cpuIdleReceipt.Phase != CpuIdlePhase.Settled)
            {
                return false;
            }
            if (!WriteCpuIdleLedgerVerified(cpuIdleReceipt.Text(CpuIdlePhase.Settled)))
            {
                return false;
            }
            if (!WriteCpuIdleLedgerVerified(""))
            {
                return false;
            }
            cpuIdleReceipt = null;
            return true;
        }

        private static bool WriteCpuIdleLedgerVerified(string text)
        {
            string message;
            if (CpuIdleWriteLedger(text) && CpuIdleReadLedger(out message))
            {
                return string.Equals(message, text, StringComparison.Ordinal);
            }
            return false;
        }

        private static bool ReapplyCpuIdle(Guid scheme)
        {
            Guid? guid = CpuIdleCurrentScheme();
            if (!guid.HasValue || guid.Value == Guid.Empty)
            {
                return false;
            }
            if (guid.Value != scheme)
            {
                return true;
            }
            uint ac;
            uint dc;
            if (!CpuIdleReadValues(scheme, out ac, out dc))
            {
                return false;
            }
            if (ac != 0 || dc != 0) return true;
            guid = CpuIdleCurrentScheme();
            if (!guid.HasValue || guid.Value == Guid.Empty)
            {
                return false;
            }
            if (guid.Value != scheme)
            {
                return true;
            }
            if (!CpuIdleSetActive(scheme))
            {
                return false;
            }
            guid = CpuIdleCurrentScheme();
            if (!guid.HasValue || guid.Value != scheme || !CpuIdleReadValues(scheme, out ac, out dc))
            {
                return false;
            }
            return true;
        }

        private static bool RestoreCpuIdleSide(CpuIdleReceipt receipt, bool ac)
        {
            int bit = ac ? 1 : 2;
            if ((receipt.PendingMask & bit) == 0) return true;
            uint value;
            if (!CpuIdleReadValue(receipt.Scheme, ac, out value)) return false;
            if (value == 1)
            {
                if (!(ac ? WriteAc(receipt.Scheme, SubProcessor, IdleDisableSet, 0u)
                    : WriteDc(receipt.Scheme, SubProcessor, IdleDisableSet, 0u))) return false;
                if (!CpuIdleReadValue(receipt.Scheme, ac, out value) || value == 1) return false;
            }
            // 零表示已恢复，其他非持有值归外部写入方。处理下一供电来源前，先提交本来源的释放，避免后来出现的 1 被重新接管。

            receipt.PendingMask &= ~bit;
            return WriteCpuIdleLedgerVerified(receipt.Text(CpuIdlePhase.Restoring));
        }

        private static Guid? CpuIdleCurrentScheme()
        {
            return Current();
        }

        private static bool CpuIdleReadValue(Guid scheme, bool ac, out uint value)
        {
            return ac ? ReadAc(scheme, SubProcessor, IdleDisableSet, out value)
                : ReadDc(scheme, SubProcessor, IdleDisableSet, out value);
        }

        private static bool CpuIdleReadValues(Guid scheme, out uint ac, out uint dc)
        {
            dc = 0u;
            return CpuIdleReadValue(scheme, true, out ac) && CpuIdleReadValue(scheme, false, out dc);
        }

        private static bool CpuIdleSetActive(Guid scheme)
        {
            return Set(scheme);
        }

        private static bool CpuIdleReadLedger(out string text)
        {
            return Settings.TryLoadStr("CpuIdleStateV1", out text);
        }

        private static bool CpuIdleWriteLedger(string text)
        {
            return Settings.SaveStrDurable("CpuIdleStateV1", text);
        }

        internal static bool ClearLegacyIdleDisableOnce()
        {
            lock (lk)
            {
                if (!RestoreLegacyCpuIdle() || LegacyCpuIdleHasResidue)
                {
                    return false;
                }
                return ClearLegacyIdleDisableOnceCore();
            }
        }

        private static bool ClearLegacyIdleDisableOnceCore()
        {
            // 先恢复现有记录；旧托管计划名称不能授权新的掌机修复。
            if (HandheldBypass) return true;
            ManagedPolicyRecord currentRecord;
            if (ReadManagedPolicy(out currentRecord) && currentRecord != null && currentRecord.CurrentTemplate) return true;
            if (Settings.Load("IdleDisableCleared", false))
            {
                return true;
            }
            try
            {
                Guid guid = ManagedPlanGuid();
                if (guid == Guid.Empty || !SettingPresent(guid, SubProcessor, IdleDisableSet))
                {
                    Settings.Save("IdleDisableCleared", true);
                    return true;
                }
                if (!WritePair(guid, SubProcessor, IdleDisableSet, 0u, 0u))
                {
                    return false;
                }
                Guid? guid2 = Current();
                if (guid2.HasValue && guid2.Value == guid && !Set(guid))
                {
                    return false;
                }
                Settings.Save("IdleDisableCleared", true);
                Logger.Log(Lang.T("log.powerplanschemes.47", 0));
                return true;
            }
            catch
            {
                return false;
            }
        }

    }
}
