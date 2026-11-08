using System;
using Microsoft.Win32;

namespace PaviseApp
{
	internal sealed class ReversibleReg
	{
		public const string Absent = "__pavise_absent__";

		private const char AppliedSep = '\u001f';

		private readonly RegistryKey hive;

		private readonly string subKey;

		private readonly string valName;

		private readonly RegistryValueKind kind;

		private readonly string slot;

		public bool HasBackup
		{
			get
			{
				return Settings.LoadStr(slot, "").Length > 0;
			}
		}

		public ReversibleReg(RegistryKey hive, string subKey, string valName, RegistryValueKind kind, string slot)
		{
			this.hive = hive;
			this.subKey = subKey;
			this.valName = valName;
			this.kind = kind;
			this.slot = slot;
		}

		public bool Apply(object newVal)
		{
			try
			{
				using (RegistryKey registryKey = hive.CreateSubKey(subKey))
				{
					if (registryKey == null)
					{
						return false;
					}
					string originalPartResult = OriginalPart(Settings.LoadStr(slot, ""));
					if (originalPartResult.Length == 0)
					{
						object value = registryKey.GetValue(valName);
						if (value != null)
						{
							RegistryValueKind registryValueKind = RegistryValueKind.Unknown;
							try
							{
								registryValueKind = registryKey.GetValueKind(valName);
							}
							catch
							{
							}
							if (registryValueKind != kind)
							{
								Logger.Warn(string.Concat(valName, Lang.T("log.reversiblereg.1"), registryValueKind, Lang.T("log.reversiblereg.2")));
								return false;
							}
							if (SameByKind(value, newVal))
							{
								return true;
							}
						}
						originalPartResult = Encode(value);
						Settings.SaveStr(slot, originalPartResult);
						if (Settings.LoadStr(slot, "") != originalPartResult)
						{
							Logger.Log(Lang.T("log.reversiblereg.3") + valName + Lang.T("log.reversiblereg.4"));
							return false;
						}
					}
					registryKey.SetValue(valName, newVal, kind);
					object value2 = registryKey.GetValue(valName);
					if (value2 == null)
					{
						return false;
					}
					bool sameByKindSucceeded = SameByKind(value2, newVal);
					if (sameByKindSucceeded)
					{
						Settings.SaveStr(slot, originalPartResult + '\u001f' + Encode(newVal));
					}
					return sameByKindSucceeded;
				}
			}
			catch
			{
				return false;
			}
		}

		private string Encode(object value)
		{
			if (value == null)
			{
				return "__pavise_absent__";
			}
			if (kind == RegistryValueKind.Binary)
			{
				return "b" + Convert.ToBase64String((byte[])value);
			}
			return "=" + value;
		}

		private static string OriginalPart(string stored)
		{
			int matchIndex = stored.LastIndexOf('\u001f');
			if (matchIndex >= 0)
			{
				return stored.Substring(0, matchIndex);
			}
			return stored;
		}

		private static string AppliedPart(string stored)
		{
			int matchIndex = stored.LastIndexOf('\u001f');
			if (matchIndex >= 0)
			{
				return stored.Substring(matchIndex + 1);
			}
			return "";
		}

		private bool TryDecode(string repr, out object value)
		{
			value = null;
			try
			{
				if (repr.Length == 0 || repr == "__pavise_absent__")
				{
					return false;
				}
				if (kind == RegistryValueKind.Binary)
				{
					if (repr[0] != 'b')
					{
						return false;
					}
					value = Convert.FromBase64String(repr.Substring(1));
					return true;
				}
				string substringResult = ((repr[0] == '=') ? repr.Substring(1) : repr);
				if (kind == RegistryValueKind.DWord)
				{
					long result;
					if (!long.TryParse(substringResult, out result))
					{
						return false;
					}
					value = (int)result;
					return true;
				}
				value = substringResult;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private bool SameByKind(object a, object b)
		{
			try
			{
				if (kind == RegistryValueKind.DWord)
				{
					return Convert.ToInt64(a) == Convert.ToInt64(b);
				}
				if (kind == RegistryValueKind.Binary)
				{
					return BytesEqual(a as byte[], b as byte[]);
				}
				return string.Equals(a.ToString(), (b == null) ? "" : b.ToString(), StringComparison.Ordinal);
			}
			catch
			{
				return false;
			}
		}

		private static bool BytesEqual(byte[] a, byte[] b)
		{
			if (a == null || b == null)
			{
				return a == b;
			}
			if (a.Length != b.Length)
			{
				return false;
			}
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i])
				{
					return false;
				}
			}
			return true;
		}

