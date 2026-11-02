using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
	internal sealed class CriticalThreadHardAffinitySession : IDisposable
	{
		private sealed class RecoveryGate : IHardThreadGate
		{
			private readonly CriticalThreadGuardian.StopProof original;

			public HardThreadTarget Target
			{
				get
				{
					return original.Target;
				}
			}

			public bool AllowWorkers
			{
				get
				{
					return false;
				}
			}

			internal RecoveryGate(CriticalThreadGuardian.StopProof original)
			{
				if (original == null)
				{
					throw new ArgumentNullException("original");
				}
				this.original = original;
				original.RequireStopped();
			}

			public IDisposable EnterUse()
			{
				return original.EnterUse();
			}

			public void RequireActive()
			{
				throw new InvalidOperationException("original guardian recovery cannot authorize forward writes");
			}

			public void RequireRecovery()
			{
				original.RequireStopped();
			}
		}

		private sealed class NativeGate : IHardThreadGate
		{
			private readonly GameMode.CriticalThreadTargetLease original;

			private readonly HardThreadTarget target;

			public HardThreadTarget Target
			{
				get
				{
					return target;
				}
			}

			public bool AllowWorkers
			{
				get
				{
					return original.WorkerAvoidanceAllowed;
				}
			}

			internal NativeGate(GameMode.CriticalThreadTargetLease lease)
			{
				if (lease == null)
				{
					throw new ArgumentNullException("original");
				}
				original = lease;
				target = new HardThreadTarget(lease.SessionId, lease.ProcessId, lease.ProcessCreation, lease.ExecutablePath, lease.AllowedMask);
			}

			public IDisposable EnterUse()
			{
				return original.EnterUse();
			}

			public void RequireActive()
			{
				original.RequireActive();
			}

			public void RequireRecovery()
			{
				original.RequireRecovery();
			}
		}

		private sealed class CriticalThreadHardAffinityNative : IHardThreadNative, IDisposable, IHardThreadTransientReads
		{
			private struct GroupAffinity
			{
				internal UIntPtr Mask;

				internal ushort Group;

				internal ushort Reserved0;

				internal ushort Reserved1;

				internal ushort Reserved2;
			}

			private readonly HardThreadTarget target;

			private IntPtr process;

			private bool closeFault;

			private readonly Dictionary<string, IntPtr> threads = new Dictionary<string, IntPtr>(StringComparer.Ordinal);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern IntPtr OpenThread(uint access, bool inherit, int tid);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool CloseHandle(IntPtr handle);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern uint GetProcessIdOfThread(IntPtr handle);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool GetProcessTimes(IntPtr handle, out long born, out long exited, out long kernel, out long user);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool GetThreadTimes(IntPtr handle, out long born, out long exited, out long kernel, out long user);

			[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
			private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder name, ref int capacity);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool GetProcessAffinityMask(IntPtr handle, out UIntPtr processMask, out UIntPtr systemMask);

			[DllImport("kernel32.dll")]
			private static extern ushort GetActiveProcessorGroupCount();

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool GetThreadSelectedCpuSets(IntPtr thread, [Out] uint[] ids, uint capacity, out uint required);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool GetProcessDefaultCpuSets(IntPtr process, [Out] uint[] ids, uint capacity, out uint required);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool GetThreadSelectedCpuSetMasks(IntPtr thread, [Out] GroupAffinity[] masks, ushort capacity, out ushort required);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool GetProcessDefaultCpuSetMasks(IntPtr process, [Out] GroupAffinity[] masks, ushort capacity, out ushort required);

			private CriticalThreadHardAffinityNative(HardThreadTarget original, IntPtr retained)
			{
				if (IntPtr.Size != 8)
				{
					throw new NotSupportedException("x64 hard affinity adapter required");
				}
				target = original;
				process = ((retained != IntPtr.Zero) ? retained : OpenProcess(1053696u, false, target.ProcessId));
				if (process == IntPtr.Zero)
				{
					throw new InvalidOperationException("original process query denied: " + Marshal.GetLastWin32Error());
				}
				try
				{
					if (ProcessIdentity() == HardThreadRead.Unknown)
					{
						throw new InvalidOperationException("original process identity mismatch");
					}
				}
				catch
				{
					CloseHandle(process);
					process = IntPtr.Zero;
					throw;
				}
			}

			internal static CriticalThreadHardAffinityNative Open(HardThreadTarget original)
			{
				return new CriticalThreadHardAffinityNative(original, IntPtr.Zero);
			}

			internal static CriticalThreadHardAffinityNative Open(CriticalThreadGuardian.StopProof original)
			{
				return new CriticalThreadHardAffinityNative(original.Target, original.DuplicateTarget());
			}

			private HardThreadRead ProcessIdentity()
			{
				if (process == IntPtr.Zero)
				{
					return HardThreadRead.Unknown;
				}
				switch (WaitForSingleObject(process, 0u))
				{
				case 0u:
					return HardThreadRead.Gone;
				default:
					return HardThreadRead.Unknown;
				case 258u:
				{
					StringBuilder stringBuilder = new StringBuilder(32768);
					int capacity = stringBuilder.Capacity;
					long born;
					long exited;
					long kernel;
					long user;
					if (!GetProcessTimes(process, out born, out exited, out kernel, out user) || born != target.ProcessCreation || !QueryFullProcessImageName(process, 0u, stringBuilder, ref capacity) || !string.Equals(Path.GetFullPath(stringBuilder.ToString()), target.ExecutablePath, StringComparison.OrdinalIgnoreCase))
					{
						return HardThreadRead.Unknown;
					}
					return HardThreadRead.Known;
				}
				}
			}

			public HardThreadMachine ReadMachine()
			{
				HardThreadMachine hardThreadMachine = new HardThreadMachine();
				hardThreadMachine.Read = HardThreadRead.Unknown;
				hardThreadMachine.Detail = "native process or topology unavailable";
				HardThreadMachine hardThreadMachine2 = hardThreadMachine;
				hardThreadMachine2.Read = ProcessIdentity();
				if (hardThreadMachine2.Read != 0)
				{
					return hardThreadMachine2;
				}
				try
				{
					hardThreadMachine2.GroupCount = GetActiveProcessorGroupCount();
					if (hardThreadMachine2.GroupCount != 1)
					{
						hardThreadMachine2.Detail = "multiple processor groups unsupported";
						return hardThreadMachine2;
					}
					UIntPtr processMask;
					UIntPtr systemMask;
					if (!GetProcessAffinityMask(process, out processMask, out systemMask) || processMask == UIntPtr.Zero)
					{
						hardThreadMachine2.Read = HardThreadRead.Unknown;
						return hardThreadMachine2;
					}
					hardThreadMachine2.ProcessMask = processMask.ToUInt64();
					hardThreadMachine2.Group = 0;
					hardThreadMachine2.PhysicalCores = PhysicalCores();
					bool empty;
					bool empty2;
					if (!CpuSetIdsEmpty(process, false, out empty) || !CpuSetMasksEmpty(process, false, out empty2))
					{
						hardThreadMachine2.Read = HardThreadRead.Unknown;
						return hardThreadMachine2;
					}
					hardThreadMachine2.CpuSetsEmpty = empty && empty2;
					return hardThreadMachine2;
				}
				catch (Exception ex)
				{
					hardThreadMachine2.Read = HardThreadRead.Unknown;
					hardThreadMachine2.Detail = ex.GetType().Name;
					return hardThreadMachine2;
				}
			}

			private static ulong[] PhysicalCores()
			{
				uint length = 0u;
				if (GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length) || Marshal.GetLastWin32Error() != 122 || length < 48 || length > 1048576)
				{
					throw new InvalidDataException("bounded native physical topology required");
				}
				IntPtr intPtr = Marshal.AllocHGlobal((int)length);
				try
				{
					uint length2 = length;
					if (!GetLogicalProcessorInformationEx(0, intPtr, ref length2) || length2 > length)
					{
						throw new InvalidDataException("physical topology changed");
					}
					int i = 0;
					List<ulong> list = new List<ulong>();
					int num2;
					for (; i < length2; i += num2)
					{
						if (length2 - i < 8)
						{
							throw new InvalidDataException("truncated physical record");
						}
						IntPtr intPtr2 = IntPtr.Add(intPtr, i);
						int num = Marshal.ReadInt32(intPtr2);
						num2 = Marshal.ReadInt32(intPtr2, 4);
						if (num != 0 || num2 < 48 || num2 > length2 - i || Marshal.ReadInt16(intPtr2, 30) != 1)
						{
							throw new InvalidDataException("unsupported physical record");
						}
						GroupAffinity groupAffinity = (GroupAffinity)Marshal.PtrToStructure(IntPtr.Add(intPtr2, 32), typeof(GroupAffinity));
						if (groupAffinity.Group != 0 || groupAffinity.Mask == UIntPtr.Zero)
						{
							throw new InvalidDataException("unsupported physical group");
						}
						list.Add(groupAffinity.Mask.ToUInt64());
					}
					if (i != length2 || list.Count == 0)
					{
						throw new InvalidDataException("empty physical topology");
					}
					return list.ToArray();
				}
				finally
				{
					Marshal.FreeHGlobal(intPtr);
				}
			}

			private static bool CpuSetIdsEmpty(IntPtr handle, bool thread, out bool empty)
			{
				empty = false;
				uint required;
				bool flag = (thread ? GetThreadSelectedCpuSets(handle, null, 0u, out required) : GetProcessDefaultCpuSets(handle, null, 0u, out required));
				if ((!flag && Marshal.GetLastWin32Error() != 122) || required > 4096)
				{
					return false;
				}
				if (required == 0)
				{
					empty = flag;
					return flag;
				}
				uint[] ids = new uint[required];
				uint required2;
				if (!(thread ? GetThreadSelectedCpuSets(handle, ids, required, out required2) : GetProcessDefaultCpuSets(handle, ids, required, out required2)) || required2 > required)
				{
					return false;
				}
				empty = required2 == 0;
				return true;
			}

			private static bool CpuSetMasksEmpty(IntPtr handle, bool thread, out bool empty)
			{
				empty = false;
				try
				{
					ushort required;
					bool flag = (thread ? GetThreadSelectedCpuSetMasks(handle, null, 0, out required) : GetProcessDefaultCpuSetMasks(handle, null, 0, out required));
					if ((!flag && Marshal.GetLastWin32Error() != 122) || required > 64)
					{
						return false;
					}
					if (required == 0)
					{
						empty = flag;
						return flag;
					}
					GroupAffinity[] masks = new GroupAffinity[required];
					ushort required2;
					if (!(thread ? GetThreadSelectedCpuSetMasks(handle, masks, required, out required2) : GetProcessDefaultCpuSetMasks(handle, masks, required, out required2)) || required2 > required)
					{
						return false;
					}
					empty = required2 == 0;
					return true;
				}
				catch (EntryPointNotFoundException)
				{
					empty = true;
					return true;
				}
			}

			private IntPtr OriginalThread(HardThreadIdentity thread)
			{
				IntPtr value;
				if (threads.TryGetValue(thread.Key, out value))
				{
					return value;
				}
				value = OpenThread(1050656u, false, thread.Tid);
				if (value == IntPtr.Zero)
				{
					return IntPtr.Zero;
				}
				long born;
				long exited;
				long kernel;
				long user;
				if (GetProcessIdOfThread(value) != target.ProcessId || !GetThreadTimes(value, out born, out exited, out kernel, out user) || born != thread.Creation)
				{
					CloseHandle(value);
					return IntPtr.Zero;
				}
				threads.Add(thread.Key, value);
				return value;
			}

			public HardThreadObserved ReadThread(HardThreadIdentity thread)
			{
				HardThreadObserved hardThreadObserved = new HardThreadObserved();
				hardThreadObserved.Read = HardThreadRead.Unknown;
				hardThreadObserved.Detail = "original thread identity unknown";
				HardThreadObserved hardThreadObserved2 = hardThreadObserved;
				if (ProcessIdentity() != 0)
				{
					return hardThreadObserved2;
				}
				IntPtr intPtr = OriginalThread(thread);
				if (intPtr == IntPtr.Zero)
				{
					return hardThreadObserved2;
				}
				switch (WaitForSingleObject(intPtr, 0u))
				{
				case 0u:
					hardThreadObserved2.Read = HardThreadRead.Gone;
					return hardThreadObserved2;
				default:
					return hardThreadObserved2;
				case 258u:
					try
					{
						GroupAffinity affinity;
						bool empty;
						bool empty2;
						if (!GetThreadGroupAffinity(intPtr, out affinity) || !CpuSetIdsEmpty(intPtr, true, out empty) || !CpuSetMasksEmpty(intPtr, true, out empty2))
						{
							return hardThreadObserved2;
						}
						hardThreadObserved2.Read = HardThreadRead.Known;
						hardThreadObserved2.Group = affinity.Group;
						hardThreadObserved2.Mask = affinity.Mask.ToUInt64();
						hardThreadObserved2.CpuSetsEmpty = empty && empty2;
						return hardThreadObserved2;
					}
					catch (Exception ex)
					{
						hardThreadObserved2.Detail = ex.GetType().Name;
						return hardThreadObserved2;
					}
				}
			}

			public bool Set(HardThreadIdentity thread, ushort group, ulong mask, out ushort previousGroup, out ulong previousMask)
			{
				previousGroup = 0;
				previousMask = 0uL;
				if (ProcessIdentity() != 0 || group != 0 || mask == 0 || GetActiveProcessorGroupCount() != 1)
				{
					return false;
				}
				IntPtr value;
				if (!threads.TryGetValue(thread.Key, out value) || WaitForSingleObject(value, 0u) != 258)
				{
					return false;
				}
				GroupAffinity groupAffinity = default(GroupAffinity);
				groupAffinity.Mask = new UIntPtr(mask);
				groupAffinity.Group = group;
				GroupAffinity affinity = groupAffinity;
				GroupAffinity previous;
				if (!SetThreadGroupAffinity(value, ref affinity, out previous))
				{
					return false;
				}
				previousGroup = previous.Group;
				previousMask = previous.Mask.ToUInt64();
				return true;
			}

			public bool ReleaseUnowned(string[] retainedIdentities)
			{
				HashSet<string> hashSet = new HashSet<string>(retainedIdentities, StringComparer.Ordinal);
				List<string> list = new List<string>();
				bool result = true;
				foreach (string key in threads.Keys)
				{
					if (!hashSet.Contains(key))
					{
						list.Add(key);
					}
				}
				foreach (string item in list)
				{
					IntPtr handle = threads[item];
					threads.Remove(item);
					if (!CloseHandle(handle))
					{
						result = false;
						closeFault = true;
					}
				}
				return result;
			}

			public void Dispose()
			{
				bool flag = false;
				foreach (IntPtr value in threads.Values)
				{
					if (!CloseHandle(value))
					{
						flag = true;
					}
				}
				threads.Clear();
				if (process != IntPtr.Zero)
				{
					if (!CloseHandle(process))
					{
						flag = true;
					}
					process = IntPtr.Zero;
				}
				closeFault |= flag;
				if (closeFault)
				{
					throw new InvalidOperationException("some retained hard-affinity handles did not close");
				}
			}
		}

		private readonly IHardThreadGate gate;

		private readonly IHardThreadNative native;

		private readonly IHardThreadStore store;

		private readonly IHardThreadGuardian guardian;

		private HardThreadJournal journal;

		private HardThreadPlan plan;

		private volatile bool disposed;

		private volatile bool resourcesClosed;

		private bool nativeClosed;

		private bool storeClosed;

		private bool guardianClosed;

		private readonly object factsGate = new object();

		private readonly Dictionary<string, HardThreadOwnership> verified = new Dictionary<string, HardThreadOwnership>(StringComparer.Ordinal);

		private volatile HardThreadFailureKind lastFailureKind;

		private volatile bool lastOperationWriteIntent;

		private volatile bool lastOperationNativeAttempt;

		private volatile HardThreadStatus currentStatus;

		private volatile string currentDetail = "";

		internal bool ResourcesClosed
		{
			get
			{
				return resourcesClosed;
			}
		}

		internal HardThreadFailureKind LastFailureKind
		{
			get
			{
				return lastFailureKind;
			}
		}

		internal bool LastOperationWriteIntent
		{
			get
			{
				return lastOperationWriteIntent;
			}
		}

		internal bool LastOperationNativeAttempt
		{
			get
			{
				return lastOperationNativeAttempt;
			}
		}

		internal HardThreadOwnership[] ActiveReceipts
		{
			get
			{
				lock (factsGate)
				{
					HardThreadOwnership[] array = new HardThreadOwnership[verified.Count];
					verified.Values.CopyTo(array, 0);
					return array;
				}
			}
		}

		internal HardThreadGuardianActor GuardianActor
		{
			get
			{
				if (guardian != null)
				{
					return guardian.Actor;
				}
				return null;
			}
		}

		internal HardThreadStatus Status
		{
			get
			{
				return currentStatus;
			}
			private set
			{
				currentStatus = value;
			}
		}

		internal string Detail
		{
			get
			{
				return currentDetail;
			}
			private set
			{
				currentDetail = value;
			}
		}

		internal string LastReassertion { get; private set; }

		private CriticalThreadHardAffinitySession(IHardThreadGate gate, IHardThreadNative native, IHardThreadStore store, IHardThreadGuardian guardian = null)
		{
			this.gate = gate;
			this.native = native;
			this.store = store;
			this.guardian = guardian;
			journal = store.Load(gate.Target);
			Status = (Pending() ? HardThreadStatus.RecoveryPending : HardThreadStatus.Ready);
			if (guardian == null)
			{
				return;
			}
			guardian.OnLost(delegate
			{
				if (!disposed)
				{
					try
					{
						Restore();
					}
					catch (ObjectDisposedException)
					{
					}
					catch
					{
						Status = HardThreadStatus.RecoveryPending;
					}
				}
			});
		}

		internal static CriticalThreadHardAffinitySession OpenNative()
		{
			throw new NotSupportedException("original GameMode target lease bridge is required");
		}

		internal static CriticalThreadHardAffinitySession OpenNative(GameMode.CriticalThreadTargetLease original)
		{
			NativeGate nativeGate = new NativeGate(original);
			using (nativeGate.EnterUse())
			{
				nativeGate.RequireRecovery();
				HardThreadFileStore originalStore;
				IHardThreadGuardian hardThreadGuardian = CriticalThreadGuardian.Arm(original, out originalStore);
				CriticalThreadHardAffinityNative criticalThreadHardAffinityNative = null;
				try
				{
					criticalThreadHardAffinityNative = CriticalThreadHardAffinityNative.Open(nativeGate.Target);
					return new CriticalThreadHardAffinitySession(nativeGate, criticalThreadHardAffinityNative, originalStore, hardThreadGuardian);
				}
				catch
				{
					try
					{
						HardThreadJournal hardThreadJournal = originalStore.Load(nativeGate.Target);
						hardThreadGuardian.NoteSettled(originalStore.CaptureSettled(nativeGate.Target, hardThreadJournal.Revision));
					}
					catch
					{
					}
					try
					{
						if (criticalThreadHardAffinityNative != null)
						{
							criticalThreadHardAffinityNative.Dispose();
						}
					}
					finally
					{
						try
						{
							if (originalStore != null)
							{
								originalStore.Dispose();
							}
						}
						finally
						{
							hardThreadGuardian.Dispose();
						}
					}
					throw;
				}
			}
		}

		internal static CriticalThreadHardAffinitySession OpenRecovery(CriticalThreadGuardian.StopProof original)
		{
			RecoveryGate recoveryGate = new RecoveryGate(original);
			using (recoveryGate.EnterUse())
			{
				recoveryGate.RequireRecovery();
				CriticalThreadHardAffinityNative criticalThreadHardAffinityNative = CriticalThreadHardAffinityNative.Open(original);
				HardThreadFileStore hardThreadFileStore = null;
				try
				{
					hardThreadFileStore = new HardThreadFileStore(original.CanonicalStore);
					hardThreadFileStore.BindOriginalScope(original.Target, original.SourceNonce, original.SourceScopeHash);
					return new CriticalThreadHardAffinitySession(recoveryGate, criticalThreadHardAffinityNative, hardThreadFileStore);
				}
				catch
				{
					try
					{
						criticalThreadHardAffinityNative.Dispose();
					}
					finally
					{
						if (hardThreadFileStore != null)
						{
							hardThreadFileStore.Dispose();
						}
					}
					throw;
				}
			}
		}

		private bool Pending()
		{
			foreach (HardThreadReceipt row in journal.Rows)
			{
				if (!row.Terminal)
				{
					return true;
				}
			}
			return false;
		}

		private void Save()
		{
			long revision = journal.Revision;
			HardThreadJournal hardThreadJournal = journal.Clone();
			hardThreadJournal.Revision = checked(revision + 1);
			lastOperationWriteIntent = true;
			store.Save(revision, hardThreadJournal);
			journal = hardThreadJournal;
		}

		private void Confirm(HardThreadReceipt row)
		{
			lock (factsGate)
			{
				verified[row.Thread.Key] = new HardThreadOwnership(row.Thread, row.Group, row.Applied, DateTime.UtcNow.Ticks);
			}
		}

		private void Forget(HardThreadIdentity thread)
		{
			lock (factsGate)
			{
				verified.Remove(thread.Key);
			}
		}

		private void BeginOperation()
		{
			if (disposed)
			{
				throw new ObjectDisposedException("hard thread session");
			}
			lastFailureKind = HardThreadFailureKind.None;
			lastOperationWriteIntent = false;
			lastOperationNativeAttempt = false;
			LastReassertion = null;
		}

		private void RequireForwardAuthority()
		{
			try
			{
				gate.RequireActive();
				if (guardian != null)
				{
					guardian.RequireForward();
				}
			}
			catch (Exception ex)
			{
				throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, ex.Message, true);
			}
		}

		private void Reload()
		{
			journal = store.Load(gate.Target);
		}

		private HardThreadReceipt Find(HardThreadIdentity thread)
		{
			foreach (HardThreadReceipt row in journal.Rows)
			{
				if (!row.Terminal && row.Thread.Key == thread.Key)
				{
					return row;
				}
			}
			return null;
		}

		internal bool Apply(HardThreadPlan requested, HardThreadIdentity[] threads, HardThreadCoverage coverage)
		{
			if (threads != null)
			{
				threads = (HardThreadIdentity[])threads.Clone();
			}
			return RunForward(delegate
			{
				RequireForwardAuthority();
				ReloadForForward();
				if (Pending())
				{
					throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original recovery is pending");
				}
				if (requested == null || threads == null)
				{
					throw InvalidRequest("plan or threads are missing");
				}
				if (!requested.Target.Same(gate.Target))
				{
					throw InvalidRequest("plan belongs to another original target");
				}
				if (requested.WorkerMode == HardThreadWorkerMode.AvoidWorkers && !gate.AllowWorkers)
				{
					throw InvalidRequest("original lease does not authorize worker avoidance");
				}
				HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
				HardThreadIdentity[] array = threads;
				foreach (HardThreadIdentity hardThreadIdentity in array)
				{
					if (hardThreadIdentity == null || !hashSet.Add(hardThreadIdentity.Key))
					{
						throw InvalidRequest("duplicate thread identity");
					}
				}
				HardThreadChoice[] choices = requested.Choices;
				foreach (HardThreadChoice hardThreadChoice in choices)
				{
					if (!hashSet.Contains(hardThreadChoice.Thread.Key))
					{
						throw InvalidRequest("candidate absent from original thread input");
					}
				}
				plan = requested;
				return ApplyThreads(threads, coverage);
			});
		}

		internal HardThreadPlan CreatePlan(HardThreadChoice[] choices)
		{
			if (disposed)
			{
				throw new ObjectDisposedException("hard thread session");
			}
			using (gate.EnterUse())
			{
				BeginOperation();
				try
				{
					RequireForwardAuthority();
					HardThreadMachine hardThreadMachine = native.ReadMachine();
					if (hardThreadMachine == null || hardThreadMachine.Read != 0)
					{
						throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "original process observation unavailable");
					}
					return HardThreadPlan.Create(gate.Target, hardThreadMachine, choices, gate.AllowWorkers ? HardThreadWorkerMode.AvoidWorkers : HardThreadWorkerMode.CandidatesOnly);
				}
				catch (Exception ex)
				{
					HardThreadOperationException ex2 = ex as HardThreadOperationException;
					lastFailureKind = ((ex2 == null) ? HardThreadFailureKind.InvalidRequest : ex2.Kind);
					if (ex is NotSupportedException)
					{
						Status = HardThreadStatus.Unsupported;
					}
					Detail = ex.Message;
					throw;
				}
			}
		}

		internal bool AdmitNewThreads(HardThreadIdentity[] threads, HardThreadCoverage coverage)
		{
			if (threads != null)
			{
				threads = (HardThreadIdentity[])threads.Clone();
			}
			return RunForward(delegate
			{
				RequireForwardAuthority();
				ReloadForForward();
				if (Status == HardThreadStatus.RecoveryPending)
				{
					throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original owned recovery is pending");
				}
				if (plan == null || (Status != HardThreadStatus.Applied && Status != HardThreadStatus.CoveragePending && Status != HardThreadStatus.AppliedCandidates))
				{
					throw InvalidRequest("no active original plan");
				}
				ValidateInput(threads, coverage);
				string text = null;
				int num = 0;
				for (int i = 0; i < journal.Rows.Count; i++)
				{
					HardThreadReceipt hardThreadReceipt = journal.Rows[i];
					if (!hardThreadReceipt.Terminal)
					{
						HardThreadObserved hardThreadObserved = native.ReadThread(hardThreadReceipt.Thread);
						if (hardThreadReceipt.Phase != HardThreadPhase.Applied || hardThreadObserved == null || hardThreadObserved.Read == HardThreadRead.Unknown)
						{
							Forget(hardThreadReceipt.Thread);
							throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "owned thread observation is unavailable");
						}
						if (hardThreadObserved.Read == HardThreadRead.Gone)
						{
							Forget(hardThreadReceipt.Thread);
							hardThreadReceipt.Phase = HardThreadPhase.Gone;
							Save();
							if (plan.IsCandidate(hardThreadReceipt.Thread))
							{
								throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "original candidate exited", true);
							}
							coverage = HardThreadCoverage.Partial;
						}
						else
						{
							if (!hardThreadObserved.CpuSetsEmpty || hardThreadObserved.Group != hardThreadReceipt.Group || hardThreadObserved.Mask != hardThreadReceipt.Applied)
							{
								string text2 = text ?? DescribeExternalChange(hardThreadReceipt, hardThreadObserved, i);
								string text3 = ReassertOwned(hardThreadReceipt, hardThreadObserved);
								if (text3 != null)
								{
									Forget(hardThreadReceipt.Thread);
									throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "owned thread state changed externally (" + text3 + "): " + text2, true);
								}
								text = text2;
								num++;
							}
							Confirm(hardThreadReceipt);
						}
					}
				}
				if (text != null)
				{
					LastReassertion = text + "; written back " + num;
				}
				return ApplyThreads(threads, coverage);
			});
		}

		private string ReassertOwned(HardThreadReceipt row, HardThreadObserved current)
		{
			if (!current.CpuSetsEmpty)
			{
				return "thread CPU Sets assigned";
			}
			if (current.Group != row.Group)
			{
				return "processor group changed";
			}
			if (!row.PreviousKnown || (row.Applied & ~gate.Target.AllowedMask) != 0)
			{
				return "receipt cannot be written back";
			}
			HardThreadMachine hardThreadMachine = native.ReadMachine();
			if (hardThreadMachine == null || hardThreadMachine.Read != 0 || hardThreadMachine.GroupCount != 1 || hardThreadMachine.Group != plan.Group || hardThreadMachine.ProcessMask != plan.ProcessMask || !hardThreadMachine.CpuSetsEmpty)
			{
				return "process scope changed";
			}
			RequireForwardAuthority();
			lastOperationNativeAttempt = true;
			ushort previousGroup;
			ulong previousMask;
			if (!native.Set(row.Thread, row.Group, row.Applied, out previousGroup, out previousMask))
			{
				return "write-back refused";
			}
			HardThreadObserved hardThreadObserved = native.ReadThread(row.Thread);
			if (hardThreadObserved == null || hardThreadObserved.Read != 0 || !hardThreadObserved.CpuSetsEmpty || hardThreadObserved.Group != row.Group || hardThreadObserved.Mask != row.Applied)
			{
				return "write-back readback unconfirmed";
			}
			return null;
		}

		private string DescribeExternalChange(HardThreadReceipt row, HardThreadObserved current, int index)
		{
			int num = 0;
			int num2 = 1;
			for (int i = 0; i < journal.Rows.Count; i++)
			{
				HardThreadReceipt hardThreadReceipt = journal.Rows[i];
				if (hardThreadReceipt.Terminal)
				{
					continue;
				}
				num++;
				if (i <= index || hardThreadReceipt.Phase != HardThreadPhase.Applied)
				{
					continue;
				}
				try
				{
					HardThreadObserved hardThreadObserved = native.ReadThread(hardThreadReceipt.Thread);
					if (hardThreadObserved != null && hardThreadObserved.Read == HardThreadRead.Known && (!hardThreadObserved.CpuSetsEmpty || hardThreadObserved.Group != hardThreadReceipt.Group || hardThreadObserved.Mask != hardThreadReceipt.Applied))
					{
						num2++;
					}
				}
				catch
				{
				}
			}
			return (plan.IsCandidate(row.Thread) ? "candidate" : "worker") + " TID " + row.Thread.Tid + " applied=0x" + row.Applied.ToString("X") + " observed=0x" + current.Mask.ToString("X") + ((current.Mask == plan.ProcessMask) ? " (process mask)" : "") + ((current.Group != row.Group) ? (" group " + row.Group + "->" + current.Group) : "") + (current.CpuSetsEmpty ? "" : " thread CPU Sets assigned") + "; " + num2 + "/" + num + " owned threads changed";
		}

		private bool RunForward(Func<bool> action)
		{
			if (disposed)
			{
				throw new ObjectDisposedException("hard thread session");
			}
			try
			{
				using (gate.EnterUse())
				{
					BeginOperation();
					try
					{
						return action();
					}
					catch (ObjectDisposedException)
					{
						throw;
					}
					catch (Exception error)
					{
						return FailOperation(error);
					}
					finally
					{
						PruneTransientReads();
					}
				}
			}
			catch (ObjectDisposedException)
			{
				throw;
			}
			catch (Exception ex3)
			{
				lastFailureKind = HardThreadFailureKind.AuthorityUnavailable;
				lastOperationWriteIntent = false;
				lastOperationNativeAttempt = false;
				Detail = "original coordination gate unavailable: " + ex3.Message;
				return false;
			}
		}

		private static HardThreadOperationException InvalidRequest(string message)
		{
			return new HardThreadOperationException(HardThreadFailureKind.InvalidRequest, message);
		}

		private void PruneTransientReads()
		{
			IHardThreadTransientReads hardThreadTransientReads = native as IHardThreadTransientReads;
			if (hardThreadTransientReads == null)
			{
				return;
			}
			try
			{
				List<string> list = new List<string>();
				foreach (HardThreadReceipt row in journal.Rows)
				{
					if (!row.Terminal)
					{
						list.Add(row.Thread.Key);
					}
				}
				if (!hardThreadTransientReads.ReleaseUnowned(list.ToArray()))
				{
					Detail += "; some transient query handles did not close";
				}
			}
			catch
			{
				Detail += "; transient query cleanup unavailable";
			}
		}

		private void ReloadForForward()
		{
			try
			{
				Reload();
			}
			catch (Exception ex)
			{
				throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original ledger cannot be observed: " + ex.Message);
			}
		}

		private bool FailOperation(Exception error)
		{
			HardThreadOperationException ex = error as HardThreadOperationException;
			HardThreadFailureKind hardThreadFailureKind = ((ex == null) ? HardThreadFailureKind.WriteOrDurabilityFault : ex.Kind);
			bool flag = ex == null || ex.Recover;
			string message = error.Message;
			if (flag)
			{
				if (RestoreInternal(false) && hardThreadFailureKind == HardThreadFailureKind.WriteOrDurabilityFault)
				{
					Status = HardThreadStatus.Conflict;
				}
				Detail = message + "; " + Detail;
			}
			else
			{
				if (hardThreadFailureKind == HardThreadFailureKind.RecoveryPending)
				{
					Status = HardThreadStatus.RecoveryPending;
				}
				else if (!Pending())
				{
					Status = ((hardThreadFailureKind == HardThreadFailureKind.InvalidRequest) ? HardThreadStatus.Conflict : HardThreadStatus.Stopped);
				}
				Detail = message;
			}
			lastFailureKind = hardThreadFailureKind;
			return false;
		}

		private void ValidateInput(HardThreadIdentity[] threads, HardThreadCoverage coverage)
		{
			if (threads == null || threads.Length > 512 || !Enum.IsDefined(typeof(HardThreadCoverage), coverage))
			{
				throw InvalidRequest("bounded known thread input required");
			}
			if (plan.WorkerMode == HardThreadWorkerMode.AvoidWorkers && !gate.AllowWorkers)
			{
				throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, "original worker authority revoked", true);
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			int num = 0;
			int num2 = 0;
			foreach (HardThreadIdentity hardThreadIdentity in threads)
			{
				if (hardThreadIdentity == null || !hashSet.Add(hardThreadIdentity.Key))
				{
					throw InvalidRequest("duplicate new thread identity");
				}
				if (!plan.IsCandidate(hardThreadIdentity))
				{
					if (coverage == HardThreadCoverage.CandidatesOnly)
					{
						throw InvalidRequest("candidate coverage cannot include workers");
					}
					if (!gate.AllowWorkers || plan.WorkerMode != HardThreadWorkerMode.AvoidWorkers)
					{
						throw InvalidRequest("original candidate-only scope refuses worker identities");
					}
					if (Find(hardThreadIdentity) == null)
					{
						num++;
					}
				}
				if (Find(hardThreadIdentity) == null)
				{
					num2++;
				}
			}
			if (num > 8 || journal.Rows.Count + num2 > 512)
			{
				throw InvalidRequest("bounded worker batch required");
			}
		}

		private bool ApplyThreads(HardThreadIdentity[] threads, HardThreadCoverage coverage)
		{
			ValidateInput(threads, coverage);
			HardThreadMachine hardThreadMachine = native.ReadMachine();
			if (hardThreadMachine == null || hardThreadMachine.Read != 0)
			{
				throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "original process observation unavailable", Pending());
			}
			if (hardThreadMachine.GroupCount != 1 || hardThreadMachine.Group != plan.Group || !hardThreadMachine.CpuSetsEmpty || hardThreadMachine.ProcessMask != plan.ProcessMask || (gate.Target.AllowedMask & ~hardThreadMachine.ProcessMask) != 0)
			{
				throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, "original process scope changed", Pending());
			}
			HardThreadPlan hardThreadPlan;
			try
			{
				hardThreadPlan = HardThreadPlan.Create(gate.Target, hardThreadMachine, plan.Choices, plan.WorkerMode);
			}
			catch (Exception ex)
			{
				throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, "native topology cannot admit the original plan: " + ex.Message, Pending());
			}
			if (hardThreadPlan.Workers != plan.Workers || hardThreadPlan.ProcessMask != plan.ProcessMask)
			{
				throw InvalidRequest("plan physical topology differs from the native admission");
			}
			HardThreadChoice[] choices = plan.Choices;
			foreach (HardThreadChoice hardThreadChoice in choices)
			{
				if (Find(hardThreadChoice.Thread) == null)
				{
					HardThreadObserved hardThreadObserved = native.ReadThread(hardThreadChoice.Thread);
					if (hardThreadObserved == null || hardThreadObserved.Read != 0 || !hardThreadObserved.CpuSetsEmpty || hardThreadObserved.Group != plan.Group || hardThreadObserved.Mask != plan.ProcessMask)
					{
						throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "original candidate is unavailable or externally constrained", Pending());
					}
				}
			}
			bool flag = coverage != HardThreadCoverage.Complete && coverage != HardThreadCoverage.CandidatesOnly;
			foreach (HardThreadIdentity thread in threads)
			{
				if (Find(thread) != null)
				{
					continue;
				}
				RequireForwardAuthority();
				HardThreadObserved hardThreadObserved2 = native.ReadThread(thread);
				bool flag2 = plan.IsCandidate(thread);
				if (hardThreadObserved2 == null || hardThreadObserved2.Read != 0 || !hardThreadObserved2.CpuSetsEmpty || hardThreadObserved2.Group != plan.Group || hardThreadObserved2.Mask != plan.ProcessMask)
				{
					if (flag2)
					{
						throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "candidate changed before admission", Pending());
					}
					flag = true;
					lastFailureKind = HardThreadFailureKind.ObservationUnavailable;
					continue;
				}
				ulong num = plan.MaskFor(thread);
				if (num == hardThreadObserved2.Mask)
				{
					continue;
				}
				HardThreadReceipt hardThreadReceipt = new HardThreadReceipt();
				hardThreadReceipt.Thread = thread;
				hardThreadReceipt.Group = hardThreadObserved2.Group;
				hardThreadReceipt.Captured = hardThreadObserved2.Mask;
				hardThreadReceipt.Applied = num;
				hardThreadReceipt.Phase = HardThreadPhase.ApplyIntent;
				HardThreadReceipt item = hardThreadReceipt;
				journal.Rows.Add(item);
				bool flag3 = false;
				try
				{
					Save();
					item = journal.Rows[journal.Rows.Count - 1];
					RequireForwardAuthority();
					hardThreadObserved2 = native.ReadThread(thread);
					hardThreadMachine = native.ReadMachine();
					if (hardThreadMachine == null || hardThreadMachine.Read != 0 || hardThreadMachine.GroupCount != 1 || hardThreadMachine.Group != plan.Group || hardThreadMachine.ProcessMask != plan.ProcessMask || !hardThreadMachine.CpuSetsEmpty || (item.Applied & ~gate.Target.AllowedMask) != 0)
					{
						throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, "process write admission changed", true);
					}
					if (hardThreadObserved2 == null || hardThreadObserved2.Read != 0 || !hardThreadObserved2.CpuSetsEmpty || hardThreadObserved2.Group != item.Group || hardThreadObserved2.Mask != item.Captured)
					{
						throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "thread changed before native write", flag2);
					}
					RequireForwardAuthority();
					flag3 = true;
					lastOperationNativeAttempt = true;
					ushort previousGroup;
					ulong previousMask;
					if (!native.Set(thread, item.Group, item.Applied, out previousGroup, out previousMask))
					{
						throw new InvalidOperationException("native apply is unconfirmed");
					}
					item.PreviousKnown = true;
					item.PreviousGroup = previousGroup;
					item.Previous = previousMask;
					item.Phase = HardThreadPhase.PreviousRecorded;
					Save();
					item = journal.Rows[journal.Rows.Count - 1];
					if (previousGroup != item.Group || previousMask != item.Captured)
					{
						item.Detail = "external race at native apply";
						Save();
						throw new InvalidOperationException("apply previous differs from captured value");
					}
					hardThreadObserved2 = native.ReadThread(thread);
					if (hardThreadObserved2.Read != 0 || !hardThreadObserved2.CpuSetsEmpty || hardThreadObserved2.Group != item.Group || hardThreadObserved2.Mask != item.Applied)
					{
						throw new InvalidOperationException("native apply readback is unconfirmed");
					}
					item.Phase = HardThreadPhase.Applied;
					Save();
					Confirm(journal.Rows[journal.Rows.Count - 1]);
				}
				catch (Exception ex2)
				{
					bool flag4 = false;
					if (!flag3)
					{
						try
						{
							Reload();
							HardThreadReceipt hardThreadReceipt2 = Find(thread);
							if (hardThreadReceipt2 != null && hardThreadReceipt2.Phase == HardThreadPhase.ApplyIntent && !hardThreadReceipt2.PreviousKnown)
							{
								hardThreadReceipt2.Phase = HardThreadPhase.NotApplied;
								Save();
								flag4 = true;
							}
						}
						catch
						{
						}
					}
					HardThreadOperationException ex3 = ex2 as HardThreadOperationException;
					if (!flag3 && flag4 && !flag2 && ex3 != null && ex3.Kind == HardThreadFailureKind.ObservationUnavailable)
					{
						flag = true;
						lastFailureKind = HardThreadFailureKind.ObservationUnavailable;
						continue;
					}
					if (!flag3 && !flag4 && ex3 != null && ex3.Kind == HardThreadFailureKind.ObservationUnavailable)
					{
						throw new HardThreadOperationException(HardThreadFailureKind.WriteOrDurabilityFault, "unwritten thread intent could not be settled", true);
					}
					throw;
				}
			}
			Status = ((plan.WorkerMode == HardThreadWorkerMode.CandidatesOnly) ? HardThreadStatus.AppliedCandidates : ((!flag) ? HardThreadStatus.Applied : HardThreadStatus.CoveragePending));
			Detail = ((Status == HardThreadStatus.AppliedCandidates) ? "only original candidates are bound; worker placement is unchanged" : ((!flag) ? "provided snapshot threads verified; future workers are not instantly exclusive" : "thread lifecycle coverage is incomplete"));
			return true;
		}

		internal bool Restore()
		{
			return RestoreInternal(true);
		}

		private bool RestoreInternal(bool resetOperation)
		{
			if (disposed)
			{
				throw new ObjectDisposedException("hard thread session");
			}
			try
			{
				using (gate.EnterUse())
				{
					try
					{
						if (disposed)
						{
							return false;
						}
						if (resetOperation)
						{
							BeginOperation();
						}
						lock (factsGate)
						{
							verified.Clear();
						}
						gate.RequireRecovery();
						Reload();
						HardThreadMachine hardThreadMachine = native.ReadMachine();
						if (hardThreadMachine.Read == HardThreadRead.Unknown)
						{
							throw new InvalidOperationException("original process identity is unknown");
						}
						for (int num = journal.Rows.Count - 1; num >= 0; num--)
						{
							HardThreadReceipt hardThreadReceipt = journal.Rows[num];
							if (!hardThreadReceipt.Terminal)
							{
								hardThreadMachine = native.ReadMachine();
								if (hardThreadMachine.Read != HardThreadRead.Unknown)
								{
									if (hardThreadMachine.Read == HardThreadRead.Gone)
									{
										hardThreadReceipt.Phase = HardThreadPhase.Gone;
										Save();
									}
									else
									{
										HardThreadObserved hardThreadObserved = native.ReadThread(hardThreadReceipt.Thread);
										if (hardThreadObserved.Read == HardThreadRead.Gone)
										{
											hardThreadReceipt.Phase = HardThreadPhase.Gone;
											Save();
										}
										else if (hardThreadObserved.Read == HardThreadRead.Known && hardThreadObserved.CpuSetsEmpty && hardThreadMachine.CpuSetsEmpty && hardThreadReceipt.PreviousKnown && (hardThreadReceipt.Phase != HardThreadPhase.RestoreIntent || hardThreadReceipt.RestorePreviousKnown))
										{
											if (hardThreadReceipt.RestorePreviousKnown)
											{
												bool flag = hardThreadReceipt.RestorePreviousGroup == hardThreadReceipt.Group && hardThreadReceipt.RestorePrevious == hardThreadReceipt.Applied;
												ulong num2 = (flag ? hardThreadReceipt.Previous : hardThreadReceipt.RestorePrevious);
												ushort num3 = (flag ? hardThreadReceipt.PreviousGroup : hardThreadReceipt.RestorePreviousGroup);
												if (hardThreadObserved.Group == num3 && hardThreadObserved.Mask == num2)
												{
													hardThreadReceipt.Phase = HardThreadPhase.Restored;
													Save();
												}
											}
											else if (hardThreadObserved.Group == hardThreadReceipt.PreviousGroup && hardThreadObserved.Mask == hardThreadReceipt.Previous)
											{
												hardThreadReceipt.Phase = HardThreadPhase.Restored;
												Save();
											}
											else if (hardThreadObserved.Group == hardThreadReceipt.Group && hardThreadObserved.Mask == hardThreadReceipt.Applied && hardThreadReceipt.PreviousGroup == hardThreadMachine.Group && (hardThreadReceipt.Previous & ~hardThreadMachine.ProcessMask) == 0)
											{
												gate.RequireRecovery();
												hardThreadReceipt.Phase = HardThreadPhase.RestoreIntent;
												hardThreadReceipt.RestorePreviousKnown = false;
												Save();
												hardThreadReceipt = journal.Rows[num];
												hardThreadObserved = native.ReadThread(hardThreadReceipt.Thread);
												hardThreadMachine = native.ReadMachine();
												if (hardThreadObserved.Read != 0 || hardThreadObserved.Group != hardThreadReceipt.Group || hardThreadObserved.Mask != hardThreadReceipt.Applied || !hardThreadObserved.CpuSetsEmpty || hardThreadMachine.Read != 0 || hardThreadMachine.GroupCount != 1 || hardThreadMachine.Group != hardThreadReceipt.PreviousGroup || !hardThreadMachine.CpuSetsEmpty || (hardThreadReceipt.Previous & ~hardThreadMachine.ProcessMask) != 0)
												{
													hardThreadReceipt.Phase = HardThreadPhase.Applied;
													Save();
												}
												else
												{
													lastOperationNativeAttempt = true;
													ushort previousGroup;
													ulong previousMask;
													if (native.Set(hardThreadReceipt.Thread, hardThreadReceipt.PreviousGroup, hardThreadReceipt.Previous, out previousGroup, out previousMask))
													{
														hardThreadReceipt.RestorePreviousKnown = true;
														hardThreadReceipt.RestorePreviousGroup = previousGroup;
														hardThreadReceipt.RestorePrevious = previousMask;
														hardThreadReceipt.Phase = HardThreadPhase.RestorePreviousRecorded;
														Save();
														hardThreadReceipt = journal.Rows[num];
														hardThreadObserved = native.ReadThread(hardThreadReceipt.Thread);
														if (previousGroup == hardThreadReceipt.Group && previousMask == hardThreadReceipt.Applied && hardThreadObserved.Read == HardThreadRead.Known && hardThreadObserved.Group == hardThreadReceipt.PreviousGroup && hardThreadObserved.Mask == hardThreadReceipt.Previous)
														{
															hardThreadReceipt.Phase = HardThreadPhase.Restored;
															Save();
														}
													}
												}
											}
										}
									}
								}
							}
						}
						bool flag2 = !Pending();
						Status = (flag2 ? HardThreadStatus.Stopped : HardThreadStatus.RecoveryPending);
						Detail = (flag2 ? "original owned changes restored or exact actors exited" : "recovery pending; unknown previous or external conflict is not overwritten");
						if (resetOperation)
						{
							lastFailureKind = ((!flag2) ? HardThreadFailureKind.RecoveryPending : HardThreadFailureKind.None);
						}
						if (flag2)
						{
							plan = null;
							IHardThreadSettledStore hardThreadSettledStore = store as IHardThreadSettledStore;
							if (guardian != null && hardThreadSettledStore != null)
							{
								guardian.NoteSettled(hardThreadSettledStore.CaptureSettled(gate.Target, journal.Revision));
							}
						}
						return flag2;
					}
					catch (Exception ex)
					{
						Detail = ex.Message;
						Status = HardThreadStatus.RecoveryPending;
						if (resetOperation)
						{
							lastFailureKind = HardThreadFailureKind.RecoveryPending;
						}
						return false;
					}
					finally
					{
						PruneTransientReads();
					}
				}
			}
			catch (Exception ex2)
			{
				Detail = ex2.Message;
				Status = HardThreadStatus.RecoveryPending;
				if (resetOperation)
				{
					lastFailureKind = HardThreadFailureKind.RecoveryPending;
				}
				return false;
			}
		}

		public void Dispose()
		{
			using (gate.EnterUse())
			{
				if (resourcesClosed)
				{
					return;
				}
				disposed = true;
				lock (factsGate)
				{
					verified.Clear();
				}
				if (Pending())
				{
					Status = HardThreadStatus.RecoveryPending;
					Detail = "unrestored original ledger retained on disposal";
				}
				List<Exception> list = new List<Exception>();
				if (!nativeClosed)
				{
					try
					{
						native.Dispose();
						nativeClosed = true;
					}
					catch (Exception item)
					{
						list.Add(item);
					}
				}
				if (!storeClosed)
				{
					try
					{
						store.Dispose();
						storeClosed = true;
					}
					catch (Exception item2)
					{
						list.Add(item2);
					}
				}
				if (guardian == null)
				{
					guardianClosed = true;
				}
				else if (storeClosed && !guardianClosed)
				{
					try
					{
						guardian.Dispose();
						guardianClosed = guardian.ResourcesClosed;
					}
					catch (Exception item3)
					{
						list.Add(item3);
					}
				}
				resourcesClosed = nativeClosed && storeClosed && guardianClosed;
				if (list.Count <= 0)
				{
					return;
				}
				throw new AggregateException("original hard affinity resource closure is incomplete", list);
			}
		}
	}
}
