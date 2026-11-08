using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
	internal static class FrameCoordinationSampler
	{
		private struct Property
		{
			internal ulong Name;

			internal uint Index;

			internal uint Reserved;
		}

		internal sealed class Evidence
		{
			internal FrameCoordinationSample Sample;

			internal long Begin;

			internal long End;

			internal long Half;

			internal int Count;

			internal int RelevantCount;

			internal int SchedulerSwitches;

			internal bool PresenterOnly;

			internal bool FixedThreadSet;

			internal volatile bool Invalid;

			internal FrameCaptureIssue Fault;

			internal IntPtr FlagsName;

			private readonly byte[] flagBuffer = new byte[4];

			private readonly Dictionary<int, int> pending = new Dictionary<int, int>();

			internal readonly Dictionary<int, int>[] Presents = new Dictionary<int, int>[2]
			{
				new Dictionary<int, int>(),
				new Dictionary<int, int>()
			};

			internal readonly Dictionary<long, int>[] Edges = new Dictionary<long, int>[2]
			{
				new Dictionary<long, int>(),
				new Dictionary<long, int>()
			};

			private readonly int[] running = new int[256];

			private readonly long[] last = new long[256];

			private bool CheckCpuBudget()
			{
				long creation;
				long exit;
				long kernel;
				long user;
				if (!Native.GetThreadTimes(new IntPtr(-2), out creation, out exit, out kernel, out user))
				{
					Fault = FrameCaptureIssue.CaptureFailed;
					Invalid = true;
					return false;
				}
				double ratio = (double)(kernel + user) / 10000.0;
				if (ratio <= 500.0)
				{
					return true;
				}
				Fault = FrameCaptureIssue.CpuBudget;
				Invalid = true;
				return false;
			}

			private bool AdmitRelevantEvent()
			{
				if (++RelevantCount <= 750000)
				{
					return true;
				}
				Fault = FrameCaptureIssue.EventBudget;
				Invalid = true;
				return false;
			}

			internal void Receive(ref Native.EventRecord r)
			{
				try
				{
					if (Invalid)
					{
						return;
					}
					if (++Count > 1000000)
					{
						Fault = FrameCaptureIssue.SystemEventBudget;
						Invalid = true;
					}
					else
					{
						if ((Count == 1 || Count % 4096 == 0) && !CheckCpuBudget())
						{
							return;
						}
						long timeStamp = r.EventHeader.TimeStamp;
						if (Invalid || timeStamp < Begin || timeStamp > End)
						{
							return;
						}
						int receiveValue = ((timeStamp >= Half) ? 1 : 0);
						int threadId = (int)r.EventHeader.ThreadId;
						Guid providerId = r.EventHeader.ProviderId;
						if (providerId == KernelThread)
						{
							if (PresenterOnly)
							{
								return;
							}
							int bufferContextMask = (int)(r.BufferContext & 0xFF);
							int opcode = r.EventHeader.EventDescriptor.Opcode;
							if (timeStamp < last[bufferContextMask])
							{
								running[bufferContextMask] = 0;
								return;
							}
							last[bufferContextMask] = timeStamp;
							switch (opcode)
							{
							case 36:
							{
								SchedulerSwitches++;
								if (r.UserDataLength < 24)
								{
									running[bufferContextMask] = 0;
									break;
								}
								int int32 = Marshal.ReadInt32(r.UserData);
								bool isInSampleThreads = Sample.Threads.ContainsKey(int32);
								running[bufferContextMask] = (isInSampleThreads ? int32 : 0);
								break;
							}
							case 50:
							{
								if (r.UserDataLength < 8 || ((uint)Marshal.ReadByte(r.UserData, 6) & (true ? 1u : 0u)) != 0)
								{
									break;
								}
								int receiveValue2 = running[bufferContextMask];
								// Nonzero entries were admitted by CSwitch from this capture's fixed thread set.
								if (FixedThreadSet && receiveValue2 == 0) break;
								int int322 = Marshal.ReadInt32(r.UserData);
								if (receiveValue2 != int322 && Sample.Threads.ContainsKey(int322) && (FixedThreadSet || Sample.Threads.ContainsKey(receiveValue2)) && AdmitRelevantEvent())
								{
									long key = ((long)int322 << 32) | (uint)receiveValue2;
									Dictionary<long, int> edges = Edges[receiveValue];
									int value;
									bool known = edges.TryGetValue(key, out value);
									if (edges.Count >= 2048 && !known)
									{
										Fault = FrameCaptureIssue.EdgeBudget;
										Invalid = true;
									}
									else
									{
										edges[key] = value + 1;
									}
								}
								break;
							}
							}
						}
						else
						{
							if (r.EventHeader.ProcessId != Sample.Pid || (!PresenterOnly && !Sample.Threads.ContainsKey(threadId)))
							{
								return;
							}
							bool matchesProviderId = providerId == Dxgi;
							bool matchesProviderId2 = providerId == D3d9;
							if ((!matchesProviderId && !matchesProviderId2) || r.EventHeader.EventDescriptor.Version != 0)
							{
								return;
							}
							int id = r.EventHeader.EventDescriptor.Id;
							if ((id != ((!matchesProviderId) ? 1 : 42) && id != (matchesProviderId ? 43 : 2)) || !AdmitRelevantEvent())
							{
								return;
							}
							int value2;
							if (id == ((!matchesProviderId) ? 1 : 42))
							{
								if (matchesProviderId)
								{
									Property property = default(Property);
									property.Name = (ulong)FlagsName.ToInt64();
									property.Index = uint.MaxValue;
									Property property2 = property;
									if (TdhGetProperty(ref r, 0u, IntPtr.Zero, 1u, ref property2, 4u, flagBuffer) != 0 || (BitConverter.ToUInt32(flagBuffer, 0) & (true ? 1u : 0u)) != 0)
									{
										pending.Remove(threadId);
										return;
									}
								}
								pending[threadId] = id;
							}
							else if (id == (matchesProviderId ? 43 : 2) && pending.TryGetValue(threadId, out value2) && value2 == ((!matchesProviderId) ? 1 : 42))
							{
								pending.Remove(threadId);
								if (r.UserDataLength >= 4 && Marshal.ReadInt32(r.UserData) == 0)
								{
									int value3;
									Presents[receiveValue].TryGetValue(threadId, out value3);
									Presents[receiveValue][threadId] = value3 + 1;
								}
							}
						}
					}
				}
				catch
				{
					Fault = FrameCaptureIssue.DecodeFailed;
					Invalid = true;
				}
			}
		}

		internal const int MaxRelevantEvents = 750000;

		internal const int MaxSystemEvents = 1000000;

		internal const int CpuCheckInterval = 4096;

		internal const double MaxCaptureCpuMs = 500.0;

		internal static readonly Guid Dxgi = new Guid("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");

		internal static readonly Guid D3d9 = new Guid("783aca0a-790e-4d7f-8451-aa850511c6b9");

		internal static readonly Guid KernelThread = new Guid("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");

		[DllImport("tdh.dll")]
		private static extern uint TdhGetProperty(ref Native.EventRecord record, uint contextCount, IntPtr context, uint count, ref Property property, uint bytes, byte[] data);

		internal static int Dominant(Dictionary<int, int> counts, int minimum, double share)
		{
			int dominantValue = 0;
			int result = 0;
			int dominantValue2 = 0;
			foreach (KeyValuePair<int, int> count in counts)
			{
				dominantValue += count.Value;
				if (count.Value > dominantValue2)
				{
					dominantValue2 = count.Value;
					result = count.Key;
				}
			}
			if (dominantValue2 < minimum || !((double)dominantValue2 >= (double)dominantValue * share))
			{
				return 0;
			}
			return result;
		}

		private static int DominantWorker(Dictionary<long, int> edges, int presenter)
		{
			int total = 0, best = 0, maximum = 0;
			foreach (KeyValuePair<long, int> edge in edges)
			{
				if ((int)(edge.Key >> 32) == presenter)
				{
					total += edge.Value;
					if (edge.Value > maximum)
					{
						maximum = edge.Value;
						best = (int)edge.Key;
					}
				}
			}
			return maximum >= 12 && (double)maximum >= (double)total * 0.75 ? best : 0;
		}

		internal static int SelectWorker(Evidence evidence, int presenter)
		{
			if (evidence.PresenterOnly)
			{
				return 0;
			}
			int dominantWorkerResult = DominantWorker(evidence.Edges[0], presenter);
			int dominantWorkerResult2 = DominantWorker(evidence.Edges[1], presenter);
			if (dominantWorkerResult == 0 || dominantWorkerResult != dominantWorkerResult2)
			{
				return 0;
			}
			return dominantWorkerResult;
		}

		internal static FrameCaptureIssue CaptureIssue(bool admitted, int identityState, bool stopped, bool drained, int traceCode, uint lost, FrameCaptureIssue fault, double cpuMs)
		{
			if (traceCode != 0 || !drained || (!stopped && lost == 0))
			{
				return FrameCaptureIssue.TraceStopFailed;
			}
			if (lost != 0)
			{
				return FrameCaptureIssue.EventsLost;
			}
			if (fault == FrameCaptureIssue.DecodeFailed)
			{
				return fault;
			}
			if (!admitted)
			{
				if (fault != FrameCaptureIssue.SystemEventBudget && fault != FrameCaptureIssue.EventBudget && fault != FrameCaptureIssue.EdgeBudget && fault != FrameCaptureIssue.CpuBudget)
				{
					return FrameCaptureIssue.Canceled;
				}
				return fault;
			}
			if (identityState > 0)
			{
				return FrameCaptureIssue.TargetChanged;
			}
			if (identityState < 0)
			{
				return FrameCaptureIssue.TargetUnavailable;
			}
			if (fault != 0)
			{
				return fault;
			}
			if (cpuMs > 500.0)
			{
				return FrameCaptureIssue.CpuBudget;
			}
			return FrameCaptureIssue.None;
		}

		internal static FrameCoordinationSample Capture(int pid, long birth, Func<bool> alive)
		{
			return Capture(pid, birth, alive, false);
		}

		private static bool AdmitPresenter(FrameCoordinationSample sample, int tid)
		{
			IntPtr intPtr = Native.OpenThread(2048, false, tid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				long creation;
				long exit;
				long kernel;
				long user;
				if (Native.QueryThreadOwnerPid(intPtr) != sample.Pid || !Native.GetThreadTimes(intPtr, out creation, out exit, out kernel, out user) || exit != 0 || creation <= 0)
				{
					return false;
				}
				sample.Threads[tid] = creation;
				return true;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		internal static FrameCoordinationSample Capture(int pid, long birth, Func<bool> alive, bool presenterOnly)
		{
			FrameCoordinationSample frameCoordinationSample = new FrameCoordinationSample();
			frameCoordinationSample.Pid = pid;
			frameCoordinationSample.Birth = birth;
			frameCoordinationSample.PresenterOnly = presenterOnly;
			FrameCoordinationSample frameCoordinationSample2 = frameCoordinationSample;
			frameCoordinationSample2.Reject(FrameCaptureIssue.InsufficientEvidence);
			Evidence evidence = new Evidence();
			evidence.Sample = frameCoordinationSample2;
			evidence.PresenterOnly = presenterOnly;
			Evidence evidence2 = evidence;
			FrameCoordinationTrace frameCoordinationTrace = new FrameCoordinationTrace();
			try
			{
				if (!alive())
				{
					return frameCoordinationSample2.Reject(FrameCaptureIssue.Canceled);
				}
				int processStateResult = NativeFrameThreads.ProcessState(pid, birth);
				if (processStateResult != 0)
				{
					return frameCoordinationSample2.Reject((processStateResult > 0) ? FrameCaptureIssue.TargetChanged : FrameCaptureIssue.TargetUnavailable);
				}
				if (!presenterOnly)
				{
					using (Process process = Process.GetProcessById(pid))
					{
						foreach (ProcessThread thread in process.Threads)
						{
							if (frameCoordinationSample2.Threads.Count >= 512)
							{
								return frameCoordinationSample2.Reject(FrameCaptureIssue.ThreadBudget);
							}
							IntPtr intPtr = Native.OpenThread(2048, false, thread.Id);
							if (intPtr == IntPtr.Zero)
							{
								continue;
							}
							try
							{
								long creation;
								long exit;
								long kernel;
								long user;
								if (Native.QueryThreadOwnerPid(intPtr) == pid && Native.GetThreadTimes(intPtr, out creation, out exit, out kernel, out user) && exit == 0)
								{
									frameCoordinationSample2.Threads[thread.Id] = creation;
								}
							}
							finally
							{
								Native.CloseHandle(intPtr);
							}
						}
					}
				}
				evidence2.FixedThreadSet = !presenterOnly;
				evidence2.FlagsName = Marshal.StringToHGlobalUni("Flags");
				evidence2.Begin = Stopwatch.GetTimestamp();
				evidence2.Half = evidence2.Begin + Stopwatch.Frequency * 8 / 5;
				evidence2.End = evidence2.Begin + Stopwatch.Frequency * 16 / 5;
				if (!frameCoordinationTrace.Start(pid, evidence2.Receive, presenterOnly))
				{
					frameCoordinationSample2.TraceCode = frameCoordinationTrace.Error;
					return frameCoordinationSample2.Reject(FrameCaptureIssue.TraceUnavailable);
				}
				while (Stopwatch.GetTimestamp() < evidence2.End && alive() && !evidence2.Invalid)
				{
					Thread.Sleep(100);
				}
				bool stopped = frameCoordinationTrace.Stop();
				frameCoordinationSample2.CpuMs = frameCoordinationTrace.CpuMs;
				frameCoordinationSample2.TraceCode = frameCoordinationTrace.Error;
				frameCoordinationSample2.LostEvents = frameCoordinationTrace.Lost;
				frameCoordinationSample2.Events = evidence2.RelevantCount;
				frameCoordinationSample2.SystemEvents = evidence2.Count;
				frameCoordinationSample2.SchedulerSwitches = evidence2.SchedulerSwitches;
				FrameCaptureIssue frameCaptureIssue = CaptureIssue(alive(), NativeFrameThreads.ProcessState(pid, birth), stopped, frameCoordinationTrace.Drained, frameCoordinationTrace.Error, frameCoordinationTrace.Lost, evidence2.Fault, frameCoordinationSample2.CpuMs);
				if (frameCaptureIssue != 0)
				{
					return frameCoordinationSample2.Reject(frameCaptureIssue);
				}
				int presentTidValue = Dominant(evidence2.Presents[0], 16, 0.7);
				int dominantResult = Dominant(evidence2.Presents[1], 16, 0.7);
				if (presentTidValue == 0 || presentTidValue != dominantResult)
				{
					return frameCoordinationSample2;
				}
				if (presenterOnly && !AdmitPresenter(frameCoordinationSample2, presentTidValue))
				{
					return frameCoordinationSample2;
				}
				frameCoordinationSample2.PresentTid = presentTidValue;
				frameCoordinationSample2.WorkerTid = SelectWorker(evidence2, presentTidValue);
				frameCoordinationSample2.Valid = true;
				frameCoordinationSample2.Issue = FrameCaptureIssue.None;
				frameCoordinationSample2.Detail = (presenterOnly ? "presenter confirmed; presenter-only capture" : ((frameCoordinationSample2.WorkerTid == 0) ? "presenter confirmed; no stable worker" : "presenter and worker confirmed"));
				return frameCoordinationSample2;
			}
			catch (ArgumentException ex)
			{
				if (!alive() || NativeFrameThreads.ProcessState(pid, birth) > 0)
				{
					return frameCoordinationSample2.Reject(FrameCaptureIssue.TargetChanged);
				}
				return frameCoordinationSample2.Reject(FrameCaptureIssue.CaptureFailed, ex.GetType().Name + ": " + ex.Message);
			}
			catch (Win32Exception ex2)
			{
				return frameCoordinationSample2.Reject(FrameCaptureIssue.TargetUnavailable, "code=" + ex2.NativeErrorCode);
			}
			catch (Exception ex3)
			{
				return frameCoordinationSample2.Reject(FrameCaptureIssue.CaptureFailed, ex3.GetType().Name + ": " + ex3.Message);
			}
			finally
			{
				evidence2.Invalid = true;
				frameCoordinationTrace.Stop();
				if (frameCoordinationTrace.Drained && evidence2.FlagsName != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(evidence2.FlagsName);
				}
			}
		}
	}
}
