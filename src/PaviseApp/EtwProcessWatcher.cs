using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
	internal sealed class EtwProcessWatcher : IDisposable
	{
		private const ulong KeywordProcess = 16uL;

		private const ushort EventIdProcessStart = 1;

		private static readonly Guid KernelProcessProvider = new Guid("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");

		private long eventsSeen;

		public ulong Keywords = 16uL;

		private readonly string sessionName;

		private ulong session;

		private ulong trace = ulong.MaxValue;

		private IntPtr propsBuffer = IntPtr.Zero;

		private Thread pump;

		private Native.EventRecordCallback callback;

		private volatile bool stopping;

		public long EventsSeen
		{
			get
			{
				return Interlocked.Read(ref eventsSeen);
			}
		}

		public string LastError { get; private set; }

		public event Action<int, long> ProcessStarted;

		public EtwProcessWatcher(string name)
		{
			sessionName = (string.IsNullOrEmpty(name) ? "Pavise.ProcWatch" : name);
		}

		public bool Start()
		{
			int sizeOfResult = Marshal.SizeOf(typeof(Native.EventTraceProperties));
			int startValue = sizeOfResult + (sessionName.Length + 1) * 2 + 2;
			StopStaleSession(sizeOfResult, startValue);
			propsBuffer = Marshal.AllocHGlobal(startValue);
			for (int i = 0; i < startValue; i++)
			{
				Marshal.WriteByte(propsBuffer, i, 0);
			}
			Native.EventTraceProperties structure = default(Native.EventTraceProperties);
			structure.WnodeBufferSize = (uint)startValue;
			structure.WnodeFlags = 131072u;
			structure.WnodeClientContext = 1u;
			structure.LogFileMode = 256u;
			structure.BufferSize = 4u;
			structure.MinimumBuffers = 2u;
			structure.MaximumBuffers = 8u;
			structure.FlushTimer = 1u;
			structure.LoggerNameOffset = (uint)sizeOfResult;
			Marshal.StructureToPtr(structure, propsBuffer, false);
			int startTraceResult = Native.StartTrace(out session, sessionName, propsBuffer);
			if (startTraceResult != 0)
			{
				LastError = Lang.T("t.etwprocesswatcher.1") + startTraceResult;
				Cleanup();
				return false;
			}
			Native.EnableTraceParameters enableParameters = default(Native.EnableTraceParameters);
			enableParameters.Version = 2u;
			Guid providerId = KernelProcessProvider;
			startTraceResult = Native.EnableTraceEx2(session, ref providerId, 1u, 4, Keywords, 0uL, 0u, ref enableParameters);
			if (startTraceResult != 0)
			{
				LastError = Lang.T("t.etwprocesswatcher.2") + startTraceResult;
				Cleanup();
				return false;
			}
			callback = OnEvent;
			Native.EventTraceLogfile logfile = default(Native.EventTraceLogfile);
			logfile.LoggerName = Marshal.StringToHGlobalUni(sessionName);
			logfile.ProcessTraceMode = 268435712u;
			logfile.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(callback);
			logfile.LogfileHeader.TimeZone = new byte[172];
			trace = Native.OpenTrace(ref logfile);
			Marshal.FreeHGlobal(logfile.LoggerName);
			if (trace == ulong.MaxValue)
			{
				LastError = Lang.T("t.etwprocesswatcher.3") + Marshal.GetLastWin32Error();
				Cleanup();
				return false;
			}
			pump = new Thread(Pump);
			pump.IsBackground = true;
			pump.Start();
			return true;
		}

		private void StopStaleSession(int structSize, int total)
		{
			IntPtr intPtr = Marshal.AllocHGlobal(total);
			try
			{
				for (int i = 0; i < total; i++)
				{
					Marshal.WriteByte(intPtr, i, 0);
				}
				Native.EventTraceProperties structure = default(Native.EventTraceProperties);
				structure.WnodeBufferSize = (uint)total;
				structure.LoggerNameOffset = (uint)structSize;
				Marshal.StructureToPtr(structure, intPtr, false);
				Native.ControlTrace(0uL, sessionName, intPtr, 1u);
			}
			catch
			{
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		private void Pump()
		{
			ulong[] handles = new ulong[1] { trace };
			try
			{
				Native.ProcessTrace(handles, 1u, IntPtr.Zero, IntPtr.Zero);
			}
			catch
			{
			}
		}

		private void OnEvent(ref Native.EventRecord record)
		{
			if (stopping)
			{
				return;
			}
			Interlocked.Increment(ref eventsSeen);
			if (record.EventHeader.EventDescriptor.Id == 1 && !(record.UserData == IntPtr.Zero) && record.UserDataLength >= 4)
			{
				int arg;
				try
				{
					arg = Marshal.ReadInt32(record.UserData);
				}
				catch
				{
					return;
				}
				Action<int, long> processStarted = this.ProcessStarted;
				if (processStarted != null)
				{
					processStarted(arg, record.EventHeader.TimeStamp);
				}
			}
		}

		private void Cleanup()
		{
			if (trace != ulong.MaxValue)
			{
				try
				{
					Native.CloseTrace(trace);
				}
				catch
				{
				}
				trace = ulong.MaxValue;
			}
			if (session != 0 && propsBuffer != IntPtr.Zero)
			{
				try
				{
					Native.ControlTrace(session, sessionName, propsBuffer, 1u);
				}
				catch
				{
				}
				session = 0uL;
			}
			if (propsBuffer != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(propsBuffer);
				propsBuffer = IntPtr.Zero;
			}
		}

		public void Dispose()
		{
			stopping = true;
			Cleanup();
			if (pump != null)
			{
				try
				{
					pump.Join(3000);
				}
				catch
				{
				}
				pump = null;
			}
			callback = null;
		}
	}
}
