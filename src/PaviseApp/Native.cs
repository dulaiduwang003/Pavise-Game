using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class Native
	{
		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct OsVersionInfo
		{
			public int Size;

			public int Major;

			public int Minor;

			public int Build;

			public int Platform;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
			public string CSD;
		}

		private struct SystemPowerStatusNative
		{
			public byte AcLineStatus;

			public byte BatteryFlag;

			public byte BatteryLifePercent;

			public byte SystemStatusFlag;

			public int BatteryLifeTime;

			public int BatteryFullLifeTime;
		}

		private struct PowerThrottlingState
		{
			public uint Version;

			public uint ControlMask;

			public uint StateMask;
		}

		private struct Luid
		{
			public uint LowPart;

			public int HighPart;
		}

		private struct TokenPrivileges
		{
			public uint PrivilegeCount;

			public Luid Luid;

			public uint Attributes;
		}

#pragma warning disable 0649
		private struct PublicObjectBasicInformation
		{
			public uint Attributes;

			public uint GrantedAccess;

			public uint HandleCount;

			public uint PointerCount;
		}

		public struct D3dkmtLuid
		{
			public uint LowPart;

			public int HighPart;
		}

		public struct D3dkmtOpenAdapterFromLuid
		{
			public D3dkmtLuid AdapterLuid;

			public uint hAdapter;
		}

		public struct D3dkmtCloseAdapter
		{
			public uint hAdapter;
		}

		public struct D3dkmtQueryVideoMemoryInfo
		{
			public IntPtr hProcess;

			public uint hAdapter;

			public uint MemorySegmentGroup;

			public ulong Budget;

			public ulong CurrentUsage;

			public ulong CurrentReservation;

			public ulong AvailableForReservation;

			public uint PhysicalAdapterIndex;
		}

		public struct EventTraceProperties
		{
			public uint WnodeBufferSize;

			public uint WnodeProviderId;

			public ulong WnodeHistoricalContext;

			public long WnodeTimeStamp;

			public Guid WnodeGuid;

			public uint WnodeClientContext;

			public uint WnodeFlags;

			public uint BufferSize;

			public uint MinimumBuffers;

			public uint MaximumBuffers;

			public uint MaximumFileSize;

			public uint LogFileMode;

			public uint FlushTimer;

			public uint EnableFlags;

			public int AgeLimit;

			public uint NumberOfBuffers;

			public uint FreeBuffers;

			public uint EventsLost;

			public uint BuffersWritten;

			public uint LogBuffersLost;

			public uint RealTimeBuffersLost;

			public IntPtr LoggerThreadId;

			public uint LogFileNameOffset;

			public uint LoggerNameOffset;
		}
#pragma warning restore 0649

		public struct EventTraceHeader
		{
			public ushort Size;

			public ushort FieldTypeFlags;

			public uint Version;

			public uint ThreadId;

			public uint ProcessId;

			public long TimeStamp;

			public Guid Guid;

			public ulong ProcessorTime;
		}

		public struct EventTraceItem
		{
			public EventTraceHeader Header;

			public uint InstanceId;

			public uint ParentInstanceId;

			public Guid ParentGuid;

			public IntPtr MofData;

			public uint MofLength;

			public uint ClientContext;
		}

		public struct TraceLogfileHeader
		{
			public uint BufferSize;

			public uint Version;

			public uint ProviderVersion;

			public uint NumberOfProcessors;

			public long EndTime;

			public uint TimerResolution;

			public uint MaximumFileSize;

			public uint LogFileMode;

			public uint BuffersWritten;

			public Guid LogInstanceGuid;

			public IntPtr LoggerName;

			public IntPtr LogFileName;

			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 172)]
			public byte[] TimeZone;

			public long BootTime;

			public long PerfFreq;

			public long StartTime;

			public uint ReservedFlags;

			public uint BuffersLost;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		public struct EventTraceLogfile
		{
			public IntPtr LogFileName;

			public IntPtr LoggerName;

			public long CurrentTime;

			public uint BuffersRead;

			public uint ProcessTraceMode;

			public EventTraceItem CurrentEvent;

			public TraceLogfileHeader LogfileHeader;

			public IntPtr BufferCallback;

			public uint BufferSize;

			public uint Filled;

			public uint EventsLost;

			public IntPtr EventRecordCallback;

			public uint IsKernelTrace;

			public IntPtr Context;
		}

		public struct EventDescriptor
		{
			public ushort Id;

			public byte Version;

			public byte Channel;

			public byte Level;

			public byte Opcode;

			public ushort Task;

			public ulong Keyword;
		}

		public struct EventHeader
		{
			public ushort Size;

			public ushort HeaderType;

			public ushort Flags;

			public ushort EventProperty;

			public uint ThreadId;

			public uint ProcessId;

			public long TimeStamp;

			public Guid ProviderId;

			public EventDescriptor EventDescriptor;

			public ulong ProcessorTime;

			public Guid ActivityId;
		}

		public struct EventRecord
		{
			public EventHeader EventHeader;

			public uint BufferContext;

			public ushort ExtendedDataCount;

			public ushort UserDataLength;

			public IntPtr ExtendedData;

			public IntPtr UserData;

			public IntPtr UserContext;
		}

		public struct EnableTraceParameters
		{
			public uint Version;

			public uint EnableProperty;

			public uint ControlFlags;

			public Guid SourceId;

			public IntPtr EnableFilterDesc;

			public uint FilterDescCount;
		}

		public delegate void EventRecordCallback(ref EventRecord record);

		private struct IoCounters
		{
			public ulong ReadOperationCount;

			public ulong WriteOperationCount;

			public ulong OtherOperationCount;

			public ulong ReadTransferCount;

			public ulong WriteTransferCount;

			public ulong OtherTransferCount;
		}

		private struct PROCESS_BASIC_INFORMATION
		{
			public IntPtr ExitStatus;

			public IntPtr PebBaseAddress;

			public IntPtr AffinityMask;

			public IntPtr BasePriority;

			public IntPtr UniqueProcessId;

			public IntPtr InheritedFromUniqueProcessId;
		}

		private struct ThreadGroupAffinity
		{
			public UIntPtr Mask;

			public ushort Group;

			public ushort Reserved0;

			public ushort Reserved1;

			public ushort Reserved2;
		}

		private const uint QosSealMask = 5u;

		private const uint TimerExemptBit = 4u;

		public const int PROCESS_SET_QUOTA = 256;

		public const int PROCESS_SET_INFORMATION = 512;

		public const int PROCESS_QUERY_INFORMATION = 1024;

		public const int PROCESS_QUERY_LIMITED_INFORMATION = 4096;

		public const int PROCESS_SET_LIMITED_INFORMATION = 8192;

		public const int THREAD_SET_LIMITED_INFORMATION = 1024;

		public const int THREAD_QUERY_LIMITED_INFORMATION = 2048;

		public const int THREAD_PRIORITY_HIGHEST = 2;

		public const int THREAD_PRIORITY_ERROR_RETURN = int.MaxValue;

		public const int SYNCHRONIZE = 1048576;

		public const int GpuPriorityHigh = 4;

		public const int GpuPriorityIdle = 0;

		public const int GpuPriorityBelowNormal = 1;

		public const int GpuPriorityNormal = 2;

		public const uint IDLE_PRIORITY_CLASS = 64u;

		public const uint NORMAL_PRIORITY_CLASS = 32u;

		public const uint BELOW_NORMAL_PRIORITY_CLASS = 16384u;

		public const uint HIGH_PRIORITY_CLASS = 128u;

		private const int ProcessIoPriorityNt = 33;

		private const int ProcessPagePriorityNt = 39;

		private const int ProcessProtectionNt = 61;

		private const int ProcessPowerThrottling = 4;

		private const int ProcessPowerThrottlingNt = 77;

		private const uint WaitTimeout = 258u;

		private const int ObjectBasicInformation = 0;

		private const int PublicObjectBasicInformationSize = 56;

		public const int WM_DROPFILES = 563;

		private const uint MSGFLT_ALLOW = 1u;

		public const int WM_NCLBUTTONDOWN = 161;

		public const int HT_CAPTION = 2;

		public const int WM_ENTERSIZEMOVE = 561;

		public const int WM_EXITSIZEMOVE = 562;

		public const uint SPI_GETUIEFFECTS = 4158u;

		public const uint SPI_SETUIEFFECTS = 4159u;

		public const uint SPIF_SENDCHANGE = 2u;

		public const uint WnodeFlagTracedGuid = 131072u;

		public const uint EventTraceRealTimeMode = 256u;

		public const uint ProcessTraceModeRealTime = 256u;

		public const uint ProcessTraceModeEventRecord = 268435456u;

		public const uint EventControlCodeEnableProvider = 1u;

		public const uint EventTraceControlStop = 1u;

		public const ulong InvalidProcessTraceHandle = ulong.MaxValue;

		public const uint D3DKMT_MEMORY_SEGMENT_GROUP_LOCAL = 0u;

		private static volatile bool timerSealUnavailable;

		private static volatile bool timerExemptUnavailable;

		public static readonly bool PowerThrottlingSupported = ProbePowerThrottling();

		private static int osBuild = -1;

		private static int domainJoined = -1;

		private static int hasBattery = -1;

		private static int boostPrivilegeState;

		private static int debugPrivilegeState;

		public static bool TimerExemptWanted
		{
			get
			{
				if (OsBuild() >= 22000)
				{
					return !timerExemptUnavailable;
				}
				return false;
			}
		}

		public static bool TrySetCpuSets(IntPtr h, uint[] ids)
		{
			if (ids == null || ids.Length == 0)
			{
				return false;
			}
			try
			{
				return SetProcessDefaultCpuSets(h, ids, (uint)ids.Length);
			}
			catch
			{
				return false;
			}
		}

		public static bool TryClearCpuSets(IntPtr h)
		{
			try
			{
				return SetProcessDefaultCpuSets(h, null, 0u);
			}
			catch
			{
				return false;
			}
		}

		public static uint[] QueryCpuSets(IntPtr h)
		{
			try
			{
				uint required;
				bool processDefaultCpuSets = GetProcessDefaultCpuSets(h, null, 0u, out required);
				if (required == 0)
				{
					return processDefaultCpuSets ? new uint[0] : null;
				}
				uint[] numericValueItems = new uint[required];
				uint required2;
				return (GetProcessDefaultCpuSets(h, numericValueItems, (uint)numericValueItems.Length, out required2) && required2 == required) ? numericValueItems : null;
			}
			catch
			{
				return null;
			}
		}

		public static int QueryThreadOwnerPid(IntPtr thread)
		{
			if (thread == IntPtr.Zero)
			{
				return -1;
			}
			try
			{
				uint processIdOfThread = GetProcessIdOfThread(thread);
				return (processIdOfThread != 0 && processIdOfThread <= int.MaxValue) ? ((int)processIdOfThread) : (-1);
			}
			catch
			{
				return -1;
			}
		}

		public static bool RestoreCpuSets(IntPtr h, uint[] ids)
		{
			if (ids != null)
			{
				if (ids.Length != 0)
				{
					return TrySetCpuSets(h, ids);
				}
				return TryClearCpuSets(h);
			}
			return false;
		}

		public static bool RestoreCpuSetsVerified(IntPtr h, uint[] ids)
		{
			if (RestoreCpuSets(h, ids))
			{
				return CpuSetsMatch(h, ids);
			}
			return false;
		}

		public static bool CpuSetsMatch(IntPtr h, uint[] expected)
		{
			if (expected == null)
			{
				return false;
			}
			uint[] cpuSets = QueryCpuSets(h);
			if (cpuSets == null || cpuSets.Length != expected.Length)
			{
				return false;
			}
			HashSet<uint> hashSet = new HashSet<uint>(cpuSets);
			foreach (uint item in expected)
			{
				if (!hashSet.Contains(item))
				{
					return false;
				}
			}
			return true;
		}

		public static bool TryClearCpuSets(int pid)
		{
			IntPtr intPtr = OpenProcess(8192, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return TryClearCpuSets(intPtr);
			}
			finally
			{
				CloseHandle(intPtr);
			}
		}

		public static uint EcoQoSWantMask(bool sealTimer)
		{
			if (!sealTimer || timerSealUnavailable)
			{
				return 1u;
			}
			return 5u;
		}

		public static bool ApplyEcoQoS(IntPtr process)
		{
			return ApplyEcoQoS(process, false);
		}

		public static bool ApplyEcoQoS(IntPtr process, bool sealTimer)
		{
			if (sealTimer && !timerSealUnavailable)
			{
				if (SetPowerThrottling(process, 5u, 5u))
				{
					return true;
				}
				if (Marshal.GetLastWin32Error() != 87)
				{
					return false;
				}
				timerSealUnavailable = true;
			}
			return SetPowerThrottling(process, 1u, 1u);
		}

		public static int QueryBoostDisabled(IntPtr h)
		{
			bool disabled;
			if (!GetProcessPriorityBoost(h, out disabled))
			{
				return -1;
			}
			if (!disabled)
			{
				return 0;
			}
			return 1;
		}

		public static bool TrySetBoostDisabled(IntPtr h, bool disable)
		{
			return SetProcessPriorityBoost(h, disable);
		}

		public static bool TimerExemptOk(int controlMask, int stateMask)
		{
			if (((uint)controlMask & 4u) != 0)
			{
				return (stateMask & 4) == 0;
			}
			return false;
		}

		public static bool HighQoSMasksOk(int controlMask, int stateMask)
		{
			if (!EcoClearedMasksOk(controlMask, stateMask))
			{
				return false;
			}
			if (TimerExemptWanted)
			{
				return TimerExemptOk(controlMask, stateMask);
			}
			return true;
		}

		public static bool EcoClearedMasksOk(int controlMask, int stateMask)
		{
			if (((uint)controlMask & (true ? 1u : 0u)) != 0)
			{
				return (stateMask & 1) == 0;
			}
			return false;
		}

		public static bool ApplyHighQoS(IntPtr process, bool ignoreTimerResolution)
		{
			bool timerExemptDropped;
			return ApplyHighQoS(process, ignoreTimerResolution, out timerExemptDropped);
		}

		public static bool ApplyHighQoS(IntPtr process, bool ignoreTimerResolution, out bool timerExemptDropped)
		{
			timerExemptDropped = false;
			if (ignoreTimerResolution && !timerExemptUnavailable)
			{
				if (SetPowerThrottling(process, 5u, 0u))
				{
					int controlMask;
					int stateMask;
					if (!TryQueryPowerThrottling(process, out controlMask, out stateMask))
					{
						return true;
					}
					if (TimerExemptOk(controlMask, stateMask))
					{
						return true;
					}
				}
				else if (Marshal.GetLastWin32Error() != 87)
				{
					return false;
				}
				timerExemptUnavailable = true;
				timerExemptDropped = true;
			}
			return SetPowerThrottling(process, 1u, 0u);
		}

		public static bool RestorePowerThrottling(IntPtr process, int controlMask, int stateMask)
		{
			if (controlMask < 0)
			{
				return SetPowerThrottling(process, 0u, 0u);
			}
			return SetPowerThrottling(process, (uint)controlMask, (stateMask >= 0) ? ((uint)stateMask) : 0u);
		}

		public static bool TryQueryPowerThrottling(IntPtr process, out int controlMask, out int stateMask)
		{
			int win32, ntStatus;
			return TryQueryPowerThrottling(process, out controlMask, out stateMask, out win32, out ntStatus);
		}

		internal static bool RestorePowerThrottling(IntPtr process, int controlMask, int stateMask, out int win32)
		{
			bool restored = RestorePowerThrottling(process, controlMask, stateMask);
			win32 = restored ? 0 : Marshal.GetLastWin32Error();
			return restored;
		}

		internal static bool TryQueryPowerThrottling(IntPtr process, out int controlMask, out int stateMask, out int win32, out int ntStatus)
		{
			int sizeOfResult = Marshal.SizeOf(typeof(PowerThrottlingState));
			PowerThrottlingState powerThrottlingState = default(PowerThrottlingState);
			powerThrottlingState.Version = 1u;
			PowerThrottlingState info = powerThrottlingState;
			bool processInformation = GetProcessInformation(process, 4, ref info, sizeOfResult);
			win32 = processInformation ? 0 : Marshal.GetLastWin32Error();
			ntStatus = 0;
			if (!processInformation)
			{
				PowerThrottlingState powerThrottlingState2 = default(PowerThrottlingState);
				powerThrottlingState2.Version = 1u;
				info = powerThrottlingState2;
				ntStatus = NtQueryInformationProcessPower(process, 77, ref info, sizeOfResult, IntPtr.Zero);
				processInformation = ntStatus == 0;
			}
			controlMask = (int)info.ControlMask;
			stateMask = (int)info.StateMask;
			return processInformation;
		}

		private static bool ProbePowerThrottling()
		{
			try
			{
				int controlMask;
				int stateMask;
				return TryQueryPowerThrottling((IntPtr)(-1), out controlMask, out stateMask);
			}
			catch
			{
				return false;
			}
		}

		private static bool SetPowerThrottling(IntPtr process, uint controlMask, uint stateMask)
		{
			PowerThrottlingState powerThrottlingState = default(PowerThrottlingState);
			powerThrottlingState.Version = 1u;
			powerThrottlingState.ControlMask = controlMask;
			powerThrottlingState.StateMask = stateMask;
			PowerThrottlingState info = powerThrottlingState;
			return SetProcessInformation(process, 4, ref info, Marshal.SizeOf(typeof(PowerThrottlingState)));
		}

		[DllImport("ntdll.dll")]
		private static extern int RtlGetVersion(ref OsVersionInfo v);

		public static int OsBuild()
		{
			if (osBuild < 0)
			{
				try
				{
					OsVersionInfo v = default(OsVersionInfo);
					v.Size = Marshal.SizeOf(typeof(OsVersionInfo));
					osBuild = ((RtlGetVersion(ref v) == 0) ? v.Build : 0);
				}
				catch
				{
					osBuild = 0;
				}
			}
			return osBuild;
		}

		[DllImport("kernel32.dll")]
		private static extern bool GetSystemPowerStatus(out SystemPowerStatusNative status);

		[DllImport("netapi32.dll")]
		private static extern int NetGetJoinInformation(string server, out IntPtr name, out int status);

		[DllImport("netapi32.dll")]
		private static extern int NetApiBufferFree(IntPtr buffer);

		public static bool IsDomainJoined()
		{
			if (domainJoined < 0)
			{
				try
				{
					IntPtr name;
					int status;
					if (NetGetJoinInformation(null, out name, out status) != 0)
					{
						domainJoined = 0;
					}
					else
					{
						if (name != IntPtr.Zero)
						{
							NetApiBufferFree(name);
						}
						domainJoined = ((status == 3) ? 1 : 0);
					}
				}
				catch
				{
					domainJoined = 0;
				}
			}
			return domainJoined == 1;
		}

		internal static bool ConfirmedAcPower()
		{
			try
			{
				SystemPowerStatusNative status;
				return GetSystemPowerStatus(out status) && status.AcLineStatus == 1;
			}
			catch
			{
				return false;
			}
		}

		public static bool OnAcPower()
		{
			try
			{
				SystemPowerStatusNative status;
				if (!GetSystemPowerStatus(out status))
				{
					return true;
				}
				return status.AcLineStatus != 0;
			}
			catch
			{
				return true;
			}
		}

		public static bool HasSystemBattery()
		{
			if (hasBattery < 0)
			{
				try
				{
					SystemPowerStatusNative status;
					hasBattery = ((GetSystemPowerStatus(out status) && status.BatteryFlag != 128 && status.BatteryFlag != byte.MaxValue && status.AcLineStatus != byte.MaxValue) ? 1 : 0);
				}
				catch
				{
					hasBattery = 0;
				}
			}
			return hasBattery == 1;
		}

		public static bool IsElevated()
		{
			try
			{
				using (WindowsIdentity ntIdentity = WindowsIdentity.GetCurrent())
				{
					return new WindowsPrincipal(ntIdentity).IsInRole(WindowsBuiltInRole.Administrator);
				}
			}
			catch
			{
				return false;
			}
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern IntPtr OpenProcess(int access, bool inherit, int pid);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern IntPtr OpenThread(int access, bool inherit, int tid);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern uint GetProcessIdOfThread(IntPtr thread);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool SetThreadPriority(IntPtr thread, int priority);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern int GetThreadPriority(IntPtr thread);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

		[DllImport("kernel32.dll")]
		public static extern int GetCurrentThreadId();

		[DllImport("kernel32.dll")]
		public static extern ulong GetTickCount64();

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

		public static bool TryGetLiveProcessSessionId(IntPtr processHandle, int pid, out int sessionId)
		{
			sessionId = -1;
			if (processHandle == IntPtr.Zero || pid <= 0)
			{
				return false;
			}
			uint sessionId2;
			if (!ProcessIdToSessionId((uint)pid, out sessionId2) || sessionId2 > int.MaxValue)
			{
				return false;
			}
			if (WaitForSingleObject(processHandle, 0u) != 258)
			{
				return false;
			}
			sessionId = (int)sessionId2;
			return true;
		}

		public static bool LastOpenProcessFailureWasNoSuchProcess()
		{
			return Marshal.GetLastWin32Error() == 87;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool CloseHandle(IntPtr h);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetExitCodeProcess(IntPtr h, out uint code);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool SetPriorityClass(IntPtr h, uint cls);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern uint GetPriorityClass(IntPtr h);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessPriorityBoost(IntPtr h, out bool disabled);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetProcessPriorityBoost(IntPtr h, bool disable);

		[DllImport("kernel32.dll")]
		public static extern IntPtr GetCurrentProcess();

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool LookupPrivilegeValue(string systemName, string name, out Luid luid);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges privileges, int bufferLength, IntPtr previous, IntPtr returnLength);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PowerThrottlingState info, int size);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessInformation(IntPtr h, int infoClass, ref PowerThrottlingState info, int size);

		[DllImport("ntdll.dll")]
		private static extern int NtSetInformationProcess(IntPtr h, int infoClass, ref int info, int len);

		[DllImport("ntdll.dll")]
		private static extern int NtQueryInformationProcess(IntPtr h, int infoClass, ref int info, int len, IntPtr retLen);

		[DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
		private static extern int NtQueryInformationProcessBasic(IntPtr h, int infoClass, ref PROCESS_BASIC_INFORMATION info, int len, IntPtr retLen);

		[DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
		private static extern int NtQueryInformationProcessPower(IntPtr h, int infoClass, ref PowerThrottlingState info, int len, IntPtr retLen);

		[DllImport("ntdll.dll")]
		private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returned);

		public static bool TryQueryGrantedAccess(IntPtr handle, out uint granted)
		{
			granted = 0u;
			if (handle == IntPtr.Zero)
			{
				return false;
			}
			IntPtr intPtr = Marshal.AllocHGlobal(56);
			try
			{
				for (int i = 0; i < 56; i += 4)
				{
					Marshal.WriteInt32(intPtr, i, 0);
				}
				int returned;
				if (NtQueryObject(handle, 0, intPtr, 56, out returned) != 0)
				{
					return false;
				}
				granted = ((PublicObjectBasicInformation)Marshal.PtrToStructure(intPtr, typeof(PublicObjectBasicInformation))).GrantedAccess;
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		public static bool HandleWriteAccessStripped(IntPtr handle, out uint granted)
		{
			if (!TryQueryGrantedAccess(handle, out granted))
			{
				return false;
			}
			return (granted & 0x200) == 0;
		}

		public static bool EnsureBoostPrivilege()
		{
			int readResult = Volatile.Read(ref boostPrivilegeState);
			if (readResult != 0)
			{
				return readResult > 0;
			}
			bool enablePrivilegeSucceeded = EnablePrivilege("SeIncreaseBasePriorityPrivilege");
			Interlocked.CompareExchange(ref boostPrivilegeState, enablePrivilegeSucceeded ? 1 : (-1), 0);
			return Volatile.Read(ref boostPrivilegeState) > 0;
		}

		public static bool TryEnableDebugPrivilege()
		{
			int readResult = Volatile.Read(ref debugPrivilegeState);
			if (readResult != 0)
			{
				return readResult > 0;
			}
			bool enablePrivilegeSucceeded = EnablePrivilege("SeDebugPrivilege");
			Interlocked.CompareExchange(ref debugPrivilegeState, enablePrivilegeSucceeded ? 1 : (-1), 0);
			return Volatile.Read(ref debugPrivilegeState) > 0;
		}

		private static bool EnablePrivilege(string name)
		{
			IntPtr token;
			if (!OpenProcessToken(GetCurrentProcess(), 40u, out token))
			{
				return false;
			}
			try
			{
				Luid luid;
				if (!LookupPrivilegeValue(null, name, out luid))
				{
					return false;
				}
				TokenPrivileges tokenPrivileges = default(TokenPrivileges);
				tokenPrivileges.PrivilegeCount = 1u;
				tokenPrivileges.Luid = luid;
				tokenPrivileges.Attributes = 2u;
				TokenPrivileges privileges = tokenPrivileges;
				if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
				{
					return false;
				}
				return Marshal.GetLastWin32Error() == 0;
			}
			finally
			{
				CloseHandle(token);
			}
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr procMask, out UIntPtr sysMask);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

		[DllImport("gdi32.dll")]
		public static extern int D3DKMTOpenAdapterFromLuid(ref D3dkmtOpenAdapterFromLuid p);

		[DllImport("gdi32.dll")]
		public static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter p);

		[DllImport("gdi32.dll")]
		public static extern int D3DKMTQueryVideoMemoryInfo(ref D3dkmtQueryVideoMemoryInfo p);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count, out uint required);

		[DllImport("user32.dll")]
		public static extern bool ReleaseCapture();

		[DllImport("user32.dll")]
		public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

		[DllImport("dwmapi.dll")]
		private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

		[DllImport("user32.dll", SetLastError = true)]
		public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

		[DllImport("user32.dll")]
		public static extern uint GetDpiForSystem();

		[DllImport("user32.dll")]
		public static extern uint GetDpiForWindow(IntPtr hwnd);

		[DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
		private static extern int SetWindowTheme(IntPtr hwnd, string sub, string list);

		[DllImport("uxtheme.dll", EntryPoint = "#135")]
		public static extern int SetPreferredAppMode(int mode);

		[DllImport("winmm.dll")]
		public static extern uint timeBeginPeriod(uint ms);

		[DllImport("ntdll.dll")]
		public static extern int NtSetTimerResolution(uint desired, [MarshalAs(UnmanagedType.U1)] bool set, out uint actual);

		[DllImport("ntdll.dll")]
		public static extern int NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);

		[DllImport("winmm.dll")]
		public static extern uint timeEndPeriod(uint ms);

		[DllImport("shell32.dll")]
		private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);

		[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
		private static extern uint DragQueryFileW(IntPtr hDrop, uint index, StringBuilder file, uint cch);

		[DllImport("shell32.dll")]
		private static extern void DragFinish(IntPtr hDrop);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool ChangeWindowMessageFilter(uint message, uint action);

		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern uint RegisterWindowMessage(string name);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr FindWindow(string cls, string name);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);

		public static bool NotificationAreaPresent()
		{
			try
			{
				IntPtr intPtr = FindWindow("Shell_TrayWnd", null);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				return FindWindowEx(intPtr, IntPtr.Zero, "TrayNotifyWnd", null) != IntPtr.Zero;
			}
			catch
			{
				return false;
			}
		}

		public static void AllowTaskbarCreatedMessage()
		{
			try
			{
				uint registerWindowMessageResult = RegisterWindowMessage("TaskbarCreated");
				if (registerWindowMessageResult != 0)
				{
					ChangeWindowMessageFilter(registerWindowMessageResult, 1u);
				}
			}
			catch
			{
			}
		}

		public static void EnableElevatedFileDrop(IntPtr hwnd)
		{
			try
			{
				ChangeWindowMessageFilter(563u, 1u);
				ChangeWindowMessageFilter(74u, 1u);
				ChangeWindowMessageFilter(73u, 1u);
				ChangeWindowMessageFilterEx(hwnd, 563u, 1u, IntPtr.Zero);
				ChangeWindowMessageFilterEx(hwnd, 74u, 1u, IntPtr.Zero);
				ChangeWindowMessageFilterEx(hwnd, 73u, 1u, IntPtr.Zero);
				DragAcceptFiles(hwnd, true);
			}
			catch
			{
			}
		}

		public static string[] ReadDroppedFiles(IntPtr hDrop)
		{
			try
			{
				uint dragQueryFileWResult = DragQueryFileW(hDrop, uint.MaxValue, null, 0u);
				List<string> list = new List<string>();
				StringBuilder stringBuilder = new StringBuilder(1024);
				for (uint iterationIndex = 0u; iterationIndex < dragQueryFileWResult; iterationIndex++)
				{
					stringBuilder.Length = 0;
					if (DragQueryFileW(hDrop, iterationIndex, stringBuilder, (uint)stringBuilder.Capacity) != 0)
					{
						list.Add(stringBuilder.ToString());
					}
				}
				return list.ToArray();
			}
			catch
			{
				return new string[0];
			}
			finally
			{
				try
				{
					DragFinish(hDrop);
				}
				catch
				{
				}
			}
		}

		public static void Dark(Control control)
		{
			EventHandler eventHandler = delegate
			{
				RefreshDark(control);
			};
			control.HandleCreated += eventHandler;
			if (control.IsHandleCreated)
			{
				eventHandler(null, EventArgs.Empty);
			}
		}

		internal static void RefreshDark(Control control)
		{
			if (control == null || control.IsDisposed || !control.IsHandleCreated)
			{
				return;
			}
			try
			{
				SetWindowTheme(control.Handle, Theme.LightMode ? "Explorer" : "DarkMode_Explorer", null);
			}
			catch
			{
			}
		}

		[DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
		public static extern bool SystemParametersInfoGet(uint action, uint param, ref int value, uint winIni);

		[DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
		public static extern bool SystemParametersInfoSet(uint action, uint param, IntPtr value, uint winIni);

		public static void RoundCorners(IntPtr hwnd)
		{
			try
			{
				int val = 2;
				DwmSetWindowAttribute(hwnd, 33, ref val, 4);
			}
			catch
			{
			}
		}

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW", SetLastError = true)]
		public static extern int StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW", SetLastError = true)]
		public static extern int ControlTrace(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

		[DllImport("advapi32.dll", SetLastError = true)]
		public static extern int EnableTraceEx2(ulong sessionHandle, ref Guid providerId, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, ref EnableTraceParameters enableParameters);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW", SetLastError = true)]
		public static extern ulong OpenTrace(ref EventTraceLogfile logfile);

		[DllImport("advapi32.dll", SetLastError = true)]
		public static extern int ProcessTrace(ulong[] handles, uint handleCount, IntPtr startTime, IntPtr endTime);

		[DllImport("advapi32.dll", SetLastError = true)]
		public static extern int CloseTrace(ulong handle);

		internal static IntPtr OpenAffinityProcess(int pid)
		{
			return OpenProcess(4608, false, pid);
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters counters);

		public static bool QueryProcessSample(IntPtr h, out long creation, out long cpu, out ulong io)
		{
			creation = (cpu = 0L);
			io = 0uL;
			long exit;
			long kernel;
			long user;
			if (!GetProcessTimes(h, out creation, out exit, out kernel, out user))
			{
				return false;
			}
			cpu = kernel + user;
			IoCounters counters;
			if (GetProcessIoCounters(h, out counters))
			{
				io = counters.ReadTransferCount + counters.WriteTransferCount;
			}
			return true;
		}

		internal static int QueryProcessIdentityState(IntPtr handle, long expectedCreation)
		{
			long creation;
			long exit;
			long kernel;
			long user;
			if (expectedCreation <= 0 || !GetProcessTimes(handle, out creation, out exit, out kernel, out user))
			{
				return -1;
			}
			if (creation != expectedCreation || exit != 0)
			{
				return 1;
			}
			return 0;
		}

		public static ulong QueryAffinity(IntPtr h)
		{
			ulong mask;
			if (!TryQueryAffinity(h, out mask))
			{
				return 0uL;
			}
			return mask;
		}

		public static bool TryQueryAffinity(IntPtr h, out ulong mask)
		{
			UIntPtr procMask;
			UIntPtr sysMask;
			bool processAffinityMask = GetProcessAffinityMask(h, out procMask, out sysMask);
			mask = (processAffinityMask ? ((ulong)procMask) : 0);
			return processAffinityMask;
		}

		public static int QueryIoPriority(IntPtr h)
		{
			int info = 0;
			if (NtQueryInformationProcess(h, 33, ref info, 4, IntPtr.Zero) != 0)
			{
				return -1;
			}
			return info;
		}

		public static int QueryPagePriority(IntPtr h)
		{
			int info = 0;
			if (NtQueryInformationProcess(h, 39, ref info, 4, IntPtr.Zero) != 0)
			{
				return -1;
			}
			return info;
		}

		public static bool IsProtectedProcess(IntPtr h)
		{
			int info = 0;
			if (NtQueryInformationProcess(h, 61, ref info, 1, IntPtr.Zero) == 0)
			{
				return (info & 0xFF) != 0;
			}
			return false;
		}

		public static bool TrySetIoPriority(IntPtr process, int priority, out int status)
		{
			status = NtSetInformationProcess(process, 33, ref priority, 4);
			return status == 0;
		}

		public static bool TrySetIoPriority(IntPtr process, int priority)
		{
			int status;
			return TrySetIoPriority(process, priority, out status);
		}

		public static bool TrySetPagePriority(IntPtr process, int priority)
		{
			int status;
			return TrySetPagePriority(process, priority, out status);
		}

		public static bool TrySetPagePriority(IntPtr process, int priority, out int status)
		{
			status = NtSetInformationProcess(process, 39, ref priority, 4);
			return status == 0;
		}

		public static string ImagePath(IntPtr h)
		{
			try
			{
				int size = 600;
				StringBuilder stringBuilder = new StringBuilder(size);
				if (!QueryFullProcessImageName(h, 0, stringBuilder, ref size))
				{
					size = 32768;
					stringBuilder = new StringBuilder(size);
					if (!QueryFullProcessImageName(h, 0, stringBuilder, ref size))
					{
						return null;
					}
				}
				string stringBuilderText = stringBuilder.ToString();
				return (stringBuilderText.Length == 0) ? null : stringBuilderText;
			}
			catch
			{
				return null;
			}
		}

		public static bool StillActive(IntPtr h)
		{
			uint code;
			if (!GetExitCodeProcess(h, out code))
			{
				return true;
			}
			return code == 259;
		}

		public static string ImageName(IntPtr h)
		{
			string imagePathResult = ImagePath(h);
			if (imagePathResult == null)
			{
				return null;
			}
			int matchIndex = imagePathResult.LastIndexOf('\\');
			string substringResult = ((matchIndex >= 0) ? imagePathResult.Substring(matchIndex + 1) : imagePathResult);
			if (substringResult.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				substringResult = substringResult.Substring(0, substringResult.Length - 4);
			}
			return substringResult;
		}

		public static int ParentProcessId(IntPtr processHandle)
		{
			try
			{
				PROCESS_BASIC_INFORMATION info = default(PROCESS_BASIC_INFORMATION);
				int len = Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION));
				return (NtQueryInformationProcessBasic(processHandle, 0, ref info, len, IntPtr.Zero) == 0) ? info.InheritedFromUniqueProcessId.ToInt32() : 0;
			}
			catch
			{
				return 0;
			}
		}

		[DllImport("gdi32.dll")]
		public static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr h, out int cls);

		[DllImport("gdi32.dll")]
		public static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr h, int cls);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetThreadSelectedCpuSets(IntPtr thread, [Out] uint[] ids, uint count, out uint required);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint[] ids, uint count);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetThreadGroupAffinity(IntPtr thread, out ThreadGroupAffinity affinity);

		internal static bool ThreadAllowsMask(IntPtr thread, ulong mask)
		{
			ThreadGroupAffinity affinity;
			if (GetThreadGroupAffinity(thread, out affinity) && affinity.Group == 0 && mask != 0)
			{
				return (mask & ~affinity.Mask.ToUInt64()) == 0;
			}
			return false;
		}

		internal static uint[] QueryThreadCpuSets(IntPtr thread)
		{
			try
			{
				uint required;
				bool threadSelectedCpuSets = GetThreadSelectedCpuSets(thread, null, 0u, out required);
				if (required == 0)
				{
					return threadSelectedCpuSets ? new uint[0] : null;
				}
				if (required > 4096)
				{
					return null;
				}
				uint[] numericValueItems = new uint[required];
				uint required2;
				return (GetThreadSelectedCpuSets(thread, numericValueItems, required, out required2) && required2 == required) ? numericValueItems : null;
			}
			catch
			{
				return null;
			}
		}

		internal static bool WriteThreadCpuSets(IntPtr thread, uint[] ids)
		{
			try
			{
				return ids != null && SetThreadSelectedCpuSets(thread, (ids.Length == 0) ? null : ids, (uint)ids.Length);
			}
			catch
			{
				return false;
			}
		}
	}
}
