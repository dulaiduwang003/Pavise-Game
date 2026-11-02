using System;
using System.Collections.Generic;
using System.Text;

namespace PaviseApp
{
	internal static class CrashGuard
	{
		private enum BoostIdentity
		{
			Match,
			Mismatch,
			Unknown
		}

		internal sealed class OriginalBoostState
		{
			public uint Priority;

			public ulong Affinity;

			public int Io;

			public int Page;

			public int Gpu;

			public uint[] CpuSets;

			public int QoSControl;

			public int QoSState;
		}

		private sealed class BoostEntry
		{
			public int Pid;

			public long Creation;

			public string Name;

			public uint Priority;

			public ulong Affinity;

			public int Io;

			public int Page;

			public int Gpu;

			public uint[] CpuSets;

			public int QoSControl = -1;

			public int QoSState = -1;

			public bool PlacementYielded;

			public bool PlacementTracked;

			public ulong AppliedAffinity;

			public ulong PreviousAffinity;
		}

		private const string KThrottle = "Crash_ThrottleMasks";

		private const string KBoost = "Crash_BoostMask";

		private const string KBoostNames = "Crash_BoostNames";

		private const string KBoostEntries = "Crash_BoostEntriesV2";

		private static readonly object sync = new object();

		private static readonly Dictionary<int, long> owned = new Dictionary<int, long>();

		public static bool UncleanThrottleAtLaunch { get; private set; }

		public static void MarkThrottle(ulong mask)
		{
			if (mask != 0)
			{
				Settings.SaveStr("Crash_ThrottleMasks", "journaled");
			}
		}

		public static void ReleaseThrottle(ulong mask)
		{
			Settings.SaveStr("Crash_ThrottleMasks", "");
		}

		public static bool MarkBoostProcess(int pid, long creation, string name, uint priority, ulong affinity, int io, int page, int gpu, uint[] cpuSets)
		{
			return MarkBoostProcess(pid, creation, name, priority, affinity, io, page, gpu, cpuSets, -1, -1);
		}

		public static bool MarkBoostProcess(int pid, long creation, string name, uint priority, ulong affinity, int io, int page, int gpu, uint[] cpuSets, int qosControl, int qosState)
		{
			OriginalBoostState recovered;
			return MarkBoostProcess(pid, creation, name, priority, affinity, io, page, gpu, cpuSets, qosControl, qosState, out recovered);
		}

