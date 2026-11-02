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
			string[] array = raw.Split(';');
			if (array.Length < 2 || array.Length > 3 || array[0] != "v1")
			{
				return false;
			}
			HashSet<int> hashSet = new HashSet<int>();
			for (int i = 1; i < array.Length; i++)
			{
				string[] array2 = array[i].Split('|');
				FrameThreadReceipt frameThreadReceipt = new FrameThreadReceipt();
				if (array2.Length != 5 || !int.TryParse(array2[0], out frameThreadReceipt.Pid) || !long.TryParse(array2[1], out frameThreadReceipt.ProcessBirth) || !int.TryParse(array2[2], out frameThreadReceipt.Tid) || !long.TryParse(array2[3], out frameThreadReceipt.ThreadBirth) || !int.TryParse(array2[4], out frameThreadReceipt.Original) || frameThreadReceipt.Pid <= 0 || frameThreadReceipt.ProcessBirth <= 0 || frameThreadReceipt.Tid <= 0 || frameThreadReceipt.ThreadBirth <= 0 || frameThreadReceipt.Original < 1 || frameThreadReceipt.Original >= 5 || !hashSet.Add(frameThreadReceipt.Tid))
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
				string text = string.Join(";", list3.ToArray());
				if (!alive())
				{
					return false;
				}
				string value;
				if (!Settings.SaveStrDurable("FrameMemoryProtectionJournalV1", text) || !Settings.TryLoadStr("FrameMemoryProtectionJournalV1", out value) || text != value)
				{
					executionFailure = "Memory protection recovery record could not be saved or verified";
					return false;
				}
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
						int num = platform.ReadMemory(item3, out priority2);
						if (num <= 0 && (num < 0 || priority2 == item3.Original || priority2 == 5))
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

		private static bool RestoreLocked()
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
			bool flag = true;
			foreach (FrameThreadReceipt item in rows)
			{
				int priority;
				int num = platform.ReadMemory(item, out priority);
				if (num < 0)
				{
					flag = false;
				}
				else if (num == 0 && priority == 5 && !platform.SetMemory(item, 5, item.Original))
				{
					flag = false;
				}
			}
			string value2;
			if (flag && Settings.TryLoadStr("FrameMemoryProtectionJournalV1", out value2) && value2 == value && Settings.SaveStr("FrameMemoryProtectionJournalV1", "") && Settings.TryLoadStr("FrameMemoryProtectionJournalV1", out value2))
			{
				return value2 == "";
			}
			return false;
		}
	}
}
