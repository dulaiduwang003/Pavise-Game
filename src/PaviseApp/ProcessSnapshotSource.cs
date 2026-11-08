using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
	internal static class ProcessSnapshotSource
	{
		private sealed class PathCacheEntry
		{
			public long Creation;

			public string Path;

			public int Failures;
			public long NextAttempt;
		}

		private const int SystemProcessInformation = 5;

		private const int StatusInfoLengthMismatch = -1073741820;

		private const int OffsetNextEntry = 0;

		private const int OffsetThreadCount = 4;

		private const int OffsetCreateTime = 32;

		private const int OffsetUserTime = 40;

		private const int OffsetKernelTime = 48;

		private const int OffsetImageNameLength = 56;

		private const int OffsetImageNameBuffer = 64;

		private const int OffsetUniqueProcessId = 80;

		private const int OffsetParentProcessId = 88;

		private const int OffsetSessionId = 100;

		private const int OffsetReadTransferCount = 232;

		private const int OffsetWriteTransferCount = 240;

		private const int ProcessRecordBytes = 256;

		private const int ThreadRecordBytes = 80;

		private const int OffsetThreadKernelTime = 0;

		private const int OffsetThreadUserTime = 8;

		private const int OffsetThreadCreateTime = 16;

		private const int OffsetThreadProcessId = 40;

		private const int OffsetThreadId = 48;

		private const int MaxSampledThreads = 16384;

		private const int InitialBufferBytes = 1048576;

		private const int BufferHeadroomBytes = 262144;

		private const int MaxBufferBytes = 67108864;

		internal const int ReuseMaxAgeMs = 5000;

		private static readonly object bufferSync = new object();

		private static IntPtr sharedBuffer;

		private static int sharedBufferBytes;

		private static int sharedBufferUsed;

		private static long sharedBufferTakenTicks;
        private static long queryFailureCycle = -1, queryAfter;
        private static int queryFailures;
        private static bool QueryFailed(string reason)
        {
            queryFailures++;
            queryAfter = AutoCores.NowTicks + TimeSpan.FromSeconds(30).Ticks;
            Logger.Warn("Process snapshot source failed phase=" + queryFailureCycle + " attempt=" + queryFailures + "/3: " + reason
                + (queryFailures >= 3 ? "; source unknown; native queries stopped for this phase" : ""));
            return false;
        }

		private static long threadSampleReuses;

		private static readonly object cacheSync = new object();

		private static readonly Dictionary<int, PathCacheEntry> pathCache = new Dictionary<int, PathCacheEntry>();

		private static long pathQueryCount;

		private static ProcessSnapshot cachedSnapshot;

		private static long cachedSnapshotTicks;

		private static int cachedSnapshotSession = -1;

		private static long reuseCount;

		internal static long ThreadSampleReuses
		{
			get
			{
				return Interlocked.Read(ref threadSampleReuses);
			}
		}

		internal static long PathQueryCount
		{
			get
			{
				return Interlocked.Read(ref pathQueryCount);
			}
		}

		internal static int PathCacheSize
		{
			get
			{
				lock (cacheSync)
				{
					return pathCache.Count;
				}
			}
		}

		internal static long ReuseCount
		{
			get
			{
				return Interlocked.Read(ref reuseCount);
			}
		}

		[DllImport("ntdll.dll")]
		private static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int len, out int ret);

		internal static void ResetPathCache()
		{
			lock (cacheSync)
			{
				pathCache.Clear();
			}
		}

		public static ProcessSnapshot Capture()
		{
			return Capture(-1);
		}

		public static ProcessSnapshot Capture(int pathSession)
		{
			return Capture(pathSession, 0);
		}

		public static ProcessSnapshot Capture(int pathSession, int maxAgeMs)
		{
			if (maxAgeMs > 0 && pathSession >= 0)
			{
				lock (cacheSync)
				{
					long num = (DateTime.UtcNow.Ticks - cachedSnapshotTicks) / 10000;
					if (cachedSnapshot != null && cachedSnapshotSession == pathSession && num >= 0 && num <= maxAgeMs)
					{
						Interlocked.Increment(ref reuseCount);
						return cachedSnapshot;
					}
				}
			}
			ProcEntry[] array = Enumerate();
			if (array == null)
			{
				return null;
			}
			if (pathSession >= 0)
			{
				ResolvePaths(array, pathSession);
			}
			else
			{
				PruneCache(array);
			}
			ProcessSnapshot result = new ProcessSnapshot(array);
			if (pathSession >= 0)
			{
				lock (cacheSync)
				{
					cachedSnapshot = result;
					cachedSnapshotTicks = DateTime.UtcNow.Ticks;
					cachedSnapshotSession = pathSession;
				}
			}
			return result;
		}

		internal static void ResetSnapshotCacheForTest()
		{
			lock (cacheSync)
			{
				cachedSnapshot = null;
				cachedSnapshotTicks = 0L;
				cachedSnapshotSession = -1;
				Interlocked.Exchange(ref reuseCount, 0L);
			}
		}

		private static ProcEntry[] Enumerate()
		{
			lock (bufferSync)
			{
				try
				{
					return QuerySharedBufferLocked() ? Parse(sharedBuffer) : null;
				}
				catch
				{
					return null;
				}
			}
		}

		private static bool QuerySharedBufferLocked()
		{
            long cycle = PerformanceTuning.RecoveryCycleSerial;
            if (cycle > queryFailureCycle) { queryFailureCycle = cycle; queryFailures = 0; queryAfter = 0; }
            if (queryFailures >= 3 || AutoCores.NowTicks < queryAfter) return false;
			sharedBufferTakenTicks = 0L;
			sharedBufferUsed = 0;
			try
			{
				if (sharedBuffer == IntPtr.Zero)
				{
					sharedBufferBytes = 1048576;
					sharedBuffer = Marshal.AllocHGlobal(sharedBufferBytes);
				}
				while (true)
				{
					int ret;
                    int status = NtQuerySystemInformation(5, sharedBuffer, sharedBufferBytes, out ret);
                    switch (status)
					{
					case 0:
						sharedBufferUsed = ((ret > 0 && ret <= sharedBufferBytes) ? ret : sharedBufferBytes);
						sharedBufferTakenTicks = AutoCores.NowTicks;
						return true;
					default:
                        return QueryFailed("NtQuerySystemInformation status=" + status);
					case -1073741820:
					{
						int num = ret + 262144;
						if (num <= sharedBufferBytes)
						{
							num = sharedBufferBytes * 2;
						}
						if (num > 67108864)
						{
                            return QueryFailed("process buffer exceeds safe capacity");
						}
						Marshal.FreeHGlobal(sharedBuffer);
						sharedBuffer = IntPtr.Zero;
						sharedBuffer = Marshal.AllocHGlobal(num);
						sharedBufferBytes = num;
						break;
					}
					}
				}
			}
            catch (Exception error)
			{
				if (sharedBuffer != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(sharedBuffer);
					sharedBuffer = IntPtr.Zero;
					sharedBufferBytes = 0;
				}
                return QueryFailed(error.GetType().Name);
			}
		}

		internal static ThreadTimes SampleThreads(int pid, long creation)
		{
			return SampleThreads(pid, creation, 1500);
		}

		internal static ThreadTimes SampleThreads(int pid, long creation, int maxAgeMs)
		{
			if (IntPtr.Size != 8 || pid <= 0 || creation <= 0)
			{
				return null;
			}
			lock (bufferSync)
			{
				try
				{
					long num = AutoCores.NowTicks - sharedBufferTakenTicks;
					bool flag = maxAgeMs > 0 && sharedBuffer != IntPtr.Zero && sharedBufferTakenTicks > 0 && num >= 0 && num <= (long)Math.Min(maxAgeMs, 1500) * 10000L;
					if (!flag && !QuerySharedBufferLocked())
					{
						return null;
					}
					if (flag)
					{
						Interlocked.Increment(ref threadSampleReuses);
					}
					return ParseThreads(sharedBuffer, sharedBufferUsed, pid, creation, sharedBufferTakenTicks);
				}
				catch
				{
					return null;
				}
			}
		}

		internal static ThreadTimes ParseThreads(IntPtr buffer, int used, int pid, long creation, long taken)
		{
			int num2;
			for (long num = 0L; num >= 0 && num + 256 <= used; num += num2)
			{
				IntPtr ptr = new IntPtr(buffer.ToInt64() + num);
				num2 = Marshal.ReadInt32(ptr, 0);
				if (Marshal.ReadIntPtr(ptr, 80).ToInt64() == pid)
				{
					if (Marshal.ReadInt64(ptr, 32) != creation)
					{
						return null;
					}
					int num3 = Marshal.ReadInt32(ptr, 4);
					long num4 = num + 256 + (long)num3 * 80L;
					if (num3 < 0 || num3 > 16384 || num4 > used || (num2 > 0 && num4 > num + num2))
					{
						return null;
					}
					ThreadTimes threadTimes = new ThreadTimes();
					threadTimes.Ticks = taken;
					threadTimes.Creation = creation;
					threadTimes.ProcessCpu = Marshal.ReadInt64(ptr, 40) + Marshal.ReadInt64(ptr, 48);
					threadTimes.Ids = new long[num3];
					threadTimes.Created = new long[num3];
					threadTimes.Cpu = new long[num3];
					ThreadTimes threadTimes2 = threadTimes;
					for (int i = 0; i < num3; i++)
					{
						IntPtr ptr2 = new IntPtr(ptr.ToInt64() + 256 + (long)i * 80L);
						if (Marshal.ReadIntPtr(ptr2, 40).ToInt64() != pid)
						{
							return null;
						}
						threadTimes2.Ids[i] = Marshal.ReadIntPtr(ptr2, 48).ToInt64();
						threadTimes2.Created[i] = Marshal.ReadInt64(ptr2, 16);
						threadTimes2.Cpu[i] = Marshal.ReadInt64(ptr2, 0) + Marshal.ReadInt64(ptr2, 8);
						if (threadTimes2.Cpu[i] < 0 || threadTimes2.Created[i] < 0)
						{
							return null;
						}
					}
					return threadTimes2;
				}
				if (num2 <= 0)
				{
					return null;
				}
			}
			return null;
		}

		private static ProcEntry[] Parse(IntPtr buffer)
		{
			List<ProcEntry> list = new List<ProcEntry>(320);
			IntPtr ptr = buffer;
			while (true)
			{
				int num = Marshal.ReadInt32(ptr, 0);
				int num2 = Marshal.ReadIntPtr(ptr, 80).ToInt32();
				if (num2 > 0)
				{
					ushort byteLength = (ushort)Marshal.ReadInt16(ptr, 56);
					IntPtr namePtr = Marshal.ReadIntPtr(ptr, 64);
					ProcEntry procEntry = new ProcEntry();
					procEntry.Pid = num2;
					procEntry.ParentPid = Marshal.ReadIntPtr(ptr, 88).ToInt32();
					procEntry.Session = Marshal.ReadInt32(ptr, 100);
					procEntry.Threads = Marshal.ReadInt32(ptr, 4);
					procEntry.Creation = Marshal.ReadInt64(ptr, 32);
					procEntry.Cpu = Marshal.ReadInt64(ptr, 40) + Marshal.ReadInt64(ptr, 48);
					procEntry.Io = (ulong)(Marshal.ReadInt64(ptr, 232) + Marshal.ReadInt64(ptr, 240));
					procEntry.Name = ReadName(namePtr, byteLength);
					ProcEntry item = procEntry;
					list.Add(item);
				}
				if (num <= 0)
				{
					break;
				}
				ptr = new IntPtr(ptr.ToInt64() + num);
			}
			return list.ToArray();
		}

		private static string ReadName(IntPtr namePtr, ushort byteLength)
		{
			if (namePtr == IntPtr.Zero || byteLength == 0)
			{
				return "";
			}
			string text;
			try
			{
				text = Marshal.PtrToStringUni(namePtr, byteLength / 2);
			}
			catch
			{
				return "";
			}
			if (string.IsNullOrEmpty(text))
			{
				return "";
			}
			if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return text;
			}
			return text.Substring(0, text.Length - 4);
		}

		private static void ResolvePaths(ProcEntry[] entries, int pathSession)
		{
			HashSet<int> hashSet = new HashSet<int>();
			lock (cacheSync)
			{
				foreach (ProcEntry procEntry in entries)
				{
					hashSet.Add(procEntry.Pid);
					if (procEntry.Pid <= 4 || (procEntry.Session != pathSession && (procEntry.Session != 0 || !BackgroundServicePolicy.CandidateName(procEntry.Name))))
					{
						continue;
					}
					PathCacheEntry value;
					if (pathCache.TryGetValue(procEntry.Pid, out value) && value.Creation == procEntry.Creation)
					{
						procEntry.Path = value.Path;
						if (value.Path != null || value.Failures >= 3 || DateTime.UtcNow.Ticks < value.NextAttempt) continue;
					}
					else value = new PathCacheEntry { Creation = procEntry.Creation };
					string text = (procEntry.Path = QueryPath(procEntry.Pid));
					if (text == null)
					{
						value.Failures++;
						value.NextAttempt = DateTime.UtcNow.AddSeconds(30).Ticks;
						pathCache[procEntry.Pid] = value;
						Logger.Info("Process path read failed pid=" + procEntry.Pid + " birth=" + procEntry.Creation + " attempt=" + value.Failures + "/3");
						continue;
					}
					pathCache[procEntry.Pid] = new PathCacheEntry
					{
						Creation = procEntry.Creation,
						Path = text
					};
				}
				DropDeadLocked(hashSet);
			}
		}

		private static void PruneCache(ProcEntry[] entries)
		{
			HashSet<int> hashSet = new HashSet<int>();
			foreach (ProcEntry procEntry in entries)
			{
				hashSet.Add(procEntry.Pid);
			}
			lock (cacheSync)
			{
				DropDeadLocked(hashSet);
			}
		}

		private static void DropDeadLocked(HashSet<int> live)
		{
			if (pathCache.Count == 0)
			{
				return;
			}
			List<int> list = null;
			foreach (int key in pathCache.Keys)
			{
				if (!live.Contains(key))
				{
					if (list == null)
					{
						list = new List<int>();
					}
					list.Add(key);
				}
			}
			if (list == null)
			{
				return;
			}
			foreach (int item in list)
			{
				pathCache.Remove(item);
			}
		}

		private static string QueryPath(int pid)
		{
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return null;
			}
			Interlocked.Increment(ref pathQueryCount);
			try
			{
				return Native.ImagePath(intPtr);
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}
	}
}