		public bool Matches(object expected)
		{
			try
			{
				using (RegistryKey registryKey = hive.OpenSubKey(subKey))
				{
					object value = ((registryKey == null) ? null : registryKey.GetValue(valName));
					if (value == null)
					{
						return false;
					}
					if (kind == RegistryValueKind.DWord)
					{
						return Convert.ToInt64(value) == Convert.ToInt64(expected);
					}
					if (kind == RegistryValueKind.Binary)
					{
						return BytesEqual(value as byte[], expected as byte[]);
					}
					return string.Equals(value.ToString(), (expected == null) ? "" : expected.ToString(), StringComparison.Ordinal);
				}
			}
			catch
			{
				return false;
			}
		}

		public bool Restore()
		{
			string value4 = Settings.LoadStr(slot, "");
			if (value4.Length == 0)
			{
				return true;
			}
			string originalPartResult = OriginalPart(value4);
			string appliedPartResult = AppliedPart(value4);
			bool matchesOriginalPartResult = originalPartResult == "__pavise_absent__";
			object fromBase64StringResult = null;
			if (!matchesOriginalPartResult)
			{
				if (kind == RegistryValueKind.Binary)
				{
					if (originalPartResult.Length < 1 || originalPartResult[0] != 'b')
					{
						Settings.SaveStr(slot, "");
						Logger.Log(Lang.T("log.reversiblereg.5") + valName + Lang.T("log.reversiblereg.6"));
						return false;
					}
					try
					{
						fromBase64StringResult = Convert.FromBase64String(originalPartResult.Substring(1));
					}
					catch
					{
						Settings.SaveStr(slot, "");
						Logger.Log(Lang.T("log.reversiblereg.5") + valName + Lang.T("log.reversiblereg.7"));
						return false;
					}
				}
				else
				{
					string substringResult = ((originalPartResult[0] == '=') ? originalPartResult.Substring(1) : originalPartResult);
					if (kind == RegistryValueKind.DWord)
					{
						long result;
						if (!long.TryParse(substringResult, out result))
						{
							Settings.SaveStr(slot, "");
							Logger.Log(Lang.T("log.reversiblereg.5") + valName + Lang.T("log.reversiblereg.8") + substringResult + "\" ");
							return false;
						}
						fromBase64StringResult = (int)result;
					}
					else
					{
						fromBase64StringResult = substringResult;
					}
				}
			}
			try
			{
				bool restoreReady = false;
				using (RegistryKey registryKey = hive.OpenSubKey(subKey, true))
				{
					object value;
					if (registryKey != null && appliedPartResult.Length > 0 && TryDecode(appliedPartResult, out value))
					{
						object value2 = registryKey.GetValue(valName);
						if (value2 == null || !SameByKind(value2, value))
						{
							Settings.SaveStr(slot, "");
							Logger.Log(valName + Lang.T("log.reversiblereg.9"));
							return Settings.LoadStr(slot, "").Length == 0;
						}
					}
					if (registryKey == null)
					{
						restoreReady = true;
					}
					else if (matchesOriginalPartResult)
					{
						if (registryKey.GetValue(valName) != null)
						{
							registryKey.DeleteValue(valName, false);
						}
						restoreReady = registryKey.GetValue(valName) == null;
					}
					else
					{
						registryKey.SetValue(valName, fromBase64StringResult, kind);
						object value3 = registryKey.GetValue(valName);
						RegistryValueKind registryValueKind = RegistryValueKind.Unknown;
						try
						{
							registryValueKind = registryKey.GetValueKind(valName);
						}
						catch
						{
						}
						restoreReady = value3 != null && registryValueKind == kind;
						if (restoreReady && kind == RegistryValueKind.DWord)
						{
							restoreReady = Convert.ToInt64(value3) == Convert.ToInt64(fromBase64StringResult);
						}
						else if (restoreReady && kind == RegistryValueKind.Binary)
						{
							restoreReady = BytesEqual((byte[])value3, (byte[])fromBase64StringResult);
						}
						else if (restoreReady)
						{
							restoreReady = string.Equals(value3.ToString(), (fromBase64StringResult == null) ? "" : fromBase64StringResult.ToString(), StringComparison.Ordinal);
						}
					}
				}
				if (!restoreReady)
				{
					Logger.Log(Lang.T("log.suppressioncore.1") + valName + Lang.T("log.reversiblereg.10"));
					return false;
				}
				Settings.SaveStr(slot, "");
				return Settings.LoadStr(slot, "").Length == 0;
			}
			catch
			{
				Logger.Warn(Lang.T("log.suppressioncore.1") + valName + Lang.T("log.reversiblereg.11"));
				return false;
			}
		}
	}
}
