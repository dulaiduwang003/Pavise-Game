using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Threading;

namespace PaviseApp
{
	internal sealed class ProcNotify
	{
		private const int FastStartMinGapMs = 250;

		private const int WindowMs = 750;

		private const int MaxBatchChanges = 256;

		public Func<string, int, bool> CaptureStartIdentity;

		public Func<int, string, int, bool> CaptureParentIdentity;

		public Func<string, int, bool> FastTrack;

		private EtwProcessWatcher etw;

		private long lastFastStartTicks;

		private ManagementEventWatcher startW;

		private ManagementEventWatcher stopW;

		private Timer coalesce;

		private int pending;

		private int accepting;

		private int lifecycleGeneration;

		private readonly object lifecycleSync = new object();

		private readonly object batchSync = new object();

		private readonly object dispatchSync = new object();

		private readonly Dictionary<long, ProcessChange> changes = new Dictionary<long, ProcessChange>();

		private bool overflowed;

		private volatile bool active;

		private long sequence;

		public bool IsActive
		{
			get
			{
				return active;
			}
		}

		public event Action Changed;

		public event Action<ProcessChangeBatch> BatchChanged;

		public event Action FastStart;

		public void Start()
		{
			lock (dispatchSync)
			{
				lock (lifecycleSync)
				{
					if (Volatile.Read(ref accepting) != 0)
					{
						return;
					}
					int incrementResult = Interlocked.Increment(ref lifecycleGeneration);
					coalesce = new Timer(delegate(object state)
					{
						OnWindow(state);
					}, incrementResult, -1, -1);
					try
					{
						startW = Watch("Win32_ProcessStartTrace", ProcessChangeKind.Started, incrementResult);
						stopW = Watch("Win32_ProcessStopTrace", ProcessChangeKind.Stopped, incrementResult);
						Interlocked.Exchange(ref accepting, 1);
						active = true;
						StartFastPath();
					}
					catch (Exception ex)
					{
						active = false;
						Interlocked.Exchange(ref accepting, 0);
						Interlocked.Increment(ref lifecycleGeneration);
						Kill(ref startW);
						Kill(ref stopW);
						DisposeTimer();
						DrainPending();
						Logger.Warn(Lang.T("log.procnotify.1") + ex.Message);
					}
				}
			}
		}

		private void StartFastPath()
		{
			if (FastTrack == null)
			{
				return;
			}
			try
			{
				if (!Native.IsElevated())
				{
					return;
				}
				etw = new EtwProcessWatcher("Pavise.ProcWatch");
				etw.ProcessStarted += OnFastProcessStart;
				if (etw.Start())
				{
					Logger.Log(Lang.T("log.procnotify.2"));
					return;
				}
				Logger.Log(Lang.T("log.procnotify.3") + etw.LastError);
				try
				{
					etw.Dispose();
				}
				catch
				{
				}
				etw = null;
			}
			catch
			{
				etw = null;
			}
		}

		private void OnFastProcessStart(int pid, long stamp)
		{
			try
			{
				if (FastTrack == null || this.FastStart == null || pid <= 4)
				{
					return;
				}
				IntPtr intPtr = Native.OpenProcess(4096, false, pid);
				if (intPtr == IntPtr.Zero)
				{
					return;
				}
				string name;
				int sessionId;
				try
				{
					name = ResolveCurrentProcessName(Native.ImagePath(intPtr));
					if (!Native.TryGetLiveProcessSessionId(intPtr, pid, out sessionId))
					{
						return;
					}
				}
				finally
				{
					Native.CloseHandle(intPtr);
				}
				FastTrackMaybe(name, sessionId);
			}
			catch
			{
			}
		}

		private void FastTrackMaybe(string name, int session)
		{
			try
			{
				Func<string, int, bool> fastTrack = FastTrack;
				Action fastStart = this.FastStart;
				if (fastTrack != null && fastStart != null && !string.IsNullOrEmpty(name) && fastTrack(name, session))
				{
					long ticks = DateTime.UtcNow.Ticks;
					long lastFastStartTicksSnapshot = Interlocked.Read(ref lastFastStartTicks);
					if ((lastFastStartTicksSnapshot == 0 || ticks - lastFastStartTicksSnapshot >= 2500000) && Interlocked.CompareExchange(ref lastFastStartTicks, ticks, lastFastStartTicksSnapshot) == lastFastStartTicksSnapshot)
					{
						fastStart();
					}
				}
			}
			catch
			{
			}
		}

