using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class PerformanceActionBackend
	{
#if PAVISE_SELFTEST
		internal static Func<string> QuantumReadForTest;
		internal static Func<string, bool> QuantumWriteForTest;
#endif
		internal sealed class DefenderInactiveException : NotSupportedException
		{
			internal DefenderInactiveException()
				: base(PerformancePolicy.Text("Defender 未运行，旧记录暂待恢复", "Defender is not running; legacy records are awaiting restoration"))
			{
			}
		}

		private const string MitigationNativeSource = "\nusing System;\nusing System.Reflection;\nusing System.Runtime.InteropServices;\nusing System.IO;\nusing Microsoft.Win32;\npublic static class PaviseNativeMitigationRestore\n{\nprivate const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;\nprivate const string Prefix = \"Microsoft.Samples.PowerShell.Commands.Ntdll\";\nprivate static readonly string[] Fields = { \"ControlFlowGuard\", \"EnableExportAddressFilter\", \"EnableExportAddressFilterPlus\", \"EnableImportAddressFilter\", \"EnableRopCallerCheck\", \"EnableRopSimExec\" };\nprivate sealed class Group\n{\ninternal Type Type;\ninternal object Kind, Buffer;\ninternal MethodInfo Query, Set;\ninternal string Name;\n}\nprivate static Group ReadGroup(Assembly assembly, string target, string typeName, string kindName, uint flags)\n{\nType native = assembly.GetType(Prefix, true);\nType type = assembly.GetType(Prefix + \"+\" + typeName, true);\nType kindType = assembly.GetType(Prefix + \"+IMAGE_MITIGATION_POLICY\", true);\nvar group = new Group { Type = type, Kind = Enum.Parse(kindType, kindName), Buffer = Activator.CreateInstance(type), Name = kindName };\nforeach (MethodInfo method in native.GetMethods(Flags))\n{\nif (!method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1 || method.GetParameters().Length != 4) continue;\nif (method.Name == \"QueryImageMitigationPolicy\") group.Query = method.MakeGenericMethod(type);\nif (method.Name == \"SetImageMitigationPolicy\") group.Set = method.MakeGenericMethod(type);\n}\nif (group.Query == null || group.Set == null) throw new NotSupportedException(\"Unsupported image mitigation API\");\nFieldInfo modules = type.GetField(\"ModuleList\");\nif (modules != null)\n{\nvar marshal = (MarshalAsAttribute)Attribute.GetCustomAttribute(modules, typeof(MarshalAsAttribute));\nif (marshal == null || marshal.SizeConst != 512) throw new NotSupportedException(\"Unsupported mitigation module list\");\nmodules.SetValue(group.Buffer, new char[marshal.SizeConst]);\n}\nobject[] args = { target, group.Kind, flags, group.Buffer };\nint status = (int)group.Query.Invoke(null, args);\n// An absent application has no local overrides. Every other error must retain its receipt.\nif (status != 0 && status != unchecked((int)0xc0000034)) ThrowStatus(\"Query \" + kindName, status);\nif (status == 0) group.Buffer = args[3];\nreturn group;\n}\nprivate static Group ReadCfg(Assembly assembly, string target)\n{ return ReadGroup(assembly, target, \"RTL_IMAGE_MITIGATION_CONTROL_FLOW_GUARD_POLICY\", \"ImageControlFlowGuardPolicy\", 0); }\nprivate static Group ReadPayload(Assembly assembly, string target, uint flags)\n{ return ReadGroup(assembly, target, \"RTL_IMAGE_MITIGATION_PAYLOAD_RESTRICTION_POLICY\", \"ImagePayloadRestrictionPolicy\", flags); }\nprivate static void ThrowStatus(string operation, int status)\n{ throw new InvalidOperationException(operation + \" failed: 0x\" + unchecked((uint)status).ToString(\"X8\")); }\nprivate static ulong ReadField(Group group, string name)\n{\nFieldInfo field = group.Type.GetField(name);\nif (field == null) throw new NotSupportedException(\"Missing mitigation field \" + name);\nobject policy = field.GetValue(group.Buffer);\nFieldInfo state = policy.GetType().GetField(\"PolicyState\");\nif (state == null || state.FieldType != typeof(ulong)) throw new NotSupportedException(\"Unsupported mitigation state\");\nreturn (ulong)state.GetValue(policy);\n}\nprivate static bool ChangeField(Group group, string name, ulong bits, ulong mask)\n{\nulong oldValue = ReadField(group, name), wanted = (oldValue & ~mask) | bits;\nif (wanted == oldValue) return false;\nFieldInfo field = group.Type.GetField(name);\nobject policy = field.GetValue(group.Buffer);\npolicy.GetType().GetField(\"PolicyState\").SetValue(policy, wanted);\nfield.SetValue(group.Buffer, policy);\nreturn true;\n}\nprivate static void Commit(Group group, string target, bool changed)\n{\nif (!changed) return;\nint status = (int)group.Set.Invoke(null, new object[] { target, group.Kind, (uint)0, group.Buffer });\nif (status != 0) ThrowStatus(\"Set \" + group.Name, status);\n}\nprivate static bool Same(byte[] a, byte[] b)\n{\nif (a == null || b == null || a.Length != b.Length) return false;\nfor (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;\nreturn true;\n}\nprivate static void ValidateTarget(string target)\n{\nif (string.IsNullOrEmpty(target) || !Path.IsPathRooted(target) || !string.Equals(Path.GetFullPath(target), target, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(target).Length == 0)\nthrow new NotSupportedException(\"An absolute canonical image path is required\");\n}\nprivate static void WriteCfg(Assembly assembly, string target, ulong bits, byte mask)\n{\nif (!Path.IsPathRooted(target) || Path.GetFileName(target).Length == 0)\nthrow new NotSupportedException(\"An absolute image path is required\");\nGroup cfg = ReadCfg(assembly, target);\nif ((ReadField(cfg, \"ControlFlowGuard\") & mask) == bits) return;\nstring path = Path.GetFullPath(target);\nconst string root = \"SOFTWARE\\\\Microsoft\\\\Windows NT\\\\CurrentVersion\\\\Image File Execution Options\\\\\";\nusing (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))\nusing (RegistryKey image = machine.OpenSubKey(root + Path.GetFileName(path), false))\n{\nif (image == null || !(image.GetValue(\"UseFilter\") is int) || (int)image.GetValue(\"UseFilter\") != 1)\nthrow new NotSupportedException(\"No image path mitigation filter\");\nstring match = null;\nforeach (string child in image.GetSubKeyNames())\n{\nusing (RegistryKey filter = image.OpenSubKey(child, false))\n{\nstring fullPath = filter == null ? null : filter.GetValue(\"FilterFullPath\") as string;\nif (fullPath == null || !string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase)) continue;\nif (match != null) throw new NotSupportedException(\"Multiple image mitigation filters\");\nmatch = child;\n}\n}\nif (match == null) throw new NotSupportedException(\"No exact image path mitigation filter\");\nusing (RegistryKey filter = machine.OpenSubKey(root + Path.GetFileName(path) + \"\\\\\" + match, true))\n{\nif (filter == null || !string.Equals(filter.GetValue(\"FilterFullPath\") as string, path, StringComparison.OrdinalIgnoreCase))\nthrow new NotSupportedException(\"Image mitigation filter changed\");\nfor (int attempt = 0; attempt < 3; attempt++)\n{\nbyte[] current = filter.GetValue(\"MitigationOptions\") as byte[];\nif (current == null || (current.Length != 16 && current.Length != 24) || filter.GetValueKind(\"MitigationOptions\") != RegistryValueKind.Binary)\nthrow new NotSupportedException(\"Unsupported image mitigation options\");\nulong actual = ReadField(ReadCfg(assembly, target), \"ControlFlowGuard\");\nif ((ulong)(current[5] & 7) != (actual & 7)) throw new NotSupportedException(\"Image mitigation layout does not match the native query\");\nif (!Same(current, filter.GetValue(\"MitigationOptions\") as byte[])) continue;\nbyte[] wanted = (byte[])current.Clone();\n// CFG uses bits 40..42. Changing this field through RtlSet also forces StrictCFG off.\n// Restrict this write to the exact path filter and preserve every other persisted bit.\nwanted[5] = (byte)((wanted[5] & ~mask) | (int)bits);\nif (Same(current, wanted)) return;\nfilter.SetValue(\"MitigationOptions\", wanted, RegistryValueKind.Binary);\nif (!Same(wanted, filter.GetValue(\"MitigationOptions\") as byte[]))\nthrow new InvalidOperationException(\"Image mitigation write could not be verified\");\nreturn;\n}\nthrow new InvalidOperationException(\"Image mitigation options kept changing\");\n}\n}\n}\nprivate static ulong[] Validate(string[] states)\n{\nif (states == null || states.Length != 5 && states.Length != Fields.Length) throw new FormatException(\"Invalid mitigation snapshot\");\nvar parsed = new ulong[states.Length];\nfor (int i = 0; i < states.Length; i++)\n{\nstring[] pieces = (states[i] ?? \"\").Split(':'); bool force;\nif (pieces.Length != 2 || !bool.TryParse(pieces[1], out force) || (pieces[0] != \"ON\" && pieces[0] != \"OFF\" && pieces[0] != \"NOTSET\"))\nthrow new FormatException(\"Invalid mitigation state\");\nparsed[i] = (pieces[0] == \"ON\" ? 1UL : pieces[0] == \"OFF\" ? 2UL : 0UL) | (force ? 4UL : 0UL);\n}\nreturn parsed;\n}\npublic static void Restore(Assembly assembly, string target, string[] states)\n{\nValidateTarget(target);\nulong[] wanted = Validate(states);\nGroup payload = ReadPayload(assembly, target, 0);\nbool payloadChanged = false;\nfor (int i = 1; i < wanted.Length; i++) payloadChanged |= ChangeField(payload, Fields[i], wanted[i], 7);\nCommit(payload, target, payloadChanged);\nWriteCfg(assembly, target, wanted[0], 7);\n}\npublic static void Apply(Assembly assembly, string target, bool includeSimExec)\n{\nValidateTarget(target);\nGroup payload = ReadPayload(assembly, target, 0);\nbool payloadChanged = false;\nfor (int i = 1; i < (includeSimExec ? Fields.Length : Fields.Length - 1); i++) payloadChanged |= ChangeField(payload, Fields[i], 2, 3);\nCommit(payload, target, payloadChanged);\nWriteCfg(assembly, target, 2, 3);\n}\n}\n";

		internal static bool DefenderRunning()
		{
			return SvcState.Query("WinDefend") == 4;
		}

		private static bool DefenderAction(string id)
		{
			if (!(id == "AutoDefenderSessionV1"))
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

		internal static string[] MitigationNames(string target)
		{
			return MitigationNames(Is32Bit(target));
		}

		private static string[] MitigationNames(bool includeSimExec)
		{
			List<string> list = new List<string>();
			list.Add("CFG");
			list.Add("EnableExportAddressFilter");
			list.Add("EnableExportAddressFilterPlus");
			list.Add("EnableImportAddressFilter");
			list.Add("EnableRopCallerCheck");
			List<string> list2 = list;
			if (includeSimExec)
			{
				list2.Add("EnableRopSimExec");
			}
			return list2.ToArray();
		}

		internal static bool Is32Bit(string target)
		{
			try
			{
				using (FileStream fileStream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					using (BinaryReader binaryReader = new BinaryReader(fileStream))
					{
						if (fileStream.Length < 64 || binaryReader.ReadUInt16() != 23117)
						{
							return false;
						}
						fileStream.Position = 60L;
						uint positionValue = binaryReader.ReadUInt32();
						if (positionValue > fileStream.Length - 26)
						{
							return false;
						}
						fileStream.Position = positionValue;
						if (binaryReader.ReadUInt32() != 17744)
						{
							return false;
						}
						ushort uInt16 = binaryReader.ReadUInt16();
						fileStream.Position = positionValue + 24;
						return uInt16 == 332 && binaryReader.ReadUInt16() == 267;
					}
				}
			}
			catch
			{
				return false;
			}
		}

		internal static bool MitigationsOff(string value)
		{
			return MitigationsOff(value, true);
		}

		internal static bool MitigationsOff(string value, bool includeSimExec)
		{
			string[] parts = (value ?? "").Split(';');
			if (parts.Length != 5 && parts.Length != 6)
			{
				return false;
			}
			for (int i = 0; i < (includeSimExec ? parts.Length : 5); i++)
			{
				if (!parts[i].StartsWith("OFF:", StringComparison.Ordinal))
				{
					return false;
				}
			}
			return true;
		}

		internal static string MitigationQuery(string target)
		{
			StringBuilder stringBuilder = new StringBuilder("Import-Module ProcessMitigations; $q=Get-ProcessMitigation -Name " + Quote(target) + "; $r=@(); ");
			string[] mitigationNamesResult = MitigationNames(true);
			foreach (string entryText in mitigationNamesResult)
			{
				string message = ((entryText == "CFG") ? "CFG" : "Payload");
				string message2 = ((entryText == "CFG") ? "Enable" : entryText);
				string entryTextOffset = ((entryText == "CFG") ? "OverrideCFG" : ("Override" + entryText));
				stringBuilder.Append("if($null-eq$q){$r+='NOTSET:False'}else{$r+=($q." + message + "." + message2 + ".ToString()+':'+$q." + message + "." + entryTextOffset + ".ToString())}; ");
			}
			return stringBuilder.Append("Write-Output ($r -join ';')").ToString();
		}

		internal static string MitigationWrite(string target, string value)
		{
			return MitigationNativeScript(target, value);
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
			switch (id)
			{
			case "MatchVisualEffectsV1":
				return MatchVisualEffects.Read(target);
			case "AutoGameMitigationsV1":
				return Script(MitigationQuery(target));
			case "AutoDefenderSessionV1":
				return Script("$p=Get-MpPreference; Write-Output ($p.DisableRealtimeMonitoring.ToString()+','+$p.DisableBehaviorMonitoring.ToString())");
			case "AutoDefenderResourcesV1":
				return Script("$p=Get-MpPreference; if(@($p.ExclusionPath)-contains " + Quote(target) + "){Write-Output '1'}else{Write-Output '0'}");
			case "AutoNtfsShortNamesV1":
				return Script("$v=& $env:SystemRoot\\System32\\fsutil.exe 8dot3name query " + Quote(target) + "; if($LASTEXITCODE-ne0){throw 'Volume query failed'}; $m=[regex]::Match(($v -join \"`n\"),'[：:]\\s*([01])\\s*(?:\\(|$)'); if(!$m.Success){throw 'Unknown volume state'}; Write-Output $m.Groups[1].Value");
			case "AutoBackgroundFpsV1":
			case "AutoOpenGlThreadsV1":
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
				written = QuantumWriteForTest != null ? QuantumWriteForTest(value) : WriteRegistry(target, value);
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
			switch (id)
			{
			case "MatchVisualEffectsV1":
				return MatchVisualEffects.Write(target, value);
			case "AutoGameMitigationsV1":
				Script(MitigationWrite(target, value));
				return true;
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
			case "AutoOpenGlThreadsV1":
				return WriteNv(id, target, value);
			default:
				return WriteRegistry(target, value);
			}
		}

		internal static bool Apply(string id, string target, string wanted)
		{
			if (id == "AutoOpenGlThreadsV1" || id == "AutoBackgroundFpsV1")
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
				// Retired: retain the receipt restoration path, but never apply new changes.
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
			Func<PerformanceActionStore.Receipt, string, PerformanceActionStore.Receipt> reconcile = null;
			if (id == "AutoGameMitigationsV1" && (wanted == "off32" || wanted == "off64"))
			{
				reconcile = (PerformanceActionStore.Receipt receipt, string current) => ReconcileMitigations(receipt, current, wanted);
			}
			return PerformanceActionStore.Change(id, target, wanted, () => Read(id, target), (string value) => Write(id, target, value), delegate(string value)
			{
				uint result;
				if (id == "AutoBackgroundFpsV1" && uint.TryParse(value, out result) && result != 0 && result <= 30)
				{
					return true;
				}
				return (!(id == "AutoGameMitigationsV1")) ? (value == wanted) : MitigationsOff(value, wanted == "off32");
			}, reconcile);
		}

		internal static string ApplyFailureHint(string id, string target)
		{
			string lastChangeFailure = PerformanceActionStore.LastChangeFailure;
			switch (lastChangeFailure)
			{
			case "Recovery record ownership does not match the target":
				return PerformancePolicy.Text("恢复记录与目标不一致，未写入", lastChangeFailure);
			case "Recovery snapshot cannot be reconciled":
				return PerformancePolicy.Text("恢复记录格式无法核对，已保留记录", lastChangeFailure);
			case "Configuration kept changing during reconciliation":
				return PerformancePolicy.Text("配置持续被外部修改，本次未写入", lastChangeFailure);
			case "Could not save the reconciled recovery record":
			case "Could not save the recovery record":
				return PerformancePolicy.Text("原配置记录保存失败，本次未写入", lastChangeFailure);
			case "Could not commit the recovery record":
				return PerformancePolicy.Text("写入后的恢复记录保存失败，已保留记录", lastChangeFailure);
			case "Configuration write did not complete":
				return PerformancePolicy.Text("配置写入未完成，已保留恢复记录", lastChangeFailure);
			case "Configuration did not match the requested state after writing":
				return PerformancePolicy.Text("配置写入后复核不一致，已保留恢复记录", lastChangeFailure);
			case "Legacy recovery record must be reconciled before enabling SimExec":
				return PerformancePolicy.Text("旧恢复记录需先核对第六项，未写入", lastChangeFailure);
			case "Configuration changed externally; recovery record retained":
				return PerformancePolicy.Text("配置已被外部修改，已保留恢复记录", lastChangeFailure);
			case "Configuration changed externally; previous ownership released":
				return PerformancePolicy.Text("配置已被外部修改，已解除旧配置托管", lastChangeFailure);
			case "Configuration changed externally; recovery record could not be cleared":
				return PerformancePolicy.Text("配置已被外部修改，旧恢复记录清理未完成", lastChangeFailure);
			default:
				return lastChangeFailure ?? PerformancePolicy.Text("写入或复核未完成，已保留恢复记录", "Write or verification incomplete; recovery record retained");
			}
		}

		private static bool MitigationSnapshot(string value, out string[] parts)
		{
			parts = (value ?? "").Split(';');
			if (parts.Length != 5 && parts.Length != 6)
			{
				return false;
			}
			string[] partsSnapshot = parts;
			foreach (string entryText in partsSnapshot)
			{
				string[] entryTextParts = entryText.Split(':');
				bool result;
				if (entryTextParts.Length != 2 || !bool.TryParse(entryTextParts[1], out result) || (entryTextParts[0] != "ON" && entryTextParts[0] != "OFF" && entryTextParts[0] != "NOTSET"))
				{
					return false;
				}
			}
			return true;
		}

		private static PerformanceActionStore.Receipt ReconcileMitigations(PerformanceActionStore.Receipt receipt, string current, string wanted)
		{
			string[] parts;
			string[] parts2;
			if (!MitigationSnapshot(receipt.Original, out parts) || !MitigationSnapshot(current, out parts2) || parts2.Length != 6)
			{
				return null;
			}
			string[] parts3;
			bool mitigationSnapshotSucceeded = MitigationSnapshot(receipt.Expected, out parts3);
			if (receipt.Committed && (!mitigationSnapshotSucceeded || parts3.Length < parts.Length))
			{
				return null;
			}
			if (!receipt.Committed && !mitigationSnapshotSucceeded && receipt.Expected != "off32" && receipt.Expected != "off64")
			{
				return null;
			}
			if (mitigationSnapshotSucceeded && parts3.Length < parts.Length)
			{
				return null;
			}
			string[] cloneResult = (string[])parts2.Clone();
			for (int i = 0; i < parts.Length; i++)
			{
				bool startsWith;
				if (!mitigationSnapshotSucceeded)
				{
					if (i >= 5 && !(receipt.Expected == "off32"))
					{
						continue;
					}
					startsWith = parts2[i].StartsWith("OFF:", StringComparison.Ordinal);
				}
				else
				{
					startsWith = parts2[i] == parts3[i];
				}
				if (startsWith)
				{
					cloneResult[i] = parts[i];
				}
			}
			string[] cloneResult2 = (string[])parts2.Clone();
			for (int j = 0; j < ((wanted == "off32") ? 6 : 5); j++)
			{
				cloneResult2[j] = "OFF:" + parts2[j].Split(':')[1];
			}
			PerformanceActionStore.Receipt receipt2 = new PerformanceActionStore.Receipt();
			receipt2.Id = receipt.Id;
			receipt2.Target = receipt.Target;
			receipt2.Original = string.Join(";", cloneResult);
			receipt2.Expected = string.Join(";", cloneResult2);
			receipt2.Committed = false;
			return receipt2;
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
					return (r.Original == "1") ? ((!(current == "1")) ? null : current) : "0";
				});
			}
			if (r.Id == "AutoGameMitigationsV1" || r.Id == "AutoDefenderSessionV1")
			{
				char separator = ((r.Id == "AutoGameMitigationsV1") ? ';' : ',');
				string[] original = r.Original.Split(separator);
				string[] expected = (r.Committed ? r.Expected.Split(separator) : null);
				string[] parts;
				if (!r.Committed && r.Id == "AutoGameMitigationsV1" && MitigationSnapshot(r.Expected, out parts))
				{
					expected = parts;
				}
				return PerformanceActionStore.RestoreComponents(r, () => Read(r.Id, r.Target), (string value) => Write(r.Id, r.Target, value), delegate(string current)
				{
					string[] currentParts = current.Split(separator);
					bool allConditionsMet = r.Id == "AutoGameMitigationsV1" && original.Length == 5 && currentParts.Length == 6;
					if (currentParts.Length != original.Length && !allConditionsMet)
					{
						return (string)null;
					}
					if (expected != null && expected.Length != currentParts.Length && (!allConditionsMet || expected.Length != 5))
					{
						return (string)null;
					}
					string[] cloneResult = (string[])currentParts.Clone();
					for (int i = 0; i < original.Length; i++)
					{
						bool startsWith;
						if (expected == null)
						{
							if (!(r.Id == "AutoDefenderSessionV1"))
							{
								if (i >= 5 && !(r.Expected == "off32"))
								{
									continue;
								}
								startsWith = currentParts[i].StartsWith("OFF:", StringComparison.Ordinal);
							}
							else
							{
								startsWith = currentParts[i] == "True";
							}
						}
						else
						{
							startsWith = currentParts[i] == expected[i];
						}
						if (startsWith)
						{
							cloneResult[i] = original[i];
						}
					}
					return string.Join(separator.ToString(), cloneResult);
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
			if ((r.Id == "AutoBackgroundFpsV1" || r.Id == "AutoOpenGlThreadsV1") && !NvApi.Available)
			{
				return PerformancePolicy.Text("显卡驱动接口不可用，已保留恢复记录", "Graphics driver interface unavailable; recovery record retained");
			}
			return PerformancePolicy.Text("写入或复核未完成，已保留恢复记录", "Write or verification incomplete; recovery record retained");
		}

		internal static string RegistryTarget(string path, string name)
		{
			return path + "\n" + name;
		}

		internal static string ReadRegistry(string target)
		{
			string[] targetParts = target.Split('\n');
			if (targetParts.Length != 2 || !targetParts[0].StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase))
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
				switch (registryKey.GetValueKind(targetParts[1]))
				{
				case RegistryValueKind.DWord:
					return "d:" + ((int)value).ToString(CultureInfo.InvariantCulture);
				case RegistryValueKind.Binary:
					return "b:" + Convert.ToBase64String((byte[])value);
				default:
					throw new InvalidOperationException("Unexpected registry value type");
				}
			}
		}

		private static bool WriteRegistry(string target, string value)
		{
			string[] targetParts = target.Split('\n');
			if (targetParts.Length != 2 || !targetParts[0].StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase))
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
			return WantedRegistry(id, original);
		}

		internal static string[] EnvironmentTargets(string id)
		{
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
				else
				{
					bool matchesId = id == "EnvStorageIdleV1";
					string message2 = "SYSTEM\\CurrentControlSet\\Enum\\" + (matchesId ? "SCSI" : "PCI");
					string b = (matchesId ? "{4d36e967-e325-11ce-bfc1-08002be10318}" : "{4d36e97b-e325-11ce-bfc1-08002be10318}");
					string name = (matchesId ? "DisableIdlePowerManagement" : ((id == "EnvStorageRuntimeV1") ? "DisableRuntimePowerManagement" : "DisableD3Cold"));
					using (RegistryKey registryKey3 = Registry.LocalMachine.OpenSubKey(message2))
					{
						if (registryKey3 != null)
						{
							string[] subKeyNames2 = registryKey3.GetSubKeyNames();
							foreach (string entryKey2 in subKeyNames2)
							{
								using (RegistryKey registryKey4 = registryKey3.OpenSubKey(entryKey2))
								{
									if (registryKey4 == null)
									{
										continue;
									}
									string[] subKeyNames3 = registryKey4.GetSubKeyNames();
									foreach (string entryKey3 in subKeyNames3)
									{
										using (RegistryKey registryKey5 = registryKey4.OpenSubKey(entryKey3))
										{
											if (registryKey5 == null || !string.Equals(registryKey5.GetValue("ClassGUID") as string, b, StringComparison.OrdinalIgnoreCase))
											{
												continue;
											}
											using (RegistryKey registryKey6 = registryKey5.OpenSubKey("Device Parameters\\StorPort"))
											{
												if (registryKey6 != null)
												{
													list.Add(RegistryTarget(message2 + "\\" + entryKey2 + "\\" + entryKey3 + "\\Device Parameters\\StorPort", name));
												}
											}
										}
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

		internal static string ShortNameVolume(string exe)
		{
			string pathRoot = Path.GetPathRoot(exe);
			DriveInfo driveInfo = new DriveInfo(pathRoot);
			using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\FileSystem"))
			{
				object value = ((registryKey == null) ? null : registryKey.GetValue("NtfsDisable8dot3NameCreation"));
				if (!(value is int) || (int)value != 2 || driveInfo.DriveType != DriveType.Fixed || driveInfo.DriveFormat != "NTFS")
				{
					return null;
				}
			}
			return pathRoot.TrimEnd('\\');
		}

		private static uint NvId(string id)
		{
			if (!(id == "AutoOpenGlThreadsV1"))
			{
				return 277041174u;
			}
			return 549528094u;
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

		private static string MitigationNativeScript(string target, string value)
		{
			bool anyConditionMet = value == "off32" || value == "off64";
			string[] parts = (anyConditionMet ? null : (value ?? "").Split(';'));
			if (!anyConditionMet)
			{
				if (parts.Length != 5 && parts.Length != 6)
				{
					throw new FormatException("Invalid mitigation snapshot");
				}
				string[] textEntries = parts;
				foreach (string entryText in textEntries)
				{
					string[] entryTextParts = entryText.Split(':');
					bool result;
					if (entryTextParts.Length != 2 || !bool.TryParse(entryTextParts[1], out result) || (entryTextParts[0] != "ON" && entryTextParts[0] != "OFF" && entryTextParts[0] != "NOTSET"))
					{
						throw new FormatException("Invalid mitigation state");
					}
				}
			}
			StringBuilder stringBuilder = new StringBuilder("Import-Module ProcessMitigations -ErrorAction Stop -WarningAction Stop; if($null-eq('PaviseNativeMitigationRestore'-as[type])){Add-Type -TypeDefinition @'\n");
			stringBuilder.Append("\nusing System;\nusing System.Reflection;\nusing System.Runtime.InteropServices;\nusing System.IO;\nusing Microsoft.Win32;\npublic static class PaviseNativeMitigationRestore\n{\nprivate const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;\nprivate const string Prefix = \"Microsoft.Samples.PowerShell.Commands.Ntdll\";\nprivate static readonly string[] Fields = { \"ControlFlowGuard\", \"EnableExportAddressFilter\", \"EnableExportAddressFilterPlus\", \"EnableImportAddressFilter\", \"EnableRopCallerCheck\", \"EnableRopSimExec\" };\nprivate sealed class Group\n{\ninternal Type Type;\ninternal object Kind, Buffer;\ninternal MethodInfo Query, Set;\ninternal string Name;\n}\nprivate static Group ReadGroup(Assembly assembly, string target, string typeName, string kindName, uint flags)\n{\nType native = assembly.GetType(Prefix, true);\nType type = assembly.GetType(Prefix + \"+\" + typeName, true);\nType kindType = assembly.GetType(Prefix + \"+IMAGE_MITIGATION_POLICY\", true);\nvar group = new Group { Type = type, Kind = Enum.Parse(kindType, kindName), Buffer = Activator.CreateInstance(type), Name = kindName };\nforeach (MethodInfo method in native.GetMethods(Flags))\n{\nif (!method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1 || method.GetParameters().Length != 4) continue;\nif (method.Name == \"QueryImageMitigationPolicy\") group.Query = method.MakeGenericMethod(type);\nif (method.Name == \"SetImageMitigationPolicy\") group.Set = method.MakeGenericMethod(type);\n}\nif (group.Query == null || group.Set == null) throw new NotSupportedException(\"Unsupported image mitigation API\");\nFieldInfo modules = type.GetField(\"ModuleList\");\nif (modules != null)\n{\nvar marshal = (MarshalAsAttribute)Attribute.GetCustomAttribute(modules, typeof(MarshalAsAttribute));\nif (marshal == null || marshal.SizeConst != 512) throw new NotSupportedException(\"Unsupported mitigation module list\");\nmodules.SetValue(group.Buffer, new char[marshal.SizeConst]);\n}\nobject[] args = { target, group.Kind, flags, group.Buffer };\nint status = (int)group.Query.Invoke(null, args);\n// An absent application has no local overrides. Every other error must retain its receipt.\nif (status != 0 && status != unchecked((int)0xc0000034)) ThrowStatus(\"Query \" + kindName, status);\nif (status == 0) group.Buffer = args[3];\nreturn group;\n}\nprivate static Group ReadCfg(Assembly assembly, string target)\n{ return ReadGroup(assembly, target, \"RTL_IMAGE_MITIGATION_CONTROL_FLOW_GUARD_POLICY\", \"ImageControlFlowGuardPolicy\", 0); }\nprivate static Group ReadPayload(Assembly assembly, string target, uint flags)\n{ return ReadGroup(assembly, target, \"RTL_IMAGE_MITIGATION_PAYLOAD_RESTRICTION_POLICY\", \"ImagePayloadRestrictionPolicy\", flags); }\nprivate static void ThrowStatus(string operation, int status)\n{ throw new InvalidOperationException(operation + \" failed: 0x\" + unchecked((uint)status).ToString(\"X8\")); }\nprivate static ulong ReadField(Group group, string name)\n{\nFieldInfo field = group.Type.GetField(name);\nif (field == null) throw new NotSupportedException(\"Missing mitigation field \" + name);\nobject policy = field.GetValue(group.Buffer);\nFieldInfo state = policy.GetType().GetField(\"PolicyState\");\nif (state == null || state.FieldType != typeof(ulong)) throw new NotSupportedException(\"Unsupported mitigation state\");\nreturn (ulong)state.GetValue(policy);\n}\nprivate static bool ChangeField(Group group, string name, ulong bits, ulong mask)\n{\nulong oldValue = ReadField(group, name), wanted = (oldValue & ~mask) | bits;\nif (wanted == oldValue) return false;\nFieldInfo field = group.Type.GetField(name);\nobject policy = field.GetValue(group.Buffer);\npolicy.GetType().GetField(\"PolicyState\").SetValue(policy, wanted);\nfield.SetValue(group.Buffer, policy);\nreturn true;\n}\nprivate static void Commit(Group group, string target, bool changed)\n{\nif (!changed) return;\nint status = (int)group.Set.Invoke(null, new object[] { target, group.Kind, (uint)0, group.Buffer });\nif (status != 0) ThrowStatus(\"Set \" + group.Name, status);\n}\nprivate static bool Same(byte[] a, byte[] b)\n{\nif (a == null || b == null || a.Length != b.Length) return false;\nfor (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;\nreturn true;\n}\nprivate static void ValidateTarget(string target)\n{\nif (string.IsNullOrEmpty(target) || !Path.IsPathRooted(target) || !string.Equals(Path.GetFullPath(target), target, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(target).Length == 0)\nthrow new NotSupportedException(\"An absolute canonical image path is required\");\n}\nprivate static void WriteCfg(Assembly assembly, string target, ulong bits, byte mask)\n{\nif (!Path.IsPathRooted(target) || Path.GetFileName(target).Length == 0)\nthrow new NotSupportedException(\"An absolute image path is required\");\nGroup cfg = ReadCfg(assembly, target);\nif ((ReadField(cfg, \"ControlFlowGuard\") & mask) == bits) return;\nstring path = Path.GetFullPath(target);\nconst string root = \"SOFTWARE\\\\Microsoft\\\\Windows NT\\\\CurrentVersion\\\\Image File Execution Options\\\\\";\nusing (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))\nusing (RegistryKey image = machine.OpenSubKey(root + Path.GetFileName(path), false))\n{\nif (image == null || !(image.GetValue(\"UseFilter\") is int) || (int)image.GetValue(\"UseFilter\") != 1)\nthrow new NotSupportedException(\"No image path mitigation filter\");\nstring match = null;\nforeach (string child in image.GetSubKeyNames())\n{\nusing (RegistryKey filter = image.OpenSubKey(child, false))\n{\nstring fullPath = filter == null ? null : filter.GetValue(\"FilterFullPath\") as string;\nif (fullPath == null || !string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase)) continue;\nif (match != null) throw new NotSupportedException(\"Multiple image mitigation filters\");\nmatch = child;\n}\n}\nif (match == null) throw new NotSupportedException(\"No exact image path mitigation filter\");\nusing (RegistryKey filter = machine.OpenSubKey(root + Path.GetFileName(path) + \"\\\\\" + match, true))\n{\nif (filter == null || !string.Equals(filter.GetValue(\"FilterFullPath\") as string, path, StringComparison.OrdinalIgnoreCase))\nthrow new NotSupportedException(\"Image mitigation filter changed\");\nfor (int attempt = 0; attempt < 3; attempt++)\n{\nbyte[] current = filter.GetValue(\"MitigationOptions\") as byte[];\nif (current == null || (current.Length != 16 && current.Length != 24) || filter.GetValueKind(\"MitigationOptions\") != RegistryValueKind.Binary)\nthrow new NotSupportedException(\"Unsupported image mitigation options\");\nulong actual = ReadField(ReadCfg(assembly, target), \"ControlFlowGuard\");\nif ((ulong)(current[5] & 7) != (actual & 7)) throw new NotSupportedException(\"Image mitigation layout does not match the native query\");\nif (!Same(current, filter.GetValue(\"MitigationOptions\") as byte[])) continue;\nbyte[] wanted = (byte[])current.Clone();\n// CFG uses bits 40..42. Changing this field through RtlSet also forces StrictCFG off.\n// Restrict this write to the exact path filter and preserve every other persisted bit.\nwanted[5] = (byte)((wanted[5] & ~mask) | (int)bits);\nif (Same(current, wanted)) return;\nfilter.SetValue(\"MitigationOptions\", wanted, RegistryValueKind.Binary);\nif (!Same(wanted, filter.GetValue(\"MitigationOptions\") as byte[]))\nthrow new InvalidOperationException(\"Image mitigation write could not be verified\");\nreturn;\n}\nthrow new InvalidOperationException(\"Image mitigation options kept changing\");\n}\n}\n}\nprivate static ulong[] Validate(string[] states)\n{\nif (states == null || states.Length != 5 && states.Length != Fields.Length) throw new FormatException(\"Invalid mitigation snapshot\");\nvar parsed = new ulong[states.Length];\nfor (int i = 0; i < states.Length; i++)\n{\nstring[] pieces = (states[i] ?? \"\").Split(':'); bool force;\nif (pieces.Length != 2 || !bool.TryParse(pieces[1], out force) || (pieces[0] != \"ON\" && pieces[0] != \"OFF\" && pieces[0] != \"NOTSET\"))\nthrow new FormatException(\"Invalid mitigation state\");\nparsed[i] = (pieces[0] == \"ON\" ? 1UL : pieces[0] == \"OFF\" ? 2UL : 0UL) | (force ? 4UL : 0UL);\n}\nreturn parsed;\n}\npublic static void Restore(Assembly assembly, string target, string[] states)\n{\nValidateTarget(target);\nulong[] wanted = Validate(states);\nGroup payload = ReadPayload(assembly, target, 0);\nbool payloadChanged = false;\nfor (int i = 1; i < wanted.Length; i++) payloadChanged |= ChangeField(payload, Fields[i], wanted[i], 7);\nCommit(payload, target, payloadChanged);\nWriteCfg(assembly, target, wanted[0], 7);\n}\npublic static void Apply(Assembly assembly, string target, bool includeSimExec)\n{\nValidateTarget(target);\nGroup payload = ReadPayload(assembly, target, 0);\nbool payloadChanged = false;\nfor (int i = 1; i < (includeSimExec ? Fields.Length : Fields.Length - 1); i++) payloadChanged |= ChangeField(payload, Fields[i], 2, 3);\nCommit(payload, target, payloadChanged);\nWriteCfg(assembly, target, 2, 3);\n}\n}\n").Append("\n'@ -ErrorAction Stop\n}; $a=(Get-Command Set-ProcessMitigation -ErrorAction Stop).ImplementingType.Assembly; [PaviseNativeMitigationRestore]::");
			if (anyConditionMet)
			{
				stringBuilder.Append("Apply($a, ").Append(Quote(target)).Append((value == "off32") ? ", $true)" : ", $false)");
			}
			else
			{
				stringBuilder.Append("Restore($a, ").Append(Quote(target)).Append(", [string[]]@(");
				for (int j = 0; j < parts.Length; j++)
				{
					if (j != 0)
					{
						stringBuilder.Append(',');
					}
					stringBuilder.Append(Quote(parts[j]));
				}
				stringBuilder.Append("))");
			}
			if (stringBuilder.Length > 11500)
			{
				throw new NotSupportedException("Image mitigation command is too long");
			}
			return stringBuilder.ToString();
		}
	}
}