		internal static bool MarkBoostProcess(int pid, long creation, string name, uint priority, ulong affinity, int io, int page, int gpu, uint[] cpuSets, int qosControl, int qosState, out OriginalBoostState recovered)
		{
			recovered = null;
			if (pid <= 0 || creation <= 0 || string.IsNullOrEmpty(name))
			{
				return false;
			}
			lock (sync)
			{
				List<BoostEntry> list = LoadEntries();
				foreach (BoostEntry item in list)
				{
					if (item.Pid == pid && item.Creation == creation)
					{
						if (!string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
						{
							return false;
						}
						owned[pid] = creation;
						recovered = new OriginalBoostState
						{
							Priority = item.Priority,
							Affinity = item.Affinity,
							Io = item.Io,
							Page = item.Page,
							Gpu = item.Gpu,
							CpuSets = ((item.CpuSets == null) ? new uint[0] : ((uint[])item.CpuSets.Clone())),
							QoSControl = item.QoSControl,
							QoSState = item.QoSState
						};
						return true;
					}
				}
				list.RemoveAll((BoostEntry e) => e.Pid == pid);
				list.Add(new BoostEntry
				{
					Pid = pid,
					Creation = creation,
					Name = name,
					Priority = priority,
					Affinity = affinity,
					Io = io,
					Page = page,
					Gpu = gpu,
					CpuSets = cpuSets,
					QoSControl = qosControl,
					QoSState = qosState,
					PlacementTracked = true
				});
				bool flag = SaveEntries(list);
				if (flag)
				{
					owned[pid] = creation;
				}
				else
				{
					owned.Remove(pid);
				}
				return flag;
			}
		}

		public static void ReleaseBoostProcess(int pid, long creation)
		{
			lock (sync)
			{
				long value;
				if (owned.TryGetValue(pid, out value) && (creation <= 0 || value == creation))
				{
					owned.Remove(pid);
					List<BoostEntry> list = LoadEntries();
					if (list.RemoveAll((BoostEntry e) => e.Pid == pid && (creation <= 0 || e.Creation == creation)) > 0)
					{
						SaveEntries(list);
					}
				}
			}
		}

		internal static bool TrackBoostAffinity(int pid, long creation, ulong applied)
		{
			return TrackBoostAffinityTransition(pid, creation, applied, 0uL);
		}

		internal static bool TrackBoostAffinityTransition(int pid, long creation, ulong applied, ulong previous)
		{
			lock (sync)
			{
				List<BoostEntry> list = LoadEntries();
				foreach (BoostEntry item in list)
				{
					if (item.Pid == pid && item.Creation == creation)
					{
						if (item.PlacementYielded && applied != 0)
						{
							return false;
						}
						item.PlacementTracked = true;
						item.AppliedAffinity = applied;
						item.PreviousAffinity = previous;
						return SaveEntries(list);
					}
				}
				return false;
			}
		}

		internal static bool RestoreOwnedAffinity(IntPtr handle, ulong original, ulong applied)
		{
			if (applied == 0)
			{
				return true;
			}
			ulong mask;
			if (!Native.TryQueryAffinity(handle, out mask))
			{
				return false;
			}
			if (mask != applied)
			{
				return true;
			}
			if (Native.SetProcessAffinityMask(handle, (UIntPtr)original) && Native.TryQueryAffinity(handle, out mask))
			{
				return mask == original;
			}
			return false;
		}

		public static void YieldBoostPlacement(int pid, long creation)
		{
			lock (sync)
			{
				List<BoostEntry> list = LoadEntries();
				bool flag = false;
				foreach (BoostEntry item in list)
				{
					if (item.Pid == pid && item.Creation == creation && !item.PlacementYielded)
					{
						item.PlacementYielded = true;
						flag = true;
					}
				}
				if (flag)
				{
					SaveEntries(list);
				}
			}
		}

		public static void ClearBoost()
		{
			Settings.SaveStr("Crash_BoostMask", "");
			Settings.SaveStr("Crash_BoostNames", "");
		}

		public static bool HasPending()
		{
			if (Settings.LoadStr("Crash_BoostEntriesV2", "").Length <= 0)
			{
				return CoreThreadPlacement.HasResidue;
			}
			return true;
		}

		public static void HealFromCrash()
		{
			CoreThreadPlacement.Recover();
			UncleanThrottleAtLaunch |= Settings.LoadStr("Crash_ThrottleMasks", "").Length > 0;
			List<BoostEntry> list;
			lock (sync)
			{
				list = LoadEntries();
			}
			List<BoostEntry> list2 = new List<BoostEntry>();
			int num = 0;
			foreach (BoostEntry item in list)
			{
				IntPtr intPtr = Native.OpenProcess(12800, false, item.Pid);
				if (intPtr == IntPtr.Zero)
				{
					IntPtr intPtr2 = Native.OpenProcess(4096, false, item.Pid);
					if (intPtr2 != IntPtr.Zero)
					{
						try
						{
							if (Identify(intPtr2, item) != BoostIdentity.Mismatch)
							{
								list2.Add(item);
							}
						}
						finally
						{
							Native.CloseHandle(intPtr2);
						}
					}
					else if (!Native.LastOpenProcessFailureWasNoSuchProcess())
					{
						list2.Add(item);
					}
					continue;
				}
				try
				{
					switch (Identify(intPtr, item))
					{
					case BoostIdentity.Mismatch:
						continue;
					case BoostIdentity.Unknown:
						list2.Add(item);
						continue;
					}
					ulong affinity = item.Affinity;
					uint[] cpuSets = item.CpuSets;
					bool flag = !item.PlacementTracked || item.PlacementYielded || (RestoreOwnedAffinity(intPtr, affinity, item.AppliedAffinity) && RestoreOwnedAffinity(intPtr, affinity, item.PreviousAffinity));
					bool flag2 = SuppressionCore.RestoreValues(intPtr, item.Priority, affinity, item.Io, item.Page, CpuTopology.AllMask, cpuSets, item.QoSControl, item.QoSState, -1, -1, !item.PlacementTracked && !item.PlacementYielded) && flag;
					if (flag2 && item.Gpu >= 0)
					{
						flag2 = Native.D3DKMTSetProcessSchedulingPriorityClass(intPtr, item.Gpu) == 0;
					}
					if (flag2)
					{
						num++;
					}
					else
					{
						list2.Add(item);
					}
				}
				catch
				{
					list2.Add(item);
				}
				finally
				{
					Native.CloseHandle(intPtr);
				}
			}
			lock (sync)
			{
				SaveEntries(list2);
			}
			Settings.SaveStr("Crash_ThrottleMasks", "");
			Settings.SaveStr("Crash_BoostMask", "");
			Settings.SaveStr("Crash_BoostNames", "");
			if (num > 0)
			{
				Logger.Warn(Lang.T("log.crashguard.1") + num + Lang.T("log.crashguard.2"));
			}
			if (list2.Count > 0)
			{
				Logger.Warn(Lang.T("log.crashguard.3") + list2.Count + Lang.T("log.crashguard.4"));
			}
		}

		private static BoostIdentity Identify(IntPtr h, BoostEntry entry)
		{
			string text = Native.ImageName(h);
			if (text == null)
			{
				return BoostIdentity.Unknown;
			}
			if (!string.Equals(text, entry.Name, StringComparison.OrdinalIgnoreCase))
			{
				return BoostIdentity.Mismatch;
			}
			long creation;
			long cpu;
			ulong io;
			if (!Native.QueryProcessSample(h, out creation, out cpu, out io))
			{
				return BoostIdentity.Unknown;
			}
			if (creation != entry.Creation)
			{
				return BoostIdentity.Mismatch;
			}
			return BoostIdentity.Match;
		}

		private static List<BoostEntry> LoadEntries()
		{
			List<BoostEntry> list = new List<BoostEntry>();
			string text = Settings.LoadStr("Crash_BoostEntriesV2", "");
			string[] array = text.Split(new char[1] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string text2 in array)
			{
				string[] array2 = text2.TrimEnd('\r').Split('|');
				int result;
				long result2;
				uint result3;
				ulong result4;
				int result5;
				int result6;
				int result7;
				if ((array2.Length != 9 && array2.Length != 11 && array2.Length != 12 && array2.Length != 14 && array2.Length != 15) || (array2.Length >= 12 && array2[11] != "0" && array2[11] != "1") || !int.TryParse(array2[0], out result) || !long.TryParse(array2[1], out result2) || !uint.TryParse(array2[3], out result3) || !ulong.TryParse(array2[4], out result4) || !int.TryParse(array2[5], out result5) || !int.TryParse(array2[6], out result6) || !int.TryParse(array2[7], out result7))
				{
					continue;
				}
				uint[] array3 = ParseCpuSets(array2[8]);
				if (array3 == null)
				{
					continue;
				}
				string @string;
				try
				{
					@string = Encoding.UTF8.GetString(Convert.FromBase64String(array2[2]));
				}
				catch
				{
					continue;
				}
				int result8 = -1;
				int result9 = -1;
				if (array2.Length >= 11)
				{
					if (!int.TryParse(array2[9], out result8))
					{
						result8 = -1;
					}
					if (!int.TryParse(array2[10], out result9))
					{
						result9 = -1;
					}
				}
				ulong result10 = 0uL;
				ulong result11 = 0uL;
				bool flag = array2.Length >= 14 && array2[13] == "owned";
				if ((array2.Length < 14 || (flag && ulong.TryParse(array2[12], out result10))) && (array2.Length != 15 || ulong.TryParse(array2[14], out result11)))
				{
					list.Add(new BoostEntry
					{
						AppliedAffinity = result10,
						PreviousAffinity = result11,
						PlacementTracked = flag,
						Pid = result,
						Creation = result2,
						Name = @string,
						Priority = result3,
						Affinity = result4,
						Io = result5,
						Page = result6,
						Gpu = result7,
						CpuSets = array3,
						QoSControl = result8,
						QoSState = result9,
						PlacementYielded = (array2.Length >= 12 && array2[11] == "1")
					});
				}
			}
			return list;
		}

		private static bool SaveEntries(List<BoostEntry> entries)
		{
			List<string> list = new List<string>();
			foreach (BoostEntry entry in entries)
			{
				list.Add(entry.Pid + "|" + entry.Creation + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Name ?? "")) + "|" + entry.Priority + "|" + entry.Affinity + "|" + entry.Io + "|" + entry.Page + "|" + entry.Gpu + "|" + CpuSetsText(entry.CpuSets) + "|" + entry.QoSControl + "|" + entry.QoSState + (entry.PlacementTracked ? ("|" + (entry.PlacementYielded ? "1" : "0") + "|" + entry.AppliedAffinity + "|owned|" + entry.PreviousAffinity) : (entry.PlacementYielded ? "|1" : "")));
			}
			string text = string.Join("\n", list.ToArray());
			string value;
			if (Settings.SaveStrDurable("Crash_BoostEntriesV2", text) && Settings.TryLoadStr("Crash_BoostEntriesV2", out value))
			{
				return value == text;
			}
			return false;
		}

		private static string CpuSetsText(uint[] ids)
		{
			if (ids == null || ids.Length == 0)
			{
				return "";
			}
			string[] array = new string[ids.Length];
			for (int i = 0; i < ids.Length; i++)
			{
				array[i] = ids[i].ToString();
			}
			return string.Join(",", array);
		}

		private static uint[] ParseCpuSets(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return new uint[0];
			}
			string[] array = text.Split(',');
			uint[] array2 = new uint[array.Length];
			for (int i = 0; i < array.Length; i++)
			{
				if (!uint.TryParse(array[i], out array2[i]))
				{
					return null;
				}
			}
			return array2;
		}
	}
}