		private ManagementEventWatcher Watch(string cls, ProcessChangeKind kind, int generation)
		{
			ManagementEventWatcher managementEventWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM " + cls));
			managementEventWatcher.Scope.Options.EnablePrivileges = true;
			managementEventWatcher.EventArrived += delegate(object sender, EventArrivedEventArgs args)
			{
				OnEvent(kind, args, generation);
			};
			try
			{
				managementEventWatcher.Start();
				return managementEventWatcher;
			}
			catch
			{
				try
				{
					managementEventWatcher.Dispose();
				}
				catch
				{
				}
				throw;
			}
		}

		private void OnEvent(ProcessChangeKind kind, EventArrivedEventArgs e, int generation)
		{
			if (!IsAccepting(generation))
			{
				DisposeNewEvent(e);
				return;
			}
			ProcessChange processChange = null;
			try
			{
				ManagementBaseObject newEvent = e.NewEvent;
				if (newEvent != null)
				{
					try
					{
						int pidValue = Convert.ToInt32(newEvent["ProcessID"]);
						string name = Convert.ToString(newEvent["ProcessName"]);
						if (pidValue > 0)
						{
							ProcessChange processChange2 = new ProcessChange();
							processChange2.Pid = pidValue;
							processChange2.Name = StripExe(name);
							processChange2.Kind = kind;
							processChange2.Sequence = Interlocked.Increment(ref sequence);
							processChange = processChange2;
							try
							{
								processChange.ParentPid = Convert.ToInt32(newEvent["ParentProcessID"]);
							}
							catch
							{
							}
							try
							{
								processChange.Session = Convert.ToInt32(newEvent["SessionID"]);
							}
							catch
							{
							}
							if (kind == ProcessChangeKind.Started)
							{
								bool onEventReady = false;
								try
								{
									Func<string, int, bool> captureStartIdentity = CaptureStartIdentity;
									onEventReady = captureStartIdentity != null && captureStartIdentity(processChange.Name, processChange.Session);
								}
								catch
								{
								}
								bool onEventReady2 = false;
								try
								{
									Func<int, string, int, bool> captureParentIdentity = CaptureParentIdentity;
									onEventReady2 = captureParentIdentity != null && captureParentIdentity(processChange.ParentPid, processChange.Name, processChange.Session);
								}
								catch
								{
								}
								if (onEventReady || onEventReady2)
								{
									int parentPid = processChange.ParentPid;
									int session = processChange.Session;
									processChange.ParentPid = 0;
									processChange.ParentCreation = 0L;
									processChange.Session = -1;
									processChange.Name = "";
									processChange.Path = null;
									processChange.Creation = 0L;
									IntPtr intPtr = Native.OpenProcess(1052672, false, pidValue);
									if (intPtr != IntPtr.Zero)
									{
										try
										{
											string pathValue = Native.ImagePath(intPtr);
											string nameValue = ResolveCurrentProcessName(pathValue);
											int sessionId;
											int sessionValue = ResolveVerifiedSessionId(session, Native.TryGetLiveProcessSessionId(intPtr, pidValue, out sessionId), sessionId);
											long creation;
											long cpu;
											ulong io;
											if (sessionValue >= 0 && nameValue.Length > 0 && Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && creation > 0)
											{
												processChange.Session = sessionValue;
												processChange.Name = nameValue;
												processChange.Path = pathValue;
												processChange.Creation = creation;
												processChange.ParentPid = ResolveVerifiedParentPid(parentPid, Native.ParentProcessId(intPtr));
											}
										}
										finally
										{
											Native.CloseHandle(intPtr);
										}
									}
								}
								if (onEventReady2 && processChange.ParentPid > 0)
								{
									IntPtr intPtr2 = Native.OpenProcess(4096, false, processChange.ParentPid);
									if (intPtr2 != IntPtr.Zero)
									{
										try
										{
											long cpu2;
											ulong io2;
											Native.QueryProcessSample(intPtr2, out processChange.ParentCreation, out cpu2, out io2);
										}
										finally
										{
											Native.CloseHandle(intPtr2);
										}
									}
								}
							}
						}
					}
					finally
					{
						newEvent.Dispose();
					}
				}
			}
			catch
			{
			}
			if (processChange == null)
			{
				return;
			}
			if (processChange.Kind == ProcessChangeKind.Started)
			{
				FastTrackMaybe(processChange.Name, processChange.Session);
			}
			Timer timer = null;
			bool onEventReady3 = false;
			lock (batchSync)
			{
				if (!IsAccepting(generation))
				{
					return;
				}
				long key = (long)((ulong)(uint)processChange.Pid << 1) | ((processChange.Kind == ProcessChangeKind.Stopped) ? 1L : 0L);
				if (changes.Count < 256 || changes.ContainsKey(key))
				{
					changes[key] = processChange;
				}
				else
				{
					overflowed = true;
				}
				if (Interlocked.Exchange(ref pending, 1) == 0)
				{
					timer = coalesce;
					onEventReady3 = true;
				}
			}
			if (!onEventReady3)
			{
				return;
			}
			try
			{
				if (IsAccepting(generation) && timer != null)
				{
					timer.Change(750, -1);
				}
			}
			catch
			{
			}
		}

