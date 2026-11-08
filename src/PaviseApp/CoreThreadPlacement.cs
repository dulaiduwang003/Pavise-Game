using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class CoreThreadPlacement
	{
		private static readonly object recoveryGate = new object();
		private static readonly AutomaticRecoveryBudget recoveryBudget = new AutomaticRecoveryBudget();
		private static long recoveryDue;
		private sealed class Receipt
		{
			internal int Pid;

			internal int Tid;

			internal long ProcessCreation;

			internal long ThreadCreation;

			internal uint[] Original;

			internal uint[] Applied;

			internal IntPtr Handle;
		}

		internal const string JournalKey = "AutoCoreThreadPlacementV1";

		private static int journalGeneration = -1;

		private static bool unownedPending;

		internal static bool UnownedPending
		{
			get
			{
				int mutationGeneration = Settings.MutationGeneration;
				if (journalGeneration != mutationGeneration)
				{
					unownedPending = HasResidue;
					journalGeneration = mutationGeneration;
				}
				return unownedPending;
			}
		}

		internal static bool HasResidue
		{
			get
			{
				string value;
				if (Settings.TryLoadStr("AutoCoreThreadPlacementV1", out value))
				{
					return value.Length != 0;
				}
				return true;
			}
		}

		internal static bool Same(uint[] a, uint[] b)
		{
			if (a != null && b != null && a.Length == b.Length)
			{
				return new HashSet<uint>(a).SetEquals(b);
			}
			return false;
		}

		private static bool RestoreReceipt(Receipt r)
		{
			if (r.Tid != 0 && Native.WaitForSingleObject(r.Handle, 0u) == 0)
			{
				return true;
			}
			if (r.Tid == 0 && !Native.StillActive(r.Handle))
			{
				return true;
			}
			uint[] array = ((r.Tid == 0) ? Native.QueryCpuSets(r.Handle) : Native.QueryThreadCpuSets(r.Handle));
			if (array == null)
			{
				return false;
			}
			if (!Same(array, r.Applied))
			{
				return true;
			}
			if ((r.Tid == 0) ? Native.RestoreCpuSets(r.Handle, r.Original) : Native.WriteThreadCpuSets(r.Handle, r.Original))
			{
				return Same((r.Tid == 0) ? Native.QueryCpuSets(r.Handle) : Native.QueryThreadCpuSets(r.Handle), r.Original);
			}
			return false;
		}

		internal static bool Recover()
		{
			lock (recoveryGate)
			{
				if (recoveryBudget.BeginCycle(PerformanceTuning.RecoveryCycleSerial + "\nlegacy-core-thread")) recoveryDue = 0;
				if (!recoveryBudget.CanAttempt) { RecoveryAttemptTrace.Exhausted(); return false; }
				if (DateTime.UtcNow.Ticks < recoveryDue) { RecoveryAttemptTrace.DeferredUntil(recoveryDue); return false; }
				bool restored = false;
				try { restored = RecoverCore(); return restored; }
				catch { return false; }
				finally
				{
					if (restored) { recoveryBudget.CompleteRecovery(); recoveryDue = 0; }
					else
					{
						recoveryBudget.Record(false);
						recoveryDue = DateTime.UtcNow.AddSeconds(30).Ticks;
						RecoveryAttemptTrace.FailureSchedule(recoveryBudget.FailureCount, 3, recoveryDue);
						Logger.Warn("Legacy core placement recovery failure=" + recoveryBudget.FailureCount + "/3; journal retained");
					}
				}
			}
		}

		private static bool RecoverCore()
		{
			string value;
			if (!Settings.TryLoadStr("AutoCoreThreadPlacementV1", out value))
			{
				return false;
			}
			if (value.Length == 0)
			{
				return true;
			}
			if (value.Length > 65536)
			{
				return false;
			}
			List<string> list = new List<string>();
			string[] array = value.Split(';');
			Array.Reverse(array);
			string[] array2 = array;
			foreach (string text in array2)
			{
				IntPtr intPtr = IntPtr.Zero;
				IntPtr intPtr2 = IntPtr.Zero;
				try
				{
					string[] array3 = text.Split('|');
					if (array3.Length != 6)
					{
						list.Add(text);
						continue;
					}
					Receipt receipt = new Receipt();
					receipt.Pid = int.Parse(array3[0]);
					receipt.ProcessCreation = long.Parse(array3[1]);
					receipt.Tid = int.Parse(array3[2]);
					receipt.ThreadCreation = long.Parse(array3[3]);
					receipt.Original = ParseIds(array3[4]);
					receipt.Applied = ParseIds(array3[5]);
					Receipt receipt2 = receipt;
					if (receipt2.Pid <= 0 || receipt2.ProcessCreation <= 0 || receipt2.Tid < 0 || (receipt2.Tid > 0 && receipt2.ThreadCreation <= 0) || receipt2.Applied.Length == 0)
					{
						list.Add(text);
						continue;
					}
					intPtr = Native.OpenProcess(0x1000 | ((receipt2.Tid == 0) ? 8704 : 0), false, receipt2.Pid);
					long creation;
					long cpu;
					ulong io;
					if (intPtr == IntPtr.Zero)
					{
						if (Marshal.GetLastWin32Error() != 87)
						{
							list.Add(text);
						}
					}
					else if (!Native.QueryProcessSample(intPtr, out creation, out cpu, out io))
					{
						list.Add(text);
					}
					else
					{
						if (creation != receipt2.ProcessCreation || !Native.StillActive(intPtr))
						{
							continue;
						}
						if (receipt2.Tid == 0)
						{
							receipt2.Handle = intPtr;
							goto RestoreOwnedPlacement;
						}
						intPtr2 = Native.OpenThread(1051648, false, receipt2.Tid);
						long exit;
						long kernel;
						long user;
						if (intPtr2 == IntPtr.Zero)
						{
							if (Marshal.GetLastWin32Error() != 87)
							{
								list.Add(text);
							}
						}
						else if (!Native.GetThreadTimes(intPtr2, out creation, out exit, out kernel, out user))
						{
							list.Add(text);
						}
						else if (exit == 0 && creation == receipt2.ThreadCreation && Native.QueryThreadOwnerPid(intPtr2) == receipt2.Pid)
						{
							receipt2.Handle = intPtr2;
							goto RestoreOwnedPlacement;
						}
						continue;
					}
					goto FinishReceiptInspection;
					RestoreOwnedPlacement:
					if (!RestoreReceipt(receipt2))
					{
						list.Add(text);
					}
					FinishReceiptInspection:;
				}
				catch
				{
					list.Add(text);
				}
				finally
				{
					if (intPtr2 != IntPtr.Zero)
					{
						Native.CloseHandle(intPtr2);
					}
					if (intPtr != IntPtr.Zero)
					{
						Native.CloseHandle(intPtr);
					}
				}
			}
			list.Reverse();
			if (Settings.SaveStrDurable("AutoCoreThreadPlacementV1", string.Join(";", list)))
			{
				return list.Count == 0;
			}
			return false;
		}

		private static uint[] ParseIds(string value)
		{
			if (value.Length != 0)
			{
				return Array.ConvertAll(value.Split(','), uint.Parse);
			}
			return new uint[0];
		}
	}
}
