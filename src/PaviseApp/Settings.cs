using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class Settings
	{
		internal const string Key = "Software\\PaviseAuto";

		internal const string GpuIdentityReceiptKey = "MonitoringGpuIdentityTraceV1";

		internal const string GpuIdentitySessionPattern = "\\APavise\\.GpuIdentity\\.[0-9]+\\.[0-9a-f]{32}\\z";

		private static readonly object writeSync = new object();

		private static bool writesSuspendedForReset;

		private static int mutationGeneration;

		private static readonly object readCacheSync = new object();

		private static readonly Dictionary<string, bool> readCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

		private static int readCacheGeneration = -1;

#if PAVISE_SELFTEST
		private static Dictionary<string, object> transientValues;

		internal static void UseTransientStoreForCurrentProcess()
		{
			lock (writeSync)
			{
				transientValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
				writesSuspendedForReset = false;
				BumpMutationGeneration();
				lock (readCacheSync)
				{
					readCache.Clear();
					readCacheGeneration = -1;
				}
			}
		}
#endif

		internal static int MutationGeneration
		{
			get
			{
				return Volatile.Read(ref mutationGeneration);
			}
		}

		private static void BumpMutationGeneration()
		{
			Interlocked.Increment(ref mutationGeneration);
		}

		internal static void SuspendWritesForReset()
		{
			lock (writeSync)
			{
				writesSuspendedForReset = true;
			}
		}

		internal static bool ClearUserPreferencesForReset(Predicate<string> selected, out int removed, out string failure)
		{
			removed = 0;
			failure = null;
			if (selected == null)
			{
				failure = "Missing preference selection";
				return false;
			}
			lock (writeSync)
			{
				writesSuspendedForReset = true;
				try
				{
					List<string> list = new List<string>();
					try
					{
#if PAVISE_SELFTEST
						if (transientValues != null)
						{
							foreach (string name in new List<string>(transientValues.Keys))
							{
								if (selected(name) && transientValues.Remove(name)) removed++;
							}
							return true;
						}
#endif
						using (RegistryKey registryKey = ProfiledOpenWritable())
						{
							if (registryKey == null)
							{
								return true;
							}
							string[] valueNames = registryKey.GetValueNames();
							foreach (string text in valueNames)
							{
								if (!selected(text))
								{
									continue;
								}
								try
								{
									registryKey.DeleteValue(text, false);
									bool flag = false;
									string[] valueNames2 = registryKey.GetValueNames();
									foreach (string a in valueNames2)
									{
										if (string.Equals(a, text, StringComparison.OrdinalIgnoreCase))
										{
											flag = true;
											break;
										}
									}
									if (flag)
									{
										list.Add(text + " (still present)");
									}
									else
									{
										removed++;
									}
								}
								catch (Exception ex)
								{
									list.Add(text + " (" + ex.GetType().Name + ")");
								}
							}
							registryKey.Flush();
						}
					}
					catch (Exception ex2)
					{
						list.Add(ex2.GetType().Name);
					}
					failure = ((list.Count == 0) ? null : string.Join("; ", list.ToArray()));
					return list.Count == 0;
				}
				finally
				{
					BumpMutationGeneration();
				}
			}
		}

		internal static bool TryLoadCached(string name, out bool value)
		{
			int num = MutationGeneration;
			lock (readCacheSync)
			{
				if (readCacheGeneration != num)
				{
					readCache.Clear();
					readCacheGeneration = num;
				}
				return readCache.TryGetValue(name, out value);
			}
		}

		public static bool LoadCached(string name, bool def)
		{
			int num = MutationGeneration;
			lock (readCacheSync)
			{
				if (readCacheGeneration != num)
				{
					readCache.Clear();
					readCacheGeneration = num;
				}
				bool value;
				if (readCache.TryGetValue(name, out value))
				{
					return value;
				}
			}
			bool flag = Load(name, def);
			lock (readCacheSync)
			{
				if (readCacheGeneration == num)
				{
					readCache[name] = flag;
				}
			}
			return flag;
		}

		internal static bool TryLoadStoredBool(string name, out bool value)
		{
			value = false;
			try
			{
				object obj = ReadValue(name);
				if (obj == null)
				{
					return false;
				}
				value = Convert.ToInt32(obj) != 0;
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static bool TryLoadBool(string name, bool fallback, out bool value)
		{
			value = fallback;
			try
			{
				object obj = ReadValue(name);
				if (obj != null)
				{
					try
					{
						value = Convert.ToInt32(obj) != 0;
					}
					catch
					{
						value = fallback;
					}
				}
				return true;
			}
			catch
			{
				value = fallback;
				return false;
			}
		}

		private static RegistryKey ProfiledOpen()
		{
#if PAVISE_SELFTEST
			throw new InvalidOperationException("Self-tests cannot open the real user settings registry.");
#else
			return Registry.CurrentUser.OpenSubKey("Software\\PaviseAuto");
#endif
		}

		private static RegistryKey ProfiledCreate()
		{
#if PAVISE_SELFTEST
			throw new InvalidOperationException("Self-tests cannot create the real user settings registry.");
#else
			return Registry.CurrentUser.CreateSubKey("Software\\PaviseAuto");
#endif
		}

		private static RegistryKey ProfiledOpenWritable()
		{
#if PAVISE_SELFTEST
			throw new InvalidOperationException("Self-tests cannot write the real user settings registry.");
#else
			return Registry.CurrentUser.OpenSubKey("Software\\PaviseAuto", true);
#endif
		}

		private static object ReadValue(string name)
		{
#if PAVISE_SELFTEST
			lock (writeSync)
			{
				if (transientValues != null)
				{
					object value;
					return transientValues.TryGetValue(name, out value) ? value : null;
				}
			}
#endif
			object obj = null;
			for (int i = 0; i < 8; i++)
			{
				using (RegistryKey registryKey = ProfiledOpen())
				{
					obj = ((registryKey == null) ? null : registryKey.GetValue(name));
				}
				string text = obj as string;
				if (text == null || text.IndexOf('\0') < 0)
				{
					break;
				}
			}
			return obj;
		}

		internal static void NoteExternalMutation()
		{
			BumpMutationGeneration();
		}

		public static bool Load(string name, bool def)
		{
			try
			{
				object obj = ReadValue(name);
				return (obj == null) ? def : (Convert.ToInt32(obj) != 0);
			}
			catch
			{
				return def;
			}
		}

		public static bool Save(string name, bool val)
		{
			lock (writeSync)
			{
				try
				{
					if (writesSuspendedForReset)
					{
						return false;
					}
#if PAVISE_SELFTEST
					if (transientValues != null)
					{
						transientValues[name] = val ? 1 : 0;
						return true;
					}
#endif
					try
					{
						using (RegistryKey registryKey = ProfiledCreate())
						{
							if (registryKey == null)
							{
								throw new InvalidOperationException(Lang.T("t.settings.1"));
							}
							registryKey.SetValue(name, val ? 1 : 0);
						}
						return true;
					}
					catch (Exception error)
					{
						Logger.LogFailure(Lang.T("log.settings.2") + name, error);
						return false;
					}
				}
				finally
				{
					BumpMutationGeneration();
				}
			}
		}

		public static void Remove(string name)
		{
			if (RecoveryRecordKeys.Contains(name))
			{
				SaveString(name, "", true);
				return;
			}
			lock (writeSync)
			{
				try
				{
					if (writesSuspendedForReset)
					{
						return;
					}
#if PAVISE_SELFTEST
					if (transientValues != null)
					{
						transientValues.Remove(name);
						return;
					}
#endif
					try
					{
						using (RegistryKey registryKey = ProfiledOpenWritable())
						{
							if (registryKey != null && registryKey.GetValue(name) != null)
							{
								registryKey.DeleteValue(name, false);
							}
						}
					}
					catch
					{
					}
				}
				finally
				{
					BumpMutationGeneration();
				}
			}
		}

		internal static bool TryRemove(string name, bool durable)
		{
			if (string.IsNullOrEmpty(name) || RecoveryRecordKeys.Contains(name))
			{
				return false;
			}
			lock (writeSync)
			{
				try
				{
					if (writesSuspendedForReset)
					{
						return false;
					}
#if PAVISE_SELFTEST
					if (transientValues != null)
					{
						transientValues.Remove(name);
						return true;
					}
#endif
					try
					{
						using (RegistryKey registryKey = ProfiledOpenWritable())
						{
							if (registryKey == null)
							{
								return ReadValue(name) == null;
							}
							registryKey.DeleteValue(name, false);
							if (durable)
							{
								registryKey.Flush();
							}
						}
						return ReadValue(name) == null;
					}
					catch (Exception error)
					{
						Logger.LogFailure(Lang.T("log.settings.2") + name, error);
						return false;
					}
				}
				finally
				{
					BumpMutationGeneration();
				}
			}
		}

		private static bool TryLoadRawNames(string prefix, out string[] names)
		{
			names = new string[0];
			if (prefix == null)
			{
				return false;
			}
#if PAVISE_SELFTEST
			lock (writeSync)
			{
				if (transientValues != null)
				{
					List<string> storedNames = new List<string>();
					foreach (string name in transientValues.Keys)
						if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) storedNames.Add(name);
					storedNames.Sort(StringComparer.OrdinalIgnoreCase);
					names = storedNames.ToArray();
					return true;
				}
			}
#endif
			try
			{
				List<string> list = new List<string>();
				using (RegistryKey registryKey = ProfiledOpen())
				{
					if (registryKey != null)
					{
						string[] valueNames = registryKey.GetValueNames();
						foreach (string text in valueNames)
						{
							if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
							{
								list.Add(text);
							}
						}
					}
				}
				list.Sort(StringComparer.OrdinalIgnoreCase);
				names = list.ToArray();
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static bool TryLoadStoredNames(string prefix, out string[] names)
		{
			names = new string[0];
			if (string.IsNullOrEmpty(prefix))
			{
				return false;
			}
			return TryLoadRawNames(prefix, out names);
		}

		private static bool TryLoadRawStr(string name, out string value)
		{
			bool present;
			return TryLoadRawStr(name, out value, out present);
		}

		private static bool TryLoadRawStr(string name, out string value, out bool present)
		{
			value = "";
			present = false;
			try
			{
				object obj = ReadValue(name);
				if (obj == null)
				{
					return true;
				}
				if (!(obj is string))
				{
					return false;
				}
				value = (string)obj;
				present = true;
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static bool TryLoadStr(string name, out string value)
		{
			return TryLoadRawStr(name, out value);
		}

		public static string LoadStr(string name, string def)
		{
			if (RecoveryRecordKeys.Contains(name))
			{
				string value;
				bool present;
				if (!TryLoadRawStr(name, out value, out present))
				{
					return "!unreadable-recovery-record!";
				}
				if (!present)
				{
					return def;
				}
				return value;
			}
			try
			{
				object obj = ReadValue(name);
				return (obj == null) ? def : obj.ToString();
			}
			catch
			{
				return def;
			}
		}

		public static bool SaveStr(string name, string val)
		{
			return SaveString(name, val, false);
		}

		internal const string PresentProbeReceiptKey = FrameCoordinationTrace.PresentProbeReceiptKey;

		internal const string PresentProbeSessionPattern = FrameCoordinationTrace.PresentProbeSessionPattern;

		// ETW receipt writes must not advance the optimistic user-setting generation.
		internal static bool SavePresentProbeReceipt(string value)
		{
			string text = value ?? "";
			if (text.Length != 0 && !Regex.IsMatch(text, PresentProbeSessionPattern))
			{
				return false;
			}
			lock (writeSync)
			{
				if (writesSuspendedForReset)
				{
					return false;
				}
				try
				{
					string existing;
					if (!TryLoadRawStr(PresentProbeReceiptKey, out existing))
					{
						return false;
					}
#if PAVISE_SELFTEST
					if (transientValues != null)
					{
						transientValues[PresentProbeReceiptKey] = text;
						return true;
					}
#endif
					using (RegistryKey registryKey = ProfiledCreate())
					{
						if (registryKey == null)
						{
							return false;
						}
						registryKey.SetValue(PresentProbeReceiptKey, text, RegistryValueKind.String);
					}
					string readBack;
					return TryLoadRawStr(PresentProbeReceiptKey, out readBack) && readBack == text;
				}
				catch (Exception error)
				{
					Logger.LogFailure(Lang.T("log.settings.2") + PresentProbeReceiptKey, error);
					return false;
				}
			}
		}

		internal static bool SaveGpuIdentityReceipt(string value)
		{
			string text = value ?? "";
			if (text.Length == 0 || Regex.IsMatch(text, "\\APavise\\.GpuIdentity\\.[0-9]+\\.[0-9a-f]{32}\\z"))
			{
				lock (writeSync)
				{
					if (!writesSuspendedForReset)
					{
						try
						{
							string value2;
							if (!TryLoadRawStr("MonitoringGpuIdentityTraceV1", out value2))
							{
								return false;
							}
#if PAVISE_SELFTEST
							if (transientValues != null)
							{
								transientValues[GpuIdentityReceiptKey] = text;
								return true;
							}
#endif
							using (RegistryKey registryKey = ProfiledCreate())
							{
								if (registryKey == null)
								{
									return false;
								}
								registryKey.SetValue("MonitoringGpuIdentityTraceV1", text, RegistryValueKind.String);
							}
							string value3;
							return TryLoadRawStr("MonitoringGpuIdentityTraceV1", out value3) && value3 == text;
						}
						catch (Exception error)
						{
							Logger.LogFailure(Lang.T("log.settings.2") + "MonitoringGpuIdentityTraceV1", error);
							return false;
						}
					}
					return false;
				}
			}
			return false;
		}

		internal static bool SaveStrDurable(string name, string val)
		{
			return SaveString(name, val, true);
		}

		private static bool SaveString(string name, string val, bool durable)
		{
			lock (writeSync)
			{
				try
				{
					if (writesSuspendedForReset)
					{
						return false;
					}
					string value = val ?? "";
					try
					{
						string value2;
						if (RecoveryRecordKeys.Contains(name) && !TryLoadRawStr(name, out value2))
						{
							return false;
						}
#if PAVISE_SELFTEST
						if (transientValues != null)
						{
							transientValues[name] = value;
							return true;
						}
#endif
						using (RegistryKey registryKey = ProfiledCreate())
						{
							if (registryKey == null)
							{
								throw new InvalidOperationException(Lang.T("t.settings.1"));
							}
							registryKey.SetValue(name, value, RegistryValueKind.String);
							if (durable || RecoveryRecordKeys.Contains(name))
							{
								registryKey.Flush();
							}
						}
						return true;
					}
					catch (Exception error)
					{
						Logger.LogFailure(Lang.T("log.settings.2") + name, error);
						return false;
					}
				}
				finally
				{
					BumpMutationGeneration();
				}
			}
		}

		internal static bool SaveDurable(string name, bool val)
		{
			lock (writeSync)
			{
				try
				{
					if (writesSuspendedForReset)
					{
						return false;
					}
#if PAVISE_SELFTEST
					if (transientValues != null)
					{
						transientValues[name] = val ? 1 : 0;
						return true;
					}
#endif
					try
					{
						using (RegistryKey registryKey = ProfiledCreate())
						{
							if (registryKey == null)
							{
								throw new InvalidOperationException(Lang.T("t.settings.1"));
							}
							registryKey.SetValue(name, val ? 1 : 0, RegistryValueKind.DWord);
							registryKey.Flush();
						}
						return true;
					}
					catch (Exception error)
					{
						Logger.LogFailure(Lang.T("log.settings.2") + name, error);
						return false;
					}
				}
				finally
				{
					BumpMutationGeneration();
				}
			}
		}
	}
}
