using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
    // MMCSS 系统配置的 6 个值 驱动启动时读取 重启电脑才生效
    internal static class MultimediaProfile
    {
        internal const string ProfilePath = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile";

        internal const string GamesPath = ProfilePath + "\\Tasks\\Games";

        // 路径 值名 写入内容 写入内容带类型前缀
        private static readonly string[,] Values = new string[6, 3]
        {
            { ProfilePath, "NetworkThrottlingIndex", "d:-1" },
            { ProfilePath, "SystemResponsiveness", "d:10" },
            { GamesPath, "Scheduling Category", "s:High" },
            { GamesPath, "Priority", "d:6" },
            { GamesPath, "GPU Priority", "d:8" },
            { GamesPath, "SFIO Priority", "s:High" }
        };

        // Games 任务不存在时不新建 只写系统配置的两项
        internal static string[] Targets(bool profile, bool games)
        {
            var list = new List<string>();
            for (int i = 0; i < Values.GetLength(0); i++)
                if (Values[i, 0] == ProfilePath ? profile : games) list.Add(Target(i));
            return list.ToArray();
        }

        internal static bool Owns(string target)
        {
            return Wanted(target) != null;
        }

        internal static string Wanted(string target)
        {
            for (int i = 0; i < Values.GetLength(0); i++)
                if (string.Equals(target, Target(i), StringComparison.OrdinalIgnoreCase)) return Values[i, 2];
            return null;
        }

        private static string Target(int index)
        {
            return PerformanceActionBackend.RegistryTarget(Values[index, 0], Values[index, 1]);
        }
    }

    internal static class PerformanceActionBackend
    {
#if PAVISE_SELFTEST
        internal static Func<string> QuantumReadForTest;
        internal static Func<string, bool> QuantumWriteForTest;
#endif
#if PAVISE_SELFTEST
        internal static Func<string, string, string> ReadForTest;
        internal static Func<string, string, string, bool> WriteForTest;
        internal static Func<string, string[]> TargetsForTest;
        internal static Func<int> DefenderServiceForTest;
        internal static Func<bool> NvAvailableForTest;
#endif
        internal sealed class DefenderInactiveException : NotSupportedException
        {
            internal DefenderInactiveException()
                : base(PerformancePolicy.Text("Defender 未运行，旧记录暂待恢复", "Defender is not running; legacy records are awaiting restoration"))
            {
            }
        }

        internal static bool DefenderRunning()
        {
#if PAVISE_SELFTEST
            if (DefenderServiceForTest == null) throw new InvalidOperationException("Unmocked Defender service query");
            return DefenderServiceForTest() == 4;
#endif
            return SvcState.Query("WinDefend") == 4;
        }

        private static bool DefenderAction(string id)
        {
            if (id != "AutoDefenderSessionV1")
            {
                return id == "AutoDefenderResourcesV1";
            }
            return true;
        }

        internal static string LegacyDefenderRestoreScript(string value)
        {
            string[] parts = (value ?? "").Split(',');
            bool result;
            bool result2;
            if (parts.Length != 2 || !bool.TryParse(parts[0], out result) || !bool.TryParse(parts[1], out result2))
            {
                return null;
            }
            if (result && result2)
            {
                return "";
            }
            return "Set-MpPreference" + (result ? "" : " -DisableRealtimeMonitoring $false") + (result2 ? "" : " -DisableBehaviorMonitoring $false") + " -ErrorAction Stop";
        }

        internal static string Quote(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }

        private static string Script(string script)
        {
            string stdout;
            string failure;
            if (!PsRunner.RunWithFailure("$ErrorActionPreference='Stop'; " + script, "Performance", 10000, out stdout, out failure))
            {
                throw new InvalidOperationException(failure);
            }
            return stdout.Trim();
        }

        internal static string Read(string id, string target)
        {
#if PAVISE_SELFTEST
            if (id == PerformanceActionCatalog.Quantum && target == QuantumRecovery.Target && QuantumReadForTest != null)
                return QuantumReadForTest();
#endif
            if (id == "MatchVisualEffectsV1")
            {
                SharedSpiManual.RequirePrimitive(target, false);
            }
            if (DefenderAction(id) && !DefenderRunning())
            {
                throw new DefenderInactiveException();
            }
#if PAVISE_SELFTEST
            if (ReadForTest == null) throw new InvalidOperationException("Unmocked performance read");
            return ReadForTest(id, target);
#endif
            switch (id)
            {
            case "MatchVisualEffectsV1":
                return MatchVisualEffects.Read(target);
            case "AutoDefenderSessionV1":
                return Script("$p=Get-MpPreference; Write-Output ($p.DisableRealtimeMonitoring.ToString()+','+$p.DisableBehaviorMonitoring.ToString())");
            case "AutoDefenderResourcesV1":
                return Script("$p=Get-MpPreference; if(@($p.ExclusionPath)-contains " + Quote(target) + "){Write-Output '1'}else{Write-Output '0'}");
            case "AutoNtfsShortNamesV1":
                return Script("$v=& $env:SystemRoot\\System32\\fsutil.exe 8dot3name query " + Quote(target) + "; if($LASTEXITCODE-ne0){throw 'Volume query failed'}; $m=[regex]::Match(($v -join \"`n\"),'[：:]\\s*([01])\\s*(?:\\(|$)'); if(!$m.Success){throw 'Unknown volume state'}; Write-Output $m.Groups[1].Value");
            case "AutoBackgroundFpsV1":
                return ReadNv(id, target);
            default:
                return ReadRegistry(target);
            }
        }

        internal static bool Write(string id, string target, string value)
        {
            if (id == PerformanceActionCatalog.Quantum && target == QuantumRecovery.Target)
            {
                if (!QuantumLiveUpdate.PrepareWrite()) return false;
                bool written;
#if PAVISE_SELFTEST
                // 自检构建没注入时间片写入替身就直接拒绝 不能落到真实的 HKLM
                if (QuantumWriteForTest == null) throw new InvalidOperationException("Unmocked quantum write");
                written = QuantumWriteForTest(value);
#else
                written = WriteRegistry(target, value);
#endif
                if (!written || Read(id, target) != value) return false;
                QuantumLiveUpdate.AfterWrite(value);
                return true;
            }
            if (id == "MatchVisualEffectsV1")
            {
                SharedSpiManual.RequirePrimitive(target, true, value);
            }
            if (id == "AutoDefenderResourcesV1" && value != "0")
            {
                return false;
            }
            if (DefenderAction(id) && !DefenderRunning())
            {
                throw new DefenderInactiveException();
            }
#if PAVISE_SELFTEST
            if (WriteForTest == null) throw new InvalidOperationException("Unmocked performance write");
            return WriteForTest(id, target, value);
#endif
            switch (id)
            {
            case "MatchVisualEffectsV1":
                return MatchVisualEffects.Write(target, value);
            case "AutoDefenderSessionV1":
            {
                string legacyDefenderRestoreScriptResult = LegacyDefenderRestoreScript(value);
                if (legacyDefenderRestoreScriptResult == null)
                {
                    return false;
                }
                if (legacyDefenderRestoreScriptResult.Length > 0)
                {
                    Script(legacyDefenderRestoreScriptResult);
                }
                return true;
            }
            case "AutoDefenderResourcesV1":
                Script("Remove-MpPreference -ExclusionPath " + Quote(target) + " -ErrorAction Stop");
                return true;
            case "AutoNtfsShortNamesV1":
                if (value != "0" && value != "1")
                {
                    return false;
                }
                Script("& $env:SystemRoot\\System32\\fsutil.exe 8dot3name set " + Quote(target) + " " + value + "; if($LASTEXITCODE-ne0){throw 'Volume configuration failed'}");
                return true;
            case "AutoBackgroundFpsV1":
                return WriteNv(id, target, value);
            default:
                return WriteRegistry(target, value);
            }
        }

        internal static bool Apply(string id, string target, string wanted)
        {
            if (id == "AutoBackgroundFpsV1")
            {
                using (GameNvNativeGate.Enter())
                {
                    return ApplyCore(id, target, wanted);
                }
            }
            return ApplyCore(id, target, wanted);
        }

        private static bool ApplyCore(string id, string target, string wanted)
        {
            if (id == "MatchVisualEffectsV1")
            {
                // 功能已退役 保留按记录恢复的路径 不再应用新改动
                return false;
            }
            if (id == "AutoDefenderResourcesV1")
            {
                return false;
            }
            if (id == "AutoDefenderSessionV1")
            {
                throw new InvalidOperationException("The legacy Defender change is restore-only");
            }
            if (PerformanceActionCatalog.IsRetired(id))
            {
                // 其他已下线项同样只按记录恢复 不再写入新改动
                return false;
            }
            return PerformanceActionStore.Change(id, target, wanted, () => Read(id, target), (string value) => Write(id, target, value), delegate(string value)
            {
                uint result;
                if (id == "AutoBackgroundFpsV1" && uint.TryParse(value, out result) && result != 0 && result <= 30)
                {
                    return true;
                }
                return value == wanted;
            });
        }

        internal static string ApplyFailureHint(string id, string target)
        {
            string lastChangeFailure = PerformanceActionStore.LastChangeFailure;
            switch (lastChangeFailure)
            {
            case "Recovery record ownership does not match the target":
                return PerformancePolicy.Text("恢复记录与目标不一致，未写入", lastChangeFailure);
            case "Could not save the recovery record":
                return PerformancePolicy.Text("原配置记录保存失败，本次未写入", lastChangeFailure);
            case "Could not commit the recovery record":
                return PerformancePolicy.Text("写入后的恢复记录保存失败，已保留记录", lastChangeFailure);
            case "Configuration write did not complete":
                return PerformancePolicy.Text("配置写入未完成，已保留恢复记录", lastChangeFailure);
            case "Configuration did not match the requested state after writing":
                return PerformancePolicy.Text("配置写入后复核不一致，已保留恢复记录", lastChangeFailure);
            case "Configuration changed externally; previous ownership released":
                return PerformancePolicy.Text("配置已被外部修改，已解除旧配置托管", lastChangeFailure);
            case "Configuration changed externally; recovery record could not be cleared":
                return PerformancePolicy.Text("配置已被外部修改，旧恢复记录清理未完成", lastChangeFailure);
            default:
                return lastChangeFailure ?? PerformancePolicy.Text("写入或复核未完成，已保留恢复记录", "Write or verification incomplete; recovery record retained");
            }
        }

        internal static bool Restore(PerformanceActionStore.Receipt r)
        {
            if (r.Id == "MatchVisualEffectsV1")
            {
                return SharedSpiManual.Restore(r);
            }
            if (QuantumRecovery.Chained(r))
            {
                return QuantumRecovery.RestoreThrough(r);
            }
            if (DefenderAction(r.Id) && !DefenderRunning())
            {
                return false;
            }
            if (r.Id == "AutoDefenderResourcesV1")
            {
                if ((r.Original != "0" && r.Original != "1") || r.Expected != "1")
                {
                    return false;
                }
                return PerformanceActionStore.RestoreComponents(r, () => Read(r.Id, r.Target), (string value) => Write(r.Id, r.Target, value), delegate(string current)
                {
                    if (current != "0" && current != "1")
                    {
                        return (string)null;
                    }
                    return (r.Original == "1") ? ((current != "1") ? null : current) : "0";
                });
            }
            if (r.Id == "AutoDefenderSessionV1")
            {
                string[] original = r.Original.Split(',');
                string[] expected = (r.Committed ? r.Expected.Split(',') : null);
                return PerformanceActionStore.RestoreComponents(r, () => Read(r.Id, r.Target), (string value) => Write(r.Id, r.Target, value), delegate(string current)
                {
                    string[] currentParts = current.Split(',');
                    if (currentParts.Length != original.Length || (expected != null && expected.Length != currentParts.Length))
                    {
                        return (string)null;
                    }
                    string[] cloneResult = (string[])currentParts.Clone();
                    for (int i = 0; i < original.Length; i++)
                    {
                        // 没有写入后的快照时 只把仍被关掉的那几项还原
                        bool startsWith = (expected == null) ? (currentParts[i] == "True") : (currentParts[i] == expected[i]);
                        if (startsWith)
                        {
                            cloneResult[i] = original[i];
                        }
                    }
                    return string.Join(",", cloneResult);
                });
            }
            return PerformanceActionStore.Restore(r, () => Read(r.Id, r.Target), (string value) => Write(r.Id, r.Target, value), (string value) => value == r.Expected);
        }

        internal static string RestoreFailureHint(PerformanceActionStore.Receipt r)
        {
            if (DefenderAction(r.Id) && !DefenderRunning())
            {
                return PerformancePolicy.Text("Defender 未运行，已保留恢复记录", "Defender is not running; recovery record retained");
            }
#if PAVISE_SELFTEST
            if (r.Id == "AutoBackgroundFpsV1")
            {
                if (NvAvailableForTest != null && !NvAvailableForTest())
                    return PerformancePolicy.Text("显卡驱动接口不可用，已保留恢复记录", "Graphics driver interface unavailable; recovery record retained");
                return PerformancePolicy.Text("写入或复核未完成，已保留恢复记录", "Write or verification incomplete; recovery record retained");
            }
#endif
            if (r.Id == "AutoBackgroundFpsV1" && !NvApi.Available)
            {
                return PerformancePolicy.Text("显卡驱动接口不可用，已保留恢复记录", "Graphics driver interface unavailable; recovery record retained");
            }
            return PerformancePolicy.Text("写入或复核未完成，已保留恢复记录", "Write or verification incomplete; recovery record retained");
        }

        internal static string RegistryTarget(string path, string name)
        {
            return path + "\n" + name;
        }

        internal static string UnsupportedRegistryKind(RegistryValueKind kind)
        {
            return PerformancePolicy.Text("注册表现有值的类型是 " + kind + "，不是 DWORD、QWORD 或二进制，无法记录原值，未作修改",
                "The existing registry value is of type " + kind + ", not DWORD, QWORD or binary; its original value cannot be recorded, so it was left unchanged");
        }

        private static bool RegistryPathAllowed(string target, string path)
        {
            return path.StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase) || MultimediaProfile.Owns(target);
        }

        internal static string ReadRegistry(string target)
        {
            string[] targetParts = target.Split('\n');
            if (targetParts.Length != 2 || !RegistryPathAllowed(target, targetParts[0]))
            {
                throw new FormatException("Invalid registry target");
            }
            using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(targetParts[0]))
            {
                object value = ((registryKey == null) ? null : registryKey.GetValue(targetParts[1]));
                if (value == null)
                {
                    return "absent";
                }
                RegistryValueKind kind = registryKey.GetValueKind(targetParts[1]);
                switch (kind)
                {
                case RegistryValueKind.DWord:
                    return "d:" + ((int)value).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.QWord:
                    // 注册表接口以有符号 Int64 返回 QWORD 保留全部位及类型 恢复时不能截成 DWORD
                    return "q:" + ((long)value).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.Binary:
                    return "b:" + Convert.ToBase64String((byte[])value);
                case RegistryValueKind.String:
                    if (!MultimediaProfile.Owns(target)) throw new InvalidOperationException(UnsupportedRegistryKind(kind));
                    return "s:" + (string)value;
                default:
        // 不支持的注册表类型无法原样恢复 保留原值并记录实际类型
                    throw new InvalidOperationException(UnsupportedRegistryKind(kind));
                }
            }
        }

        private static bool WriteRegistry(string target, string value)
        {
            string[] targetParts = target.Split('\n');
            if (targetParts.Length != 2 || !RegistryPathAllowed(target, targetParts[0]))
            {
                return false;
            }
            using (RegistryKey registryKey = Registry.LocalMachine.CreateSubKey(targetParts[0]))
            {
                if (registryKey == null)
                {
                    return false;
                }
                if (value == "absent")
                {
                    registryKey.DeleteValue(targetParts[1], false);
                }
                else if (value.StartsWith("d:", StringComparison.Ordinal))
                {
                    registryKey.SetValue(targetParts[1], int.Parse(value.Substring(2), CultureInfo.InvariantCulture), RegistryValueKind.DWord);
                }
                else if (value.StartsWith("q:", StringComparison.Ordinal))
                {
                    registryKey.SetValue(targetParts[1], long.Parse(value.Substring(2), CultureInfo.InvariantCulture), RegistryValueKind.QWord);
                }
                else if (value.StartsWith("s:", StringComparison.Ordinal) && MultimediaProfile.Owns(target))
                {
                    registryKey.SetValue(targetParts[1], value.Substring(2), RegistryValueKind.String);
                }
                else
                {
                    if (!value.StartsWith("b:", StringComparison.Ordinal))
                    {
                        return false;
                    }
                    registryKey.SetValue(targetParts[1], Convert.FromBase64String(value.Substring(2)), RegistryValueKind.Binary);
                }
            }
            return true;
        }

        internal static uint Dword(string value)
        {
            if (value == "absent")
            {
                return 0u;
            }
            if (value == null || !value.StartsWith("d:", StringComparison.Ordinal))
            {
                throw new FormatException("Expected a DWORD setting");
            }
            return (uint)int.Parse(value.Substring(2), CultureInfo.InvariantCulture);
        }

        internal static string WantedRegistry(string id, string original)
        {
            switch (id)
            {
            case "EnvNtfsLastAccessV1":
                // 仅此完整值替换接受 QWORD 原值 目标沿用 DWORD 关闭时按回执恢复原类型
                if (original != null && original.StartsWith("q:", StringComparison.Ordinal))
                    long.Parse(original.Substring(2), CultureInfo.InvariantCulture);
                else Dword(original);
                return "d:1";
            case "EnvCpuMitigationsV1":
                return "d:" + ((int)(Dword(original) | 3)).ToString(CultureInfo.InvariantCulture);
            case "EnvQuantumProfileV1":
                return "d:" + (((int)Dword(original) & -64) | QuantumProfiles.Target).ToString(CultureInfo.InvariantCulture);
            case "EnvAudioIdleV1":
                if (original == null || !original.StartsWith("b:", StringComparison.Ordinal) || Convert.FromBase64String(original.Substring(2)).Length != 4)
                {
                    throw new FormatException("Expected a four-byte audio idle setting");
                }
                return "b:" + Convert.ToBase64String(new byte[4]);
            default:
                Dword(original);
                return "d:1";
            }
        }

        internal static string WantedRegistry(string id, string target, string original)
        {
            if (id == PerformanceActionCatalog.CpuMitigations && PerformanceTuning.CpuMitigationsExpanded)
            {
                if (target != null && target.EndsWith("\nFeatureSettingsOverride", StringComparison.OrdinalIgnoreCase))
                    return CpuMitigationPolicy.WantedOverride(original);
                if (target != null && target.EndsWith("\nFeatureSettingsOverrideMask", StringComparison.OrdinalIgnoreCase))
                    return CpuMitigationPolicy.WantedMask(original);
            }
            if (id == PerformanceActionCatalog.MultimediaProfile && MultimediaProfile.Owns(target))
                return MultimediaProfile.Wanted(target);
            return WantedRegistry(id, original);
        }

        internal static string[] EnvironmentTargets(string id)
        {
#if PAVISE_SELFTEST
            if (TargetsForTest == null) throw new InvalidOperationException("Unmocked performance target discovery");
            return TargetsForTest(id);
#endif
            switch (id)
            {
            case "EnvCpuMitigationsV1":
                return new string[2]
                {
                    RegistryTarget("SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management", "FeatureSettingsOverride"),
                    RegistryTarget("SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management", "FeatureSettingsOverrideMask")
                };
            case "EnvNtfsLastAccessV1":
                return new string[1] { RegistryTarget("SYSTEM\\CurrentControlSet\\Control\\FileSystem", "NtfsDisableLastAccessUpdate") };
            case "EnvQuantumProfileV1":
                return new string[1] { RegistryTarget("SYSTEM\\CurrentControlSet\\Control\\PriorityControl", "Win32PrioritySeparation") };
            case PerformanceActionCatalog.MultimediaProfile:
                return MultimediaProfile.Targets(KeyExists(MultimediaProfile.ProfilePath), KeyExists(MultimediaProfile.GamesPath));
            default:
            {
                List<string> list = new List<string>();
                if (id == "EnvAudioIdleV1")
                {
                    using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e96c-e325-11ce-bfc1-08002be10318}"))
                    {
                        if (registryKey != null)
                        {
                            string[] subKeyNames = registryKey.GetSubKeyNames();
                            foreach (string entryKey in subKeyNames)
                            {
                                string message = "SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e96c-e325-11ce-bfc1-08002be10318}\\" + entryKey + "\\PowerSettings";
                                using (RegistryKey registryKey2 = Registry.LocalMachine.OpenSubKey(message))
                                {
                                    if (registryKey2 != null && registryKey2.GetValue("PerformanceIdleTime") is byte[] && ((byte[])registryKey2.GetValue("PerformanceIdleTime")).Length == 4)
                                    {
                                        list.Add(RegistryTarget(message, "PerformanceIdleTime"));
                                    }
                                }
                            }
                        }
                    }
                }
                return list.ToArray();
            }
            }
        }

        private static bool KeyExists(string path)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path)) return key != null;
        }

        private static uint NvId(string id)
        {
            return 277041174u;
        }

        private static string ReadNv(string id, string target)
        {
            lock (NvDrsTweaks.SessionGate)
            {
                return ReadNvCore(id, target);
            }
        }

        private static string ReadNvCore(string id, string target)
        {
            if (!NvApi.Available)
            {
                throw new NotSupportedException("NVIDIA unavailable");
            }
            IntPtr session;
            if (!NvApi.TryOpenSession(out session))
            {
                throw new InvalidOperationException("NVIDIA session unavailable");
            }
            try
            {
                IntPtr profile;
                if (!NvApi.FindOrCreateAppProfile(session, Path.GetFileName(target), out profile))
                {
                    throw new InvalidOperationException("NVIDIA profile unavailable");
                }
                uint value;
                int dword = NvApi.TryGetDword(session, profile, NvId(id), out value);
                if (dword < 0)
                {
                    throw new InvalidOperationException("NVIDIA setting unavailable");
                }
                return (dword == 0) ? "absent" : value.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                NvApi.CloseSession(session);
            }
        }

        private static bool WriteNv(string id, string target, string value)
        {
            lock (NvDrsTweaks.SessionGate)
            {
                return WriteNvCore(id, target, value);
            }
        }

        private static bool WriteNvCore(string id, string target, string value)
        {
            IntPtr session;
            if (!NvApi.TryOpenSession(out session))
            {
                return false;
            }
            try
            {
                IntPtr profile;
                if (!NvApi.FindOrCreateAppProfile(session, Path.GetFileName(target), out profile))
                {
                    return false;
                }
                int status;
                return ((value == "absent") ? NvApi.DeleteSetting(session, profile, NvId(id)) : NvApi.SetDword(session, profile, NvId(id), uint.Parse(value, CultureInfo.InvariantCulture), out status)) && NvApi.SaveSession(session);
            }
            finally
            {
                NvApi.CloseSession(session);
            }
        }
    }
}
