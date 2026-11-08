using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
	internal sealed class InterruptAttribution
	{
		private sealed class RoutineStat
		{
			public long Count;

			public long TotalTicks;

			public long MaxTicks;

			public long BadDuration;

			public ulong CpuMask;

			public bool CpuMaskTruncated;

			public readonly long[] Buckets = new long[13];
		}

		private sealed class Module
		{
			public ulong Base;

			public ulong End;

			public string Name;
		}

#pragma warning disable 0649
		private struct WnodeHeader
		{
			public uint BufferSize;

			public uint ProviderId;

			public ulong HistoricalContext;

			public long TimeStamp;

			public Guid Guid;

			public uint ClientContext;

			public uint Flags;
		}

		private struct EventTraceProperties
		{
			public WnodeHeader Wnode;

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

		private struct EventTraceHeader
		{
			public ushort Size;

			public ushort FieldTypeFlags;

			public byte Type;

			public byte Level;

			public ushort Version;

			public uint ThreadId;

			public uint ProcessId;

			public long TimeStamp;

			public Guid Guid;

			public uint KernelTime;

			public uint UserTime;
		}

#pragma warning disable 0649
		private struct EventDescriptor
		{
			public ushort Id;

			public byte Version;

			public byte Channel;

			public byte Level;

			public byte Opcode;

			public ushort Task;

			public ulong Keyword;
		}

		private struct EventHeader
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

		private struct EtwBufferContext
		{
			public ushort ProcessorIndex;

			public ushort LoggerId;
		}

		private struct EventRecord
		{
			public EventHeader EventHeader;

			public EtwBufferContext BufferContext;

			public ushort ExtendedDataCount;

			public ushort UserDataLength;

			public IntPtr ExtendedData;

			public IntPtr UserData;

			public IntPtr UserContext;
		}
#pragma warning restore 0649

		private delegate void EventRecordCallback(ref EventRecord record);

		private struct EventTrace
		{
			public EventTraceHeader Header;

			public uint InstanceId;

			public uint ParentInstanceId;

			public Guid ParentGuid;

			public IntPtr MofData;

			public uint MofLength;

			public uint ClientContext;
		}

		private struct SystemTime
		{
			public ushort Year;

			public ushort Month;

			public ushort DayOfWeek;

			public ushort Day;

			public ushort Hour;

			public ushort Minute;

			public ushort Second;

			public ushort Milliseconds;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct TimeZoneInformation
		{
			public int Bias;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
			public string StandardName;

			public SystemTime StandardDate;

			public int StandardBias;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
			public string DaylightName;

			public SystemTime DaylightDate;

			public int DaylightBias;
		}

		private struct TraceLogfileHeader
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

			public TimeZoneInformation TimeZone;

			public long BootTime;

			public long PerfFreq;

			public long StartTime;

			public uint ReservedFlags;

			public uint BuffersLost;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct EventTraceLogfile
		{
			public IntPtr LogFileName;

			public IntPtr LoggerName;

			public long CurrentTime;

			public uint BuffersRead;

			public uint ProcessTraceMode;

			public EventTrace CurrentEvent;

			public TraceLogfileHeader LogfileHeader;

			public IntPtr BufferCallback;

			public uint BufferSize;

			public uint Filled;

			public uint EventsLost;

			public IntPtr EventRecordCallbackPtr;

			public uint IsKernelTrace;

			public IntPtr Context;
		}

		private const int WnodeFlagTracedGuid = 131072;

		private const uint RealTimeMode = 256u;

		private const uint SystemLoggerMode = 33554432u;

		private const uint IndependentSessionMode = 134217728u;

		private const uint ProcessModeRealTime = 256u;

		private const uint ProcessModeRawTimestamp = 4096u;

		private const uint ProcessModeEventRecord = 268435456u;

		private const uint ControlStop = 1u;

		private const uint FlagDpc = 32u;

		private const uint FlagInterrupt = 64u;

		private const int ErrorAccessDenied = 5;

		private const int ErrorAlreadyExists = 183;

		private const int ErrorInvalidParameter = 87;

		private const string SessionName = "PaviseInterruptProbe";

		private const string AliveEventName = "Global\\Pavise_InterruptProbeAlive";

		internal const int BucketCount = 13;

		private const int BucketOver500 = 9;

		private const int BucketOver1Ms = 10;

		private const ushort HeaderFlag32Bit = 32;

		private const ushort HeaderFlag64Bit = 64;

		private static readonly Guid SessionGuid = new Guid("7d1f8c2a-64b3-4c5e-9a1d-2e8f0b6c4d3a");

		private static readonly Guid PerfInfoGuid = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");

		private EventWaitHandle aliveOwned;

		internal static readonly double[] BucketUpperUs = new double[13]
		{
			1.0,
			2.0,
			5.0,
			10.0,
			25.0,
			50.0,
			100.0,
			250.0,
			500.0,
			1000.0,
			2000.0,
			5000.0,
			double.MaxValue
		};

		private readonly object gate = new object();

		private readonly Dictionary<ulong, RoutineStat> dpcHits = new Dictionary<ulong, RoutineStat>();

		private readonly Dictionary<ulong, RoutineStat> isrHits = new Dictionary<ulong, RoutineStat>();

		private readonly List<Module> modules = new List<Module>();

		private double usPerTick;

		private long sanityMaxTicks;

		private long dpcTotal;

		private long isrTotal;

		private ulong traceHandle;

		private Thread worker;

		private EventRecordCallback keepAlive;

		private volatile bool started;

		private volatile bool stopRequested;

		private volatile bool consumerExitedEarly;

		private volatile bool processTraceSucceeded;

		public bool Busy { get; private set; }

		public string FailDetail { get; private set; }

		public static string StartFailureText(InterruptAttribution ia)
		{
			if (ia == null)
			{
				return Lang.T("irqmove.nosession");
			}
			if (ia.Busy)
			{
				return Lang.T("irq.probe.busy");
			}
			if (!string.IsNullOrEmpty(ia.FailDetail))
			{
				return ia.FailDetail;
			}
			return Lang.T("irqmove.nosession");
		}

		private bool TakeOwnership()
		{
			try
			{
				EventWaitHandle result;
				if (EventWaitHandle.TryOpenExisting("Global\\Pavise_InterruptProbeAlive", out result))
				{
					using (result)
					{
					}
					return false;
				}
			}
			catch
			{
			}
			try
			{
				bool createdNew;
				EventWaitHandle eventWaitHandle2 = new EventWaitHandle(false, EventResetMode.ManualReset, "Global\\Pavise_InterruptProbeAlive", out createdNew);
				if (!createdNew)
				{
					eventWaitHandle2.Close();
					return false;
				}
				aliveOwned = eventWaitHandle2;
				return true;
			}
			catch
			{
				return true;
			}
		}

		private void ReleaseOwnership()
		{
			EventWaitHandle eventWaitHandle = aliveOwned;
			aliveOwned = null;
			if (eventWaitHandle != null)
			{
				try
				{
					eventWaitHandle.Close();
				}
				catch
				{
				}
			}
		}

		public bool Start()
		{
			lock (gate)
			{
				if (started)
				{
					return true;
				}
				Busy = false;
				FailDetail = null;
				if (!TakeOwnership())
				{
					Busy = true;
					Logger.Log(Lang.T("log.interruptattribution.9"));
					return false;
				}
				Native.TryEnableDebugPrivilege();
				string error;
				if (!LoadModules(out error))
				{
					FailDetail = error;
					Logger.Warn("IRQ " + error);
					ReleaseOwnership();
					return false;
				}
				uint enableFlags = 96u;
				IntPtr intPtr = AllocProps(enableFlags);
				try
				{
					ulong handle;
					uint num = StartTrace(out handle, "PaviseInterruptProbe", intPtr);
					if (num == 183)
					{
						StopStale();
						Marshal.FreeHGlobal(intPtr);
						intPtr = AllocProps(enableFlags);
						num = StartTrace(out handle, "PaviseInterruptProbe", intPtr);
					}
					switch (num)
					{
					case 87u:
						Logger.Log(Lang.T("log.interruptattribution.1"));
						FailDetail = Lang.T("irq.fail.nokernel");
						ReleaseOwnership();
						return false;
					default:
					{
						Logger.Warn(Lang.T("log.interruptattribution.2") + num);
						bool flag = false;
						try
						{
							flag = Native.IsElevated();
						}
						catch
						{
						}
						FailDetail = ((num != 5) ? (Lang.T("irq.fail.start") + num) : (flag ? Lang.T("irq.fail.denied") : Lang.T("irq.state.needadmin")));
						ReleaseOwnership();
						return false;
					}
					case 0u:
						break;
					}
				}
				finally
				{
					Marshal.FreeHGlobal(intPtr);
				}
				keepAlive = OnEvent;
				EventTraceLogfile logfile = default(EventTraceLogfile);
				logfile.LoggerName = Marshal.StringToHGlobalUni("PaviseInterruptProbe");
				logfile.ProcessTraceMode = 268439808u;
				logfile.EventRecordCallbackPtr = Marshal.GetFunctionPointerForDelegate(keepAlive);
				traceHandle = OpenTrace(ref logfile);
				if (traceHandle == ulong.MaxValue || traceHandle == 0)
				{
					int lastWin32Error = Marshal.GetLastWin32Error();
					Logger.Warn(Lang.T("log.interruptattribution.3") + lastWin32Error);
					FailDetail = Lang.T("irq.fail.open") + lastWin32Error;
					StopStale();
					ReleaseOwnership();
					return false;
				}
				long num2 = logfile.LogfileHeader.PerfFreq;
				if (num2 <= 0)
				{
					num2 = Stopwatch.Frequency;
				}
				usPerTick = 1000000.0 / (double)num2;
				sanityMaxTicks = num2;
				worker = new Thread(RunProcessTrace);
				worker.IsBackground = true;
				try
				{
					worker.Priority = ThreadPriority.Highest;
				}
				catch
				{
				}
				stopRequested = false;
				consumerExitedEarly = false;
				processTraceSucceeded = false;
				worker.Start();
				started = true;
				return true;
			}
		}

		private void RunProcessTrace()
		{
			try
			{
				ulong[] handles = new ulong[1] { traceHandle };
				uint num = ProcessTrace(handles, 1u, IntPtr.Zero, IntPtr.Zero);
				processTraceSucceeded = num == 0;
				if (num != 0)
				{
					consumerExitedEarly = true;
				}
			}
			catch
			{
				consumerExitedEarly = true;
			}
			finally
			{
				if (!stopRequested)
				{
					consumerExitedEarly = true;
				}
			}
		}

		internal static bool CaptureComplete(bool stopSucceeded, bool workerDone, bool processSucceeded, bool exitedEarly)
		{
			if (stopSucceeded && workerDone && processSucceeded)
			{
				return !exitedEarly;
			}
			return false;
		}

		internal static bool HasMappedEvents(long rawDpc, long rawIsr, int mappedDrivers)
		{
			if (rawDpc >= 0 && rawIsr >= 0 && (rawDpc > 0 || rawIsr > 0))
			{
				return mappedDrivers > 0;
			}
			return false;
		}

		public InterruptAttributionResult Stop()
		{
			InterruptAttributionResult interruptAttributionResult = new InterruptAttributionResult();
			lock (gate)
			{
				if (!started)
				{
					interruptAttributionResult.Error = Lang.T("t.interruptattribution.4");
					return interruptAttributionResult;
				}
				stopRequested = true;
				uint eventsLost;
				uint buffersLost;
				uint error;
				bool flag = StopStale(out eventsLost, out buffersLost, out error);
				interruptAttributionResult.EventsLost = eventsLost;
				interruptAttributionResult.BuffersLost = buffersLost;
				if (!flag)
				{
					try
					{
						if (traceHandle != 0)
						{
							CloseTrace(traceHandle);
						}
					}
					catch
					{
					}
				}
				bool flag2 = true;
				if (worker != null)
				{
					try
					{
						flag2 = worker.Join(10000);
					}
					catch
					{
						flag2 = false;
					}
				}
				if (flag)
				{
					try
					{
						if (traceHandle != 0)
						{
							CloseTrace(traceHandle);
						}
					}
					catch
					{
					}
				}
				interruptAttributionResult.Incomplete = !CaptureComplete(flag, flag2, processTraceSucceeded, consumerExitedEarly);
				started = false;
				ReleaseOwnership();
				if (!flag2)
				{
					interruptAttributionResult.Error = Lang.T("t.interruptattribution.5");
					return interruptAttributionResult;
				}
				keepAlive = null;
				Dictionary<string, DriverInterrupt> dictionary = new Dictionary<string, DriverInterrupt>();
				Dictionary<string, long[]> dictionary2 = new Dictionary<string, long[]>();
				Fold(dictionary, dictionary2, dpcHits, true);
				Fold(dictionary, dictionary2, isrHits, false);
				foreach (KeyValuePair<string, DriverInterrupt> item in dictionary)
				{
					DriverInterrupt value = item.Value;
					long[] value2;
					if (dictionary2.TryGetValue(item.Key, out value2))
					{
						value.DpcOver500Us = SumFrom(value2, 9);
						value.DpcOver1Ms = SumFrom(value2, 10);
						value.DpcBuckets = (long[])value2.Clone();
					}
					interruptAttributionResult.Drivers.Add(value);
				}
				interruptAttributionResult.Drivers.Sort(delegate(DriverInterrupt a, DriverInterrupt b)
				{
					long value3 = a.Dpc + a.Isr;
					return (b.Dpc + b.Isr).CompareTo(value3);
				});
				long num = 0L;
				foreach (DriverInterrupt driver in interruptAttributionResult.Drivers)
				{
					num += driver.Dpc;
				}
				interruptAttributionResult.Unmapped = Math.Max(0L, dpcTotal - num);
				bool flag3 = (dpcTotal > 0 || isrTotal > 0) && interruptAttributionResult.Drivers.Count == 0;
				string text = (flag3 ? ("已采集中断事件，但无法映射到驱动模块，本局中断归因不可用 DPC=" + dpcTotal + " ISR=" + isrTotal + " 模块=" + modules.Count) : null);
				if (flag3)
				{
					Logger.Warn("IRQ " + text);
				}
				interruptAttributionResult.Ok = HasMappedEvents(dpcTotal, isrTotal, interruptAttributionResult.Drivers.Count) && !interruptAttributionResult.Incomplete && !interruptAttributionResult.Lossy;
				if (interruptAttributionResult.Lossy)
				{
					Logger.Log(Lang.F("log.interruptattribution.lossy", interruptAttributionResult.EventsLost, interruptAttributionResult.BuffersLost));
				}
				if (interruptAttributionResult.Incomplete)
				{
					interruptAttributionResult.Error = "ETW 消费或停止未完整 win32=" + error;
				}
				else if (interruptAttributionResult.Lossy)
				{
					interruptAttributionResult.Error = Lang.F("t.interruptattribution.7", interruptAttributionResult.EventsLost, interruptAttributionResult.BuffersLost);
				}
				else if (flag3)
				{
					interruptAttributionResult.Error = text;
				}
				else if (!interruptAttributionResult.Ok)
				{
					interruptAttributionResult.Error = Lang.T("t.interruptattribution.6");
				}
				return interruptAttributionResult;
			}
		}

		private void Fold(Dictionary<string, DriverInterrupt> byMod, Dictionary<string, long[]> dpcBuckets, Dictionary<ulong, RoutineStat> hits, bool dpc)
		{
			foreach (KeyValuePair<ulong, RoutineStat> hit in hits)
			{
				string text = Resolve(hit.Key);
				if (text == null)
				{
					continue;
				}
				DriverInterrupt value;
				if (!byMod.TryGetValue(text, out value))
				{
					DriverInterrupt driverInterrupt = new DriverInterrupt();
					driverInterrupt.Driver = text;
					value = (byMod[text] = driverInterrupt);
				}
				RoutineStat value2 = hit.Value;
				if (dpc)
				{
					value.CpuMask |= value2.CpuMask;
					value.CpuMaskTruncated |= value2.CpuMaskTruncated;
				}
				value.BadDuration += value2.BadDuration;
				double num = (double)value2.TotalTicks * usPerTick;
				double num2 = (double)value2.MaxTicks * usPerTick;
				if (dpc)
				{
					value.Dpc += value2.Count;
					value.DpcTotalUs += num;
					if (num2 > value.DpcMaxUs)
					{
						value.DpcMaxUs = num2;
					}
					long[] value3;
					if (!dpcBuckets.TryGetValue(text, out value3))
					{
						value3 = (dpcBuckets[text] = new long[13]);
					}
					for (int i = 0; i < 13; i++)
					{
						value3[i] += value2.Buckets[i];
					}
				}
				else
				{
					value.Isr += value2.Count;
				}
			}
		}

		internal static long SumFrom(long[] b, int startIndex)
		{
			long num = 0L;
			if (b == null)
			{
				return 0L;
			}
			for (int i = startIndex; i < 13; i++)
			{
				num += b[i];
			}
			return num;
		}

		private void OnEvent(ref EventRecord record)
		{
			if (record.EventHeader.ProviderId != PerfInfoGuid)
			{
				return;
			}
			byte opcode = record.EventHeader.EventDescriptor.Opcode;
			bool flag = opcode == 67;
			bool flag2 = opcode == 66 || opcode == 68 || opcode == 69;
			if ((!flag && !flag2) || record.UserData == IntPtr.Zero)
			{
				return;
			}
			ushort flags = record.EventHeader.Flags;
			int num = (((flags & 0x40u) != 0) ? 8 : (((flags & 0x20u) != 0) ? 4 : IntPtr.Size));
			if (record.UserDataLength < 8 + num)
			{
				return;
			}
			long num2 = record.UserData.ToInt64();
			long num3 = Marshal.ReadInt64(new IntPtr(num2));
			ulong key = (ulong)((num == 8) ? Marshal.ReadInt64(new IntPtr(num2 + 8)) : ((uint)Marshal.ReadInt32(new IntPtr(num2 + 8))));
			long timeStamp = record.EventHeader.TimeStamp;
			long num4 = timeStamp - num3;
			bool flag3 = num4 >= 0 && num4 <= sanityMaxTicks;
			Dictionary<ulong, RoutineStat> dictionary = (flag ? isrHits : dpcHits);
			RoutineStat value;
			if (!dictionary.TryGetValue(key, out value))
			{
				value = (dictionary[key] = new RoutineStat());
			}
			value.Count++;
			ushort processorIndex = record.BufferContext.ProcessorIndex;
			if (processorIndex < 64)
			{
				value.CpuMask |= (ulong)(1L << (int)processorIndex);
			}
			else
			{
				value.CpuMaskTruncated = true;
			}
			if (!flag3)
			{
				value.BadDuration++;
			}
			else
			{
				value.TotalTicks += num4;
				if (num4 > value.MaxTicks)
				{
					value.MaxTicks = num4;
				}
				value.Buckets[BucketOf(num4)]++;
			}
			if (flag)
			{
				isrTotal++;
			}
			else
			{
				dpcTotal++;
			}
		}

		private int BucketOf(long ticks)
		{
			double num = (double)ticks * usPerTick;
			for (int i = 0; i < 12; i++)
			{
				if (num < BucketUpperUs[i])
				{
					return i;
				}
			}
			return 12;
		}

		private string Resolve(ulong addr)
		{
			for (int i = 0; i < modules.Count; i++)
			{
				if (addr >= modules[i].Base && addr < modules[i].End)
				{
					return modules[i].Name;
				}
			}
			return null;
		}

		private static bool StopStale(out uint eventsLost, out uint buffersLost, out uint error)
		{
			eventsLost = 0u;
			buffersLost = 0u;
			error = 0u;
			int num = ("PaviseInterruptProbe".Length + 1) * 2;
			int num2 = Marshal.SizeOf(typeof(EventTraceProperties)) + num + 16;
			IntPtr intPtr = Marshal.AllocHGlobal(num2);
			try
			{
				for (int i = 0; i < num2; i++)
				{
					Marshal.WriteByte(intPtr, i, 0);
				}
				EventTraceProperties structure = default(EventTraceProperties);
				structure.Wnode.BufferSize = (uint)num2;
				structure.Wnode.Guid = SessionGuid;
				structure.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
				Marshal.StructureToPtr(structure, intPtr, false);
				uint num3 = ControlTrace(0uL, "PaviseInterruptProbe", intPtr, 1u);
				if (num3 != 0)
				{
					error = num3;
					return false;
				}
				EventTraceProperties eventTraceProperties = (EventTraceProperties)Marshal.PtrToStructure(intPtr, typeof(EventTraceProperties));
				eventsLost = eventTraceProperties.EventsLost;
				buffersLost = eventTraceProperties.RealTimeBuffersLost + eventTraceProperties.LogBuffersLost;
				return true;
			}
			catch
			{
				error = uint.MaxValue;
				return false;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern uint StartTrace(out ulong handle, string name, IntPtr props);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern uint ControlTrace(ulong handle, string name, IntPtr props, uint code);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern ulong OpenTrace(ref EventTraceLogfile logfile);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern uint CloseTrace(ulong handle);

		[DllImport("ntdll.dll")]
		private static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);

		private bool LoadModules(out string error)
		{
			modules.Clear();
			List<Module> loaded;
			if (!TryReadLoadedModules(out loaded, out error))
			{
				return false;
			}
			modules.AddRange(loaded);
			return true;
		}

		private static bool TryReadLoadedModules(out List<Module> loaded, out string error)
		{
			loaded = new List<Module>();
			error = null;
			IntPtr intPtr = IntPtr.Zero;
			try
			{
				int ret = 0;
				NtQuerySystemInformation(11, IntPtr.Zero, 0, out ret);
				ret = checked(Math.Max(ret, 1048576) + 65536);
				intPtr = Marshal.AllocHGlobal(ret);
				int ret2;
				int num = NtQuerySystemInformation(11, intPtr, ret, out ret2);
				if (num != 0)
				{
					uint num2 = (uint)num;
					error = "驱动模块枚举失败，无法启动中断归因 NTSTATUS=0x" + num2.ToString("X8");
					return false;
				}
				int num3 = Marshal.ReadInt32(intPtr);
				long num4 = intPtr.ToInt64() + IntPtr.Size;
				int num5 = 296;
				if (num3 < 0 || num3 > (ret - IntPtr.Size) / num5)
				{
					error = "驱动模块枚举返回的长度无效，无法启动中断归因";
					return false;
				}
				for (int i = 0; i < num3; i++)
				{
					long num6 = num4 + (long)i * (long)num5;
					ulong num7 = (ulong)Marshal.ReadInt64(new IntPtr(num6 + 16));
					uint num8 = (uint)Marshal.ReadInt32(new IntPtr(num6 + 24));
					if (num7 != 0 && num8 != 0)
					{
						string text = Marshal.PtrToStringAnsi(new IntPtr(num6 + 40));
						if (!string.IsNullOrEmpty(text))
						{
							int num9 = text.LastIndexOf('\\');
							string name = ((num9 >= 0) ? text.Substring(num9 + 1) : text);
							loaded.Add(new Module
							{
								Base = num7,
								End = num7 + num8,
								Name = name
							});
						}
					}
				}
				if (loaded.Count == 0)
				{
					error = "驱动模块枚举未返回可用地址，无法启动中断归因";
					return false;
				}
				return true;
			}
			catch (Exception ex)
			{
				loaded.Clear();
				error = "驱动模块枚举异常，无法启动中断归因 " + ex.GetType().Name;
				return false;
			}
			finally
			{
				if (intPtr != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(intPtr);
				}
			}
		}

		private static IntPtr AllocProps(uint enableFlags)
		{
			int num = ("PaviseInterruptProbe".Length + 1) * 2;
			int num2 = Marshal.SizeOf(typeof(EventTraceProperties)) + num + 16;
			IntPtr intPtr = Marshal.AllocHGlobal(num2);
			for (int i = 0; i < num2; i++)
			{
				Marshal.WriteByte(intPtr, i, 0);
			}
			EventTraceProperties structure = default(EventTraceProperties);
			structure.Wnode.BufferSize = (uint)num2;
			structure.Wnode.Flags = 131072u;
			structure.Wnode.Guid = SessionGuid;
			structure.Wnode.ClientContext = 1u;
			structure.BufferSize = 128u;
			structure.MinimumBuffers = 64u;
			structure.MaximumBuffers = 256u;
			structure.LogFileMode = 167772416u;
			structure.FlushTimer = 1u;
			structure.EnableFlags = enableFlags;
			structure.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
			Marshal.StructureToPtr(structure, intPtr, false);
			return intPtr;
		}

		public static void CleanupStaleSession()
		{
			try
			{
				StopStale();
			}
			catch
			{
			}
		}

		public static void HealFromCrash()
		{
			StopStale();
		}

		private static void StopStale()
		{
			uint eventsLost;
			uint buffersLost;
			uint error;
			StopStale(out eventsLost, out buffersLost, out error);
		}
	}
}