		private void OnWindow(object state)
		{
			int generation = ((state is int) ? ((int)state) : (-1));
			lock (dispatchSync)
			{
				if (!IsAccepting(generation))
				{
					return;
				}
				ProcessChangeBatch processChangeBatch;
				lock (batchSync)
				{
					ProcessChange[] processChangeItems = new ProcessChange[changes.Count];
					changes.Values.CopyTo(processChangeItems, 0);
					Array.Sort(processChangeItems, (ProcessChange left, ProcessChange right) => left.Sequence.CompareTo(right.Sequence));
					processChangeBatch = new ProcessChangeBatch(processChangeItems, overflowed);
					changes.Clear();
					overflowed = false;
					Interlocked.Exchange(ref pending, 0);
				}
				if (processChangeBatch.Changes.Length == 0 && !processChangeBatch.Overflowed)
				{
					return;
				}
				Action<ProcessChangeBatch> batchChanged = this.BatchChanged;
				if (batchChanged != null)
				{
					Delegate[] invocationList = batchChanged.GetInvocationList();
					for (int i = 0; i < invocationList.Length; i++)
					{
						Action<ProcessChangeBatch> action = (Action<ProcessChangeBatch>)invocationList[i];
						if (!IsAccepting(generation))
						{
							return;
						}
						try
						{
							action(processChangeBatch);
						}
						catch
						{
						}
					}
				}
				Action changed = this.Changed;
				if (changed == null)
				{
					return;
				}
				Delegate[] invocationList2 = changed.GetInvocationList();
				for (int j = 0; j < invocationList2.Length; j++)
				{
					Action action2 = (Action)invocationList2[j];
					if (!IsAccepting(generation))
					{
						break;
					}
					try
					{
						action2();
					}
					catch
					{
					}
				}
			}
		}

		public void Stop()
		{
			lock (dispatchSync)
			{
				lock (lifecycleSync)
				{
					Interlocked.Exchange(ref accepting, 0);
					Interlocked.Increment(ref lifecycleGeneration);
					active = false;
					if (etw != null)
					{
						try
						{
							etw.Dispose();
						}
						catch
						{
						}
						etw = null;
					}
					Kill(ref startW);
					Kill(ref stopW);
					DisposeTimer();
					DrainPending();
				}
			}
		}

		private bool IsAccepting(int generation)
		{
			if (Volatile.Read(ref accepting) != 0)
			{
				return Volatile.Read(ref lifecycleGeneration) == generation;
			}
			return false;
		}

		private void DisposeTimer()
		{
			Timer timer = coalesce;
			coalesce = null;
			if (timer != null)
			{
				try
				{
					timer.Dispose();
				}
				catch
				{
				}
			}
		}

		private void DrainPending()
		{
			lock (dispatchSync)
			{
				lock (batchSync)
				{
					changes.Clear();
					overflowed = false;
					Interlocked.Exchange(ref pending, 0);
				}
			}
		}

		private static void Kill(ref ManagementEventWatcher w)
		{
			if (w != null)
			{
				try
				{
					w.Stop();
				}
				catch
				{
				}
				try
				{
					w.Dispose();
				}
				catch
				{
				}
				w = null;
			}
		}

		private static string StripExe(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return "";
			}
			if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return name;
			}
			return name.Substring(0, name.Length - 4);
		}

		internal static int ResolveVerifiedParentPid(int eventParentPid, int currentParentPid)
		{
			if (currentParentPid <= 0)
			{
				return 0;
			}
			if (eventParentPid > 0 && eventParentPid != currentParentPid)
			{
				return 0;
			}
			return currentParentPid;
		}

		internal static int ResolveVerifiedSessionId(int eventSession, bool currentSessionKnown, int currentSession)
		{
			if (!currentSessionKnown || eventSession < 0 || currentSession < 0 || eventSession != currentSession)
			{
				return -1;
			}
			return currentSession;
		}

		internal static string ResolveCurrentProcessName(string currentImagePath)
		{
			if (string.IsNullOrEmpty(currentImagePath))
			{
				return "";
			}
			try
			{
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(currentImagePath);
				return string.IsNullOrEmpty(fileNameWithoutExtension) ? "" : fileNameWithoutExtension;
			}
			catch
			{
				return "";
			}
		}

		private static void DisposeNewEvent(EventArrivedEventArgs e)
		{
			try
			{
				ManagementBaseObject managementBaseObject = ((e == null) ? null : e.NewEvent);
				if (managementBaseObject != null)
				{
					managementBaseObject.Dispose();
				}
			}
			catch
			{
			}
		}
	}
}
