using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal enum ManagedPowerSchemeState { Present, ConfirmedMissing, Unavailable }

    internal struct ManagedPowerSchemeEvidence
    {
        internal readonly ManagedPowerSchemeState State;
        internal readonly uint NativeCode;
        internal ManagedPowerSchemeEvidence(ManagedPowerSchemeState state, uint nativeCode)
        { State = state; NativeCode = nativeCode; }
    }

    internal interface IManagedPowerSchemeEvidence
    {
        ManagedPowerSchemeEvidence SchemeEvidence(Guid scheme);
    }

    internal interface IPowerPresetWriterPlatform
    {
        Guid? Current();
        bool Exists(Guid scheme);
        uint Duplicate(Guid source, Guid destination);
        ManagedPowerPresetValue[] StoredSettings(Guid scheme);
        uint ReadDefault(ManagedPowerPresetValue value, Guid personality, bool ac, out uint index);
        bool IsRegistered(ManagedPowerPresetValue value);
        uint Write(Guid scheme, ManagedPowerPresetValue value, bool ac, uint index);
        uint Read(Guid scheme, ManagedPowerPresetValue value, bool ac, out uint index);
    }

    internal interface IVerifiedPowerPresetBaseline
    {
        bool VerifyBaseline(Guid baseline, Guid destination);
    }

    internal static class PowerPresetWriter
    {
        [ThreadStatic] private static uint failureNativeCode;
        internal static uint LastFailureNativeCode { get { return failureNativeCode; } }
        internal static ManagedPowerSchemeEvidence ClassifySchemeEvidence(uint nativeCode)
        {
            return new ManagedPowerSchemeEvidence(nativeCode == 0 || nativeCode == 234 ? ManagedPowerSchemeState.Present
                : nativeCode == 2 || nativeCode == 3 ? ManagedPowerSchemeState.ConfirmedMissing
                : ManagedPowerSchemeState.Unavailable, nativeCode);
        }

        private static ManagedPowerSchemeEvidence SchemeEvidence(IPowerPresetWriterPlatform platform, Guid scheme)
        {
            var evidence = platform as IManagedPowerSchemeEvidence;
            return evidence != null ? evidence.SchemeEvidence(scheme)
                : ClassifySchemeEvidence(platform.Exists(scheme) ? 0u : 2u);
        }
        internal static string SettingPath(ManagedPowerPresetValue value)
        {
            return (value.Sub == ManagedPowerPreset.SubNone ? "" : value.Sub.ToString("D") + "\\")
                + value.Setting.ToString("D");
        }

        internal static bool IsRegistered(string path)
        {
            // 隐藏参数仍有系统定义，不按 Attributes 过滤。
            using (RegistryKey definition = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\" + path))
                return definition != null;
        }

        internal static bool Create(ManagedPowerPreset preset, Guid destination, Guid ultimate, Guid highPerformance,
            IPowerPresetWriterPlatform platform, out string failure)
        { return Create(preset, destination, ultimate, highPerformance, Guid.Empty, platform, out failure); }

        internal static bool Create(ManagedPowerPreset preset, Guid destination, Guid ultimate, Guid highPerformance,
            Guid verifiedBaseline, IPowerPresetWriterPlatform platform, out string failure)
        {
            failure = "";
            failureNativeCode = 0;
            if (preset == null) return Fail(out failure, "Create: missing parameter set");
            Guid copySource;
            if (!TryCopyTemplate(destination, ultimate, highPerformance, verifiedBaseline, platform, out copySource, out failure)) return false;
            if (!NormalizeInherited(preset, destination, copySource == verifiedBaseline ? verifiedBaseline : Guid.Empty, platform, out failure)) return false;

            int written = 0, skipped = 0;
            foreach (ManagedPowerPresetValue value in preset.Entries)
            {
                if (!platform.IsRegistered(value))
                {
                    skipped++;
                    Logger.Info("POWER source=" + preset.Id + " unregistered setting skipped: " + SettingPath(value));
                    continue;
                }
                foreach (bool ac in new[] { true, false })
                {
                    string raw = ac ? value.AcRaw : value.DcRaw;
                    uint? requested = ac ? value.Ac : value.Dc;
                    int type = ac ? value.AcRegistryType : value.DcRegistryType;
                    string field = (ac ? "AC " : "DC ") + SettingPath(value);
                    if (raw != null || (requested.HasValue && type != (int)RegistryValueKind.DWord))
                        return Fail(out failure, "Unsupported preset value type: " + field + " type=" + type);
                    if (!requested.HasValue) continue;
                    if (!Inactive(platform, destination)) return Fail(out failure, "Write: candidate became active or active plan is unreadable; " + field);
                    uint code = platform.Write(destination, value, ac, requested.Value);
                    if (code != 0) return Fail(out failure, "PowerWrite" + (ac ? "AC" : "DC") + "ValueIndex code=" + code + "; " + field + " requested=" + requested.Value, code);
                    uint actual;
                    code = platform.Read(destination, value, ac, out actual);
                    if (code != 0) return Fail(out failure, "PowerRead" + (ac ? "AC" : "DC") + "ValueIndex code=" + code + "; " + field, code);
                    if (actual != requested.Value)
                        return Fail(out failure, "Readback mismatch: " + field + " requested=" + requested.Value + " actual=" + actual);
                    written++;
                }
            }
            if (!Inactive(platform, destination)) return Fail(out failure, "Verify: candidate became active or active plan is unreadable");
            Logger.Info("POWER source=" + preset.Id + " method=duplicate/native-write copySource=" + copySource.ToString("D")
                + " verified=" + written + " unregistered=" + skipped);
            return true;
        }

        // Try hidden templates by GUID. Only native missing-source errors permit the next fallback.
        // Access denial and unsupported operations are failures, not evidence of absence.
        internal static bool TryCopyTemplate(Guid destination, Guid ultimate, Guid highPerformance,
            Guid verifiedBaseline, IPowerPresetWriterPlatform platform, out Guid copySource, out string failure)
        {
            failure = "";
            failureNativeCode = 0;
            copySource = Guid.Empty;
            Guid? active = platform.Current();
            ManagedPowerSchemeEvidence candidate = SchemeEvidence(platform, destination);
            if (destination == Guid.Empty || destination == ultimate || destination == highPerformance
                || !active.HasValue || active == Guid.Empty || active == destination || candidate.State == ManagedPowerSchemeState.Present)
                return Fail(out failure, "Create: destination is not a new inactive plan, or active plan is unreadable");
            if (candidate.State == ManagedPowerSchemeState.Unavailable)
                return Fail(out failure, "Create: candidate existence is unavailable code=" + candidate.NativeCode, candidate.NativeCode);
            if (verifiedBaseline != Guid.Empty)
            {
                ManagedPowerSchemeEvidence baseline = SchemeEvidence(platform, verifiedBaseline);
                if (baseline.State != ManagedPowerSchemeState.Present)
                    return Fail(out failure, "Create: local baseline existence is unavailable code=" + baseline.NativeCode, baseline.NativeCode);
            }
            if (verifiedBaseline != Guid.Empty && !BaselineIdentityVerified(platform, verifiedBaseline, destination))
                return Fail(out failure, "Create: local baseline identity or active-plan snapshot could not be verified");

            copySource = ultimate;
            uint first = platform.Duplicate(ultimate, destination);
            if (first != 0)
            {
                string nativeCodes = "PowerDuplicateScheme ultimate code=" + first;
                candidate = SchemeEvidence(platform, destination);
                if (candidate.State == ManagedPowerSchemeState.Unavailable)
                    return Fail(out failure, nativeCodes + "; candidate existence is unavailable code=" + candidate.NativeCode, candidate.NativeCode);
                if (candidate.State == ManagedPowerSchemeState.Present)
                    return Fail(out failure, nativeCodes + "; partial plan retained for recovery", first);
                if (!Inactive(platform, destination))
                    return Fail(out failure, nativeCodes + "; candidate became active or active plan is unreadable", first);
                if (!SourceUnavailable(first))
                    return Fail(out failure, nativeCodes + "; source fallback denied", first);

                copySource = highPerformance;
                uint fallback = platform.Duplicate(highPerformance, destination);
                if (fallback != 0)
                {
                    nativeCodes += "; high-performance code=" + fallback;
                    candidate = SchemeEvidence(platform, destination);
                    if (candidate.State == ManagedPowerSchemeState.Unavailable)
                        return Fail(out failure, nativeCodes + "; candidate existence is unavailable code=" + candidate.NativeCode, candidate.NativeCode);
                    if (candidate.State == ManagedPowerSchemeState.Present)
                        return Fail(out failure, nativeCodes + "; partial plan retained for recovery", fallback);
                    if (!SourceUnavailable(fallback))
                        return Fail(out failure, nativeCodes + "; local fallback denied", fallback);
                    if (verifiedBaseline == Guid.Empty || verifiedBaseline == ultimate || verifiedBaseline == highPerformance
                        || !BaselineVerified(platform, verifiedBaseline, destination))
                        return Fail(out failure, nativeCodes + "; verified local baseline unavailable", fallback);

                    copySource = verifiedBaseline;
                    uint local = platform.Duplicate(verifiedBaseline, destination);
                    if (local != 0)
                    {
                        candidate = SchemeEvidence(platform, destination);
                        return Fail(out failure, nativeCodes + "; local-baseline code=" + local
                            + (candidate.State == ManagedPowerSchemeState.Present ? "; partial plan retained for recovery"
                                : candidate.State == ManagedPowerSchemeState.Unavailable ? "; candidate existence is unavailable code=" + candidate.NativeCode : ""),
                            candidate.State == ManagedPowerSchemeState.Unavailable ? candidate.NativeCode : local);
                    }
                }
            }
            candidate = SchemeEvidence(platform, destination);
            if (candidate.State != ManagedPowerSchemeState.Present)
                return Fail(out failure, "PowerDuplicateScheme: created plan could not be read back code=" + candidate.NativeCode, candidate.NativeCode);
            if (!Inactive(platform, destination)) return Fail(out failure, "Prepare: candidate became active or active plan is unreadable");
            var verifier = platform as IVerifiedPowerPresetBaseline;
            if (verifiedBaseline != Guid.Empty && copySource == verifiedBaseline && (platform.Current() != verifiedBaseline
                || verifier == null || !verifier.VerifyBaseline(verifiedBaseline, destination)
                || platform.Current() != verifiedBaseline))
                return Fail(out failure, "Prepare: local baseline admission changed after duplication");
            return true;
        }

        private static bool SourceUnavailable(uint code)
        { return code == 2 || code == 3; }

        private static bool BaselineVerified(IPowerPresetWriterPlatform platform, Guid baseline, Guid destination)
        {
            return platform.Current() == baseline && BaselineIdentityVerified(platform, baseline, destination)
                && platform.Current() == baseline && SchemeEvidence(platform, destination).State == ManagedPowerSchemeState.ConfirmedMissing;
        }

        private static bool BaselineIdentityVerified(IPowerPresetWriterPlatform platform, Guid baseline, Guid destination)
        {
            Guid? active = platform.Current();
            var verifier = platform as IVerifiedPowerPresetBaseline;
            return baseline != Guid.Empty && baseline != destination && active.HasValue && active != Guid.Empty && active != destination
                && SchemeEvidence(platform, baseline).State == ManagedPowerSchemeState.Present
                && verifier != null && verifier.VerifyBaseline(baseline, destination)
                && platform.Current() == active && SchemeEvidence(platform, destination).State == ManagedPowerSchemeState.ConfirmedMissing;
        }

        private static bool NormalizeInherited(ManagedPowerPreset preset, Guid destination, Guid copiedBaseline,
            IPowerPresetWriterPlatform platform, out string failure)
        {
            failure = "";
            var explicitValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var templateSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Guid acPersonality = Guid.Empty, dcPersonality = Guid.Empty;
            foreach (ManagedPowerPresetValue value in preset.Entries)
            {
                string path = SettingPath(value);
                templateSettings.Add(path);
                if (value.Ac.HasValue) explicitValues.Add(path + "/AC");
                if (value.Dc.HasValue) explicitValues.Add(path + "/DC");
                if (value.Sub == ManagedPowerPreset.SubNone && value.Setting == new Guid("245d8541-3943-4422-b025-13a784f679b7"))
                { acPersonality = Personality(value.Ac); dcPersonality = Personality(value.Dc); }
            }
            if (acPersonality == Guid.Empty || dcPersonality == Guid.Empty)
                return Fail(out failure, "Reset: preset personality is missing or unsupported");
            ManagedPowerPresetValue[] inherited = platform.StoredSettings(destination);
            if (inherited == null) return Fail(out failure, "Reset: inherited settings could not be enumerated");
            foreach (ManagedPowerPresetValue value in inherited)
            {
                if (!platform.IsRegistered(value)) continue;
                foreach (bool ac in new[] { true, false })
                {
                    if (!(ac ? value.Ac : value.Dc).HasValue || explicitValues.Contains(SettingPath(value) + (ac ? "/AC" : "/DC"))) continue;
                    if (!Inactive(platform, destination)) return Fail(out failure, "Reset: candidate became active or active plan is unreadable");
                    // 按原配置的 personality 解析缺省值，不继承复制源额外的覆盖，也不改注册表权限。
                    uint expected, actual;
                    uint code = platform.ReadDefault(value, ac ? acPersonality : dcPersonality, ac, out expected);
                    string field = (ac ? "AC " : "DC ") + SettingPath(value);
                    if (code != 0)
                    {
                        // Copy OEM-only values only after verifying the exact admitted local baseline.
                        string retainedFailure;
                        if (SourceUnavailable(code) && copiedBaseline != Guid.Empty && !templateSettings.Contains(SettingPath(value)))
                        {
                            if (RetainVerifiedLocalValue(platform, copiedBaseline, destination, value, ac, out retainedFailure)) continue;
                            return Fail(out failure, "Read default: " + field + " code=" + code + "; " + retainedFailure, code);
                        }
                        return Fail(out failure, "Read default: " + field + " code=" + code, code);
                    }
                    code = platform.Write(destination, value, ac, expected);
                    if (code != 0) return Fail(out failure, "Write default: " + field + " code=" + code, code);
                    code = platform.Read(destination, value, ac, out actual);
                    if (code != 0 || actual != expected)
                        return Fail(out failure, "Default readback: " + field + " code=" + code + " expected=" + expected + " actual=" + actual, code);
                }
            }
            return true;
        }

        private static bool RetainVerifiedLocalValue(IPowerPresetWriterPlatform platform, Guid baseline, Guid destination,
            ManagedPowerPresetValue value, bool ac, out string failure)
        {
            failure = "";
            var verifier = platform as IVerifiedPowerPresetBaseline;
            if (verifier == null || !verifier.VerifyBaseline(baseline, destination)
                || platform.Current() != baseline || !Inactive(platform, destination))
                return Fail(out failure, "Local extra value admission changed");
            uint original, actual;
            uint sourceCode = platform.Read(baseline, value, ac, out original);
            if (sourceCode != 0) return Fail(out failure, "Local extra value source read code=" + sourceCode, sourceCode);
            uint targetCode = platform.Read(destination, value, ac, out actual);
            if (targetCode != 0 || original != actual)
                return Fail(out failure, "Local extra value readback code=" + targetCode + " expected=" + original + " actual=" + actual, targetCode);
            return platform.Current() == baseline && Inactive(platform, destination)
                && verifier.VerifyBaseline(baseline, destination) || Fail(out failure, "Local extra value admission changed after readback");
        }

        private static Guid Personality(uint? index)
        {
            if (index == 0) return new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
            if (index == 1) return new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            if (index == 2) return new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
            return Guid.Empty;
        }

        private static bool Inactive(IPowerPresetWriterPlatform platform, Guid destination)
        { Guid? active = platform.Current(); return active.HasValue && active != Guid.Empty && active != destination; }

        internal static bool PermanentNativeFailure(uint code) { return code == 5 || code == 50 || code == 120; }

        private static bool Fail(out string failure, string detail, uint nativeCode = 0)
        { if (failureNativeCode == 0) failureNativeCode = nativeCode; failure = detail; return false; }
    }
}
