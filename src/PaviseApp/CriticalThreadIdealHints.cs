using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	// Secondary threads use ideal-processor hints so the scheduler can migrate them when needed.
	// Restore only while the current value still matches the lease.
	// Callers hold criticalProcessGate; soft hints expire with the thread and need no persistent journal.
	internal sealed class CriticalThreadIdealHints
	{
		private const int QueryAccess = 0x0800;          // THREAD_QUERY_LIMITED_INFORMATION
		private const int WriteAccess = 0x0800 | 0x0020; // + THREAD_SET_INFORMATION

		private sealed class Hint
		{
			internal int Tid;
			internal long Creation;
			internal int Original;
			internal int Target;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct ProcessorNumber
		{
			internal ushort Group;
			internal byte Number;
			internal byte Reserved;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetThreadIdealProcessorEx(IntPtr thread, out ProcessorNumber ideal);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetThreadIdealProcessorEx(IntPtr thread, ref ProcessorNumber ideal, out ProcessorNumber previous);

		private readonly List<Hint> held = new List<Hint>();

		private int pid;

		private long birth;

		internal int Count
		{
			get { return held.Count; }
		}

		internal string Apply(int processId, long processBirth, HardThreadChoice[] choices)
		{
			string released = Release();
			pid = processId;
			birth = processBirth;
			List<string> parts = new List<string>();
			if (released.Length != 0)
			{
				parts.Add("previous " + released);
			}
			foreach (HardThreadChoice choice in choices ?? new HardThreadChoice[0])
			{
				if (choice == null || choice.Thread == null)
				{
					continue;
				}
				string label = "TID " + choice.Thread.Tid;
				int target = SingleLogical(choice.Mask);
				if (target < 0)
				{
					parts.Add(label + " skipped=mask");
					continue;
				}
				int original;
				int state = Read(choice.Thread.Tid, choice.Thread.Creation, out original);
				if (state != 0)
				{
					parts.Add(label + (state > 0 ? " skipped=gone" : " skipped=unreadable"));
					continue;
				}
				if (!Write(choice.Thread.Tid, choice.Thread.Creation, original, target))
				{
					parts.Add(label + " skipped=write");
					continue;
				}
				held.Add(new Hint { Tid = choice.Thread.Tid, Creation = choice.Thread.Creation, Original = original, Target = target });
				parts.Add(label + " ideal " + original + "->" + target);
			}
			return string.Join("; ", parts.ToArray());
		}

		// Release exited or externally changed hints; retain unreadable threads for later observation.
		internal string Audit()
		{
			List<string> parts = new List<string>();
			for (int i = held.Count - 1; i >= 0; i--)
			{
				Hint hint = held[i];
				int current;
				int state = Read(hint.Tid, hint.Creation, out current);
				if (state > 0)
				{
					held.RemoveAt(i);
					parts.Add("TID " + hint.Tid + " exited");
				}
				else if (state == 0 && current != hint.Target)
				{
					held.RemoveAt(i);
					parts.Add("TID " + hint.Tid + " yielded to game ideal=" + current);
				}
			}
			return string.Join("; ", parts.ToArray());
		}

		// Restore hints still matching Pavise's value, then release their records.
		// Unrestored soft hints expire with the thread and are not retried.
		internal string Release()
		{
			if (held.Count == 0)
			{
				return "";
			}
			List<string> parts = new List<string>();
			foreach (Hint hint in held)
			{
				int current;
				int state = Read(hint.Tid, hint.Creation, out current);
				string label = "TID " + hint.Tid;
				if (state > 0)
				{
					parts.Add(label + " exited");
				}
				else if (state < 0)
				{
					parts.Add(label + " unreadable");
				}
				else if (current != hint.Target)
				{
					parts.Add(label + " game-owned ideal=" + current);
				}
				else
				{
					parts.Add(label + (Write(hint.Tid, hint.Creation, hint.Target, hint.Original) ? " restored ideal=" + hint.Original : " restore-failed"));
				}
			}
			held.Clear();
			return string.Join("; ", parts.ToArray());
		}

		internal static int SingleLogical(ulong mask)
		{
			if (mask == 0 || (mask & (mask - 1)) != 0)
			{
				return -1;
			}
			int index = 0;
			while ((mask & 1) == 0)
			{
				mask >>= 1;
				index++;
			}
			return index;
		}

		// 0 = identity confirmed and value read, 1 = process or thread gone, -1 = unknown.
		private int Read(int tid, long creation, out int logical)
		{
			logical = -1;
			int process = NativeFrameThreads.ProcessState(pid, birth);
			if (process != 0)
			{
				return process;
			}
			IntPtr handle = Native.OpenThread(QueryAccess, false, tid);
			if (handle == IntPtr.Zero)
			{
				return Marshal.GetLastWin32Error() == 87 ? 1 : -1;
			}
			try
			{
				int identity = ThreadIdentity(handle, tid, creation);
				if (identity != 0)
				{
					return identity;
				}
				ProcessorNumber ideal;
				if (!GetThreadIdealProcessorEx(handle, out ideal) || ideal.Group != 0)
				{
					return -1;
				}
				logical = ideal.Number;
				return 0;
			}
			catch
			{
				return -1;
			}
			finally
			{
				Native.CloseHandle(handle);
			}
		}

		private bool Write(int tid, long creation, int expected, int desired)
		{
			if (expected < 0 || expected > 63 || desired < 0 || desired > 63 || NativeFrameThreads.ProcessState(pid, birth) != 0)
			{
				return false;
			}
			IntPtr handle = Native.OpenThread(WriteAccess, false, tid);
			if (handle == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				ProcessorNumber current;
				if (ThreadIdentity(handle, tid, creation) != 0 || !GetThreadIdealProcessorEx(handle, out current) || current.Group != 0 || current.Number != expected)
				{
					return false;
				}
				ProcessorNumber value = new ProcessorNumber { Group = 0, Number = (byte)desired };
				ProcessorNumber previous;
				if (!SetThreadIdealProcessorEx(handle, ref value, out previous))
				{
					return false;
				}
				if (previous.Group != 0 || previous.Number != expected)
				{
					// The game changed it between the read and the write: hand its value back.
					ProcessorNumber theirs = previous;
					ProcessorNumber ignored;
					SetThreadIdealProcessorEx(handle, ref theirs, out ignored);
					return false;
				}
				return GetThreadIdealProcessorEx(handle, out current) && current.Group == 0 && current.Number == desired;
			}
			catch
			{
				return false;
			}
			finally
			{
				Native.CloseHandle(handle);
			}
		}

		private int ThreadIdentity(IntPtr handle, int tid, long creation)
		{
			int owner = Native.QueryThreadOwnerPid(handle);
			if (owner < 0)
			{
				return -1;
			}
			if (owner != pid)
			{
				return 1;
			}
			long born, exited, kernel, user;
			if (!Native.GetThreadTimes(handle, out born, out exited, out kernel, out user))
			{
				return -1;
			}
			return (born != creation || exited != 0) ? 1 : 0;
		}
	}
}
