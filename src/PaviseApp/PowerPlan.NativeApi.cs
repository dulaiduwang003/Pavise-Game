using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {

        [DllImport("powrprof.dll", EntryPoint = "PowerEnumerate")]
        private static extern uint EnumerateProcessorSetting(IntPtr root, ref Guid scheme, ref Guid sub, uint access, uint index, byte[] buffer, ref uint size);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadValueMin(IntPtr root, ref Guid sub, ref Guid setting, out uint min);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadValueMax(IntPtr root, ref Guid sub, ref Guid setting, out uint max);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid src, ref IntPtr dest);

        [DllImport("powrprof.dll")]
        private static extern uint PowerDeleteScheme(IntPtr root, ref Guid guid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subgroup, uint accessFlags, uint index, byte[] buffer, ref uint size);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, IntPtr buffer, ref uint size);

        [DllImport("powrprof.dll", EntryPoint = "PowerReadFriendlyName")]
        private static extern uint PowerReadFriendlyNameBuf(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, ref uint size);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteDescription(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr h);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

        private static bool TuneTarget(Guid g, bool aggressive, bool legacy)
        {
            return legacy ? TuneLegacy3110Target(g, aggressive) : TunePresetTarget(g, aggressive);
        }

        private static void ReadAutonomousScaling(Guid scheme, out bool? ac, out bool? dc)
        {
            ac = null;
            dc = null;
            try
            {
                ProcessorPowerPlatform.Interface current = ProcessorPowerPlatform.Current;
                uint value;
                uint? requested = (ReadAc(scheme, SubProcessor, PerfAutonomous, out value) ? new uint?(value) : null);
                uint? requested2 = (ReadDc(scheme, SubProcessor, PerfAutonomous, out value) ? new uint?(value) : null);
                ac = ProcessorPowerPlatform.AutonomousMinimum(current, requested);
                dc = ProcessorPowerPlatform.AutonomousMinimum(current, requested2);
            }
            catch
            {
            }
        }

        internal static bool ReadAc(Guid scheme, Guid sub, Guid setting, out uint value)
        {
            Guid sub2 = sub;
            Guid setting2 = setting;
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, out value) == 0;
        }

        internal static uint? ActivePersonality()
        {
            Guid? guid = Current();
            uint value;
            if (!guid.HasValue || !ReadAc(guid.Value, SubNone, Personality, out value))
            {
                return null;
            }
            return value;
        }

        internal static bool ReadDc(Guid scheme, Guid sub, Guid setting, out uint value)
        {
            Guid sub2 = sub;
            Guid setting2 = setting;
            return PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, out value) == 0;
        }

        internal static bool WriteDc(Guid scheme, Guid sub, Guid setting, uint value)
        {
            Guid sub2 = sub;
            Guid setting2 = setting;
            return PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, value) == 0;
        }

        internal static bool WriteAc(Guid scheme, Guid sub, Guid setting, uint value)
        {
            Guid sub2 = sub;
            Guid setting2 = setting;
            return PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, value) == 0;
        }

        private static bool SettingPresent(Guid scheme, Guid sub, Guid setting)
        {
            Guid sub2 = sub;
            Guid setting2 = setting;
            uint value;
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, out value) == 0;
        }

        private static uint Clamp(Guid sub, Guid setting, uint v)
        {
            Guid sub2 = sub;
            Guid setting2 = setting;
            uint min;
            if (PowerReadValueMin(IntPtr.Zero, ref sub2, ref setting2, out min) != 0)
            {
                return v;
            }
            sub2 = sub;
            setting2 = setting;
            uint max;
            if (PowerReadValueMax(IntPtr.Zero, ref sub2, ref setting2, out max) != 0)
            {
                return v;
            }
            if (min > max)
            {
                return v;
            }
            if (v < min)
            {
                return min;
            }
            if (v > max)
            {
                return max;
            }
            return v;
        }

        private static bool WritePair(Guid scheme, Guid sub, Guid setting, uint ac, uint dc)
        {
            uint code;
            return WritePair(scheme, sub, setting, ac, dc, out code);
        }

        private static bool WritePair(Guid scheme, Guid sub, Guid setting, uint ac, uint dc, out uint code)
        {
            Guid sub2 = sub;
            Guid setting2 = setting;
            code = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, ac);
            if (code == 87)
            {
                uint acLimit = Clamp(sub, setting, ac);
                if (acLimit != ac)
                {
                    sub2 = sub;
                    setting2 = setting;
                    code = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, acLimit);
                }
            }
            if (code != 0)
            {
                return false;
            }
            sub2 = sub;
            setting2 = setting;
            code = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, dc);
            if (code == 87)
            {
                uint dcLimit = Clamp(sub, setting, dc);
                if (dcLimit != dc)
                {
                    sub2 = sub;
                    setting2 = setting;
                    code = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, dcLimit);
                }
            }
            return code == 0;
        }

        private static Guid? Current()
        {
            IntPtr guid;
            if (PowerGetActiveScheme(IntPtr.Zero, out guid) != 0 || guid == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                return (Guid)Marshal.PtrToStructure(guid, typeof(Guid));
            }
            catch
            {
                return null;
            }
            finally
            {
                LocalFree(guid);
            }
        }

        private static bool Set(Guid g)
        {
            uint code = PowerSetActiveScheme(IntPtr.Zero, ref g);
            if (code != 0)
            {
                if (string.IsNullOrEmpty(applyFailureReason)) applyFailurePermanent = code == 5 || code == 50 || code == 120;
                Guid? observed = Current();
                return PlanActivationFailed(g, "api=PowerSetActiveScheme code=" + code + " observed=" + (observed.HasValue ? observed.Value.ToString("D") : "unreadable"));
            }
            Guid? guid = Current();
            return guid == g || PlanActivationFailed(g, "api=PowerSetActiveScheme code=0 observed=" + (guid.HasValue ? guid.Value.ToString("D") : "unreadable"));
        }

        private static bool PlanActivationFailed(Guid targetPlan, string detail)
        {
            string failure = "stage=activation target=" + targetPlan.ToString("D") + " " + detail;
            if (string.IsNullOrEmpty(applyFailureReason))
                applyFailureReason = failure;
            // 恢复错误记录自身的原生失败，不能覆盖最初的应用失败。
            Logger.Warn("电源 阶段=原生 " + failure);
            return false;
        }

        private static bool SchemeUsable(Guid g)
        {
            uint size = 0u;
            uint powerReadFriendlyNameResult = PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            if (powerReadFriendlyNameResult != 0)
            {
                return powerReadFriendlyNameResult == 234;
            }
            return true;
        }

        private static ManagedPowerSchemeEvidence ReadManagedSchemeEvidenceNative(Guid scheme)
        {
            try
            {
                uint size = 0;
                return PowerPresetWriter.ClassifySchemeEvidence(PowerReadFriendlyName(IntPtr.Zero,
                    ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size));
            }
            catch { return PowerPresetWriter.ClassifySchemeEvidence(31); }
        }

        private static ManagedPowerSchemeEvidence ReadManagedSchemeEvidence(IManagedPowerPlans plans, Guid scheme)
        {
            var evidence = plans as IManagedPowerSchemeEvidence;
            return evidence != null ? evidence.SchemeEvidence(scheme)
                : PowerPresetWriter.ClassifySchemeEvidence(plans.Exists(scheme) ? 0u : 2u);
        }

        private static bool ManagedSchemeEvidenceAvailable(ManagedPowerSchemeEvidence evidence, Guid scheme)
        {
            if (evidence.State != ManagedPowerSchemeState.Unavailable) return true;
            applyFailurePermanent |= PowerPresetWriter.PermanentNativeFailure(evidence.NativeCode);
            return PowerApplyFailed("plan-existence", "Power-plan existence unavailable code=" + evidence.NativeCode
                + " scheme=" + scheme.ToString("D"));
        }

        private static string ReadName(Guid g)
        {
            try
            {
                uint size = 0u;
                uint powerReadFriendlyNameBufResult = PowerReadFriendlyNameBuf(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size);
                if ((powerReadFriendlyNameBufResult != 0 && powerReadFriendlyNameBufResult != 234) || size == 0 || size > 4096)
                {
                    return "";
                }
                byte[] rawBytes = new byte[size];
                if (PowerReadFriendlyNameBuf(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, rawBytes, ref size) != 0)
                {
                    return "";
                }
                string @string = Encoding.Unicode.GetString(rawBytes);
                char[] trimChars = new char[1];
                return @string.TrimEnd(trimChars);
            }
            catch
            {
                return "";
            }
        }

        private static bool WriteName(Guid g, string title, string note)
        {
            try
            {
                byte[] bytes = Encoding.Unicode.GetBytes(title + "\0");
                Guid scheme = g;
                if (PowerWriteFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length) != 0)
                {
                    return false;
                }
                byte[] bytes2 = Encoding.Unicode.GetBytes(note + "\0");
                scheme = g;
                PowerWriteDescription(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, bytes2, (uint)bytes2.Length);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<Guid> EnumerateSchemes()
        {
            List<Guid> list;
            TryEnumerateSchemes(out list);
            return list;
        }

        private static bool TryEnumerateSchemes(out List<Guid> list)
        {
            list = new List<Guid>();
            try
            {
                uint processedCount = 0u;
                while (processedCount < 128)
                {
                    uint size = 16u;
                    byte[] rawBytes = new byte[16];
                    switch (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16u, processedCount, rawBytes, ref size))
                    {
                    case 259u:
                        return true;
                    case 0u:
                        if (size == 16)
                        {
                            Guid guid = new Guid(rawBytes);
                            if (guid == Guid.Empty || list.Contains(guid))
                            {
                                return false;
                            }
                            list.Add(guid);
                            processedCount++;
                            break;
                        }
                        goto default;
                    default:
                        return false;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        internal static void SyncDisplayFeel(Guid src, Guid dst)
        {
            Guid[] guidItems = new Guid[3] { VideoBrightness, VideoDimBrightness, VideoAdaptive };
            Guid[] guidItems2 = guidItems;
            foreach (Guid guid in guidItems2)
            {
                Guid scheme = src;
                Guid scheme2 = dst;
                Guid sub = SubVideo;
                Guid setting = guid;
                uint value;
                if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value) == 0)
                {
                    PowerWriteACValueIndex(IntPtr.Zero, ref scheme2, ref sub, ref setting, value);
                }
                if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value) == 0)
                {
                    PowerWriteDCValueIndex(IntPtr.Zero, ref scheme2, ref sub, ref setting, value);
                }
            }
        }

        private static bool Duplicate(Guid src, out Guid created)
        {
            created = Guid.Empty;
            IntPtr dest = IntPtr.Zero;
            Guid src2 = src;
            if (PowerDuplicateScheme(IntPtr.Zero, ref src2, ref dest) != 0 || dest == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                created = (Guid)Marshal.PtrToStructure(dest, typeof(Guid));
            }
            catch
            {
                return false;
            }
            finally
            {
                LocalFree(dest);
            }
            return created != Guid.Empty;
        }

    }
}
