using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class FrameMemoryProtection
	{
		internal const string JournalKey = "FrameMemoryProtectionJournalV1";

		[ThreadStatic]
		private static string executionFailure;

		private static readonly object gate = new object();

		private static IFrameThreadMemory platform = new NativeFrameThreads();

		internal static string ExecutionFailure
		{
			get
			{
				return executionFailure;
			}
		}

		internal static bool HasResidue
		{
			get
			{
				string value;
				if (Settings.TryLoadStr("FrameMemoryProtectionJournalV1", out value))
				{
					return value.Length > 0;
				}
				return true;
			}
		}

		private static bool Parse(string raw, out List<FrameThreadReceipt> rows)
		{
			rows = new List<FrameThreadReceipt>();
			if (raw == "")
			{
				return true;
			}
			string[] rawParts = raw.Split(';');
			if (rawParts.Length < 2 || rawParts.Length > 3 || rawParts[0] != "v1")
			{
				return false;
			}
			HashSet<int> hashSet = new HashSet<int>();
			for (int i = 1; i < rawParts.Length; i++)
			{
				string[] parts = rawParts[i].Split('|');
				FrameThreadReceipt frameThreadReceipt = new FrameThreadReceipt();
				if (parts.Length != 5 || !int.TryParse(parts[0], out frameThreadReceipt.Pid) || !long.TryParse(parts[1], out frameThreadReceipt.ProcessBirth) || !int.TryParse(parts[2], out frameThreadReceipt.Tid) || !long.TryParse(parts[3], out frameThreadReceipt.ThreadBirth) || !int.TryParse(parts[4], out frameThreadReceipt.Original) || frameThreadReceipt.Pid <= 0 || frameThreadReceipt.ProcessBirth <= 0 || frameThreadReceipt.Tid <= 0 || frameThreadReceipt.ThreadBirth <= 0 || frameThreadReceipt.Original < 1 || frameThreadReceipt.Original >= 5 || !hashSet.Add(frameThreadReceipt.Tid))
				{
					return false;
				}
				if (rows.Count > 0 && (frameThreadReceipt.Pid != rows[0].Pid || frameThreadReceipt.ProcessBirth != rows[0].ProcessBirth))
				{
					return false;
				}
				rows.Add(frameThreadReceipt);
			}
			return true;
		}

		internal static bool Apply(List<FrameThreadReceipt> selected, IFrameThreadMemory adapter, Func<bool> alive)
		{
			lock (gate)
			{
				executionFailure = null;
				if (!RestoreLocked())
				{
					executionFailure = "Previous memory protection restoration is incomplete";
					return false;
				}
				if (!OvernightPerformancePolicy.Enabled)
				{
					return true;
				}
				if (adapter == null || selected == null || selected.Count == 0 || !alive() || !adapter.UnderPressure(selected[0].Pid, selected[0].ProcessBirth))
				{
					return true;
				}
				List<FrameThreadReceipt> list = new List<FrameThreadReceipt>();
				foreach (FrameThreadReceipt item in selected)
				{
					int priority;
					if (item.Original >= -2 && item.Original < 1 && adapter.ReadMemory(item, out priority) == 0 && priority >= 1 && priority < 5)
					{
						list.Add(new FrameThreadReceipt
						{
							Pid = item.Pid,
							ProcessBirth = item.ProcessBirth,
							Tid = item.Tid,
							ThreadBirth = item.ThreadBirth,
							Original = priority
						});
					}
				}
				if (list.Count == 0)
				{
					return true;
				}
				List<string> list2 = new List<string>();
				list2.Add("v1");
				List<string> list3 = list2;
				foreach (FrameThreadReceipt item2 in list)
				{
					list3.Add(item2.Encode());
				}
				string joinResult = string.Join(";", list3.ToArray());
				if (!alive())
				{
					return false;
				}
				string value;
				if (!Settings.SaveStrDurable("FrameMemoryProtectionJournalV1", joinResult) || !Settings.TryLoadStr("FrameMemoryProtectionJournalV1", out value) || joinResult != value)
				{
					executionFailure = "Memory protection recovery record could not be saved or verified";
					return false;
				}
				FrameRecoveryBudget.Own("memory", selected[0].Pid, selected[0].ProcessBirth);
				platform = adapter;
				foreach (FrameThreadReceipt item3 in list)
				{
					if (!alive())
					{
						RestoreLocked();
						return false;
					}
					if (!platform.SetMemory(item3, item3.Original, 5))
					{
						int priority2;
						int memory = platform.ReadMemory(item3, out priority2);
						if (memory <= 0 && (memory < 0 || priority2 == item3.Original || priority2 == 5))
						{
							executionFailure = "Memory protection apply or readback failed";
						}
						RestoreLocked();
						return false;
					}
				}
				if (!alive())
				{
					RestoreLocked();
					return false;
				}
				return true;
			}
		}

		internal static bool Healthy()
		{
			lock (gate)
			{
				string value;
				List<FrameThreadReceipt> rows;
				if (!Settings.TryLoadStr("FrameMemoryProtectionJournalV1", out value) || !Parse(value, out rows))
				{
					return false;
				}
				foreach (FrameThreadReceipt item in rows)
				{
					int priority;
					if (platform.ReadMemory(item, out priority) != 0 || priority != 5)
					{
						return false;
					}
				}
				return true;
			}
		}

		internal static bool Restore()
		{
			lock (gate)
			{
				return RestoreLocked();
			}
		}

		internal static bool HealthyOutsideReservation()
		{
			lock (ThreadPriorityReservation.Gate)
			lock (gate)
			{
				string raw;
				List<FrameThreadReceipt> rows;
				if (!Settings.TryLoadStr(JournalKey, out raw) || !Parse(raw, out rows)) return false;
				foreach (FrameThreadReceipt row in rows)
				{
					if (ThreadPriorityReservation.Contains(row.Pid, row.ProcessBirth, row.Tid, row.ThreadBirth)) continue;
					int priority;
					if (platform.ReadMemory(row, out priority) != 0 || priority != 5) return false;
				}
				return true;
			}
		}

		internal static bool ReleaseReservedPriority()
		{
			if (!HasResidue) return true;
			return FrameRecoveryBudget.Run("memory", ReleaseReservedPriorityCore,
				delegate { string raw; return Settings.TryLoadStr(JournalKey, out raw) && raw.Length == 0; });
		}

		private static bool ReleaseReservedPriorityCore()
		{
			lock (ThreadPriorityReservation.Gate)
			lock (gate)
			{
				string raw;
				List<FrameThreadReceipt> rows;
				if (!Settings.TryLoadStr(JournalKey, out raw) || !Parse(raw, out rows)) return false;
				List<string> remaining = new List<string> { "v1" };
				bool matched = false;
				foreach (FrameThreadReceipt row in rows)
				{
					if (!ThreadPriorityReservation.Contains(row.Pid, row.ProcessBirth, row.Tid, row.ThreadBirth))
					{
						remaining.Add(row.Encode());
						continue;
					}
					matched = true;
					int priority;
					int read = platform.ReadMemory(row, out priority);
					if (read < 0 || (read == 0 && priority == 5 && !platform.SetMemory(row, 5, row.Original))) return false;
				}
				if (!matched) return true;
				string next = remaining.Count == 1 ? "" : string.Join(";", remaining.ToArray());
				string check;
				return Settings.TryLoadStr(JournalKey, out check) && check == raw && Settings.SaveStrDurable(JournalKey, next)
					&& Settings.TryLoadStr(JournalKey, out check) && check == next;
			}
		}

		private static bool RestoreLocked()
		{
			if (!HasResidue) return true;
			return FrameRecoveryBudget.Run("memory", RestoreCore);
		}

		private static bool RestoreCore()
		{
			string value;
			List<FrameThreadReceipt> rows;
			if (!Settings.TryLoadStr("FrameMemoryProtectionJournalV1", out value) || !Parse(value, out rows))
			{
				return false;
			}
			if (value == "")
			{
				return true;
			}
			bool restoreCoreReady = true;
			foreach (FrameThreadReceipt item in rows)
			{
				int priority;
				int memory = platform.ReadMemory(item, out priority);
				if (memory < 0)
				{
					restoreCoreReady = false;
				}
				else if (memory == 0 && priority == 5 && !platform.SetMemory(item, 5, item.Original))
				{
					restoreCoreReady = false;
				}
			}
			string value2;
			if (restoreCoreReady && Settings.TryLoadStr("FrameMemoryProtectionJournalV1", out value2) && value2 == value && Settings.SaveStr("FrameMemoryProtectionJournalV1", "") && Settings.TryLoadStr("FrameMemoryProtectionJournalV1", out value2))
			{
				return value2 == "";
			}
			return false;
		}
	}
}
