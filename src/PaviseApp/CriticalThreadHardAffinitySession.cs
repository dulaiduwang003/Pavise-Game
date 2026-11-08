using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
	internal interface IHardThreadPlacementReads
	{
		HardThreadObserved ReadPlacement(HardThreadIdentity thread);
	}

	internal interface IHardThreadPlacementBatch : IHardThreadPlacementReads, IDisposable
	{
		void Complete();
	}

	internal interface IHardThreadPlacementBatchReads
	{
		IHardThreadPlacementBatch BeginPlacementBatch();
	}

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

		private sealed class CriticalThreadHardAffinityNative : IHardThreadNative, IDisposable, IHardThreadTransientReads, IHardThreadCandidateReads, IHardThreadPlacementReads, IHardThreadPlacementBatchReads
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

			private PlacementBatch placementBatch;

			private sealed class PlacementBatch : IHardThreadPlacementBatch
			{
				private readonly CriticalThreadHardAffinityNative owner;
				private readonly IntPtr originalProcess;
				private readonly int ownerThread;
				private volatile bool closed;

				internal PlacementBatch(CriticalThreadHardAffinityNative owner)
				{
					this.owner = owner;
					originalProcess = owner.process;
					ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
				}

				private bool Current
				{
					get { return !closed && ownerThread == System.Threading.Thread.CurrentThread.ManagedThreadId
						&& originalProcess != IntPtr.Zero && owner.process == originalProcess
						&& object.ReferenceEquals(System.Threading.Volatile.Read(ref owner.placementBatch), this); }
				}

				internal HardThreadRead Alive()
				{
					if (!Current) return HardThreadRead.Unknown;
					uint result = WaitForSingleObject(originalProcess, 0u);
					return result == 258u ? HardThreadRead.Known : result == 0u ? HardThreadRead.Gone : HardThreadRead.Unknown;
				}

				internal void RequireIdentity()
				{
					if (!Current || owner.ProcessIdentity() != HardThreadRead.Known)
						throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original process identity unavailable during placement audit");
				}

				public HardThreadObserved ReadPlacement(HardThreadIdentity thread)
				{
					return owner.ReadThreadCore(thread, false, this);
				}

				public void Complete()
				{
					try { RequireIdentity(); }
					finally { Dispose(); }
				}

				public void Dispose()
				{
					closed = true;
					System.Threading.Interlocked.CompareExchange(ref owner.placementBatch, null, this);
				}
			}

			public IHardThreadPlacementBatch BeginPlacementBatch()
			{
				var batch = new PlacementBatch(this);
				if (System.Threading.Interlocked.CompareExchange(ref placementBatch, batch, null) != null)
					throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "placement audit already active");
				try { batch.RequireIdentity(); return batch; }
				catch { batch.Dispose(); throw; }
			}

			private bool closeFault;

			private readonly StringBuilder processPath = new StringBuilder(32768);

			private static readonly HardThreadOptionalEntry threadCpuSetMasks = new HardThreadOptionalEntry();

			private static readonly HardThreadOptionalEntry processCpuSetMasks = new HardThreadOptionalEntry();

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
			private static extern int GetThreadPriority(IntPtr thread);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern uint GetPriorityClass(IntPtr process);

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
					processPath.Length = 0;
					int capacity = processPath.Capacity;
					long born;
					long exited;
					long kernel;
					long user;
					if (!GetProcessTimes(process, out born, out exited, out kernel, out user) || born != target.ProcessCreation || !QueryFullProcessImageName(process, 0u, processPath, ref capacity) || !string.Equals(Path.GetFullPath(processPath.ToString()), target.ExecutablePath, StringComparison.OrdinalIgnoreCase))
					{
						return HardThreadRead.Unknown;
					}
					return HardThreadRead.Known;
				}
				}
			}

			public HardThreadMachine ReadMachine()
			{
				HardThreadMachine machine = ReadProcessScope();
				if (machine.Read != HardThreadRead.Known || machine.GroupCount != 1) return machine;
				try { machine.PhysicalCores = PhysicalCores(); }
				catch (Exception error) { machine.Read = HardThreadRead.Unknown; machine.Detail = error.GetType().Name; }
				return machine;
			}

			public HardThreadMachine ReadProcessScope()
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
					bool empty;
					bool empty2;
					if (!CpuSetIdsEmpty(process, false, out empty) || !CpuSetMasksEmpty(process, false, out empty2))
					{
						hardThreadMachine2.Read = HardThreadRead.Unknown;
						return hardThreadMachine2;
					}
					hardThreadMachine2.CpuSetsEmpty = empty && empty2;
					try
					{
						uint priority = GetPriorityClass(process);
						if (priority != 0) { hardThreadMachine2.ProcessPriorityClass = priority; hardThreadMachine2.ProcessPriorityClassKnown = true; }
					}
					catch { }
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
				bool observedEmpty = false, unsupported;
				HardThreadOptionalEntry entry = thread ? threadCpuSetMasks : processCpuSetMasks;
				bool read = entry.TryInvoke(delegate { return ReadCpuSetMasks(handle, thread, out observedEmpty); }, out unsupported);
				empty = unsupported || observedEmpty;
				return read;
			}

			private static bool ReadCpuSetMasks(IntPtr handle, bool thread, out bool empty)
			{
				empty = false;
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
				return ReadThreadCore(thread, true);
			}

			public HardThreadObserved ReadPlacement(HardThreadIdentity thread)
			{
				return ReadThreadCore(thread, false);
			}

			private HardThreadObserved ReadThreadCore(HardThreadIdentity thread, bool includePriority)
			{
				return ReadThreadCore(thread, includePriority, null);
			}

			private HardThreadObserved ReadThreadCore(HardThreadIdentity thread, bool includePriority, PlacementBatch batch)
			{
				HardThreadObserved hardThreadObserved = new HardThreadObserved();
				hardThreadObserved.Read = HardThreadRead.Unknown;
				hardThreadObserved.Detail = "original thread identity unknown";
				HardThreadObserved hardThreadObserved2 = hardThreadObserved;
				if ((batch == null ? ProcessIdentity() : batch.Alive()) != HardThreadRead.Known)
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
						if (includePriority) try
						{
							int priority = GetThreadPriority(intPtr);
							if (priority != int.MaxValue) { hardThreadObserved2.ThreadPriorityLevel = priority; hardThreadObserved2.ThreadPriorityKnown = true; }
						}
						catch { }
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

		private Dictionary<string, int> receiptIndex;

		private HardThreadIdentity pendingApplied;

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

		private bool reassertionAdmitted;

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

		internal Func<bool> BeforeReassertion { get; set; }

		private CriticalThreadHardAffinitySession(IHardThreadGate gate, IHardThreadNative native, IHardThreadStore store, IHardThreadGuardian guardian = null)
		{
			this.gate = gate;
			this.native = native;
			this.store = store;
			this.guardian = guardian;
			Reload();
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
			SaveJournal(journal.Clone());
		}

		private void SaveJournal(HardThreadJournal next)
		{
			long revision = journal.Revision;
			next.Revision = checked(revision + 1);
			lastOperationWriteIntent = true;
			store.Save(revision, next);
			// Clone preserves row order and thread identities; positions also address the new rows.
			journal = next;
			if (pendingApplied != null)
			{
				HardThreadReceipt completed = Find(pendingApplied);
				if (completed != null && completed.Phase == HardThreadPhase.Applied) Confirm(completed);
				else Forget(pendingApplied);
				pendingApplied = null;
			}
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
			reassertionAdmitted = false;
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
			HardThreadJournal loaded = store.Load(gate.Target);
			Dictionary<string, int> loadedIndex = IndexReceipts(loaded);
			journal = loaded;
			receiptIndex = loadedIndex;
			pendingApplied = null;
		}

		private Dictionary<string, int> IndexReceipts(HardThreadJournal source)
		{
			if (source == null || source.Target == null || !source.Target.Same(gate.Target)
				|| source.Revision < 0 || source.Rows.Count > 512)
				throw new InvalidDataException("journal identity or bounds");
			var index = new Dictionary<string, int>(StringComparer.Ordinal);
			for (int position = 0; position < source.Rows.Count; position++)
			{
				HardThreadReceipt row = source.Rows[position];
				if (row == null || row.Thread == null || (uint)row.Phase > (uint)HardThreadPhase.NotApplied
					|| row.Captured == 0 || row.Applied == 0 || (row.Applied & ~source.Target.AllowedMask) != 0
					|| (row.PreviousKnown && row.Previous == 0)
					|| (row.RestorePreviousKnown && (!row.PreviousKnown || row.RestorePrevious == 0)))
					throw new InvalidDataException("journal receipt contract");
				if ((row.Phase == HardThreadPhase.PreviousRecorded || row.Phase == HardThreadPhase.Applied
					|| row.Phase == HardThreadPhase.RestoreIntent || row.Phase == HardThreadPhase.RestorePreviousRecorded
					|| row.Phase == HardThreadPhase.Restored) && !row.PreviousKnown)
					throw new InvalidDataException("journal missing native previous");
				if (row.Phase == HardThreadPhase.RestorePreviousRecorded && !row.RestorePreviousKnown)
					throw new InvalidDataException("journal missing restore previous");
				string key = row.Thread.Key;
				if (index.ContainsKey(key)) throw new InvalidDataException("overlapping thread receipts");
				// A terminal history row may precede a later live receipt for the same actor.
				if (!row.Terminal) index.Add(key, position);
			}
			return index;
		}

		private HardThreadReceipt Find(HardThreadIdentity thread)
		{
			int position;
			if (thread == null || !receiptIndex.TryGetValue(thread.Key, out position)) return null;
			if (position < 0 || position >= journal.Rows.Count)
				throw new InvalidDataException("thread receipt position changed");
			HardThreadReceipt row = journal.Rows[position];
			if (row == null || row.Thread == null || row.Thread.Tid != thread.Tid || row.Thread.Creation != thread.Creation)
				throw new InvalidDataException("thread receipt identity changed");
			return row.Terminal ? null : row;
		}

		private void AddReceipt(HardThreadReceipt row)
		{
			if (row == null || row.Thread == null || journal.Rows.Count >= 512 || Find(row.Thread) != null)
				throw new InvalidDataException("duplicate or unbounded thread receipt");
			journal.Rows.Add(row);
			receiptIndex[row.Thread.Key] = journal.Rows.Count - 1;
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

		internal HardThreadPlan CreatePlan(HardThreadChoice[] choices, bool requireWorkerCoverage = false)
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
					return HardThreadPlan.Create(gate.Target, hardThreadMachine, choices, gate.AllowWorkers ? HardThreadWorkerMode.AvoidWorkers : HardThreadWorkerMode.CandidatesOnly, requireWorkerCoverage);
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

		internal HardThreadCandidateAudit AuditCandidates(out string detail)
		{
			detail = "";
			try
			{
				using (gate.EnterUse())
				{
					BeginOperation();
					RequireForwardAuthority();
					IHardThreadCandidateReads reads = native as IHardThreadCandidateReads;
					if (reads == null || plan == null || plan.WorkerMode != HardThreadWorkerMode.CandidatesOnly
						|| Status != HardThreadStatus.AppliedCandidates || journal == null || journal.Revision <= 0
						|| journal.Target == null || !journal.Target.Same(gate.Target) || !plan.Target.Same(gate.Target))
						return AuditResult(HardThreadCandidateAudit.Unavailable, "no supported applied candidate-only scope", out detail);
					HardThreadChoice[] choices = plan.Choices;
					var owned = new Dictionary<string, HardThreadReceipt>(StringComparer.Ordinal);
					foreach (HardThreadReceipt row in journal.Rows)
					{
						if (row.Terminal) continue;
						if (row.Thread == null || row.Phase != HardThreadPhase.Applied || !row.PreviousKnown
							|| !plan.IsCandidate(row.Thread) || row.Group != plan.Group || row.Applied != plan.MaskFor(row.Thread)
							|| row.Captured != plan.ProcessMask || row.PreviousGroup != row.Group || row.Previous != row.Captured
							|| owned.ContainsKey(row.Thread.Key))
							return AuditResult(HardThreadCandidateAudit.Unavailable, "candidate ownership is not durably applied", out detail);
						owned.Add(row.Thread.Key, row);
					}
					if (owned.Count != choices.Length)
						return AuditResult(HardThreadCandidateAudit.Unavailable, "candidate ownership is incomplete", out detail);
					HardThreadMachine scope = reads.ReadProcessScope();
					if (scope == null || scope.Read == HardThreadRead.Unknown)
						return AuditResult(HardThreadCandidateAudit.Unavailable, "original process scope is unavailable", out detail);
					if (scope.Read == HardThreadRead.Gone)
						return AuditResult(HardThreadCandidateAudit.Gone, "original process exited", out detail);
					if (scope.Read != HardThreadRead.Known)
						return AuditResult(HardThreadCandidateAudit.Unavailable, "original process scope is unreadable", out detail);
					if (scope.GroupCount != 1 || scope.Group != plan.Group || scope.ProcessMask != plan.ProcessMask
						|| !scope.CpuSetsEmpty || (gate.Target.AllowedMask & ~scope.ProcessMask) != 0)
						return AuditResult(HardThreadCandidateAudit.Changed, "original process range, group or CPU Sets changed", out detail);
					var candidateFacts = new List<string>();
					foreach (HardThreadChoice choice in choices)
					{
						HardThreadReceipt row;
						if (!owned.TryGetValue(choice.Thread.Key, out row))
							return AuditResult(HardThreadCandidateAudit.Unavailable, "original candidate receipt is missing", out detail);
						HardThreadObserved observed = native.ReadThread(choice.Thread);
						if (observed == null || observed.Read == HardThreadRead.Unknown)
							return AuditResult(HardThreadCandidateAudit.Unavailable, "original candidate is unavailable", out detail);
						if (observed.Read == HardThreadRead.Gone)
							return AuditResult(HardThreadCandidateAudit.Gone, "original candidate exited", out detail);
						if (observed.Read != HardThreadRead.Known)
							return AuditResult(HardThreadCandidateAudit.Unavailable, "original candidate is unreadable", out detail);
						if (observed.Group != row.Group || observed.Mask != row.Applied || !observed.CpuSetsEmpty)
							return AuditResult(HardThreadCandidateAudit.Changed, "original candidate range, group or CPU Sets changed", out detail);
						candidateFacts.Add("TID=" + choice.Thread.Tid + " affinity=0x" + observed.Mask.ToString("X") + " group=" + observed.Group
							+ " priorityLevel(raw)=" + (observed.ThreadPriorityKnown ? observed.ThreadPriorityLevel.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unreadable"));
					}
					RequireForwardAuthority();
					foreach (HardThreadChoice choice in choices) Confirm(owned[choice.Thread.Key]);
					return AuditResult(HardThreadCandidateAudit.Verified, string.Join("; ", candidateFacts.ToArray())
						+ "; processPriorityClass(raw)=" + (scope.ProcessPriorityClassKnown ? "0x" + scope.ProcessPriorityClass.ToString("X") : "unreadable"), out detail);
				}
			}
			catch (Exception error)
			{
				return AuditResult(HardThreadCandidateAudit.Unavailable, "candidate audit unavailable: " + error.GetType().Name + ": " + error.Message, out detail);
			}
		}

		private HardThreadCandidateAudit AuditResult(HardThreadCandidateAudit result, string reason, out string detail)
		{
			detail = Detail = reason;
			lastFailureKind = result == HardThreadCandidateAudit.Verified ? HardThreadFailureKind.None : HardThreadFailureKind.ObservationUnavailable;
			return result;
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
				IHardThreadPlacementReads placementReads = plan.RequireWorkerCoverage ? native as IHardThreadPlacementReads : null;
				var exitedWorkers = new HashSet<string>(StringComparer.Ordinal);
				string text = null;
				int num = 0;
				IHardThreadPlacementBatchReads batchReads = plan.RequireWorkerCoverage && coverage == HardThreadCoverage.Complete
					&& CompleteOwnedInput(threads) ? native as IHardThreadPlacementBatchReads : null;
				// Share identity checks only for complete stable audits; new writes and recovery require independent full reads.
				IHardThreadPlacementBatch batch = null;
				try
				{
					if (batchReads != null)
					{
						batch = batchReads.BeginPlacementBatch();
						if (batch == null) throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "placement audit scope unavailable");
						placementReads = batch;
					}
					for (int i = 0; i < journal.Rows.Count; i++)
					{
						HardThreadReceipt hardThreadReceipt = journal.Rows[i];
						if (!hardThreadReceipt.Terminal)
						{
							HardThreadObserved hardThreadObserved = placementReads == null
								? native.ReadThread(hardThreadReceipt.Thread) : placementReads.ReadPlacement(hardThreadReceipt.Thread);
							if (hardThreadReceipt.Phase != HardThreadPhase.Applied || hardThreadObserved == null
								|| (hardThreadObserved.Read != HardThreadRead.Known && hardThreadObserved.Read != HardThreadRead.Gone))
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
								if (plan.RequireWorkerCoverage) exitedWorkers.Add(hardThreadReceipt.Thread.Key);
								else coverage = HardThreadCoverage.Partial;
							}
							else
							{
								if (!hardThreadObserved.CpuSetsEmpty || hardThreadObserved.Group != hardThreadReceipt.Group || hardThreadObserved.Mask != hardThreadReceipt.Applied)
								{
									string text2 = text ?? DescribeExternalChange(hardThreadReceipt, hardThreadObserved, i);
									if (plan.RequireWorkerCoverage)
									{
										Forget(hardThreadReceipt.Thread);
										throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
											"exclusive thread placement changed externally: " + text2, true);
									}
									string text3 = ReassertOwned(hardThreadReceipt, hardThreadObserved);
									if (text3 != null)
									{
										Forget(hardThreadReceipt.Thread);
										throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "owned thread state changed externally (" + text3 + "): " + text2, true);
									}
									text = text2;
									num++;
								}
								if (batch == null) Confirm(hardThreadReceipt);
							}
						}
					}
					if (batch != null)
					{
						batch.Complete();
						foreach (HardThreadReceipt row in journal.Rows)
							if (!row.Terminal) Confirm(row);
					}
				}
				catch
				{
					if (batchReads != null) lock (factsGate) verified.Clear();
					throw;
				}
				finally { if (batch != null) batch.Dispose(); }
				if (text != null)
				{
					LastReassertion = text + "; written back " + num;
				}
				if (exitedWorkers.Count != 0)
					threads = Array.FindAll(threads, thread => !exitedWorkers.Contains(thread.Key));
				if (plan.RequireWorkerCoverage && coverage == HardThreadCoverage.Complete && CompleteOwnedInput(threads))
				{
					ValidateInput(threads, coverage);
					ReadValidatedMachine();
					RequireForwardAuthority();
					return CompleteApply(false);
				}
				return ApplyThreads(threads, coverage);
			});
		}

		private bool CompleteOwnedInput(HardThreadIdentity[] threads)
		{
			foreach (HardThreadIdentity thread in threads)
			{
				HardThreadReceipt row = Find(thread);
				if (row == null || row.Phase != HardThreadPhase.Applied || !row.PreviousKnown
					|| row.Group != plan.Group || row.Applied != plan.MaskFor(thread)) return false;
			}
			return true;
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
			if (!reassertionAdmitted)
			{
				Func<bool> admit = BeforeReassertion;
				if (admit != null)
				{
					try { if (!admit()) return "forward reassertion declined"; }
					catch { return "forward reassertion policy unavailable"; }
				}
				reassertionAdmitted = true;
			}
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

		private bool RetireConfirmedWorkerExit(HardThreadIdentity thread, HardThreadObserved observed)
		{
			if (!plan.RequireWorkerCoverage || plan.IsCandidate(thread) || observed == null || observed.Read != HardThreadRead.Gone)
				return false;
			Forget(thread);
			HardThreadReceipt owned = Find(thread);
			if (owned != null)
			{
				owned.Phase = HardThreadPhase.Gone;
				Save();
			}
			return true;
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
			if ((!plan.RequireWorkerCoverage && num > 8) || journal.Rows.Count + num2 > 512)
			{
				throw InvalidRequest("bounded worker batch required (input=" + threads.Length + " rows=" + journal.Rows.Count + " new=" + num2 + " workers=" + num + ")");
			}
			if (plan.RequireWorkerCoverage && (coverage != HardThreadCoverage.Complete || num2 + journal.Rows.Count > 512))
			{
				throw InvalidRequest("complete bounded worker snapshot required (coverage=" + coverage + " input=" + threads.Length + " rows=" + journal.Rows.Count + " new=" + num2 + ")");
			}
			if (plan.RequireWorkerCoverage)
			{
				foreach (HardThreadChoice choice in plan.Choices)
					if (!hashSet.Contains(choice.Thread.Key)) throw InvalidRequest("complete snapshot is missing an original candidate");
			}
		}

		private HardThreadMachine ReadValidatedMachine()
		{
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
				hardThreadPlan = HardThreadPlan.Create(gate.Target, hardThreadMachine, plan.Choices, plan.WorkerMode, plan.RequireWorkerCoverage);
			}
			catch (Exception ex)
			{
				throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, "native topology cannot admit the original plan: " + ex.Message, Pending());
			}
			if (hardThreadPlan.Workers != plan.Workers || hardThreadPlan.ProcessMask != plan.ProcessMask)
			{
				throw InvalidRequest("plan physical topology differs from the native admission");
			}
			return hardThreadMachine;
		}

		private HardThreadMachine ReadWriteScope()
		{
			IHardThreadCandidateReads reads = native as IHardThreadCandidateReads;
			return reads == null ? native.ReadMachine() : reads.ReadProcessScope();
		}

		private bool ApplyThreads(HardThreadIdentity[] threads, HardThreadCoverage coverage)
		{
			ValidateInput(threads, coverage);
			HardThreadMachine hardThreadMachine = ReadValidatedMachine();
			if (plan.RequireWorkerCoverage)
			{
				var ordered = new List<HardThreadIdentity>(threads.Length);
				foreach (HardThreadIdentity thread in threads)
				{
					if (Find(thread) == null)
					{
						HardThreadObserved observed = native.ReadThread(thread);
						if (RetireConfirmedWorkerExit(thread, observed)) continue;
						if (observed == null || observed.Read != HardThreadRead.Known || !observed.CpuSetsEmpty
							|| observed.Group != plan.Group || observed.Mask != plan.ProcessMask)
							throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
								"complete worker preflight unavailable or externally constrained", Pending());
					}
					if (!plan.IsCandidate(thread)) ordered.Add(thread);
				}
				foreach (HardThreadIdentity thread in threads)
					if (plan.IsCandidate(thread)) ordered.Add(thread);
				threads = ordered.ToArray();
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
			bool workersVerified = !plan.RequireWorkerCoverage;
			foreach (HardThreadIdentity thread in threads)
			{
				if (!workersVerified && plan.IsCandidate(thread))
				{
					foreach (HardThreadIdentity worker in threads)
					{
						if (plan.IsCandidate(worker)) continue;
						HardThreadReceipt owned = Find(worker);
						HardThreadObserved observed = native.ReadThread(worker);
						if (RetireConfirmedWorkerExit(worker, observed)) continue;
						if (owned == null || owned.Phase != HardThreadPhase.Applied || observed == null
							|| observed.Read != HardThreadRead.Known || !observed.CpuSetsEmpty
							|| observed.Group != plan.Group || observed.Mask != plan.Workers)
							throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
								"worker exclusion changed before candidate admission", Pending());
					}
					workersVerified = true;
				}
				if (Find(thread) != null)
				{
					continue;
				}
				RequireForwardAuthority();
				HardThreadObserved hardThreadObserved2 = native.ReadThread(thread);
				bool flag2 = plan.IsCandidate(thread);
				if (RetireConfirmedWorkerExit(thread, hardThreadObserved2)) continue;
				if (hardThreadObserved2 == null || hardThreadObserved2.Read != 0 || !hardThreadObserved2.CpuSetsEmpty || hardThreadObserved2.Group != plan.Group || hardThreadObserved2.Mask != plan.ProcessMask)
				{
					if (flag2 || plan.RequireWorkerCoverage)
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
				AddReceipt(item);
				bool flag3 = false;
				try
				{
					Save();
					item = journal.Rows[journal.Rows.Count - 1];
					RequireForwardAuthority();
					hardThreadObserved2 = native.ReadThread(thread);
					hardThreadMachine = ReadWriteScope();
					if (RetireConfirmedWorkerExit(thread, hardThreadObserved2)) continue;
					if (hardThreadMachine == null || hardThreadMachine.Read != 0 || hardThreadMachine.GroupCount != 1 || hardThreadMachine.Group != plan.Group || hardThreadMachine.ProcessMask != plan.ProcessMask || !hardThreadMachine.CpuSetsEmpty || (item.Applied & ~gate.Target.AllowedMask) != 0)
					{
						throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, "process write admission changed", true);
					}
					if (hardThreadObserved2 == null || hardThreadObserved2.Read != 0 || !hardThreadObserved2.CpuSetsEmpty || hardThreadObserved2.Group != item.Group || hardThreadObserved2.Mask != item.Captured)
					{
							throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "thread changed before native write", flag2 || plan.RequireWorkerCoverage);
					}
					RequireForwardAuthority();
					flag3 = true;
					lastOperationNativeAttempt = true;
					ushort previousGroup;
					ulong previousMask;
					if (!native.Set(thread, item.Group, item.Applied, out previousGroup, out previousMask))
					{
						if (plan.RequireWorkerCoverage && !flag2 && RetireConfirmedWorkerExit(thread, native.ReadThread(thread))) continue;
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
					if (RetireConfirmedWorkerExit(thread, hardThreadObserved2)) continue;
					if (hardThreadObserved2 == null || hardThreadObserved2.Read != 0 || !hardThreadObserved2.CpuSetsEmpty || hardThreadObserved2.Group != item.Group || hardThreadObserved2.Mask != item.Applied)
					{
						throw new InvalidOperationException("native apply readback is unconfirmed");
					}
					item.Phase = HardThreadPhase.Applied;
					// The native previous value is already durable; the next intent also commits this completion.
					pendingApplied = thread;
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
					if (!flag3 && flag4 && !flag2 && !plan.RequireWorkerCoverage && ex3 != null && ex3.Kind == HardThreadFailureKind.ObservationUnavailable)
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
			if (pendingApplied != null) Save();
			return CompleteApply(flag);
		}

		private bool CompleteApply(bool partial)
		{
			Status = ((plan.WorkerMode == HardThreadWorkerMode.CandidatesOnly) ? HardThreadStatus.AppliedCandidates : ((!partial) ? HardThreadStatus.Applied : HardThreadStatus.CoveragePending));
			Detail = ((Status == HardThreadStatus.AppliedCandidates) ? "only original candidates are bound; worker placement is unchanged" : ((!partial) ? "provided snapshot threads verified; future workers are not instantly exclusive" : "thread lifecycle coverage is incomplete"));
			return true;
		}

		internal bool Restore()
		{
			return RestoreInternal(true);
		}

		private void SettleExitedProcess()
		{
			HardThreadJournal settled = journal.Clone();
			bool changed = false;
			foreach (HardThreadReceipt row in settled.Rows)
			{
				if (row.Terminal) continue;
				row.Phase = HardThreadPhase.Gone;
				changed = true;
			}
			if (changed) SaveJournal(settled);
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
						if (hardThreadMachine.Read == HardThreadRead.Gone) SettleExitedProcess();
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
										SettleExitedProcess();
										break;
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
												if (hardThreadMachine.Read == HardThreadRead.Gone)
												{
													SettleExitedProcess();
													break;
												}
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

		// Observe stopped-guardian exit without spending cleanup attempts.
		// Return false when native Dispose work is still required.
		internal bool RefreshGuardianClosure()
		{
			using (gate.EnterUse())
			{
				if (resourcesClosed)
				{
					return true;
				}
				if (!disposed || !nativeClosed || !storeClosed || guardian == null || guardianClosed)
				{
					return false;
				}
				guardianClosed = guardian.ResourcesClosed;
				resourcesClosed = guardianClosed;
				return true;
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
