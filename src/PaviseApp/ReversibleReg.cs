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
					string text = OriginalPart(Settings.LoadStr(slot, ""));
					if (text.Length == 0)
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
						text = Encode(value);
						Settings.SaveStr(slot, text);
						if (Settings.LoadStr(slot, "") != text)
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
					bool flag = SameByKind(value2, newVal);
					if (flag)
					{
						Settings.SaveStr(slot, text + '\u001f' + Encode(newVal));
					}
					return flag;
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
			int num = stored.LastIndexOf('\u001f');
			if (num >= 0)
			{
				return stored.Substring(0, num);
			}
			return stored;
		}

		private static string AppliedPart(string stored)
		{
			int num = stored.LastIndexOf('\u001f');
			if (num >= 0)
			{
				return stored.Substring(num + 1);
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
				string text = ((repr[0] == '=') ? repr.Substring(1) : repr);
				if (kind == RegistryValueKind.DWord)
				{
					long result;
					if (!long.TryParse(text, out result))
					{
						return false;
					}
					value = (int)result;
					return true;
				}
				value = text;
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
					object obj = ((registryKey == null) ? null : registryKey.GetValue(valName));
					if (obj == null)
					{
						return false;
					}
					if (kind == RegistryValueKind.DWord)
					{
						return Convert.ToInt64(obj) == Convert.ToInt64(expected);
					}
					if (kind == RegistryValueKind.Binary)
					{
						return BytesEqual(obj as byte[], expected as byte[]);
					}
					return string.Equals(obj.ToString(), (expected == null) ? "" : expected.ToString(), StringComparison.Ordinal);
				}
			}
			catch
			{
				return false;
			}
		}

		public bool Restore()
		{
			string text = Settings.LoadStr(slot, "");
			if (text.Length == 0)
			{
				return true;
			}
			string text2 = OriginalPart(text);
			string text3 = AppliedPart(text);
			bool flag = text2 == "__pavise_absent__";
			object obj = null;
			if (!flag)
			{
				if (kind == RegistryValueKind.Binary)
				{
					if (text2.Length < 1 || text2[0] != 'b')
					{
						Settings.SaveStr(slot, "");
						Logger.Log(Lang.T("log.reversiblereg.5") + valName + Lang.T("log.reversiblereg.6"));
						return false;
					}
					try
					{
						obj = Convert.FromBase64String(text2.Substring(1));
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
					string text4 = ((text2[0] == '=') ? text2.Substring(1) : text2);
					if (kind == RegistryValueKind.DWord)
					{
						long result;
						if (!long.TryParse(text4, out result))
						{
							Settings.SaveStr(slot, "");
							Logger.Log(Lang.T("log.reversiblereg.5") + valName + Lang.T("log.reversiblereg.8") + text4 + "\" ");
							return false;
						}
						obj = (int)result;
					}
					else
					{
						obj = text4;
					}
				}
			}
			try
			{
				bool flag2 = false;
				using (RegistryKey registryKey = hive.OpenSubKey(subKey, true))
				{
					object value;
					if (registryKey != null && text3.Length > 0 && TryDecode(text3, out value))
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
						flag2 = true;
					}
					else if (flag)
					{
						if (registryKey.GetValue(valName) != null)
						{
							registryKey.DeleteValue(valName, false);
						}
						flag2 = registryKey.GetValue(valName) == null;
					}
					else
					{
						registryKey.SetValue(valName, obj, kind);
						object value3 = registryKey.GetValue(valName);
						RegistryValueKind registryValueKind = RegistryValueKind.Unknown;
						try
						{
							registryValueKind = registryKey.GetValueKind(valName);
						}
						catch
						{
						}
						flag2 = value3 != null && registryValueKind == kind;
						if (flag2 && kind == RegistryValueKind.DWord)
						{
							flag2 = Convert.ToInt64(value3) == Convert.ToInt64(obj);
						}
						else if (flag2 && kind == RegistryValueKind.Binary)
						{
							flag2 = BytesEqual((byte[])value3, (byte[])obj);
						}
						else if (flag2)
						{
							flag2 = string.Equals(value3.ToString(), (obj == null) ? "" : obj.ToString(), StringComparison.Ordinal);
						}
					}
				}
				if (!flag2)
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
