using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
	internal static class AdlxTweaks
	{
		private delegate bool FeatureGetter(IntPtr gpu, out bool supported, out bool enabled);

		private delegate bool FeatureApplier(IntPtr gpu);

		private delegate bool RestoreApply(IntPtr gpu, string orig);

		private delegate bool RangeGetter(IntPtr gpu, out bool supported, out string snapValue, out AdlxIntRange range);

		private delegate bool RangeSetter(IntPtr gpu, int value);

		private delegate bool RangeVerifier(IntPtr gpu, int value);

		internal const string SnapKey = "AmdSnap";

		public const int RisSharpness = 80;

		public const int RsrDefaultSharpness = 75;

		private static readonly object lk = new object();

		private static bool renderSkipLogged;

		private static int alagSup;

		private static int afmfSup;

		private static int rsrSup;

		public static bool Available
		{
			get
			{
				return AdlxApi.Available;
			}
		}

		private static bool AmdRenderGateOpen()
		{
			GpuAdapter[] adaptersResult = GpuInventory.Adapters();
			if (adaptersResult == null || adaptersResult.Length == 0)
			{
				return true;
			}
			bool amdRenderGateOpenReady = false;
			bool amdRenderGateOpenReady2 = false;
			bool amdRenderGateOpenReady3 = false;
			GpuAdapter[] gpuAdapterItems = adaptersResult;
			foreach (GpuAdapter gpuAdapter in gpuAdapterItems)
			{
				if (gpuAdapter.Vendor == GpuVendor.Amd)
				{
					amdRenderGateOpenReady = true;
					if (!gpuAdapter.Integrated)
					{
						amdRenderGateOpenReady2 = true;
					}
				}
				else if (!gpuAdapter.Integrated)
				{
					amdRenderGateOpenReady3 = true;
				}
			}
			bool matchesAmdRenderGateOpenReady2 = amdRenderGateOpenReady2 || (amdRenderGateOpenReady && !amdRenderGateOpenReady3);
			if (!matchesAmdRenderGateOpenReady2 && !renderSkipLogged)
			{
				renderSkipLogged = true;
				Logger.Log(Lang.T("log.adlxtweaks.36"));
			}
			return matchesAmdRenderGateOpenReady2;
		}

		private static Dictionary<string, string> LoadSnap()
		{
			return NvDrsTweaks.ParseSnapshot(Settings.LoadStr("AmdSnap", ""));
		}

		private static bool SaveSnap(Dictionary<string, string> snapshot)
		{
			return Settings.SaveStrDurable("AmdSnap", (snapshot.Count == 0) ? "" : NvDrsTweaks.SerializeSnapshot(snapshot));
		}

		private static string GpuKey(IntPtr gpu, int index, ref bool identityMissing)
		{
			string gpuPnpStringResult = AdlxApi.GpuPnpString(gpu);
			if (string.IsNullOrEmpty(gpuPnpStringResult))
			{
				identityMissing = true;
				return "g" + index;
			}
			return "id" + gpuPnpStringResult.Replace('=', '_').Replace(';', '_');
		}

		private static bool EnsureSnapshot(Dictionary<string, string> snapshot, string key, string legacyKey, string value, string label)
		{
			if (!snapshot.ContainsKey(key) && legacyKey != null && snapshot.ContainsKey(legacyKey))
			{
				snapshot[key] = snapshot[legacyKey];
				snapshot.Remove(legacyKey);
				if (!SaveSnap(snapshot))
				{
					Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.1"));
					return false;
				}
				return true;
			}
			if (!snapshot.ContainsKey(key))
			{
				snapshot[key] = value;
				if (!SaveSnap(snapshot))
				{
					Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.1"));
					return false;
				}
			}
			return true;
		}

		public static bool RestoreAntiLag()
		{
			return RestoreFeature("alag", "Anti-Lag", delegate(IntPtr gpu, string orig)
			{
				bool supported;
				bool enabled;
				if ((orig != "0" && orig != "1") || !AdlxApi.AntiLagGet(gpu, out supported, out enabled) || !supported)
				{
					return false;
				}
				if (enabled == (orig == "1"))
				{
					return true;
				}
				return AdlxApi.AntiLagSet(gpu, orig == "1") && AdlxApi.AntiLagGet(gpu, out supported, out enabled) && supported && enabled == (orig == "1");
			});
		}

		public static bool ActivateEnhancedSync()
		{
			return ApplyToggle("esync", "Enhanced Sync", delegate(IntPtr gpu, out bool supported, out bool enabled)
			{
				return AdlxApi.EnhancedSyncGet(gpu, out supported, out enabled);
			}, (IntPtr gpu) => AdlxApi.EnhancedSyncSet(gpu, true), true);
		}

		public static bool RestoreEnhancedSync()
		{
			return RestoreFeature("esync", "Enhanced Sync", (IntPtr gpu, string orig) => AdlxApi.EnhancedSyncSet(gpu, orig == "1"));
		}

		public static bool RestoreChill()
		{
			return RestoreFeature("chill", "Chill", delegate(IntPtr gpu, string orig)
			{
				string[] origParts = orig.Split('|');
				int result;
				int result2;
				if (origParts.Length != 3 || !int.TryParse(origParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || !int.TryParse(origParts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out result2))
				{
					return true;
				}
				return (!(origParts[0] == "1")) ? AdlxApi.ChillSet(gpu, false, 0, 0) : AdlxApi.ChillSet(gpu, true, result, result2);
			});
		}

		public static bool ActivateRis()
		{
			bool supported2;
			bool enabled;
			int sharpness;
			AdlxIntRange range2;
			return ApplyRange("ris", Lang.T("t.adlxtweaks.4"), 80, Lang.T("t.adlxtweaks.5") + 80, delegate(IntPtr gpu, out bool supported, out string snapValue, out AdlxIntRange range)
			{
				bool enabled2;
				int sharpness2;
				bool result = AdlxApi.RisGet(gpu, out supported, out enabled2, out sharpness2, out range);
				snapValue = (enabled2 ? "1" : "0") + "|" + sharpness2;
				return result;
			}, (IntPtr gpu, int target) => AdlxApi.RisSet(gpu, true, target), (IntPtr gpu, int target) => AdlxApi.RisGet(gpu, out supported2, out enabled, out sharpness, out range2) && enabled);
		}

		public static bool RestoreRis()
		{
			return RestoreFeature("ris", Lang.T("t.adlxtweaks.4"), delegate(IntPtr gpu, string orig)
			{
				string[] origParts = orig.Split('|');
				int result;
				return origParts.Length != 2 || !int.TryParse(origParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || AdlxApi.RisSet(gpu, origParts[0] == "1", result);
			});
		}

		public static bool HasAntiLagResidue()
		{
			Dictionary<string, string> snapshot = LoadSnap();
			if (!HasPrefix(snapshot, ".alag"))
			{
				return HasPrefix(snapshot, ".chill");
			}
			return true;
		}

		public static bool RestoreFrameLimit()
		{
			return RestoreChill() & RestoreFrtc();
		}

		public static bool HasFrameLimitResidue()
		{
			Dictionary<string, string> snapshot = LoadSnap();
			if (!HasPrefix(snapshot, ".chill"))
			{
				return HasPrefix(snapshot, ".frtc");
			}
			return true;
		}

		public static bool RestoreFrtc()
		{
			return RestoreFeature("frtc", Lang.T("t.adlxtweaks.8"), delegate(IntPtr gpu, string orig)
			{
				string[] origParts = orig.Split('|');
				int result;
				if (origParts.Length != 2 || !int.TryParse(origParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
				{
					return true;
				}
				return (!(origParts[0] == "1")) ? AdlxApi.FrtcSet(gpu, false, 0) : AdlxApi.FrtcSet(gpu, true, result);
			});
		}

		public static bool ActivateAfmf()
		{
			if (!Available)
			{
				return false;
			}
			lock (lk)
			{
				bool supported;
				bool enabled;
				if (!AdlxApi.AfmfGet(out supported, out enabled) || !supported)
				{
					Logger.Log(Lang.T("log.adlxtweaks.11"));
					return false;
				}
				if (enabled)
				{
					return true;
				}
				Dictionary<string, string> dictionary = LoadSnap();
				if (!dictionary.ContainsKey("sys.afmf"))
				{
					dictionary["sys.afmf"] = "0";
					if (!SaveSnap(dictionary))
					{
						Logger.Log(Lang.T("log.adlxtweaks.12"));
						return false;
					}
				}
				if (!AdlxApi.AfmfSet(true))
				{
					Logger.Warn(Lang.T("log.adlxtweaks.13"));
					return false;
				}
				bool supported2;
				bool enabled2;
				if (AdlxApi.AfmfGet(out supported2, out enabled2) && enabled2)
				{
					Logger.Log(Lang.T("log.adlxtweaks.14"));
					return true;
				}
				Logger.Log(Lang.T("log.adlxtweaks.15"));
				return false;
			}
		}

		public static bool RestoreAfmf()
		{
			lock (lk)
			{
				Dictionary<string, string> dictionary = LoadSnap();
				string value;
				if (!dictionary.TryGetValue("sys.afmf", out value))
				{
					return true;
				}
				if (!Available)
				{
					return false;
				}
				if (!AdlxApi.AfmfSet(value == "1"))
				{
					Logger.Log(Lang.T("log.adlxtweaks.16"));
					return false;
				}
				dictionary.Remove("sys.afmf");
				SaveSnap(dictionary);
				Logger.Log(Lang.T("log.adlxtweaks.17"));
				return true;
			}
		}

		public static bool ActivateRsr()
		{
			if (!AmdRenderGateOpen())
			{
				return true;
			}
			if (!Available)
			{
				return false;
			}
			lock (lk)
			{
				bool supported;
				bool enabled;
				int sharpness;
				if (!AdlxApi.RsrGet(out supported, out enabled, out sharpness) || !supported)
				{
					Logger.Log(Lang.T("log.adlxtweaks.18"));
					return false;
				}
				if (enabled)
				{
					return true;
				}
				Dictionary<string, string> dictionary = LoadSnap();
				if (!dictionary.ContainsKey("sys.rsr"))
				{
					dictionary["sys.rsr"] = "0";
					dictionary["sys.rsrsharp"] = sharpness.ToString();
					if (!SaveSnap(dictionary))
					{
						Logger.Log(Lang.T("log.adlxtweaks.19"));
						return false;
					}
				}
				if (!AdlxApi.RsrSet(true, 75))
				{
					Logger.Warn(Lang.T("log.adlxtweaks.20"));
					return false;
				}
				bool supported2;
				bool enabled2;
				int sharpness2;
				if (AdlxApi.RsrGet(out supported2, out enabled2, out sharpness2) && enabled2)
				{
					Logger.Log(Lang.T("log.adlxtweaks.21"));
				}
				return true;
			}
		}

		public static bool RestoreRsr()
		{
			lock (lk)
			{
				Dictionary<string, string> dictionary = LoadSnap();
				string value;
				if (!dictionary.TryGetValue("sys.rsr", out value))
				{
					return true;
				}
				if (!Available)
				{
					return false;
				}
				string value2;
				int result;
				if (!dictionary.TryGetValue("sys.rsrsharp", out value2) || !int.TryParse(value2, out result))
				{
					result = -1;
				}
				if (!AdlxApi.RsrSet(value == "1", (value == "1") ? result : (-1)))
				{
					Logger.Log(Lang.T("log.adlxtweaks.22"));
					return false;
				}
				dictionary.Remove("sys.rsr");
				dictionary.Remove("sys.rsrsharp");
				SaveSnap(dictionary);
				Logger.Log(Lang.T("log.adlxtweaks.23"));
				return true;
			}
		}

		public static bool PowerLimitGetFirst(out int current, out int max)
		{
			current = 0;
			max = 0;
			if (!Available)
			{
				return false;
			}
			IntPtr[] gpus = AdlxApi.GetGpus();
			if (gpus == null || gpus.Length == 0)
			{
				return false;
			}
			try
			{
				IntPtr[] gpusSnapshot = gpus;
				foreach (IntPtr gpu in gpusSnapshot)
				{
					bool supported;
					int current2;
					int max2;
					if (AdlxApi.PowerLimitGet(gpu, out supported, out current2, out max2) && supported && max2 > 0)
					{
						current = current2;
						max = max2;
						return true;
					}
				}
				return false;
			}
			finally
			{
				AdlxApi.ReleaseAll(gpus);
			}
		}

		public static bool PowerLimitSetFirst(int value)
		{
			if (!Available)
			{
				return false;
			}
			IntPtr[] gpus = AdlxApi.GetGpus();
			if (gpus == null || gpus.Length == 0)
			{
				return false;
			}
			try
			{
				IntPtr[] gpusSnapshot = gpus;
				foreach (IntPtr gpu in gpusSnapshot)
				{
					bool supported;
					int current;
					int max;
					if (AdlxApi.PowerLimitGet(gpu, out supported, out current, out max) && supported && max > 0)
					{
						return AdlxApi.PowerLimitSet(gpu, value);
					}
				}
				return false;
			}
			finally
			{
				AdlxApi.ReleaseAll(gpus);
			}
		}

		public static bool RsrSupported()
		{
			if (!AmdRenderGateOpen())
			{
				return false;
			}
			if (rsrSup == 0)
			{
				bool supported;
				bool enabled;
				int sharpness;
				rsrSup = ((Available && AdlxApi.RsrGet(out supported, out enabled, out sharpness) && supported) ? 1 : (-1));
			}
			return rsrSup > 0;
		}

		public static bool AntiLagSupported()
		{
			if (!AmdRenderGateOpen())
			{
				return false;
			}
			if (alagSup == 0)
			{
				alagSup = (ProbeGpuToggle(delegate(IntPtr gpu, out bool s, out bool e)
				{
					return AdlxApi.AntiLagGet(gpu, out s, out e);
				}) ? 1 : (-1));
			}
			return alagSup > 0;
		}

		public static bool AfmfSupported()
		{
			if (!AmdRenderGateOpen())
			{
				return false;
			}
			if (afmfSup == 0)
			{
				bool supported;
				bool enabled;
				afmfSup = ((Available && AdlxApi.AfmfGet(out supported, out enabled) && supported) ? 1 : (-1));
			}
			return afmfSup > 0;
		}

		private static bool ProbeGpuToggle(FeatureGetter get)
		{
			if (!Available)
			{
				return false;
			}
			IntPtr[] gpus = AdlxApi.GetGpus();
			if (gpus == null || gpus.Length == 0)
			{
				return false;
			}
			try
			{
				IntPtr[] gpusSnapshot = gpus;
				foreach (IntPtr gpu in gpusSnapshot)
				{
					bool supported;
					bool enabled;
					if (get(gpu, out supported, out enabled) && supported)
					{
						return true;
					}
				}
				return false;
			}
			finally
			{
				AdlxApi.ReleaseAll(gpus);
			}
		}

		private static bool ApplyRange(string keySuffix, string label, int desired, string successLog, RangeGetter get, RangeSetter set, RangeVerifier verify)
		{
			if (!Available)
			{
				return false;
			}
			lock (lk)
			{
				IntPtr[] gpus = AdlxApi.GetGpus();
				if (gpus == null || gpus.Length == 0)
				{
					return false;
				}
				try
				{
					Dictionary<string, string> snapshot = LoadSnap();
					bool identityMissing = false;
					int processedCount = 0;
					int processedCount2 = 0;
					for (int i = 0; i < gpus.Length; i++)
					{
						bool supported;
						string snapValue;
						AdlxIntRange range;
						if (get(gpus[i], out supported, out snapValue, out range) && supported)
						{
							string key = GpuKey(gpus[i], i, ref identityMissing) + "." + keySuffix;
							if (!EnsureSnapshot(snapshot, key, "g" + i + "." + keySuffix, snapValue, label))
							{
								return false;
							}
							int value = Clamp(desired, range.Min, range.Max);
							if (!set(gpus[i], value))
							{
								processedCount2++;
							}
							else if (verify(gpus[i], value))
							{
								processedCount++;
							}
							else
							{
								processedCount2++;
							}
						}
					}
					if (processedCount > 0 && processedCount2 == 0)
					{
						Logger.Log(successLog);
						return true;
					}
					if (processedCount2 > 0)
					{
						Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.24") + processedCount2 + Lang.T("log.adlxtweaks.25"));
					}
					else if (processedCount == 0)
					{
						Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.26"));
					}
					return false;
				}
				finally
				{
					AdlxApi.ReleaseAll(gpus);
				}
			}
		}

		private static bool ApplyToggle(string keySuffix, string label, FeatureGetter get, FeatureApplier apply, bool wantEnabled)
		{
			if (!Available)
			{
				return false;
			}
			lock (lk)
			{
				IntPtr[] gpus = AdlxApi.GetGpus();
				if (gpus == null || gpus.Length == 0)
				{
					return false;
				}
				try
				{
					Dictionary<string, string> snapshot = LoadSnap();
					bool identityMissing = false;
					int processedCount = 0;
					int processedCount2 = 0;
					for (int i = 0; i < gpus.Length; i++)
					{
						bool supported;
						bool enabled;
						if (!get(gpus[i], out supported, out enabled) || !supported)
						{
							continue;
						}
						if (enabled == wantEnabled)
						{
							processedCount++;
							continue;
						}
						string key = GpuKey(gpus[i], i, ref identityMissing) + "." + keySuffix;
						if (!EnsureSnapshot(snapshot, key, "g" + i + "." + keySuffix, enabled ? "1" : "0", label))
						{
							return false;
						}
						bool supported2;
						bool enabled2;
						if (!apply(gpus[i]))
						{
							processedCount2++;
						}
						else if (get(gpus[i], out supported2, out enabled2) && enabled2 == wantEnabled)
						{
							processedCount++;
						}
						else
						{
							processedCount2++;
						}
					}
					if (processedCount > 0 && processedCount2 == 0)
					{
						Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.27"));
						return true;
					}
					if (processedCount2 > 0)
					{
						Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.24") + processedCount2 + Lang.T("log.adlxtweaks.25"));
					}
					else if (processedCount == 0)
					{
						Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.26"));
					}
					return false;
				}
				finally
				{
					AdlxApi.ReleaseAll(gpus);
				}
			}
		}

		private static bool RestoreFeature(string suffix, string label, RestoreApply apply)
		{
			lock (lk)
			{
				Dictionary<string, string> dictionary = LoadSnap();
				if (!HasPrefix(dictionary, "." + suffix))
				{
					return true;
				}
				if (!Available)
				{
					return false;
				}
				IntPtr[] gpus = AdlxApi.GetGpus();
				if (gpus == null)
				{
					return false;
				}
				try
				{
					bool restoreFeatureSucceeded = true;
					bool identityMissing = false;
					HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					for (int i = 0; i < gpus.Length; i++)
					{
						string[] textEntries = new string[2]
						{
							GpuKey(gpus[i], i, ref identityMissing) + "." + suffix,
							"g" + i + "." + suffix
						};
						string[] textEntries2 = textEntries;
						foreach (string entryText in textEntries2)
						{
							hashSet.Add(entryText);
							string value;
							if (dictionary.TryGetValue(entryText, out value))
							{
								if (apply(gpus[i], value))
								{
									dictionary.Remove(entryText);
								}
								else
								{
									restoreFeatureSucceeded = false;
								}
							}
						}
					}
					if (!identityMissing)
					{
						List<string> list = new List<string>();
						foreach (KeyValuePair<string, string> item in dictionary)
						{
							if (item.Key.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase) && !hashSet.Contains(item.Key))
							{
								list.Add(item.Key);
							}
						}
						foreach (string item2 in list)
						{
							dictionary.Remove(item2);
						}
						if (list.Count > 0)
						{
							Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.28") + list.Count + Lang.T("log.adlxtweaks.29"));
						}
					}
					if (!SaveSnap(dictionary))
					{
						restoreFeatureSucceeded = false;
					}
					if (restoreFeatureSucceeded)
					{
						Logger.Log("AMD " + label + Lang.T("log.renderlane.13"));
					}
					else
					{
						Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.30"));
					}
					return restoreFeatureSucceeded;
				}
				finally
				{
					AdlxApi.ReleaseAll(gpus);
				}
			}
		}

		private static bool HasPrefix(Dictionary<string, string> snapshot, string suffix)
		{
			foreach (KeyValuePair<string, string> item in snapshot)
			{
				if (item.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		private static int Clamp(int value, int min, int max)
		{
			if (max > 0 && value > max)
			{
				return max;
			}
			if (min > 0 && value < min)
			{
				return min;
			}
			return value;
		}

		public static bool ResetShaderCacheAll(out int done)
		{
			done = 0;
			if (!Available)
			{
				return false;
			}
			IntPtr[] gpus = AdlxApi.GetGpus();
			if (gpus == null || gpus.Length == 0)
			{
				return false;
			}
			try
			{
				IntPtr[] gpusSnapshot = gpus;
				foreach (IntPtr gpu in gpusSnapshot)
				{
					if (AdlxApi.ResetShaderCache(gpu))
					{
						done++;
					}
				}
				Logger.Log(Lang.T("log.adlxtweaks.31") + done + "/" + gpus.Length + Lang.T("log.adlxtweaks.32"));
				return done > 0;
			}
			finally
			{
				AdlxApi.ReleaseAll(gpus);
			}
		}

		public static bool HasResidue()
		{
			return Settings.LoadStr("AmdSnap", "").Length > 0;
		}

		public static bool GfxMinSupported()
		{
			if (!Available)
			{
				return false;
			}
			IntPtr[] gpus = AdlxApi.GetGpus();
			if (gpus == null || gpus.Length == 0)
			{
				return false;
			}
			try
			{
				IntPtr[] gpusSnapshot = gpus;
				foreach (IntPtr gpu in gpusSnapshot)
				{
					bool supported;
					int min;
					int max;
					AdlxIntRange minRange;
					if (AdlxApi.GfxMinGet(gpu, out supported, out min, out max, out minRange) && supported)
					{
						return true;
					}
				}
				return false;
			}
			finally
			{
				AdlxApi.ReleaseAll(gpus);
			}
		}

		internal static int GfxMinTarget(int currentMax, AdlxIntRange minRange)
		{
			int currentMaxSnapshot = currentMax;
			if (minRange.Max > 0 && currentMaxSnapshot > minRange.Max)
			{
				currentMaxSnapshot = minRange.Max;
			}
			if (minRange.Min > 0 && currentMaxSnapshot < minRange.Min)
			{
				currentMaxSnapshot = minRange.Min;
			}
			return currentMaxSnapshot;
		}

		public static bool ActivateGfxMin()
		{
			if (!AmdRenderGateOpen())
			{
				return true;
			}
			if (!Available)
			{
				return false;
			}
			lock (lk)
			{
				IntPtr[] gpus = AdlxApi.GetGpus();
				if (gpus == null || gpus.Length == 0)
				{
					return false;
				}
				try
				{
					Dictionary<string, string> snapshot = LoadSnap();
					bool identityMissing = false;
					int processedCount = 0;
					int processedCount2 = 0;
					for (int i = 0; i < gpus.Length; i++)
					{
						bool supported;
						int min;
						int max;
						AdlxIntRange minRange;
						if (!AdlxApi.GfxMinGet(gpus[i], out supported, out min, out max, out minRange) || !supported)
						{
							continue;
						}
						int gfxMinTargetResult = GfxMinTarget(max, minRange);
						if (gfxMinTargetResult <= 0 || min >= gfxMinTargetResult)
						{
							processedCount++;
							continue;
						}
						string key = GpuKey(gpus[i], i, ref identityMissing) + ".gfxmin";
						if (!EnsureSnapshot(snapshot, key, null, min.ToString(CultureInfo.InvariantCulture), Lang.T("t.adlxtweaks.gfxmin")))
						{
							return false;
						}
						bool supported2;
						int min2;
						int max2;
						AdlxIntRange minRange2;
						if (!AdlxApi.GfxMinSet(gpus[i], gfxMinTargetResult))
						{
							processedCount2++;
						}
						else if (AdlxApi.GfxMinGet(gpus[i], out supported2, out min2, out max2, out minRange2) && min2 >= gfxMinTargetResult)
						{
							processedCount++;
						}
						else
						{
							processedCount2++;
						}
					}
					if (processedCount > 0 && processedCount2 == 0)
					{
						Logger.Log("AMD " + Lang.T("t.adlxtweaks.gfxmin") + Lang.T("log.adlxtweaks.27"));
						return true;
					}
					if (processedCount2 > 0)
					{
						Logger.Log("AMD " + Lang.T("t.adlxtweaks.gfxmin") + Lang.T("log.adlxtweaks.24") + processedCount2 + Lang.T("log.adlxtweaks.25"));
					}
					else
					{
						Logger.Log("AMD " + Lang.T("t.adlxtweaks.gfxmin") + Lang.T("log.adlxtweaks.26"));
					}
					return false;
				}
				finally
				{
					AdlxApi.ReleaseAll(gpus);
				}
			}
		}

		public static bool RestoreGfxMin()
		{
			int result;
			return RestoreFeature("gfxmin", Lang.T("t.adlxtweaks.gfxmin"), (IntPtr gpu, string orig) => !int.TryParse(orig, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || AdlxApi.GfxMinSet(gpu, result));
		}

		public static bool HasGfxMinResidue()
		{
			return HasPrefix(LoadSnap(), ".gfxmin");
		}

		public static bool SamGet(out bool supported, out bool enabled)
		{
			supported = false;
			enabled = false;
			if (!Available)
			{
				return false;
			}
			IntPtr[] gpus = AdlxApi.GetGpus();
			if (gpus == null || gpus.Length == 0)
			{
				return false;
			}
			try
			{
				IntPtr[] gpusSnapshot = gpus;
				foreach (IntPtr gpu in gpusSnapshot)
				{
					bool supported2;
					bool enabled2;
					if (AdlxApi.SamGet(gpu, out supported2, out enabled2) && supported2)
					{
						supported = true;
						enabled = enabled2;
						return true;
					}
				}
				return true;
			}
			finally
			{
				AdlxApi.ReleaseAll(gpus);
			}
		}

		public static bool SamSet(bool on)
		{
			if (!Available)
			{
				return false;
			}
			IntPtr[] gpus = AdlxApi.GetGpus();
			if (gpus == null || gpus.Length == 0)
			{
				return false;
			}
			try
			{
				IntPtr[] gpusSnapshot = gpus;
				foreach (IntPtr gpu in gpusSnapshot)
				{
					bool supported;
					bool enabled;
					if (AdlxApi.SamGet(gpu, out supported, out enabled) && supported)
					{
						return AdlxApi.SamSet(gpu, on);
					}
				}
				return false;
			}
			finally
			{
				AdlxApi.ReleaseAll(gpus);
			}
		}

		public static void HealFromCrash()
		{
			if (Settings.LoadStr("AmdSnap", "").Length != 0 && (RestoreAntiLag() & RestoreEnhancedSync() & RestoreChill() & RestoreRis() & RestoreFrtc() & RestoreAfmf() & RestoreRsr() & RestoreGfxMin()))
			{
				Logger.Log(Lang.T("log.adlxtweaks.33"));
			}
		}

		public static bool PurgeResidue()
		{
			HealFromCrash();
			if (!HasResidue())
			{
				return true;
			}
			if (!Available)
			{
				if (AmdDisplayPresent())
				{
					Logger.Log(Lang.T("log.adlxtweaks.34"));
					return false;
				}
				Logger.Log(Lang.T("log.adlxtweaks.35"));
				Settings.SaveStr("AmdSnap", "");
				return !HasResidue();
			}
			return false;
		}

		private static bool AmdDisplayPresent()
		{
			try
			{
				foreach (string item in PresentDevices.ByClass(new Guid("4d36e968-e325-11ce-bfc1-08002be10318")))
				{
					if (item != null && item.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						return true;
					}
				}
			}
			catch
			{
			}
			return false;
		}
	}
}
